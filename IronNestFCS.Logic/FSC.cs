using HarmonyInstance = HarmonyLib.Harmony;
using System.Collections;
using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

public enum LeftRight {
    Left,
    Right,
}

/// <summary>
/// 纯火控领域逻辑: 查找游戏对象, 读取游戏数据, 操控游戏内交互(dial 等)
/// 不含任何 UI / IMGUI / 生命周期框架代码 - 那些在 <see cref="FcsModule"/> 和 <see cref="FcsWindow"/> 里
///
/// 重载安全规则:
///  - 不要在这里注册新的 IL2CPP 类型(同一类型进程内只能注册一次)
///  - 每次实例用独立的 Harmony 实例; Shutdown 时 UnpatchSelf
///  - 所有对 IL2CPP 对象的引用在 Shutdown 时清空, 便于旧 ALC 回收
/// </summary>
public class FSC
{
    private const string HarmonyId = "com.svr2kos2.ironnestfcs.logic";

    // ===== 药包自动补充 =====
    // 两炮共用一个装药余量池: 余量低于 PowderReplenishThreshold 时, 每 PowderCheckInterval 秒
    // 自动购买一次装药卡, 把药包维持在充足水位, 避免任务流程因装药不足卡在装填/击发阶段
    // 只做检测与补充, 不干预 RunTaskRoutine 里的任何现有步骤
    private const float PowderCheckInterval = 5f;
    private const int PowderReplenishThreshold = 6;

    private HarmonyInstance? _harmony;
    
    private FcsSceneInteractor _sceneInteractor;
    private readonly PurchaseDeck _purchaseDeck = new();
    public readonly MapTable MapTable = new MapTable();
    public readonly BallisticCalculator BallisticCalculator = new BallisticCalculator();
    public readonly GunSystem LeftGun = new GunSystem();
    public readonly GunSystem RightGun = new GunSystem();
    public readonly Turret Turret = new Turret();
    public readonly TriggerConsole TriggerConsole = new();
    
    // ===== 任务调度 =====
    // 用户不再指定炮管: 任务入队后由调度器派给空闲炮管, 炮管打完一发自动拉下一个
    // 所有读写都在 Unity 主线程(入队来自点击回调, 派发/完成来自协程), 无并发, 无需锁
    private readonly Queue<ArtilleryTask> _taskQueue = new();

    /// <summary>当前各炮管正在执行的任务; null 表示该炮管空闲. 供 UI 显示与调度判断.</summary>
    public ArtilleryTask? LeftTask { get; private set; }
    public ArtilleryTask? RightTask { get; private set; }

    /// <summary>等待派发的任务数(已入队但还没分到炮管). 供 UI 显示.</summary>
    public int PendingCount => _taskQueue.Count;
    public Queue<ArtilleryTask> QueueCan => new Queue<ArtilleryTask>(_taskQueue);

    /// <summary>
    /// 控制台互斥锁: 保护弹道计算器, 确认开关台, 采购台这三组全局唯一的"短操作"硬件
    /// 临界区都很短(解算 / 确认弹 / 击发前的确认+击发), 用完即放
    /// </summary>
    private readonly CoroutineLock _deskLock = new();

    /// <summary>
    /// 炮塔方向角锁: 方向角是全炮塔共享的, 且一旦为某任务转到位, 必须独占到这一发打出去为止
    /// (中途被另一任务转走就会打偏). 与 <see cref="_deskLock"/> 分开, 是为了让本任务能在
    /// 后台早早抢占炮塔, 与装填/升仰角重叠, 而不挡住另一管炮在 deskLock 上的解算
    ///
    /// 防死锁: 凡同时需要两把锁处, 一律"先 turret 后 desk". 本类只有击发段会嵌套两把锁
    /// (此时炮塔已由后台预约持有, 再去抢 desk), 解算/确认弹只单独用 desk, 故无环, 不死锁
    /// </summary>
    private readonly CoroutineLock _turretLock = new();

    // 正在运行的协程句柄. Dispose 时全部停掉, 避免热重载后旧 ALC 的协程继续执行导致崩溃
    private readonly List<(object handle, LeftRight gun)> _runningCoroutines = new();
    // 进度超时监控
    private float _leftProgressTime;
    private float _rightProgressTime;
    private Progress _lastLeftProgress;
    private Progress _lastRightProgress;
    private const float ProgressTimeout = 20f;
    public FSC() {
        this._sceneInteractor = new FcsSceneInteractor(this);
    }

    public bool IsBound { get; private set; } = false;

    /// <summary>查找并绑定游戏对象. 返回 false 表示当前场景还没有目标控件.</summary>
    public bool TryBind()
    {
        // 每次重载创建全新的 Harmony 实例, 避免与上一版补丁冲突
        _sceneInteractor = new FcsSceneInteractor(this);
        _sceneInteractor.Initialize();
        _harmony = new HarmonyInstance(HarmonyId);
        _deskLock.Reset();
        _turretLock.Reset();
        IsBound = MapTable.TryBind()
                  && BallisticCalculator.TryBind()
                  && LeftGun.TryBind("Left")
                  && RightGun.TryBind("Right")
                  && _purchaseDeck.TryBind()
                  && Turret.TryBind()
                  && TriggerConsole.TryBind();
        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        _runningCoroutines.Add((MelonCoroutines.Start(ProgressTimeoutMonitor()), LeftRight.Left)); // 监控用左槽位无关
        if (IsBound) {
            // 常驻药包自动补充协程: 仅保证装药余量充足, 不改动任务流程
            _runningCoroutines.Add((MelonCoroutines.Start(ReplenishPowderLoop()), LeftRight.Left));
        }
        // _runningCoroutines.Add(MelonCoroutines.Start(ExposeAllEntities()));

        return IsBound;
    }

    public void Update() {
        _sceneInteractor.Update();
    }

    /// <summary>键盘快捷键触发射击目标(小键盘 1-4).</summary>
    public void FireTarget(int targetId) {
        _sceneInteractor.FireTarget(targetId);
    }

    /// <summary>扫荡: 将目标位置加入打击队列.</summary>
    public void FireAtWorldPos(int id, Vector3 worldPos) {
        _sceneInteractor.FireAtWorldPos(id, worldPos);
    }

    /// <summary>扫荡插队: 目标加入队列最前面.</summary>
    public void FireAtWorldPosFront(int id, Vector3 worldPos) {
        _sceneInteractor.FireAtWorldPosFront(id, worldPos);
    }
    
    /// <summary>强制重置指定炮管: 停协程, 放锁, 已中止任务放回队首重试(最多重试一次, 防死循环).</summary>
    public void AbortGun(LeftRight gun) {
        MelonLogger.Msg($"[FCS] AbortGun {gun}");
        // 保存被中止的任务, 稍后重新入队
        var abortedTask = gun == LeftRight.Left ? LeftTask : RightTask;
        // 停掉该炮管的所有协程
        for (int i = _runningCoroutines.Count - 1; i >= 0; i--) {
            if (_runningCoroutines[i].gun != gun) continue;
            try { MelonCoroutines.Stop(_runningCoroutines[i].handle); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] Abort stop failed: {ex}"); }
            _runningCoroutines.RemoveAt(i);
        }
        // 强制释放所有锁
        _deskLock.Reset();
        _turretLock.Reset();
        // 清空槽位
        if (gun == LeftRight.Left) LeftTask = null;
        else RightTask = null;
        // 被中止的任务: 已重试过则标记失败, 不再放回队首(避免反复采购/反复解算的无限循环)
        if (abortedTask != null) {
            if (abortedTask.abortCount >= 1) {
                abortedTask.progress = Progress.Failed;
                MelonLogger.Error($"[FCS] {gun} 任务 T{abortedTask.targetId} 已中止过，标记 Failed，不再重试。");
            }
            else {
                abortedTask.abortCount++;
                EnqueueTaskFront(abortedTask);
            }
        }
        else {
            TryDispatch();
        }
    }
    
    /// <summary>记录进度更新时间戳</summary>
    private void MarkProgress(LeftRight gun, Progress p) {
        var now = Time.time;
        if (gun == LeftRight.Left) { _leftProgressTime = now; _lastLeftProgress = p; }
        else { _rightProgressTime = now; _lastRightProgress = p; }
    }

    /// <summary>
    /// 该进度状态是否应该受固定超时约束
    ///  - 炮管物理运动阶段(Aiming / BackToIdle): 由 SetElevation 内部的无进展检测兜底, 不做固定超时(慢速瞄准可等任意久)
    ///  - WaitingForFire 且未开启 AutoFire: 等待玩家手动击发, 预期可长时间挂起, 不做固定超时
    ///  - 其余阶段(解算/装填/击发确认等)理论上数秒内完成, 保留固定超时兜底
    /// </summary>
    private bool ShouldTimeout(Progress p, bool autoFire) {
        if (p == Progress.Aiming || p == Progress.AimingAzimuth || p == Progress.BackToIdle) return false;
        if (p == Progress.WaitingForFire && !autoFire) return false;
        return true;
    }

    /// <summary>
    /// 常驻后台协程: 周期性检测装药余量(两炮共用池), 低于阈值时自动购买一次装药卡
    /// 购买必须持 _deskLock - 采购台是共享硬件, 与任务流程的采购互斥(阻塞等待, 不破坏临界区)
    /// 必须在 TryBind 成功后启动并登记进 _runningCoroutines; Dispose 时随其它协程一起 Stop
    /// 迭代器被 Stop 时 Dispose 会执行 finally, 锁不会泄漏
    /// </summary>
    private IEnumerator ReplenishPowderLoop() {
        while (true) {
            yield return new WaitForSeconds(PowderCheckInterval);
            // 两炮共用一个装药余量池, 读数应一致; 取较小值保守触发
            var charges = Math.Min(LeftGun.RemainingCharges(), RightGun.RemainingCharges());
            if (charges >= PowderReplenishThreshold) continue;
            MelonLogger.Msg(
                $"[FCS] AutoReplenish: powder charges {charges} < {PowderReplenishThreshold}, buying one");
            yield return _deskLock.Acquire();
            try {
                yield return _purchaseDeck.BuyPowders();
            }
            finally {
                _deskLock.Release();
            }
        }
    }

    /// <summary>监控协程: 非豁免阶段卡在同一状态超 20 秒则自动重置(豁免阶段由各自的进展检测兜底).</summary>
    private IEnumerator ProgressTimeoutMonitor() {
        while (true) {
            yield return new WaitForSeconds(2f);
            var now = Time.time;
            if (LeftTask != null && _leftProgressTime > 0 && now - _leftProgressTime > ProgressTimeout) {
                if (ShouldTimeout(_lastLeftProgress, _sceneInteractor.AutoFire)) {
                    MelonLogger.Msg($"[FCS] Timeout {_lastLeftProgress}, auto-abort Left");
                    AbortGun(LeftRight.Left);
                    _leftProgressTime = 0;
                }
                else {
                    // 豁免阶段: 刷新时间戳, 避免状态切换瞬间被误判超时
                    _leftProgressTime = now;
                }
            }
            if (RightTask != null && _rightProgressTime > 0 && now - _rightProgressTime > ProgressTimeout) {
                if (ShouldTimeout(_lastRightProgress, _sceneInteractor.AutoFire)) {
                    MelonLogger.Msg($"[FCS] Timeout {_lastRightProgress}, auto-abort Right");
                    AbortGun(LeftRight.Right);
                    _rightProgressTime = 0;
                }
                else {
                    _rightProgressTime = now;
                }
            }
        }
    }

    /// <summary>释放: 撤销补丁, 清空 IL2CPP 引用.</summary>
    public void Dispose()
    {
        // 停掉所有未完成的协程, 否则热重载后旧 ALC 的协程仍会被 Unity 驱动 → 崩溃
        foreach (var (handle, _) in _runningCoroutines) {
            try { MelonCoroutines.Stop(handle); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] Stop coroutines failed: {ex}"); }
        }
        _runningCoroutines.Clear();

        // 清空调度状态, 避免热重载后残留任务/槽位影响新一轮绑定
        _taskQueue.Clear();
        LeftTask = null;
        RightTask = null;

        _sceneInteractor.ShutDown();
        try { _harmony?.UnpatchSelf(); }
        catch (Exception ex) { MelonLogger.Error($"[FCS] UnpatchSelf failed: {ex}"); }
        _harmony = null;
    }

    public IEnumerator ExposeAllEntities() {
        while (true) {
            foreach (var m in MapTable.GetAllFireMissionEntities()) {
                var vr = m.transform.FindChild("VisualRoot");
                if (vr == null) continue;
                vr.gameObject.SetActive(true);
                var info = vr.FindChild("Info");
                if (info != null) info.gameObject.SetActive(true);
            }
            yield return new WaitForSeconds(1f);
        }
    }

    /// <summary>
    /// 把任务加入调度队列. 用户不指定炮管 - 调度器自动派给空闲炮管
    /// 入队后立即尝试派发; 若两管炮都忙, 任务留在队列里, 等某管炮打完自动拉取
    /// 必须在主线程调用(点击回调即是)
    /// </summary>
    public void EnqueueTask(ArtilleryTask task) {
        task.progress = Progress.Pending;
        _taskQueue.Enqueue(task);
        TryDispatch();
    }

    /// <summary>插队到队列最前面(炮兵优先).</summary>
    public void EnqueueTaskFront(ArtilleryTask task) {
        task.progress = Progress.Pending;
        var existing = _taskQueue.ToArray();
        _taskQueue.Clear();
        _taskQueue.Enqueue(task);
        foreach (var t in existing) _taskQueue.Enqueue(t);
        TryDispatch();
    }

    /// <summary>把队首任务派给空闲炮管, 直到没有空闲炮管或队列空.</summary>
    private void TryDispatch() {
        while (_taskQueue.Count > 0) {
            LeftRight slot;
            if (LeftTask == null) slot = LeftRight.Left;
            else if (RightTask == null) slot = LeftRight.Right;
            else break; // 两管炮都忙

            var task = _taskQueue.Dequeue();
            if (slot == LeftRight.Left) LeftTask = task;
            else RightTask = task;
            StartTaskRoutine(slot, task);
        }
    }

    /// <summary>
    /// 启动一个火控任务协程. 用 MelonCoroutines 跑协程实现延时 -
    /// 协程由 Unity 在主线程分帧驱动, yield 期间不阻塞, 恢复后仍在主线程
    /// 因此可安全访问 IL2CPP 对象. 绝不能用 async/Task.Delay: 其 continuation
    /// 会在线程池线程恢复, 跨线程访问 IL2CPP 运行时会导致进程崩溃且无日志
    /// </summary>
    private void StartTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var handle = MelonCoroutines.Start(RunTaskRoutine(leftRight, task));
        _runningCoroutines.Add((handle, leftRight));
    }

    /// <summary>炮管打完一发后释放槽位并尝试拉取队列里的下一个任务.</summary>
    private void ReleaseSlot(LeftRight leftRight) {
        if (leftRight == LeftRight.Left) LeftTask = null;
        else RightTask = null;
        TryDispatch();
    }

    /// <summary>
    /// 单次打击任务主流程, 弹种保护状态机:
    /// 现场三要素 = 膛内弹种 (ChamberedShellBlueprint) / 实装药包数 (PowderCharges) / 装填相位 (CurrentStateIndex).
    /// 最多两轮: 第一轮可能是退弹 (膛内弹错, 或弹对但实装药包不足且 P4 锁死补不了), 第二轮实射.
    /// 实装药包 >= 需求量时跳过装填, 按实装量重算诸元直接击发 (省一次装填).
    /// </summary>
    private IEnumerator RunTaskRoutine(LeftRight leftRight, ArtilleryTask task) {
        var gunSys = leftRight == LeftRight.Left ? LeftGun : RightGun;
        MarkProgress(leftRight, task.progress);

        // ===== 炮塔预约: 任务一开始就在后台抢方向角并转向 =====
        // 方向旋转和装填/升仰角互不冲突. 后台协程阻塞式抢炮塔锁("一旦释放就立即获取")
        // 一拿到就开始转向, 与本任务接下来的整个装填+升仰角段重叠. 等到击发前只需确认它转好
        // 而不必等仰角转完再从头抢炮塔, 再转向. 方向角必须独占到这一发打出去为止
        // 故锁一直持有到实射完成(WaitFire 后由 ReleaseOnce 归还)
        var turret = new TurretReservation();
        // 独立的 fire-and-forget 协程, 必须登记以便 Dispose 时一并 Stop
        // 否则热重载后旧 ALC 的它仍被 Unity 驱动 → 崩溃
        _runningCoroutines.Add((MelonCoroutines.Start(ReserveTurretAndRotate(task, turret)), leftRight));

        for (int round = 0; round < 2; round++) {
            // ===== 读现场, 决定本轮是退弹还是实射 =====
            string? chambered = gunSys.BulletInChamber();
            int loaded = gunSys.LoadedPowderCharges();
            int need = _sceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);
            bool shellWrong = chambered != null && chambered != task.bulletType.ToString();
            bool powderLacking = chambered != null && !shellWrong && loaded > 0 && loaded < need;
            bool isDump = shellWrong || powderLacking;

            // 本轮装药量: 退弹用已装(没装补 1); 实射时实装足量按实装, 否则按需求
            int powderCount;
            if (isDump) powderCount = loaded > 0 ? loaded : 1;
            else if (loaded >= need) powderCount = loaded;
            else powderCount = need;

            if (isDump) {
                task.progress = Progress.DumpingWrongShell;
                MarkProgress(leftRight, Progress.DumpingWrongShell);
                MelonLogger.Msg($"[FCS] {leftRight}: dump round {round}, chambered={chambered}, loaded={loaded}, need={need}");
            }
            else {
                MelonLogger.Msg($"[FCS] {leftRight}: fire round {round}, chambered={chambered}, loaded={loaded}, powderCount={powderCount}");
            }

            // ===== 临界区 1:解算(无论如何都要算仰角)=====
            // 弹道计算器 / 确认台 / 采购台都是全局唯一硬件, 必须串行. 算完仰角即放
            // 让另一管炮能立刻进来算它自己的弹道, 与本管炮接下来的长装填段重叠
            float elevation = 0f;
            bool viable = true;
            yield return _deskLock.Acquire();
            try {
                task.progress = Progress.Calculating;
                MarkProgress(leftRight, Progress.Calculating);
                yield return BallisticCalculator.SetDistance(task.distance);
                yield return BallisticCalculator.SetDirection(task.angel);
                yield return BallisticCalculator.SetCharge(powderCount);
                yield return BallisticCalculator.SetShellType(task.bulletType);
                yield return BallisticCalculator.Calculate();
                elevation = BallisticCalculator.GetElevation();

                // 装药不足则补购. 单次采购未必补满(且偶发点击早于卡牌入槽而失败)
                // 故循环购买直到够本次发射所需, 避免"装药不足但非 0"时直接推进, 卡住后续装填
                // 加购买次数上限兜底: 采购始终无效时不至于无限循环(每次约 2.5s)
                var powderPurchaseAttempts = 0;
                while (gunSys.RemainingCharges() < powderCount) {
                    yield return _purchaseDeck.BuyPowders();
                    if (++powderPurchaseAttempts >= 10) {
                        MelonLogger.Error(
                            $"[FCS] {leftRight} 炮管: 购买装药 {powderPurchaseAttempts} 次后仍不足 " +
                            $"{powderCount} (当前 {gunSys.RemainingCharges()}), 停止补购.");
                        break;
                    }
                }

                task.progress = Progress.SelectingBullet;
                MarkProgress(leftRight, Progress.SelectingBullet);
                // 弹仓里没有目标弹种则采购(采购台也是共享硬件, 放在锁内)
                if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                    if (!gunSys.HaveEmptyShellInCylinder()) {
                        task.progress = Progress.Failed;
                        MarkProgress(leftRight, Progress.Failed);
                        viable = false;
                    }
                    else {
                        yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                        // 确认采购到位: 等弹仓出现目标弹种, 最多 3 秒
                        float waited = 0f;
                        while (!gunSys.HaveBulletInCylinder(task.bulletType) && waited < 3f) {
                            yield return new WaitForSeconds(0.5f);
                            waited += 0.5f;
                        }
                    }
                }
            }
            finally {
                _deskLock.Release();
            }

            // 任务不可行: 取消炮塔预约并归还(后台若尚未抢到, 会在抢到后自行归还)
            // 避免炮塔锁被一个不会击发的任务永久占用
            if (!viable) {
                turret.Canceled = true;
                ReleaseTurretOnce(turret);
                ReleaseSlot(leftRight);
                yield break;
            }

            // ===== 锁外: 装填(按现场分支)=====
            if (isDump) {
                // 退弹: 只保证膛内有药包, 弹不换; 已装药则直接打
                if (loaded == 0) {
                    task.progress = Progress.LoadingPowder;
                    MarkProgress(leftRight, Progress.LoadingPowder);
                    yield return gunSys.LoadPowder(1);
                    task.progress = Progress.WaitLoading;
                    MarkProgress(leftRight, Progress.WaitLoading);
                    while (!gunSys.CanFire()) {
                        yield return new WaitForSeconds(1f);
                    }
                }
            }
            else if (chambered == null) {
                // 空膛: 装弹 + 装药. 先等残留实装计数清零(上一发击发后的自动循环)
                float waitLoaded = 0f;
                while (gunSys.LoadedPowderCharges() > 0 && waitLoaded < 10f) {
                    yield return new WaitForSeconds(0.5f);
                    waitLoaded += 0.5f;
                }
                task.progress = Progress.LoadingBullet;
                MarkProgress(leftRight, Progress.LoadingBullet);
                yield return gunSys.LoadBullet(task.bulletType);
                task.progress = Progress.LoadingPowder;
                MarkProgress(leftRight, Progress.LoadingPowder);
                yield return gunSys.LoadPowder(powderCount);
                task.progress = Progress.WaitLoading;
                MarkProgress(leftRight, Progress.WaitLoading);
                while (!gunSys.CanFire()) {
                    yield return new WaitForSeconds(1f);
                }
            }
            else if (loaded == 0) {
                // 弹已在膛: 只装药
                task.progress = Progress.LoadingPowder;
                MarkProgress(leftRight, Progress.LoadingPowder);
                yield return gunSys.LoadPowder(powderCount);
                task.progress = Progress.WaitLoading;
                MarkProgress(leftRight, Progress.WaitLoading);
                while (!gunSys.CanFire()) {
                    yield return new WaitForSeconds(1f);
                }
            }
            // 否则 loaded >= need: 实装足量, 全部跳过, 直接瞄准击发

            // ===== 锁外: 升仰角(退弹平射跳过)=====
            if (!isDump) {
                task.progress = Progress.Aiming;
                MarkProgress(leftRight, Progress.Aiming);
                yield return gunSys.SetElevation(elevation);
            }

            // ===== 锁外: 等炮塔水平到位(2-2 HAIM)=====
            task.progress = Progress.AimingAzimuth;
            MarkProgress(leftRight, Progress.AimingAzimuth);
            while (!turret.Ready) {
                yield return null;
            }

            // ===== 临界区 2: 击发 =====
            task.progress = Progress.WaitingForFire;
            MarkProgress(leftRight, Progress.WaitingForFire);
            try {
                yield return TriggerConsole.ConfirmTask();
                yield return TriggerConsole.ConfirmBullet();
                yield return TriggerConsole.ConfirmRotation();
                yield return TriggerConsole.ConfirmElevation();
                yield return TriggerConsole.ReadyToFire();
                yield return TriggerConsole.Arm(leftRight);
                task.impactTime = gunSys.PredictedImpactTime(); // 火控解总飞行时间: 击发前拷贝炮兵计时器
                if (_sceneInteractor.AutoFire) {
                    TriggerConsole.Fire();
                }
                yield return gunSys.WaitFire();
            }
            finally {
                // 炮塔方向角独占到实射这一发打出去为止; 退弹轮继续持有给下一轮
                if (!isDump) ReleaseTurretOnce(turret);
            }

            if (!isDump) {
                // ===== 锁外: 回位 =====
                task.progress = Progress.BackToIdle;
                MarkProgress(leftRight, Progress.BackToIdle);
                yield return gunSys.WaitBackToIdle();
                task.progress = Progress.Finished;
                MarkProgress(leftRight, Progress.Finished);
                _sceneInteractor.TaskFinished(task);
                ReleaseSlot(leftRight);
                yield break;
            }

            // 退弹轮结束: 等机构自动循环, 下一轮重装正确弹种
            yield return new WaitForSeconds(2f);
        }

        // 两轮仍未完成(异常兜底): 归还炮塔并失败
        ReleaseTurretOnce(turret);
        task.progress = Progress.Failed;
        MarkProgress(leftRight, Progress.Failed);
        ReleaseSlot(leftRight);
    }

    /// <summary>
    /// 炮塔预约状态. 三个标志全在主线程协作式调度下读写, 无真正并发
    /// 生命周期: 后台 <see cref="ReserveTurretAndRotate"/> 抢锁→转向→置 Ready;
    /// 主流程击发后 / 任务放弃时 ReleaseTurretOnce 归还. Released 保证恰好归还一次
    /// </summary>
    private sealed class TurretReservation {
        public bool Acquired;  // 已拿到炮塔锁
        public bool Ready;     // 已转到目标方向角
        public bool Canceled;  // 主流程已放弃本次预约
        public bool Released;  // 锁已归还(防重复 Release)
    }

    /// <summary>
    /// 后台预约炮塔并转向. 阻塞式抢锁实现"一旦炮塔释放就立即获取"
    /// 抢到后若发现已被取消则立即归还, 不空转; 否则转到目标方向并置 Ready
    /// 此后炮塔由主流程在击发完成时归还(若转向期间被取消则在此自行归还)
    /// </summary>
    private IEnumerator ReserveTurretAndRotate(ArtilleryTask task, TurretReservation res) {
        yield return _turretLock.Acquire();
        res.Acquired = true;
        if (res.Canceled) {
            ReleaseTurretOnce(res);
            yield break;
        }
        yield return Turret.SetRotation(task.angel);
        res.Ready = true;
        // 转向期间主流程可能已放弃(如解算失败) - 此时主流程不会再击发, 由这里归还
        if (res.Canceled) {
            ReleaseTurretOnce(res);
        }
    }

    /// <summary>归还炮塔锁, 保证恰好一次. 仅在确实持有(Acquired)且未归还时执行.</summary>
    private void ReleaseTurretOnce(TurretReservation res) {
        if (res.Acquired && !res.Released) {
            res.Released = true;
            _turretLock.Release();
        }
    }
}
