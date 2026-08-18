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
    /// <summary>游戏 CANFIRE 信号 (膛内+装药+保险): 绿十字显示门槛 (1.x 同口径, 弹没装好不出落点).</summary>
    public bool CanFire { get { try { return _gun.CanFire(); } catch { return false; } } }
    /// <summary>击发后游戏炮表剩余秒数 (与游戏自身飞行指示器同一数据源, 消除检测时间差); 未倒计时 NaN.</summary>
    public float FlyRemaining { get; private set; } = float.NaN;

    // ===== 内部 =====
    private readonly TrackAxis _eAxis = new(0.01f, 0.1f, 0.5f);  // E: 收敛 0.01°, 变积分 0.1~0.5
    private readonly TrackAxis _hAxis = new(0.1f, 0.3f, 1.0f);    // H: 收敛 0.1°, 变积分 0.3~1
    private object? _loopHandle;
    private object? _taskHandle;
    private float _eStable;
    private float _hStable;
    private float _latchedFlyTime = float.NaN; // 击发后锁存总飞时
    private string _lastStateKey = "";         // 装填状态码变化检测 (诊断用, 定位完删)
    private float _lastLoadDiag;               // CanFire 不置位诊断节流 (定位完删)
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
        float idleRefresh = 0f;
        while (!_disposed) {
            yield return new WaitForSeconds(0.04f);
            ReadSensors();                     // 每帧: 俯仰/方位/飞时 (传感器值不受动作影响)
            // 装填状态码诊断 (定位完删): 游戏状态机 stateKey 变化才打 — 玩家手动装填也能抓, 不用跑 mod 动作
            var stateKey = _gun.ReloadStateKey() ?? "<null>";
            if (stateKey != _lastStateKey) {
                _lastStateKey = stateKey;
                MelonLogger.Msg($"[GC] {_side}: reloadState '{stateKey}'");
            }
            if (ManualControl) {               // 手动: 立即停手, 残局交玩家, 线程照跑
                TryStop(_taskHandle);
                _taskHandle = null;
                KernelMode = false;
                AllReady = false;
                Action = GunAction.Idle;
                SalvoActive = false;
                PushBallistic(-1);             // 弹种 -1 = 未就绪不渲染
                continue;
            }
            // 方位追踪不受装弹影响: 被选中即全程追 (从派发起, 不等 LOAD/TRAK)
            if (AzimuthSelect && !float.IsNaN(DesiredAzimuth) && !float.IsNaN(Azimuth)) {
                float hCorr = _hAxis.Step(Mathf.DeltaAngle(Azimuth, DesiredAzimuth), FcsBus.TurretVelRead?.Invoke() ?? 0f, Azimuth);
                if (!_hAxis.GaveUp && FcsBus.TurretSet != null) FcsBus.TurretSet(DesiredAzimuth + hCorr);
            }
            if (DesiredCharge < 0) SalvoActive = false; // 任务撤了/完成了: 导演标志复位, 下次派发才能重启
            if (_taskHandle == null) {
                // 空闲: 周期性刷新实装快照 (FC 派发读实装匹配用, 不能陈), 有新任务则起链
                if (DesiredCharge < 0 && Time.time - idleRefresh > 0.5f) {
                    RefreshSnapshot();
                    idleRefresh = Time.time;
                }
                if (DesiredShell != (BulletType)(-1) && DesiredCharge >= 0 && !SalvoActive) {
                    RefreshSnapshot();         // 任务上炮: 边界读实装快照
                    if (SyncCommand && SyncPeer != null) {
                        // 齐射: 左炮启动导演 (一条协程带两炮), 右炮登记同一句柄不自己起链
                        if (_side == LeftRight.Left) {
                            MelonLogger.Msg($"[GC] {_side}: salvo director start shell={DesiredShell} charge={DesiredCharge}");
                            _taskHandle = MelonCoroutines.Start(SalvoDirector.Run(this, SyncPeer));
                            SyncPeer._taskHandle = _taskHandle;
                        }
                    }
                    else {
                        MelonLogger.Msg($"[GC] {_side}: chain start shell={DesiredShell} charge={DesiredCharge} chamber='{Chamber}' charges={Charges}");
                        _taskHandle = MelonCoroutines.Start(TaskChain());
                    }
                }
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
        else {
            FlyRemaining = _gun.CountdownRemainingSeconds(); // 击发后: 游戏炮表倒计时 (红点进度同步游戏指示器)
        }
    }

    /// <summary>动作边界: 刷新实装快照 (机构稳定期读数才可信).</summary>
    internal void RefreshSnapshot() {
        Chamber = _gun.BulletInChamber() ?? "";
        Charges = _gun.LoadedPowderCharges();
    }

    /// <summary>任务链 (单发): 弹药准备 (逐步决策) → TRAK (持续) → 击发后 REST → IDLE. 齐射走 SalvoDirector.
    /// 任务被撤 (DesiredCharge&lt;0) / 手动 / 停止 → 立即退出, 句柄 finally 清 (否则下个任务起不了链).</summary>
    private IEnumerator TaskChain() {
        ResetForTask();
        try {
            while (!_disposed) {
                if (ManualControl || DesiredCharge < 0) yield break; // 撤任务: 别把膛内弹当错弹 DUMP 掉
                var step = DecidePrepStep();
                if (step == null) break; // 弹药就绪 → TRAK
                yield return Exec(step.Value.Action, step.Value.Deadline, step.Value.Routine);
            }
            if (ManualControl || _disposed) yield break;
            yield return RunTrak(); // 3-1 TRAK: 持续追踪直到击发 (AllReady 供 FC 统一火控)
            if (ManualControl || _disposed) yield break;
            yield return RunRest(); // 3-3 REST 复位 → IDLE
        }
        finally { _taskHandle = null; }
    }

    /// <summary>任务开工复位: AllReady/Fired/飞时锁存/双轴稳定器 (单发与齐射导演共用).</summary>
    internal void ResetForTask() {
        AllReady = false;
        Fired = false;
        _latchedFlyTime = float.NaN;
        _eAxis.ResetForTask();
        _hAxis.ResetForTask();
    }

    /// <summary>弹药准备决策一步 (单发与齐射导演共用): 下一步动作, null = 弹药就绪.
    /// 齐射口径: 多药/少药都是错药 (Charges == DesiredCharge).</summary>
    internal SalvoStep? DecidePrepStep() {
        RefreshSnapshot();
        bool chamberOk = Chamber == DesiredShell.ToString();
        bool chargeOk = !SyncCommand ? Charges >= DesiredCharge : Charges == DesiredCharge;
        bool shellWrong = Chamber.Length > 0 && !chamberOk;
        if (shellWrong) return new SalvoStep { Action = GunAction.Dump, Deadline = 25f, Routine = DumpRoutine };
        if (Chamber.Length == 0) {
            if (!_gun.HaveBulletInCylinder(DesiredShell)) {
                return new SalvoStep { Action = GunAction.Selc, Deadline = 20f, Routine = SelcRoutine };
            }
            // 选弹后必须推弹入膛 (固定两步, 否则膛永远空着死循环); 复合步相位跟真实动作
            return new SalvoStep { Action = GunAction.Shrd, Deadline = 28f, Routine = ShrdShldStep };
        }
        if (!chargeOk) {
            if (Charges > DesiredCharge && SyncCommand) { // 齐射多药: 只能整发打掉
                return new SalvoStep { Action = GunAction.Dump, Deadline = 25f, Routine = DumpRoutine };
            }
            // PWDR 拉杆只改"选药" (实装不变), 必须接 LOAD 推药入膛再回决策, 否则 loaded 永远 0 死循环
            return new SalvoStep { Action = GunAction.Pwdr, Deadline = 45f, Routine = PwdrLoadStep };
        }
        // 弹对+药对 = 装填完成 (CanFire 含保险, 装填段不卡它 — 保险由 FC 在 TRAK 段解, 1.x 口径)
        return null; // 弹药就绪
    }

    private IEnumerator ShrdShldStep() {
        yield return ShrdRoutine();
        Action = GunAction.Shld;
        yield return ShldRoutine();
    }

    private IEnumerator PwdrLoadStep() {
        yield return PwdrRoutine();
        Action = GunAction.Load;
        yield return LoadRoutine();
    }

    /// <summary>执行一步 (导演复用 Exec).</summary>
    internal IEnumerator RunStep(SalvoStep s) => Exec(s.Action, s.Deadline, s.Routine);

    /// <summary>游戏装填状态码 → HUD 相位 (真实状态优先; null = 码未覆盖的段, 用声称 Action 兜底).
    /// 码序列 (实机抓): ShellRamming=推弹, SelectPowderCharge=拉药, RamCharges=推药,
    /// CloseShellGuide/FinalSequence=收尾 (2-4 LOAD), BreachLocked=炮闩锁 (2-5 COFM, 一闪而过).</summary>
    public (string code, string name)? ReloadPhase() {
        switch (_gun.ReloadStateKey()) {
            case "ShellRamming": return ("2-2", "BLLD");
            case "SelectPowderCharge": return ("2-3", "PWDR");
            case "RamCharges":
            case "CloseShellGuide":
            case "FinalSequence": return ("2-4", "LOAD"); // 推药+收尾 (1.x WaitLoading 口径)
            case "BreachLocked": return ("2-5", "COFM");  // 炮闩锁定 (1.x 装填确认, 一闪而过)
            default: return null;
        }
    }

    /// <summary>3-1 TRAK: 持续追踪直到击发 (FC 击发或玩家); 任务被撤 (DesiredCharge<0) 退出不开火.
    /// 击发判定看 pendingReload (HasFired): 击发后天然切 REST — CanFire 含保险, TRAK 期保险未解恒 false, 不能当击发信号.</summary>
    internal IEnumerator RunTrak() {
        Action = GunAction.Trak;
        yield return new WaitForSeconds(0.5f); // 装填机构停稳再追 (开头仰角杆不鬼畜)
        while (!_disposed) {
            if (ManualControl || DesiredCharge < 0) yield break;
            yield return new WaitForSeconds(0.04f);
            if (!TrackOnce()) continue; // 本帧追踪推进 (AllReady 内部维护)
            if (_gun.HasFired()) break; // 击发 (pendingReload) → 天然切 REST; CanFire 含保险, TRAK 期保险未解恒 false, 不能当击发信号
        }
        if (_gun.HasFired() && !Fired) {
            Fired = true;
            var sw = _gun.StopwatchLatch();
            if (sw.HasValue && sw.Value.travelTime > 0.01f) _latchedFlyTime = sw.Value.travelTime;
        }
        FlyTime = _latchedFlyTime; // 击发后: 锁存真值供 FC 落点计时
    }

    /// <summary>3-3 REST 复位 → IDLE; AllReady 一并清 (防残留误导下个任务装填期).</summary>
    internal IEnumerator RunRest() {
        yield return Exec(GunAction.Rest, 30f, RestRoutine);
        Action = GunAction.Idle;
        AllReady = false;
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
        bool hLock = !AzimuthSelect || _hAxis.Locked; // 未被选中不算; 选中时 H 稳定态由常驻循环的 _hAxis 维护 (方位全程追)
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

    /// <summary>PWDR 给药: 查池不足锁内买药 (药包杆与计算台无关), 补拉差, 不推药.
    /// 齐射: 锁内一次买够两炮总量 (2×need, 1.x 同款), 双炮并行给药不抢池.
    /// 拉杆前等游戏状态机进 SelectPowderCharge (推弹完全结束, 码确认; 超时兜底继续).</summary>
    private IEnumerator PwdrRoutine() {
        if (!_gun.ReloadStateAtOrAfter("SelectPowderCharge")) yield return _gun.WaitReloadState("SelectPowderCharge");
        int selected = _gun.SelectedPowderCharges();
        int need = DesiredCharge;
        if (selected > need) yield break; // 超出 (齐射多药在决策层 DUMP; 单发多药由 FC 侧 useActual 处理)
        int target = SyncCommand ? 2 * need : need; // 齐射查池按两倍药量
        yield return _purchaseLock.Acquire();
        try {
            // 购买次数上限 (1.x 同款 10 次): 采购始终无效时 FALL, 不静默空转
            int attempts = 0;
            while (_gun.RemainingCharges() + selected < target) {
                yield return _deck.BuyPowders();
                if (_gun.RemainingCharges() + selected >= target) break;
                if (++attempts >= 10) {
                    throw new System.Exception($"PWDR buy powders {attempts} times still pool {_gun.RemainingCharges()} < {target}");
                }
                yield return new WaitForSeconds(0.5f);
            }
        }
        finally { _purchaseLock.Release(); }
        if (selected < need) yield return _gun.PullPowders(need - selected);
        // 拉杆后等游戏"选药"读数到位 (分配器动画完成, 推药按钮才激活); 15s 兜底
        float waited = 0f;
        while (_gun.SelectedPowderCharges() < need && waited < 15f) {
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;
        }
        RefreshSnapshot();
    }

    /// <summary>LOAD 装填: 按装填钮 (药拉够后激活, 游戏据此推药入膛) + 实装确认 (2-5 COFM 弹对药对+炮闩锁).
    /// CanFire 不卡 (含保险, 保险由 FC 在 TRAK 段解).</summary>
    private IEnumerator LoadRoutine() {
        yield return _gun.RamPowder(); // 机构停稳 + 等装填钮激活点击
        float waited = 0f;
        const float cofmTimeout = 25f; // 装填全流程 (RamCharges→BreachLocked) 实测 ~20s, 留裕量
        // COFM 实装确认: 弹对 + 药对 (齐射多药/少药都是错药 ==) + 炮闩锁定 = 整体封膛完成
        while (waited < cofmTimeout) {
            bool shellOk = _gun.BulletInChamber() == DesiredShell.ToString();
            bool powderOk = SyncCommand ? _gun.LoadedPowderCharges() == DesiredCharge : _gun.LoadedPowderCharges() >= DesiredCharge;
            if (shellOk && powderOk && _gun.ReloadStateAtOrAfter("BreachLocked")) break;
            // COFM 卡住诊断 (定位完删)
            if (Time.time - _lastLoadDiag > 2f) {
                _lastLoadDiag = Time.time;
                MelonLogger.Msg($"[GC] {_side}: COFM wait — shell='{_gun.BulletInChamber()}' loaded={_gun.LoadedPowderCharges()} need={DesiredCharge} state='{_gun.ReloadStateKey()}'");
            }
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
                int attempts = 0;
                while (_gun.RemainingCharges() < 1) {
                    yield return _deck.BuyPowders();
                    if (_gun.RemainingCharges() >= 1) break;
                    if (++attempts >= 10) {
                        throw new System.Exception($"DUMP buy powder {attempts} times still pool {_gun.RemainingCharges()}");
                    }
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

    /// <summary>每帧 push 实时弹道指示器数据给 DC (开火前的绿色瞄准十字/LR; 弹种 -1 = 未就绪不渲染).</summary>
    private void PushBallistic(int bulletType) {
        OnBallisticPush?.Invoke(_side, 0f, 0f, 0f, bulletType); // 瞄准点/杀伤圈坐标由 DC 渲染线程按 FC 数据画, GC 只出就绪态
    }

    // ===== 齐射导演接口 (SalvoDirector 驱动两门炮, 1.x 单协程带双炮同款) =====
    /// <summary>弹药准备的一步 (导演每轮各自决策, 两炮动作并行, 都完成才走下一步).</summary>
    internal struct SalvoStep {
        public GunAction Action;
        public float Deadline;
        public System.Func<IEnumerator> Routine;
    }

    /// <summary>齐射导演接管中 (本炮不起自己的链; 任务撤/完成时由 Loop 复位).</summary>
    internal bool SalvoActive;
    /// <summary>导演可用性: 已停/手动 (导演循环退出条件).</summary>
    internal bool Stopped => _disposed || ManualControl;
    /// <summary>导演结束时清任务句柄 (两炮都指向导演协程句柄).</summary>
    internal void ClearTaskHandle() { _taskHandle = null; }
    /// <summary>导演收尾回 IDLE (FALL 保留不覆盖, 交 FC 判定); AllReady 一并清 (防残留误导下个任务).</summary>
    internal void ResetActionIdle() {
        if (Action != GunAction.Fall) Action = GunAction.Idle;
        AllReady = false;
    }
}
