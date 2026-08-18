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

    // 完成队列: 击发时刻入列, 最多显示 8 条, 溢出计数
    private readonly List<FinishedTask> _finished = new();
    private int _finishedOverflow;

    public int FinishedCount => _finished.Count;
    public bool FinishedOverflow => _finishedOverflow > 0;
    public int FinishedOverflowCount => _finishedOverflow;
    public bool PendingOverflow => _taskQueue.Count > 8;
    public IReadOnlyList<FinishedTask> FinishedQueue => _finished;

    /// <summary>火控总暂停: 冻结调度/解算/装填/刷新, 再按一次恢复.</summary>
    private bool _paused = false;
    public bool Paused => _paused;

    /// <summary>任务时钟 (玩家手腕 MissionWatch 的 GenericTimerSceneSync): 自 10:00:00 起向上累计的秒数, 无表/未走时 NaN. 缓存引用, 失效自动重找.</summary>
    private GenericTimerSceneSync? _missionClock;
    public float MissionSeconds
    {
        get
        {
            if (_missionClock == null)
            {
                foreach (var s in Resources.FindObjectsOfTypeAll<GenericTimerSceneSync>())
                {
                    if (s == null || s.TimerID != "MissionTime" || s.CurrentTime <= 0f) continue;
                    _missionClock = s;
                    break;
                }
            }
            if (_missionClock == null) return float.NaN;
            try { return _missionClock.CurrentTime; }
            catch { _missionClock = null; return float.NaN; }
        }
    }

    // 常驻共享循环的协程句柄: 按炮清协程 (AbortGun/StopGun) 时跳过, 它们挂在 Left 槽位上
    private object? _syncLoopHandle;
    private object? _timeoutLoopHandle;
    private object? _replenishLoopHandle;

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

    /// <summary>2.0 迁移开关: false = 不绑旧 MapTable 标记系统、不启动旧常驻循环与旧交互层 (新架构接管), 只保留硬件绑定.</summary>
    public bool LegacyDisabled { get; set; } = false;

    /// <summary>查找并绑定游戏对象. 返回 false 表示当前场景还没有目标控件.</summary>
    public bool TryBind()
    {
        // 每次重载创建全新的 Harmony 实例, 避免与上一版补丁冲突
        _sceneInteractor = new FcsSceneInteractor(this);
        _harmony = new HarmonyInstance(HarmonyId);
        _deskLock.Reset();
        _turretLock.Reset();
        IsBound = (LegacyDisabled || MapTable.TryBind())
                  && BallisticCalculator.TryBind()
                  && LeftGun.TryBind("Left")
                  && RightGun.TryBind("Right")
                  && _purchaseDeck.TryBind()
                  && Turret.TryBind()
                  && TriggerConsole.TryBind();
        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        if (LegacyDisabled) return IsBound; // 新架构接管: 旧循环/旧标记/旧按钮一律不启动
        _sceneInteractor.Initialize();
        _timeoutLoopHandle = MelonCoroutines.Start(ProgressTimeoutMonitor());
        _runningCoroutines.Add((_timeoutLoopHandle, LeftRight.Left)); // 监控用左槽位无关
        if (IsBound) {
            ShellData.Init(); // 扫游戏 ShellDefinition: 杀伤半径/速度曲线 (杀伤圈与射表数据源)
            // 常驻药包自动补充协程: 仅保证装药余量充足, 不改动任务流程
            _replenishLoopHandle = MelonCoroutines.Start(ReplenishPowderLoop());
            _runningCoroutines.Add((_replenishLoopHandle, LeftRight.Left));
            // 铁巢棋子自动吸附游戏网格位置 (四角校准映射), 摆错棋子不再导致火控打飞
            _syncLoopHandle = MelonCoroutines.Start(SyncIronNestLoop());
            _runningCoroutines.Add((_syncLoopHandle, LeftRight.Left));
            MapTable.SpawnEntityDiamonds();                                   // 地图实体菱形框: 敌对红/友军蓝
            MapTable.SpawnAimMarks();                                         // 左右炮瞄准指示: 十字/X
            MapTable.SpawnTargetLines();                                      // 铁巢→当前目标虚线
            _sceneInteractor.RegisterEntityClickTargets(MapTable.EntityClickTargets); // 右键菱形框入队/取消
            _sceneInteractor.RegisterMarkerClickTargets(); // T1-T4 标记物右键: 虚拟目标入队/齐射/取消
        }

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

    /// <summary>暂停/继续整个火控流程: 冻结调度/解算/装填/刷新, 再按一次恢复.</summary>
    public void TogglePause() {
        _paused = !_paused;
        if (!_paused) {
            // 恢复: 暂停期间的时间不计入 20s 超时, 重置计时基准
            _leftProgressTime = Time.time;
            _rightProgressTime = Time.time;
            TryDispatch();
        }
        MelonLogger.Msg($"[FCS] Paused = {_paused}");
    }

    /// <summary>Stop: 中止两门炮当前任务并清空任务队列, 回到空闲 (被中止任务不再放回队列).</summary>
    public void StopAll() {
        MelonLogger.Msg("[FCS] StopAll");
        StopGun(LeftRight.Left);
        StopGun(LeftRight.Right);
        _taskQueue.Clear();
    }

    /// <summary>
    /// 停止单炮的所有任务协程并清槽位; 常驻循环 (同步/超时/补药) 挂在 Left 槽位上, 跳过不杀.
    /// resetLocks=false 用于单炮取消: 另一炮可能正持锁, 只能重置自己的 (协程被 Stop 时 finally 会正常还锁).
    /// </summary>
    private void StopGun(LeftRight gun, bool resetLocks = true) {
        for (int i = _runningCoroutines.Count - 1; i >= 0; i--) {
            var (handle, g) = _runningCoroutines[i];
            if (g != gun) continue;
            if (handle == _syncLoopHandle || handle == _timeoutLoopHandle || handle == _replenishLoopHandle) continue;
            try { MelonCoroutines.Stop(handle); }
            catch (Exception ex) { MelonLogger.Error($"[FCS] StopGun stop failed: {ex}"); }
            _runningCoroutines.RemoveAt(i);
        }
        if (resetLocks) {
            _deskLock.Reset();
            _turretLock.Reset();
        }
        if (gun == LeftRight.Left) LeftTask = null;
        else RightTask = null;
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
    /// <summary>右键菱形: 未入队 → 入队 (双圈); 在队列再点 → 升级齐射 (三圈); 再点 → 出队; 已上炮 → 不许改计划只允许取消.</summary>
    public void ToggleEntityTask(Transform entity, BulletType bullet)
    {
        if (entity == null) return;
        // 死实体拒绝入队: 任务挂上后下一秒就会被死清理连底座一起销毁
        var loc = entity.GetComponent<EntityLocation>();
        if (loc != null && !TacticalRadar.IsUnitAlive(loc, entity.gameObject)) return;
        var existing = MapTable.TaskOfEntity(entity);
        if (existing == null) {
            var task = MapTable.TaskFromEntity(entity);
            if (task == null) return;
            task.bulletType = bullet;
            EnqueueTask(task);
            return;
        }
        bool inQueue = _taskQueue.Contains(existing);
        if (!MapTable.MarkIsSalvo(entity)) {
            if (!inQueue) {
                // 已上炮: 不许改计划, 只允许取消
                CancelEntityTask(entity, existing);
                return;
            }
            // 第二次右键: 创建跟随任务插到主任务正后方 (1 2 3 中 1 升级 → 1 1' 2 3)
            var follower = CloneTask(existing);
            follower.salvoFollower = true;
            follower.salvoLeader = existing;
            AssignFireControlId(follower);
            var arr = _taskQueue.ToList();
            _taskQueue.Clear();
            foreach (var t in arr) {
                _taskQueue.Enqueue(t);
                if (t == existing) _taskQueue.Enqueue(follower);
            }
            MapTable.SetMarkSalvo(entity, true);
            return;
        }
        // 已齐射: 再点 = 取消 (队列里两条一起出队; 炮上两门一起中止)
        CancelEntityTask(entity, existing);
    }

    /// <summary>取消实体任务: 主任务与跟随任务一起清理 — 队列出队 + 在炮的中止该炮流程 (不重试), 清标记.</summary>
    private void CancelEntityTask(Transform entity, ArtilleryTask existing) {
        var arr = _taskQueue.ToList();
        _taskQueue.Clear();
        foreach (var t in arr) {
            if (t == existing) continue;
            if (t.salvoFollower && t.salvoLeader == existing) continue;
            _taskQueue.Enqueue(t);
        }
        MapTable.ClearEntityMark(entity);
        if (existing == LeftTask) StopGun(LeftRight.Left, resetLocks: false);
        else if (existing == RightTask) StopGun(LeftRight.Right, resetLocks: false);
        // 齐射双炮在射: 另一门炮上的跟随任务也中止, 两炮一起空闲
        var follower = existing == LeftTask ? RightTask : existing == RightTask ? LeftTask : null;
        if (follower != null && follower.salvoFollower && follower.salvoLeader == existing) {
            StopGun(follower == LeftTask ? LeftRight.Left : LeftRight.Right, resetLocks: false);
        }
    }

    /// <summary>刷新点选目标的队列位置标签: 在炮上 = L/R, 在队列 = 1..n, 已完成 = 回单菱形.</summary>
    private void UpdateTaskedMarks()
    {
        foreach (var task in MapTable.TaskedTasks) {
            string? slot = null;
            if (task == LeftTask) slot = "[L]";
            else if (task == RightTask) slot = "[R]";
            else {
                var queue = QueueCan.ToList();
                int idx = queue.IndexOf(task);
                if (idx >= 0) slot = (idx + 1).ToString("00"); // 两位数: 01 02 03 ...
            }
            MapTable.UpdateTaskMark(task, slot);
        }
    }

    /// <summary>齐射跟随任务参数镜像主任务 (主任务实时重算, 跟随任务无 mark 不进 TaskedTasks, 单独同步).</summary>
    private void SyncSalvoFollowers() {
        foreach (var t in new[] { LeftTask, RightTask }) {
            if (t == null || !t.salvoFollower || t.salvoLeader == null) continue;
            t.angel = t.salvoLeader.angel;
            t.distance = t.salvoLeader.distance;
            t.position = t.salvoLeader.position;
            t.trackAngel = t.salvoLeader.trackAngel;
            t.trackDistance = t.salvoLeader.trackDistance;
        }
    }

    /// <summary>地图刷新循环 25fps (0.04s): 铁巢棋子吸附 / 标签 / 瞄准指示 / 目标虚线.</summary>
    private IEnumerator SyncIronNestLoop() {
        int tick = 0;
        while (true) {
            // 总暂停不冻结沙盘刷新: 暂停是为了计划火控任务, 地图/瞄准/入队仍需实时
            yield return new WaitForSeconds(0.04f);
            MapTable.SyncIronNestToken();
            UpdateTaskedMarks();          // 刷新点选目标的队列位置标签
            SyncSalvoFollowers();         // 齐射跟随任务镜像主任务参数 (虚线/落点跟着目标走)
            MapTable.UpdateAimMarks(LeftGun.CanFire(), RightGun.CanFire(), LeftTask?.bulletType, RightTask?.bulletType); // 瞄准十字/X + 杀伤圈实时跟落点
            MapTable.UpdateTargetLines(LeftTask, RightTask);                  // 铁巢→当前目标虚线
            if (++tick % 25 == 0) {
                MapTable.RefreshEntityMarks(); // 每秒: 阵亡销毁挂件 + 新实体补挂件
                _sceneInteractor.RegisterEntityClickTargets(MapTable.EntityClickTargets); // 新挂件注册右键点击
            }
        }
    }

    private IEnumerator ReplenishPowderLoop() {
        while (true) {
            while (_paused) yield return null; // 总暂停: 不补药
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
            while (_paused) yield return null; // 总暂停: 不计超时 (恢复时 TogglePause 重置基准)
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

    /// <summary>
    /// 把任务加入调度队列. 用户不指定炮管 - 调度器自动派给空闲炮管
    /// 入队后立即尝试派发; 若两管炮都忙, 任务留在队列里, 等某管炮打完自动拉取
    /// 必须在主线程调用(点击回调即是)
    /// </summary>
    public void EnqueueTask(ArtilleryTask task) {
        AssignFireControlId(task);
        task.progress = Progress.Pending;
        _taskQueue.Enqueue(task);
        TryDispatch();
    }

    /// <summary>插队到队列最前面(炮兵优先).</summary>
    public void EnqueueTaskFront(ArtilleryTask task) {
        AssignFireControlId(task);
        task.progress = Progress.Pending;
        var existing = _taskQueue.ToArray();
        _taskQueue.Clear();
        _taskQueue.Enqueue(task);
        foreach (var t in existing) _taskQueue.Enqueue(t);
        TryDispatch();
    }

    private int _fcCounter = 0;

    /// <summary>火控 UID: 首次入队时递增分配, 已有编号 (重试/插队) 保持原号, 日志追溯用.</summary>
    private void AssignFireControlId(ArtilleryTask task) {
        if (task.fireControlId > 0) return;
        task.fireControlId = ++_fcCounter;
    }

    /// <summary>
    /// 把队首任务派给空闲炮管, 直到没有空闲炮管或队列空. 暂停时只入队不派发 (计划模式可随时取消).
    /// 齐射对 (主任务 + 跟随任务): 要求两炮同时空闲, 一起出队走独立齐射流程 (RunSalvoRoutine).
    /// </summary>
    private void TryDispatch() {
        while (!_paused && _taskQueue.Count > 0) {
            var head = _taskQueue.Peek();
            if (!head.salvoFollower) {
                // 队首是主任务且队列里有它的跟随任务 → 齐射对
                ArtilleryTask? salvoFollower = null;
                foreach (var t in _taskQueue) {
                    if (t.salvoFollower && t.salvoLeader == head) { salvoFollower = t; break; }
                }
                if (salvoFollower != null) {
                    if (LeftTask != null || RightTask != null) break; // 齐射等两炮都空
                    _taskQueue.Dequeue(); // 主任务出队
                    var rest = _taskQueue.ToList();
                    _taskQueue.Clear();
                    foreach (var t in rest) {
                        if (t == salvoFollower) continue; // 跟随任务随主任务一起出队
                        _taskQueue.Enqueue(t);
                    }
                    LeftTask = head;
                    RightTask = salvoFollower;
                    MelonLogger.Msg($"[FCS] Dispatch SALVO fc#{head.fireControlId}+{salvoFollower.fireControlId} ({head.bulletType}, {head.distance:F1}km)");
                    PreComputeFlightTime(head);
                    PreComputeFlightTime(salvoFollower);
                    var h = MelonCoroutines.Start(RunSalvoRoutine(head, salvoFollower));
                    _runningCoroutines.Add((h, LeftRight.Left));
                    continue;
                }
            }
            LeftRight slot;
            if (LeftTask == null) slot = LeftRight.Left;
            else if (RightTask == null) slot = LeftRight.Right;
            else break; // 两管炮都忙

            var task = _taskQueue.Dequeue();
            if (slot == LeftRight.Left) LeftTask = task;
            else RightTask = task;
            MelonLogger.Msg($"[FCS] Dispatch fc#{task.fireControlId} ({task.bulletType}, {task.distance:F1}km) → {slot}");
            PreComputeFlightTime(task);
            StartTaskRoutine(slot, task);
        }
    }

    /// <summary>任务上炮即预解飞行时间 (按计划装药), 面板 T: 不用等推药/抬炮; EAIM 时再按实际装药重闩.</summary>
    private void PreComputeFlightTime(ArtilleryTask task) {
        int plannedCharge = _sceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);
        task.impactTime = ShellData.FlightTime(task.distance, plannedCharge);
    }

    /// <summary>齐射跟随任务克隆: 同目标同弹种, 独立任务实例 (两炮各跑各的状态).</summary>
    private static ArtilleryTask CloneTask(ArtilleryTask t) => new() {
        targetId = t.targetId,
        angel = t.angel,
        distance = t.distance,
        position = t.position,
        bulletType = t.bulletType,
        progress = Progress.Pending,
    };

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
    /// 单次打击任务主流程, CALL 枢纽回跳式弹种保护状态机 (详见 Function.md 第 5 章):
    /// CALL 读现场决策: 有弹正确 + CANFIRE 检查装药 (不够 DUMP / 超出回 CALL 带条件 / 符合 EAIM);
    /// 有弹正确 + 未就绪 PWDR (实拉不足补拉 / 超出回 CALL / 符合或超时 LOAD); 有弹错误 DUMP (平射打掉); 无弹 SELC.
    /// CALL 计数器: PEND 后最多 2 次完整检查, 第 3 次跳过检查直接按实际装药实射, 保证终止.
    /// 检查点鲁棒性: 推弹机动作中 (ShellRamming) 现场读数不可信, 等它完成再决策.
    /// 最多两轮: 第一轮退弹, 等 2s 机构循环, 第二轮实射; 两轮未完 Failed.
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

        // 外层 try/finally 兜底: 取消/中止/热重载在任意 yield 处 Stop 时归还炮塔锁
        // (正常路径已在内部 ReleaseTurretOnce, 幂等不双释放)
        try {
            yield return RunTaskRoutineBody(leftRight, task, gunSys, turret);
        }
        finally {
            ReleaseTurretOnce(turret);
        }
    }

    private IEnumerator RunTaskRoutineBody(LeftRight leftRight, ArtilleryTask task, GunSystem gunSys, TurretReservation turret) {
        int callCount = 0; // CALL 枢纽计数器, PEND 后最多 2 次完整检查

        for (int round = 0; round < 2; round++) {
            bool roundFinished = false;
            bool useActual = false; // CALL(带条件): 以实际装药为基准
            float elevation = 0f;

            while (!roundFinished) {
                while (_paused) yield return null; // 总暂停: 冻结在两步骤之间 (当前步骤跑完才停)
                // ===== 检查点: 推弹机动作中, 膛内读数不可信, 等它完成再决策 (不消耗 CALL 预算) =====
                if (gunSys.IsShellRamming()) {
                    yield return gunSys.WaitShellRammed();
                    continue;
                }

                // ===== 1-1 CALL 枢纽: 读现场 + 决策 =====
                callCount++;
                task.progress = Progress.Calculating;
                MarkProgress(leftRight, Progress.Calculating);

                string? chambered = gunSys.BulletInChamber();
                int loaded = gunSys.LoadedPowderCharges();
                int selected = gunSys.SelectedPowderCharges();
                int need = _sceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(task.distance);
                bool canFire = gunSys.CanFire();
                bool shellWrong = chambered != null && chambered != task.bulletType.ToString();

                int powderCount;
                string path;
                if (callCount > 2) {
                    // 第 3 次 CALL: 跳过检查, 直接按实际装药实射
                    powderCount = loaded > 0 ? loaded : (selected > 0 ? selected : need);
                    path = "aim";
                    MelonLogger.Msg($"[FCS] {leftRight}: CALL x{callCount}, forced proceed with charge {powderCount}");
                }
                else if (shellWrong) {
                    powderCount = loaded > 0 ? loaded : 1;
                    path = "dump";
                }
                else if (chambered == null) {
                    powderCount = need;
                    path = "selc";
                }
                else if (canFire) {
                    // 有弹正确 + CANFIRE: 检查装药
                    int expected = useActual ? loaded : need;
                    if (loaded < expected) {
                        powderCount = loaded > 0 ? loaded : 1;
                        path = "dump";
                    }
                    else if (loaded > expected) {
                        MelonLogger.Msg($"[FCS] {leftRight}: CALL over-charge {loaded}>{expected}, retry with actual");
                        useActual = true;
                        continue;
                    }
                    else {
                        powderCount = loaded;
                        path = "aim";
                    }
                }
                else {
                    // 有弹正确 + 未就绪: 直接进 PWDR
                    powderCount = useActual && (loaded > 0 || selected > 0) ? Math.Max(loaded, selected) : need;
                    path = "pwdr";
                }

                MelonLogger.Msg($"[FCS] {leftRight}: CALL x{callCount} round {round}: path={path}, chambered={chambered}, loaded={loaded}, selected={selected}, need={need}, powderCount={powderCount}");

                // ===== 临界区 1: 补购 (解算不在这里, 整个任务只解算一次, 见加载/瞄准路径) =====
                // 采购台是全局唯一硬件, 必须串行. 买完即放
                yield return _deskLock.Acquire();
                try {
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
                }
                finally {
                    _deskLock.Release();
                }

                // ===== 分支执行 =====
                if (path == "dump") {
                    // 1-3 DUMP: 退弹平射
                    task.progress = Progress.DumpingWrongShell;
                    MarkProgress(leftRight, Progress.DumpingWrongShell);
                    if (!gunSys.CanFire()) {
                        if (gunSys.BulletInChamber() == null) {
                            continue; // 无弹 → 回 CALL 重新决策
                        }
                        // 有弹: 架上保证至少一药
                        if (gunSys.LoadedPowderCharges() == 0 && gunSys.SelectedPowderCharges() == 0) {
                            task.progress = Progress.LoadingPowder;
                            MarkProgress(leftRight, Progress.LoadingPowder);
                            yield return LoadPowderWithDialLock(task, gunSys, 1, 1);
                        }
                        task.progress = Progress.WaitLoading;
                        MarkProgress(leftRight, Progress.WaitLoading);
                        while (!gunSys.CanFire()) {
                            yield return new WaitForSeconds(1f);
                        }
                    }
                    // 平射: HAIM + 击发
                    task.progress = Progress.AimingAzimuth;
                    MarkProgress(leftRight, Progress.AimingAzimuth);
                    while (!turret.Ready) {
                        yield return null;
                    }
                    yield return FireSequence(leftRight, task, gunSys, turret, true);
                    yield return new WaitForSeconds(2f); // 退弹轮结束: 等机构自动循环
                    roundFinished = true;
                }
                else {
                    if (path == "selc") {
                        // 弹仓缺目标弹种才采购 (采购台共享硬件, 进桌子锁); 膛内弹已正确时不会走到这里
                        if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                            yield return _deskLock.Acquire();
                            try {
                                if (!gunSys.HaveBulletInCylinder(task.bulletType)) {
                                    if (!gunSys.HaveEmptyShellInCylinder()) {
                                        // 弹仓全满无空位: 任务不可行
                                        MelonLogger.Error($"[FCS] {leftRight}: cylinder full, no {task.bulletType} slot, fail task");
                                        turret.Canceled = true;
                                        ReleaseTurretOnce(turret);
                                        task.progress = Progress.Failed;
                                        MarkProgress(leftRight, Progress.Failed);
                                        ReleaseSlot(leftRight);
                                        yield break;
                                    }
                                    yield return _purchaseDeck.BuyShell(task.bulletType, leftRight);
                                    // 确认采购到位: 等弹仓出现目标弹种, 最多 3 秒
                                    float waited = 0f;
                                    while (!gunSys.HaveBulletInCylinder(task.bulletType) && waited < 3f) {
                                        yield return new WaitForSeconds(0.5f);
                                        waited += 0.5f;
                                    }
                                }
                            }
                            finally {
                                _deskLock.Release();
                            }
                        }
                        // 空膛: 先等残留实装计数清零(上一发击发后的自动循环)
                        float waitLoaded = 0f;
                        while (gunSys.LoadedPowderCharges() > 0 && waitLoaded < 10f) {
                            yield return new WaitForSeconds(0.5f);
                            waitLoaded += 0.5f;
                        }
                        // 1-2 SELC: 转弹仓选弹
                        yield return gunSys.RotateCylinderTo(task.bulletType);
                        // 2-1 BLRD: 按推弹按钮, 确认架上有弹
                        task.progress = Progress.LoadingBullet;
                        MarkProgress(leftRight, Progress.LoadingBullet);
                        yield return gunSys.PressRammer();
                        yield return gunSys.WaitRammingStart();
                        // 2-2 BLLD: 推弹中
                        task.progress = Progress.RammingBullet;
                        MarkProgress(leftRight, Progress.RammingBullet);
                        yield return gunSys.WaitShellRammed();
                    }

                    if (path is "selc" or "pwdr") {
                        // 2-3 PWDR: 检查装药 (实拉 vs 期望)
                        task.progress = Progress.LoadingPowder;
                        MarkProgress(leftRight, Progress.LoadingPowder);
                        int selectedNow = gunSys.SelectedPowderCharges();
                        if (selectedNow > powderCount) {
                            MelonLogger.Msg($"[FCS] {leftRight}: PWDR over {selectedNow}>{powderCount}, retry with actual");
                            useActual = true;
                            continue; // 超出 → CALL(带条件)
                        }
                        // 不够补拉差, 符合/超时也走推药
                        yield return LoadPowderWithDialLock(task, gunSys, powderCount, powderCount - selectedNow);
                        elevation = task.calculatedElevation; // 本任务唯一解算在锁内已完成, 取快照供 EAIM 用
                        // 2-4 LOAD: 推药 + 等装填完成
                        task.progress = Progress.WaitLoading;
                        MarkProgress(leftRight, Progress.WaitLoading);
                        while (!gunSys.CanFire()) {
                            yield return new WaitForSeconds(1f);
                        }
                        // 2-5 COFM: 击发前装药确认
                        task.progress = Progress.ConfirmingCharge;
                        MarkProgress(leftRight, Progress.ConfirmingCharge);
                        int finalCharge = gunSys.LoadedPowderCharges();
                        if (finalCharge <= 0) finalCharge = powderCount;
                        if (finalCharge != task.charge) {
                            MelonLogger.Msg($"[FCS] {leftRight}: COFM mismatch {finalCharge}!={task.charge}, back to CALL");
                            continue; // 差异 → CALL
                        }
                    }

                    if (path == "aim") {
                        // 实装足量跳过装填: 本任务的唯一一次解算放在这里 (锁内, 算完即放)
                        yield return _deskLock.Acquire();
                        try {
                            yield return BallisticCalculator.SetDistance(task.distance);
                            yield return BallisticCalculator.SetDirection(task.angel);
                            yield return BallisticCalculator.SetCharge(powderCount);
                            yield return BallisticCalculator.SetShellType(task.bulletType);
                            yield return BallisticCalculator.Calculate(); // 游戏靠这次 Calculate 解锁药包杆, 保留
                            elevation = ShellData.ElevationDeg(task.distance, powderCount); // 仰角按射表直算, 不再读计算台输出
                            task.calculatedElevation = elevation;
                            task.charge = powderCount;
                        }
                        finally {
                            _deskLock.Release();
                        }
                    }

                    // 3-1~3-3 EAIM/HAIM/WAIT→TRAK 合并: 双轴持续追踪 (25fps 速度-位置双环, 追 1 帧预测点, 套上后不停),
                    // 套上 (双轴误差+速度收住) → 五步确认 + 解除保险 (一次性) → AutoFire 自动击发 / 手动模式持续追踪等玩家击发
                    task.progress = Progress.Aiming;
                    MarkProgress(leftRight, Progress.Aiming);
                    {
                        var eAxis = new TrackAxis(0.01f, 0.1f, 0.5f); // E 收敛标准 0.01°, 变积分 0.1~0.5
                        var aAxis = new TrackAxis(0.1f, 0.3f, 1.0f);  // H 收敛标准 0.1°, 变积分 0.3~1
                        bool armed = false;
                        bool confirmStarted = false;
                        while (true) {
                            // E 轴: 仰角持续追踪 (天顶星伺服设 1 帧预测值 + TrackAxis 修正)
                            if (!eAxis.GaveUp) {
                                float target = ShellData.ElevationDeg(task.trackDistance, powderCount); // 预测点实时目标仰角
                                float actual = gunSys.ActualElevation();
                                float corr = eAxis.Step(target - actual, gunSys.ElevationVelocity(), actual);
                                if (eAxis.GaveUp) MelonLogger.Error($"[FCS] {leftRight}: TRAK elevation stuck {eAxis.Stuck:F0}s at {actual:F2}, give up");
                                gunSys.SetElevationValue(target + corr);
                            }
                            // H 轴: 方位持续追踪 (先等后台预约首转到位)
                            if (!aAxis.GaveUp) {
                                if (turret.Ready) {
                                    float cur = Turret.CurrentAngle();
                                    if (float.IsNaN(cur)) aAxis.Locked = true; // 读不到方位: 不追踪 (退化为原等待逻辑)
                                    else {
                                        float corr = aAxis.Step(Mathf.DeltaAngle(cur, task.trackAngel), Turret.RotationVelocity(), cur);
                                        if (aAxis.GaveUp) MelonLogger.Error($"[FCS] {leftRight}: TRAK azimuth stuck {aAxis.Stuck:F0}s at {cur:F2}, give up");
                                        Turret.SetDesiredRotation(task.trackAngel + corr);
                                    }
                                }
                                else aAxis.Locked = false;
                            }
                            // 套上且未解除保险: 后台五步确认 + Arm (不阻塞 TRAK, 双轴持续追踪; 一次性, 中止时随本炮协程组一起停)
                            if (!armed && !confirmStarted && eAxis.Locked && aAxis.Locked) {
                                confirmStarted = true;
                                var h = MelonCoroutines.Start(ConfirmArmBackground(leftRight, task, gunSys, () => armed = true));
                                _runningCoroutines.Add((h, leftRight));
                            }
                            // 击发: AutoFire 解除保险即击发; 手动模式等玩家击发 (持续追踪期间随时可打)
                            if (armed) {
                                if (_sceneInteractor.AutoFire) {
                                    task.progress = Progress.Fire; // 3-2 FIRE
                                    MarkProgress(leftRight, Progress.Fire);
                                    yield return _deskLock.Acquire();
                                    try {
                                        TriggerConsole.Fire();
                                        yield return gunSys.WaitFire();
                                    }
                                    finally {
                                        _deskLock.Release();
                                    }
                                    break;
                                }
                                if (gunSys.HasFired()) break; // 玩家已击发 (pendingReload 置位)
                            }
                            yield return new WaitForSeconds(0.04f);
                        }
                        LatchFireTime(leftRight, task, gunSys); // 击发快照: 游戏炮兵计时表真值 (消除固定时间差)
                        AddFinished(task);                     // 完成入列 (最多 8 条 + 溢出计数)
                        ReleaseTurretOnce(turret); // 炮塔方向角独占到实射完成
                    }
                    // 3-4 RSET: 回位
                    task.progress = Progress.BackToIdle;
                    MarkProgress(leftRight, Progress.BackToIdle);
                    yield return gunSys.WaitBackToIdle();
                    task.progress = Progress.Finished;
                    MarkProgress(leftRight, Progress.Finished);
                    _sceneInteractor.TaskFinished(task);
                    ReleaseSlot(leftRight);
                    yield break;
                }
            }
        }

        // 两轮仍未完成(异常兜底): 归还炮塔并失败
        ReleaseTurretOnce(turret);
        task.progress = Progress.Failed;
        MarkProgress(leftRight, Progress.Failed);
        ReleaseSlot(leftRight);
    }

    /// <summary>
    /// 齐射主流程 (独立于单发 RunTaskRoutine, 详见 Function.md 5.6):
    /// 一个齐射对驱动两门炮, 每个相位双炮并行执行, 双方都到位才推进.
    /// 相位: CALL (药包 >= 2x需求 + 膛内检查) → DUMP 双炮退弹 → SELC/BLRD/BLLD 双炮装弹 →
    /// PWDR (解算一次 + 双炮拉杆推药) → LOAD → COFM → EAIM 双炮仰角 → HAIM → 五步确认 + 双炮 Arm + 一击发 → RSET.
    /// </summary>
    private IEnumerator RunSalvoRoutine(ArtilleryTask leader, ArtilleryTask follower) {
        var gunL = LeftGun;
        var gunR = RightGun;
        int need = _sceneInteractor.maxCharge ? 6 : BallisticCalculator.MinimumCharge(leader.distance);
        // 炮塔预约 (同目标, 方向角全炮塔共享, 用主任务方位角)
        var turret = new TurretReservation();
        _runningCoroutines.Add((MelonCoroutines.Start(ReserveTurretAndRotate(leader, turret)), LeftRight.Left));

        // 中止兜底: 取消/中止在任意 yield 处 Stop 时归还炮塔锁 (正常路径已释放, 幂等)
        try {
            yield return RunSalvoRoutineBody(leader, follower, gunL, gunR, need, turret);
        }
        finally {
            ReleaseTurretOnce(turret);
        }
    }

    private IEnumerator RunSalvoRoutineBody(ArtilleryTask leader, ArtilleryTask follower, GunSystem gunL, GunSystem gunR, int need, TurretReservation turret) {
        for (int round = 0; round < 2; round++) {
            // 1-1 CALL: 检查两炮 + 药包库存 >= 2 x 需求 (两炮共用池)
            MarkSalvo(leader, follower, Progress.Calculating);
            string? lCh = gunL.BulletInChamber();
            string? rCh = gunR.BulletInChamber();
            bool lWrong = lCh != null && lCh != leader.bulletType.ToString();
            bool rWrong = rCh != null && rCh != leader.bulletType.ToString();
            yield return _deskLock.Acquire();
            try {
                int buyAttempts = 0;
                while (gunL.RemainingCharges() < 2 * need) {
                    yield return _purchaseDeck.BuyPowders();
                    if (++buyAttempts >= 10) break;
                }
            }
            finally {
                _deskLock.Release();
            }

            // 1-1 CALL: 两炮弹仓缺目标弹则采购 (共享采购台持锁, 左买完买右, 都买完下一步)
            bool leftNeedBuy = !gunL.HaveBulletInCylinder(leader.bulletType);
            bool rightNeedBuy = !gunR.HaveBulletInCylinder(leader.bulletType);
            if (leftNeedBuy || rightNeedBuy) {
                yield return _deskLock.Acquire();
                try {
                    if (leftNeedBuy && !gunL.HaveBulletInCylinder(leader.bulletType)) {
                        if (!gunL.HaveEmptyShellInCylinder()) {
                            MelonLogger.Error($"[FCS] SALVO Left cylinder full, no {leader.bulletType} slot, fail pair");
                            FailSalvo(leader, follower, turret);
                            yield break;
                        }
                        yield return _purchaseDeck.BuyShell(leader.bulletType, LeftRight.Left);
                        float waited = 0f;
                        while (!gunL.HaveBulletInCylinder(leader.bulletType) && waited < 3f) {
                            yield return new WaitForSeconds(0.5f);
                            waited += 0.5f;
                        }
                    }
                    if (rightNeedBuy && !gunR.HaveBulletInCylinder(leader.bulletType)) {
                        if (!gunR.HaveEmptyShellInCylinder()) {
                            MelonLogger.Error($"[FCS] SALVO Right cylinder full, no {leader.bulletType} slot, fail pair");
                            FailSalvo(leader, follower, turret);
                            yield break;
                        }
                        yield return _purchaseDeck.BuyShell(leader.bulletType, LeftRight.Right);
                        float waited = 0f;
                        while (!gunR.HaveBulletInCylinder(leader.bulletType) && waited < 3f) {
                            yield return new WaitForSeconds(0.5f);
                            waited += 0.5f;
                        }
                    }
                }
                finally {
                    _deskLock.Release();
                }
            }

            if (lWrong || rWrong) {
                // 1-3 DUMP: 双炮各自平射误弹, 机构循环后回 CALL
                MarkSalvo(leader, follower, Progress.DumpingWrongShell);
                yield return SalvoDump(LeftRight.Left, gunL, lWrong, leader, turret);
                yield return SalvoDump(LeftRight.Right, gunR, rWrong, follower, turret);
                yield return new WaitForSeconds(2f);
                continue;
            }

            // 快速路径: 两炮同弹同装药 (实装 >= 需求, 均 CanFire) → 跳过装填段, 锁内解算一次直接进 TRAK
            int lLoaded = gunL.LoadedPowderCharges();
            int rLoaded = gunR.LoadedPowderCharges();
            if (gunL.CanFire() && gunR.CanFire() && lLoaded >= need && rLoaded >= need) {
                int useCharge = Mathf.Max(lLoaded, need); // 实装足量按实装 (两炮同装药)
                MelonLogger.Msg($"[FCS] SALVO fast-track fc#{leader.fireControlId}+{follower.fireControlId}: loaded L={lLoaded} R={rLoaded} need={need}, use={useCharge}");
                yield return _deskLock.Acquire();
                try {
                    yield return BallisticCalculator.SetDistance(leader.distance);
                    yield return BallisticCalculator.SetDirection(leader.angel);
                    yield return BallisticCalculator.SetCharge(useCharge);
                    yield return BallisticCalculator.SetShellType(leader.bulletType);
                    yield return BallisticCalculator.Calculate(); // 保留: 游戏靠这次 Calculate 解锁药包杆
                    leader.charge = useCharge;
                    follower.charge = useCharge;
                    leader.calculatedElevation = ShellData.ElevationDeg(leader.distance, useCharge);
                    follower.calculatedElevation = leader.calculatedElevation;
                }
                finally {
                    _deskLock.Release();
                }
                break; // 出装填循环, 进 TRAK
            }

            // 1-2 SELC + 2-1/2-2: 空膛侧转弹仓 + 推弹, 双炮并行
            bool lEmpty = lCh == null;
            bool rEmpty = rCh == null;
            if (lEmpty || rEmpty) {
                MarkSalvo(leader, follower, Progress.SelectingBullet);
                yield return SalvoDual(g => SalvoLoadShell(g, leader.bulletType), lEmpty, rEmpty);
            }

            // 2-3 PWDR: 解算一次 (共享计算台) + 双炮拉杆推药
            MarkSalvo(leader, follower, Progress.LoadingPowder);
            yield return SalvoPowder(leader, follower, gunL, gunR, need);

            // 2-4 LOAD: 两炮都 CanFire
            MarkSalvo(leader, follower, Progress.WaitLoading);
            while (!gunL.CanFire() || !gunR.CanFire()) {
                yield return new WaitForSeconds(0.5f);
            }

            // 2-5 COFM: 两炮实装 vs 快照都一致
            MarkSalvo(leader, follower, Progress.ConfirmingCharge);
            int lc = gunL.LoadedPowderCharges();
            int rc = gunR.LoadedPowderCharges();
            if (lc <= 0) lc = leader.charge;
            if (rc <= 0) rc = follower.charge;
            if (lc != leader.charge || rc != follower.charge) {
                MelonLogger.Msg($"[FCS] SALVO COFM mismatch L={lc}/{leader.charge} R={rc}/{follower.charge}, back to CALL");
                continue;
            }
            break; // 装填确认完成, 出循环进击发
        }

        // 3-1~3-3 SALVO TRAK 合并: 双炮持续追踪同一目标 (trackX 取主任务), 双炮都套上并稳定 → 五步确认 + 双炮 Arm → AutoFire 立即击发 / 手动等击发
        MarkSalvo(leader, follower, Progress.Aiming);
        {
            var eAxisL = new TrackAxis(0.01f, 0.1f, 0.5f); // E 收敛标准 0.01°, 变积分 0.1~0.5
            var eAxisR = new TrackAxis(0.01f, 0.1f, 0.5f);
            var aAxis = new TrackAxis(0.1f, 0.3f, 1.0f);   // H 收敛标准 0.1°, 变积分 0.3~1
            bool armed = false;
            bool confirmStarted = false;
            while (true) {
                // 左炮仰角追踪
                if (!eAxisL.GaveUp) {
                    float target = ShellData.ElevationDeg(leader.trackDistance, leader.charge); // 同目标同装药, 两炮同仰角
                    float actual = gunL.ActualElevation();
                    float corr = eAxisL.Step(target - actual, gunL.ElevationVelocity(), actual);
                    if (eAxisL.GaveUp) MelonLogger.Error($"[FCS] SALVO Left: TRAK elevation stuck {eAxisL.Stuck:F0}s at {actual:F2}, give up");
                    gunL.SetElevationValue(target + corr);
                }
                // 右炮仰角追踪
                if (!eAxisR.GaveUp) {
                    float target = ShellData.ElevationDeg(leader.trackDistance, leader.charge);
                    float actual = gunR.ActualElevation();
                    float corr = eAxisR.Step(target - actual, gunR.ElevationVelocity(), actual);
                    if (eAxisR.GaveUp) MelonLogger.Error($"[FCS] SALVO Right: TRAK elevation stuck {eAxisR.Stuck:F0}s at {actual:F2}, give up");
                    gunR.SetElevationValue(target + corr);
                }
                // 炮塔方位追踪 (全炮塔共享)
                if (!aAxis.GaveUp) {
                    if (turret.Ready) {
                        float cur = Turret.CurrentAngle();
                        if (float.IsNaN(cur)) aAxis.Locked = true;
                        else {
                            float corr = aAxis.Step(Mathf.DeltaAngle(cur, leader.trackAngel), Turret.RotationVelocity(), cur);
                            if (aAxis.GaveUp) MelonLogger.Error($"[FCS] SALVO: TRAK azimuth stuck {aAxis.Stuck:F0}s at {cur:F2}, give up");
                            Turret.SetDesiredRotation(leader.trackAngel + corr);
                        }
                    }
                    else aAxis.Locked = false;
                }
                // 双炮都套上且未解除保险: 后台五步确认 + 双炮 Arm (不阻塞 TRAK, 三轴持续追踪; 一次性, 中止时随协程组停)
                if (!armed && !confirmStarted && eAxisL.Locked && eAxisR.Locked && aAxis.Locked) {
                    confirmStarted = true;
                    var h = MelonCoroutines.Start(SalvoConfirmArm(leader, follower, () => armed = true));
                    _runningCoroutines.Add((h, LeftRight.Left)); // 主炮槽位, 齐射中止时连它一起停
                }
                // 击发: AutoFire 解除保险即击发; 手动等玩家击发
                if (armed) {
                    if (_sceneInteractor.AutoFire) {
                        MarkSalvo(leader, follower, Progress.Fire);
                        yield return SalvoShoot(gunL, gunR);
                        break;
                    }
                    if (gunL.HasFired() || gunR.HasFired()) break;
                }
                yield return new WaitForSeconds(0.04f);
            }
            // 击发快照: 取左炮计时表真值 (同目标同装药, 两炮飞时一致) + 完成入列 + 归还炮塔
            var sw = gunL.StopwatchLatch();
            if (sw.HasValue && sw.Value.travelTime > 0.01f) {
                leader.fireTime = follower.fireTime = sw.Value.startTime;
                leader.impactTime = follower.impactTime = sw.Value.travelTime;
                MelonLogger.Msg($"[FCS] SALVO fire latch travel={sw.Value.travelTime:F2}s");
            }
            else {
                leader.fireTime = follower.fireTime = Time.time;
            }
            AddFinished(leader);
            AddFinished(follower);
            ReleaseTurretOnce(turret);
        }

        // 3-5 RSET: 双炮回位
        MarkSalvo(leader, follower, Progress.BackToIdle);
        yield return SalvoDual(g => g.WaitBackToIdle(), true, true);

        MarkSalvo(leader, follower, Progress.Finished);
        _sceneInteractor.TaskFinished(leader);
        ReleaseSlot(LeftRight.Left);
        ReleaseSlot(LeftRight.Right);
    }

    /// <summary>齐射对进度同步更新 (两行面板一致 + 超时监控时间戳).</summary>
    private void MarkSalvo(ArtilleryTask leader, ArtilleryTask follower, Progress p) {
        leader.progress = p;
        follower.progress = p;
        MarkProgress(LeftRight.Left, p);
        MarkProgress(LeftRight.Right, p);
    }

    /// <summary>齐射对失败收尾: 双任务 Failed + 归还炮塔 + 双槽位释放.</summary>
    private void FailSalvo(ArtilleryTask leader, ArtilleryTask follower, TurretReservation turret) {
        turret.Canceled = true;
        ReleaseTurretOnce(turret);
        MarkSalvo(leader, follower, Progress.Failed);
        ReleaseSlot(LeftRight.Left);
        ReleaseSlot(LeftRight.Right);
    }

    /// <summary>齐射双炮并行驱动: 对指定侧并行执行同一动作, 双方都完成才返回.</summary>
    private IEnumerator SalvoDual(Func<GunSystem, IEnumerator> step, bool doLeft, bool doRight) {
        bool leftDone = !doLeft, rightDone = !doRight;
        if (doLeft) {
            var h = MelonCoroutines.Start(WrapSalvoStep(LeftGun, step, () => leftDone = true));
            _runningCoroutines.Add((h, LeftRight.Left));
        }
        if (doRight) {
            var h = MelonCoroutines.Start(WrapSalvoStep(RightGun, step, () => rightDone = true));
            _runningCoroutines.Add((h, LeftRight.Right));
        }
        while (!leftDone || !rightDone) yield return null;
    }

    private static IEnumerator WrapSalvoStep(GunSystem gun, Func<GunSystem, IEnumerator> step, Action done) {
        yield return step(gun);
        done();
    }

    /// <summary>齐射双炮保险并行: 单侧 Arm 包装, 完成回调.</summary>
    private IEnumerator WrapSalvoArm(LeftRight side, Action done) {
        yield return TriggerConsole.Arm(side);
        done();
    }

    /// <summary>单炮装弹段 (齐射双炮并行各跑): 转弹仓到目标弹种 → 按推弹 → 等推到位.</summary>
    private static IEnumerator SalvoLoadShell(GunSystem gun, BulletType bullet) {
        yield return gun.RotateCylinderTo(bullet);
        yield return gun.PressRammer();
        yield return gun.WaitShellRammed();
    }

    /// <summary>单炮退弹平射 (齐射 DUMP 相位的单炮侧): 有弹保证至少 1 药 → 装填 → 平射.</summary>
    private IEnumerator SalvoDump(LeftRight side, GunSystem gun, bool needDump, ArtilleryTask dumpTask, TurretReservation turret) {
        if (!needDump) yield break;
        if (!gun.CanFire()) {
            // 解锁药包杆 (计算台 charge=1) + 拉 1 杆 + 推药 + 等 CanFire
            yield return _deskLock.Acquire();
            try {
                yield return BallisticCalculator.SetCharge(1);
                yield return BallisticCalculator.Calculate();
            }
            finally {
                _deskLock.Release();
            }
            if (gun.SelectedPowderCharges() <= 0) yield return gun.PullPowders(1);
            yield return gun.RamPowder();
            while (!gun.CanFire()) {
                yield return new WaitForSeconds(0.5f);
            }
        }
        yield return FireSequence(side, dumpTask, gun, turret, isDump: true);
    }

    /// <summary>2-3 PWDR (齐射): 解算一次 (同目标同装药, 一次 Calculate 供两炮) + 双炮并行拉杆推药.</summary>
    private IEnumerator SalvoPowder(ArtilleryTask leader, ArtilleryTask follower, GunSystem gunL, GunSystem gunR, int need) {
        yield return _deskLock.Acquire();
        try {
            yield return BallisticCalculator.SetDistance(leader.distance);
            yield return BallisticCalculator.SetDirection(leader.angel);
            yield return BallisticCalculator.SetCharge(need);
            yield return BallisticCalculator.SetShellType(leader.bulletType);
            yield return BallisticCalculator.Calculate(); // 游戏靠这次 Calculate 解锁两炮药包杆
            leader.charge = need;
            follower.charge = need;
            leader.calculatedElevation = ShellData.ElevationDeg(leader.distance, need);
            follower.calculatedElevation = leader.calculatedElevation;
        }
        finally {
            _deskLock.Release();
        }
        yield return SalvoDual(g => PullAndRamPowder(g, need), true, true);
    }

    private static IEnumerator PullAndRamPowder(GunSystem gun, int need) {
        int selected = gun.SelectedPowderCharges();
        if (selected < need) yield return gun.PullPowders(need - selected);
        yield return gun.RamPowder();
    }

    /// <summary>后台五步确认 + Arm (TRAK 套上后启动): 与追踪循环并行, 确认台流程不冻结 E/H 追踪.
    /// 登记进本炮协程组: AbortGun/StopGun 会连它一起停 (finally 释放 deskLock).</summary>
    private IEnumerator ConfirmArmBackground(LeftRight leftRight, ArtilleryTask task, GunSystem gunSys, Action onArmed) {
        yield return _deskLock.Acquire(); // 五步确认台全局唯一, 两炮串行过台
        try {
            yield return TriggerConsole.ConfirmTask();
            yield return TriggerConsole.ConfirmBullet();
            yield return TriggerConsole.ConfirmRotation();
            yield return TriggerConsole.ConfirmElevation();
            yield return TriggerConsole.ReadyToFire();
            yield return TriggerConsole.Arm(leftRight);
            if (task.impactTime <= 0f) task.impactTime = ShellData.FlightTime(task.distance, task.charge); // 飞时兜底锁存
            MelonLogger.Msg($"[FCS] {leftRight}: TRAK armed fc#{task.fireControlId} (elev={gunSys.ActualElevation():F2} dist={task.distance:F2}km)");
            onArmed();
        }
        finally {
            _deskLock.Release();
        }
    }

    /// <summary>齐射解除保险 (deskLock 内): 五步确认走一遍 (确认台校验两门炮) + 双炮并行 Arm + 飞时兜底锁存.</summary>
    private IEnumerator SalvoConfirmArm(ArtilleryTask leader, ArtilleryTask follower, Action onArmed) {
        yield return _deskLock.Acquire();
        try {
            yield return TriggerConsole.ConfirmTask();
            yield return TriggerConsole.ConfirmBullet();
            yield return TriggerConsole.ConfirmRotation();
            yield return TriggerConsole.ConfirmElevation();
            yield return TriggerConsole.ReadyToFire();
            // 双炮保险同帧按下: 并行驱动两根 ArmingLever (顺序调用会一前一后 0.2s)
            bool leftArmed = false, rightArmed = false;
            var armL = MelonCoroutines.Start(WrapSalvoArm(LeftRight.Left, () => leftArmed = true));
            var armR = MelonCoroutines.Start(WrapSalvoArm(LeftRight.Right, () => rightArmed = true));
            _runningCoroutines.Add((armL, LeftRight.Left));
            _runningCoroutines.Add((armR, LeftRight.Right));
            while (!leftArmed || !rightArmed) yield return null;
            if (leader.impactTime <= 0f) leader.impactTime = ShellData.FlightTime(leader.distance, leader.charge);
            follower.impactTime = leader.impactTime;
            MelonLogger.Msg($"[FCS] SALVO armed fc#{leader.fireControlId}+{follower.fireControlId}");
            onArmed();
        }
        finally {
            _deskLock.Release();
        }
    }

    /// <summary>齐射击发 (deskLock 内): 一击发 (击发钮全局一个, 一按两炮齐射) + 双炮 WaitFire.</summary>
    private IEnumerator SalvoShoot(GunSystem gunL, GunSystem gunR) {
        yield return _deskLock.Acquire();
        try {
            TriggerConsole.Fire();
            yield return SalvoDual(g => g.WaitFire(), true, true);
        }
        finally {
            _deskLock.Release();
        }
    }

    /// <summary>临界区 2 击发: 五步确认 + Arm + 拷贝飞行时间 + Fire + WaitFire. 非退弹轮归还炮塔.</summary>
    private IEnumerator FireSequence(LeftRight leftRight, ArtilleryTask task, GunSystem gunSys, TurretReservation turret, bool isDump) {
        task.progress = Progress.WaitingForFire;
        MarkProgress(leftRight, Progress.WaitingForFire);
        try {
            yield return _deskLock.Acquire(); // 五步确认台全局唯一: 齐射两炮串行过台, 防抢台导致保险没开
            try {
                yield return TriggerConsole.ConfirmTask();
                yield return TriggerConsole.ConfirmBullet();
                yield return TriggerConsole.ConfirmRotation();
                yield return TriggerConsole.ConfirmElevation();
                yield return TriggerConsole.ReadyToFire();
                yield return TriggerConsole.Arm(leftRight);
                // 总飞行时间正常在仰角就位时锁存; 这里兜底平射轮等未锁存的情况
                if (task.impactTime <= 0f) task.impactTime = ShellData.FlightTime(task.distance, task.charge);
                task.progress = Progress.Fire; // 3-4 FIRE: 击发瞬间 (自动/手动开火都一闪而过)
                MarkProgress(leftRight, Progress.Fire);
                if (_sceneInteractor.AutoFire) {
                    TriggerConsole.Fire();
                }
                yield return gunSys.WaitFire();
            }
            finally {
                _deskLock.Release();
            }
        }
        finally {
            // 炮塔方向角独占到实射这一发打出去为止; 退弹轮继续持有给下一轮
            if (!isDump) ReleaseTurretOnce(turret);
        }
        if (!isDump) {
            LatchFireTime(leftRight, task, gunSys); // 击发快照 (退弹轮不算完成任务)
            AddFinished(task);
        }
    }

    /// <summary>击发快照: 以游戏炮兵计时表真值锁存 fireTime/impactTime (游戏击发有 fireDelay 等固定延迟, 用 Time.time 会早一个固定差), 无表则用 Time.time.</summary>
    private void LatchFireTime(LeftRight leftRight, ArtilleryTask task, GunSystem gunSys) {
        var sw = gunSys.StopwatchLatch();
        if (sw.HasValue && sw.Value.travelTime > 0.01f) {
            task.fireTime = sw.Value.startTime;
            task.impactTime = sw.Value.travelTime;
            MelonLogger.Msg($"[FCS] {leftRight}: fire latch travel={sw.Value.travelTime:F2}s (formula 预测 {ShellData.FlightTime(task.distance, task.charge):F2}s)");
        }
        else {
            task.fireTime = Time.time;
        }
    }

    /// <summary>反炮兵 (敌方下一轮炮击) 剩余秒数: 未激活/已停止/已过期 = NaN; 暂停 (打掉 FDC) 时数值冻结.</summary>
    public float CbtSeconds
    {
        get
        {
            try
            {
                var cbt = CounterBatteryTimer.Instance;
                if (cbt == null || !cbt.IsRunning || cbt.IsExpired || cbt.IsPermanentlyStopped) return float.NaN;
                return cbt.TimeRemaining;
            }
            catch { return float.NaN; }
        }
    }

    /// <summary>完成入列 (最多 8 条 + 溢出计数).</summary>
    private void AddFinished(ArtilleryTask task) {
        // 抵达时刻基准: 回推击发瞬间的任务时钟秒数 (任务时钟与 Time.time 同速), 无时钟时为 NaN
        task.fireMissionTime = MissionSeconds - (Time.time - task.fireTime);
        _finished.Add(new FinishedTask { task = task, fireTime = Time.time });
        if (_finished.Count > 8) {
            _finished.RemoveAt(0);
            _finishedOverflow++;
        }
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
    /// <summary>
    /// PWDR 段: 锁内只做重算 (距离/方向/装药/弹种 + Calculate, 秒级短临界区), 拉药包杆+推药在锁外.
    /// 游戏只在按 Calculate 那一刻存储"已计算装药数", 药包杆最多允许拉到这个数;
    /// 弹道计算器全局唯一, 另一炮的解算会改写存储值. 拉杆放锁外有被中途改写的可能 (上限被改小 → 少拉),
    /// 由 CALL 回读实装兜底补拉 (与齐射 SalvoPowder 同款短临界区, 长等待不再堵住另一炮的解算).
    /// </summary>
    private IEnumerator LoadPowderWithDialLock(ArtilleryTask task, GunSystem gunSys, int calcCharge, int pullCount) {
        yield return _deskLock.Acquire();
        try {
            yield return BallisticCalculator.SetDistance(task.distance);
            yield return BallisticCalculator.SetDirection(task.angel);
            yield return BallisticCalculator.SetCharge(calcCharge);
            yield return BallisticCalculator.SetShellType(task.bulletType);
            yield return BallisticCalculator.Calculate(); // 游戏靠这次 Calculate 解锁药包杆, 保留
            task.calculatedElevation = ShellData.ElevationDeg(task.distance, calcCharge); // 仰角按射表直算, 快照供 EAIM 用
            task.charge = calcCharge;
        }
        finally {
            _deskLock.Release();
        }
        if (pullCount > 0) {
            yield return gunSys.PullPowders(pullCount);
        }
        yield return gunSys.RamPowder();
    }

    private IEnumerator ReserveTurretAndRotate(ArtilleryTask task, TurretReservation res) {
        while (_paused) yield return null; // 总暂停: 计划模式不抢炮塔不转向
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
