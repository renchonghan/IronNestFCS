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

/// <summary>火控任务 (2.0): 位置源由 DC 提供 (每帧可查询; null = 失效 → 撤任务).</summary>
public class FireTask {
    public int Id;
    public string Name = "";
    public GameObject? Entity;                             // 绑定实体/令牌 (渲染绑定用)
    public System.Func<Vector3?>? PositionSource; // 世界位置 (实体/令牌每帧刷新), null = 位置源失效
    public System.Func<Vector2>? VelocitySource; // TWS 速度矢量 (km/s, 地图局部系), null = 无预瞄直瞄
    public BulletType Shell;
    public ChargeMode Mode = ChargeMode.Normal;
    public float PlannedStrikeTime = -1f; // 预定打击时间 (任务时钟秒, -1 = 就绪即打)
    public int Priority;
    public float Angle;      // 最新解算方位 (HUD 显示缓存)
    public float Distance;   // 最新解算距离 (HUD 显示缓存)
    public bool SalvoPair;   // 齐射对标志: 派发时两炮同任务 + SyncCommand
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
    public BallisticCalculator? Calculator;   // 解算台 (Calculate 保留: 出火控卡仪式感)
    public CoroutineLock? FireLock;           // 统一火控短锁 (与 GC 的 DUMP 平射共享)
    public Transform? NestRef;                // 铁巢/炮塔参考 (相对方位计算用)
    public Transform? MapSurfaceRef;          // "Draggable Surface" (局部系换算用)
    /// <summary>队列显示 push (DC 渲染线程): 打击队列指示器数据 (FC 自引用传出).</summary>
    public System.Action<FireControl>? OnQueueChanged;

    // ===== 请求入口 (DC 调用; 显控台舰长席自己排布, FC 只接收) =====
    private readonly List<FireTask> _requests = new();   // 待处理请求 (入队/取消)
    private readonly List<FireTask> _queue = new();      // 任务队列
    private FireTask? _taskL;
    private FireTask? _taskR;
    private int _fcCounter;

    // ===== 状态 =====
    public FireMode Mode { get; private set; } = FireMode.Manual;
    public int QueueCount => _queue.Count;
    public IReadOnlyList<FireTask> Queue => _queue;
    public FireTask? LeftTask => _taskL;
    public FireTask? RightTask => _taskR;
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
    private bool _armedL;
    private bool _armedR;
    private bool _fireL, _fireR; // 击发已按 (触发核心有 fireDelay, Fired latch 前防连点)
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
        _taskL = _taskR = null;
        _armedL = _armedR = false;
        _confirmedL = _confirmedR = _salvoConfirmed = false;
        Mode = FireMode.Manual;
    }

    // ===== DC 调用: 请求入口 =====
    public void RequestTask(FireTask task) { task.Id = ++_fcCounter; _requests.Add(task); }

    /// <summary>任务是否已上炮 (上炮任务右键 = 取消, 不许改计划, 1.x 口径).</summary>
    public bool IsOnGun(FireTask task) => task == _taskL || task == _taskR;

    /// <summary>按实体找任务 (队列 + 在炮).</summary>
    public FireTask? FindEntityTask(GameObject entity) {
        foreach (var t in _queue) if (t.Entity == entity) return t;
        if (_taskL?.Entity == entity) return _taskL;
        if (_taskR?.Entity == entity) return _taskR;
        return null;
    }
    public void RequestCancel(FireTask task) {
        _requests.Remove(task);
        _queue.Remove(task);
        if (_taskL == task) { _taskL = null; _armedL = false; _confirmedL = false; _fireL = false; if (GunL != null) { GunL.DesiredShell = (BulletType)(-1); GunL.DesiredCharge = -1; } }
        if (_taskR == task) { _taskR = null; _armedR = false; _confirmedR = false; _fireR = false; if (GunR != null) { GunR.DesiredShell = (BulletType)(-1); GunR.DesiredCharge = -1; } }
        if (_taskL == null && _taskR == null) _salvoConfirmed = false;
        ClearSyncIfAlone();
    }

    /// <summary>Stop: 清队列 + 撤任务 (线程常驻不死, 只是改指令内容).</summary>
    public void StopTasks() {
        _queue.Clear();
        _requests.Clear();
        _finished.Clear();
        _taskL = _taskR = null;
        _armedL = _armedR = false;
        _confirmedL = _confirmedR = _salvoConfirmed = false;
        _fireL = _fireR = false;
        if (GunL != null) { GunL.DesiredShell = (BulletType)(-1); GunL.DesiredCharge = -1; }
        if (GunR != null) { GunR.DesiredShell = (BulletType)(-1); GunR.DesiredCharge = -1; }
        ClearSyncIfAlone();
    }

    /// <summary>模式推导 (DC 只传 AutoFire + AutoTask 开关位): 扫荡=FullAuto 强制自动开火; AF=Semi; AF off=PreAiming; ManualControl=Manual.</summary>
    public void SetManual(bool manual) {
        _manual = manual;
        RederiveMode();
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

    /// <summary>队列任务坐标解算 (显示用): 相对方位/距离 + 提前量, 不碰硬件不派发.</summary>
    private void UpdateQueueSolutions() {
        foreach (var t in _queue) {
            if (t == _taskL || t == _taskR) continue; // 在炮任务由 UpdateFireSolutions 解
            var pos = t.PositionSource?.Invoke();
            if (pos == null) continue;
            var (dist, angle) = RelToTarget(pos.Value);
            var vel = t.VelocitySource?.Invoke();
            if (vel != null && vel.Value.magnitude > 0.0001f) {
                var (ld, la) = LeadSolve(dist, angle, vel.Value);
                dist = ld;
                angle = la;
            }
            t.Angle = angle;
            t.Distance = dist;
        }
    }

    private void ProcessRequests() {
        foreach (var r in _requests) {
            if (!_queue.Contains(r)) _queue.Add(r);
        }
        _requests.Clear();
    }

    /// <summary>智能派发 (骨架): 齐射对要求两炮都空 (同任务挂两槽 + SyncCommand); 队首任务按实装匹配优先挑炮
    /// (能不 DUMP 就不 DUMP), 空闲才派; 乱序/前瞻后续版本.</summary>
    private void Dispatch() {
        while (_queue.Count > 0) {
            var head = _queue[0];
            if (head.SalvoPair) { // 齐射: 等两炮都回 WAIT 才挂双槽 (同任务 + SyncCommand)
                if (_taskL != null || _taskR != null || GunL == null || GunR == null) break;
                if (GunL.Action != GunAction.Idle || GunR.Action != GunAction.Idle) break;
                _queue.RemoveAt(0);
                _taskL = _taskR = head;
                ClearSyncIfAlone();
                MelonLogger.Msg($"[FC] dispatch SALVO fc#{head.Id} {head.Shell}");
                continue;
            }
            GunControl? freeL = GunL != null && _taskL == null ? GunL : null;
            GunControl? freeR = GunR != null && _taskR == null ? GunR : null;
            if (freeL == null && freeR == null) break;
            FireTask? best = null;
            GunControl? bestGun = null;
            foreach (var t in _queue) {
                if (freeL != null && LoadoutMatches(freeL, t)) { best = t; bestGun = freeL; break; }
                if (freeR != null && LoadoutMatches(freeR, t)) { best = t; bestGun = freeR; break; }
            }
            if (best == null) { best = _queue[0]; bestGun = freeL ?? freeR; }
            _queue.Remove(best);
            if (bestGun == freeL && freeL != null) { _taskL = best; }
            else if (bestGun == freeR && freeR != null) { _taskR = best; }
            MelonLogger.Msg($"[FC] dispatch fc#{best.Id} {best.Shell} → {(bestGun == freeL ? "L" : "R")}");
        }
    }

    /// <summary>SyncCommand 随齐射对置位/复位 (GC 相位级同步).</summary>
    private void ClearSyncIfAlone() {
        bool salvo = (_taskL?.SalvoPair ?? false) || (_taskR?.SalvoPair ?? false);
        if (GunL != null) GunL.SyncCommand = salvo;
        if (GunR != null) GunR.SyncCommand = salvo;
    }

    private static bool LoadoutMatches(GunControl gun, FireTask t) {
        return gun.Chamber == t.Shell.ToString() && gun.Charges >= ChargeOf(t, 0f);
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

    private float _solChargeL, _solChargeR, _solElevL, _solElevR, _solAngleL, _solAngleR, _solDistL, _solDistR;
    private FireTask? _solTaskL, _solTaskR; // 解算快照对应的任务 (派发当帧解算未刷, 应用层据此跳过)
    /// <summary>解算快照 (HUD 第二行显示用): 仰角/装药, 无任务 NaN/-1.</summary>
    public float SolElevL => _taskL != null ? _solElevL : float.NaN;
    public float SolElevR => _taskR != null ? _solElevR : float.NaN;
    public int SolChargeL => _taskL != null ? (int)_solChargeL : -1;
    public int SolChargeR => _taskR != null ? (int)_solChargeR : -1;

    /// <summary>火控解析 (与炮无关, 任何状态持续刷新): 位置源 → 方位/距离 (含提前量) → 装药/仰角. 只写任务缓存与本地变量, 不碰 GC.</summary>
    private void ComputeGunSolutions() {
        foreach (var (gun, task) in new[] { (GunL, _taskL), (GunR, _taskR) }) {
            if (gun == null) continue;
            bool isL = gun == GunL;
            if (task == null) {
                if (isL) { _solDistL = 0f; _solChargeL = -1; _solElevL = float.NaN; _solAngleL = float.NaN; _solTaskL = null; }
                else { _solDistR = 0f; _solChargeR = -1; _solElevR = float.NaN; _solAngleR = float.NaN; _solTaskR = null; }
                continue;
            }
            var pos = task.PositionSource?.Invoke();
            if (pos == null) { RequestCancel(task); continue; } // 位置源失效 (阵亡/令牌离图) → 撤任务
            var (dist, angle) = RelToTarget(pos.Value);
            var vel = task.VelocitySource?.Invoke();
            if (vel != null && vel.Value.magnitude > 0.0001f) {
                var (ld, la) = LeadSolve(dist, angle, vel.Value); // 提前量解析解 (飞时线性 → 一元二次)
                dist = ld; angle = la;
            }
            int charge = ChargeOf(task, dist);
            task.Angle = angle;
            task.Distance = dist;
            if (isL) { _solChargeL = charge; _solElevL = ShellData.ElevationDeg(dist, charge); _solAngleL = angle; _solDistL = dist; _solTaskL = task; }
            else { _solChargeR = charge; _solElevR = ShellData.ElevationDeg(dist, charge); _solAngleR = angle; _solDistR = dist; _solTaskR = task; }
        }
    }

    /// <summary>解算 → GC 指令 (受 Manual/Pause 门控): DesiredX 持续输出 + AzimuthSelect; 无任务清 Desired.
    /// 绿十字不归 FC — GC 全权 push (瞄准点/杀伤圈/弹种/AllReady/飞时).</summary>
    private void ApplySolutions() {
        foreach (var (gun, task) in new[] { (GunL, _taskL), (GunR, _taskR) }) {
            if (gun == null) continue;
            bool isL = gun == GunL;
            if (task == null) {
                if (gun.DesiredCharge >= 0) { gun.DesiredShell = (BulletType)(-1); gun.DesiredCharge = -1; }
                continue;
            }
            // 派发当帧: 解算快照还是派发前旧任务的 → 跳过本帧, 下一帧 ComputeGunSolutions 刷完再下发
            // (否则旧 charge=0 下发, GC 装 0 包药卡死)
            if ((isL ? _solTaskL : _solTaskR) != task) continue;
            int charge = isL ? (int)_solChargeL : (int)_solChargeR;
            float elev = isL ? _solElevL : _solElevR;
            float angle = isL ? _solAngleL : _solAngleR;
            gun.DesiredShell = task.Shell;
            gun.DesiredCharge = charge;
            gun.DesiredElevation = elev;
            gun.DesiredAzimuth = angle;
            gun.DesiredDistance = isL ? _solDistL : _solDistR; // 锁定死区动态口径数据源
        }
        // AzimuthSelect: 共享炮塔只能一门炮追 H — 两炮各打各时编号小者优先 (公平轮转);
        // 齐射同目标时双炮都追 (SameTarget 双选)
        GunControl? selected;
        if (_taskL == null) selected = _taskR != null ? GunR : null;
        else if (_taskR == null) selected = GunL;
        else if (SameTarget(_taskL, _taskR)) selected = null; // 双选走 SameTarget 分支
        else selected = _taskL.Id <= _taskR.Id ? GunL : GunR; // 编号小者优先 (乱序执行 = 重分配编号, 转向权随之走)
        if (GunL != null) GunL.AzimuthSelect = GunL == selected || (_taskL != null && _taskR != null && SameTarget(_taskL, _taskR));
        if (GunR != null) GunR.AzimuthSelect = GunR == selected || (_taskL != null && _taskR != null && SameTarget(_taskL, _taskR));
    }

    private static bool SameTarget(FireTask a, FireTask b) => a.PositionSource == b.PositionSource;

    /// <summary>铁巢 → 目标: 相对方位/距离 (地图局部系, GeoMap 公式).</summary>
    private (float dist, float angle) RelToTarget(Vector3 worldPos) {
        var surface = MapSurfaceRef;
        var nest = NestRef;
        if (surface == null || nest == null) return (0f, 0f);
        var nestLocal = (Vector2)surface.InverseTransformPoint(nest.position);
        var targetLocal = (Vector2)surface.InverseTransformPoint(worldPos);
        return GeoMap.RelToTarget(nestLocal, targetLocal);
    }

    /// <summary>提前量解析解: 飞时 T = k·d (线性), 不动点 → 一元二次闭式解. v 为地图局部系速度 (km/s).</summary>
    private static (float dist, float angle) LeadSolve(float dist, float angle, Vector2 v) {
        float k = ShellData.FlightTime(1f, 6); // 飞时斜率 (s/km) 与装药弱相关, 骨架先按满装药取
        Vector2 dir = new(Mathf.Sin(angle * Mathf.Deg2Rad), Mathf.Cos(angle * Mathf.Deg2Rad)); // 方位 0°=+y (北) 顺时针
        Vector2 p = dir * dist;
        float pv = Vector2.Dot(p, v), v2 = v.sqrMagnitude;
        float a = 1f - k * k * v2, b = 2f * k * pv, c = -p.sqrMagnitude;
        float r = (-b + Mathf.Sqrt(Mathf.Max(b * b - 4f * a * c, 0f))) / (2f * a);
        Vector2 aim = p + k * r * v;
        float d2 = aim.magnitude;
        float a2 = Vector2.SignedAngle(aim, Vector2.up);
        if (a2 < 0) a2 += 360f;
        return (d2, a2);
    }

    /// <summary>统一火控仲裁: FALL → 撤任务; 进 TRAK → 火控卡+五步确认 (前置, 不等稳定); 首次 AllReady → Arm (一次性);
    /// 齐射走独立仲裁; AutoFire/预定时间 → 击发; PreAiming → 玩家击发后 Fired 收尾.</summary>
    private void FireArbiter() {
        // 齐射: 双炮同任务, 单独仲裁 (两炮都跟稳才确认, 同时解保险, 一次开火)
        if (_taskL != null && _taskR != null && _taskL == _taskR && _taskL.SalvoPair) {
            SalvoArbiter();
            return;
        }
        foreach (var (gun, task, armed, side) in new[] {
                     (GunL, _taskL, _armedL, LeftRight.Left),
                     (GunR, _taskR, _armedR, LeftRight.Right) }) {
            if (gun == null || task == null) {
                if (side == LeftRight.Left) { _armedL = false; _confirmedL = false; } else { _armedR = false; _confirmedR = false; }
                continue;
            }
            if (gun.Action == GunAction.Fall) { // GC 自报故障: 撤任务 (骨架; 换炮策略后续)
                MelonLogger.Error($"[FC] {side}: gun FALL, cancel fc#{task.Id}");
                RequestCancel(task);
                continue;
            }
            // 确认前置: 进 TRAK (弹药已装好, 瞄准开始) 即出火控卡+五步确认 — 不等稳定,
            // AllReady 后只剩解保险+击发; 完成才置位 (不靠锁排队), 保证 确认 → 保险 → 击发 顺序
            bool confirmed = side == LeftRight.Left ? _confirmedL : _confirmedR;
            bool confirming = side == LeftRight.Left ? _confirmingL : _confirmingR;
            if (!confirmed && !confirming && gun.Action == GunAction.Trak) {
                if (side == LeftRight.Left) _confirmingL = true; else _confirmingR = true;
                StartCoroutineHost(ConfirmRoutine(gun, side, task));
            }
            if (!armed && confirmed && gun.AllReady && gun.AzimuthSelect && gun.Action == GunAction.Trak) {
                StartCoroutineHost(ArmRoutine(gun, side)); // 套上即解保险 (一次性; 卡+确认已前置)
                if (side == LeftRight.Left) _armedL = true; else _armedR = true;
                continue;
            }
            if (!armed || !gun.AllReady || !gun.AzimuthSelect) continue; // 不稳回退: 不开火 (已 Arm 不自动回保险; 已击发由 _fire 锁拦)
            if (side == LeftRight.Left ? _fireL : _fireR) continue; // 击发已按 (等触发核心击发 + 收尾)
            if (Mode == FireMode.PreAiming) {
                if (!float.IsNaN(gun.FlyRemaining)) StartCoroutineHost(FinishRoutine(gun, side, task)); // 玩家击发 (炮表倒计时启动) → 收尾
                continue;
            }
            // 预定打击时间: 当前任务时钟 + FlyTime ≥ 预定 → 开火; -1 = 就绪即打
            if (task.PlannedStrikeTime > 0f && !float.IsNaN(gun.FlyTime)) {
                float now = MissionClock.Seconds;
                if (float.IsNaN(now) || now + gun.FlyTime < task.PlannedStrikeTime) continue;
            }
            StartCoroutineHost(FireRoutine(gun, side, task));
            if (side == LeftRight.Left) _fireL = true; else _fireR = true;
            return; // 一击发后本帧不再仲裁另一炮
        }
    }

    private bool _salvoArming; // 齐射解保险进行中 (只启动一次)
    private bool _salvoConfirming, _salvoConfirmed; // 齐射火控卡+五步确认前置: 进行中/已完成
    private bool _confirmingL, _confirmedL;          // 左炮确认前置
    private bool _confirmingR, _confirmedR;          // 右炮确认前置

    /// <summary>齐射仲裁: 双炮进 TRAK → 卡+五步确认 (前置); 双炮都 AllReady → 同时解保险 → 一次开火; 任一不稳/已击发 → 不动.</summary>
    private void SalvoArbiter() {
        var task = _taskL!;
        if (GunL == null || GunR == null) return;
        if (GunL.Action == GunAction.Fall || GunR.Action == GunAction.Fall) {
            MelonLogger.Error($"[FC] salvo: gun FALL, cancel fc#{task.Id}");
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
        if (!_armedL || !_armedR) {
            if (!bothReady || _salvoArming || !_salvoConfirmed) return; // 等两个都跟稳 (不能等一个); 确认没跑完不解保险 (防抢锁乱序)
            _salvoArming = true;
            StartCoroutineHost(SalvoArmRoutine(task));
            return;
        }
        if (!bothReady || _fireL || _fireR) return; // armed 后不稳/已按 → 等收尾 (已击发由 _fire 锁拦)
        if (Mode == FireMode.PreAiming) {
            if (!float.IsNaN(GunL?.FlyRemaining ?? float.NaN) || !float.IsNaN(GunR?.FlyRemaining ?? float.NaN))
                StartCoroutineHost(FinishRoutine(GunL, LeftRight.Left, task)); // 玩家击发 (任一炮表倒计时启动) → 收尾
            return;
        }
        // 预定打击时间 (与单发同口径, 按主炮左炮飞时)
        if (task.PlannedStrikeTime > 0f && !float.IsNaN(GunL.FlyTime)) {
            float now = MissionClock.Seconds;
            if (float.IsNaN(now) || now + GunL.FlyTime < task.PlannedStrikeTime) return;
        }
        StartCoroutineHost(FireRoutine(GunL, LeftRight.Left, task));
        _fireL = _fireR = true; // 齐射一击发锁双炮 (触发核心击发前防连点)
    }

    /// <summary>齐射确认前置: 双炮进 TRAK 即出火控卡+五步确认 (瞄准过程中, 不等稳定); 完成才置位, 解保险靠 _salvoConfirmed 门.</summary>
    private IEnumerator SalvoConfirmRoutine(FireTask task) {
        try {
            if (FireLock == null || ConsolePort == null) yield break;
            yield return FireLock.Acquire();
            try {
                if (Calculator != null) {
                    yield return Calculator.SetDistance(task.Distance);
                    yield return Calculator.SetDirection(task.Angle);
                    yield return Calculator.SetCharge(GunL?.DesiredCharge > 0 ? GunL.DesiredCharge : 1);
                    yield return Calculator.SetShellType(task.Shell);
                    yield return Calculator.Calculate();
                }
                yield return ConsolePort.ConfirmTask();
                yield return ConsolePort.ConfirmBullet();
                yield return ConsolePort.ConfirmRotation();
                yield return ConsolePort.ConfirmElevation();
                yield return ConsolePort.ReadyToFire();
                // 任务可能在确认途中被撤 (RequestCancel 已清 confirmed): 只有任务还在炮上才置位, 防残留
                if (_taskL == task || _taskR == task) {
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
                if (_taskL == task || _taskR == task) {
                    _armedL = _armedR = true;
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
                if (Calculator != null) {
                    yield return Calculator.SetDistance(task.Distance);
                    yield return Calculator.SetDirection(task.Angle);
                    yield return Calculator.SetCharge(gun.DesiredCharge > 0 ? gun.DesiredCharge : 1);
                    yield return Calculator.SetShellType(task.Shell);
                    yield return Calculator.Calculate();
                }
                yield return ConsolePort.ConfirmTask();
                yield return ConsolePort.ConfirmBullet();
                yield return ConsolePort.ConfirmRotation();
                yield return ConsolePort.ConfirmElevation();
                yield return ConsolePort.ReadyToFire();
                // 任务可能在确认途中被撤 (RequestCancel 已清 confirmed): 只有任务还在炮上才置位, 防残留
                if ((side == LeftRight.Left ? _taskL : _taskR) == task) {
                    if (side == LeftRight.Left) _confirmedL = true; else _confirmedR = true;
                    MelonLogger.Msg($"[FC] {side}: confirmed fc#{task.Id}");
                }
            }
            finally { FireLock?.Release(); }
        }
        finally {
            if (side == LeftRight.Left) _confirmingL = false; else _confirmingR = false;
        }
    }

    /// <summary>解保险 (短锁内, 卡+确认已前置).</summary>
    private IEnumerator ArmRoutine(GunControl gun, LeftRight side) {
        if (FireLock == null || ConsolePort == null) yield break;
        yield return FireLock.Acquire();
        try {
            yield return ConsolePort.Arm(side);
            MelonLogger.Msg($"[FC] {side}: armed fc#{(side == LeftRight.Left ? _taskL : _taskR)?.Id}");
        }
        finally { FireLock?.Release(); }
    }

    /// <summary>击发 (统一火控短锁) + 收尾.</summary>
    private IEnumerator FireRoutine(GunControl gun, LeftRight side, FireTask task) {
        if (FireLock == null) yield break;
        yield return FireLock.Acquire();
        try {
            MelonLogger.Msg($"[FC] {side}: FIRE pressed");
            FcsBus.Fire?.Invoke(); // 击发钮全局一个: 齐射一按两炮齐
        }
        finally { FireLock.Release(); }
        yield return FinishRoutine(gun, side, task);
    }

    /// <summary>等击发确认 (炮表倒计时启动 = FlyRemaining 从 NaN 变有效; 齐射等两炮) → 收尾: 完成队列 + 槽位释放.
    /// 哑炮防护: 5s 无倒计时 = 哑炮 → 重新解保险 + 再击发 (最多 3 次击发); 全哑 → 报错撤任务
    /// (膛内哑弹由下一任务自然消耗 — DecidePrepStep 见膛内弹对就直接用, 系统自愈).
    /// 落点指示不归 FC — GC 常驻循环自检击发 (pendingReload 上升沿) 锁存+通知 DC, 手动开火同样出轨迹.</summary>
    private IEnumerator FinishRoutine(GunControl gun, LeftRight side, FireTask task) {
        bool confirmed = false;
        for (int attempt = 0; attempt < 3; attempt++) {
            // 击发确认等待: 炮表倒计时启动 (齐射 = 两炮都有)
            float waited = 0f;
            while (waited < 5f) {
                bool ok = task.SalvoPair
                    ? !float.IsNaN(GunL?.FlyRemaining ?? float.NaN) && !float.IsNaN(GunR?.FlyRemaining ?? float.NaN)
                    : !float.IsNaN(gun.FlyRemaining);
                if (ok) { confirmed = true; break; }
                yield return new WaitForSeconds(0.1f);
                waited += 0.1f;
            }
            if (confirmed) break;
            if (attempt >= 2) break;
            MelonLogger.Warning($"[FC] {side}: misfire (5s no countdown), re-arm + fire retry {attempt + 1}/2");
            if (task.SalvoPair) {
                if (FireLock == null || ConsolePort == null) break;
                yield return FireLock.Acquire();
                try { yield return ConsolePort.ArmBoth(); } // 齐射重试: 双炮保险同时解除 (不许一先一后)
                finally { FireLock.Release(); }
            }
            else yield return ArmRoutine(gun, side);
            if (FireLock == null) break;
            yield return FireLock.Acquire();
            try { FcsBus.Fire?.Invoke(); } // 击发钮全局一个: 齐射重击只哑炮会响 (已响的炮没弹)
            finally { FireLock.Release(); }
        }
        if (!confirmed) {
            MelonLogger.Error($"[FC] {side}: gun did not fire after 3 attempts, cancel fc#{task.Id}");
            RequestCancel(task);
            yield break;
        }
        FinishTask(side, task, gun);
        if (task.SalvoPair) FinishTask(side == LeftRight.Left ? LeftRight.Right : LeftRight.Left, task, side == LeftRight.Left ? GunR : GunL);
    }

    private void FinishTask(LeftRight side, FireTask task, GunControl? gun) {
        if ((side == LeftRight.Left ? _taskL : _taskR) != task) return; // 已收尾过
        if (gun == null) return;
        _finished.Add(new FinishedEntry { Task = task, Fly = gun.FlyTime, FireMission = MissionClock.Seconds, RemainingSource = () => gun.FlyRemaining });
        if (_finished.Count > 8) _finished.RemoveAt(0);
        MelonLogger.Msg($"[FC] {side}: finished fc#{task.Id} (fly={gun.FlyTime:F2}s)");
        if (side == LeftRight.Left) _taskL = null; else _taskR = null;
        _armedL = _armedR = false;
        _confirmedL = _confirmedR = _salvoConfirmed = false;
        _fireL = _fireR = false;
        if (GunL != null && _taskL == null) { GunL.DesiredShell = (BulletType)(-1); GunL.DesiredCharge = -1; }
        if (GunR != null && _taskR == null) { GunR.DesiredShell = (BulletType)(-1); GunR.DesiredCharge = -1; }
        ClearSyncIfAlone();
    }

    /// <summary>完成队列条目 (HUD Finish Queue 用): 抵达时刻 = FireMission + Fly; 倒计时吃炮表剩余 (与任务时钟无关).</summary>
    public class FinishedEntry {
        public FireTask Task = null!;
        public float Fly;
        public float FireMission;
        public System.Func<float>? RemainingSource;
    }

    private readonly List<FinishedEntry> _finished = new();
    public IReadOnlyList<FinishedEntry> Finished => _finished;

    private void StartCoroutineHost(IEnumerator it) {
        try { MelonCoroutines.Start(it); } catch (Exception ex) { MelonLogger.Error($"[FC] coroutine start failed: {ex.Message}"); }
    }
}
