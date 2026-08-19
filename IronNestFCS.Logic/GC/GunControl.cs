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
    public float DesiredDistance = float.NaN;   // 目标距离 km (FC 持续输出; 锁定死区动态口径用)

    // ===== 状态端口 =====
    public string Chamber { get; private set; } = "";   // 膛内弹种 (实装快照)
    public int Charges { get; private set; }            // 实装药数 (实装快照)
    /// <summary>膛内弹种活读 (显示/稳定判定用 — 玩家手动装填时快照不刷新, 活读才准).</summary>
    public string ChamberLive { get { try { return _gun.BulletInChamber() ?? ""; } catch { return ""; } } }
    /// <summary>实装药数活读 (显示用, 同 ChamberLive).</summary>
    public int ChargesLive { get { try { return _gun.LoadedPowderCharges(); } catch { return 0; } } }
    public float Elevation { get; private set; } = float.NaN;      // 实际俯仰 (每帧传感器)
    public float Azimuth { get; private set; } = float.NaN;        // 实际方位回读 (每帧)
    public GunAction Action { get; private set; } = GunAction.Idle;
    public float FlyTime { get; private set; } = float.NaN;        // 飞行时间 (游戏自解: 瞄准期 PredictedImpactTime / 击发后锁存)
    public bool AllReady { get; private set; }                     // 弹药确认且追踪稳定; 不稳定回退
    public bool Fired { get; private set; }                        // 本发已击发 (GC 内部防重; 不对外 — FC 用 FlyRemaining 作击发确认)
    public bool KernelMode { get; private set; }                   // 内核态: 硬件动作执行中 (指令只记录不生效)
    /// <summary>游戏 CANFIRE 信号 (实测不含保险: 装填完成 — 弹+药+炮闩锁 — 未开保险即 True;
    /// 就是"俯仰手柄解锁"的综合信号): 绿十字/落弹点显示门槛 (1.x 同口径, 弹没装好不出落点).</summary>
    public bool CanFire { get { try { return _gun.CanFire(); } catch { return false; } } }
    /// <summary>击发后游戏炮表剩余秒数 (与游戏自身飞行指示器同一数据源, 消除检测时间差); 未倒计时 NaN.</summary>
    public float FlyRemaining { get; private set; } = float.NaN;

    // ===== 内部 =====
    private readonly TrackAxis _eAxis = new(0.05f, 0.002f, 0.05f, 2.0f, 0f, 0f, 0f, 16); // E: 纯前馈 — 游戏天顶星环无超调, 外圈修正全拆; 锁定死区 0.05° (动目标残差波动 ±0.05, 0.01 太苛刻)
    private readonly TrackAxis _hAxis = new(0.05f, 0.002f, 0.3f, 4.0f, 0f, 0f, 0f, 16);  // H: 同上
    // 伺服滞后外推 (游戏环一阶一型, 动目标追踪有固定相位滞后): 设定值 = 目标 + EWMA 斜率 × 外推帧数
    private float _lastTargetE = float.NaN, _lastTargetA = float.NaN;
    private float _slopeE = float.NaN, _slopeA = float.NaN; // EWMA 斜率 (单帧差分 × ExtrapFrames 放大解算噪声, 设定值跳)
    private const float ExtrapFramesE = 3f;  // E 环快: 5 帧过头 (误差负漂), 3 帧
    private const float ExtrapFramesA = 4f;  // H 环慢 (炮塔 4°/s); 5 帧实测小滞后 → 4
    private const float SlopeAlphaE = 0.3f;  // E 斜率平滑
    private const float SlopeAlphaA = 0.5f;  // H 斜率平滑加大 (过最近点角速度急变段跟快点)
    private object? _loopHandle;
    private object? _taskHandle;
    private float _latchedFlyTime = float.NaN; // 击发后锁存总飞时
    private bool _lastHasFired;                // pendingReload 上升沿检测 (击发自检)
    private bool? _lastCanFire;                // CanFire 沿检测 (装填完成 → 击发沿复位)
    private bool _impactFlying;                // 落点指示器飞行中 (GC 持续传导倒计时剩余)
    private bool _sawCountdown;                // 已见过倒计时真值 (落地判定: 见过后又 NaN = 落地)
    private float _impactFiredAt;              // 击发确认时刻 (倒计时未启动兜底)
    private bool _disposed;

    // 实时弹道指示器 push 目标 (FcsModule 注入 DC 回调; GC 全权): (瞄准点, 杀伤圈, 弹种, AllReady, 飞时)
    public delegate void BallisticPush(LeftRight side, float aimX, float aimY, float killRadiusKm, int bulletType, bool ready, float flyTime);
    public BallisticPush? OnBallisticPush;

    // ===== GC → DC: 落点指示器 (FcsModule 注入 DC 回调) =====
    /// <summary>击发确认 (锁存总飞时已取到): DC 画落点线 — 落点 = GC 冻结的开火前最后瞄准点 (板面坐标, 开火后游戏把标记拉回铁巢, 必须 GC 侧冻结).</summary>
    public System.Action<LeftRight, float, float, BulletType, float>? OnImpactFired;
    /// <summary>飞行期间持续传导游戏倒计时剩余 (与游戏指示器逐帧同步); 0 = 落地隐藏.</summary>
    public System.Action<LeftRight, float>? OnImpactRemain;

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
            // 锁定死区动态口径 (弹道学): 落点偏移 ≤ 混凝土弹 (DRIL) 杀伤半径 / 5 → δ(°) = (R/5)/d × 57.3
            // 近距离放宽 远距离收紧; clamp 防病态 (R=0.07 km 见 GameInternals.md)
            if (!float.IsNaN(DesiredDistance) && DesiredDistance > 0f) {
                float db = ShellData.KillRadiusKm(BulletType.DRIL) / 5f / DesiredDistance * Mathf.Rad2Deg;
                db = Mathf.Clamp(db, 0.02f, 0.3f);
                _eAxis.LockDeadband = db;
                _hAxis.LockDeadband = db;
            }
            // 实时弹道指示器 (GC 全权 push, 与 FC 解算/任务/手动无关): 内部按 CanFire 门控 (装填完成即显示)
            PushBallistic();
            // 装填完成 (CanFire 上升沿): 击发沿基准复位 — 手动连打多发时每发都是新的击发事件
            var cf = _gun.CanFire();
            if (cf != _lastCanFire) {
                if (cf) {
                    Fired = false;
                    _lastHasFired = false;
                    _latchedFlyTime = float.NaN;
                }
                _lastCanFire = cf;
            }
            // 击发自检 (手动/自动通用, 不依赖 FC): pendingReload 上升沿 + 膛空 (CanFire 已掉) = 开火瞬间 →
            // 锁存炮表真值 + 通知 DC 画落点线; 手动开火 (无 FC 收尾) 同样出飞行轨迹
            bool firedNow = _gun.HasFired();
            if (firedNow && !_lastHasFired && !Fired && !cf) {
                Fired = true; // 内部防重 (不对外 — FC 用 FlyRemaining 作击发确认)
                var sw = _gun.StopwatchLatch();
                if (sw.HasValue && sw.Value.travelTime > 0.01f) _latchedFlyTime = sw.Value.travelTime;
                else _latchedFlyTime = FlyTime; // 倒计时未启动 (fireDelay): 瞄准期预测值兜底
                FlyTime = _latchedFlyTime;      // 击发后: 锁存真值 (瞄准期活读停更, 下一帧 ReadSensors 走 else)
                // 弹种: 最后非空膛内弹 (击发瞬间膛已空, 活读拿不到 — 开火按最后一次落点指示走); 兜底 DesiredShell
                BulletType firedShell = System.Enum.TryParse<BulletType>(_lastChamberLive, out var fb) ? fb : DesiredShell;
                _impactFlying = true;
                _sawCountdown = false;
                _impactFiredAt = Time.time;
                OnImpactFired?.Invoke(_side, _lastAimLive.x, _lastAimLive.y, firedShell, _latchedFlyTime);
            }
            _lastHasFired = firedNow;
            // 飞行期间: 持续传导游戏倒计时剩余 (与游戏指示器同步); 落地 (已见过倒计时后变 NaN) 传 0 隐藏
            if (_impactFlying) {
                float r = _gun.CountdownRemainingSeconds();
                if (!float.IsNaN(r) && r > 0f) {
                    _sawCountdown = true;
                    OnImpactRemain?.Invoke(_side, r);
                }
                else if (_sawCountdown && float.IsNaN(r)) {
                    _impactFlying = false;
                    OnImpactRemain?.Invoke(_side, 0f);
                }
                else if (!_sawCountdown && Time.time - _impactFiredAt > 3f) {
                    _impactFlying = false; // 表没绑/倒计时没启动: 兜底结束 (DC 侧本地计时兜底进度)
                }
            }
            if (ManualControl) {               // 手动: 立即停手, 残局交玩家, 线程照跑 (落点指示不受手动影响 — 已在上方 push)
                TryStop(_taskHandle);
                _taskHandle = null;
                KernelMode = false;
                AllReady = false;
                Action = GunAction.Idle;
                SalvoActive = false;
                continue;
            }
            // 方位追踪不受装弹影响: 被选中即全程追 (从派发起, 不等 LOAD/TRAK).
            // 齐射: 右炮 DesiredX 输入忽略, 按左炮数据走 (前向接口规范; 同任务解算相同, 语义对齐)
            float desiredA = DesiredAzimuth;
            if (SyncCommand && SyncPeer != null && _side == LeftRight.Right) desiredA = SyncPeer.DesiredAzimuth;
            if (AzimuthSelect && !float.IsNaN(desiredA) && !float.IsNaN(Azimuth)) {
                // 伺服滞后外推 (同 E): 设定值 = 目标 + EWMA 斜率 × ExtrapFrames
                float rawSlopeA = float.IsNaN(_lastTargetA) ? 0f : desiredA - _lastTargetA;
                _lastTargetA = desiredA;
                _slopeA = float.IsNaN(_slopeA) ? rawSlopeA : _slopeA + SlopeAlphaA * (rawSlopeA - _slopeA);
                float hCorr = _hAxis.Step(Mathf.DeltaAngle(Azimuth, desiredA), Azimuth);
                if (!_hAxis.GaveUp && FcsBus.TurretSet != null) FcsBus.TurretSet(desiredA + _slopeA * ExtrapFramesA + hCorr);
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
                    ResetForTask();            // 追之前清 PID 缓存 (链内开头也有, 这里提前到起链帧 — H 轴派发帧已开追)
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
        _lastHasFired = false;        // 残留的击发沿不许带到下个任务
        _impactFlying = false;
        _sawCountdown = false;
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
        // 弹对+药对 = 装填完成 (比 CanFire 更精细的实装校验; CanFire 实测不含保险本可直接用, 但弹药逐项对号更稳)
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
            case "BreachLocked": return ManualControl ? null : ("2-5", "COFM"); // 炮闩锁定 (1.x 装填确认, 一闪而过); 手动: 跳过 2-5 确认 (玩家自己装填, mod 不确认)
            default: return null;
        }
    }

    /// <summary>3-1 TRAK: 持续追踪直到击发 (FC 击发或玩家); 任务被撤 (DesiredCharge<0) 退出不开火.
    /// 击发判定看 pendingReload (HasFired): 击发瞬间置位最准 — CanFire 是装填完成信号 (不含保险),
    /// 击发后膛空也会掉, 时序上比 pendingReload 晚, 不能当击发信号.</summary>
    internal IEnumerator RunTrak() {
        Action = GunAction.Trak;
        // 装填机构停稳 + 炮管运动停止再追 (装填完炮管有回落动作, 追早了对摇杆打架 → 鬼畜);
        // 不看码 (BreachLocked 一闪而过), 用机构信号 + 小缓冲
        yield return _gun.WaitForReloadReady();
        yield return new WaitForSeconds(0.3f);
        while (!_disposed) {
            if (ManualControl || DesiredCharge < 0) yield break;
            yield return new WaitForSeconds(0.04f);
            if (!TrackOnce()) continue; // 本帧追踪推进 (AllReady 内部维护)
            if (_gun.HasFired()) break; // 击发 (pendingReload) → 天然切 REST (锁存/落点通知由常驻 Loop 击发自检做)
        }
    }

    /// <summary>3-3 REST 复位 → IDLE; AllReady 一并清 (防残留误导下个任务装填期).</summary>
    internal IEnumerator RunRest() {
        yield return Exec(GunAction.Rest, 30f, RestRoutine);
        Action = GunAction.Idle;
        AllReady = false;
    }

    /// <summary>TRAK 单帧推进: E 恒追 (TrackAxis 天顶星伺服 = 设 1 帧预测目标 + 修正);
    /// H 仅在被 AzimuthSelect 选中时追. 双轴稳定 + 弹药确认 → AllReady; 不稳定即回退.
    /// 齐射: 右炮跟随左炮设置 (按左炮数据走, 游戏内齐射联动) — 右炮 DesiredX 输入忽略, E 不单独控制 PID,
    /// 直接设左炮的设定值; 锁定判定 = 右炮实际仰角跟上目标 (死区内).</summary>
    private bool TrackOnce() {
        float targetE = DesiredElevation;
        float targetA = DesiredAzimuth;
        if (SyncCommand && SyncPeer != null && _side == LeftRight.Right) { targetE = SyncPeer.DesiredElevation; targetA = SyncPeer.DesiredAzimuth; }
        bool eLock = false;
        if (!float.IsNaN(targetE)) {
            // 伺服滞后外推: 设定值 = 目标 + 帧间变化率 × ExtrapFrames (动目标相位滞后补偿);
            // 斜率 EWMA 平滑 (单帧差分 × ExtrapFrames 放大解算噪声, 设定值跳 → 掉锁)
            float rawSlopeE = float.IsNaN(_lastTargetE) ? 0f : targetE - _lastTargetE;
            _lastTargetE = targetE;
            _slopeE = float.IsNaN(_slopeE) ? rawSlopeE : _slopeE + SlopeAlphaE * (rawSlopeE - _slopeE);
            float setTarget = targetE + _slopeE * ExtrapFramesE;
            if (SyncCommand && SyncPeer != null && _side == LeftRight.Right) {
                // 齐射右炮: 跟随左炮设定值, 不跑自己的 PID
                float set = !float.IsNaN(SyncPeer.LastElevationSet) ? SyncPeer.LastElevationSet : setTarget;
                _gun.SetElevationValue(set);
                LastElevationSet = set;
                eLock = Mathf.Abs(Elevation - targetE) <= _eAxis.Deadband;
            }
            else {
                float corr = _eAxis.Step(targetE - Elevation, Elevation);
                if (!_eAxis.GaveUp) _gun.SetElevationValue(setTarget + corr);
                LastElevationSet = setTarget + corr;
                eLock = _eAxis.Locked;
            }
        }
        // 选中: H 稳定态由常驻循环的 _hAxis 维护; 未选中: 炮塔归另一炮 — 等炮塔转到本炮目标方位 (死区内) 才算稳,
        // 不然两炮不同任务时未选中炮 H 没对准也报 AllReady, FC 会打飞
        bool hLock = AzimuthSelect ? _hAxis.Locked
            : float.IsNaN(targetA) || Mathf.Abs(Mathf.DeltaAngle(Azimuth, targetA)) <= _hAxis.Deadband;
        // 弹药判定活读 (TRAK 期机构稳定, 活读可信; 玩家介入时快照可能陈旧)
        bool ammoReady = _gun.BulletInChamber() == DesiredShell.ToString()
            && (!SyncCommand ? _gun.LoadedPowderCharges() >= DesiredCharge : _gun.LoadedPowderCharges() == DesiredCharge);
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
        // 空膛转弹仓前: 等残留实装计数清零 (上一发击发后的自动循环; 1.x 同款) —
        // 炮闩打开后机构还要 ~3s 复位, 计数清零才算复位完, 光看炮闩打开就转弹仓会过早点击
        float waitLoaded = 0f;
        while (_gun.LoadedPowderCharges() > 0 && waitLoaded < 10f) {
            yield return new WaitForSeconds(0.5f);
            waitLoaded += 0.5f;
        }
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
    /// 不卡 CanFire: 实测不含保险 (装完未开保险即 True) 本可直接等, 但逐项实装校验对号更稳.</summary>
    private IEnumerator LoadRoutine() {
        yield return _gun.RamPowder(); // 机构停稳 + 等装填钮激活点击
        float waited = 0f;
        const float cofmTimeout = 25f; // 装填全流程 (RamCharges→BreachLocked) 实测 ~20s, 留裕量
        // COFM 实装确认: 弹对 + 药对 (齐射多药/少药都是错药 ==) + 炮闩锁定 = 整体封膛完成
        while (waited < cofmTimeout) {
            bool shellOk = _gun.BulletInChamber() == DesiredShell.ToString();
            bool powderOk = SyncCommand ? _gun.LoadedPowderCharges() == DesiredCharge : _gun.LoadedPowderCharges() >= DesiredCharge;
            if (shellOk && powderOk && _gun.ReloadStateAtOrAfter("BreachLocked")) break;
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

    private Transform? _impact; // 游戏实时落点标记 (绿十字跟它走, 1.x 同款; GC 全权)
    /// <summary>本炮最近一次仰角设定值 (齐射右炮跟随左炮设置用 — 右炮直接设左炮的 PID 输出).</summary>
    public float LastElevationSet = float.NaN;
    private string _lastChamberLive = ""; // 最后非空膛内弹活读 (击发瞬间膛已空, 弹种用它 — 开火按最后一次落点指示走)
    private Vector2 _lastAimLive;         // CanFire 期间每帧更新的瞄准点 (板面坐标; 击发瞬间冻结 — 开火后游戏把落点标记拉回铁巢, 不能跟进)
    private bool _hasAimLive;

    /// <summary>每帧 push 实时弹道指示器数据给 DC (GC 全权: 瞄准点/杀伤圈/弹种/AllReady/飞时).
    /// 瞄准点每帧都传真实值 (弹种 -1 只管绿十字显隐, DC 侧瞄准点缓存必须跟着炮走 —
    /// 击发瞬间 CanFire 已掉, 落点线终点 = 开火前最后有效瞄准点).
    /// 弹种 = CanFire 门控 (实测不含保险: 装填完成 — 弹+药+炮闩锁 — 未开保险即 True),
    /// 不跟任务/相位走 — F9 后/无任务时膛内有弹也显示 (弹种读膛内实弹活读).</summary>
    private void PushBallistic() {
        if (_impact == null)
            _impact = GameObject.Find(_side == LeftRight.Left ? "GunLeft_ImpactMarker" : "GunRight_ImpactMarker")?.transform;
        if (_impact == null) return;
        // 膛内弹种活读 (不读 Chamber 快照 — 手动态快照不刷新; 显示信号无"动作中不可信"问题)
        string chamber = _gun.BulletInChamber() ?? "";
        if (chamber.Length > 0) _lastChamberLive = chamber; // 缓存最后非空膛内弹
        BulletType shell = !CanFire ? (BulletType)(-1)
            : System.Enum.TryParse<BulletType>(chamber, out var b) ? b : (BulletType)(-1);
        var g = _impact.localPosition; // 游戏网格坐标 → 板面 (1.x: MapBottomLeft + grid × MapCellSize)
        float bx = GeoMap.MapBottomLeft.x + g.x * GeoMap.MapCellSize;
        float by = GeoMap.MapBottomLeft.y + g.y * GeoMap.MapCellSize;
        if (CanFire) { // 只在装填完成期间跟进 — 击发后游戏把标记拉回铁巢, 冻结住开火前最后落点
            _lastAimLive = new Vector2(bx, by);
            _hasAimLive = true;
        }
        OnBallisticPush?.Invoke(_side,
            bx, by,
            shell >= 0 ? ShellData.KillRadiusKm(shell) : 0f, (int)shell, AllReady, FlyTime);
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
