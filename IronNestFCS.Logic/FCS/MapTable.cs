using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
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

    /// <summary>实体菱形框的点击目标 (collider, 实体 transform), 由 FcsSceneInteractor 注册到 ClickRaycaster.</summary>
    private readonly List<(Collider collider, Transform entity)> _entityClickTargets = new();
    public IReadOnlyList<(Collider collider, Transform entity)> EntityClickTargets => _entityClickTargets;

    // 菱形框存活跟踪: 游戏不会反激活被消灭目标的标记, 需用雷达的 IsUnitAlive 判断后隐藏
    private readonly List<(GameObject holder, EntityLocation loc, GameObject entity)> _entityMarks = new();

    /// <summary>
    /// 实体菱形框刷新: 阵亡的销毁挂件 (无尽模式持续出实体, 隐藏会无限累积),
    /// 新出现的实体补挂件.
    /// </summary>
    public void RefreshEntityMarks()
    {
        var dead = new List<GameObject>();
        foreach (var (holder, loc, entity) in _entityMarks) {
            if (holder == null || loc == null || entity == null) continue;
            if (!TacticalRadar.IsUnitAlive(loc, entity)) dead.Add(entity);
        }
        foreach (var e in dead) {
            foreach (var (collider, entity) in _entityClickTargets) {
                if (entity.gameObject == e && collider != null) { UnityEngine.Object.Destroy(collider.gameObject); break; }
            }
            _entityClickTargets.RemoveAll(t => t.entity.gameObject == e);
            _entityMarks.RemoveAll(m => m.entity == e);
            _marks.Remove(e.transform);
        }
        if (fireMissionRoot == null) return;
        for (int i = 0; i < fireMissionRoot.childCount; i++) {
            var child = fireMissionRoot.GetChild(i);
            var loc = child.GetComponent<EntityLocation>();
            if (loc == null) continue;
            if (_entityMarks.Any(m => m.entity == child.gameObject)) continue;
            if (!TacticalRadar.IsUnitAlive(loc, child.gameObject)) continue; // 尸体不再挂件: 游戏把死实体留在场景里, 不检查会清完立刻重挂
            SpawnDiamondFor(child, loc);
        }
    }

    /// <summary>实体标记: 双菱形外圈 + 头上队列位置标签 (线段七段数码). 右键切换显示.</summary>
    private class TaskedMark {
        public GameObject holder = null!;
        public bool ownsHolder; // T1-T4 虚拟目标自建底座, 清理标记时一并销毁
        public Transform entity = null!;
        public List<GameObject> outerSegs = new();
        public GameObject? labelRoot;
        public GameObject? bulletRoot; // 序列上方的弹种标签
        public GameObject? timerRoot; // 菱形框下方的落点计时器 (整秒)
        public GameObject? radiusRoot; // 杀伤圈容器 (选中后显示红色同款虚线)
        public List<Il2CppShapes.Line> radiusSegs = new();
        public List<GameObject> salvoSegs = new(); // 齐射第三圈
        public bool salvo; // 已升级齐射 (创建了跟随任务)
        public float radiusKm = -1f;
        public int segCount = -1;
        public bool pierce = false;
        public Color color;
        public ArtilleryTask? task;
        public bool visible;
        public float lastDist = float.NaN;  // 上一帧目标距离 (TRAK 1 帧预测差分用)
        public float lastAngel = float.NaN; // 上一帧目标方位
    }
    private readonly Dictionary<Transform, TaskedMark> _marks = new();
    public IReadOnlyList<ArtilleryTask> TaskedTasks =>
        _marks.Values.Where(m => m.task != null).Select(m => m.task!).ToList();

    /// <summary>右键入队: 实体或 T1-T4 炮兵标记 (无 EntityLocation 的按标记 index 反查). 记录任务到标记并点亮双菱形+位置标签.</summary>
    public ArtilleryTask? TaskFromEntity(Transform entity)
    {
        ArtilleryTask? task;
        if (entity.GetComponent<EntityLocation>() == null) {
            // T1-T4 标记物: 虚拟目标, 没有实体单菱形, 按标记物当前位置生成任务
            int markerId = -1;
            foreach (var kv in artilleries) {
                if (kv.Value == entity) { markerId = kv.Key; break; }
            }
            if (markerId < 0) return null;
            task = GetMarkTarget(markerId);
        }
        else {
            task = TaskFromGridPosition(entity.localPosition);
        }
        if (task == null) return null;
        AttachTaskMark(entity, task);
        return task;
    }

    /// <summary>给目标挂任务标记 (点亮双菱形+位置标签). 实体与 T 标记物共用.</summary>
    public void AttachTaskMark(Transform entity, ArtilleryTask task)
    {
        var mark = GetOrCreateMark(entity);
        mark.task = task;
        mark.visible = true;
        SetMarkVisible(mark);
    }

    /// <summary>火控台按钮/键盘入队的虚拟目标: 给第 index 号 T 标记物挂任务标记.</summary>
    public void AttachMarkerTask(int index, ArtilleryTask task)
    {
        if (!artilleries.TryGetValue(index, out var marker)) return;
        AttachTaskMark(marker, task);
    }

    /// <summary>实体当前挂着的任务 (无则 null).</summary>
    public ArtilleryTask? TaskOfEntity(Transform entity)
    {
        return _marks.TryGetValue(entity, out var mark) ? mark.task : null;
    }

    /// <summary>实体当前是否已升级齐射 (三圈).</summary>
    public bool MarkIsSalvo(Transform entity)
    {
        return _marks.TryGetValue(entity, out var mark) && mark.salvo;
    }

    /// <summary>清除实体的标记 (出队/完成时), 回单菱形.</summary>
    public void ClearEntityMark(Transform entity)
    {
        if (!_marks.TryGetValue(entity, out var mark)) return;
        foreach (var seg in mark.outerSegs) {
            if (seg != null) UnityEngine.Object.Destroy(seg);
        }
        mark.outerSegs.Clear();
        foreach (var seg in mark.salvoSegs) {
            if (seg != null) UnityEngine.Object.Destroy(seg);
        }
        mark.salvoSegs.Clear();
        if (mark.labelRoot != null) UnityEngine.Object.Destroy(mark.labelRoot);
        if (mark.bulletRoot != null) UnityEngine.Object.Destroy(mark.bulletRoot);
        if (mark.timerRoot != null) UnityEngine.Object.Destroy(mark.timerRoot);
        if (mark.radiusRoot != null) UnityEngine.Object.Destroy(mark.radiusRoot);
        if (mark.ownsHolder && mark.holder != null) UnityEngine.Object.Destroy(mark.holder); // 虚拟目标自建底座一起销毁
        _marks.Remove(entity);
    }

    /// <summary>取或建实体的标记 (外圈线段预建, 默认隐藏).</summary>
    private TaskedMark GetOrCreateMark(Transform entity)
    {
        if (_marks.TryGetValue(entity, out var existing)) return existing;
        GameObject? holder = null;
        bool ownsHolder = false;
        foreach (var (collider, e) in _entityClickTargets) {
            if (e == entity) { holder = collider.gameObject; break; }
        }
        if (holder == null) {
            // T1-T4 虚拟目标: 自建底座挂 Fire Mission Root 下 (与实体挂件同空间同图层), 每帧跟随标记物 (UpdateTaskMark 同步), T 标记拖到哪火控框跟到哪
            holder = new GameObject("FCS_MarkerMarkHolder");
            var parent = fireMissionRoot != null ? fireMissionRoot : entity.parent;
            holder.transform.SetParent(parent, false);
            var lp = parent.InverseTransformPoint(entity.position);
            lp.z -= 0.02f;
            holder.transform.localPosition = lp;
            ownsHolder = true;
        }
        var loc = entity.GetComponent<EntityLocation>();
        // T1-T4 标记物无 EntityLocation, 视为敌方目标红色 (虚拟目标)
        bool hostile = loc == null || TacticalRadar.IsHostile(loc, entity);
        var color = hostile ? Color.red : Color.blue;
        var mark = new TaskedMark { holder = holder, ownsHolder = ownsHolder, entity = entity, color = color };
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
        foreach (var seg in mark.salvoSegs) seg.SetActive(mark.visible);
        if (mark.labelRoot != null) mark.labelRoot.SetActive(mark.visible);
        if (mark.radiusRoot != null) mark.radiusRoot.SetActive(mark.visible);
    }

    /// <summary>齐射标记: 双圈外再画第三圈菱形 (右键二次升级时调用).</summary>
    public void SetMarkSalvo(Transform entity, bool salvo)
    {
        if (!_marks.TryGetValue(entity, out var mark)) return;
        mark.salvo = salvo;
        foreach (var seg in mark.salvoSegs) {
            if (seg != null) UnityEngine.Object.Destroy(seg);
        }
        mark.salvoSegs.Clear();
        if (!salvo) return;
        float r3 = 0.05f * Mathf.Sqrt(2f) * 1.7f;
        var pts = new[] {
            new Vector3(0f, r3, 0f), new Vector3(r3, 0f, 0f),
            new Vector3(0f, -r3, 0f), new Vector3(-r3, 0f, 0f),
        };
        for (int s = 0; s < 4; s++) {
            var lineGo = new GameObject("FCS_EntityDiamondSegSalvo");
            lineGo.transform.SetParent(mark.holder.transform, false);
            var line = lineGo.AddComponent<Il2CppShapes.Line>();
            line.Thickness = 0.01f;
            line.Start = pts[s];
            line.End = pts[(s + 1) % 4];
            line.Color = mark.color;
            line.ColorStart = mark.color;
            line.ColorEnd = mark.color;
            mark.salvoSegs.Add(lineGo);
        }
        SetMarkVisible(mark);
    }

    /// <summary>
    /// 目标杀伤圈: 选中后显示红色同款虚线圆, 半径随任务弹种; 弹种没变不重建.
    /// 圈挂实体下随实体移动/销毁. 实体坐标系单位 = km (Fire Mission Root 1 单位 = 1 km
    /// = 0.262 板面单位, 世界缩放 = 0.262 x 板面世界缩放 0.81 ≈ 0.212): 半径/线宽/虚线长
    /// 都除以"实体世界缩放 / 板面世界缩放", 视觉与瞄准圈完全一致.
    /// </summary>
    private void SetMarkRadius(TaskedMark mark, BulletType bullet)
    {
        float rKm = ShellData.KillRadiusKm(bullet); // 游戏 ImpactRadius (半径, km), 不再手调表
        bool solid = bullet == BulletType.DRIL; // 混凝土: 整圆
        bool pierce = IsArmorPierce(bullet);    // 穿甲: 圈内 X 指示线
        float rBoard = rKm * MapCellSize;
        float holderScale = mark.holder.transform.lossyScale.x;
        float surfaceScale = mapSurface != null ? mapSurface.lossyScale.x : 1f;
        if (holderScale <= 0.0001f) holderScale = 1f;
        if (surfaceScale <= 0.0001f) surfaceScale = 1f;
        float scale = holderScale / surfaceScale; // 实体单位 -> 板面单位 的比例 (= 1 km 的板面长)
        if (mark.radiusRoot == null) {
            var go = new GameObject("FCS_MarkRadius");
            go.transform.SetParent(mark.holder.transform, false);
            mark.radiusRoot = go;
        }
        int n = KillSegCount(rBoard, solid);
        if (n == mark.segCount && pierce == mark.pierce && Mathf.Abs(rKm - mark.radiusKm) < 0.0001f) return;
        mark.radiusKm = rKm;
        mark.segCount = n;
        mark.pierce = pierce;
        foreach (var s in mark.radiusSegs) {
            if (s != null) UnityEngine.Object.Destroy(s.gameObject);
        }
        mark.radiusSegs.Clear();
        mark.radiusSegs = BuildKillCircle(mark.radiusRoot.transform, rBoard, KillDashLen, KillThick, scale, Color.red, solid, pierce);
        mark.radiusRoot.SetActive(mark.visible);
    }

    /// <summary>更新点选目标的队列位置标签: slot = L/R/1..n, null = 任务完成 (清标记回单菱形).</summary>
    public void UpdateTaskMark(ArtilleryTask task, string? slot)
    {
        TaskedMark? mark = null;
        foreach (var m in _marks.Values) {
            if (m.task == task) { mark = m; break; }
        }
        if (mark == null) return;
        // 追踪目标默认 = 任务参数本身 (静态目标); 虚拟目标下面覆盖为 1 帧预测值
        task.trackDistance = task.distance;
        task.trackAngel = task.angel;
        if (mark.ownsHolder && mark.holder != null && fireMissionRoot != null) {
            // 虚拟目标底座跟随标记物: 世界位置映射进 Fire Mission Root 局部 + 实体挂件同款 z 偏移 (浮出板面)
            var lp = fireMissionRoot.InverseTransformPoint(mark.entity.position);
            lp.z -= 0.02f;
            mark.holder.transform.localPosition = lp;
            // 任务实时跟随标记物: 按标记物当前位置重算距离/方位/位置 (动目标射击; 已上炮的落点指示随之刷新)
            if (mark.task != null) {
                int markerId = -1;
                foreach (var kv in artilleries) {
                    if (kv.Value == mark.entity) { markerId = kv.Key; break; }
                }
                if (markerId >= 0) {
                    var fresh = GetMarkTarget(markerId);
                    if (fresh != null) {
                        mark.task.distance = fresh.distance;
                        mark.task.angel = fresh.angel;
                        mark.task.position = fresh.position;
                        // TRAK 1 帧预测 (25fps): 当前 + 上一帧差分外推, PID 追预测点 (补偿机械延迟)
                        if (float.IsNaN(mark.lastDist)) {
                            mark.task.trackDistance = fresh.distance;
                            mark.task.trackAngel = fresh.angel;
                        }
                        else {
                            mark.task.trackDistance = fresh.distance + (fresh.distance - mark.lastDist);
                            mark.task.trackAngel = fresh.angel + Mathf.DeltaAngle(mark.lastAngel, fresh.angel);
                        }
                        mark.lastDist = fresh.distance;
                        mark.lastAngel = fresh.angel;
                    }
                }
            }
        }
        SetMarkRadius(mark, task.bulletType); // 杀伤圈随任务弹种
        if (mark.salvo && slot is "[L]" or "[R]") slot = "[S]"; // 齐射执行时顶部显示 [S], 队列内仍显示队列位
        if (slot == null) {
            // 任务已完成 (主炮已换目标) 但炮弹可能还在飞: 撤位置标签, 计时器留到落地
            float remain = task.fireTime > 0f ? task.impactTime - (Time.time - task.fireTime) : 0f;
            if (remain > 0f) {
                SetMarkLabel(mark, "", Mathf.RoundToInt(remain).ToString()); // 四舍五入: 1.9=2, 1.1=1, 能数到 0
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
                if (remain > 0f) timer = Mathf.RoundToInt(remain).ToString(); // 四舍五入: 1.9=2, 1.1=1, 能数到 0
            }
            SetMarkLabel(mark, slot, timer);
        }
    }

    // 十六段米字数码标签: 字符宽高与段定义 (单位格: 宽 1, 高 1.6)
    private const float LabelSegW = 0.045f; // 字符宽

    /// <summary>
    /// 重建标签: 上方队列位置 + 下方落点计时器 (整秒), 十六段数码线段平贴板面.
    /// 字号缩小 1/6 (最终 5/6), 标签上移 / 计时器下移 1/8 原字号 (给杀伤圈让位).
    /// </summary>
    private static void SetMarkLabel(TaskedMark mark, string slot, string? timer)
    {
        float segW = LabelSegW * 5f / 6f; // 字号: 原 5/6
        float step = segW * 1.4f;         // 字符间距 (两位数字离太近看不清)
        float dy = LabelSegW / 8f + 0.0222f; // 1/8 原字号 + 1/5 小格

        if (mark.labelRoot != null) UnityEngine.Object.Destroy(mark.labelRoot);
        mark.labelRoot = null;
        if (!string.IsNullOrEmpty(slot)) {
            var root = new GameObject("FCS_EntityLabelLines");
            root.transform.SetParent(mark.holder.transform, false);
            root.transform.localPosition = new Vector3(-((slot.Length - 1) * step + segW) / 2f, 0.14f + dy, 0f);
            for (int i = 0; i < slot.Length; i++) {
                DrawCharSegments(root.transform, slot[i], mark.color, i * step, segW);
            }
            mark.labelRoot = root;
        }

        // 序列上方: 本次任务的弹种标签 (16 段字形, 最多 4 字符)
        if (mark.bulletRoot != null) UnityEngine.Object.Destroy(mark.bulletRoot);
        mark.bulletRoot = null;
        if (mark.task != null && !string.IsNullOrEmpty(slot)) {
            string bt = mark.task.bulletType.ToString();
            var bRoot = new GameObject("FCS_EntityBulletLines");
            bRoot.transform.SetParent(mark.holder.transform, false);
            bRoot.transform.localPosition = new Vector3(-((bt.Length - 1) * step + segW) / 2f, 0.14f + dy + segW * 1.6f + (step - segW) + 0.02775f, 0f); // 行间距 = 字间距 + 再上抬 1/4 小格 (避开杀伤圈)
            for (int i = 0; i < bt.Length; i++) {
                DrawCharSegments(bRoot.transform, bt[i], mark.color, i * step, segW);
            }
            mark.bulletRoot = bRoot;
        }

        if (mark.timerRoot != null) UnityEngine.Object.Destroy(mark.timerRoot);
        mark.timerRoot = null;
        if (!string.IsNullOrEmpty(timer)) {
            var tRoot = new GameObject("FCS_EntityTimerLines");
            tRoot.transform.SetParent(mark.holder.transform, false);
            tRoot.transform.localPosition = new Vector3(-((timer.Length - 1) * step + segW) / 2f, -0.2f - dy, 0f);
            for (int i = 0; i < timer.Length; i++) {
                DrawCharSegments(tRoot.transform, timer[i], mark.color, i * step, segW);
            }
            mark.timerRoot = tRoot;
        }
    }

    /// <summary>
    /// TM1629A 十六段米字数码管: 段坐标 (单位格: 宽 1, 高 1.6, 中心 0.5,0.8) 与笔画位映射.
    /// 微调字形直接改下方 Glyph16: ['X'] = 段名按位或, 例如 ['L'] = F|E|D1|D2.
    /// 笔画位与布局 (bit0=a1 ... bit15=m):
    ///      a1|a2
    ///    f   h   b
    ///      g1|g2
    ///    e   i   c
    ///      d1|d2
    /// 对角: j 左上->中, k 右上->中, l 左下->中, m 右下->中
    /// </summary>
    private static readonly Dictionary<string, (Vector2, Vector2)> Seg16 = new() {
        ["a1"] = (new Vector2(0.05f, 1.6f), new Vector2(0.5f, 1.6f)),
        ["a2"] = (new Vector2(0.5f, 1.6f), new Vector2(0.95f, 1.6f)),
        ["b"] = (new Vector2(0.95f, 1.6f), new Vector2(0.95f, 0.8f)),
        ["c"] = (new Vector2(0.95f, 0.8f), new Vector2(0.95f, 0f)),
        ["d1"] = (new Vector2(0.05f, 0f), new Vector2(0.5f, 0f)),
        ["d2"] = (new Vector2(0.5f, 0f), new Vector2(0.95f, 0f)),
        ["e"] = (new Vector2(0.05f, 0.8f), new Vector2(0.05f, 0f)),
        ["f"] = (new Vector2(0.05f, 1.6f), new Vector2(0.05f, 0.8f)),
        ["g1"] = (new Vector2(0.05f, 0.8f), new Vector2(0.5f, 0.8f)),
        ["g2"] = (new Vector2(0.5f, 0.8f), new Vector2(0.95f, 0.8f)),
        ["h"] = (new Vector2(0.5f, 0.8f), new Vector2(0.5f, 1.6f)),
        ["i"] = (new Vector2(0.5f, 0.8f), new Vector2(0.5f, 0f)),
        ["j"] = (new Vector2(0.05f, 1.6f), new Vector2(0.5f, 0.8f)),
        ["k"] = (new Vector2(0.95f, 1.6f), new Vector2(0.5f, 0.8f)),
        ["l"] = (new Vector2(0.05f, 0f), new Vector2(0.5f, 0.8f)),
        ["m"] = (new Vector2(0.95f, 0f), new Vector2(0.5f, 0.8f)),
    };

    // 笔画位定义: ushort 掩码, bit N = 第 N 段, 与 SegBit16 顺序一致 (0-7 外框, 8-11 中间十字, 12-15 对角)
    private const ushort A1 = 1 << 0, A2 = 1 << 1, B = 1 << 2, C = 1 << 3,
                        D1 = 1 << 4, D2 = 1 << 5, E = 1 << 6, F = 1 << 7,
                        G1 = 1 << 8, G2 = 1 << 9, H = 1 << 10, I = 1 << 11,
                        J = 1 << 12, K = 1 << 13, L = 1 << 14, M = 1 << 15;

    private static readonly string[] SegBit16 = { "a1", "a2", "b", "c", "d1", "d2", "e", "f", "g1", "g2", "h", "i", "j", "k", "l", "m" };

    /// <summary>
    /// 可打印 ASCII (0x20-0x7E) 字形表, ushort 位掩码按 SegBit16 点亮.
    /// 无法表达的字符 (如 ~) 置 0 显示空白.
    /// </summary>
    private static readonly Dictionary<char, ushort> Glyph16 = new() {
        // ---- 数字 ----
        ['0'] = A1|A2|B|C|D1|D2|E|F,
        ['1'] = B|C,
        ['2'] = A1|A2|B|G1|G2|E|D1|D2,
        ['3'] = A1|A2|B|G1|G2|C|D1|D2,
        ['4'] = F|G1|G2|B|C,
        ['5'] = A1|A2|F|G1|G2|C|D1|D2,
        ['6'] = A1|A2|F|E|G1|G2|C|D1|D2,
        ['7'] = A1|A2|B|C,
        ['8'] = A1|A2|B|C|D1|D2|E|F|G1|G2,
        ['9'] = A1|A2|F|G1|G2|B|C|D1|D2,
        // ---- 大写字母 ----
        ['A'] = A1|A2|B|C|E|F|G1|G2,
        ['B'] = A1|A2|F|E|G1|D1|D2|K|M,   // 左竖 + 三横 + 右侧斜边 k/m
        ['C'] = A1|A2|D1|D2|E|F,
        ['D'] = F|E|J|L,   // 左竖 + 左侧尖角 j/l (上 \ 下 /)
        ['E'] = A1|A2|F|G1|G2|E|D1|D2,
        ['F'] = A1|A2|F|G1|G2|E,
        ['G'] = A1|A2|F|E|G2|C|D1|D2,
        ['H'] = F|E|G1|G2|B|C,
        ['I'] = A1|A2|H|I|D1|D2,
        ['J'] = A1|A2|H|I|D1,   // 上横 + 中竖 + 左下钩
        ['K'] = F|E|G1|K|M,   // 中横只留左半
        ['L'] = F|E|D1|D2,
        ['M'] = F|E|J|K|B|C,
        ['N'] = F|E|K|L|B|C,
        ['O'] = A1|A2|B|C|D1|D2|E|F,
        ['P'] = A1|A2|F|E|G1|G2|B,
        ['Q'] = A1|A2|B|C|D1|D2|E|F|M,
        ['R'] = A1|A2|F|E|G1|G2|B|M,   // 上横 + 左竖全 f/e + 中横 g1g2 + 右上竖 b + 斜腿 m
        ['S'] = A1|A2|J|M|D1|D2,   // Z 镜像形, 与 5 区分 (齐射 S 标记用)
        ['T'] = A1|A2|H|I,
        ['U'] = F|E|B|C|D1|D2,
        ['V'] = F|E|K|L,   // 左竖 f/e + 整条斜线 k/l (右上到左下)
        ['W'] = E|F|B|C|J|K|L|M,
        ['X'] = J|K|L|M,
        ['Y'] = J|K|I,
        ['Z'] = A1|A2|K|L|D1|D2,
        // ---- 小写字母 (暂时用不上, 未调试, a 已知有误待定) ----
        ['a'] = E|F|G1|G2|C|D1|D2,
        ['b'] = F|E|G1|G2|C|D1|D2,
        ['c'] = E|G1|G2|D1|D2,
        ['d'] = B|C|G1|G2|E|D1|D2,
        ['e'] = A1|F|E|G1|G2|D1|D2,
        ['f'] = A1|F|G1|G2|E,
        ['g'] = A1|A2|F|G1|G2|C|D1|D2,
        ['h'] = F|E|G1|G2|C,
        ['i'] = I,
        ['j'] = C|I|D1|D2,
        ['k'] = F|E|G1|G2|K|M,
        ['l'] = F|E,
        ['m'] = E|F|G1|G2|H|B|C,
        ['n'] = E|G1|G2|C,
        ['o'] = E|C|G1|G2|D1|D2,
        ['p'] = A1|F|E|G1|G2|B,
        ['q'] = A1|A2|B|C|G1|G2|D1|D2,
        ['r'] = E|G1|G2,
        ['s'] = A1|A2|F|G1|G2|C|D1|D2,
        ['t'] = F|E|G1|G2|I,
        ['u'] = E|C|D1|D2,
        ['v'] = J|K,
        ['w'] = E|C|J|K|L|M,
        ['x'] = K|L,
        ['y'] = K|L|I,
        ['z'] = A1|A2|G1|G2|D1|D2,
        // ---- 符号 ----
        [' '] = 0,
        ['!'] = H|D2,   // 中上竖 + 右半底横
        ['"'] = F|B,
        ['#'] = F|E|B|C|G1|G2,
        ['$'] = A1|A2|F|G1|G2|C|D1|D2|H|I,
        ['%'] = F|C|K|L,
        ['&'] = A1|F|E|G1|G2|C|D1|D2,
        ['\''] = H,   // 中上竖
        ['('] = A1|F|E,   // 左上横 + 左竖两段
        [')'] = B|C|D2,   // 右竖两段 + 右下横
        ['*'] = J|K|L|M|H|I,
        ['+'] = G1|G2|H|I,
        [','] = L,
        ['-'] = G1|G2,
        ['.'] = G1|D1|E|I,   // 左下角画圈: g1 上 / d1 下 / e 左 / i 右
        ['/'] = K|L,
        [':'] = G1|D1,   // 左中横 + 左下横
        [';'] = A1|L,   // 逗号 + 左上横
        ['<'] = J|L,
        ['='] = G1|G2|D1|D2,
        ['>'] = K|M,
        ['?'] = A1|A2|F|K|I,   // 上横 + 左竖上半 + 半斜线(k 右上到中) + 中下竖
        ['@'] = A1|A2|B|C|E|F|G2|D2|I,   // 无底横的 0 + 右下角圈 (g2 d2 c i)
        ['['] = A2|D2|H|I,   // 右半横 (钩朝右) + 中竖
        ['\\'] = J|M,
        [']'] = A1|D1|H|I,   // 左半横 (钩朝左) + 中竖
        ['^'] = L|M,   // 下面两条斜杠 l/m 组成尖朝上的 ^
        ['_'] = D1|D2,
        ['`'] = J,
        ['{'] = A2|G1|D2|H|I,
        ['|'] = H|I,
        ['}'] = A1|G2|D1|H|I,
        ['~'] = A1|G1|F|H,  // 左上角圈: 当 ° 用 (a1 上 / g1 下 / f 左 / h 右)
    };

    /// <summary>画一个字符: scale = 字符宽 (板面单位), 字形高 1.6 x scale, 笔画粗随字号同比缩放 (实体标签基准 0.008).</summary>
    private static void DrawCharSegments(Transform parent, char ch, Color color, float x0, float scale)
    {
        if (!Glyph16.TryGetValue(ch, out var mask)) mask = A1|A2|B|C|D1|D2|E|F|G1|G2; // 未知字符全亮 (8 形)
        for (int bit = 0; bit < SegBit16.Length; bit++) {
            if ((mask & (1 << bit)) == 0) continue;
            var (a, b) = Seg16[SegBit16[bit]];
            var go = new GameObject("FCS_LabelSeg");
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<Il2CppShapes.Line>();
            line.Thickness = 0.008f * (scale / LabelSegW);
            line.Start = new Vector3(x0 + a.x * scale, a.y * scale, 0f);
            line.End = new Vector3(x0 + b.x * scale, b.y * scale, 0f);
            line.Color = color;
            line.ColorStart = color;
            line.ColorEnd = color;
        }
    }

    /// <summary>
    /// 火炮瞄准指示: 左炮十字 / 右炮 X, 挂在板面上, 位置跟游戏落点标记.
    /// 四条臂中空 (CS 准星式) 且等长; 外圈绿色虚线 = 当前任务弹种的杀伤半径.
    /// </summary>
    private AimMark? _aimLeft;   // 左炮十字
    private AimMark? _aimRight;  // 右炮 X
    private Transform? _impactLeft;
    private Transform? _impactRight;

    /// <summary>瞄准标记: 根物体 + 杀伤圈容器/段列表 + 上次半径 (km)/段数/穿甲标志 (变了才重建).</summary>
    private class AimMark {
        public GameObject root = null!;
        public Transform radiusRoot = null!;
        public List<Il2CppShapes.Line> radiusSegs = new();
        public float radiusKm = -1f;
        public int segCount = -1;
        public bool pierce = false;
        public Color color;
    }

    public void SpawnAimMarks()
    {
        if (mapSurface == null) return;
        // F9 热重载清理: 旧程序集的挂件引用已丢但物件还在场景里, 按名字清掉
        foreach (var old in Resources.FindObjectsOfTypeAll<GameObject>()) {
            if (old != null && (old.name == "FCS_AimLeft" || old.name == "FCS_AimRight")) UnityEngine.Object.Destroy(old);
        }
        _impactLeft = GameObject.Find("GunLeft_ImpactMarker")?.transform;
        _impactRight = GameObject.Find("GunRight_ImpactMarker")?.transform;
        if (_aimLeft != null) UnityEngine.Object.Destroy(_aimLeft.root);
        if (_aimRight != null) UnityEngine.Object.Destroy(_aimRight.root);
        _aimLeft = BuildAimMark(mapSurface, true, Color.green);  // 左炮: 十字 + 左横线 L 字标
        _aimRight = BuildAimMark(mapSurface, false, Color.green); // 右炮: 十字 + 右横线 R 字标 (齐射时区分两炮)
        _aimLeft.root.SetActive(false);
        _aimRight.root.SetActive(false);
    }

    /// <summary>
    /// 构造瞄准标记: 左右炮统一十字, 四臂等长 armLen, 中心留 gap 中空; 臂与杀伤圈均粗 0.005.
    /// 识别字标: 左炮左臂横线延伸 + L 字标骑在线上, 右炮右侧 + R (齐射时区分两炮).
    /// </summary>
    private static AimMark BuildAimMark(Transform parent, bool isLeft, Color color)
    {
        var mark = new AimMark();
        var root = new GameObject(isLeft ? "FCS_AimLeft" : "FCS_AimRight");
        root.transform.SetParent(parent, false);
        mark.root = root;
        const float gap = 0.02f;     // 中心中空半径 (CS 准星式)
        const float armLen = 0.045f; // 臂长
        Vector2[] dirs = { new Vector2(0f, 1f), new Vector2(0f, -1f), new Vector2(1f, 0f), new Vector2(-1f, 0f) };
        foreach (var d in dirs) {
            var go = new GameObject("FCS_AimSeg");
            go.transform.SetParent(root.transform, false);
            var line = go.AddComponent<Il2CppShapes.Line>();
            line.Thickness = 0.005f;
            line.Start = new Vector3(d.x * gap, d.y * gap, 0f);
            line.End = new Vector3(d.x * (gap + armLen), d.y * (gap + armLen), 0f);
            line.Color = color;
            line.ColorStart = color;
            line.ColorEnd = color;
        }
        // 左右炮识别: L 在左臂端 / R 在右臂端, 字形中心骑在横线上 (不延长横线)
        const float tagScale = 0.0225f; // L/R 字标宽 (0.03 缩到 3/4)
        float side = isLeft ? -1f : 1f;
        float armEnd = gap + armLen;
        var tagRoot = new GameObject("FCS_AimTag");
        tagRoot.transform.SetParent(root.transform, false);
        float tagCenter = side * (armEnd + tagScale * 1.5f); // 臂端外再让一个字符宽度, 不压线
        tagRoot.transform.localPosition = new Vector3(tagCenter - tagScale * 0.5f, -0.8f * tagScale, 0f); // 字形中心骑线
        DrawCharSegments(tagRoot.transform, isLeft ? 'L' : 'R', color, 0f, tagScale);
        // 杀伤圈容器: 段在 SetAimRadius 按弹种重建 (半径定段数, 保持虚线周期一致)
        var circle = new GameObject("FCS_AimRadius");
        circle.transform.SetParent(root.transform, false);
        mark.radiusRoot = circle.transform;
        mark.color = color;
        return mark;
    }

    /// <summary>带穿甲效果的弹种: 圈内加 X 型穿甲指示线.</summary>
    private static bool IsArmorPierce(BulletType bt) => bt switch {
        BulletType.AP or BulletType.APHE or BulletType.EQKE or BulletType.ATMC => true,
        _ => false,
    };

    /// <summary>实时更新瞄准标记: 位置跟落点标记, 显示跟 CanFire, 杀伤圈跟任务弹种.</summary>
    public void UpdateAimMarks(bool leftCanFire, bool rightCanFire, BulletType? leftBullet, BulletType? rightBullet)
    {
        if (_aimLeft == null || _aimRight == null) return;
        UpdateAimMark(_aimLeft, _impactLeft, leftCanFire, leftBullet);
        UpdateAimMark(_aimRight, _impactRight, rightCanFire, rightBullet);
    }

    private static void UpdateAimMark(AimMark mark, Transform? impact, bool canFire, BulletType? bullet)
    {
        if (mark == null) return;
        SetAimRadius(mark, bullet); // 杀伤圈先于显示判断更新, 重新可见时半径不滞后
        if (!canFire || impact == null) {
            if (mark.root.activeSelf) mark.root.SetActive(false);
            return;
        }
        var grid = impact.localPosition;
        // 板界: 网格 0-20 x 0-10 大格, 外扩 1 小格死区; 出界不画 (别画出桌子)
        const float margin = 1f / 9f;
        if (grid.x < -margin || grid.x > 20f + margin || grid.y < -margin || grid.y > 10f + margin) {
            if (mark.root.activeSelf) mark.root.SetActive(false);
            return;
        }
        mark.root.transform.localPosition = new Vector3(MapBottomLeft.x + grid.x * MapCellSize, MapBottomLeft.y + grid.y * MapCellSize, -0.03f);
        if (!mark.root.activeSelf) mark.root.SetActive(true);
    }

    // 杀伤圈画法参数: 虚线实/空长 0.01 板面单位 (与线宽解耦, 各弹种虚线段数固定);
    // 圈线视觉宽 = 菱形框视觉宽 (菱形线宽 0.01 是实体局部值 = 0.01 km = 0.01 x MapCellSize 板面单位)
    private const float KillDashLen = 0.01f;
    private static readonly float KillThick = 0.01f * MapCellSize;

    /// <summary>
    /// 手排虚线圆: 短实线弧 + 留空 (游戏 Dashed 按整条线排周期, 短段上显不出虚线),
    /// 虚线整体旋转与段数相关: 90°/n = 360/2n/2 (HE 12 段 = 7.5°, AP 8 段 = 11.25°). solid = 整圆.
    /// r/dash/thick 传板面真实值; scale = 挂载父级的缩放, 局部系尺寸 = 真值 / scale.
    /// </summary>
    private static List<Il2CppShapes.Line> BuildKillCircle(Transform parent, float rBoard, float dashBoard, float thickBoard, float scale, Color color, bool solid, bool pierce)
    {
        var segs = new List<Il2CppShapes.Line>();
        int n = solid ? 24 : Mathf.Max(4, (int)Mathf.Round(2f * Mathf.PI * rBoard / (2f * dashBoard)));
        if (!solid && (n & 1) != 0) n--; // 虚线段数取偶数: 奇数段图案不轴对称, 看着歪
        float r = rBoard / scale;
        float t = thickBoard / scale;
        float dash = dashBoard / scale;
        for (int s = 0; s < n; s++) {
            float a0 = Mathf.PI * 2f * s / n + (solid ? 0f : Mathf.PI / (2f * n)); // 各圈各自转 90°/n
            float a1 = a0 + (solid ? Mathf.PI * 2f / n : dash / r); // 实线覆盖角
            var go = new GameObject("FCS_RadiusSeg");
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<Il2CppShapes.Line>();
            line.Thickness = t;
            line.Dashed = false; // 虚靠排空, 不靠游戏 Dashed
            line.Color = color;
            line.ColorStart = color;
            line.ColorEnd = color;
            line.Start = new Vector3(Mathf.Cos(a0) * r, Mathf.Sin(a0) * r, 0f);
            line.End = new Vector3(Mathf.Cos(a1) * r, Mathf.Sin(a1) * r, 0f);
            segs.Add(line);
        }
        if (pierce) {
            // 穿甲指示: 从圆周向内 4 条半半径长线, X 型 (对角方向)
            Vector2[] xdirs = { new Vector2(1f, 1f).normalized, new Vector2(-1f, 1f).normalized,
                                new Vector2(1f, -1f).normalized, new Vector2(-1f, -1f).normalized };
            foreach (var d in xdirs) {
                var go = new GameObject("FCS_RadiusSeg");
                go.transform.SetParent(parent, false);
                var line = go.AddComponent<Il2CppShapes.Line>();
                line.Thickness = t;
                line.Dashed = false;
                line.Color = color;
                line.ColorStart = color;
                line.ColorEnd = color;
                line.Start = new Vector3(d.x * r, d.y * r, 0f);               // 圆周
                line.End = new Vector3(d.x * (r * 0.5f), d.y * (r * 0.5f), 0f); // 向内半半径
                segs.Add(line);
            }
        }
        return segs;
    }

    /// <summary>杀伤圈段数: 虚线按 KillDashLen 排, 混凝土整圆 24 段.</summary>
    private static int KillSegCount(float rBoard, bool solid)
        => solid ? 24 : Mathf.Max(4, (int)Mathf.Round(2f * Mathf.PI * rBoard / (2f * KillDashLen)));

    /// <summary>按弹种设置瞄准杀伤圈: 半径 = 直径/2 (km) x 格长, 弹种没变不重建.</summary>
    private static void SetAimRadius(AimMark mark, BulletType? bullet)
    {
        if (bullet == null) return; // 无任务保持上次半径
        float rKm = ShellData.KillRadiusKm(bullet.Value); // 游戏 ImpactRadius (半径, km), 不再手调表
        bool solid = bullet.Value == BulletType.DRIL; // 混凝土: 整圆
        bool pierce = IsArmorPierce(bullet.Value);    // 穿甲: 圈内 X 指示线
        float r = rKm * MapCellSize;
        int n = KillSegCount(r, solid);
        if (n == mark.segCount && pierce == mark.pierce && Mathf.Abs(rKm - mark.radiusKm) < 0.0001f) return;
        mark.radiusKm = rKm;
        mark.segCount = n;
        mark.pierce = pierce;
        foreach (var s in mark.radiusSegs) {
            if (s != null) UnityEngine.Object.Destroy(s.gameObject);
        }
        mark.radiusSegs.Clear();
        mark.radiusSegs = BuildKillCircle(mark.radiusRoot, r, KillDashLen, KillThick, 1f, mark.color, solid, pierce); // 瞄准圈挂板面下, 缩放 1
    }

    /// <summary>铁巢 → 当前目标虚线 (左右炮各一条, 荧光绿 0.006).</summary>
    private GameObject? _targetLineLeft;
    private GameObject? _targetLineRight;
    private Il2CppShapes.Line? _targetLineL;
    private Il2CppShapes.Line? _targetLineR;

    public void SpawnTargetLines()
    {
        if (mapSurface == null) return;
        // F9 热重载清理: 旧程序集的挂件引用已丢但物件还在场景里, 按名字清掉
        foreach (var old in Resources.FindObjectsOfTypeAll<GameObject>()) {
            if (old != null && (old.name == "FCS_TargetLineLeft" || old.name == "FCS_TargetLineRight")) UnityEngine.Object.Destroy(old);
        }
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
            if (mark.ownsHolder) {
                // T1-T4 虚拟目标: 标记物在板面局部系, 反解成网格坐标 (实体 localPosition 本来就是网格系, 直接可用)
                Vector2 lp = mark.entity.localPosition;
                targetGrid = new Vector2((lp.x - MapBottomLeft.x) / MapCellSize, (lp.y - MapBottomLeft.y) / MapCellSize);
            }
            else {
                targetGrid = mark.entity.localPosition; // 实体实时位置
            }
        }
        else {
            float rad = task.angel * Mathf.Deg2Rad;
            targetGrid = nest + new Vector2(Mathf.Sin(rad), Mathf.Cos(rad)) * task.distance; // 1 网格 = 1 km
        }
        // z=-0.03 与瞄准十字同层: 平贴板面 (z=0) 会被生成的侦察照片盖住
        var startLocal = new Vector3(MapBottomLeft.x + nest.x * MapCellSize, MapBottomLeft.y + nest.y * MapCellSize, -0.03f);
        var endLocal = new Vector3(MapBottomLeft.x + targetGrid.x * MapCellSize, MapBottomLeft.y + targetGrid.y * MapCellSize, -0.03f);
        go.transform.localPosition = startLocal;
        line.Start = Vector3.zero;
        line.End = endLocal - startLocal;
        if (!go.activeSelf) go.SetActive(true);
    }

    /// <summary>T1-T4 炮兵标记物 (marker transform, 编号), 注册右键入队用.</summary>
    public IReadOnlyList<(Transform marker, int id)> ArtilleryMarkers
    {
        get
        {
            var list = new List<(Transform, int)>();
            foreach (var kv in artilleries) list.Add((kv.Value, kv.Key));
            return list;
        }
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
        // F9 热重载清理: 旧程序集的挂件引用已丢但物件还在场景里, 按名字清掉 (杀伤圈等挂件随底座一起销毁)
        foreach (var old in Resources.FindObjectsOfTypeAll<GameObject>()) {
            if (old != null && (old.name == "FCS_EntityDiamond" || old.name == "FCS_MarkerMarkHolder")) UnityEngine.Object.Destroy(old);
        }
        _entityClickTargets.Clear();
        _entityMarks.Clear();
        if (fireMissionRoot == null) return;
        for (int i = 0; i < fireMissionRoot.childCount; i++) {
            var child = fireMissionRoot.GetChild(i);
            var loc = child.GetComponent<EntityLocation>();
            if (loc == null) continue;
            SpawnDiamondFor(child, loc);
        }
        MelonLogger.Msg("[FCS] SpawnEntityDiamonds: done");
    }

    /// <summary>给单个实体挂标识 (holder + 点击 collider): 敌红/友蓝菱形, 参考点绿十字. 中心标识线: 装甲=内正方形, FDC=六芒星, 炮兵=圆. 全部纯线, 与边框同宽.</summary>
    private void SpawnDiamondFor(Transform child, EntityLocation loc)
    {
        bool hostile = TacticalRadar.IsHostile(loc, child);
        string icon = TacticalRadar.GetIcon(loc);
        bool isRef = icon.Contains("Refrence"); // 参考点 (游戏拼错 Reference): 不是友军, 绿色十字
        var color = isRef ? Color.green : (hostile ? Color.red : Color.blue);
        var (armour, immune) = TacticalRadar.GetArmour(loc);
        bool armoured = armour > 0 || immune > 0; // 装甲 = 有装甲值或免疫弹种 (只有非免疫弹种能处理)
        // FDC 炮兵指挥中心: 打掉敌方打击计时器暂停. 不同关卡的 icon 名不一样 (Fire Direction Center / Artillery Observer), 实体名兜底 (fdc#/enemyfdc#)
        bool isFdc = icon.Contains("Fire Direction Center") || icon.Contains("Artillery Observer") || child.name.ToLower().Contains("fdc");
        bool isArty = icon.Contains("Field Artillery") || icon.Contains("Arty Turret"); // 炮兵: 打掉敌方打击计时器延长
        bool isAa = icon.Contains("AA") || child.name.ToLower().Contains("aa"); // 防空炮: 两个短竖线
        MelonLogger.Msg($"[FCS] Diamond: {child.name} hostile={hostile} ref={isRef} armoured={armoured} armour={armour} immune={immune} fdc={isFdc} arty={isArty} aa={isAa}");
        var holder = new GameObject("FCS_EntityDiamond");
        holder.transform.SetParent(child, false);
        holder.transform.localPosition = new Vector3(0f, 0f, -0.02f);
        var col = holder.AddComponent<BoxCollider>();
        col.size = new Vector3(0.15f, 0.15f, 0.02f);
        if (!isRef) _entityClickTargets.Add((col, child)); // 参考点不是目标, 不注册右键入队
        _entityMarks.Add((holder, loc, child.gameObject));
        if (isRef) {
            // 参考点: 绿色十字 (横竖两线), 半臂 0.05 与菱形视觉匹配
            var cross = new[] {
                (new Vector3(-0.05f, 0f, 0f), new Vector3(0.05f, 0f, 0f)),
                (new Vector3(0f, -0.05f, 0f), new Vector3(0f, 0.05f, 0f)),
            };
            foreach (var (a, b) in cross) {
                var lineGo = new GameObject("FCS_EntityRefSeg");
                lineGo.transform.SetParent(holder.transform, false);
                var line = lineGo.AddComponent<Il2CppShapes.Line>();
                line.Thickness = 0.01f;
                line.Start = a;
                line.End = b;
                line.Color = color;
                line.ColorStart = color;
                line.ColorEnd = color;
            }
            return;
        }
        float r = 0.05f * Mathf.Sqrt(2f); // 菱形半对角线 (正方形边长 0.1)
        var pts = new[] {
            new Vector3(0f, r, 0f), new Vector3(r, 0f, 0f),
            new Vector3(0f, -r, 0f), new Vector3(-r, 0f, 0f),
        };
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
        if (armoured) {
            // 装甲: 正方与菱形叠加, 边长一致 (正方形边长 = 菱形边长 0.1, 顶点共圆交错 45°). 同心套圈已被任务/齐射标记占用, 方菱叠加不与任何现有标记冲突.
            var sq = new[] {
                new Vector3(0.05f, 0.05f, 0f), new Vector3(-0.05f, 0.05f, 0f),
                new Vector3(-0.05f, -0.05f, 0f), new Vector3(0.05f, -0.05f, 0f),
            };
            AddEntityMarkLines(holder.transform, sq, "FCS_EntityArmourSeg", color);
        }
        if (isFdc) {
            // FDC: 六芒星* (三条线 60° 交叉, 每线横穿中心一整段), 与装甲正方形可叠加. 半径 0.0267 (0.04 缩小 1/3)
            const float rh = 0.0267f;
            for (int a = 0; a < 3; a++) {
                float ang = Mathf.PI / 3f * a;
                var dir = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                var lineGo = new GameObject("FCS_EntityFdcSeg");
                lineGo.transform.SetParent(holder.transform, false);
                var line = lineGo.AddComponent<Il2CppShapes.Line>();
                line.Thickness = 0.01f;
                line.Start = dir * rh;
                line.End = dir * -rh;
                line.Color = color;
                line.ColorStart = color;
                line.ColorEnd = color;
            }
        }
        if (isArty) {
            // 炮兵: 实线圆 (16 段折线, 游戏自带 Dashed 圆在短段上失效, 手写实线). 半径 0.0267 (0.04 缩小 1/3)
            const int n = 16;
            var circle = new Vector3[n];
            for (int i = 0; i < n; i++) {
                float a = Mathf.PI * 2f * i / n;
                circle[i] = new Vector3(Mathf.Sin(a) * 0.0267f, Mathf.Cos(a) * 0.0267f, 0f);
            }
            AddEntityMarkLines(holder.transform, circle, "FCS_EntityArtySeg", color);
        }
        if (isAa) {
            // 防空炮: 中间两根平行短竖线 (半长 0.0267 与星/圆一致, 间距 ±0.02) + 底座横线 (两倍粗, 竖线 3/4 高处)
            for (int s = -1; s <= 1; s += 2) {
                var lineGo = new GameObject("FCS_EntityAaSeg");
                lineGo.transform.SetParent(holder.transform, false);
                var line = lineGo.AddComponent<Il2CppShapes.Line>();
                line.Thickness = 0.01f;
                line.Start = new Vector3(s * 0.02f, -0.0267f, 0f);
                line.End = new Vector3(s * 0.02f, 0.0267f, 0f);
                line.Color = color;
                line.ColorStart = color;
                line.ColorEnd = color;
            }
            var baseGo = new GameObject("FCS_EntityAaBaseSeg");
            baseGo.transform.SetParent(holder.transform, false);
            var baseLine = baseGo.AddComponent<Il2CppShapes.Line>();
            baseLine.Thickness = 0.02f;
            baseLine.Start = new Vector3(-0.02f, -0.0133f, 0f); // 竖线从顶往下 3/4 全高 (靠下): -0.0267 + 0.25*0.0534
            baseLine.End = new Vector3(0.02f, -0.0133f, 0f);
            baseLine.Color = color;
            baseLine.ColorStart = color;
            baseLine.ColorEnd = color;
        }
    }

    /// <summary>实体标记线段组: pts 按序首尾相连, 与菱形框同宽同色.</summary>
    private static void AddEntityMarkLines(Transform parent, Vector3[] pts, string name, Color color)
    {
        for (int s = 0; s < pts.Length; s++) {
            var lineGo = new GameObject(name);
            lineGo.transform.SetParent(parent, false);
            var line = lineGo.AddComponent<Il2CppShapes.Line>();
            line.Thickness = 0.01f;
            line.Start = pts[s];
            line.End = pts[(s + 1) % pts.Length];
            line.Color = color;
            line.ColorStart = color;
            line.ColorEnd = color;
        }
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
}
