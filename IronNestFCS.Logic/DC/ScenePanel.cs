using System.Collections;
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
    private readonly List<System.Action> _refreshColors = new(); // 每按钮注册: 按当前状态设置自己的颜色 (初始化统一着色 + RST 闪白后恢复)
    private object? _flashHandle;
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
        foreach (var refresh in _refreshColors) refresh(); // 初始统一着色 (按钮创建时全白, 这里切回各自颜色)
    }

    // ===== 弹种选择按钮列 (保持现状: 火控台右侧斜线列, 全弹种枚举) =====
    private void BuildBulletButtons() {
        const float z = -18.4181f;
        var x = 0.8f;
        var y = -0.65f;
        foreach (BulletType type in System.Enum.GetValues(typeof(BulletType))) {
            BulletType captured = type;
            GameObject button = null!;
            button = Button(type.ToString(), Color.white, () => {
                if (Dc == null) return;
                Dc.SelectedShell = captured;
                foreach (var (bt, go) in _bulletButtons) {
                    SetColor(go, bt == captured ? Color.green : Color.white);
                }
            });
            Place(button, x, y, z);
            _bulletButtons.Add((type, button));
            _refreshColors.Add(() => SetColor(button, Dc != null && Dc.SelectedShell == captured ? Color.green : Color.white));
            x -= 0.05f;
            y -= 0.0045f;
        }
    }
    private readonly List<(BulletType, GameObject)> _bulletButtons = new();

    public void ShutDown() {
        _clicks.Clear();
        try { if (_flashHandle != null) MelonCoroutines.Stop(_flashHandle); } catch { }
        _flashHandle = null;
        foreach (var go in _owned) UnityEngine.Object.Destroy(go);
        _owned.Clear();
        _refreshColors.Clear();
        _entityColliders.Clear();
        _built = false;
    }

    /// <summary>每帧: 点击检测 + 点击盒世界归正 + 新实体右键注册 (1s 一批, 去重).</summary>
    public void Update() {
        if (!_built) return;
        _clicks.Update();
        StraightenClickBoxes(); // 盒世界系水平归正 (棋子照片倾斜时盒跟着斜, 特定视角射线擦边点不中)
        if (Time.time - _lastRegister > 1f) {
            _lastRegister = Time.time;
            RegisterEntityRightClicks();
        }
    }

    /// <summary>点击盒每帧归正: 旋转清零 (世界水平薄片, 任何视角可命中) + 位置只跟实体位置 (不受实体倾斜牵连).</summary>
    private void StraightenClickBoxes() {
        _entityColliders.RemoveWhere(c => c == null);
        _tokenColliders.RemoveWhere(c => c == null);
        foreach (var c in _entityColliders) {
            var t = c.transform;
            t.rotation = Quaternion.identity;
            t.position = t.parent.position;
        }
        foreach (var c in _tokenColliders) {
            var t = c.transform;
            t.rotation = Quaternion.identity;
            t.position = t.parent.position;
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
            box.size = new Vector3(0.26f, 0.26f, 0.1f);
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
            // 标记令牌只注册 T1-10 (MapToken_Artillery 系列); 击杀/侦察/参考点令牌不可点
            if (go == null || !go.name.StartsWith("MapToken_Artillery") || go.name.Contains("Killed")) continue;
            var col = go.GetComponent<Collider>();
            if (col == null) {
                if (_tokenColliders.Any(x => x != null && x.transform.parent == go.transform)) continue;
                var boxGo = new GameObject("FCS2_ClickBox");
                boxGo.transform.SetParent(go.transform, false);
                boxGo.transform.localPosition = new Vector3(0f, 0f, -0.01f);
                var box = boxGo.AddComponent<BoxCollider>();
                box.size = new Vector3(0.26f, 0.26f, 0.1f);
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

    // ===== 沙盘右侧第二列 (原 T1-T4 按钮位置): AUTO TASK / AUTO FIRE / (空) / SAR RADAR / TWS CGMTI / (空) / T/N/X =====
    // 空行 = 面板上占一个按钮位置 (视觉分组); 全大写标签. SAR = 雷达电源: 默认关, 关时无雷达 (目标全无), TWS 灰按不动.
    private void BuildModeButtons() {
        const float z = -18.6181f; // 与弹种列 (-18.4181) 间隔 0.2
        var x = 0.8f;
        var y = -0.65f;
        void Step() { x -= 0.05f; y -= 0.0045f; }

        // 先声明再赋值: lambda 要捕获按钮引用, 不能在其声明表达式内部引用它
        GameObject autoTask = null!, autoFire = null!, sar = null!, tws = null!, tight = null!, normal = null!, extra = null!;

        autoTask = Button("AUTO TASK", Color.white, () => {
            if (Dc == null) return;
            Dc.SetAutoTask(!Dc.AutoTask);
            if (Dc.AutoTask) { Dc.AutoFire = true; SetColor(autoFire, Color.green); }
            SetColor(autoTask, Dc.AutoTask ? Color.green : Color.white);
        });
        Place(autoTask, x, y, z); Step();

        autoFire = Button("AUTO FIRE", Color.white, () => {
            if (Dc == null) return;
            Dc.AutoFire = !Dc.AutoFire;
            SetColor(autoFire, Dc.AutoFire ? Color.green : Color.white);
        });
        Place(autoFire, x, y, z); Step();

        Step(); // 空行 (占一个按钮位置)

        sar = Button("SAR RADAR", Color.white, () => {
            if (RadarPort == null) return;
            RadarPort.Power = !RadarPort.Power;
            SetColor(sar, RadarPort.Power ? Color.green : Color.white);
            SetColor(tws, !RadarPort.Power ? Color.gray : (Dc != null && Dc.Tws ? Color.green : Color.white)); // 雷达关 → TWS 灰
        });
        Place(sar, x, y, z); Step();

        tws = Button("TWS CGMTI", Color.white, () => { // 雷达默认关时灰按不动 (统一着色时设灰)
            if (Dc == null || RadarPort == null || !RadarPort.Power) return; // SAR 没开: 按不动
            Dc.SetTws(!Dc.Tws);
            SetColor(tws, Dc.Tws ? Color.green : Color.white);
        });
        Place(tws, x, y, z); Step();

        Step(); // 空行 (占一个按钮位置)

        tight = Button("T - TIGHT", Color.white, () => {
            if (Dc == null) return;
            Dc.ChargeModeSelection = ChargeMode.Tight;
            SetChargeColors(tight);
        });
        Place(tight, x, y, z); Step();

        normal = Button("N - NORML", Color.white, () => {
            if (Dc == null) return;
            Dc.ChargeModeSelection = ChargeMode.Normal;
            SetChargeColors(normal);
        });
        Place(normal, x, y, z); Step();

        extra = Button("X - EXTRA", Color.white, () => {
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
        // 统一着色注册 (RST 闪白后按当前状态恢复)
        _refreshColors.Add(() => SetColor(autoTask, Dc != null && Dc.AutoTask ? Color.green : Color.white));
        _refreshColors.Add(() => SetColor(autoFire, Dc != null && Dc.AutoFire ? Color.green : Color.white));
        _refreshColors.Add(() => SetColor(sar, RadarPort != null && RadarPort.Power ? Color.green : Color.white));
        _refreshColors.Add(() => SetColor(tws, RadarPort == null || !RadarPort.Power ? Color.gray : (Dc != null && Dc.Tws ? Color.green : Color.white)));
        _refreshColors.Add(() => SetColor(tight, Dc != null && Dc.ChargeModeSelection == ChargeMode.Tight ? Color.green : Color.white));
        _refreshColors.Add(() => SetColor(normal, Dc != null && Dc.ChargeModeSelection == ChargeMode.Normal ? Color.green : Color.white));
        _refreshColors.Add(() => SetColor(extra, Dc != null && Dc.ChargeModeSelection == ChargeMode.Extra ? Color.green : Color.white));
    }

    // ===== 火控台右侧: START / PAUSE / ST/CL (START 占原 PAUSE 位; PAUSE 在 START 右侧, 上下对齐第二列 AUTO TASK; ST/CL 原位) =====
    private void BuildControlButtons() {
        const float z = -18.4181f;

        GameObject start = null!, pause = null!, stop = null!, rst = null!;
        // 三态指示: 停止 (白/白/红) → 运行 (绿/白/白) → 暂停 (白/橙/白); 当前状态亮在对应按钮, 其余白
        start = Button("RUN", Color.white, () => {
            _state = PanelState.Running;
            if (Fc != null) {
                Fc.Paused = false;
                Fc.SetManual(false); // Start/Resume: AutoFire 已开 → Semi-Auto; 否则 PreAiming
                Fc.SortQueueOnce();  // 乱序重排一次 (45° 内匹配后续任务提前), 之后不动
            }
            ApplyState(start, pause, stop);
        });
        Place(start, 0.9f, -0.641f, z); // RUN: 最前层 (弹种列平面 -18.4181)

        pause = Button("HLD", Color.white, () => {
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
        Place(pause, 0.9f, -0.641f, -18.5181f); // HLD: 同 x/y, z 步进 0.1

        stop = Button("STP", Color.white, () => {
            _state = PanelState.Stopped;
            Fc?.StopTasks();  // 清队列 + 撤任务 (线程常驻不死)
            Fc?.SetManual(true); // Stop 回 Manual
            if (Fc != null) Fc.Paused = false;
            ApplyState(start, pause, stop);
        });
        Place(stop, 0.9f, -0.641f, -18.6181f); // STP: 同 x/y, 再深一层 (z 步进 0.1, 与第二列同平面)

        rst = Button("RST", Color.white, () => { // 手动重置: 停火 + 全部按钮闪白再切回各自颜色
            _state = PanelState.Stopped;
            Fc?.StopTasks();
            Fc?.SetManual(true);
            if (Fc != null) Fc.Paused = false;
            try { if (_flashHandle != null) MelonCoroutines.Stop(_flashHandle); } catch { }
            _flashHandle = MelonCoroutines.Start(FlashRst());
        });
        Place(rst, 0.9f, -0.641f, -18.7181f); // RST: 队尾, 再深一层

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
        // 统一着色注册: 三态 + RST 青 (Build 末尾统一着色, RST 闪白后恢复)
        _refreshColors.Add(() => ApplyState(start, pause, stop));
        _refreshColors.Add(() => SetColor(rst, Color.cyan));
        if (Fc != null) { Fc.Paused = true; Fc.SetManual(true); } // 模块同步: 初始 = Manual+Paused (冻结待命), 与 FC 默认 Mode 对齐 — GC.ManualControl 一并置位
    }

    /// <summary>RST 手动重置: 所有按钮闪白 0.15s → 切回各自当前状态颜色.</summary>
    private IEnumerator FlashRst() {
        foreach (var go in _owned) SetColor(go, Color.white);
        yield return new WaitForSeconds(0.15f);
        foreach (var refresh in _refreshColors) refresh();
        _flashHandle = null;
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

    /// <summary>URP Unlit 材质换色 (同旧 FcsSceneInteractor.SetColor).
    /// 跳过 TMP 文字: 换材质会丢字体 atlas → 字符全变方块 (RST 闪白遍历 _owned 时踩过).</summary>
    private static void SetColor(GameObject go, Color color) {
        if (go.GetComponent<TextMeshPro>() != null) return; // 文字不换材质
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
