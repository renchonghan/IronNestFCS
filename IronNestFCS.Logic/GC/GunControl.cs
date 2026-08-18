using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>炮上动作 (2.0 Action 状态机), 相位代号映射见 Architecture.md HUD 段.</summary>
public enum GunAction {
    Idle,   // 0-0 空闲 (仅 Manual 模式由 FC 显示)
    Selc,   // 1-2 采购 (保证弹巢至少有一个相应的弹)
    Dump,   // 1-3 退弹 (平射打掉)
    Shrd,   // 2-1 选弹 (转弹仓)
    Shld,   // 2-2 推弹 (推弹机)
    Pwdr,   // 2-3 给药
    Load,   // 2-4 装填 (推药)
    Trak,   // 3-1 追踪 (持续跟诸元)
    Rest,   // 3-3 复位 (回位)
    Fall,   // 0-0 错误 (红显)
}

/// <summary>
/// [GC] GunControl — 单炮执行器 (2.0 新架构, 每炮一条, 25fps 常驻线程).
/// 前向接口 (FC 写): ManualControl / SyncCommand / AzimuthSelect / DesiredShell / DesiredCharge /
///   DesiredElevation / DesiredAzimuth. 开火不在这里 (统一火控归 FC, DUMP 平射除外).
/// 状态端口 (FC 读): 实装快照 (动作边界刷新) / Elevation / Azimuth 回读 / Action / FlyTime /
///   AllReady / Fired / KernelMode.
/// 内部: 跟踪稳定器 (TrackAxis, E 恒追; H 仅被 AzimuthSelect 选中时追), 硬件读数缓存,
///   DUMP 自决, 给药独立 (药包杆与计算台无关, 只受共享药包池约束), 采购台短锁, 动作看门狗 → FALL.
/// </summary>
public class GunControl {
    private readonly LeftRight _side;
    private readonly GunSystem _gun;
    private readonly PurchaseDeck _deck;
    private readonly CoroutineLock _purchaseLock; // 采购台短锁 (两炮共享)
    private readonly CoroutineLock _fireLock;     // 统一火控锁 (与 FC 共享; GC 只用于 DUMP 平射)

    // ===== 指令端口 (FC 持续写, 最新值覆盖) =====
    public bool ManualControl;          // 手动: FC 停机, 立即停手不碰硬件 (线程照跑)
    public bool SyncCommand;            // 齐射锁定: 按左炮数据走 (FC 侧镜像 DesiredX)
    public bool AzimuthSelect;          // 本炮被选中执行水平追踪
    public BulletType DesiredShell = (BulletType)(-1);
    public int DesiredCharge = -1;      // -1 = 无任务
    public float DesiredElevation = float.NaN;  // 目标俯仰 (FC 持续输出, 含提前量)
    public float DesiredAzimuth = float.NaN;    // 目标方位 (FC 持续输出, 含提前量)

    // ===== 状态端口 =====
    public string Chamber { get; private set; } = "";   // 膛内弹种 (实装快照)
    public int Charges { get; private set; }            // 实装药数 (实装快照)
    public float Elevation { get; private set; } = float.NaN;      // 实际俯仰 (每帧传感器)
    public float Azimuth { get; private set; } = float.NaN;        // 实际方位回读 (每帧)
    public GunAction Action { get; private set; } = GunAction.Idle;
    public float FlyTime { get; private set; } = float.NaN;        // 飞行时间 (游戏自解: 瞄准期 PredictedImpactTime / 击发后锁存)
    public bool AllReady { get; private set; }                     // 弹药确认且追踪稳定; 不稳定回退
    public bool Fired { get; private set; }                        // 本发已击发 (FC 收尾用)
    public bool KernelMode { get; private set; }                   // 内核态: 硬件动作执行中 (指令只记录不生效)

    // ===== 内部 =====
    private readonly TrackAxis _eAxis = new(0.01f, 0.1f, 0.5f);  // E: 收敛 0.01°, 变积分 0.1~0.5
    private readonly TrackAxis _hAxis = new(0.1f, 0.3f, 1.0f);    // H: 收敛 0.1°, 变积分 0.3~1
    private object? _loopHandle;
    private object? _taskHandle;
    private float _eStable;
    private float _hStable;
    private float _latchedFlyTime = float.NaN; // 击发后锁存总飞时
    private bool _disposed;

    // 实时弹道指示器 push 目标 (FcsModule 注入 DC 回调): (瞄准点, 杀伤圈, 弹种)
    public delegate void BallisticPush(LeftRight side, float aimX, float aimY, float killRadiusKm, int bulletType);
    public BallisticPush? OnBallisticPush;

    /// <summary>齐射对端 (SyncCommand 时相位级同步互相等; 对炮 FALL → 本炮也 FALL 防陪死).</summary>
    public GunControl? SyncPeer;

    public GunControl(LeftRight side, GunSystem gun, PurchaseDeck deck, CoroutineLock purchaseLock, CoroutineLock fireLock) {
        _side = side;
        _gun = gun;
        _deck = deck;
        _purchaseLock = purchaseLock;
        _fireLock = fireLock;
    }

    public void Start() {
        _disposed = false;
        _loopHandle = MelonCoroutines.Start(Loop());
    }

    public void Stop() {
        _disposed = true;
        TryStop(_loopHandle);
        TryStop(_taskHandle);
        _loopHandle = _taskHandle = null;
        KernelMode = false;
        AllReady = false;
        Action = GunAction.Idle;
    }

    private static void TryStop(object? h) {
        if (h == null) return;
        try { MelonCoroutines.Stop(h); } catch { }
    }

    /// <summary>25fps 常驻循环: 传感器每帧读, 实装快照动作边界读; 任务链在独立协程里跑.</summary>
    private IEnumerator Loop() {
        while (!_disposed) {
            yield return new WaitForSeconds(0.04f);
            ReadSensors();                     // 每帧: 俯仰/方位/飞时 (传感器值不受动作影响)
            if (ManualControl) {               // 手动: 立即停手, 残局交玩家, 线程照跑
                TryStop(_taskHandle);
                _taskHandle = null;
                KernelMode = false;
                AllReady = false;
                Action = GunAction.Idle;
                PushBallistic(-1);             // 弹种 -1 = 未就绪不渲染
                continue;
            }
            if (_taskHandle == null && DesiredShell != (BulletType)(-1) && DesiredCharge >= 0) {
                RefreshSnapshot();             // 任务上炮: 边界读实装快照
                _taskHandle = MelonCoroutines.Start(TaskChain());
            }
        }
    }

    /// <summary>每帧传感器: 俯仰/方位/飞时. 实装快照 (膛内/装药) 不在这里读 — 动作中不可信.</summary>
    private void ReadSensors() {
        Elevation = _gun.ActualElevation();
        Azimuth = FcsBus.TurretRead != null ? FcsBus.TurretRead() : float.NaN; // 经共享口回读 (炮塔由 FC 直控)
        if (!Fired) {
            FlyTime = _gun.RemainingFlightSeconds(); // 瞄准期: 游戏按实际仰角自解; NaN = 未就绪
        }
    }

    /// <summary>动作边界: 刷新实装快照 (机构稳定期读数才可信).</summary>
    private void RefreshSnapshot() {
        Chamber = _gun.BulletInChamber() ?? "";
        Charges = _gun.LoadedPowderCharges();
    }

    /// <summary>任务链: 弹药准备 → TRAK (持续) → 击发后 REST → IDLE. 每步短协程 + 看门狗.</summary>
    private IEnumerator TaskChain() {
        AllReady = false;
        Fired = false;
        _latchedFlyTime = float.NaN;
        _eAxis.ResetForTask();
        _hAxis.ResetForTask();
        while (!_disposed) {
            if (ManualControl) yield break;
            RefreshSnapshot();
            bool chamberOk = Chamber == DesiredShell.ToString();
            bool chargeOk = !SyncCommand ? Charges >= DesiredCharge : Charges == DesiredCharge; // 齐射多药/少药都是错药
            bool shellWrong = Chamber.Length > 0 && !chamberOk;

            // 弹药准备 (齐射: 每步相位级同步, 两炮都完成才一起走下一步)
            if (shellWrong) {
                yield return WaitPeerPhase(GunAction.Dump);
                yield return Exec(GunAction.Dump, 25f, DumpRoutine);
                continue; // 平射打掉后回决策
            }
            if (Chamber.Length == 0) {
                if (!_gun.HaveBulletInCylinder(DesiredShell)) {
                    yield return WaitPeerPhase(GunAction.Selc);
                    yield return Exec(GunAction.Selc, 20f, SelcRoutine);
                    continue;
                }
                yield return WaitPeerPhase(GunAction.Shrd);
                yield return Exec(GunAction.Shrd, 8f, ShrdRoutine);
                continue;
            }
            if (Chamber.Length > 0 && !chargeOk) {
                if (Charges > DesiredCharge && SyncCommand) { // 齐射多药: 只能整发打掉
                    yield return WaitPeerPhase(GunAction.Dump);
                    yield return Exec(GunAction.Dump, 25f, DumpRoutine);
                    continue;
                }
                yield return WaitPeerPhase(GunAction.Pwdr);
                yield return Exec(GunAction.Pwdr, 25f, PwdrRoutine);
                continue;
            }
            if (!_gun.CanFire()) {
                yield return WaitPeerPhase(GunAction.Load);
                yield return Exec(GunAction.Load, 20f, LoadRoutine);
                continue;
            }
            break; // 弹药就绪 → TRAK
        }
        if (ManualControl || _disposed) yield break;

        // 3-1 TRAK: 持续追踪直到击发 (AllReady 供 FC 统一火控; 追踪不稳回退)
        yield return WaitPeerPhase(GunAction.Trak);
        Action = GunAction.Trak;
        while (!_disposed) {
            if (ManualControl) yield break;
            yield return new WaitForSeconds(0.04f);
            if (!TrackOnce()) continue;     // 本帧追踪推进 (AllReady 内部维护)
            if (Fired || _gun.HasFired()) break; // 击发 (FC 或玩家)
        }
        if (_gun.HasFired() && !Fired) {
            Fired = true;
            var sw = _gun.StopwatchLatch();
            if (sw.HasValue && sw.Value.travelTime > 0.01f) _latchedFlyTime = sw.Value.travelTime;
        }
        FlyTime = _latchedFlyTime; // 击发后: 锁存真值供 FC 落点计时

        // 3-3 REST 复位 → IDLE
        yield return Exec(GunAction.Rest, 30f, RestRoutine);
        Action = GunAction.Idle;
        _taskHandle = null;
    }

    /// <summary>TRAK 单帧推进: E 恒追 (TrackAxis 天顶星伺服 = 设 1 帧预测目标 + 修正);
    /// H 仅在被 AzimuthSelect 选中时追. 双轴稳定 + 弹药确认 → AllReady; 不稳定即回退.</summary>
    private bool TrackOnce() {
        bool eLock = false;
        if (!float.IsNaN(DesiredElevation)) {
            float corr = _eAxis.Step(DesiredElevation - Elevation, _gun.ElevationVelocity(), Elevation);
            if (!_eAxis.GaveUp) _gun.SetElevationValue(DesiredElevation + corr);
            eLock = _eAxis.Locked;
        }
        bool hLock = !AzimuthSelect; // 未被选中: H 不归本炮, 不算就绪条件
        if (AzimuthSelect && !float.IsNaN(DesiredAzimuth) && !float.IsNaN(Azimuth)) {
            float corr = _hAxis.Step(Mathf.DeltaAngle(Azimuth, DesiredAzimuth), FcsBus.TurretVelRead != null ? FcsBus.TurretVelRead() : 0f, Azimuth);
            if (!_hAxis.GaveUp && FcsBus.TurretSet != null) FcsBus.TurretSet(DesiredAzimuth + corr);
            hLock = _hAxis.Locked;
        }
        bool ammoReady = Chamber == DesiredShell.ToString()
            && (!SyncCommand ? Charges >= DesiredCharge : Charges == DesiredCharge);
        AllReady = ammoReady && eLock && hLock; // 任一不稳 → 回退
        return Fired || _gun.HasFired();
    }

    /// <summary>带内核态 + 看门狗的动作执行包装: 动作中 KernelMode 置位 (FC 指令只记录),
    /// 超时/异常 → FALL, 不静默空转. 手动驱动子枚举器以绕开 C# 的 yield-in-try 限制
    /// (MoveNext 的异常在 try 内捕获, yield return 在 try 外).</summary>
    private IEnumerator Exec(GunAction action, float deadlineSec, System.Func<IEnumerator> routine) {
        Action = action;
        KernelMode = true;
        float t0 = Time.time;
        Exception? failure = null;
        IEnumerator it = routine();
        while (true) {
            bool more;
            try { more = it.MoveNext(); }
            catch (Exception ex) { failure = ex; more = false; }
            if (failure != null || !more) break;
            yield return it.Current;
        }
        if (failure != null) {
            MelonLogger.Error($"[GC] {_side}: {action} crashed: {failure.Message}");
            try { (it as IDisposable)?.Dispose(); } catch { } // 迭代器对象实现 IDisposable, finally 块会还锁
        }
        KernelMode = false;
        if (failure != null || Time.time - t0 > deadlineSec) {
            Action = GunAction.Fall;
            MelonLogger.Error($"[GC] {_side}: {action} {(failure != null ? "failed" : "watchdog timeout")} → FALL");
            // FALL: 停在本炮等 FC 换策略 (换炮/重试/换弹种) 或 ManualControl 复位
            while (!ManualControl && !_disposed && Action == GunAction.Fall) {
                yield return new WaitForSeconds(0.25f);
            }
            if (ManualControl || _disposed) yield break;
            Action = GunAction.Idle;
        }
    }

    /// <summary>SELC 采购: 弹巢缺目标弹 → 采购台短锁内查+买 (两炮共用互不重买).</summary>
    private IEnumerator SelcRoutine() {
        yield return _purchaseLock.Acquire();
        try {
            if (!_gun.HaveBulletInCylinder(DesiredShell)) {
                yield return _deck.BuyShell(DesiredShell, _side);
                float waited = 0f;
                while (!_gun.HaveBulletInCylinder(DesiredShell) && waited < 5f) {
                    yield return new WaitForSeconds(0.5f);
                    waited += 0.5f;
                }
                if (!_gun.HaveBulletInCylinder(DesiredShell)) {
                    MelonLogger.Error($"[GC] {_side}: SELC buy {DesiredShell} not landed");
                    yield break; // Exec 会判 FALL
                }
            }
        }
        finally { _purchaseLock.Release(); }
    }

    /// <summary>SHRD 选弹: 转弹仓到目标弹位 (未推弹前可改).</summary>
    private IEnumerator ShrdRoutine() {
        yield return _gun.RotateCylinderTo(DesiredShell);
        yield return new WaitForSeconds(0.5f); // 转完等弹仓列表刷新再确认
        RefreshSnapshot();
    }

    /// <summary>SHLD 推弹: 按推弹按钮 + 等入膛 (机构动作中不读膛内, 用 WaitShellRammed 的状态机判定).</summary>
    private IEnumerator ShldRoutine() {
        yield return _gun.PressRammer();
        yield return _gun.WaitRammingStart();
        yield return _gun.WaitShellRammed();
        RefreshSnapshot();
    }

    /// <summary>PWDR 给药: 查池不足锁内买药 (药包杆与计算台无关), 补拉差, 不推药.</summary>
    private IEnumerator PwdrRoutine() {
        int selected = _gun.SelectedPowderCharges();
        int need = DesiredCharge;
        if (selected > need) yield break; // 超出 (齐射多药在决策层 DUMP; 单发多药由 FC 侧 useActual 处理)
        yield return _purchaseLock.Acquire();
        try {
            while (_gun.RemainingCharges() + selected < need) {
                yield return _deck.BuyPowders();
                if (_gun.RemainingCharges() + selected >= need) break;
                yield return new WaitForSeconds(0.5f);
            }
        }
        finally { _purchaseLock.Release(); }
        if (selected < need) yield return _gun.PullPowders(need - selected);
        RefreshSnapshot();
    }

    /// <summary>LOAD 装填: 推药入膛 (CanFire 置位为止, 超时由 Exec 判 FALL).</summary>
    private IEnumerator LoadRoutine() {
        yield return _gun.RamPowder();
        float waited = 0f;
        while (!_gun.CanFire() && waited < 15f) {
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;
        }
        RefreshSnapshot();
    }

    /// <summary>DUMP 自决 (最复杂): 膛内弹不对 (或齐射多药/少药) → 给药至少 1 包 (查池, 没药先买) +
    /// 0° 平射自行击发打掉, 不对准直接打 (统一火控例外: 退弹平射 GC 自己打).</summary>
    private IEnumerator DumpRoutine() {
        if (Charges <= 0) {
            yield return _purchaseLock.Acquire();
            try {
                while (_gun.RemainingCharges() < 1) {
                    yield return _deck.BuyPowders();
                    yield return new WaitForSeconds(0.5f);
                }
            }
            finally { _purchaseLock.Release(); }
            yield return _gun.PullPowders(1);
            yield return _gun.RamPowder();
            float waited = 0f;
            while (!_gun.CanFire() && waited < 15f) {
                yield return new WaitForSeconds(0.5f);
                waited += 0.5f;
            }
        }
        yield return _fireLock.Acquire();
        try {
            FcsBus.Fire?.Invoke();
            yield return _gun.WaitFire();
        }
        finally { _fireLock.Release(); }
        yield return new WaitForSeconds(2f); // 机构循环
        RefreshSnapshot();
    }

    /// <summary>REST 复位: 炮口回位.</summary>
    private IEnumerator RestRoutine() {
        yield return _gun.WaitBackToIdle();
    }

    /// <summary>齐射相位级同步 (仪式感): 进入动作前等对炮到达同一相位或更后; 对炮 FALL → 本炮 FALL (防陪死).
    /// 非齐射或对端不存在时直接放行.</summary>
    private IEnumerator WaitPeerPhase(GunAction next) {
        if (!SyncCommand || SyncPeer == null) yield break;
        while (SyncCommand && SyncPeer != null && !_disposed) {
            if (SyncPeer.Action == GunAction.Fall) {
                Action = GunAction.Fall; // 对炮挂了, 不陪死: 自己也报 FALL 交给 FC
                MelonLogger.Error($"[GC] {_side}: salvo peer FALL, follow FALL");
                yield break;
            }
            if (SyncPeer.Action >= next) yield break; // 对炮已到本相位或更后 → 一起走
            yield return new WaitForSeconds(0.04f);
        }
    }

    /// <summary>每帧 push 实时弹道指示器数据给 DC (开火前的绿色瞄准十字/LR; 弹种 -1 = 未就绪不渲染).</summary>
    private void PushBallistic(int bulletType) {
        OnBallisticPush?.Invoke(_side, 0f, 0f, 0f, bulletType); // 瞄准点/杀伤圈坐标由 DC 渲染线程按 FC 数据画, GC 只出就绪态
    }
}
