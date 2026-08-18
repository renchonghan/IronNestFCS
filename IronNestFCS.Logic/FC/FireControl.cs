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
    public System.Func<Vector3?>? PositionSource; // 世界位置 (实体/令牌每帧刷新), null = 位置源失效
    public System.Func<Vector2>? VelocitySource; // TWS 速度矢量 (km/s, 地图局部系), null = 无预瞄直瞄
    public BulletType Shell;
    public ChargeMode Mode = ChargeMode.Normal;
    public float PlannedStrikeTime = -1f; // 预定打击时间 (任务时钟秒, -1 = 就绪即打)
    public int Priority;
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
    /// <summary>队列显示 push (DC 渲染线程): 打击队列指示器数据.</summary>
    public System.Action<IReadOnlyList<FireTask>, GunControl?, GunControl?>? OnQueueChanged;
    /// <summary>落点指示器创建 (DC): (落点世界坐标, 弹种, 飞行时长, 击发时刻任务时钟).</summary>
    public System.Action<Vector3, BulletType, float, float>? OnShellFired;

    // ===== 请求入口 (DC 调用; 显控台舰长席自己排布, FC 只接收) =====
    private readonly List<FireTask> _requests = new();   // 待处理请求 (入队/取消)
    private readonly List<FireTask> _queue = new();      // 任务队列
    private FireTask? _taskL;
    private FireTask? _taskR;
    private int _fcCounter;

    // ===== 状态 =====
    public FireMode Mode { get; private set; } = FireMode.Manual;
    public int QueueCount => _queue.Count;
    public bool AutoFire { get; set; }
    public bool AutoTask { get; set; }
    private bool _armedL;
    private bool _armedR;
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
        Mode = FireMode.Manual;
    }

    // ===== DC 调用: 请求入口 =====
    public void RequestTask(FireTask task) { task.Id = ++_fcCounter; _requests.Add(task); }
    public void RequestCancel(FireTask task) { _requests.Remove(task); _queue.Remove(task); if (_taskL == task) _taskL = null; if (_taskR == task) _taskR = null; }

    /// <summary>模式推导 (DC 只传 AutoFire + AutoTask 开关位): 扫荡=FullAuto 强制自动开火; AF=Semi; AF off=PreAiming; ManualControl=Manual.</summary>
    public void SetManual(bool manual) {
        if (manual) Mode = FireMode.Manual;
        else Mode = AutoTask ? FireMode.FullAuto : (AutoFire ? FireMode.SemiAuto : FireMode.PreAiming);
        if (GunL != null) GunL.ManualControl = manual;
        if (GunR != null) GunR.ManualControl = manual;
    }

    private IEnumerator Loop() {
        while (!_disposed) {
            yield return new WaitForSeconds(0.04f);
            if (Mode == FireMode.Manual) continue;
            ProcessRequests();
            Dispatch();                       // 空闲炮 + 实装匹配派发
            UpdateFireSolutions();            // 每帧诸元 → GC 指令
            FireArbiter();                    // 就绪 → 统一火控 → 收尾
            OnQueueChanged?.Invoke(_queue, GunL, GunR);
        }
    }

    private void ProcessRequests() {
        foreach (var r in _requests) {
            if (!_queue.Contains(r)) _queue.Add(r);
        }
        _requests.Clear();
    }

    /// <summary>智能派发 (骨架): 队首任务按实装匹配优先挑炮 (能不 DUMP 就不 DUMP), 空闲才派; 乱序/前瞻后续版本.</summary>
    private void Dispatch() {
        while (_queue.Count > 0) {
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

    /// <summary>每帧诸元: 位置源 → 相对方位/距离 (含提前量解析解) → 装药/仰角 → GC 指令 + AzimuthSelect.</summary>
    private void UpdateFireSolutions() {
        GunControl? selected = null;
        foreach (var (gun, task) in new[] { (GunL, _taskL), (GunR, _taskR) }) {
            if (gun == null || task == null) continue;
            var pos = task.PositionSource?.Invoke();
            if (pos == null) { RequestCancel(task); continue; } // 位置源失效 (阵亡/令牌离图) → 撤任务
            var (dist, angle) = RelToTarget(pos.Value);
            var vel = task.VelocitySource?.Invoke();
            if (vel != null && vel.Value.magnitude > 0.0001f) {
                var (ld, la) = LeadSolve(dist, angle, vel.Value); // 提前量解析解 (飞时线性 → 一元二次)
                dist = ld; angle = la;
            }
            int charge = ChargeOf(task, dist);
            gun.DesiredShell = task.Shell;
            gun.DesiredCharge = charge;
            gun.DesiredElevation = ShellData.ElevationDeg(dist, charge);
            gun.DesiredAzimuth = angle;
            if (selected == null) { selected = gun; } // 执行顺序: 先 L 后 R (乱序后续版本按时间轴)
        }
        // AzimuthSelect: 只有被派任务的炮能被选中; 齐射 (同目标) 时无所谓
        if (selected != null) {
            if (GunL != null) GunL.AzimuthSelect = GunL == selected || (_taskL != null && _taskR != null && SameTarget(_taskL, _taskR));
            if (GunR != null) GunR.AzimuthSelect = GunR == selected || (_taskL != null && _taskR != null && SameTarget(_taskL, _taskR));
        }
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
        Vector2 dir = new(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
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

    /// <summary>统一火控仲裁: 首次 AllReady → 五步确认 + Arm (一次性); AutoFire/预定时间 → 击发; Fired → 收尾; FALL → 换炮.</summary>
    private void FireArbiter() {
        foreach (var (gun, task, armed, side) in new[] {
                     (GunL, _taskL, _armedL, LeftRight.Left),
                     (GunR, _taskR, _armedR, LeftRight.Right) }) {
            if (gun == null || task == null) { if (side == LeftRight.Left) _armedL = false; else _armedR = false; continue; }
            if (!armed && gun.AllReady && gun.AzimuthSelect) {
                StartCoroutineHost(ArmRoutine(gun, side)); // 首次套上解保险 (一次性, 与追踪并行)
                if (side == LeftRight.Left) _armedL = true; else _armedR = true;
                continue;
            }
            if (!armed || !gun.AllReady || !gun.AzimuthSelect) continue; // 不稳回退: 不开火 (已 Arm 不自动回保险)
            if (Mode == FireMode.PreAiming) continue; // 等玩家扣扳机 (Fired 置位后收尾)
            // 预定打击时间: 当前任务时钟 + FlyTime ≥ 预定 → 开火; -1 = 就绪即打
            if (task.PlannedStrikeTime > 0f && !float.IsNaN(gun.FlyTime)) {
                float now = MissionClock.Seconds;
                if (float.IsNaN(now) || now + gun.FlyTime < task.PlannedStrikeTime) continue;
            }
            StartCoroutineHost(FireRoutine(gun, side));
        }
    }

    /// <summary>五步确认 + Arm (统一火控, 短锁内).</summary>
    private IEnumerator ArmRoutine(GunControl gun, LeftRight side) {
        if (FireLock == null || ConsolePort == null) yield break;
        yield return FireLock.Acquire();
        try {
            yield return ConsolePort.ConfirmTask();
            yield return ConsolePort.ConfirmBullet();
            yield return ConsolePort.ConfirmRotation();
            yield return ConsolePort.ConfirmElevation();
            yield return ConsolePort.ReadyToFire();
            yield return ConsolePort.Arm(side);
            if (Calculator != null) {
                var task = side == LeftRight.Left ? _taskL : _taskR;
                if (task != null) {
                    yield return Calculator.SetDistance(Mathf.Round(task.PositionSource?.Invoke() != null ? 0f : 0f) * 0f + (side == LeftRight.Left ? 1f : 1f)); // 骨架占位: 解算台出火控卡 (仪式感), 数值待接
                }
                yield return Calculator.Calculate();
            }
            MelonLogger.Msg($"[FC] {side}: armed fc#{(side == LeftRight.Left ? _taskL : _taskR)?.Id}");
        }
        finally { FireLock?.Release(); }
    }

    /// <summary>击发 + 收尾 (Fired 快照 + 落点指示器 + 槽位释放).</summary>
    private IEnumerator FireRoutine(GunControl gun, LeftRight side) {
        if (FireLock == null || ConsolePort == null) yield break;
        yield return FireLock.Acquire();
        try {
            FcsBus.Fire?.Invoke();
        }
        finally { FireLock.Release(); }
        // 等 Fired (GC 状态端口) 置位
        float waited = 0f;
        while (!gun.Fired && waited < 10f) {
            yield return new WaitForSeconds(0.05f);
            waited += 0.05f;
        }
        var task = side == LeftRight.Left ? _taskL : _taskR;
        if (task != null && gun.Fired) {
            var pos = task.PositionSource?.Invoke();
            if (pos != null && OnShellFired != null) {
                float fireMission = MissionClock.Seconds;
                OnShellFired(pos.Value, task.Shell, gun.FlyTime, fireMission);
            }
            MelonLogger.Msg($"[FC] {side}: fired fc#{task.Id} (fly={gun.FlyTime:F2}s)");
        }
        if (side == LeftRight.Left) { _taskL = null; _armedL = false; }
        else { _taskR = null; _armedR = false; }
        gun.DesiredShell = (BulletType)(-1);
        gun.DesiredCharge = -1;
    }

    private void StartCoroutineHost(IEnumerator it) {
        try { MelonCoroutines.Start(it); } catch (Exception ex) { MelonLogger.Error($"[FC] coroutine start failed: {ex.Message}"); }
    }
}
