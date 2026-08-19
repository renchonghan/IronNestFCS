using System.Collections.Generic;
using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// [DC] ScenePanel — 2.0 场景交互层 (迁移期: 旧 FcsSceneInteractor 清退前不 Build, 无视觉冲突).
/// 3D 按钮列 (沙盘右侧第二列, 原 T1-T4 位置): AutoFire / AutoTask / TWS / TightCharge(T) / NormalCharge(N) / ExtraCharge(X);
/// 火控台右侧按钮列改为三个: Start / Pause-Resume / Stop; 弹种选择按钮列保持现状 (旧层).
/// 目标输入: 实体右键 = 入队请求 (再点取消); 令牌拖放注册由 DisplayControl 方法承载.
/// </summary>
public class ScenePanel {
    public DisplayControl? Dc;
    public FireControl? Fc;
    public Radar? RadarPort;

    private readonly ClickRaycaster _clicks = new();
    private readonly List<GameObject> _owned = new();
    private readonly HashSet<Collider> _entityColliders = new();
    private readonly HashSet<Collider> _tokenColliders = new();
    private float _lastRegister;
    private bool _built;

    /// <summary>火控台三态: 停止 (白/白/红) / 运行 (绿/白/白) / 暂停 (白/黄/白).</summary>
    private enum PanelState { Stopped, Running, Paused }
    private PanelState _state = PanelState.Paused; // 初始 = 暂停 (冻结待命), 模块在 Build 末尾同步 Fc.Paused

    /// <summary>Build 3D 按钮列 + 右键注册 (旧交互层清退后由 FcsModule 调用一次).</summary>
    public void Build() {
        if (_built) return;
        _built = true;
        BuildModeButtons();
        BuildControlButtons();
        BuildBulletButtons();
    }

    // ===== 弹种选择按钮列 (保持现状: 火控台右侧斜线列, 全弹种枚举) =====
    private void BuildBulletButtons() {
        const float z = -18.4181f;
        var x = 0.8f;
        var y = -0.65f;
        foreach (BulletType type in System.Enum.GetValues(typeof(BulletType))) {
            BulletType captured = type;
            GameObject button = null!;
            button = Button(type.ToString(), Dc != null && Dc.SelectedShell == type ? Color.green : Color.white, () => {
                if (Dc == null) return;
                Dc.SelectedShell = captured;
                foreach (var (bt, go) in _bulletButtons) {
                    SetColor(go, bt == captured ? Color.green : Color.white);
                }
            });
            Place(button, x, y, z);
            _bulletButtons.Add((type, button));
            x -= 0.05f;
            y -= 0.0045f;
        }
    }
    private readonly List<(BulletType, GameObject)> _bulletButtons = new();

    public void ShutDown() {
        _clicks.Clear();
        foreach (var go in _owned) UnityEngine.Object.Destroy(go);
        _owned.Clear();
        _entityColliders.Clear();
        _built = false;
    }

    /// <summary>每帧: 点击检测 + 新实体右键注册 (1s 一批, 去重).</summary>
    public void Update() {
        if (!_built) return;
        _clicks.Update();
        if (Time.time - _lastRegister > 1f) {
            _lastRegister = Time.time;
            RegisterEntityRightClicks();
        }
    }

    private void RegisterEntityRightClicks() {
        if (RadarPort == null) return;
        _entityColliders.RemoveWhere(c => c == null);
        foreach (var c in RadarPort.Contacts) {
            if (c == null || c.Entity == null) continue;
            // 不用实体自带的 collider (游戏棋子的可能太小/位置偏), 统一挂 FCS 自有点击盒
            if (_entityColliders.Any(x => x != null && x.transform.parent == c.Entity.transform)) continue;
            var boxGo = new GameObject("FCS2_ClickBox");
            boxGo.transform.SetParent(c.Entity.transform, false);
            boxGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            var box = boxGo.AddComponent<BoxCollider>();
            box.size = new Vector3(0.22f, 0.22f, 0.05f);
            _owned.Add(boxGo); // 随 ShutDown 清理
            _entityColliders.Add(box);
            var go = c.Entity;
            _clicks.Register(box, () => {
                MelonLogger.Msg($"[DC] right-click {go.name}");
                Dc?.RightClickEntity(go);
            }, right: true); // 右键 (左键留给游戏自身拖拽)
        }
        // 地图令牌 (MapToken_* 棋子): 右键 = 虚拟目标入队 (拖放由 DC 数据循环检测).
        // 令牌无自带 collider: 挂 FCS 自有点击盒 (与实体右键同款)
        _tokenColliders.RemoveWhere(c => c == null);
        foreach (var go in GameObject.FindObjectsOfType<GameObject>()) {
            if (go == null || !go.name.StartsWith("MapToken")) continue;
            var col = go.GetComponent<Collider>();
            if (col == null) {
                if (_tokenColliders.Any(x => x != null && x.transform.parent == go.transform)) continue;
                var boxGo = new GameObject("FCS2_ClickBox");
                boxGo.transform.SetParent(go.transform, false);
                boxGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
                var box = boxGo.AddComponent<BoxCollider>();
                box.size = new Vector3(0.22f, 0.22f, 0.05f);
                _owned.Add(boxGo); // 随 ShutDown 清理
                col = box;
            }
            if (!_tokenColliders.Add(col)) continue;
            var token = go;
            _clicks.Register(col, () => {
                Dc?.RightClickToken(token);
            }, right: true);
        }
    }

    // ===== 沙盘右侧第二列: AutoFire / AutoTask / TWS / Tight / Normal / Extra (原 T1-T4 按钮位置) =====
    private void BuildModeButtons() {
        const float z = -18.5881f;
        var x = 0.8f;
        var y = -0.65f;

        // 先声明再赋值: lambda 要捕获按钮引用, 不能在其声明表达式内部引用它
        GameObject autoFire = null!;
        autoFire = Button("Auto Fire", Color.white, () => {
            if (Dc == null) return;
            Dc.AutoFire = !Dc.AutoFire;
            SetColor(autoFire, Dc.AutoFire ? Color.red : Color.white);
        });
        Place(autoFire, x, y, z); x -= 0.05f; y -= 0.0045f;

        GameObject autoTask = null!;
        autoTask = Button("Auto Task", Color.white, () => {
            if (Dc == null) return;
            Dc.SetAutoTask(!Dc.AutoTask);
            if (Dc.AutoTask) { Dc.AutoFire = true; SetColor(autoFire, Color.red); }
            SetColor(autoTask, Dc.AutoTask ? Color.red : Color.white);
        });
        Place(autoTask, x, y, z); x -= 0.05f; y -= 0.0045f;

        GameObject tws = null!;
        tws = Button("TWS", Color.white, () => {
            if (Dc == null) return;
            Dc.SetTws(!Dc.Tws);
            SetColor(tws, Dc.Tws ? Color.cyan : Color.white);
        });
        Place(tws, x, y, z); x -= 0.05f; y -= 0.0045f;

        GameObject tight = null!, normal = null!, extra = null!;
        tight = Button("Tight(T)", Color.white, () => {
            if (Dc == null) return;
            Dc.ChargeModeSelection = ChargeMode.Tight;
            SetChargeColors(tight);
        });
        Place(tight, x, y, z); x -= 0.05f; y -= 0.0045f;

        normal = Button("Normal(N)", Color.green, () => {
            if (Dc == null) return;
            Dc.ChargeModeSelection = ChargeMode.Normal;
            SetChargeColors(normal);
        });
        Place(normal, x, y, z); x -= 0.05f; y -= 0.0045f;

        extra = Button("Extra(X)", Color.white, () => {
            if (Dc == null) return;
            Dc.ChargeModeSelection = ChargeMode.Extra;
            SetChargeColors(extra);
        });
        Place(extra, x, y, z);

        void SetChargeColors(GameObject? active) {
            SetColor(tight, active == tight ? Color.green : Color.white);
            SetColor(normal, active == normal ? Color.green : Color.white);
            SetColor(extra, active == extra ? Color.green : Color.white);
        }
    }

    // ===== 火控台右侧: Start / Pause-Resume / Stop (三按钮在弹种列延长线上方一格起, 避免压到 AP) =====
    private void BuildControlButtons() {
        const float z = -18.4181f;
        var x = 0.95f;      // 比旧版 (0.9) 再上一格: 三按钮多占一格, 不能再往下伸
        var y = -0.6365f;

        GameObject start = null!, pause = null!, stop = null!;
        // 三态指示: 停止 (白/白/红) → 运行 (绿/白/白) → 暂停 (白/黄/白); 当前状态亮在对应按钮, 其余白
        start = Button("Start", Color.white, () => {
            _state = PanelState.Running;
            if (Fc != null) { Fc.Paused = false; Fc.SetManual(false); } // Start/Resume: AutoFire 已开 → Semi-Auto; 否则 PreAiming
            ApplyState(start, pause, stop);
        });
        Place(start, x, y, z); x -= 0.05f; y -= 0.0045f;

        pause = Button("Pause", Color.white, () => {
            if (_state == PanelState.Paused) {
                _state = PanelState.Running;
                if (Fc != null) { Fc.Paused = false; Fc.SetManual(false); }
            }
            else {
                // Stopped/Running 都可直接拉 Pause (取消 STOP→PAUSE 限制): 冻结派发与炮塔控制 + 解除 Manual
                // (豁免: 飞行计时/落点指示不冻结)
                _state = PanelState.Paused;
                if (Fc != null) { Fc.Paused = true; Fc.SetManual(false); }
            }
            ApplyState(start, pause, stop);
        });
        Place(pause, x, y, z); x -= 0.05f; y -= 0.0045f;

        stop = Button("Stop", Color.white, () => {
            _state = PanelState.Stopped;
            Fc?.StopTasks();  // 清队列 + 撤任务 (线程常驻不死)
            Fc?.SetManual(true); // Stop 回 Manual
            if (Fc != null) Fc.Paused = false;
            ApplyState(start, pause, stop);
        });
        Place(stop, x, y, z);

        void ApplyState(GameObject s, GameObject p, GameObject t) {
            switch (_state) {
                case PanelState.Stopped: // 白 / 白 / 红
                    SetColor(s, Color.white); SetColor(p, Color.white); SetColor(t, Color.red);
                    break;
                case PanelState.Running: // 绿 / 白 / 白
                    SetColor(s, Color.green); SetColor(p, Color.white); SetColor(t, Color.white);
                    break;
                default:                 // Paused: 白 / 黄 / 白
                    SetColor(s, Color.white); SetColor(p, new Color(1f, 0.6f, 0f)); SetColor(t, Color.white);
                    break;
            }
        }
        ApplyState(start, pause, stop); // 默认态 = 暂停 (白/黄/白)
        if (Fc != null) { Fc.Paused = true; Fc.SetManual(true); } // 模块同步: 初始 = Manual+Paused (冻结待命), 与 FC 默认 Mode 对齐 — GC.ManualControl 一并置位
    }

    private GameObject Button(string label, Color color, System.Action onClick) {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _owned.Add(go);
        var collider = go.GetComponent<Collider>();
        if (collider is BoxCollider box) box.size = new Vector3(1f, 10f, 1f); // 薄片视觉 + 厚点击盒
        _clicks.Register(collider, onClick);
        SetColor(go, color);
        var text = AddText(label, 14f);
        text.transform.SetParent(go.transform, false);
        text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        text.transform.localScale = Vector3.one;
        return go;
    }

    private static void Place(GameObject go, float x, float y, float z) {
        go.transform.position = new Vector3(x, y, z);
        go.transform.localScale = new Vector3(0.02f, 0.004f, 0.02f); // 平面薄片: 薄在 Y, 躺平贴桌面
    }

    private GameObject AddText(string label, float fontSize) {
        var go = new GameObject("FcsText");
        _owned.Add(go);
        go.transform.Rotate(new Vector3(90, 0, 0));
        go.transform.Rotate(new Vector3(0, 0, -90));
        var tmp = go.AddComponent<TextMeshPro>();
        if (tmp.font == null && TMP_Settings.defaultFontAsset != null) tmp.font = TMP_Settings.defaultFontAsset;
        tmp.text = label;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        return go;
    }

    /// <summary>URP Unlit 材质换色 (同旧 FcsSceneInteractor.SetColor).</summary>
    private static void SetColor(GameObject go, Color color) {
        var renderer = go.GetComponent<Renderer>();
        if (renderer == null) return;
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) {
            if (renderer.material != null) renderer.material.color = color;
            return;
        }
        var mat = new Material(shader);
        mat.color = color;
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
        renderer.material = mat;
    }
}
