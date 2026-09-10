using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>装药模式三档 (T/N/X): Tight 最省 / Normal 默认效率 (仰角≤30° 尽量) / Extra 强制最大.</summary>
public enum ChargeMode { Tight, Normal, Extra }

/// <summary>四档运行模式: FullAuto 扫荡强制自动开火 / SemiAuto 自动到击发 / PreAiming 自动到解保险 / Manual 全手动.</summary>
public enum FireMode { Manual, PreAiming, SemiAuto, FullAuto }

/// <summary>火控任务 (2.0): 绑定目标实体 (DC 参数表持位置/轨迹参数, FC 每帧读参).</summary>
public class FireTask {
    public int Id;
    public string Name = "";
    public GameObject? Entity;   // 绑定实体/令牌 (FC 据此查 DC 目标参数表; Entity 失效 → 撤任务)
    public BulletType Shell;
    public ChargeMode Mode = ChargeMode.Normal;
    public float PlannedStrikeTime = -1f; // 预定打击时间 (任务时钟秒, -1 = 就绪即打)
    public int Priority;
    public float Angle;      // 最新解算方位 (HUD 显示缓存)
    public float Distance;   // 最新解算距离 (HUD 显示缓存)
    public Vector2 AimBoard; // 交汇点 (板面单位) — 3D 预瞄线终点直连 (与轨迹线终点同点); 直瞄 = 目标当前位置
    public bool SalvoPair;   // 齐射对标志: 派发时两炮同任务 + SyncCommand
    public int LockedCharge = -1; // 派发锁存的装药解析 (COFM 与仰角解算数据源; -1 = 未锁存, 完成/撤销清零)
    public bool Dump;        // DUMP 占位 (FC 内部构造: 同膛内弹 1 包药 0° 平射; 不进队列/Finished, 无目标)
}

/// <summary>
/// [FC] FireControl — 2.0 大脑 (迁移期: 与旧 FSC 并存, 未接线).
/// 循环 25fps: 接收火控请求 → 任务队列 (智能派发: 实装匹配优先) → 每帧诸元 (方位/距离→装药模式→仰角→提前量解析解)
///   → 持续输出 GC 指令 (DesiredX + AzimuthSelect) → 就绪判定 (AllReady+选中) → 统一火控 (五步确认+Arm+击发, 短锁)
///   → 收尾 (Fired 快照 / 阵亡撤任务 / FALL 换炮). 预定打击时间: 当前任务时钟 + FlyTime ≥ 预定 → 开火.
/// 保险 (Arm) 也是 FC 的事, 首次 AllReady 解一次, 已 Arm 不自动回保险.
/// </summary>
public class FireControl {
    // ===== 端口接线 (FcsModule 注入) =====
    public GunControl? GunL;
    public GunControl? GunR;
    public TriggerConsole? ConsolePort;       // 五步确认台 + 击发钮 (统一火控)
    public DisplayControl? DcPort;            // 目标参数表 (位置/轨迹参数直读; Entity 失效 → 撤任务)
    public BallisticCalculator? Calculator;   // 解算台 (Calculate 保留: 出火控卡仪式感)
    public CoroutineLock? FireLock;           // 统一火控短锁 (与 GC 的 DUMP 平射共享)
    public Transform? NestRef;                // 铁巢/炮塔参考 (相对方位计算用)
    public Transform? MapSurfaceRef;          // "Draggable Surface" (局部系换算用)
    /// <summary>队列显示 push (DC 渲染线程): 打击队列指示器数据 (FC 自引用传出).</summary>
    public System.Action<FireControl>? OnQueueChanged;

    // ===== 请求入口 (DC 调用; 显控台舰长席自己排布, FC 只接收) =====
    private readonly List<FireTask> _requests = new();   // 待处理请求 (入队/取消)
    private readonly List<FireTask> _queue = new();      // 任务队列
    private int _fcCounter;

    // ===== 状态 =====
    public FireMode Mode { get; private set; } = FireMode.Manual;
    public int QueueCount => _queue.Count;
    public IReadOnlyList<FireTask> Queue => _queue;
    public FireTask? LeftTask => _gL.Task;
    public FireTask? RightTask => _gR.Task;
    private bool _autoFire;
    private bool _autoTask;
    private bool _manual;
    /// <summary>AutoFire 开关 (DC 传): 运行中切换即时重推四档 (扫荡开=FullAuto, AF=Semi, AF off=PreAiming).</summary>
    public bool AutoFire {
        get => _autoFire;
        set { _autoFire = value; RederiveMode(); }
    }
    /// <summary>AutoTask 开关: 开自动拉起 AutoFire (扫荡强制自动开火).</summary>
    public bool AutoTask {
        get => _autoTask;
        set { _autoTask = value; if (value) _autoFire = true; RederiveMode(); }
    }

    private void RederiveMode() {
        Mode = _manual ? FireMode.Manual
            : _autoTask ? FireMode.FullAuto
            : _autoFire ? FireMode.SemiAuto
            : FireMode.PreAiming;
    }
    /// <summary>Pause: 冻结火控派发与炮塔控制输出 (豁免: 飞行计时/落点指示在 GC/DC 常驻线程, 不受影响); 在途动作 GC 自行跑完.</summary>
    public bool Paused { get; set; }
    private object? _loopHandle;
    private bool _disposed;

    /// <summary>反炮兵 (敌方下一轮炮击) 剩余秒数: 未激活/已停 NaN; 只读显示, 暂停/延长是游戏自身行为.</summary>
    public float CbtSeconds {
        get {
            try {
                var cbt = CounterBatteryTimer.Instance;
                if (cbt == null || !cbt.IsRunning || cbt.IsExpired || cbt.IsPermanentlyStopped) return float.NaN;
                return cbt.TimeRemaining;
            }
            catch { return float.NaN; }
        }
    }

    public void Start() {
        _disposed = false;
        _loopHandle = MelonCoroutines.Start(Loop());
    }

    public void Stop() {
        _disposed = true;
        if (_loopHandle != null) { try { MelonCoroutines.Stop(_loopHandle); } catch { } }
        _loopHandle = null;
        _queue.Clear();
        _requests.Clear();
        _gL.Task = _gR.Task = null;
        _gL.Armed = _gR.Armed = false;
        _gL.Confirmed = _gR.Confirmed = _salvoConfirmed = false;
        Mode = FireMode.Manual;
    }

    // ===== DC 调用: 请求入口 =====
    public void RequestTask(FireTask task) { task.Id = ++_fcCounter; _requests.Add(task); }

    /// <summary>任务是否已上炮 (上炮任务右键 = 取消, 不许改计划, 1.x 口径).</summary>
    public bool IsOnGun(FireTask task) => task == _gL.Task || task == _gR.Task;

    /// <summary>按实体找任务 (队列 + 在炮).</summary>
    public FireTask? FindEntityTask(GameObject entity) {
        foreach (var t in _queue) if (t.Entity == entity) return t;
        if (_gL.Task?.Entity == entity) return _gL.Task;
        if (_gR.Task?.Entity == entity) return _gR.Task;
        return null;
    }
    public void RequestCancel(FireTask task) {
        _requests.Remove(task);
        _queue.Remove(task);
        task.LockedCharge = -1; // 装药冻结随任务生命周期走
        if (_gL.Task == task) { _gL.Task = null; _gL.Armed = false; _gL.Confirmed = false; _gL.Fire = false; if (GunL != null) { GunL.DesiredShell = (BulletType)(-1); GunL.DesiredCharge = -1; } }
        if (_gR.Task == task) { _gR.Task = null; _gR.Armed = false; _gR.Confirmed = false; _gR.Fire = false; if (GunR != null) { GunR.DesiredShell = (BulletType)(-1); GunR.DesiredCharge = -1; } }
        if (_gL.Task == null && _gR.Task == null) _salvoConfirmed = false;
        ClearSyncIfAlone();
    }

    /// <summary>Stop: 清队列 + 撤任务 (线程常驻不死, 只是改指令内容).</summary>
    public void StopTasks() {
        foreach (var t in _queue) t.LockedCharge = -1; // 装药冻结随任务生命周期走
        _gL.DumpStuck = _gR.DumpStuck = false; // 挂起复位 (玩家手动接管处理哑弹)
        _queue.Clear();
        _requests.Clear();
        _finished.Clear();
        _gL.Task = _gR.Task = null;
        _gL.Armed = _gR.Armed = false;
        _gL.Confirmed = _gR.Confirmed = _salvoConfirmed = false;
        _gL.Fire = _gR.Fire = false;
        if (GunL != null) { GunL.DesiredShell = (BulletType)(-1); GunL.DesiredCharge = -1; }
        if (GunR != null) { GunR.DesiredShell = (BulletType)(-1); GunR.DesiredCharge = -1; }
        ClearSyncIfAlone();
    }

    /// <summary>模式推导 (DC 只传 AutoFire + AutoTask 开关位): 扫荡=FullAuto 强制自动开火; AF=Semi; AF off=PreAiming; ManualControl=Manual.</summary>
    public void SetManual(bool manual) {
        _manual = manual;
        RederiveMode();
        if (manual) _gL.DumpStuck = _gR.DumpStuck = false; // 挂起复位 (玩家手动接管处理哑弹)
        if (GunL != null) GunL.ManualControl = manual;
        if (GunR != null) GunR.ManualControl = manual;
    }

    private IEnumerator Loop() {
        while (!_disposed) {
            yield return new WaitForSeconds(0.04f);
            try {
                ProcessRequests();                    // 入队不受模式限制 (计划模式可先入队)
                UpdateQueueSolutions();               // 队列任务也实时解方位/距离 (HUD 显示用, 不受模式限制)
                ComputeGunSolutions();                // 火控解析与炮无关: 任何状态都持续刷新 (Pause/Manual 也不停)
                PushFireSolutions();                  // 解算 push → DC 渲染目标轨迹线+交汇点 (25fps, 不受模式限制)
                OnQueueChanged?.Invoke(this);         // 队列显示同样不受限
                if (Mode == FireMode.Manual || Paused) continue; // Manual 停机 / Pause 冻结派发与炮塔控制
                Dispatch();                       // 空闲炮 + 实装匹配派发
                ApplySolutions();                 // 解算 → GC 指令 (受门控)
                FireArbiter();                    // 就绪 → 统一火控 → 收尾
            }
            catch (Exception ex) {
                MelonLogger.Error($"[FC] loop error: {ex}");
            }
        }
    }

    /// <summary>队列任务坐标解算 (显示用): 与在炮真解算同源 SolveOne (C4), 不碰硬件不派发.</summary>
    private void UpdateQueueSolutions() {
        foreach (var t in _queue) {
            if (t == _gL.Task || t == _gR.Task) continue; // 在炮任务由 ComputeGunSolutions 解
            var target = DcPort?.GetTarget(t.Entity!);
            if (target == null) continue;
            var (rawDist, _) = RelToTarget(target.WorldPos);
            SolveOne(t, target, ChargeOf(t, rawDist)); // 队列显示: 按当前距离解析装药 (派发时重解)
        }
    }

    private void ProcessRequests() {
        foreach (var r in _requests) {
            if (!_queue.Contains(r)) {
                // 动目标强制 N 入队: T (最小药数) 随距离变, 目标一动锁存装药就没裕量了 (打不到新距离);
                // N 解析倾向多装 (仰角≤30° 尽量) 有余量. 入队时点判定: TWS 速度矢量非零 = 动目标
                if (r.Mode == ChargeMode.Tight) {
                    var target = DcPort?.GetTarget(r.Entity!);
                    if (target != null && target.Velocity.magnitude > 0.0001f) {
                        r.Mode = ChargeMode.Normal;
                        MelonLogger.Msg($"[FC] fc#{r.Id} moving target: Tight → Normal");
                    }
                }
                _queue.Add(r);
            }
        }
        _requests.Clear();
    }

    /// <summary>智能派发: 只队首 (乱序在 Start 时 SortQueueOnce 排一次, 派发不搜索后续).
    /// 空闲炮三态: 空膛 → 派队首 (GC 装填); 弹对且 (没药 → 装药打 / 药够 ≥ 最小装药 → 按膛内药解析弹道) → 派;
    /// 弹不对/药不够 → FC 强制 DUMP 占位 (同膛内弹 1 包药 0° 平射).
    /// 齐射: 两炮都空 (槽+Idle+空膛) 才挂双槽 (同时装弹); 膛内有弹先单炮 DUMP 清空.</summary>
    private void Dispatch() {
        while (_queue.Count > 0) {
            var head = _queue[0];
            if (head.SalvoPair) { // 齐射: 等两炮都回 WAIT 且空膛才挂双槽 (同任务 + SyncCommand, 同时装弹)
                if (_gL.Task != null || _gR.Task != null || GunL == null || GunR == null) break;
                if (GunL.Action != GunAction.Idle || GunR.Action != GunAction.Idle) break;
                if (_gL.DumpStuck || _gR.DumpStuck) break; // 挂起炮不参与齐射 (哑弹需人工处理)
                if (GunL.ChamberLive.Length > 0 || GunR.ChamberLive.Length > 0) {
                    AttachDump(GunL.ChamberLive.Length > 0 ? GunL : GunR); // 膛内有弹: 先 DUMP 清膛 (占位), 下轮再查
                    break;
                }
                _queue.RemoveAt(0);
                _gL.Task = _gR.Task = head;
                ClearSyncIfAlone();
                MelonLogger.Msg($"[FC] dispatch SALVO fc#{head.Id} {head.Shell}");
                continue;
            }
            GunControl? freeL = GunL != null && _gL.Task == null && !_gL.DumpStuck && GunL.Action != GunAction.Fall ? GunL : null;
            GunControl? freeR = GunR != null && _gR.Task == null && !_gR.DumpStuck && GunR.Action != GunAction.Fall ? GunR : null;
            if (freeL == null && freeR == null) break;
            // 空闲炮挑选: 弹种匹配队首者优先 (都匹配/都不匹配 → L)
            GunControl pick;
            if (freeL != null && freeR != null) {
                bool lOk = LoadoutMatches(freeL, head), rOk = LoadoutMatches(freeR, head);
                pick = lOk || !rOk ? freeL : freeR;
            }
            else pick = freeL ?? freeR!;
            string ch = pick.ChamberLive;
            int charges = pick.ChargesLive;
            bool serve = ch.Length == 0 || (ch == head.Shell.ToString() && (charges == 0 || charges >= ChargeOf(head, 0f)));
            if (serve) {
                _queue.RemoveAt(0);
                if (pick == freeL) _gL.Task = head; else _gR.Task = head;
                ClearSyncIfAlone();
                MelonLogger.Msg($"[FC] dispatch fc#{head.Id} {head.Shell} → {(pick == GunL ? "L" : "R")} (chamber '{ch}' charges {charges})");
            }
            else {
                AttachDump(pick); // 膛内弹不对/药不够最小装药: FC 强制 DUMP (占位后本炮不再空闲, 下轮再派)
                break;
            }
        }
    }

    /// <summary>DUMP 占位上炮 (FC 强制退弹): 同膛内弹 1 包药, 仰角 0 平射方位不动; 不进队列/Finished, 右键不可取消 (Entity null).</summary>
    private void AttachDump(GunControl gun) {
        var dump = new FireTask {
            Id = ++_fcCounter,
            Name = "DUMP",
            Shell = System.Enum.TryParse<BulletType>(gun.ChamberLive, out var b) ? b : BulletType.AP,
            Mode = ChargeMode.Normal,
            Priority = 0,
            Dump = true,
            LockedCharge = 1,
        };
        if (gun == GunL) _gL.Task = dump; else _gR.Task = dump;
        ClearSyncIfAlone();
        MelonLogger.Msg($"[FC] {(gun == GunL ? "L" : "R")}: DUMP placeholder (chamber '{gun.ChamberLive}' charges {gun.ChargesLive})");
    }

    /// <summary>乱序重排 (Start 时排一次, 之后不动): 非空膛炮的膛内弹匹配后续任务且与队首目标方位差 ≤45°
    /// → 该后续任务提前到队首后 (超线程, 物尽其用); 45° 一刀切不估算代价 (打完转炮塔 ≤ ~11s). 派发只队首不搜索.</summary>
    public void SortQueueOnce() {
        if (_queue.Count < 2) return;
        var head = _queue[0];
        if (head.SalvoPair || float.IsNaN(head.Angle)) return; // 齐射队首不重排 (等两炮空膛); 无解算方位无从比较
        for (int i = 1; i < _queue.Count; i++) {
            var t = _queue[i];
            if (t.SalvoPair || float.IsNaN(t.Angle)) continue;
            float dA = Mathf.Abs(Mathf.DeltaAngle(head.Angle, t.Angle));
            if (dA > 45f) continue;
            bool matches = (GunL != null && LoadoutMatches(GunL, t)) || (GunR != null && LoadoutMatches(GunR, t));
            if (matches) {
                _queue.RemoveAt(i);
                _queue.Insert(1, t);
                MelonLogger.Msg($"[FC] reorder: fc#{t.Id} {t.Shell} after head fc#{head.Id} (chamber match, ΔA={dA:F0}°)");
                break; // 一次只提一个
            }
        }
    }

    /// <summary>SyncCommand 随齐射对置位/复位 (GC 相位级同步).</summary>
    private void ClearSyncIfAlone() {
        bool salvo = (_gL.Task?.SalvoPair ?? false) || (_gR.Task?.SalvoPair ?? false);
        if (GunL != null) GunL.SyncCommand = salvo;
        if (GunR != null) GunR.SyncCommand = salvo;
    }

    /// <summary>实装匹配 (活读 — 玩家可介入处不依赖快照): 膛内弹对 + 药数 ≥ 最小装药 (≥ChargeOf 按膛内药解析弹道).</summary>
    private static bool LoadoutMatches(GunControl gun, FireTask t) {
        return gun.ChamberLive == t.Shell.ToString() && gun.ChargesLive >= ChargeOf(t, 0f);
    }

    private static int ChargeOf(FireTask t, float dist) {
        switch (t.Mode) {
            case ChargeMode.Tight: return BallisticCalculator.MinimumCharge(dist);
            case ChargeMode.Extra: return 6;
            default: // Normal: 保证仰角 ≤ 30° 尽量, 达不到取 6
                for (int c = BallisticCalculator.MinimumCharge(dist); c <= 6; c++) {
                    if (ShellData.ElevationDeg(dist, c) <= 30f) return c;
                }
                return 6;
        }
    }

    /// <summary>单任务解算 (C4 统一口径 — 队列显示与在炮真解算同源): 相对方位/距离 → LeadSolve 提前量 (k 按装药) → 交汇点.
    /// 回写 task.Angle/Distance/AimBoard (无预瞄时 AimBoard=NaN 哨兵); 返回 (dist, angle, aimKm, vKm, aKm, jKm) 供快照/下发.</summary>
    private (float dist, float angle, Vector2 aimKm, Vector2 vKm, Vector2 aKm, Vector2 jKm) SolveOne(FireTask task, DcTarget target, int charge) {
        Vector2 nestBoard = MapSurfaceRef != null && NestRef != null ? (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position) : Vector2.zero;
        Vector2 vKm = target.Velocity, aKm = target.Accel, jKm = target.Jerk; // TWS 轨迹参数直读 (TWS 关 = 零, 自然直瞄)
        var (dist, angle) = RelToTarget(target.WorldPos);
        Vector2 dir = new(Mathf.Sin(angle * Mathf.Deg2Rad), Mathf.Cos(angle * Mathf.Deg2Rad));
        Vector2 aimKm = dir * dist; // 直瞄: 交汇点 = 目标位置
        if (vKm.magnitude > 0.0001f) {
            var (ld, la, aim) = LeadSolve(dist, angle, vKm, aKm, jKm, charge);
            dist = ld; angle = la; aimKm = aim;
        }
        task.Angle = angle;
        task.Distance = dist;
        task.AimBoard = vKm.magnitude > 0.0001f ? nestBoard + aimKm * GeoMap.MapCellSize : new Vector2(float.NaN, float.NaN);
        return (dist, angle, aimKm, vKm, aKm, jKm);
    }

    /// <summary>单炮火控状态 (C8: L/R 成对字段统一为两个实例).</summary>
    private struct PerGun {
        public FireTask? Task;       // 在炮任务
        public bool Armed;           // 保险已解除
        public bool Confirmed;       // 五步确认已完成
        public bool Confirming;      // 五步确认进行中
        public bool Fire;            // 击发已按 (Fired latch 前防连点)
        public bool DumpStuck;       // DUMP 全哑挂起 (Manual/Stop 复位)
        public FireTask? SolTask;    // 解算快照对应任务 (派发当帧解算未刷, 应用层据此跳过)
        public FireTask? FrozenTask; // 击发真空期锁存对应任务 (换任务自动重快照)
        public float SolCharge, SolElev, SolT, FrozenT;              // 解算快照: 装药/仰角/飞时 (HUD 显示 + GC 下发同源)
        public Vector2 SolAim, SolV, SolA, SolJ;                     // 解算快照: 交汇点矢量 (km 局部) / 轨迹参数 (km/s, km/s², km/s³)
        public Vector2 FrozenAim, FrozenV, FrozenA, FrozenJ;         // 击发瞬间解算锁存 (push 用 — 预瞄线/交汇点停住不跟滑)
    }
    private PerGun _gL, _gR;
    /// <summary>火控解算 push (DC 渲染目标轨迹线+交汇点): (side, 目标引用, 交汇点板面, 轨迹速度板面/s, 加速度板面/s², 三次项板面/s³, 飞时秒).
    /// 无任务/DUMP/解算无效 → 目标引用 null (DC 隐藏). 开火后 DC 侧冻结 (GC 击发通知), push 被忽略.</summary>
    public System.Action<LeftRight, Transform?, Vector2, Vector2, Vector2, Vector2, float>? OnFireSolution;
    /// <summary>解算快照 (HUD 第二行显示用): 仰角/装药, 无任务 NaN/-1.</summary>
    public float SolElevL => _gL.Task != null ? _gL.SolElev : float.NaN;
    public float SolElevR => _gR.Task != null ? _gR.SolElev : float.NaN;
    public int SolChargeL => _gL.Task != null ? (int)_gL.SolCharge : -1;
    public int SolChargeR => _gR.Task != null ? (int)_gR.SolCharge : -1;

    /// <summary>火控解析 (与炮无关, 任何状态持续刷新): 位置源 → 方位/距离 (含提前量) → 装药/仰角. 只写任务缓存与本地变量, 不碰 GC.</summary>
    private void ComputeGunSolutions() {
        foreach (var (gun, task) in new[] { (GunL, _gL.Task), (GunR, _gR.Task) }) {
            if (gun == null) continue;
            bool isL = gun == GunL;
            if (task == null) {
                if (isL) { _gL.SolCharge = -1; _gL.SolElev = float.NaN; _gL.SolTask = null; _gL.SolAim = Vector2.zero; _gL.SolV = Vector2.zero; _gL.SolA = Vector2.zero; _gL.SolJ = Vector2.zero; _gL.SolT = float.NaN; }
                else { _gR.SolCharge = -1; _gR.SolElev = float.NaN; _gR.SolTask = null; _gR.SolAim = Vector2.zero; _gR.SolV = Vector2.zero; _gR.SolA = Vector2.zero; _gR.SolJ = Vector2.zero; _gR.SolT = float.NaN; }
                continue;
            }
            if (task.Dump) {
                // DUMP 占位: 无目标无弹道 — 仰角 0 平射, 方位 = 当前炮方位 (不转炮塔), 装药 1 (膛内同弹种)
                if (isL) { _gL.SolCharge = 1; _gL.SolElev = 0f; task.Angle = gun.Azimuth; task.Distance = 0f; _gL.SolTask = task; _gL.SolAim = Vector2.zero; _gL.SolV = Vector2.zero; _gL.SolA = Vector2.zero; _gL.SolJ = Vector2.zero; _gL.SolT = float.NaN; }
                else { _gR.SolCharge = 1; _gR.SolElev = 0f; task.Angle = gun.Azimuth; task.Distance = 0f; _gR.SolTask = task; _gR.SolAim = Vector2.zero; _gR.SolV = Vector2.zero; _gR.SolA = Vector2.zero; _gR.SolJ = Vector2.zero; _gR.SolT = float.NaN; }
                continue;
            }
            var target = DcPort?.GetTarget(task.Entity!);
            if (target == null) { RequestCancel(task); continue; } // 目标失效 (阵亡/令牌离图) → 撤任务
            Vector2 vKm = target.Velocity, aKm = target.Accel, jKm = target.Jerk; // TWS 轨迹参数直读 (TWS 关 = 零, 自然直瞄)
            var (rawDist, rawAngle) = RelToTarget(target.WorldPos);
            bool liveMatches = false;
            // 装药冻结: 派发帧锁存一次 (初始解析), 之后不再重解析 — 目标运动/距离变化不改变装填计划
            // (每帧重解析会让 DesiredCharge 中途变卦: 装填链换药 COFM 报错 / 膛内药数与仰角解算脱节打飞)
            // 锁定先于 SolveOne: 提前量的 k (飞时斜率) 按实际装药 — 满装药近似会让提前量按最短飞时算,
            // 低装药实弹飞时长 → 提前量不足 → 炮弹落在移动目标身后 (N 装药落点滞后事故)
            if (task.LockedCharge < 0) {
                // 实装匹配 (GC 见膛内对弹对药直接打, 不重装) → 锁膛内实际药数 (仰角必须按实际打); 否则锁 FC 解析 (GC 按它装)
                liveMatches = !task.SalvoPair && LoadoutMatches(gun, task) && gun.ChargesLive > 0;
                // 齐射: 双炮膛内都是对弹且药数相同才沿用膛内, 否则统一按解析重装 (相位同步按左炮数据走, 药数不同会打飞)
                if (!liveMatches && task.SalvoPair && GunL != null && GunR != null
                    && LoadoutMatches(GunL, task) && LoadoutMatches(GunR, task)
                    && GunL.ChargesLive == GunR.ChargesLive && GunL.ChargesLive > 0) liveMatches = true;
                task.LockedCharge = liveMatches ? gun.ChargesLive : ChargeOf(task, rawDist);
                MelonLogger.Msg($"[FC] {(isL ? "L" : "R")}: fc#{task.Id} charge locked = {task.LockedCharge}");
                // 锁定帧精化一次: 按预瞄距离跨装药档重锁. 只此一次 — 后续帧只 SolveOne, 装药不再动
                // (精化放锁定块外会被目标运动反复触发: LockedCharge 每帧改写 → k 跳变 → TRAK 中提前量突变)
                if (!liveMatches && vKm.magnitude > 0.0001f) {
                    float ld = LeadSolve(rawDist, rawAngle, vKm, aKm, jKm, task.LockedCharge).dist;
                    int refined = ChargeOf(task, ld);
                    if (refined != task.LockedCharge) task.LockedCharge = refined;
                }
            }
            // 解算统一口径 (C4): 相对方位/距离 + 提前量 (k 按锁定装药) + 交汇点, 回写 task 缓存
            var (dist, angle, aimKm, _, _, _) = SolveOne(task, target, task.LockedCharge);
            // 后续弹道用 GC 回报数据: 装填完成 (TRAK) 后膛内实际药数是唯一真值 (计划与实际不符时按实际打);
            // 装填中 ChargesLive 还是旧膛内值, 用锁存 (与 DesiredCharge 一致)
            int charge = gun.Action == GunAction.Trak && gun.ChargesLive > 0 ? gun.ChargesLive : task.LockedCharge;
            float elev = ShellData.ElevationDeg(dist, charge);
            // TRAK 按实际装药解算; 仰角超 60° (射表每段末端 = 射界上限) → 强制 DUMP:
            // 原任务回队首重派 (锁定装药提升到 MinimumCharge 能打到), 膛内平射退掉 — 旧锁定重装 = 再超界死循环.
            // 齐射: 双炮同装药同距离, 超界必双炮同时 — 双炮一起退弹, 任务 (单 task 双槽) 回队首, 双空闲后自动重挂齐射.
            // 槽位比较挡住 foreach 元组里的旧 task 引用 (首炮触发后第二炮迭代再触发的重复 DUMP)
            if (!task.Dump && (isL ? _gL.Task == task : _gR.Task == task) && gun.Action == GunAction.Trak && charge > 0 && elev > 60f) {
                int need = BallisticCalculator.MinimumCharge(dist);
                if (need > charge) {
                    task.LockedCharge = need;
                    _queue.Insert(0, task);
                    if (task.SalvoPair) {
                        _gL.Task = _gR.Task = null;
                        AttachDump(GunL!);
                        AttachDump(GunR!);
                    }
                    else {
                        if (isL) _gL.Task = null; else _gR.Task = null;
                        AttachDump(gun);
                    }
                    MelonLogger.Msg($"[FC] {(isL ? "L" : "R")}: fc#{task.Id}{(task.SalvoPair ? " SALVO" : "")} TRAK charge {charge} elev {elev:F1}° > 60 → DUMP, re-lock {need}");
                    continue;
                }
            }
            float T = ShellData.FlightTime(dist, charge); // 解算飞时按实际装药 (与 LeadSolve 同 k — 渲染 T 显示与提前量同源)
            if (isL) { _gL.SolCharge = charge; _gL.SolElev = elev; _gL.SolTask = task; _gL.SolAim = aimKm; _gL.SolV = vKm; _gL.SolA = aKm; _gL.SolJ = jKm; _gL.SolT = T; }
            else { _gR.SolCharge = charge; _gR.SolElev = elev; _gR.SolTask = task; _gR.SolAim = aimKm; _gR.SolV = vKm; _gR.SolA = aKm; _gR.SolJ = jKm; _gR.SolT = T; }
        }
    }

    /// <summary>火控解算 push (DC 渲染目标轨迹线+交汇点): 25fps 随解算循环 — 轨迹参数+交汇点+飞时;
    /// 无任务/DUMP/解算未刷 → 目标引用 null (DC 隐藏). 开火后 DC 侧冻结 (GC 击发通知), push 被忽略.</summary>
    private void PushFireSolutions() {
        if (OnFireSolution == null || MapSurfaceRef == null || NestRef == null) return;
        var nestBoard = (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position);
        float cell = GeoMap.MapCellSize;
        foreach (var (gun, task) in new[] { (GunL, _gL.Task), (GunR, _gR.Task) }) {
            if (gun == null) continue;
            bool isL = gun == GunL;
            ref var g = ref (isL ? ref _gL : ref _gR);
            Vector2 aimKm, vKm, aKm, jKm;
            float T;
            bool frozen, valid;
            // 击发真空期锁存: 第一次看到 _fire 置位 → 快照击发瞬间解算; 之后重发快照 —
            // 预瞄虚线/交汇点停在击发瞬间画面, 不跟解算滑 (炮塔跟踪仍走 ApplySolutions 最新解算, 两路分离)
            if (g.Fire && g.FrozenTask != task) {
                g.FrozenTask = task;
                g.FrozenAim = g.SolAim; g.FrozenV = g.SolV; g.FrozenA = g.SolA; g.FrozenJ = g.SolJ; g.FrozenT = g.SolT;
            }
            frozen = g.Fire && g.FrozenTask == task;
            aimKm = frozen ? g.FrozenAim : g.SolAim;
            vKm = frozen ? g.FrozenV : g.SolV;
            aKm = frozen ? g.FrozenA : g.SolA;
            jKm = frozen ? g.FrozenJ : g.SolJ;
            T = frozen ? g.FrozenT : g.SolT;
            valid = task != null && !task.Dump && (isL ? _gL.SolTask : _gR.SolTask) == task && !float.IsNaN(T)
                && vKm.magnitude > 0.0001f; // SRC 模式 (无预瞄): 目标轨迹线/交汇点不 push (DC 隐藏)
            OnFireSolution(isL ? LeftRight.Left : LeftRight.Right,
                valid ? task!.Entity?.transform : null,
                nestBoard + aimKm * cell, // 交汇点 (板面)
                vKm * cell,               // 轨迹速度 (板面/s)
                aKm * cell,               // 轨迹加速度 (板面/s²)
                jKm * cell,               // 轨迹三次项 (板面/s³, 弧线延伸)
                T);
        }
    }

    /// <summary>解算 → GC 指令 (受 Manual/Pause 门控): DesiredX 持续输出 + AzimuthSelect; 无任务清 Desired.
    /// 绿十字不归 FC — GC 全权 push (瞄准点/杀伤圈/弹种/AllReady/飞时).</summary>
    private void ApplySolutions() {
        foreach (var (gun, task) in new[] { (GunL, _gL.Task), (GunR, _gR.Task) }) {
            if (gun == null) continue;
            bool isL = gun == GunL;
            if (task == null) {
                if (gun.DesiredCharge >= 0) { gun.DesiredShell = (BulletType)(-1); gun.DesiredCharge = -1; }
                gun.DesiredDump = false; // 手动开火别被残留 DUMP 标志吃掉落点线
                continue;
            }
            // 派发当帧: 解算快照还是派发前旧任务的 → 跳过本帧, 下一帧 ComputeGunSolutions 刷完再下发
            // (否则旧 charge=0 下发, GC 装 0 包药卡死)
            if ((isL ? _gL.SolTask : _gR.SolTask) != task) continue;
            int charge = isL ? (int)_gL.SolCharge : (int)_gR.SolCharge;
            float elev = isL ? _gL.SolElev : _gR.SolElev;
            gun.DesiredShell = task.Shell;
            gun.DesiredCharge = charge;
            gun.DesiredElevation = elev;
            gun.DesiredAzimuth = task.Angle;     // C5: 方位/距离单副本 (task 缓存, SolveOne 回写)
            gun.DesiredDistance = task.Distance; // 锁定死区动态口径数据源
            gun.DesiredDump = task.Dump; // DUMP 标志: GC 击发自检据此不画落点 (平射)
        }
        // AzimuthSelect: 共享炮塔只能一门炮追 H — 两炮各打各时编号小者优先 (公平轮转);
        // 齐射同目标时双炮都追 (SameTarget 双选)
        GunControl? selected;
        if (_gL.Task == null) selected = _gR.Task != null ? GunR : null;
        else if (_gR.Task == null) selected = GunL;
        else if (SameTarget(_gL.Task, _gR.Task)) selected = null; // 双选走 SameTarget 分支
        else selected = _gL.Task.Id <= _gR.Task.Id ? GunL : GunR; // 编号小者优先 (乱序执行 = 重分配编号, 转向权随之走)
        if (GunL != null) GunL.AzimuthSelect = GunL == selected || (_gL.Task != null && _gR.Task != null && SameTarget(_gL.Task, _gR.Task));
        if (GunR != null) GunR.AzimuthSelect = GunR == selected || (_gL.Task != null && _gR.Task != null && SameTarget(_gL.Task, _gR.Task));
    }

    private static bool SameTarget(FireTask a, FireTask b) => a.Entity == b.Entity;

    /// <summary>铁巢 → 目标: 相对方位/距离 (地图局部系, GeoMap 公式).</summary>
    private (float dist, float angle) RelToTarget(Vector3 worldPos) {
        var surface = MapSurfaceRef;
        var nest = NestRef;
        if (surface == null || nest == null) return (0f, 0f);
        var nestLocal = (Vector2)surface.InverseTransformPoint(nest.position);
        var targetLocal = (Vector2)surface.InverseTransformPoint(worldPos);
        return GeoMap.RelToTarget(nestLocal, targetLocal);
    }

    /// <summary>提前量解析解 (三次轨迹曲线): 预测点 = p + v·T + ½a·T² + ⅙j·T³ (e' 在弧上, 不是切线直线),
    /// T = k·r (飞时线性), v/a/j 为地图局部系 (km/s, km/s², km/s³).
    /// 无 fireDelay 补偿: 解算每帧滑动, 出膛瞬间炮指向的就是最新解算, 按钮→出膛延迟 Δ 只是把双方同步平移,
    /// 加补偿反而把 aim 前移 Δ·v 打远.
    /// a/j≈0 走闭式一元二次; 否则数值不动点 8 次. 返回 (瞄准距离 km, 瞄准方位, 瞄准矢量 km 局部系 — 交汇点渲染用).</summary>
    private static (float dist, float angle, Vector2 aim) LeadSolve(float dist, float angle, Vector2 v, Vector2 a, Vector2 j, int charge) {
        float k = ShellData.FlightTime(1f, Mathf.Max(1, charge)); // 飞时斜率 (s/km) 按实际装药 — 满装药近似提前量不足, 移动目标落点滞后
        Vector2 dir = new(Mathf.Sin(angle * Mathf.Deg2Rad), Mathf.Cos(angle * Mathf.Deg2Rad)); // 方位 0°=+y (北) 顺时针
        Vector2 p = dir * dist;
        float r;
        Vector2 aimF;
        if (a.sqrMagnitude < 1e-10f && j.sqrMagnitude < 1e-10f) { // 匀速: 一元二次闭式
            // 方程: r = |p + k·r·v| → (1−k²v²)r² − 2k(p·v)r − p² = 0 → b = −2kpv
            // (b 符号反了会解出 r = |p − k·r·v|: 提前量被反向扣除, 炮弹落在目标身后)
            float pv = Vector2.Dot(p, v), v2 = v.sqrMagnitude;
            float qa = 1f - k * k * v2, b = -2f * k * pv, c = -p.sqrMagnitude;
            r = (-b + Mathf.Sqrt(Mathf.Max(b * b - 4f * qa * c, 0f))) / (2f * qa);
            aimF = p + k * r * v;
        }
        else { // 变速/转弯: 数值不动点 (三次轨迹曲线)
            r = p.magnitude;
            for (int i = 0; i < 8; i++) {
                float t = k * r;
                r = (p + v * t + 0.5f * a * t * t + j * (t * t * t) / 6f).magnitude;
            }
            float tf = k * r;
            aimF = p + v * tf + 0.5f * a * tf * tf + j * (tf * tf * tf) / 6f;
        }
        float d2 = aimF.magnitude;
        float a2 = Vector2.SignedAngle(aimF, Vector2.up);
        if (a2 < 0) a2 += 360f;
        return (d2, a2, aimF);
    }

    /// <summary>统一火控仲裁: FALL → 撤任务; 进 TRAK → 火控卡+五步确认 (前置, 不等稳定); 首次 AllReady → Arm (一次性);
    /// 齐射走独立仲裁; AutoFire/预定时间 → 击发; PreAiming → 玩家击发后 Fired 收尾.</summary>
    private void FireArbiter() {
        // 齐射: 双炮同任务, 单独仲裁 (两炮都跟稳才确认, 同时解保险, 一次开火)
        if (_gL.Task != null && _gR.Task != null && _gL.Task == _gR.Task && _gL.Task.SalvoPair) {
            SalvoArbiter();
            return;
        }
        foreach (var (gun, task, armed, side) in new[] {
                     (GunL, _gL.Task, _gL.Armed, LeftRight.Left),
                     (GunR, _gR.Task, _gR.Armed, LeftRight.Right) }) {
            if (gun == null || task == null) {
                if (side == LeftRight.Left) { _gL.Armed = false; _gL.Confirmed = false; } else { _gR.Armed = false; _gR.Confirmed = false; }
                continue;
            }
            if (gun.Action == GunAction.Fall) { // GC 自报故障: 撤任务 (骨架; 换炮策略后续)
                MelonLogger.Error($"[FC] {side}: gun FALL, cancel fc#{task.Id}");
                RequestCancel(task);
                continue;
            }
            // GC 进 1-3 DUMP 相位停手 (膛内弹不对/齐射多药): 接管 — 撤任务改派 DUMP 占位 (重派走 Dispatch 三态)
            if (gun.Action == GunAction.Dump && gun.DumpWaitActive) {
                MelonLogger.Warning($"[FC] {side}: GC in 1-3 DUMP (chamber '{gun.ChamberLive}'), takeover fc#{task.Id}");
                RequestCancel(task);
                continue;
            }
            if (task.Dump) { // DUMP 占位: 跳过火控卡/五步确认 — AllReady (仰角 0 平射稳定) → 解保险 → 强制击发 (不看 AutoFire/PreAiming)
                if (!armed && gun.AllReady && gun.Action == GunAction.Trak) {
                    StartCoroutineHost(ArmRoutine(gun, side));
                    if (side == LeftRight.Left) _gL.Armed = true; else _gR.Armed = true;
                    continue;
                }
                if (!armed || !gun.AllReady || gun.Action != GunAction.Trak) continue;
                if (side == LeftRight.Left ? _gL.Fire : _gR.Fire) continue;
                StartCoroutineHost(FireRoutine(gun, side, task));
                if (side == LeftRight.Left) _gL.Fire = true; else _gR.Fire = true;
                return;
            }
            // 确认前置: 进 TRAK (弹药已装好, 瞄准开始) 即出火控卡+五步确认 — 不等稳定,
            // AllReady 后只剩解保险+击发; 完成才置位 (不靠锁排队), 保证 确认 → 保险 → 击发 顺序
            bool confirmed = side == LeftRight.Left ? _gL.Confirmed : _gR.Confirmed;
            bool confirming = side == LeftRight.Left ? _gL.Confirming : _gR.Confirming;
            if (!confirmed && !confirming && gun.Action == GunAction.Trak) {
                if (side == LeftRight.Left) _gL.Confirming = true; else _gR.Confirming = true;
                StartCoroutineHost(ConfirmRoutine(gun, side, task));
            }
            if (!armed && confirmed && gun.AllReady && gun.AzimuthSelect && gun.Action == GunAction.Trak) {
                StartCoroutineHost(ArmRoutine(gun, side)); // 套上即解保险 (一次性; 卡+确认已前置)
                if (side == LeftRight.Left) _gL.Armed = true; else _gR.Armed = true;
                continue;
            }
            if (!armed || !gun.AllReady || !gun.AzimuthSelect) continue; // 不稳回退: 不开火 (已 Arm 不自动回保险; 已击发由 _fire 锁拦)
            if (side == LeftRight.Left ? _gL.Fire : _gR.Fire) continue; // 击发已按 (等触发核心击发 + 收尾)
            if (Mode == FireMode.PreAiming) {
                if (gun.Fired) { // 玩家击发 (GC HasFired 沿 — 真出膛; 不用 FlyRemaining: 装填期炮表残留值会骗过早收尾) → 收尾; 置位冻结解算 push (见 PushFireSolutions)
                    if (side == LeftRight.Left) _gL.Fire = true; else _gR.Fire = true;
                    StartCoroutineHost(FinishRoutine(gun, side, task));
                }
                continue;
            }
            // 预定打击时间: 当前任务时钟 + FlyTime ≥ 预定 → 开火; -1 = 就绪即打
            if (task.PlannedStrikeTime > 0f && !float.IsNaN(gun.FlyTime)) {
                float now = MissionClock.Seconds;
                if (float.IsNaN(now) || now + gun.FlyTime < task.PlannedStrikeTime) continue;
            }
            StartCoroutineHost(FireRoutine(gun, side, task));
            if (side == LeftRight.Left) _gL.Fire = true; else _gR.Fire = true;
            return; // 一击发后本帧不再仲裁另一炮
        }
    }

    private bool _salvoArming; // 齐射解保险进行中 (只启动一次)
    private bool _salvoConfirming, _salvoConfirmed; // 齐射火控卡+五步确认前置: 进行中/已完成

    /// <summary>齐射仲裁: 双炮进 TRAK → 卡+五步确认 (前置); 双炮都 AllReady → 同时解保险 → 一次开火; 任一不稳/已击发 → 不动.</summary>
    private void SalvoArbiter() {
        var task = _gL.Task!;
        if (GunL == null || GunR == null) return;
        if (GunL.Action == GunAction.Fall || GunR.Action == GunAction.Fall) {
            MelonLogger.Error($"[FC] salvo: gun FALL, cancel fc#{task.Id}");
            RequestCancel(task);
            return;
        }
        // 齐射中任一炮进 1-3 (多药): 接管 — 撤齐射, 重派走 Dispatch (有弹炮 DUMP 清膛后重派齐射)
        if ((GunL.Action == GunAction.Dump && GunL.DumpWaitActive) || (GunR.Action == GunAction.Dump && GunR.DumpWaitActive)) {
            MelonLogger.Warning($"[FC] salvo: GC in 1-3 DUMP, cancel fc#{task.Id} (takeover)");
            RequestCancel(task);
            return;
        }
        // AllReady 只在 TRAK 里是活值 (装填期残留旧任务的 true); 必须双炮都在 TRAK 才算跟稳
        bool bothTrak = GunL.Action == GunAction.Trak && GunR.Action == GunAction.Trak;
        bool bothReady = GunL.AllReady && GunR.AllReady && GunL.AzimuthSelect && GunR.AzimuthSelect && bothTrak;
        // 确认前置: 双炮都进 TRAK 即出卡+五步 (瞄准过程中, 不等稳定)
        if (!_salvoConfirmed && !_salvoConfirming && bothTrak) {
            _salvoConfirming = true;
            StartCoroutineHost(SalvoConfirmRoutine(task));
        }
        if (!_gL.Armed || !_gR.Armed) {
            if (!bothReady || _salvoArming || !_salvoConfirmed) return; // 等两个都跟稳 (不能等一个); 确认没跑完不解保险 (防抢锁乱序)
            _salvoArming = true;
            StartCoroutineHost(SalvoArmRoutine(task));
            return;
        }
        if (!bothReady || _gL.Fire || _gR.Fire) return; // armed 后不稳/已按 → 等收尾 (已击发由 _fire 锁拦)
        if (Mode == FireMode.PreAiming) {
            if ((GunL != null && GunL.Fired) || (GunR != null && GunR.Fired))
                StartCoroutineHost(FinishRoutine(GunL!, LeftRight.Left, task)); // 玩家击发 (任一炮 GC HasFired 沿 — 真出膛) → 收尾
            return;
        }
        // 预定打击时间 (与单发同口径, 按主炮左炮飞时)
        if (task.PlannedStrikeTime > 0f && !float.IsNaN(GunL.FlyTime)) {
            float now = MissionClock.Seconds;
            if (float.IsNaN(now) || now + GunL.FlyTime < task.PlannedStrikeTime) return;
        }
        StartCoroutineHost(FireRoutine(GunL, LeftRight.Left, task));
        _gL.Fire = _gR.Fire = true; // 齐射一击发锁双炮 (触发核心击发前防连点)
    }

    /// <summary>齐射确认前置: 双炮进 TRAK 即出火控卡+五步确认 (瞄准过程中, 不等稳定); 完成才置位, 解保险靠 _salvoConfirmed 门.</summary>
    private IEnumerator SalvoConfirmRoutine(FireTask task) {
        try {
            if (FireLock == null || ConsolePort == null) yield break;
            yield return FireLock.Acquire();
            try {
                // 计算台不重设 (装填期 PWDR 前已 Calculate 一次; 五步确认按 mod 缓存值走)
                yield return ConsolePort.ConfirmTask();
                yield return ConsolePort.ConfirmBullet();
                yield return ConsolePort.ConfirmRotation();
                yield return ConsolePort.ConfirmElevation();
                yield return ConsolePort.ReadyToFire();
                // 任务可能在确认途中被撤 (RequestCancel 已清 confirmed): 只有任务还在炮上才置位, 防残留
                if (_gL.Task == task || _gR.Task == task) {
                    _salvoConfirmed = true;
                    MelonLogger.Msg($"[FC] salvo: confirmed fc#{task.Id}");
                }
            }
            finally { FireLock?.Release(); }
        }
        finally { _salvoConfirming = false; }
    }

    /// <summary>齐射解保险: 两炮保险同时解除 (ArmBoth, 不许一先一后); 任务还在炮上才置 armed.</summary>
    private IEnumerator SalvoArmRoutine(FireTask task) {
        try {
            if (FireLock == null || ConsolePort == null) yield break;
            yield return FireLock.Acquire();
            try {
                yield return ConsolePort.ArmBoth();
                if (_gL.Task == task || _gR.Task == task) {
                    _gL.Armed = _gR.Armed = true;
                    MelonLogger.Msg($"[FC] salvo: armed fc#{task.Id}");
                }
            }
            finally { FireLock?.Release(); }
        }
        finally { _salvoArming = false; }
    }

    /// <summary>单发确认前置: 进 TRAK 即出火控卡+五步确认 (瞄准过程中, 不等稳定); 完成才置位, 解保险靠 confirmed 门.</summary>
    private IEnumerator ConfirmRoutine(GunControl gun, LeftRight side, FireTask task) {
        try {
            if (FireLock == null || ConsolePort == null) yield break;
            yield return FireLock.Acquire();
            try {
                // 计算台不再重设 (装填期 PWDR 前已 Calculate 一次 — 读数刷新 + 少一张记事本卡;
                // 五步确认按 mod 缓存值走, 不惯着游戏重新算)
                yield return ConsolePort.ConfirmTask();
                yield return ConsolePort.ConfirmBullet();
                yield return ConsolePort.ConfirmRotation();
                yield return ConsolePort.ConfirmElevation();
                yield return ConsolePort.ReadyToFire();
                // 任务可能在确认途中被撤 (RequestCancel 已清 confirmed): 只有任务还在炮上才置位, 防残留
                if ((side == LeftRight.Left ? _gL.Task : _gR.Task) == task) {
                    if (side == LeftRight.Left) _gL.Confirmed = true; else _gR.Confirmed = true;
                    MelonLogger.Msg($"[FC] {side}: confirmed fc#{task.Id}");
                }
            }
            finally { FireLock?.Release(); }
        }
        finally {
            if (side == LeftRight.Left) _gL.Confirming = false; else _gR.Confirming = false;
        }
    }

    /// <summary>解保险 (短锁内, 卡+确认已前置).</summary>
    private IEnumerator ArmRoutine(GunControl gun, LeftRight side) {
        if (FireLock == null || ConsolePort == null) yield break;
        yield return FireLock.Acquire();
        try {
            yield return ConsolePort.Arm(side);
            MelonLogger.Msg($"[FC] {side}: armed fc#{(side == LeftRight.Left ? _gL.Task : _gR.Task)?.Id}");
        }
        finally { FireLock?.Release(); }
    }

    /// <summary>击发 (统一火控短锁) + 收尾.</summary>
    private IEnumerator FireRoutine(GunControl gun, LeftRight side, FireTask task) {
        if (FireLock == null) yield break;
        yield return FireLock.Acquire();
        try {
            // 开火对账 (定位"打歪"用): 设定值 vs 实际指向 + 解算交汇点/飞时 (落点与目标轨迹对账)
            bool isL = side == LeftRight.Left;
            var aimKm = isL ? _gL.SolAim : _gR.SolAim;
            float T = isL ? _gL.SolT : _gR.SolT;
            MelonLogger.Msg($"[FC] {side}: FIRE pressed E: desired={gun.DesiredElevation:F2} actual={gun.Elevation:F2} | A: desired={gun.DesiredAzimuth:F1} actual={gun.Azimuth:F1} | aim=({aimKm.x:F2},{aimKm.y:F2})km T={T:F2}s");
            FcsBus.Fire?.Invoke(); // 击发钮全局一个: 齐射一按两炮齐
        }
        finally { FireLock.Release(); }
        yield return FinishRoutine(gun, side, task);
    }

    /// <summary>等击发确认 (GC HasFired 沿 = 真出膛; 齐射等两炮) → 收尾: 完成队列 + 槽位释放.
    /// 不用 FlyRemaining 作判据 — 装填期炮表残留值会骗过早收尾 (收尾时 CurrentFlight 还是旧发, 完成队列绑错 Flight).
    /// 哑炮防护: 5s 无击发沿 = 哑炮 → 重新解保险 + 再击发 (最多 3 次击发); 全哑 → 报错撤任务
    /// (膛内哑弹由下一任务自然消耗 — DecidePrepStep 见膛内弹对就直接用, 系统自愈).
    /// 落点指示不归 FC — GC 常驻循环自检击发 (pendingReload 上升沿) 锁存+通知 DC, 手动开火同样出轨迹.</summary>
    private IEnumerator FinishRoutine(GunControl gun, LeftRight side, FireTask task) {
        bool confirmed = false;
        for (int attempt = 0; attempt < 3; attempt++) {
            // 击发确认等待: GC HasFired 沿 (齐射 = 两炮都有)
            float waited = 0f;
            while (waited < 5f) {
                bool ok = task.SalvoPair
                    ? GunL != null && GunL.Fired && GunR != null && GunR.Fired
                    : gun.Fired;
                if (ok) { confirmed = true; break; }
                yield return new WaitForSeconds(0.1f);
                waited += 0.1f;
            }
            if (confirmed) break;
            if (attempt >= 2) break;
            // 哑炮 = 保险没按下去就拉了绳 (无效); 重试 = 再按保险 + 再拉绳 (按下一侧保险 → 拉绳 → 对应侧激发)
            MelonLogger.Warning($"[FC] {side}: misfire (5s no countdown), re-arm + fire retry {attempt + 1}/2");
            if (task.SalvoPair) {
                if (FireLock == null || ConsolePort == null) break;
                yield return FireLock.Acquire();
                try { yield return ConsolePort.ArmBoth(); } // 齐射重试: 双炮保险同时再按 (不许一先一后)
                finally { FireLock.Release(); }
            }
            else yield return ArmRoutine(gun, side);
            if (FireLock == null) break;
            yield return FireLock.Acquire();
            try { FcsBus.Fire?.Invoke(); } // 拉绳: 已响的炮没弹不会再响, 只哑炮激发
            finally { FireLock.Release(); }
        }
        if (!confirmed) {
            MelonLogger.Error($"[FC] {side}: gun did not fire after 3 attempts, cancel fc#{task.Id}");
            if (task.Dump) { // DUMP 哑弹 (膛内弹打不掉): 挂起该炮防 DUMP 死循环, 交 Manual 人工处理
                if (side == LeftRight.Left) _gL.DumpStuck = true; else _gR.DumpStuck = true;
                MelonLogger.Error($"[FC] {side}: DUMP misfire — gun suspended until Manual/Stop");
            }
            RequestCancel(task);
            yield break;
        }
        FinishTask(side, task, gun);
        if (task.SalvoPair) FinishTask(side == LeftRight.Left ? LeftRight.Right : LeftRight.Left, task, side == LeftRight.Left ? GunR : GunL);
    }

    private void FinishTask(LeftRight side, FireTask task, GunControl? gun) {
        if ((side == LeftRight.Left ? _gL.Task : _gR.Task) != task) return; // 已收尾过
        if (gun == null) return;
        task.LockedCharge = -1; // 装药冻结随任务生命周期走
        if (!task.Dump) { // DUMP 占位不进完成队列 (内部退弹不是打击任务)
            // FireMission = Flight 记的出膛时刻任务时钟 — 炮表倒计时启动晚 ~1s, 用启动时刻会让抵达时刻系统性偏晚 (传导时间问题)
            var flight = gun.CurrentFlight;
            float firedAt = flight != null && !float.IsNaN(flight.FiredAtMission) ? flight.FiredAtMission : MissionClock.Seconds;
            _finished.Add(new FinishedEntry { Task = task, Fly = gun.FlyTime, FireMission = firedAt, Flight = flight });
            if (_finished.Count > 8) _finished.RemoveAt(0);
        }
        MelonLogger.Msg($"[FC] {side}: {(task.Dump ? "DUMP done (chamber cleared)" : $"finished fc#{task.Id} (fly={gun.FlyTime:F2}s)")}");
        if (side == LeftRight.Left) _gL.Task = null; else _gR.Task = null;
        _gL.Armed = _gR.Armed = false;
        _gL.Confirmed = _gR.Confirmed = _salvoConfirmed = false;
        _gL.Fire = _gR.Fire = false;
        if (GunL != null && _gL.Task == null) { GunL.DesiredShell = (BulletType)(-1); GunL.DesiredCharge = -1; }
        if (GunR != null && _gR.Task == null) { GunR.DesiredShell = (BulletType)(-1); GunR.DesiredCharge = -1; }
        ClearSyncIfAlone();
    }

    /// <summary>完成队列条目 (HUD Finish Queue 用): 抵达时刻 = FireMission + Fly;
    /// 剩余/落地直读 Flight 字段 (统一口径 — HUD 侧无判据, 只显示).</summary>
    public class FinishedEntry {
        public FireTask Task = null!;
        public float Fly;
        public float FireMission;
        public Flight? Flight;
    }

    private readonly List<FinishedEntry> _finished = new();
    public IReadOnlyList<FinishedEntry> Finished => _finished;

    private void StartCoroutineHost(IEnumerator it) {
        try { MelonCoroutines.Start(it); } catch (Exception ex) { MelonLogger.Error($"[FC] coroutine start failed: {ex.Message}"); }
    }
}
