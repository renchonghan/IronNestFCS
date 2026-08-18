using Il2Cpp;
using IronNestFCS.Abstractions;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IronNestFCS.Logic;

/// <summary>
/// Logic 程序集的入口 (2.0): 组装四模块 (RD/DC/FC/GC) 并接线, 旧调度层已停用 (LegacyDisabled).
/// 数据链: RD → DC_U → FC → GC → DC_D; F9 热重载 = Shutdown (全模块 Stop + 清端口) → 重 Initialize.
/// </summary>
public class FcsModule : IFcsModule
{
    private readonly FSC fcs = new() { LegacyDisabled = true }; // 旧调度层停用, 只保留硬件绑定

    private GunControl? gunL;
    private GunControl? gunR;
    private CoroutineLock? gcPurchaseLock;
    private CoroutineLock? gcFireLock;
    private Radar? radar2;
    private DisplayControl? display;
    private SandboxRenderer? renderer2;
    private FireControl? fireControl;
    private FcsHud? hud;
    private ScenePanel? scenePanel;

    public bool Initialize()
    {
        bool bound = fcs.TryBind();
        if (!bound) return false;
        ShellData.Init(); // 扫游戏 ShellDefinition: 杀伤半径/速度曲线 (杀伤圈与射表数据源)
        WireModules();
        return true;
    }

    /// <summary>四模块装配 + 端口接线 (RD-DC_U-FC-GC-DC_D).</summary>
    private void WireModules()
    {
        // 全局端口总线: 炮塔/击发钮 (FC 直控炮塔, GC 只读/被选中时写修正)
        FcsBus.TurretRead = () => fcs.Turret.CurrentAngle();
        FcsBus.TurretVelRead = () => fcs.Turret.RotationVelocity();
        FcsBus.TurretSet = a => fcs.Turret.SetDesiredRotation(a);
        FcsBus.Fire = () => fcs.TriggerConsole.Fire();

        gcPurchaseLock = new CoroutineLock();
        gcFireLock = new CoroutineLock();
        var deck = new PurchaseDeck();
        deck.TryBind();

        // GC: 两炮执行器
        gunL = new GunControl(LeftRight.Left, fcs.LeftGun, deck, gcPurchaseLock, gcFireLock);
        gunR = new GunControl(LeftRight.Right, fcs.RightGun, deck, gcPurchaseLock, gcFireLock);
        gunL.SyncPeer = gunR;
        gunR.SyncPeer = gunL;
        gunL.Start();
        gunR.Start();

        // RD → DC 数据链
        radar2 = new Radar();
        radar2.Start();
        var surface = GameObject.Find("Draggable Surface")?.transform;
        var nest = GameObject.Find("Player Turret Piece")?.transform;
        renderer2 = new SandboxRenderer { MapSurfaceRef = surface, NestRef = nest };
        display = new DisplayControl {
            RadarPort = radar2,
            NestRef = nest,
            MapSurfaceRef = surface,
        };
        display.OnIconSpawn = t => renderer2.SpawnIcon(t);
        display.OnIconRemove = go => renderer2.RemoveIcon(go);
        display.Start();
        renderer2.Start();

        // GC → DC_D: 实时弹道 push
        gunL.OnBallisticPush = (side, x, y, r, b) => renderer2.PushBallistic(side, new Vector2(x, y), r, b);
        gunR.OnBallisticPush = (side, x, y, r, b) => renderer2.PushBallistic(side, new Vector2(x, y), r, b);

        // FC
        fireControl = new FireControl {
            GunL = gunL,
            GunR = gunR,
            ConsolePort = fcs.TriggerConsole,
            Calculator = fcs.BallisticCalculator,
            FireLock = gcFireLock,
            NestRef = nest,
            MapSurfaceRef = surface,
        };
        fireControl.OnShellFired = (pos, shell, fly, fireMission) => renderer2.CreateImpact(pos, shell, fly);
        fireControl.OnQueueChanged = fc => renderer2.UpdateQueueIndicator(fc);
        fireControl.OnAimChanged = (side, pos, r, b) => renderer2.PushBallistic(side, pos, r, b);
        fireControl.Start();
        display.FcPort = fireControl;
        hud = new FcsHud { Fc = fireControl, GunL = gunL, GunR = gunR };

        // DC 场景交互层 (新按钮列 + 右键)
        scenePanel = new ScenePanel { Dc = display, Fc = fireControl, RadarPort = radar2 };
        scenePanel.Build();
    }

    public void Update()
    {
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

    public void OnGui()
    {
        hud?.OnGui();
    }

    public void Shutdown()
    {
        scenePanel?.ShutDown();
        fireControl?.Stop();
        display?.Stop();
        renderer2?.Stop();
        radar2?.Stop();
        gunL?.Stop();
        gunR?.Stop();
        gunL = gunR = null;
        radar2 = null;
        display = null;
        renderer2 = null;
        fireControl = null;
        hud = null;
        scenePanel = null;
        MissionClock.Reset();
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
