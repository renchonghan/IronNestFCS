using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class MapTable {
    private Transform? turret;
    private ImpactMarkerManager? impactManager; // 铁巢网格位置真源: 它把真实炮塔世界坐标投到地图网格
    private Dictionary<int, Transform> artilleries;

    // 沙盘校准: 网格 A-T × 1-10 映射到棋子局部系
    // 格长用代码原有比例 1/3.8164 (1 棋盘单位 = 3.8164 km); 左下角用 1 药平射真实落点解出:
    // 目标 (-1.88,0.97) = 左下角 + 网格 (2.85,8.95) × 格长
    private static readonly Vector2 MapBottomLeft = new(-2.6238f, -1.3741f); // 网格原点 (A1) 在棋子空间的位置 (含目测修正: 右 0.1 小格 / 上 1/20 小格)
    private static readonly float MapCellSize = 1f / 3.8164f;                 // 每大格的棋子空间尺寸
    private Transform? fireMissionRoot;
    private FireMission? fireMission;
    private Transform? mapSurface;
    
    /// <summary>炮塔 Transform(供扫荡/世界坐标打击换算使用)</summary>
    public Transform? Turret => turret;
    
    public bool TryBind() {
        artilleries = new Dictionary<int, Transform>();
        var turretObject = GameObject.Find("Player Turret Piece");
        if (turretObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Player Turret Piece, 当前场景尚未就绪");
            return false;
        }

        var mapObject = GameObject.Find("Draggable Surface");
        if (mapObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Draggable Surface, 当前场景尚未就绪");
            return false;
        }

        turret = turretObject.transform;
        mapSurface = mapObject.transform;
        impactManager = GameObject.Find("---ImpactMarkerManager")?.GetComponent<ImpactMarkerManager>();
        var map = mapObject.transform;
        for (var i = 0; i < map.childCount; ++i) {
            var t = map.GetChild(i);
            if (t.name != "MapToken_Artillery") continue;
            var tmp = t.GetComponentInChildren<Il2CppTMPro.TextMeshPro>();
            if (tmp == null) continue;
            if (!int.TryParse(tmp.text, out var id)) continue;
            artilleries.Add(id, t);
        }
        MelonLogger.Msg($"[FCS] 找到 Player Turret Piece: {turret}, Artilleries: {artilleries.Count}");
        var fireMissionObject = GameObject.Find("Fire Mission Root");
        if (fireMissionObject == null) {
            MelonLogger.Warning("[FCS] 未找到 Fire Mission Root, 当前场景尚未就绪");
            return false;
        }

        fireMissionRoot = fireMissionObject.transform;
        fireMission = fireMissionRoot.GetComponent<FireMission>();
        return fireMission != null;
    }

    /// <summary>
    /// 把铁巢棋子吸附到游戏网格位置: 铁巢底座 RectTransform (turretBase) 就挂在
    /// MapRoot 网格空间里, 它的 localPosition 就是铁巢当前网格坐标, 游戏自己维护
    /// (归位/紧急转移都会更新). 再经四角校准常数映射到棋子局部系.
    /// </summary>
    public void SyncIronNestToken()
    {
        if (turret == null || impactManager == null || impactManager.turretController == null) return;
        var tb = impactManager.turretController.turretBase;
        if (tb == null) return;
        var grid = tb.localPosition;
        var local = new Vector3(MapBottomLeft.x + grid.x * MapCellSize,
                                MapBottomLeft.y + grid.y * MapCellSize,
                                turret.localPosition.z);
        turret.localPosition = local;
    }

    /// <summary>
    /// [临时调试] 在地图网格位置放一个纯色方块, 验证沙盘直接放置元素 (画箭头的地基).
    /// 与令牌同空间: 挂 Draggable Surface 下, 用沙盘校准常数把网格坐标映射到棋子局部系.
    /// </summary>
    public void SpawnTestMarker(Vector2 gridPos, Color color)
    {
        if (mapSurface == null) return;
        // 清理旧测试块 (F9 重载不销毁运行时物件, 会累积)
        foreach (var old in Resources.FindObjectsOfTypeAll<GameObject>()) {
            if (old != null && old.name == "FCS_MapElement_Test") UnityEngine.Object.Destroy(old);
        }
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "FCS_MapElement_Test";
        go.transform.SetParent(mapSurface, false);
        go.transform.localPosition = new Vector3(
            MapBottomLeft.x + gridPos.x * MapCellSize,
            MapBottomLeft.y + gridPos.y * MapCellSize,
            -0.01f);
        go.transform.localScale = new Vector3(0.06f, 0.06f, 1f);
        var r = go.GetComponent<Renderer>();
        if (r != null) {
            // 优先用游戏自己的绿灯材质 (InteractionLight Green, 高发射, 沙盘按钮按下同款);
            // 默认 primitive 材质着色器被裁剪 (紫块), 场景克隆材质会带原贴图
            Material? mat = null;
            foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) {
                if (m == null || m.name == null) continue;
                if (m.name.Contains("InteractionLight Green")) { mat = new Material(m); break; }
            }
            if (mat == null) {
                var src = mapSurface.GetComponentInChildren<Renderer>()?.material;
                if (src != null) { mat = new Material(src); mat.color = color; }
            }
            if (mat != null) r.material = mat;
        }
        MelonLogger.Msg($"[FCS] TestMarker placed at grid {gridPos}");
    }

    /// <summary>
    /// [临时调试] 沙盘箭头: 自建 Il2CppShapes.Line 形状组件 (不依赖场景标记/玩家画线).
    /// 挂在 Draggable Surface 下 (3D 板面空间), 用沙盘校准常数映射网格坐标.
    /// </summary>
    private Il2CppShapes.Line? _testArrowLine;
    private Vector2 _testArrowTarget;

    public void SpawnArrow(Vector2 targetGrid, Color color)
    {
        // 清理旧测试箭头 (F9 重载会累积)
        foreach (var old in Resources.FindObjectsOfTypeAll<GameObject>()) {
            if (old != null && old.name == "FCS_Arrow_Test") UnityEngine.Object.Destroy(old);
        }
        if (mapSurface == null) return;
        var go = new GameObject("FCS_Arrow_Test");
        go.transform.SetParent(mapSurface, false);
        go.transform.localPosition = new Vector3(0f, 0f, -0.02f);
        _testArrowLine = go.AddComponent<Il2CppShapes.Line>();
        _testArrowTarget = targetGrid;
        _testArrowLine.Thickness = 0.003f;
        _testArrowLine.Color = color;
        _testArrowLine.ColorStart = color;
        _testArrowLine.ColorEnd = color;
        MelonLogger.Msg($"[FCS] SpawnArrow: self-built Line for target grid {targetGrid}");
    }

    /// <summary>箭头实时跟随铁巢: 起点 = turretBase 网格位置 (经校准映射到板面), 终点 = 目标格.</summary>
    public void UpdateArrowToNest()
    {
        if (_testArrowLine == null || impactManager == null || impactManager.turretController == null) return;
        var tb = impactManager.turretController.turretBase;
        if (tb == null) return;
        var nest = (Vector2)tb.localPosition;
        var startLocal = new Vector3(MapBottomLeft.x + nest.x * MapCellSize, MapBottomLeft.y + nest.y * MapCellSize, 0f);
        var endLocal = new Vector3(MapBottomLeft.x + _testArrowTarget.x * MapCellSize, MapBottomLeft.y + _testArrowTarget.y * MapCellSize, 0f);
        _testArrowLine.transform.localPosition = startLocal;
        _testArrowLine.Start = Vector3.zero;
        _testArrowLine.End = endLocal - startLocal;
    }

    /// <summary>
    /// [临时调试] 所有地图实体画菱形框: 敌对纯红, 友军纯蓝.
    /// 正方形边长 0.1 转 45°, 四条边用 Il2CppShapes.Line 画 (线宽与箭头一致).
    /// 挂实体子级, 实体移动/死亡自动跟随, 不需要更新循环.
    /// </summary>
    /// <summary>实体菱形框的点击目标 (collider, 实体 transform), 由 FcsSceneInteractor 注册到 ClickRaycaster.</summary>
    private readonly List<(Collider collider, Transform entity)> _entityClickTargets = new();
    public IReadOnlyList<(Collider collider, Transform entity)> EntityClickTargets => _entityClickTargets;

    // 菱形框存活跟踪: 游戏不会反激活被消灭目标的标记, 需用雷达的 IsUnitAlive 判断后隐藏
    private readonly List<(GameObject holder, EntityLocation loc, GameObject entity)> _entityMarks = new();

    /// <summary>按雷达存活判定隐藏已消灭目标的菱形框 (每秒调用).</summary>
    public void UpdateEntityMarks()
    {
        foreach (var (holder, loc, entity) in _entityMarks) {
            if (holder == null || loc == null || entity == null) continue;
            bool alive = TacticalRadar.IsUnitAlive(loc, entity);
            if (holder.activeSelf != alive) holder.SetActive(alive);
        }
    }

    /// <summary>实体标记: 双菱形外圈 + 头上队列位置标签 (线段七段数码). 右键切换显示.</summary>
    private class TaskedMark {
        public GameObject holder = null!;
        public Transform entity = null!;
        public List<GameObject> outerSegs = new();
        public GameObject? labelRoot;
        public GameObject? timerRoot; // 菱形框下方的落点计时器 (整秒)
        public Color color;
        public ArtilleryTask? task;
        public bool visible;
    }
    private readonly Dictionary<Transform, TaskedMark> _marks = new();
    public IReadOnlyList<ArtilleryTask> TaskedTasks =>
        _marks.Values.Where(m => m.task != null).Select(m => m.task!).ToList();

    /// <summary>右键实体入队: 记录任务到标记并点亮双菱形+位置标签.</summary>
    public ArtilleryTask? TaskFromEntity(Transform entity)
    {
        var task = TaskFromGridPosition(entity.localPosition);
        if (task == null) return null;
        var mark = GetOrCreateMark(entity);
        mark.task = task;
        mark.visible = true;
        SetMarkVisible(mark);
        return task;
    }

    /// <summary>实体当前挂着的任务 (无则 null).</summary>
    public ArtilleryTask? TaskOfEntity(Transform entity)
    {
        return _marks.TryGetValue(entity, out var mark) ? mark.task : null;
    }

    /// <summary>清除实体的标记 (出队/完成时), 回单菱形.</summary>
    public void ClearEntityMark(Transform entity)
    {
        if (!_marks.TryGetValue(entity, out var mark)) return;
        foreach (var seg in mark.outerSegs) {
            if (seg != null) UnityEngine.Object.Destroy(seg);
        }
        mark.outerSegs.Clear();
        if (mark.labelRoot != null) UnityEngine.Object.Destroy(mark.labelRoot);
        if (mark.timerRoot != null) UnityEngine.Object.Destroy(mark.timerRoot);
        _marks.Remove(entity);
    }

    /// <summary>取或建实体的标记 (外圈线段预建, 默认隐藏).</summary>
    private TaskedMark GetOrCreateMark(Transform entity)
    {
        if (_marks.TryGetValue(entity, out var existing)) return existing;
        GameObject? holder = null;
        foreach (var (collider, e) in _entityClickTargets) {
            if (e == entity) { holder = collider.gameObject; break; }
        }
        var loc = entity.GetComponent<EntityLocation>();
        bool hostile = loc != null && TacticalRadar.IsHostile(loc, entity);
        var color = hostile ? Color.red : Color.blue;
        var mark = new TaskedMark { holder = holder ?? entity.gameObject, entity = entity, color = color };
        float r2 = 0.05f * Mathf.Sqrt(2f) * 1.35f;
        var pts = new[] {
            new Vector3(0f, r2, 0f), new Vector3(r2, 0f, 0f),
            new Vector3(0f, -r2, 0f), new Vector3(-r2, 0f, 0f),
        };
        for (int s = 0; s < 4; s++) {
            var lineGo = new GameObject("FCS_EntityDiamondSegOuter");
            lineGo.transform.SetParent(mark.holder.transform, false);
            var line = lineGo.AddComponent<Il2CppShapes.Line>();
            line.Thickness = 0.01f;
            line.Start = pts[s];
            line.End = pts[(s + 1) % 4];
            line.Color = color;
            line.ColorStart = color;
            line.ColorEnd = color;
            lineGo.SetActive(false);
            mark.outerSegs.Add(lineGo);
        }
        _marks[entity] = mark;
        return mark;
    }

    private static void SetMarkVisible(TaskedMark mark)
    {
        foreach (var seg in mark.outerSegs) seg.SetActive(mark.visible);
        if (mark.labelRoot != null) mark.labelRoot.SetActive(mark.visible);
    }

    /// <summary>更新点选目标的队列位置标签: slot = L/R/1..n, null = 任务完成 (清标记回单菱形).</summary>
    public void UpdateTaskMark(ArtilleryTask task, string? slot)
    {
        TaskedMark? mark = null;
        foreach (var m in _marks.Values) {
            if (m.task == task) { mark = m; break; }
        }
        if (mark == null) return;
        if (slot == null) {
            // 任务已完成 (主炮已换目标) 但炮弹可能还在飞: 撤位置标签, 计时器留到落地
            float remain = task.fireTime > 0f ? task.impactTime - (Time.time - task.fireTime) : 0f;
            if (remain > 0f) {
                SetMarkLabel(mark, "", Mathf.CeilToInt(remain).ToString());
            }
            else {
                ClearEntityMark(mark.entity);
            }
        }
        else if (mark.visible) {
            // 落点计时器: 击发后按剩余飞行时间取整秒, 未击发/已落地不显示
            string? timer = null;
            if (task.fireTime > 0f) {
                float remain = task.impactTime - (Time.time - task.fireTime);
                if (remain > 0f) timer = Mathf.CeilToInt(remain).ToString();
            }
            SetMarkLabel(mark, slot, timer);
        }
    }

    // 七段数码标签: 字符宽高与段定义 (单位格: 宽 1, 高 1.6)
    private const float LabelSegW = 0.045f; // 字符宽

    /// <summary>重建标签: 上方队列位置 + 下方落点计时器 (整秒), 七段数码线段平贴板面.</summary>
    private static void SetMarkLabel(TaskedMark mark, string slot, string? timer)
    {
        float step = LabelSegW * 1.4f; // 字符间距 (两位数字离太近看不清)
        // 水平居中按字形实际跨度算: 字形占 [0.00225, 0.04275] 格子, 串中心 = ((n-1)*step + 0.045)/2

        if (mark.labelRoot != null) UnityEngine.Object.Destroy(mark.labelRoot);
        mark.labelRoot = null;
        if (!string.IsNullOrEmpty(slot)) {
            var root = new GameObject("FCS_EntityLabelLines");
            root.transform.SetParent(mark.holder.transform, false);
            root.transform.localPosition = new Vector3(-((slot.Length - 1) * step + LabelSegW) / 2f, 0.14f, 0f);
            for (int i = 0; i < slot.Length; i++) {
                DrawCharSegments(root.transform, slot[i], mark.color, i * step);
            }
            mark.labelRoot = root;
        }

        if (mark.timerRoot != null) UnityEngine.Object.Destroy(mark.timerRoot);
        mark.timerRoot = null;
        if (!string.IsNullOrEmpty(timer)) {
            var tRoot = new GameObject("FCS_EntityTimerLines");
            tRoot.transform.SetParent(mark.holder.transform, false);
            tRoot.transform.localPosition = new Vector3(-((timer.Length - 1) * step + LabelSegW) / 2f, -0.2f, 0f);
            for (int i = 0; i < timer.Length; i++) {
                DrawCharSegments(tRoot.transform, timer[i], mark.color, i * step);
            }
            mark.timerRoot = tRoot;
        }
    }

    /// <summary>七段数码段坐标 (单位格).</summary>
    private static (Vector2 a, Vector2 b) Seg(char key) => key switch {
        'a' => (new Vector2(0.05f, 1.6f), new Vector2(0.95f, 1.6f)),
        'b' => (new Vector2(0.95f, 1.6f), new Vector2(0.95f, 0.8f)),
        'c' => (new Vector2(0.95f, 0.8f), new Vector2(0.95f, 0f)),
        'd' => (new Vector2(0.05f, 0f), new Vector2(0.95f, 0f)),
        'e' => (new Vector2(0.05f, 0f), new Vector2(0.05f, 0.8f)),
        'f' => (new Vector2(0.05f, 1.6f), new Vector2(0.05f, 0.8f)),
        'g' => (new Vector2(0.05f, 0.8f), new Vector2(0.95f, 0.8f)),
        _ => (Vector2.zero, Vector2.zero),
    };

    private static void DrawCharSegments(Transform parent, char ch, Color color, float x0)
    {
        string segs = ch switch {
            '0' => "abcdef", '1' => "bc", '2' => "abged", '3' => "abgcd", '4' => "fgbc",
            '5' => "afgcd", '6' => "afgedc", '7' => "abc", '8' => "abcdefg", '9' => "abcdfg",
            'L' => "efd", 'R' => "abgefc", '?' => "adg",
            _ => "abcdefg",
        };
        foreach (char key in segs) {
            var (a, b) = Seg(key);
            var go = new GameObject("FCS_LabelSeg");
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<Il2CppShapes.Line>();
            line.Thickness = 0.008f;
            line.Start = new Vector3(x0 + a.x * LabelSegW, a.y * LabelSegW, 0f);
            line.End = new Vector3(x0 + b.x * LabelSegW, b.y * LabelSegW, 0f);
            line.Color = color;
            line.ColorStart = color;
            line.ColorEnd = color;
        }
    }

    /// <summary>
    /// [临时调试] 火炮瞄准指示: 左炮十字 / 右炮 X, 挂在板面上.
    /// 位置 = 游戏落点标记 (GunLeft/GunRight_ImpactMarker) 的网格坐标经校准映射到板面.
    /// </summary>
    private GameObject? _aimLeft;   // 左炮十字
    private GameObject? _aimRight;  // 右炮 X
    private Transform? _impactLeft;
    private Transform? _impactRight;

    public void SpawnAimMarks()
    {
        if (mapSurface == null) return;
        _impactLeft = GameObject.Find("GunLeft_ImpactMarker")?.transform;
        _impactRight = GameObject.Find("GunRight_ImpactMarker")?.transform;
        if (_aimLeft != null) UnityEngine.Object.Destroy(_aimLeft);
        if (_aimRight != null) UnityEngine.Object.Destroy(_aimRight);
        _aimLeft = BuildAimMark(mapSurface, true, Color.green);  // 十字
        _aimRight = BuildAimMark(mapSurface, false, Color.green); // X (统一荧光绿, 形状区分左右)
        _aimLeft.SetActive(false);
        _aimRight.SetActive(false);
    }

    /// <summary>构造瞄准标记: isLeft = 十字, 否则 X. 半臂长 0.045 板面单位.</summary>
    private static GameObject BuildAimMark(Transform parent, bool isLeft, Color color)
    {
        var root = new GameObject(isLeft ? "FCS_AimLeft" : "FCS_AimRight");
        root.transform.SetParent(parent, false);
        float h = 0.045f;
        (Vector3 a, Vector3 b)[] segs = isLeft
            ? new[] { (new Vector3(0f, -h, 0f), new Vector3(0f, h, 0f)), (new Vector3(-h, 0f, 0f), new Vector3(h, 0f, 0f)) }
            : new[] { (new Vector3(-h, -h, 0f), new Vector3(h, h, 0f)), (new Vector3(-h, h, 0f), new Vector3(h, -h, 0f)) };
        foreach (var (a, b) in segs) {
            var go = new GameObject("FCS_AimSeg");
            go.transform.SetParent(root.transform, false);
            var line = go.AddComponent<Il2CppShapes.Line>();
            line.Thickness = 0.012f;
            line.Start = a;
            line.End = b;
            line.Color = color;
            line.ColorStart = color;
            line.ColorEnd = color;
        }
        return root;
    }

    /// <summary>实时更新瞄准标记: 位置跟落点标记, 显示与否跟炮管 CanFire.</summary>
    public void UpdateAimMarks(bool leftCanFire, bool rightCanFire)
    {
        if (_aimLeft == null || _aimRight == null) return;
        UpdateAimMark(_aimLeft, _impactLeft, leftCanFire);
        UpdateAimMark(_aimRight, _impactRight, rightCanFire);
    }

    private void UpdateAimMark(GameObject mark, Transform? impact, bool canFire)
    {
        if (mark == null) return;
        if (!canFire || impact == null) {
            if (mark.activeSelf) mark.SetActive(false);
            return;
        }
        var grid = impact.localPosition;
        mark.transform.localPosition = new Vector3(MapBottomLeft.x + grid.x * MapCellSize, MapBottomLeft.y + grid.y * MapCellSize, -0.03f);
        if (!mark.activeSelf) mark.SetActive(true);
    }

    /// <summary>[临时调试] 铁巢 → 当前目标虚线 (左右炮各一条, 荧光绿 0.006).</summary>
    private GameObject? _targetLineLeft;
    private GameObject? _targetLineRight;
    private Il2CppShapes.Line? _targetLineL;
    private Il2CppShapes.Line? _targetLineR;

    public void SpawnTargetLines()
    {
        if (mapSurface == null) return;
        if (_targetLineLeft != null) UnityEngine.Object.Destroy(_targetLineLeft);
        if (_targetLineRight != null) UnityEngine.Object.Destroy(_targetLineRight);
        (_targetLineLeft, _targetLineL) = BuildTargetLine(mapSurface, "FCS_TargetLineLeft");
        (_targetLineRight, _targetLineR) = BuildTargetLine(mapSurface, "FCS_TargetLineRight");
        _targetLineLeft.SetActive(false);
        _targetLineRight.SetActive(false);
    }

    private static (GameObject, Il2CppShapes.Line) BuildTargetLine(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var line = go.AddComponent<Il2CppShapes.Line>();
        line.Thickness = 0.006f;
        line.Dashed = true;
        line.Color = Color.green;
        line.ColorStart = Color.green;
        line.ColorEnd = Color.green;
        return (go, line);
    }

    /// <summary>实时更新目标连线: 有实体标记跟实体位置, 否则按任务方位/距离反推.</summary>
    public void UpdateTargetLines(ArtilleryTask? left, ArtilleryTask? right)
    {
        UpdateTargetLine(_targetLineLeft, _targetLineL, left);
        UpdateTargetLine(_targetLineRight, _targetLineR, right);
    }

    private void UpdateTargetLine(GameObject? go, Il2CppShapes.Line? line, ArtilleryTask? task)
    {
        if (go == null || line == null) return;
        if (task == null) {
            if (go.activeSelf) go.SetActive(false);
            return;
        }
        if (impactManager == null || impactManager.turretController == null) return;
        var tb = impactManager.turretController.turretBase;
        if (tb == null) return;
        Vector2 nest = tb.localPosition;
        Vector2 targetGrid = nest;
        var mark = _marks.Values.FirstOrDefault(m => m.task == task);
        if (mark != null && mark.entity != null) {
            targetGrid = mark.entity.localPosition; // 实体实时位置
        }
        else {
            float rad = task.angel * Mathf.Deg2Rad;
            targetGrid = nest + new Vector2(Mathf.Sin(rad), Mathf.Cos(rad)) * task.distance; // 1 网格 = 1 km
        }
        var startLocal = new Vector3(MapBottomLeft.x + nest.x * MapCellSize, MapBottomLeft.y + nest.y * MapCellSize, 0f);
        var endLocal = new Vector3(MapBottomLeft.x + targetGrid.x * MapCellSize, MapBottomLeft.y + targetGrid.y * MapCellSize, 0f);
        go.transform.localPosition = startLocal;
        line.Start = Vector3.zero;
        line.End = endLocal - startLocal;
        if (!go.activeSelf) go.SetActive(true);
    }

    /// <summary>按地图网格位置创建打击任务 (铁巢 turretBase 为锚点). 点击实体菱形框入队用.</summary>
    public ArtilleryTask? TaskFromGridPosition(Vector3 gridPos)
    {
        if (impactManager == null || impactManager.turretController == null) return null;
        var tb = impactManager.turretController.turretBase;
        if (tb == null) return null;
        // 网格坐标 → 板面局部系 (与 GetMarkTarget 同空间), 方向角必须在该空间算
        // 网格画布空间与板面空间朝向不一致, 直接在网格空间算角会打飞
        var nestGrid = (Vector2)tb.localPosition;
        var local = new Vector2(MapBottomLeft.x + gridPos.x * MapCellSize, MapBottomLeft.y + gridPos.y * MapCellSize);
        var nestLocal = new Vector2(MapBottomLeft.x + nestGrid.x * MapCellSize, MapBottomLeft.y + nestGrid.y * MapCellSize);
        var target = local - nestLocal;
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(new Vector3(target.x, target.y, 0f), Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        return new ArtilleryTask {
            angel = angle,
            distance = dist,
            position = new Vector3(local.x, local.y, 0f) * 3.8164f + new Vector3(10.016f, 5.235f, 0f),
        };
    }

    public void SpawnEntityDiamonds()
    {
        foreach (var old in Resources.FindObjectsOfTypeAll<GameObject>()) {
            if (old != null && old.name == "FCS_EntityDiamond") UnityEngine.Object.Destroy(old);
        }
        _entityClickTargets.Clear();
        _entityMarks.Clear();
        if (fireMissionRoot == null) return;
        var red = Color.red;   // 纯红
        var blue = Color.blue; // 纯蓝
        float r = 0.05f * Mathf.Sqrt(2f); // 菱形半对角线 (正方形边长 0.1)
        var pts = new[] {
            new Vector3(0f, r, 0f), new Vector3(r, 0f, 0f),
            new Vector3(0f, -r, 0f), new Vector3(-r, 0f, 0f),
        };
        for (int i = 0; i < fireMissionRoot.childCount; i++) {
            var child = fireMissionRoot.GetChild(i);
            var loc = child.GetComponent<EntityLocation>();
            if (loc == null) continue;
            bool hostile = TacticalRadar.IsHostile(loc, child);
            var color = hostile ? red : blue;
            var holder = new GameObject("FCS_EntityDiamond");
            holder.transform.SetParent(child, false);
            holder.transform.localPosition = new Vector3(0f, 0f, -0.02f);
            var col = holder.AddComponent<BoxCollider>();
            col.size = new Vector3(0.15f, 0.15f, 0.02f);
            _entityClickTargets.Add((col, child));
            _entityMarks.Add((holder, loc, child.gameObject));
            for (int s = 0; s < 4; s++) {
                var lineGo = new GameObject("FCS_EntityDiamondSeg");
                lineGo.transform.SetParent(holder.transform, false);
                var line = lineGo.AddComponent<Il2CppShapes.Line>();
                line.Thickness = 0.01f;
                line.Start = pts[s];
                line.End = pts[(s + 1) % 4];
                line.Color = color;
                line.ColorStart = color;
                line.ColorEnd = color;
            }
        }
        MelonLogger.Msg("[FCS] SpawnEntityDiamonds: done");
    }

    /// <summary>把第 index 号地图标记放到世界坐标对应位置(雷达自动标点用)</summary>
    public void SetMarkerWorldPos(int index, Vector3 worldPos)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        if (mapSurface == null) return;
        var local = mapSurface.InverseTransformPoint(worldPos);
        local.z = marker.localPosition.z;
        marker.localPosition = local;
    }

    /// <summary>把第 index 号地图标记复位到炮塔位置(目标不存在/已摧毁时)</summary>
    public void ResetMarker(int index)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        if (turret == null || mapSurface == null) return;
        marker.localPosition = mapSurface.InverseTransformPoint(turret.position);
    }

    /// <summary>按公里坐标设置第 index 号标记(KM 系 → 地图局部系, 比例 3.8164)</summary>
    public void SetMarkerByKmPos(int index, Vector2 kmPos)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        var local = new Vector3(kmPos.x / 3.8164f, kmPos.y / 3.8164f, marker.localPosition.z);
        marker.localPosition = local;
    }

    /// <summary>直接按地图局部坐标设置第 index 号标记.</summary>
    public void SetMarkerLocalPos(int index, Vector2 localPos)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        marker.localPosition = new Vector3(localPos.x, localPos.y, marker.localPosition.z);
    }

    public ArtilleryTask? GetMarkTarget(int index) {
        if (turret == null) {
            MelonLogger.Error("[FCS] GetMarkTarget: turret unbound");
            return null;
        }

        if (mapSurface == null) {
            MelonLogger.Error("[FCS] GetMarkTarget: map surface unbound");
            return null;
        }

        if (index > artilleries.Count) {
            MelonLogger.Error($"[FCS] GetMarkTarget: index {index} out of range, artillery count: {artilleries.Count}");
            return null;
        }

        // 炮塔世界坐标 → 地图局部坐标, 与标记处于同一坐标系后再相减
        // 铁巢紧急转移后炮塔移动也能得到正确的相对偏移(旧实现直接减 turret.localPosition, 坐标系不同, 转移后失准)
        var turretLocalOnMap = mapSurface.InverseTransformPoint(turret.position);
        var target = artilleries[index].localPosition - turretLocalOnMap;
        var dist = target.magnitude * 3.8164f;
        var angle = Vector3.SignedAngle(target, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360;
        var task = new ArtilleryTask {
            angel = angle,
            distance = dist,
            position = artilleries[index].localPosition * 3.8164f + new Vector3(10.016f, 5.235f, 0f)
        };
        return task;
    }

    public List<EntityLocation> GetAllFireMissionEntities() {
        List<EntityLocation> res = new();
        if (fireMissionRoot == null) {
            return res;
        }

        for (var i = 0; i < fireMissionRoot.childCount; ++i) {
            var m = fireMissionRoot.GetChild(i).GetComponent<EntityLocation>();
            if (m != null) res.Add(m);
        }
        return res;
    }
    
}
