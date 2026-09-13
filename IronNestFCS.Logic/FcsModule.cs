using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IronNestFCS.Logic;

/// <summary>
/// Logic 程序集的入口 (2.0): 开机自检装配链 + 四模块 (RD/DC/FC/GC) 接线, 旧调度层已停用 (LegacyDisabled).
/// 数据链: RD → DC_U → FC → GC → DC_D; F9 热重载 = Shutdown (全模块 Stop + 清端口) → 重 Initialize.
/// 开机自检 = 跨帧装配状态机 (进任务, Update 驱动不用协程): System Init 重试硬件绑定 (实体就绪等待),
/// 各模块行 [DONE] = 真实装配完成; 失败行 [FAIL] 红显冻结面板, 回主菜单 (FMR 消失) → NO SIGNAL 接管.
/// </summary>
public class FcsModule : IFcsModule
{
    private readonly FSC fcs = new(); // 硬件绑定壳 (旧调度层已清退)

    private const float BootGap = 0.25f;       // 信息行显现后的推进间隙 (s)
    private const float BootBindPause = 0.4f;  // 模块行名字 → 补 [DONE] 的最短停顿 (s)
    private const float BootRetry = 0.25f;     // 模块绑定失败重试间隔 (s)
    private const int BootFailCap = 10;        // 模块绑定失败重试上限
    private const float BootInitRetry = 0.5f;  // System Init 绑定重试间隔 (s)
    private const float BootInitCap = 30f;     // System Init 实体等待上限 (s)
    private const float BootBenchWait = 2f;    // Bench 采样等待 (s, ping 节奏: 先 ... 等回复再报数)

    private BootLog? _boot;
    private GunControl? gunL;
    private GunControl? gunR;
    private CoroutineLock? gcPurchaseLock;
    private CoroutineLock? fireLock; // 统一火控锁 (FC 专用 — GC 已不自行平射)
    private Radar? radar2;
    private DisplayControl? display;
    private SandboxRenderer? renderer2;
    private FireControl? fireControl;
    private FcsHud? hud;
    private ScenePanel? scenePanel;
    private float _lastAliveCheck;
    private float _bootStageT;
    private float _bootRetryT;
    private int _bootStage;
    private int _bootFails;
    private bool _booting;
    private bool _bootFailed;
    private bool _shutdown;

    public bool Initialize()
    {
        // 主菜单/无任务场景: 不开机 (NO SIGNAL 占位); 进任务 (FMR 在案) = 开机自检装配链
        if (GameObject.Find("Fire Mission Root") == null) return false;
        _booting = true;
        _bootStage = 0;
        _bootFails = 0;
        _bootStageT = Time.time;
        _bootRetryT = Time.time;
        _boot = new BootLog();
        MelonLogger.Msg("[FCS] boot started");
        return true;
    }

    public void Update()
    {
        if (_shutdown) {
            // 开机失败冻结面板: 仍盯场景 — FMR 消失 (回主菜单) 才清屏, NO SIGNAL 接管
            if (_bootFailed && GameObject.Find("Fire Mission Root") == null) ClearBoot();
            return;
        }
        if (_booting) { BootTick(); return; }
        // 场景存活检测 (2s): 回主菜单/场景卸载时铁巢棋子消失 → 自清全部模块, NO SIGNAL 占位接管;
        // 再进任务场景由 Host 的 OnSceneWasLoaded 自动重载 (0.5s 后 Reload → 开机自检 → 重新装配)
        if (hud != null && Time.time - _lastAliveCheck > 2f) {
            _lastAliveCheck = Time.time;
            // 任务实体根 = 场景判据 (实测: 回主菜单时 Fire Mission Root 消失, 沙盘/铁巢棋子仍常驻)
            bool fmr = GameObject.Find("Fire Mission Root") != null;
            if (!fmr) {
                _shutdown = true;
                Shutdown();
                MelonLogger.Msg("[FCS] scene unloaded, HUD → NO SIGNAL");
                return;
            }
        }
        // DC → FC 火控请求桥接 (右键/扫荡/令牌离图)
        if (display != null && fireControl != null) {
            foreach (var req in display.DrainRequests()) fireControl.RequestTask(req);
            fireControl.AutoFire = display.AutoFire;
            fireControl.AutoTask = display.AutoTask;
        }
        scenePanel?.Update();

        var kb = Keyboard.current;
        if (kb == null || fireControl == null) return;
        bool ctrl = kb.ctrlKey.isPressed;
        if (kb.numpad0Key.wasPressedThisFrame || (ctrl && kb.digit0Key.wasPressedThisFrame)) {
            // 扫荡开关: 只有 Start 之后才能点 (Full-Auto 档, 强制自动开火)
            if (fireControl.Mode != FireMode.Manual && display != null) {
                display.SetAutoTask(!display.AutoTask);
                if (display.AutoTask) display.AutoFire = true;
            }
            return;
        }
        if (kb.numpadMinusKey.wasPressedThisFrame) { AdjustAllValves(0f); return; }
        if (kb.numpadPlusKey.wasPressedThisFrame) { AdjustAllValves(999f); return; }
    }

    // ===== 开机自检装配链 =====

    /// <summary>开机自检状态机 (Update 驱动, 不用协程 — 绕开 WaitForSeconds 调度异常).
    /// 步骤: 0 System Init (重试硬件绑定 = 实体就绪等待) → 1 System Loaded → 2 Enable Peripherals →
    /// 3-6 四模块按依赖序装配 (GC → DC_U → FC → DC_D) → 7 Peripherals Enabled → 8 SAR 数据链 → 9 Bench 实测 (ping 节奏) →
    /// 10 FINAL CHECK → 11 Load Application → HUD 接管. 每行 [DONE] = 该阶段真实装配完成.</summary>
    private void BootTick()
    {
        // 开机中回主菜单: FMR 消失 → 中断自清 (下一帧清屏, NO SIGNAL 接管)
        if (GameObject.Find("Fire Mission Root") == null) { AbortBoot(); return; }
        var lines = _boot!.Lines;
        float t = Time.time - _bootStageT;
        switch (_bootStage) {
            case 0: // System Init ... — 硬件绑定重试 (失败不算故障: 实体未就绪), 30s 上限 → [FAIL]
                if (lines[0].State == BootLineState.Hidden) {
                    lines[0].ActiveText = $"{Stamp()} [INFO] System Init ...";
                    lines[0].State = BootLineState.Active;
                    fcs.TryBind(); // 显现帧即首试
                    _bootRetryT = Time.time;
                }
                if (!fcs.IsBound) {
                    if (t > BootInitCap) { lines[0].State = BootLineState.Fail; lines[0].FailText = lines[0].ActiveText.Replace("...", "[FAIL]"); AbortBoot(); return; }
                    if (Time.time - _bootRetryT > BootInitRetry) { _bootRetryT = Time.time; fcs.TryBind(); }
                    break;
                }
                if (t > BootGap) Advance();
                break;
            case 1: // System Loaded — 全局端口总线 + 锁 + 射表 (硬件已绑定)
                if (lines[1].State == BootLineState.Hidden) {
                    lines[1].ActiveText = $"{Stamp()} [INFO] System Loaded For FCS 2.0.0";
                    lines[1].State = BootLineState.Active;
                    gcPurchaseLock = new CoroutineLock();
                    fireLock = new CoroutineLock();
                    WirePorts();
                    ShellData.Init(); // 扫游戏 ShellDefinition: 杀伤半径/速度曲线 (杀伤圈与射表数据源)
                }
                if (t > BootGap) Advance();
                break;
            case 2: // Enable Peripherals — 外设装配前奏
                if (lines[2].State == BootLineState.Hidden) { lines[2].ActiveText = $"{Stamp()} [CORE] Enable Peripherals ..."; lines[2].State = BootLineState.Active; }
                if (t > 0.2f) Advance();
                break;
            case 3: BootModuleStep(3, TryBindGuns); break;
            case 4: BootModuleStep(4, TryBindDisplay); break;
            case 5: BootModuleStep(5, TryBindFireControl); break;
            case 6: BootModuleStep(6, TryBindRenderer); break;
            case 7:
                if (lines[7].State == BootLineState.Hidden) { lines[7].ActiveText = $"{Stamp()} [CORE] Peripherals Enabled"; lines[7].State = BootLineState.Active; }
                if (t > 0.2f) Advance();
                break;
            case 8: // SAR 数据链 — 真判据: 雷达已装配且板面注入完成 (雷达在 DC_U 阶段已建, 此行只验线)
                BootModuleStep(8, () => radar2 != null && radar2.MapSurfaceRef != null, l => {
                    string p = $"{Stamp()} [CORE] Connect To SAR DataLine";
                    l.ActiveText = p;
                    l.DoneText = BootLog.PadDone(p);
                    l.FailText = BootLog.PadFail(p);
                });
                break;
            case 9: // Bench — 数据链间隔实测 (ping 节奏: ... 等 ~2s 采样, 报最后一次粗跟 tick 间隔)
                if (lines[9].State == BootLineState.Hidden) {
                    lines[9].ActiveText = $"{Stamp()} [INFO] DataLine Bench ...";
                    lines[9].State = BootLineState.Active;
                }
                if (t > BootBenchWait) {
                    float dl = radar2 != null ? radar2.LastIntervalMs : 0f;
                    string suffix = $" DL:{dl:0}ms PL:0.0%";
                    string p = $"{Stamp()} [INFO] DataLine Bench ";
                    lines[9].ActiveText = p + new string('-', Mathf.Max(0, 64 - p.Length - suffix.Length)) + suffix;
                    Advance();
                }
                break;
            case 10: // FINAL CHECK — 全链路核查 (硬件+五模块+端口), 不过 → [FAIL] 冻结
                if (lines[10].State == BootLineState.Hidden) { lines[10].ActiveText = $"{Stamp()} [CORE] FINAL CHECK ..."; lines[10].State = BootLineState.Active; }
                if (t > 0.45f) {
                    if (!FinalCheck()) { lines[10].State = BootLineState.Fail; lines[10].FailText = lines[10].ActiveText.Replace("...", "[FAIL]"); AbortBoot(); return; }
                    Advance();
                }
                break;
            case 11: // Load Application — 场景交互层装配 (最后一块) → HUD 接管
                if (lines[11].State == BootLineState.Hidden) {
                    lines[11].ActiveText = $"{Stamp()} [INFO] Load Application ...";
                    lines[11].State = BootLineState.Active;
                    scenePanel = new ScenePanel { Dc = display!, Fc = fireControl!, RadarPort = radar2! };
                    scenePanel.Build();
                }
                if (t > 0.45f) {
                    _booting = false;
                    _boot = null;
                    MelonLogger.Msg("[FCS] boot complete, HUD live");
                }
                break;
        }
    }

    /// <summary>模块装配步: 显现名字 → 装配 → 停顿后补 [DONE]; 连续失败超上限 → [FAIL] 中断.</summary>
    private void BootModuleStep(int line, System.Func<bool> bind, System.Action<BootLog.Line>? setup = null)
    {
        var l = _boot!.Lines[line];
        float t = Time.time - _bootStageT;
        if (l.State == BootLineState.Hidden) {
            setup?.Invoke(l);
            l.State = BootLineState.Active;
            _bootFails = 0;
        }
        if (l.State == BootLineState.Active) {
            if (bind()) {
                if (t > BootBindPause) l.State = BootLineState.Done;
            } else if (Time.time - _bootRetryT > BootRetry) {
                _bootRetryT = Time.time;
                if (++_bootFails > BootFailCap) { l.State = BootLineState.Fail; AbortBoot(); return; }
            }
            return;
        }
        if (t > BootBindPause + BootGap) Advance();
    }

    private void Advance()
    {
        _bootStage++;
        _bootStageT = Time.time;
        _bootRetryT = Time.time;
    }

    /// <summary>开机中断 (FMR 消失 / 绑定失败): 面板冻结在 [FAIL] (画面保留), 全模块自清.</summary>
    private void AbortBoot()
    {
        MelonLogger.Msg("[FCS] boot aborted, panel frozen");
        var boot = _boot; // Shutdown 会清开机状态 — 冻结画面保留
        _booting = false;
        _shutdown = true;
        Shutdown();
        _boot = boot;
        _bootFailed = true;
    }

    /// <summary>开机失败面板清屏 (FMR 消失 = 回主菜单): NO SIGNAL 接管.</summary>
    private void ClearBoot()
    {
        _bootFailed = false;
        _boot = null;
        MelonLogger.Msg("[FCS] boot panel cleared, HUD → NO SIGNAL");
    }

    /// <summary>开机行时间戳 (真实墙钟, xx.xx.xx — 全限定避开 Il2Cpp 同名类型).</summary>
    private static string Stamp() => System.DateTime.Now.ToString("HH.mm.ss");

    // ===== 装配步 (原 WireModules 拆分, 依赖序 = 显示序) =====

    /// <summary>全局端口总线接线 (炮塔/击发钮 — FC 直控炮塔, GC 只读/被选中时写修正).</summary>
    private void WirePorts()
    {
        FcsBus.TurretRead = () => fcs.Turret.CurrentAngle();
        FcsBus.TurretVelRead = () => fcs.Turret.RotationVelocity();
        FcsBus.TurretSet = a => fcs.Turret.SetDesiredRotation(a);
        FcsBus.Fire = () => fcs.TriggerConsole.Fire();
    }

    /// <summary>GC 装配: 两炮执行器构造 + 解算台/锁注入 + Start (依赖: 硬件绑定/端口/锁 — 前步已就位).</summary>
    private bool TryBindGuns()
    {
        if (gunL != null && gunR != null) return true;
        var deck = new PurchaseDeck();
        deck.TryBind();
        gunL = new GunControl(LeftRight.Left, fcs.LeftGun, deck, gcPurchaseLock!);
        gunR = new GunControl(LeftRight.Right, fcs.RightGun, deck, gcPurchaseLock!);
        gunL.SyncPeer = gunR;
        gunR.SyncPeer = gunL;
        // 解算台注入 (装填期 PWDR 前 Calculate 刷新分配器读数缓存; 锁与 FC 共用同一把 — 计算台是共享硬件)
        gunL.Calculator = fcs.BallisticCalculator;
        gunR.Calculator = fcs.BallisticCalculator;
        gunL.CalculatorLock = fireLock!;
        gunR.CalculatorLock = fireLock!;
        gunL.Start();
        gunR.Start();
        return true;
    }

    /// <summary>DC_U 装配: 雷达 (板面注入) + 沙盘显示 (图标层) + Start; Holography (DC_D) 在下一阶段接显示回调.</summary>
    private bool TryBindDisplay()
    {
        if (radar2 != null || display != null) return true;
        radar2 = new Radar();
        radar2.MapSurfaceRef = GameObject.Find("Draggable Surface")?.transform; // 信号注入载体母体 (板面局部系)
        radar2.Start();
        var surface = radar2.MapSurfaceRef;
        var nest = GameObject.Find("Player Turret Piece")?.transform;
        display = new DisplayControl {
            RadarPort = radar2,
            NestRef = nest,
            MapSurfaceRef = surface,
        };
        display.Start();
        return true;
    }

    /// <summary>FC 装配: 火控内核 + HUD; 与 Holography 的 push 回调在 DC_D 阶段接 (renderer2 尚未构造).</summary>
    private bool TryBindFireControl()
    {
        if (fireControl != null) return true;
        fireControl = new FireControl {
            GunL = gunL!,
            GunR = gunR!,
            ConsolePort = fcs.TriggerConsole,
            Calculator = fcs.BallisticCalculator,
            FireLock = fireLock!,
            NestRef = GameObject.Find("Player Turret Piece")?.transform,
            MapSurfaceRef = radar2!.MapSurfaceRef,
            DcPort = display!, // 目标参数表直读 (位置/轨迹参数; Entity 失效 → 撤任务)
        };
        fireControl.Start();
        display!.FcPort = fireControl;
        hud = new FcsHud { Fc = fireControl, GunL = gunL!, GunR = gunR! };
        return true;
    }

    /// <summary>DC_D 装配: Holography 渲染器 + 全部 push 回调接线 (GC 弹道/落点, FC 队列/解算, DC 图标) + Start.</summary>
    private bool TryBindRenderer()
    {
        if (renderer2 != null) return true;
        renderer2 = new SandboxRenderer {
            MapSurfaceRef = radar2!.MapSurfaceRef,
            FireMissionRootRef = GameObject.Find("Fire Mission Root")?.transform,
            NestRef = GameObject.Find("Player Turret Piece")?.transform,
        };
        var r2 = renderer2;
        var dc = display!;
        dc.OnIconSpawn = t => r2.SpawnIcon(t);
        dc.OnIconRemove = go => r2.RemoveIcon(go);
        r2.TwsActive = () => dc.Tws; // 矢量符显隐跟 TWS 开关
        // GC → DC_D: 实时弹道 push (GC 全权: 瞄准点/杀伤圈/弹种/AllReady/飞时)
        gunL!.OnBallisticPush = (side, x, y, r, b, ready, fly) => r2.PushBallistic(side, new Vector2(x, y), r, b, ready, fly);
        gunR!.OnBallisticPush = (side, x, y, r, b, ready, fly) => r2.PushBallistic(side, new Vector2(x, y), r, b, ready, fly);
        // GC → DC_D: 落点指示器 (GC 击发确认 → DC 画线; Flight 引用直读 — 剩余/落地由 DC 每帧读字段, 不再逐帧传导)
        gunL.OnImpactFired = (side, x, y, shell, fly, flight) => r2.ImpactFired(side, x, y, shell, fly, flight);
        gunR.OnImpactFired = (side, x, y, shell, fly, flight) => r2.ImpactFired(side, x, y, shell, fly, flight);
        fireControl!.OnQueueChanged = fc => r2.UpdateQueueIndicator(fc);
        fireControl.OnFireSolution = (side, target, aim, v, a, j, t) => r2.UpdateFireSolution(side, target, aim, v, a, j, t);
        r2.Start();
        return true;
    }

    /// <summary>FINAL CHECK 判据: 硬件绑定在案 + 五模块非空 + 端口齐.</summary>
    private bool FinalCheck()
    {
        return fcs.IsBound
            && gunL != null && gunR != null
            && radar2 != null && display != null && renderer2 != null
            && fireControl != null && hud != null
            && FcsBus.TurretRead != null && FcsBus.TurretVelRead != null
            && FcsBus.TurretSet != null && FcsBus.Fire != null;
    }

    public void OnGui()
    {
        if (_boot != null) { FcsHud.DrawBoot(_boot); return; } // 开机自检 (装配中/失败冻结)
        if (hud != null) { hud.OnGui(); return; }
        FcsHud.DrawNoSignal(); // 实体未初始化 (1.0.7 的 "wait for ..." 位): 军航 HUD 风 NO SIGNAL 占位
    }

    public void Shutdown()
    {
        _booting = false;
        _bootFailed = false;
        _boot = null;
        // 各 Stop 独立 try: 一个炸了不漏全链 (残留实例 → 协程叠加事故防复发)
        try { scenePanel?.ShutDown(); } catch (System.Exception ex) { MelonLogger.Error($"[Shutdown] scenePanel: {ex}"); }
        try { fireControl?.Stop(); } catch (System.Exception ex) { MelonLogger.Error($"[Shutdown] fireControl: {ex}"); }
        try { display?.Stop(); } catch (System.Exception ex) { MelonLogger.Error($"[Shutdown] display: {ex}"); }
        try { renderer2?.Stop(); } catch (System.Exception ex) { MelonLogger.Error($"[Shutdown] renderer2: {ex}"); }
        try { radar2?.Stop(); } catch (System.Exception ex) { MelonLogger.Error($"[Shutdown] radar2: {ex}"); }
        try { gunL?.Stop(); } catch (System.Exception ex) { MelonLogger.Error($"[Shutdown] gunL: {ex}"); }
        try { gunR?.Stop(); } catch (System.Exception ex) { MelonLogger.Error($"[Shutdown] gunR: {ex}"); }
        gunL = gunR = null;
        radar2 = null;
        display = null;
        renderer2 = null;
        fireControl = null;
        hud = null;
        scenePanel = null;
        IronNestFCS.Logic.FCS.MissionClock.Reset(); // 全限定: Il2Cpp 命名空间有同名 MissionClock (游戏新版本), 消歧义
        FcsBus.TurretRead = null;
        FcsBus.TurretVelRead = null;
        FcsBus.TurretSet = null;
        FcsBus.Fire = null;
        fcs.Dispose();
    }

    /// <summary>NumpadPlus/Minus: 控制所有蒸汽阀门开/关.</summary>
    private static void AdjustAllValves(float value)
    {
        var all = GameObject.FindObjectsOfType<GameObject>();
        MelonLogger.Msg($"[Valve] Setting all valves to {value}...");
        int done = 0;
        foreach (var leak in all)
        {
            if (leak == null || !leak.name.ToLower().Contains("steam leak")) continue;
            DialInteractable? nearestDi = null;
            float minDist = float.MaxValue;
            foreach (var go in all)
            {
                if (go == null) continue;
                var di = go.GetComponent<DialInteractable>();
                if (di == null) continue;
                var d = (go.transform.position - leak.transform.position).magnitude;
                if (d < minDist) { minDist = d; nearestDi = di; }
            }
            if (nearestDi == null) continue;
            nearestDi.SetDialValue(value);
            done++;
        }
        MelonLogger.Msg($"[Valve] Set {done} valves to {value}.");
    }
}
