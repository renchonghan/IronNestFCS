using System.Collections;
using Il2Cpp;
using Il2CppTMPro;
using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS.Logic;

public class FcsSceneInteractor {
    private FSC fcs;

    private List<GameObject> destroyOnShutdown = new();
    private readonly ClickRaycaster clicks = new();

    // 当前选中的弹种(两管炮共享, 由调度器决定任务派到哪管炮).
    public BulletType selectedBulletType = BulletType.AP;

    private List<GameObject> bulletTypeBtns = new();

    // 每个地图目标对应一个按钮: targetId -> 按钮. 点击=用当前弹种为该目标入队一个任务.
    private readonly Dictionary<int, GameObject> targetButtons = new();

    public bool AutoFire = false;
    public bool maxCharge = false;

    public FcsSceneInteractor(FSC fcs) {
        this.fcs = fcs;
    }

    public void Initialize() {
        InitializeControlButtons();
        InitializeBulletTypeButtons();
        InitializeTargetButtons();
    }

    /// <summary>火控总控按钮: Start/Pause 冻结/恢复全流程 (绿=运行, 橙=暂停), Stop 中止两炮并清队列. 与弹种列同一条斜排线 (每格 x-0.05 / y-0.0045), 排在弹种列延长线的上方, 同款薄片贴桌面.</summary>
    private void InitializeControlButtons() {
        const float z = -18.4181f;
        var x = 0.9f;     // 弹种列斜线往右上延长两格: 弹种列顶 AP 在 (0.8, -0.65)
        var y = -0.641f;

        GameObject? pauseButton = null;
        pauseButton = AddButton(() => {
            fcs.TogglePause();
            SetColor(pauseButton, fcs.Paused ? new Color(1f, 0.6f, 0f) : Color.green);
        }, Color.green);
        pauseButton.transform.position = new Vector3(x, y, z);
        pauseButton.transform.localScale = new Vector3(0.02f, 0.004f, 0.02f); // 与弹种按钮同款平面薄片: 薄在 Y, 躺平贴桌面
        var pauseText = AddText("Start/Pause", 14f);
        pauseText.transform.SetParent(pauseButton.transform, false);
        pauseText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        pauseText.transform.localScale = Vector3.one * 1.0f;

        x -= 0.05f;
        y -= 0.0045f;

        GameObject? stopButton = null;
        stopButton = AddButton(() => {
            fcs.StopAll();
        }, Color.red);
        stopButton.transform.position = new Vector3(x, y, z);
        stopButton.transform.localScale = new Vector3(0.02f, 0.004f, 0.02f);
        var stopText = AddText("Stop", 14f);
        stopText.transform.SetParent(stopButton.transform, false);
        stopText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        stopText.transform.localScale = Vector3.one * 1.0f;
    }

    private void InitializeBulletTypeButtons() {
        const float z = -18.4181f;
        var x = 0.8f;
        var y = -0.65f;
        foreach (BulletType type in Enum.GetValues(typeof(BulletType))) {
            BulletType captured = type;
            // 先声明再赋值: lambda 要捕获 button, 不能在其声明表达式内部引用它.
            GameObject button = null;
            button = AddButton(() => {
                selectedBulletType = captured;
                foreach (var btn in bulletTypeBtns) {
                    SetColor(btn, btn == button ? Color.green : Color.white);
                }
            }, type == BulletType.AP ? Color.green : Color.white);
            button.transform.position = new Vector3(x, y, z);
            button.transform.localScale = new Vector3(0.02f, 0.004f, 0.02f); // 平面薄片: 薄在 Y, 躺平贴桌面 (XZ 平面, Y 高度), 保留 BoxCollider 点击
            bulletTypeBtns.Add(button);
            var text = AddText(type.ToString(), 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one * 1.0f;
            x -= 0.05f;
            y -= 0.0045f;
        }
    }

    /// <summary>
    /// 4 个目标按钮(对应地图上 1~4 号炮兵标记). 点击即用当前选中弹种为该目标入队一个任务,
    /// 调度器自动派给空闲炮管. 用 activeTargets 防止同一目标重复入队.
    /// </summary>
    private void InitializeTargetButtons() {
        const float z = -18.5881f;
        var x = 0.8f;
        var y = -0.65f;
        
        GameObject? autoFireButton = null;
        autoFireButton = AddButton(() => {
            AutoFire = !AutoFire;
            SetColor(autoFireButton, AutoFire ? Color.red : Color.white);
        }, AutoFire ? Color.red : Color.white);
        autoFireButton.transform.position = new Vector3(x, y, z);
        autoFireButton.transform.localScale = new Vector3(0.02f, 0.004f, 0.02f); // 平面薄片: 薄在 Y, 躺平贴桌面 (XZ 平面, Y 高度), 保留 BoxCollider 点击
        var autoFiretext = AddText("Auto Fire", 14f);
        autoFiretext.transform.SetParent(autoFireButton.transform, false);
        autoFiretext.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        autoFiretext.transform.localScale = Vector3.one * 1.0f;
        
        x -= 0.05f;
        y -= 0.0045f;
        
        GameObject maxChargeButton = null;
        maxChargeButton = AddButton(() => {
            maxCharge = !maxCharge;
            SetColor(maxChargeButton, maxCharge ? Color.red : Color.white);
        }, maxCharge ? Color.red : Color.white);
        maxChargeButton.transform.position = new Vector3(x, y, z);
        maxChargeButton.transform.localScale = new Vector3(0.02f, 0.004f, 0.02f); // 平面薄片: 薄在 Y, 躺平贴桌面 (XZ 平面, Y 高度), 保留 BoxCollider 点击
        var maxChargeText = AddText("Max Charge", 14f);
        maxChargeText.transform.SetParent(maxChargeButton.transform, false);
        maxChargeText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        maxChargeText.transform.localScale = Vector3.one * 1.0f;
        
        x -= 0.05f;
        y -= 0.0045f;
        
        ////////////////
        
        for (var i = 1; i <= 4; i++) {
            var targetId = i;
            GameObject button = null;
            button = AddButton(() => FireButton(targetId, button), Color.red);
            button.transform.position = new Vector3(x, y, z);
            button.transform.localScale = new Vector3(0.02f, 0.004f, 0.02f); // 平面薄片: 薄在 Y, 躺平贴桌面 (XZ 平面, Y 高度), 保留 BoxCollider 点击
            targetButtons[targetId] = button;
            var text = AddText("T" + targetId, 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one * 1.0f;
            x -= 0.05f;
            y -= 0.0045f;
        }
    }

    /// <summary>任务完成回调</summary>
    public void TaskFinished(ArtilleryTask task) {
    }

    /// <summary>把地图实体菱形框注册为右键点击目标: 点击直接按实体当前位置入队/取消. 内部去重.</summary>
    private readonly HashSet<Collider> _registeredEntityColliders = new();
    public void RegisterEntityClickTargets(IReadOnlyList<(Collider collider, Transform entity)> targets) {
        _registeredEntityColliders.RemoveWhere(c => c == null); // 清掉已销毁的 (阵亡删除挂件)
        foreach (var (collider, entity) in targets) {
            if (!_registeredEntityColliders.Add(collider)) continue; // 已注册过
            var e = entity; // 闭包捕获
            clicks.Register(collider, () => fcs.ToggleEntityTask(e, selectedBulletType), right: true);
        }
    }

    /// <summary>T1-T4 炮兵标记物右键入队: 标记物没 collider 就补个小点击盒, 不画任何东西 (虚拟目标, 无单菱形).</summary>
    private readonly HashSet<Collider> _registeredMarkerColliders = new();
    public void RegisterMarkerClickTargets() {
        foreach (var (marker, id) in fcs.MapTable.ArtilleryMarkers) {
            if (marker == null) continue;
            var self = marker.GetComponent<Collider>();
            var children = marker.GetComponentsInChildren<Collider>(true);
            MelonLogger.Msg($"[FCS] Marker T{id} '{marker.name}' selfCollider={self != null} childColliders={children.Length} pos={marker.localPosition} scale={marker.lossyScale}");
            var collider = self != null ? self : (children.Length > 0 ? children[0] : null);
            if (collider == null) {
                collider = marker.gameObject.AddComponent<BoxCollider>();
                var box = (BoxCollider)collider;
                box.size = new Vector3(0.1f, 0.1f, 0.05f); // 标记物大小的点击盒
            }
            if (!_registeredMarkerColliders.Add(collider)) continue; // 已注册过
            var m = marker; // 闭包捕获
            clicks.Register(collider, () => fcs.ToggleEntityTask(m, selectedBulletType), right: true); // 右键 (左键留给游戏自身拖拽)
        }
    }

    /// <summary>按 T 标记物入队一次打击任务 + 1s 冷却灰显 (按钮与键盘共用).</summary>
    private void FireButton(int targetId, GameObject button) {
        if (!button.GetComponent<Collider>().enabled) return; // 冷却中
        var task = fcs.MapTable.GetMarkTarget(targetId);
        if (task == null) return; // 地图上没有这个编号的目标
        task.targetId = targetId;
        task.bulletType = selectedBulletType;
        fcs.EnqueueTask(task);
        fcs.MapTable.AttachMarkerTask(targetId, task); // 虚拟目标: 沙盘 T 标记物上挂双菱形火控框
        SetColor(button, Color.gray);
        button.GetComponent<Collider>().enabled = false;
        MelonCoroutines.Start(InvokeDelay(() => {
            SetColor(button, Color.red);
            button.GetComponent<Collider>().enabled = true;
        }, 1f));
    }

    /// <summary>键盘快捷键触发射击目标(对应小键盘 1-4), 等价于点击 T1-T4 按钮.</summary>
    public void FireTarget(int targetId) {
        if (!targetButtons.TryGetValue(targetId, out var button)) return;
        FireButton(targetId, button);
    }

    /// <summary>按世界坐标创建打击任务 (扫荡用): front=true 插队到队首 (高优先级目标).</summary>
    private ArtilleryTask? TaskFromWorldPos(int id, Vector3 worldPos)
    {
        var turret = fcs.MapTable.Turret;
        if (turret == null) return null;
        var mapSurface = GameObject.Find("Draggable Surface")?.transform;
        if (mapSurface == null) return null;
        var localPos = mapSurface.InverseTransformPoint(worldPos);
        // 炮塔世界坐标 → 地图局部坐标, 统一坐标系后再相减(铁巢转移后依然正确)
        var turretLocalOnMap = mapSurface.InverseTransformPoint(turret.position);
        var target = localPos - turretLocalOnMap;
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        return new ArtilleryTask
        {
            targetId = id,
            angel = angle,
            distance = dist,
            position = localPos * 3.8164f + GeoMap.KmOffset,
            bulletType = selectedBulletType
        };
    }

    public void FireAtWorldPos(int id, Vector3 worldPos)
    {
        var task = TaskFromWorldPos(id, worldPos);
        if (task != null) fcs.EnqueueTask(task);
    }

    public void FireAtWorldPosFront(int id, Vector3 worldPos)
    {
        var task = TaskFromWorldPos(id, worldPos);
        if (task != null) fcs.EnqueueTaskFront(task);
    }
    
    public void Update() {
        clicks.Update();
    }

    public void ShutDown() {
        clicks.Clear();
        foreach (var obj in destroyOnShutdown) {
            Object.Destroy(obj);
        }
    }
    
    public GameObject AddButton(Action onClick) {
        return AddButton(onClick, Color.white);
    }

    public GameObject AddButton(Action onClick, Color color) {
        // 用自带 BoxCollider 的 cube 当可点击目标, 靠 ClickRaycaster 自己 raycast 检测点击,
        // 不依赖游戏的 LookAtTarget, 也不注册新 IL2CPP 类型(保持可热重载).
        var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
        destroyOnShutdown.Add(button);
        var collider = button.GetComponent<Collider>();
        // 视觉是薄片 (Y 0.004), 从浅视角射线很难命中; 点击盒加高 10 倍成隐形厚盒兜住
        if (collider is BoxCollider box) box.size = new Vector3(1f, 10f, 1f);
        clicks.Register(collider, onClick);
        SetColor(button, color);
        return button;
    }

    /// <summary>
    /// 给对象的 Renderer 换上当前渲染管线(URP)的材质并设颜色.
    /// CreatePrimitive 默认用内置管线的 Standard 材质, 在 URP 下 shader 无效会渲染成紫色;
    /// 这里用 URP 的 Unlit shader 重建材质(不受光照影响, 纯色所见即所得).
    /// </summary>
    public static void SetColor(GameObject go, Color color) {
        var renderer = go.GetComponent<Renderer>();
        if (renderer == null)
            return;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) {
            MelonLogger.Warning("[FCS] Can't find URP shader. Use default material color instead.");
            // 退而求其次: 直接改现有材质颜色
            if (renderer.material != null)
                renderer.material.color = color;
            return;
        }

        var mat = new Material(shader);
        // URP Unlit 用 _BaseColor 控制颜色; 同时设 color 兼容.
        mat.color = color;
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
        renderer.material = mat;
    }

    /// <summary>
    /// 在 3D 世界里创建一段文本(World Space 的 TextMeshPro, 非 UGUI).
    /// 返回 GameObject, 调用方自行设 transform.position/scale. 文本/字号后续可通过
    /// go.GetComponent&lt;TextMeshPro&gt;() 修改. 英文数字用默认字体即可显示.
    /// </summary>
    public GameObject AddText(string text, float fontSize = 4f) {
        var go = new GameObject("FcsText");
        destroyOnShutdown.Add(go);
        go.transform.Rotate(new Vector3(90, 0, 0));
        go.transform.Rotate(new Vector3(0, 0, -90));
        var tmp = go.AddComponent<TextMeshPro>();
        // AddComponent 后 Awake 未必已执行, 字体可能未自动赋值导致不渲染;
        // 显式赋默认字体(含 ASCII, 英文数字足够).
        if (tmp.font == null && TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        // 锚点设到左上角, 方便从左上往下排版(Center 会以几何中心为原点).
        // tmp.alignment = TextAlignmentOptions.MidlineLeft;
        return go;
    }
    
    public static IEnumerator WaitAndClick(LookAtTarget? button, float timeout = 9f) {
        if (button == null) {
            MelonLogger.Error("[FCS] WaitAndClick: button is null");
            yield break;
        }
        float waited = 0f;
        while (button.isActive == false || button.nextAllowedClickTime > Time.realtimeSinceStartup) {
            if (waited >= timeout) {
                MelonLogger.Error($"[FCS] WaitAndClick: {button.name} not active after {timeout:F0}s, skip click");
                yield break;
            }
            yield return new WaitForSeconds(0.1f);
            waited += 0.1f;
        }
        yield return new WaitForSeconds(0.1f);
        button.OnClickDown();
        yield return new WaitForSeconds(0.1f);
        button.OnClickUp();
    }
    
    public static IEnumerator InvokeDelay(Action action, float delay) {
        yield return new WaitForSeconds(delay);
        action();
    }
    
}