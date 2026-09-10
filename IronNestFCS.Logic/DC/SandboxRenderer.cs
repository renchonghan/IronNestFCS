using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// [DC] SandboxRenderer — 2.0 渲染线程 (独立循环, 每帧, 迁移期未接线).
/// 持续读各数据源画沙盘: GC/FC push 的实时弹道 (绿十字+瞄准圈) / FC 的打击队列信息 (红杀伤圈+编号+预瞄线) /
/// 炮弹落点指示器 (红线: 虚线全长固定 + 实线未飞段渐短 + 实心红点弹头 + 红落点圈) / 实体图标 (差集回调).
/// 分层 (沿用旧版, 显示手感不变): 绿十字+瞄准圈 z=0 (最上) / 落点指示器+实体图标 z=-0.02 (浮一层) / 红杀伤圈+编号 z=-0.03 (贴板).
/// 3D 物件生命周期: 实体死亡/任务取消/令牌离图 → 清退; F9 → 全清.
/// </summary>
public class SandboxRenderer {
    public Transform? MapSurfaceRef; // "Draggable Surface" (板面局部系, 板面层挂件母体)
    public Transform? FireMissionRootRef; // "Fire Mission Root" (实体挂件母体 — 旧版菱形框同空间, 实体位置在此空间才是板面坐标)
    public Transform? NestRef;       // 铁巢/炮塔 (预瞄线/落点线起点)

    // ===== 3D 指示器状态 =====
    private readonly Dictionary<GameObject, QueueIndicator> _queueMarks = new();
    private readonly Dictionary<LeftRight, ImpactIndicator> _impacts = new(); // 恒定实体: 每炮一套 (虚线/实线/点), 不用就隐藏
    private readonly Dictionary<LeftRight, TrackIndicator> _tracks = new();     // 火控目标线 (开火前解算, 绿点线) 每炮一条
    private readonly Dictionary<LeftRight, TrackIndicator> _finals = new();     // 最终轨迹线 (击发冻结缩短, 绿点线) 每炮一条
    private const float TrackDotDia = 0.006f; // 轨迹点直径 (= 线宽 0.006); 点距 = 均分 len/(n-1) (最小 0.008, 无上限, 最多 32 点)
    private readonly Dictionary<GameObject, IconEntry> _icons = new();
    private readonly Dictionary<LeftRight, BallisticMark> _ballistic = new();

    private object? _loopHandle;
    private bool _disposed;

    // 层级偏移量 (相对板面 z=0, 越负越浮): 所有标记一律挂板面 —
    // 游戏大地图在实体层上堆叠照片 (改透明度), 挂实体的东西会被盖淡/消失, 只能挂板面.
    // ===== 渲染分层 (画画模型: queue 数值大 = 后渲染, 后画的笔迹盖在上面) =====
    // renderQueue = QueueTop − prio: 绿层 5000 = 队列上限最后一笔画, 永远显示在最上; 红层 4998 最先画垫底.
    // z = ZStep × prio 是同 queue 内的次级深度排序 (防止同层互盖).
    private const int QueueTop = 5000;          // 渲染队列上限 (游戏照片层靠 queue 叠, 标记永远最后画)
    private const float ZStep = 0.001f;         // 层间 z 步进 (同 queue 内的深度差)
    private const int GreenPrio = 0;            // GC 弹道 (绿十字/瞄准圈/飞时) + 轨迹点/最终线/交汇点
    private const int ImpactPrio = 1;           // 预瞄线 + 落点指示器 (红虚线/红实线/红点/落点圈/计时/弹种)
    private const int RedPrio = 2;              // 队列标记 (红杀伤圈/编号/弹种/菱形/齐射框) + 实体图标
    private const float GreenOffset = ZStep * GreenPrio;   // 0
    private const float ImpactOffset = ZStep * ImpactPrio; // 0.001
    private const float RedOffset = ZStep * RedPrio;       // 0.002
    private const float SurfScale = 0.212f / 0.81f; // 实体单位 → 板面单位 (实体世界缩放 / 板面世界缩放)
    // 速度矢量符尺寸 (板面单位, 用户定稿): 空心圆半径 0.005 = 静止点直径; 点缩至原 1/4
    private const float SpeedR = 0.005f;     // 矢量符圆半径 (线长基准 r: 0.5r/1r/1.5r 对数档)
    private const float SpeedDotR = 0.0025f; // 静止点半径

    public void Start() {
        _disposed = false;
        _loopHandle = MelonCoroutines.Start(Loop());
    }

    public void Stop() {
        _disposed = true;
        if (_loopHandle != null) { try { MelonCoroutines.Stop(_loopHandle); } catch { } }
        _loopHandle = null;
        ClearAll();
    }

    private IEnumerator Loop() {
        while (!_disposed) {
            yield return null; // 每帧
            UpdateImpacts();
            UpdateTracks();   // 目标轨迹线: 开火前跟解算, 开火后沿冻结轨迹缩短
            FollowIcons(); // 实体图标挂板面, 每帧跟随实体世界位置
        }
    }

    /// <summary>实体图标位置跟随 (板面父级, 每帧由实体世界坐标反算; 死亡实体由 DC 差集回调清退) + 速度矢量符活读.</summary>
    private void FollowIcons() {
        if (MapSurfaceRef == null) return;
        foreach (var pair in _icons) {
            if (pair.Key == null || pair.Value == null) continue;
            var b = (Vector2)MapSurfaceRef.InverseTransformPoint(pair.Key.transform.position);
            pair.Value.Root.transform.localPosition = new Vector3(b.x, b.y, RedOffset);
            UpdateSpeedVector(pair.Value);
        }
    }

    /// <summary>速度矢量符 (战雷 TTS 同款): 目标中心往下 0.15 格, 静止 = 点, 运动 = 空心圆 (半径 SpeedR) + 速度方向线 (自圆周伸出).
    /// 线长对数连续: 1-10 m/s → 0.5r, 10-100 → 1r, 100-1000 → 1.5r, 超 1000 封顶 2r (档内 log10 平滑, 无跳变).
    /// 数据源 = DC TWS 滤波速度 (板面局部系 km/s, 方向即板面方向); TWS 关 → 速度 0 → 全显示静止点.</summary>
    private static void UpdateSpeedVector(IconEntry e) {
        if (e.SpeedRoot == null) return;
        Vector2 v = e.Target.Velocity;
        Vector2 dir;
        float vMps = v.magnitude * 1000f; // km/s → m/s
        float len;
        bool moving = vMps >= 1f;
        e.CircleRoot.SetActive(moving);
        e.DotRoot.SetActive(!moving);
        e.SpeedLine.gameObject.SetActive(moving);
        if (!moving) return;
        len = Mathf.Clamp(SpeedR * (0.5f + 0.5f * Mathf.Log10(vMps)), 0.5f * SpeedR, 2f * SpeedR);
        dir = v / v.magnitude;
        e.SpeedLine.Start = new Vector3(dir.x, dir.y, 0f) * SpeedR;         // 线从圆周起 (不是圆心)
        e.SpeedLine.End = new Vector3(dir.x, dir.y, 0f) * (SpeedR + len);
    }

    // ===== DC 方法 (其他模块调用) =====

    /// <summary>清空所有标记 (F9/STOP).</summary>
    public void ClearAll() {
        foreach (var m in _queueMarks.Values) { DestroyRoot(m.Root); DestroyRoot(m.LineRoot); }
        _queueMarks.Clear();
        foreach (var i in _impacts.Values) DestroyRoot(i.Root);
        _impacts.Clear();
        foreach (var t in _tracks.Values) DestroyRoot(t.Root);
        _tracks.Clear();
        foreach (var f in _finals.Values) DestroyRoot(f.Root);
        _finals.Clear();
        foreach (var e in _icons.Values) DestroyRoot(e.Root);
        _icons.Clear();
        foreach (var b in _ballistic.Values) DestroyRoot(b.Root);
        _ballistic.Clear();
    }

    /// <summary>实时弹道指示器 (FC 每帧 push): 开火前的绿色瞄准十字/LR 那套 + 外圈 (当前任务弹种杀伤半径, 穿甲弹带 X).
    /// 弹种 -1 = 未就绪 (CANFIRE 前), 不渲染. AllReady 时十字上下臂端加折角 (边长 = 臂长一半). 板面局部坐标.
    /// 十字下方 = 飞行时间两位数字 (炮给的, 瞄准期实时自解), 与弹种标签同字号.</summary>
    public void PushBallistic(LeftRight side, Vector2 boardPos, float killRadiusKm, int bulletType, bool ready = false, float flyTime = float.NaN) {
        if (MapSurfaceRef == null) return;
        if (bulletType < 0) {
            if (_ballistic.TryGetValue(side, out var oldB) && oldB.Root != null) { DestroyRoot(oldB.Root); _ballistic.Remove(side); }
            return;
        }
        const float gap = 0.02f, armLen = 0.045f;
        const float top = gap + armLen; // 臂端距中心
        if (!_ballistic.TryGetValue(side, out var mark) || mark.Root == null) {
            mark = new BallisticMark { Root = new GameObject(side == LeftRight.Left ? "FCS2_AimLeft" : "FCS2_AimRight") };
            mark.Root.transform.SetParent(MapSurfaceRef, false);
            _ballistic[side] = mark;
        }
        mark.Root.transform.localPosition = new Vector3(boardPos.x, boardPos.y, GreenOffset);
        if (mark.RadiusRoot == null) {
            mark.RadiusRoot = new GameObject("FCS2_AimRadius");
            mark.RadiusRoot.transform.SetParent(mark.Root.transform, false);
            mark.CrossRoot = new GameObject("FCS2_AimCross");
            mark.CrossRoot.transform.SetParent(mark.Root.transform, false);
            mark.CornerRoot = new GameObject("FCS2_AimCorners");
            mark.CornerRoot.transform.SetParent(mark.Root.transform, false);
            mark.BulletRoot = new GameObject("FCS2_AimBullet");
            mark.BulletRoot.transform.SetParent(mark.Root.transform, false);
            mark.FlyRoot = new GameObject("FCS2_AimFlyTime");
            mark.FlyRoot.transform.SetParent(mark.Root.transform, false);
            // CS 准星式四臂 + L/R 字标 (复用旧 BuildAimMark 参数)
            Vector2[] dirs = { new(0f, 1f), new(0f, -1f), new(1f, 0f), new(-1f, 0f) };
            foreach (var d in dirs) {
                Line(mark.CrossRoot.transform, d * gap, d * top, 0.005f, Color.green);
            }
            float sideSign = side == LeftRight.Left ? -1f : 1f;
            const float tagScale = 0.0225f;
            float tagCenter = sideSign * (top + tagScale * 1.5f);
            var tagRoot = new GameObject("FCS2_AimTag");
            tagRoot.transform.SetParent(mark.CrossRoot.transform, false);
            tagRoot.transform.localPosition = new Vector3(tagCenter - tagScale * 0.5f, -0.8f * tagScale, 0f); // 字形中心骑线 (同旧版)
            Glyph16Font.DrawCharSegments(tagRoot.transform, side == LeftRight.Left ? 'L' : 'R', Color.green, 0f, tagScale);
        }
        // AllReady 生气符号 (四象限直角弯, 拐点卡在准星四臂内角 (±gap,±gap), 边长 = 半臂长):
        // 每弯 = 一条边从外侧到拐点 + 一条边从拐点朝外; 未就绪拆掉.
        if (mark.Ready != ready) {
            mark.Ready = ready;
            ClearChildren(mark.CornerRoot.transform);
            if (ready) {
                float h = armLen * 0.5f;
                // 左上: (-gap-h,gap)→(-gap,gap)→(-gap,gap+h)
                Line(mark.CornerRoot.transform, new Vector2(-gap - h, gap), new Vector2(-gap, gap), 0.005f, Color.green);
                Line(mark.CornerRoot.transform, new Vector2(-gap, gap), new Vector2(-gap, gap + h), 0.005f, Color.green);
                // 右上: (gap+h,gap)→(gap,gap)→(gap,gap+h)
                Line(mark.CornerRoot.transform, new Vector2(gap + h, gap), new Vector2(gap, gap), 0.005f, Color.green);
                Line(mark.CornerRoot.transform, new Vector2(gap, gap), new Vector2(gap, gap + h), 0.005f, Color.green);
                // 左下: (-gap-h,-gap)→(-gap,-gap)→(-gap,-gap-h)
                Line(mark.CornerRoot.transform, new Vector2(-gap - h, -gap), new Vector2(-gap, -gap), 0.005f, Color.green);
                Line(mark.CornerRoot.transform, new Vector2(-gap, -gap), new Vector2(-gap, -gap - h), 0.005f, Color.green);
                // 右下: (gap+h,-gap)→(gap,-gap)→(gap,-gap-h)
                Line(mark.CornerRoot.transform, new Vector2(gap + h, -gap), new Vector2(gap, -gap), 0.005f, Color.green);
                Line(mark.CornerRoot.transform, new Vector2(gap, -gap), new Vector2(gap, -gap - h), 0.005f, Color.green);
            }
        }
        // 弹种标签: 十字上端居中 (与 L/R 字标同字号)
        string bt = ((BulletType)bulletType).ToString();
        if (bt != mark.BulletText) {
            mark.BulletText = bt;
            ClearChildren(mark.BulletRoot.transform);
            const float segW = 0.0225f;
            float step = segW * 1.4f;
            mark.BulletRoot.transform.localPosition = new Vector3(-((bt.Length - 1) * step + segW) / 2f, top + segW * 0.8f, 0f);
            for (int i = 0; i < bt.Length; i++) {
                Glyph16Font.DrawCharSegments(mark.BulletRoot.transform, bt[i], Color.green, i * step, segW);
            }
        }
        // 飞行时间 (炮给的, 瞄准期实时自解): 十字下端居中, 两位数字, 与弹种标签同字号; NaN 不显示
        string ft = float.IsNaN(flyTime) ? "" : Mathf.FloorToInt(flyTime).ToString("00"); // 舍小数点 (0.x 算 0, 不四舍五入)
        if (ft != mark.FlyText) {
            mark.FlyText = ft;
            ClearChildren(mark.FlyRoot.transform);
            if (ft.Length > 0) {
                const float segW = 0.0225f;
                float step = segW * 1.4f;
                mark.FlyRoot.transform.localPosition = new Vector3(-((ft.Length - 1) * step + segW) / 2f, -top - segW * 0.8f - segW * 1.6f, 0f);
                for (int i = 0; i < ft.Length; i++) {
                    Glyph16Font.DrawCharSegments(mark.FlyRoot.transform, ft[i], Color.green, i * step, segW);
                }
            }
        }
        // 外圈跟任务弹种: 穿甲弹带 X 指示 (旧版同款)
        RebuildCircle(mark.RadiusRoot.transform, killRadiusKm, GreenOffset, Color.green, solid: false, pierce: IsArmorPierce((BulletType)bulletType),
            ref mark.RadiusKm, ref mark.SegCount, ref mark.Pierce);
    }

    /// <summary>打击队列指示器批量同步 (FC 每帧调用): 队列任务 + 在炮任务 → 逐实体更新指示器.
    /// 炮击指示线只画在炮任务上 (L/R 两条, 旧版同款); 排队任务只有圈+编号.</summary>
    public void UpdateQueueIndicator(FireControl fc) {
        var seen = new HashSet<GameObject>();
        int idx = 1;
        foreach (var t in fc.Queue) {
            if (t.Entity != null) {
                seen.Add(t.Entity);
                UpdateQueueIndicator(t.Entity, Slot.Queue, idx++, t.SalvoPair, t.Shell, t.Mode, Vector2.zero, drawLine: false);
            }
        }
        SyncOne(fc.LeftTask, fc.LeftTask == fc.RightTask && fc.LeftTask != null ? Slot.Salvo : Slot.Left, seen);
        SyncOne(fc.RightTask, fc.RightTask == fc.LeftTask && fc.RightTask != null ? Slot.Salvo : Slot.Right, seen);
        // 不在队列也不在炮上的旧标记: 清退
        var gone = new List<GameObject>();
        foreach (var k in _queueMarks.Keys) if (!seen.Contains(k)) gone.Add(k);
        foreach (var k in gone) {
            DestroyRoot(_queueMarks[k].Root);
            DestroyRoot(_queueMarks[k].LineRoot);
            _queueMarks.Remove(k);
        }
    }

    private void SyncOne(FireTask? task, Slot slot, HashSet<GameObject> seen) {
        if (task?.Entity == null) return;
        seen.Add(task.Entity);
        UpdateQueueIndicator(task.Entity, slot, 0, task.SalvoPair, task.Shell, task.Mode, task.AimBoard, drawLine: true);
    }

    /// <summary>打击队列指示器 (FC 调用): 红色杀伤圈 + 编号米字数码 (00T/01N/02X; 在炮上 L-N/R-X) + 预瞄线 (仅在炮任务).
    /// 全部挂板面 (不挂实体 — 大地图照片堆叠会盖淡实体挂件), 位置每帧由实体世界坐标反算到板面.
    /// queuePos -1 = 移出队列 (清退).</summary>
    public void UpdateQueueIndicator(GameObject entity, Slot slot, int queuePos, bool salvo, BulletType shell, ChargeMode mode, Vector2 aimEnd, bool drawLine = false) {
        if (MapSurfaceRef == null) return;
        if (queuePos < 0) {
            if (_queueMarks.TryGetValue(entity, out var old)) { DestroyRoot(old.Root); DestroyRoot(old.LineRoot); _queueMarks.Remove(entity); }
            return;
        }
        var entityBoard = (Vector2)MapSurfaceRef.InverseTransformPoint(entity.transform.position);
        if (!_queueMarks.TryGetValue(entity, out var mark)) {
            mark = new QueueIndicator {
                Root = new GameObject("FCS2_QueueMark"),
                RadiusRoot = new GameObject("FCS2_QueueRadius"),
                LabelRoot = new GameObject("FCS2_QueueLabel"),
                BulletRoot = new GameObject("FCS2_QueueBullet"),
                LineRoot = new GameObject("FCS2_QueueLine"),
                OuterRoot = new GameObject("FCS2_QueueOuter"),
                SalvoRoot = new GameObject("FCS2_QueueSalvo"),
            };
            mark.Root.transform.SetParent(MapSurfaceRef, false);
            mark.RadiusRoot.transform.SetParent(mark.Root.transform, false);
            mark.LabelRoot.transform.SetParent(mark.Root.transform, false);
            mark.BulletRoot.transform.SetParent(mark.Root.transform, false);
            mark.LineRoot.transform.SetParent(MapSurfaceRef, false); // 预瞄线直接挂板面 (落点层), 不跟队列标记根
            mark.SalvoRoot.transform.SetParent(mark.Root.transform, false);
            mark.OuterRoot.transform.SetParent(mark.Root.transform, false);
            // 第二圈菱形 (旧版 outerSegs 同款: 选中/排队即出, 半径 0.05×√2×1.35, 线宽 0.01, 板面单位 ×SurfScale)
            float r2 = 0.05f * Mathf.Sqrt(2f) * 1.35f * SurfScale;
            var pts2 = new[] {
                new Vector3(0f, r2, 0f), new Vector3(r2, 0f, 0f),
                new Vector3(0f, -r2, 0f), new Vector3(-r2, 0f, 0f),
            };
            for (int s = 0; s < 4; s++) {
                Line(mark.OuterRoot.transform, pts2[s], pts2[(s + 1) % 4], 0.01f * SurfScale, Color.red, RedPrio);
            }
            _queueMarks[entity] = mark;
        }
        // 根在实体 (菱形选中框留目标); 编号/弹种标签/杀伤圈跟预瞄点 (hasAim 时偏移); 无预瞄回实体
        mark.Root.transform.localPosition = new Vector3(entityBoard.x, entityBoard.y, RedOffset);
        mark.LineRoot.transform.localPosition = new Vector3(entityBoard.x, entityBoard.y, GreenOffset); // 预瞄线根在实体 (线终点 = aimEnd); 绿权重 (绿层, 永远浮红线上)
        bool hasAim = !float.IsNaN(aimEnd.x) && !float.IsNaN(aimEnd.y) && aimEnd.sqrMagnitude > 1e-8f;
        Vector2 aimOff = hasAim ? aimEnd - entityBoard : Vector2.zero;
        mark.RadiusRoot.transform.localPosition = new Vector3(aimOff.x, aimOff.y, 0f); // 圈跟预瞄点
        mark.Slot = slot;
        mark.QueuePos = queuePos;
        mark.Shell = shell;
        mark.Mode = mode;
        // 齐射第三圈菱形 (旧版 SetMarkSalvo 同款: 半径 0.05×√2×1.7, 线宽 0.01, 板面单位 ×SurfScale)
        if (mark.Salvo != salvo) {
            mark.Salvo = salvo;
            ClearChildren(mark.SalvoRoot.transform);
            if (salvo) {
                float r3 = 0.05f * Mathf.Sqrt(2f) * 1.7f * SurfScale;
                var pts = new[] {
                    new Vector3(0f, r3, 0f), new Vector3(r3, 0f, 0f),
                    new Vector3(0f, -r3, 0f), new Vector3(-r3, 0f, 0f),
                };
                for (int s = 0; s < 4; s++) {
                    Line(mark.SalvoRoot.transform, pts[s], pts[(s + 1) % 4], 0.01f * SurfScale, Color.red, RedPrio);
                }
            }
        }
        // 红杀伤圈 (板面父级, 板面单位直接画)
        float rKm = ShellData.KillRadiusKm(shell);
        bool pierce = IsArmorPierce(shell);
        RebuildCircle(mark.RadiusRoot.transform, rKm, RedOffset, Color.red, solid: shell == BulletType.DRIL, pierce: pierce,
            ref mark.RadiusKm, ref mark.SegCount, ref mark.Pierce, RedPrio);
        // 编号米字数码 (旧版 SetMarkLabel 逐条复刻, 板面空间: 实体局部字号 × SurfScale): 字号 0.045×5/6, 间距 1.4×, 上方 0.14+dy
        const float segW = 0.045f * 5f / 6f * SurfScale;
        float step = segW * 1.4f;
        float dy = (0.045f / 8f + 0.0222f) * SurfScale;
        string label = BuildQueueLabel(mark);
        if (label != mark.LabelText) {
            mark.LabelText = label;
            ClearChildren(mark.LabelRoot.transform);
            for (int i = 0; i < label.Length; i++) {
                Glyph16Font.DrawCharSegments(mark.LabelRoot.transform, label[i], Color.red, i * step, segW);
            }
        }
        // 编号位置每帧设: 预瞄偏移 + 基准偏移 (标签上方 0.14+dy)
        mark.LabelRoot.transform.localPosition = new Vector3(aimOff.x - ((label.Length - 1) * step + segW) / 2f, aimOff.y + 0.14f * SurfScale + dy, 0f);
        // 弹种标签 (旧版 bulletRoot 同款: 编号上方, 行间距 = 字间距 + 再上抬 1/4 小格避开杀伤圈)
        string bt = shell.ToString();
        if (bt != mark.BulletText) {
            mark.BulletText = bt;
            ClearChildren(mark.BulletRoot.transform);
            for (int i = 0; i < bt.Length; i++) {
                Glyph16Font.DrawCharSegments(mark.BulletRoot.transform, bt[i], Color.red, i * step, segW);
            }
        }
        // 弹种标签位置每帧设 (跟预瞄点)
        mark.BulletRoot.transform.localPosition = new Vector3(aimOff.x - ((bt.Length - 1) * step + segW) / 2f, aimOff.y + 0.14f * SurfScale + dy + segW * 1.6f + (step - segW) + 0.02775f * SurfScale, 0f);
        // 预瞄线 (炮 → 任务点, 厚 0.006 纯绿, 游戏 Dashed 材质): 只要炮上有任务就显示 (与 TWS 无关);
        // 终点 = 交汇点 (有预瞄); 静目标/TWS 关 (aimEnd NaN) → 直瞄线指向目标本身.
        // LineRoot 直接挂板面 (落点层), 线 z=0 即可; 恒定实体: 建一次, 不用就隐藏
        if (drawLine && NestRef != null) {
            if (mark.TargetLine == null) {
                mark.TargetLine = CreateDashedLine(mark.LineRoot.transform, "FCS2_TargetLine", 0.006f, Color.green, GreenPrio); // 绿层: 绿色元素统一绿权重
            }
            var nest = (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position) - entityBoard;
            var endPt = hasAim ? aimEnd - entityBoard : Vector2.zero; // 无预瞄: 直瞄线 (终点 = 目标)
            mark.TargetLine.Start = new Vector3(nest.x, nest.y, 0f);
            mark.TargetLine.End = new Vector3(endPt.x, endPt.y, 0f);
            mark.TargetLine.gameObject.SetActive(true);
        }
        else if (mark.TargetLine != null && mark.TargetLine.gameObject.activeSelf) {
            mark.TargetLine.gameObject.SetActive(false); // 不在炮任务: 隐藏 (恒定实体)
        }
    }

    private static string BuildQueueLabel(QueueIndicator m) {
        char modeLetter = m.Mode switch { ChargeMode.Tight => 'T', ChargeMode.Extra => 'X', _ => 'N' };
        switch (m.Slot) {
            case Slot.Left: return $"L-{modeLetter}";
            case Slot.Right: return $"R-{modeLetter}";
            case Slot.Salvo: return $"S-{modeLetter}";
            default: return $"{m.QueuePos:00}{modeLetter}";
        }
    }

    /// <summary>炮弹落点指示器 (GC 击发确认时调用一次): 红色落点 + 杀伤圈, 下面计时, 弹种在编号位 (队列时 L-X 的位置);
    /// 红线 (全红): 虚线固定 (全长弹道) + 实线逐渐缩短 (未飞段) + 实心红点 (弹头).
    /// 落点 = 开火瞬间的瞄准点缓存 (GC 口径: 落弹点就是 GC 传给 DC 的落弹点, 不另传坐标).
    /// 恒定实体: 每炮一套 (6 个根), 击发时重画激活, 飞行结束隐藏 — 不新建不销毁.
    /// 剩余时间由 GC 持续传导 (游戏倒计时真值, 与游戏指示器同步); DC 侧本地计时兜底.</summary>
    public void ImpactFired(LeftRight side, float aimX, float aimY, BulletType shell, float flightTime, Flight flight) {
        if (MapSurfaceRef == null || NestRef == null) return;
        var board = new Vector2(aimX, aimY); // GC 冻结的开火前最后瞄准点 (开火后游戏把标记拉回铁巢, DC 侧缓存不可靠)
        // 射表偏差探针: GC 标记 = 游戏按炮口 E/A 自解的真实落点 (弹坑所在); FC aim = 射表解算.
        // 两者差 = 射表拟合偏差 (瞄多/瞄少的直接证据). 不看 activeSelf: 击发后 FC push 冻结会把线隐藏, 数据仍在 AimBoard
        if (_tracks.TryGetValue(side, out var trLine) && trLine.Root != null
            && !float.IsNaN(trLine.AimBoard.x) && !float.IsNaN(trLine.AimBoard.y)) {
            MelonLogger.Msg($"[DC] impact src: gc=({board.x:F3},{board.y:F3}) fc=({trLine.AimBoard.x:F3},{trLine.AimBoard.y:F3}) d={Vector2.Distance(board, trLine.AimBoard):F3}板面");
        }
        // 边界检查: 落点出地图 (向外扩一小格) 不显示指示器 — 落点打到板外时红线别飞出火控台 (统一 GeoMap.IsOnBoard)
        if (!GeoMap.IsOnBoard(board, GeoMap.MapCellSize)) return;
        if (!_impacts.TryGetValue(side, out var im) || im.Root == null) {
            im = new ImpactIndicator { Root = new GameObject("FCS2_ImpactIndicator") };
            im.Root.transform.SetParent(MapSurfaceRef, false);
            im.FixedRoot = new GameObject("FCS2_ImpactFixed");
            im.SolidRoot = new GameObject("FCS2_ImpactSolid");
            im.DotRoot = new GameObject("FCS2_ImpactDot");
            im.CircleRoot = new GameObject("FCS2_ImpactCircle");
            im.TimerRoot = new GameObject("FCS2_ImpactTimer");
            im.BulletRoot = new GameObject("FCS2_ImpactBullet");
            foreach (var c in new[] { im.FixedRoot, im.SolidRoot, im.DotRoot, im.CircleRoot, im.TimerRoot, im.BulletRoot })
                c.transform.SetParent(im.Root.transform, false);
            // 红线/红点挂 ImpactOffset 同层 (圈/字在各自动作里单独设 z, 这里只抬线层)
            foreach (var c in new[] { im.FixedRoot, im.SolidRoot, im.DotRoot })
                c.transform.localPosition = new Vector3(0f, 0f, ImpactOffset);
            // 实心红点 (圆画在局部原点, 后续移动父级位置即可, 只建一次)
            FillDot(im.DotRoot.transform, Vector2.zero, 0.012f, Color.red, ImpactPrio);
            _impacts[side] = im;
        }
        // 每发重画/重置 (弹道终点/弹种可能变)
        im.ImpactBoard = board;
        im.NestBoard = (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position);
        im.Shell = shell;
        im.Flight = flight; // 剩余/落地统一口径 (GC 每帧更新 Flight, 这里只存引用直读字段)
        im.LastShownSecond = -1;
        im.Landed = false;
        // 上一发落地后隐藏的飞行件恢复激活 (虚线一直留着, 见 LandImpact)
        im.SolidRoot.SetActive(true);
        im.DotRoot.SetActive(true);
        im.TimerRoot.SetActive(true);
        im.BulletRoot.SetActive(true);
        im.CircleRoot.SetActive(true);
        // 目标轨迹线: 击发 → 火控线参数转移给最终线 (冻结, 倒计时驱动缩短), 火控线立即释放 (下个任务解算接管).
        // 不看 activeSelf: 击发后 FC push 冻结已把线隐藏, 数据仍在.
        // 转移校验: ① LastValid 3s 窗口 — 静态目标无预瞄 (v=0) 不 push, tr 里是上一发 (假目标) 的旧参数, 超窗拒转移
        // (真目标开火不再把假目标轨迹复活成最终线); ② 参数有效性 — 交汇点非 NaN 且飞时为正
        if (_tracks.TryGetValue(side, out var tr) && tr.Root != null && tr.Target != null && MapSurfaceRef != null
            && Time.time - tr.LastValid < 3f
            && !float.IsNaN(tr.AimBoard.x) && tr.T0 > 0f) {
            var fin = EnsureTrack(_finals, side);
            fin.Target = tr.Target;
            fin.AimBoard = tr.AimBoard;
            fin.VBoard = tr.VBoard;
            fin.ABoard = tr.ABoard;
            fin.JBoard = tr.JBoard;
            fin.T0 = tr.T0;
            fin.P0Board = (Vector2)MapSurfaceRef.InverseTransformPoint(tr.Target.position); // 冻结起点 = 击发瞬间目标位置
            fin.Flight = flight; // 最终线缩短驱动 (统一口径)
            fin.Root.SetActive(true);
            tr.Root.SetActive(false); // 火控线释放
        }
        // 红色固定虚线 (全长弹道, 落地保留): 与 A1 同款 — 游戏 Dashed 材质单线, 保持红色 (恒定实体, 建一次)
        if (im.FixedLine == null) {
            im.FixedLine = CreateDashedLine(im.FixedRoot.transform, "FCS2_ImpactFixed", 0.006f, Color.red, ImpactPrio);
        }
        im.FixedLine.Start = new Vector3(im.NestBoard.x, im.NestBoard.y, 0f);
        im.FixedLine.End = new Vector3(im.ImpactBoard.x, im.ImpactBoard.y, 0f);
        im.FixedLine.gameObject.SetActive(true);
        // 实线缓存作废: 下一帧 UpdateImpacts 按新弹道重建 (起点=炮口, 终点=新落点)
        if (im.SolidLine != null) { try { UnityEngine.Object.Destroy(im.SolidLine.gameObject); } catch { } im.SolidLine = null; }
        // 红落点圈 (弹种杀伤半径, 随弹种)
        float rKm = ShellData.KillRadiusKm(im.Shell);
        RebuildCircle(im.CircleRoot.transform, rKm, 0f, Color.red, solid: im.Shell == BulletType.DRIL, pierce: IsArmorPierce(im.Shell),
            ref im.RadiusKm, ref im.SegCount, ref im.Pierce, ImpactPrio);
        im.CircleRoot.transform.localPosition = new Vector3(im.ImpactBoard.x, im.ImpactBoard.y, ImpactOffset);
        // 弹种标签 (飞行时挪到队列编号位: 与队列时 L-X 同 y = 0.14 + dy; 板面空间 = 实体空间常量 × SurfScale)
        float segW = 0.045f * 5f / 6f * SurfScale;
        float step = segW * 1.4f;
        float dy = (0.045f / 8f + 0.0222f) * SurfScale;
        string bt = im.Shell.ToString();
        ClearChildren(im.BulletRoot.transform);
        im.BulletRoot.transform.localPosition = new Vector3(
            im.ImpactBoard.x - ((bt.Length - 1) * step + segW) / 2f,
            im.ImpactBoard.y + (0.14f * SurfScale + dy),
            ImpactOffset);
        for (int i = 0; i < bt.Length; i++) {
            Glyph16Font.DrawCharSegments(im.BulletRoot.transform, bt[i], Color.red, i * step, segW);
        }
        im.SegW = segW;
        im.Step = step;
        im.TimerY = im.ImpactBoard.y + (-0.2f * SurfScale - dy);
        im.Root.SetActive(true);
    }

    /// <summary>落地对账: Flight 判定的落地时刻, 目标彼时实际位置 vs 落点 — 直接差 = 打远/打近 (不依赖铁巢/开火计时).</summary>
    private void LogLanding(LeftRight side, ImpactIndicator im) {
        if (MapSurfaceRef == null || !_finals.TryGetValue(side, out var fin) || fin.Target == null) return;
        var tb = (Vector2)MapSurfaceRef.InverseTransformPoint(fin.Target.position);
        MelonLogger.Msg($"[DC] landing {side}: t={Time.time:F2} impact=({im.ImpactBoard.x:F3},{im.ImpactBoard.y:F3}) target=({tb.x:F3},{tb.y:F3}) d={Vector2.Distance(im.ImpactBoard, tb):F3}板面");
    }

    /// <summary>落地: 飞行件全部隐藏 (实线/红点/计时/弹种标签/杀伤圈) — 只有红色虚线弹道保留到下一次开火
    /// (下一发 ImpactFired 恢复并重画; 恒定实体不销毁).</summary>
    private static void LandImpact(ImpactIndicator im) {
        if (im.Landed) return;
        im.Landed = true;
        im.SolidRoot.SetActive(false);
        im.DotRoot.SetActive(false);
        im.TimerRoot.SetActive(false);
        im.BulletRoot.SetActive(false);
        im.CircleRoot.SetActive(false);
    }

    // ===== 目标轨迹线 + 交汇点 (FC 解算 push) =====

    /// <summary>轨迹线恒定实体 (每炮一条): 火控线 = 点池 32 点 (点直径 0.006, 只动端点); 最终线 = 单条细实线.</summary>
    private TrackIndicator EnsureTrack(Dictionary<LeftRight, TrackIndicator> dict, LeftRight side) {
        if (dict.TryGetValue(side, out var tr) && tr.Root != null) return tr;
        tr = new TrackIndicator { Root = new GameObject(dict == _tracks ? "FCS2_TrackLine" : "FCS2_TrackFinal") };
        tr.Root.transform.SetParent(MapSurfaceRef, false);
        tr.DashRoot = new GameObject("FCS2_TrackDash");
        tr.DashRoot.transform.SetParent(tr.Root.transform, false);
        tr.DashRoot.transform.localPosition = new Vector3(0f, 0f, GreenOffset);
        tr.DotRoot = new GameObject("FCS2_TrackDot");
        tr.DotRoot.transform.SetParent(tr.Root.transform, false);
        tr.DotRoot.transform.localPosition = new Vector3(0f, 0f, GreenOffset);
        if (dict == _tracks) {
            for (int i = 0; i < tr.Dots.Length; i++)
                tr.Dots[i] = Line(tr.DashRoot.transform, Vector2.zero, Vector2.zero, TrackDotDia, Color.green); // 点 = 直径长短线, 沿轨迹切线摆
        }
        else {
            tr.Solid = Line(tr.DashRoot.transform, Vector2.zero, Vector2.zero, 0.004f, Color.green); // 最终线: 细实线 (冻结轨迹缩短)
        }
        FillDot(tr.DotRoot.transform, Vector2.zero, 0.0067f, Color.green); // 交汇点 (圆画在局部原点, 移父级)
        dict[side] = tr;
        return tr;
    }

    /// <summary>FC 解算 push (25fps): 火控目标线 (粗粒度绿虚线) + 交汇点 (绿点).
    /// 曲线 P(τ) = 目标位置 + V·τ + ½A·τ² + ⅙J·τ³ (三次插值弧线), τ∈[0,T], 每帧随解算更新;
    /// 击发时参数转移给最终线 (见 ImpactFired). target null (无任务/DUMP/解算无效) → 隐藏.</summary>
    public void UpdateFireSolution(LeftRight side, Transform? target, Vector2 aimBoard, Vector2 vBoard, Vector2 aBoard, Vector2 jBoard, float T) {
        var tr = EnsureTrack(_tracks, side);
        if (target == null || float.IsNaN(T) || T <= 0f) {
            tr.Root.SetActive(false);
            return; // Target 保留最后有效值: 击发后 GC 确认转移 (ImpactFired) 还需要 (Root 已隐藏, UpdateTracks 不活读, 安全)
        }
        tr.Target = target;
        tr.AimBoard = aimBoard;
        tr.VBoard = vBoard;
        tr.ABoard = aBoard;
        tr.JBoard = jBoard;
        tr.T0 = T;
        tr.LastValid = Time.time; // 最后有效解算时间戳 (ImpactFired 转移校验: 旧任务残留参数不复活最终线)
        tr.Root.SetActive(true);
    }

    /// <summary>每帧: 火控线 (τ∈[0,T0], 起点活读目标, 点式虚线) 与最终线 (τ∈[progress·T0, T0] 沿冻结轨迹缩短的细实线,
    /// 终点恒 = 开火瞬间交汇点, 倒计时归零 = 落地隐藏). 点数按弧长动态铺, 多余隐藏.</summary>
    private void UpdateTracks() {
        foreach (var tr in _tracks.Values) { // 火控线: 开火前解算 (点式)
            if (tr.Root == null || !tr.Root.activeSelf) continue;
            if (tr.Target == null || MapSurfaceRef == null) { tr.Root.SetActive(false); continue; } // 目标阵亡/离图 → 隐藏
            var p0 = (Vector2)MapSurfaceRef.InverseTransformPoint(tr.Target.position); // 起点活读目标
            int n = BuildDots(tr, p0, 0f, tr.T0);
            for (int i = n; i < tr.Dots.Length; i++) tr.Dots[i].gameObject.SetActive(false);
            tr.DotRoot.transform.localPosition = new Vector3(tr.AimBoard.x, tr.AimBoard.y, GreenOffset);
        }
        foreach (var fin in _finals.Values) { // 最终线: 击发冻结缩短 (细实线)
            if (fin.Root == null || !fin.Root.activeSelf) continue;
            if (fin.Flight == null || fin.Flight.Landed) { fin.Root.SetActive(false); continue; } // 落地: 隐藏
            float progress = 1f - fin.Flight.Remain / fin.Flight.FlyTime; // 0=刚出膛 1=落地 (Flight 统一口径)
            var head = TrackCurve(fin.P0Board, fin.VBoard, fin.ABoard, fin.JBoard, fin.T0 * progress);
            fin.Solid.Start = new Vector3(head.x, head.y, 0f);
            fin.Solid.End = new Vector3(fin.AimBoard.x, fin.AimBoard.y, 0f); // 终点 = 开火瞬间交汇点 (不动)
            fin.DotRoot.transform.localPosition = new Vector3(fin.AimBoard.x, fin.AimBoard.y, GreenOffset); // 交汇点不动
        }
    }

    private static Vector2 TrackCurve(Vector2 p0, Vector2 v, Vector2 a, Vector2 j, float t) =>
        p0 + v * t + 0.5f * a * t * t + j * (t * t * t) / 6f;

    /// <summary>沿曲线 τ∈[t0,t1] 铺点 (点式虚线): 64 点采样累计弧长, 点位置弧长插值定位, 点方向 = 轨迹切线.
    /// 点距 = 均分 len/(n-1): 最小 0.008 (实测手感值), 无上限, 最多 32 点 —
    /// 轨迹长 ∝ 目标速度, 点疏密直观反映速度. 返回铺出的点数, 多余由调用方隐藏.</summary>
    private static int BuildDots(TrackIndicator tr, Vector2 p0, float t0, float t1) {
        const int S = 64;
        tr.Pts[0] = TrackCurve(p0, tr.VBoard, tr.ABoard, tr.JBoard, t0);
        tr.Cum[0] = 0f;
        for (int i = 1; i <= S; i++) {
            tr.Pts[i] = TrackCurve(p0, tr.VBoard, tr.ABoard, tr.JBoard, t0 + (t1 - t0) * i / S);
            tr.Cum[i] = tr.Cum[i - 1] + (tr.Pts[i] - tr.Pts[i - 1]).magnitude;
        }
        float len = tr.Cum[S];
        if (len < 0.001f) return 0; // 曲线退化: 不铺
        const float minSpacing = 0.0075f; // 最小点距 (实测手感值)
        int n = Mathf.Min(tr.Dots.Length, (int)(len / minSpacing) + 1);
        const float dotSeg = 0.0015f; // 点线段长 (极小: 胶囊两端圆帽相接 ≈ 正圆; 0 = 起终点重合不渲染)
        if (n <= 1) { // 单点
            var c = SampleAt(tr, 0f);
            tr.Dots[0].Start = new Vector3(c.x, c.y, 0f);
            tr.Dots[0].End = new Vector3(c.x + dotSeg, c.y, 0f);
            tr.Dots[0].gameObject.SetActive(true);
            return 1;
        }
        float step = len / (n - 1);
        for (int i = 0; i < n; i++) {
            float s = i * step;
            Vector2 c = SampleAt(tr, s);
            Vector2 t = (SampleAt(tr, Mathf.Min(s + 0.001f, len)) - SampleAt(tr, Mathf.Max(s - 0.001f, 0f))).normalized;
            if (t.sqrMagnitude < 1e-6f) t = Vector2.right;
            tr.Dots[i].Start = new Vector3(c.x - t.x * dotSeg * 0.5f, c.y - t.y * dotSeg * 0.5f, 0f);
            tr.Dots[i].End = new Vector3(c.x + t.x * dotSeg * 0.5f, c.y + t.y * dotSeg * 0.5f, 0f);
            tr.Dots[i].gameObject.SetActive(true);
        }
        return n;
    }

    /// <summary>累计弧长 → 曲线点 (二分定位段 + 线性插值).</summary>
    private static Vector2 SampleAt(TrackIndicator tr, float s) {
        int lo = 0, hi = 64;
        while (lo + 1 < hi) {
            int mid = (lo + hi) / 2;
            if (tr.Cum[mid] <= s) lo = mid; else hi = mid;
        }
        float seg = tr.Cum[hi] - tr.Cum[lo];
        float f = seg > 1e-6f ? (s - tr.Cum[lo]) / seg : 0f;
        return Vector2.Lerp(tr.Pts[lo], tr.Pts[hi], f);
    }

    /// <summary>每帧: 落点指示器推进 — 实线未飞段渐短 + 实心红点弹头沿线移动 + 计时数字每秒刷新; 落地 → 飞行件隐藏
    /// (红色虚线弹道 + 落点圈保留到下一次开火). 剩余/落地直读 Flight 字段 (唯一口径, GC 已修正炮表冻结/清表).</summary>
    private void UpdateImpacts() {
        foreach (var kv in _impacts) {
            var im = kv.Value;
            if (im.Root == null || !im.Root.activeSelf || im.Landed) continue; // 未激活/已落地 不动
            if (im.Flight == null) continue;
            if (im.Flight.Landed) { LogLanding(kv.Key, im); LandImpact(im); continue; } // 落地: 飞行件隐藏
            float remain = im.Flight.Remain;
            float progress = 1f - remain / im.Flight.FlyTime; // 0=刚出膛 1=落地
            Vector2 shell = Vector2.Lerp(im.NestBoard, im.ImpactBoard, progress);
            // 实线: 弹头 → 落点 (未飞段) — 只动端点不重建
            if (im.SolidLine == null) {
                im.SolidLine = Line(im.SolidRoot.transform, shell, im.ImpactBoard, 0.006f, Color.red, ImpactPrio);
            }
            else {
                im.SolidLine.Start = new Vector3(shell.x, shell.y, 0f);
                im.SolidLine.End = new Vector3(im.ImpactBoard.x, im.ImpactBoard.y, 0f);
            }
            // 实心红点: 圆画在局部原点, 移动父级位置
            im.DotRoot.transform.localPosition = new Vector3(shell.x, shell.y, ImpactOffset);
            // 计时数字 (整秒, 下面; 舍小数点 — 0.x 算 0, 不四舍五入)
            int sec = Mathf.FloorToInt(remain);
            if (sec != im.LastShownSecond) {
                im.LastShownSecond = sec;
                ClearChildren(im.TimerRoot.transform);
                string t = sec.ToString();
                im.TimerRoot.transform.localPosition = new Vector3(
                    im.ImpactBoard.x - ((t.Length - 1) * im.Step + im.SegW) / 2f, im.TimerY, ImpactOffset);
                for (int k = 0; k < t.Length; k++) {
                    Glyph16Font.DrawCharSegments(im.TimerRoot.transform, t[k], Color.red, k * im.Step, im.SegW);
                }
            }
        }
    }

    // ===== 实体图标 (差集回调): 挂板面 (实体挂件会被大地图照片堆叠盖淡), 位置由 Loop 每帧跟随实体 =====
    public void SpawnIcon(DcTarget t) {
        if (t == null || t.Entity == null || _icons.ContainsKey(t.Entity) || MapSurfaceRef == null) return;
        var root = new GameObject("FCS2_EntityIcon");
        root.transform.SetParent(MapSurfaceRef, false);
        var b = (Vector2)MapSurfaceRef.InverseTransformPoint(t.Entity.transform.position);
        root.transform.localPosition = new Vector3(b.x, b.y, RedOffset); // z 越负越浮 (相机在 -z 侧), 与队列标记同层
        Color color = t.Side switch {
            Side3.Friendly => Color.blue,   // 旧版同款: 友军蓝
            Side3.Neutral => Color.green,   // 参考点绿
            _ => Color.red,                 // 敌对红 (与杀伤圈同一红)
        };
        // 尺寸归一: 板面单位 = 实体单位 × SurfScale (实体/令牌视觉同款大小)
        DrawIcon(root.transform, t.Kind, color, SurfScale, t.Armour);
        // 速度矢量符 (战雷 TTS 同款): 目标中心往下 0.15 格 — 空心圆 (半径 SpeedR) + 速度方向线 (对数长度, 自圆周起) / 静止点.
        // 参考点不画 (地图元素非目标, 无速度语义)
        GameObject? speedRoot = null, circleRoot = null, dotRoot = null;
        Il2CppShapes.Line? line = null;
        if (t.Kind != EntityKind.Reference) {
            const float spdW = 0.005f / 3f; // 线宽 (用户定稿: 原 0.005 取 1/3)
            const int spdSegs = 24;
            speedRoot = new GameObject("FCS2_SpeedVec");
            speedRoot.transform.SetParent(root.transform, false);
            speedRoot.transform.localPosition = new Vector3(0f, -0.15f * GeoMap.MapCellSize, 0f);
            circleRoot = new GameObject("FCS2_SpeedCircle");
            circleRoot.transform.SetParent(speedRoot.transform, false);
            for (int i = 0; i < spdSegs; i++) {
                float a0 = i * 2f * Mathf.PI / spdSegs, a1 = (i + 1) * 2f * Mathf.PI / spdSegs;
                Line(circleRoot.transform, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * SpeedR,
                    new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * SpeedR, spdW, color, RedPrio);
            }
            dotRoot = new GameObject("FCS2_SpeedDot");
            dotRoot.transform.SetParent(speedRoot.transform, false);
            FillDot(dotRoot.transform, Vector2.zero, SpeedDotR, color, RedPrio);
            line = Line(speedRoot.transform, Vector2.zero, new Vector2(SpeedR, 0f), spdW, color, RedPrio);
        }
        _icons[t.Entity] = new IconEntry {
            Root = root, Target = t, SpeedRoot = speedRoot!, CircleRoot = circleRoot!, DotRoot = dotRoot!, SpeedLine = line!,
        };
    }

    public void RemoveIcon(GameObject go) {
        if (_icons.TryGetValue(go, out var entry)) { DestroyRoot(entry.Root); _icons.Remove(go); }
    }

    /// <summary>实体图标三遍遍历渲染 (用户定稿):
    /// 第一遍 菱形框 (除参考点外的所有目标) → 第二遍 装甲正方形 (装甲类或有装甲值 — FDC 带装甲也画) → 第三遍 内容符号 (参考点十字/AA/FDC 六芒星/炮兵圆).</summary>
    private static void DrawIcon(Transform parent, EntityKind kind, Color color, float s = 1f, int armour = 0, int prio = RedPrio) {
        float thin = 0.01f * s, thick = 0.02f * s;
        // 第一遍: 菱形框 (参考点不是目标, 不画框)
        if (kind != EntityKind.Reference) {
            float r = 0.05f * Mathf.Sqrt(2f) * s;
            Line(parent, new Vector2(0f, r), new Vector2(r, 0f), thin, color, prio);
            Line(parent, new Vector2(r, 0f), new Vector2(0f, -r), thin, color, prio);
            Line(parent, new Vector2(0f, -r), new Vector2(-r, 0f), thin, color, prio);
            Line(parent, new Vector2(-r, 0f), new Vector2(0f, r), thin, color, prio);
        }
        // 第二遍: 装甲正方形 (边长 0.1, 半边长 0.05) — kind==Armour 或带装甲值 (FDC 有装甲也要指示)
        if (kind == EntityKind.Armour || armour > 0) {
            float sq = 0.05f * s;
            Line(parent, new Vector2(-sq, -sq), new Vector2(sq, -sq), thin, color, prio);
            Line(parent, new Vector2(sq, -sq), new Vector2(sq, sq), thin, color, prio);
            Line(parent, new Vector2(sq, sq), new Vector2(-sq, sq), thin, color, prio);
            Line(parent, new Vector2(-sq, sq), new Vector2(-sq, -sq), thin, color, prio);
        }
        // 第三遍: 内容符号
        switch (kind) {
            case EntityKind.Fdc: { // 中心六芒星 (三条线, 夹角 60°)
                float r = 0.0267f * s;
                for (int i = 0; i < 3; i++) {
                    float a = i * 60f * Mathf.Deg2Rad;
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    Line(parent, -dir * r, dir * r, thin, color, prio);
                }
                break;
            }
            case EntityKind.Artillery: { // 中心圆
                float r = 0.0267f * s;
                const int n = 24;
                for (int i = 0; i < n; i++) {
                    float a0 = i * 2f * Mathf.PI / n, a1 = (i + 1) * 2f * Mathf.PI / n;
                    Line(parent, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, thin, color, prio);
                }
                break;
            }
            case EntityKind.Aa: { // 两短竖线 + 2 倍粗底座
                float half = 0.0267f * s, bx = 0.03f * s, x0 = 0.02f * s;
                Line(parent, new Vector2(-x0, -half), new Vector2(-x0, half), thin, color, prio);
                Line(parent, new Vector2(x0, -half), new Vector2(x0, half), thin, color, prio);
                Line(parent, new Vector2(-bx, -half / 2f), new Vector2(bx, -half / 2f), thick, color, prio);
                break;
            }
            case EntityKind.Reference: { // 绿色十字 (半臂 0.05, 非点击目标)
                float r = 0.05f * s;
                Line(parent, new Vector2(-r, 0f), new Vector2(r, 0f), thin, color, prio);
                Line(parent, new Vector2(0f, -r), new Vector2(0f, r), thin, color, prio);
                break;
            }
        }
    }

    // ===== 图元 =====

    /// <summary>游戏 Dashed 材质单线 (A1 预瞄线 / C1 红固定虚线同款): 恒定实体建一次, 之后只动端点.
    /// 恒定实体不走 Line(), renderQueue 在此单独按层设.</summary>
    private static Il2CppShapes.Line CreateDashedLine(Transform parent, string name, float thickness, Color color, int prio) {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var line = go.AddComponent<Il2CppShapes.Line>();
        line.Thickness = thickness;
        line.Dashed = true;
        line.Color = color;
        line.ColorStart = color;
        line.ColorEnd = color;
        var r = go.GetComponent<Renderer>();
        if (r != null) r.material.renderQueue = QueueTop - prio;
        return line;
    }

    private static Il2CppShapes.Line Line(Transform parent, Vector2 a, Vector2 b, float thickness, Color color, int prio = GreenPrio) {
        var go = new GameObject("FCS2_Seg");
        go.transform.SetParent(parent, false);
        var line = go.AddComponent<Il2CppShapes.Line>();
        line.Thickness = thickness;
        line.Start = new Vector3(a.x, a.y, 0f);
        line.End = new Vector3(b.x, b.y, 0f);
        line.Color = color;
        line.ColorStart = color;
        line.ColorEnd = color;
        // 渲染队列按层 (画画模型): queue 大 = 后渲染 = 盖在上面. prio 0 → 5000 最后一笔, 永远显示;
        // 游戏照片层层堆叠大概也是靠 queue 叠的 — 斜视角深度排序穿插 → 半透明/消失的问题根治
        var r = go.GetComponent<Renderer>();
        if (r != null) r.material.renderQueue = QueueTop - prio;
        return line;
    }

    /// <summary>实心圆点 (单圆环: 环心半径 = 半径/2, 线宽 = 半径, 内缘恰好覆圆心 → 16 段即实心圆;
    /// 旧 8 段扇形辐条呈梅花, 同心圆环段数多, 均弃用).</summary>
    private static void FillDot(Transform parent, Vector2 center, float radius, Color color, int prio = GreenPrio) {
        const int segs = 16;
        float ringR = radius / 2f;
        for (int i = 0; i < segs; i++) {
            float a0 = i * 2f * Mathf.PI / segs, a1 = (i + 1) * 2f * Mathf.PI / segs;
            Line(parent,
                center + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * ringR,
                center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * ringR,
                radius, color, prio);
        }
    }

    /// <summary>杀伤圈 (旧版 BuildKillCircle 逐条复刻): 段数 = 2πr/(2×dashLen) 取偶 (奇数不轴对称),
    /// 各圈自转 90°/n, 实线覆盖角 = dash/r; solid=整圆 24 段; pierce = 从圆周向内 4 条半半径 X 线.
    /// 全部挂板面, 板面单位直接画.</summary>
    private static void RebuildCircle(Transform root, float rKm, float z, Color color, bool solid, bool pierce, ref float lastR, ref int lastN, ref bool lastPierce, int prio = GreenPrio) {
        float rB = rKm * GeoMap.MapCellSize;               // 板面单位 (段数/虚线长按板面算)
        const float dashB = 0.01f;                         // KillDashLen (板面单位)
        int n = solid ? 24 : Mathf.Max(4, (int)Mathf.Round(2f * Mathf.PI * rB / (2f * dashB)));
        if (!solid && (n & 1) != 0) n--; // 虚线段数取偶数: 奇数段图案不轴对称, 看着歪
        if (Mathf.Abs(rKm - lastR) < 0.0001f && n == lastN && pierce == lastPierce && root.childCount > 0) return;
        lastR = rKm;
        lastN = n;
        lastPierce = pierce;
        ClearChildren(root);
        float r = rB;                                       // 板面半径
        float dash = dashB;                                 // 板面虚线弧长
        float t = 0.01f * GeoMap.MapCellSize;               // KillThick (板面单位)
        for (int s = 0; s < n; s++) {
            float a0 = Mathf.PI * 2f * s / n + (solid ? 0f : Mathf.PI / (2f * n)); // 各圈各自转 90°/n
            float a1 = a0 + (solid ? Mathf.PI * 2f / n : dash / r);                // 实线覆盖角
            Line(root, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, t, color, prio);
        }
        if (pierce) { // 穿甲指示: 从圆周向内 4 条半半径长线, X 型 (对角方向)
            Vector2[] xdirs = { new(1f, 1f), new(-1f, 1f), new(1f, -1f), new(-1f, -1f) };
            foreach (var d in xdirs) {
                Vector2 dn = d.normalized;
                Line(root, dn * r, dn * (r * 0.5f), t, color, prio);
            }
        }
    }

    /// <summary>编号米字数码: 逐字符画 (Glyph16Font 复用), 字符间距 = 字宽.</summary>
    private static void DrawGlyphLine(Transform parent, string text, Color color, float scale) {
        for (int i = 0; i < text.Length; i++) {
            Glyph16Font.DrawCharSegments(parent, text[i], color, i * scale, scale);
        }
    }

    private static bool IsArmorPierce(BulletType bt) => bt is BulletType.AP or BulletType.APHE or BulletType.EQKE or BulletType.ATMC;


    /// <summary>清空子物体 (GetChild 类型安全; Destroy 延后生效, 先收集再删).</summary>
    private static void ClearChildren(Transform parent) {
        var kids = new List<Transform>();
        for (int i = 0; i < parent.childCount; i++) kids.Add(parent.GetChild(i));
        foreach (var k in kids) UnityEngine.Object.Destroy(k.gameObject);
    }

    private static void DestroyRoot(GameObject? root) {
        if (root != null) UnityEngine.Object.Destroy(root);
    }

    public enum Slot { Queue, Left, Right, Salvo }

    private class IconEntry {
        public GameObject Root = null!;
        public DcTarget Target = null!; // DC 对象复用引用 (位置/速度每帧被 DC 更新, 活读)
        public GameObject SpeedRoot = null!;  // 矢量符根 (目标中心往下 0.15 格)
        public GameObject CircleRoot = null!; // 矢量符圆 (运动显示)
        public GameObject DotRoot = null!;    // 静止点 (速度 <1 m/s 显示)
        public Il2CppShapes.Line SpeedLine = null!; // 速度方向线 (每帧只动端点)
    }

    private class QueueIndicator {
        public GameObject Root = null!;
        public GameObject RadiusRoot = null!;
        public GameObject LabelRoot = null!;
        public GameObject BulletRoot = null!;
        public GameObject LineRoot = null!;
        public GameObject OuterRoot = null!;
        public GameObject SalvoRoot = null!;
        public Il2CppShapes.Line? TargetLine; // 预瞄线 (恒定实体: 建一次, 不用就隐藏; 游戏 Dashed 材质)
        public Slot Slot;
        public int QueuePos;
        public bool Salvo;
        public BulletType Shell;
        public ChargeMode Mode = ChargeMode.Normal;
        public string LabelText = "";
        public string BulletText = "";
        public float RadiusKm = -1f;
        public int SegCount;
        public bool Pierce;
    }

    private class ImpactIndicator {
        public GameObject Root = null!;
        public GameObject FixedRoot = null!;
        public GameObject SolidRoot = null!;
        public GameObject DotRoot = null!;
        public GameObject CircleRoot = null!;
        public GameObject TimerRoot = null!;
        public GameObject BulletRoot = null!;
        public Vector2 ImpactBoard;
        public Vector2 NestBoard;
        public Il2CppShapes.Line? FixedLine;  // 红色固定虚线 (游戏 Dashed 单线, 恒定实体)
        public BulletType Shell;
        public Flight? Flight;   // 飞行状态引用 (剩余/落地统一口径, GC 每帧更新 — DC 直读)
        public float SegW;      // 计时/弹种标签字号 (板面空间)
        public float Step;      // 字符间距
        public float TimerY;    // 计时标签 y (板面空间)
        public float RadiusKm = -1f;
        public int SegCount;
        public bool Pierce;
        public int LastShownSecond = -1;
        public Il2CppShapes.Line? SolidLine; // 未飞段实线 (缓存, 每帧只动端点)
        public bool Landed; // 已落地: 飞行件 (实线/红点/计时/弹种标签) 隐藏, 虚线弹道+落点圈保留到下一次开火
    }

    /// <summary>目标轨迹预测线 (每炮两条恒定实体, 绿色 = 火控解算内容): 火控线 = 目标 → 交汇点轨迹 (FC 25fps push, 粗粒度虚线);
    /// 最终线 = 击发时参数转移冻结 (终点 = 交汇点), 沿冻结曲线缩短 (倒计时驱动), 缩没 = 落地 (细粒度虚线).
    /// dash 段数按曲线弧长/周期动态铺 (32 短线池每帧只动端点, 多余隐藏).</summary>
    private class TrackIndicator {
        public float LastValid; // 最后有效解算时间戳 (转移校验: 旧任务残留不复活最终线)
        public GameObject Root = null!;
        public GameObject DashRoot = null!;
        public GameObject DotRoot = null!;
        public readonly Il2CppShapes.Line[] Dots = new Il2CppShapes.Line[32]; // 火控线点池 (最终线不用, 用 Solid)
        public Il2CppShapes.Line Solid = null!;              // 最终线细实线 (击发冻结缩短)
        public readonly Vector2[] Pts = new Vector2[65]; // 弧长采样缓存 (避免每帧分配)
        public readonly float[] Cum = new float[65];
        public Transform? Target;          // 目标引用 (火控线起点活读)
        public Vector2 AimBoard;           // 交汇点 (板面)
        public Vector2 VBoard, ABoard, JBoard; // 轨迹参数 (板面/s, /s², /s³) — 曲线 P(τ) = P0 + V·τ + ½A·τ² + ⅙J·τ³
        public Vector2 P0Board;            // 最终线冻结起点 (击发瞬间目标位置)
        public float T0;                   // 解算飞时 (曲线总时长)
        public Flight? Flight;             // 飞行状态引用 (最终线缩短/落地驱动 — 统一口径)
    }

    private class BallisticMark {
        public GameObject? Root;
        public GameObject? RadiusRoot;
        public GameObject? CrossRoot;
        public GameObject CornerRoot = null!;
        public GameObject BulletRoot = null!;
        public GameObject FlyRoot = null!;
        public bool Ready;
        public string BulletText = "";
        public string FlyText = "";
        public float RadiusKm = -1f;
        public int SegCount;
        public bool Pierce;
    }
}
