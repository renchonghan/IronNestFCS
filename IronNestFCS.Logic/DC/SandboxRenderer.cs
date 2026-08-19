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
    private readonly Dictionary<GameObject, GameObject> _icons = new();
    private readonly Dictionary<LeftRight, BallisticMark> _ballistic = new();

    private object? _loopHandle;
    private bool _disposed;

    // 层级偏移量 (相对板面 z=0, 越负越浮): 所有标记一律挂板面 —
    // 游戏大地图在实体层上堆叠照片 (改透明度), 挂实体的东西会被盖淡/消失, 只能挂板面.
    private const float GreenOffset = 0f;      // 绿十字+瞄准圈 (renderQueue 5000 压阵后深度排序不依赖 z, 回 20cb07b 低高度版)
    private const float ImpactOffset = 0.001f;  // 落点指示器 (红线/红点/圈/字)
    private const float RedOffset = 0.002f;     // 红杀伤圈+编号+弹种标签+预瞄线+实体图标
    private const float SurfScale = 0.212f / 0.81f; // 实体单位 → 板面单位 (实体世界缩放 / 板面世界缩放)

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
            FollowIcons(); // 实体图标挂板面, 每帧跟随实体世界位置
        }
    }

    /// <summary>实体图标位置跟随 (板面父级, 每帧由实体世界坐标反算; 死亡实体由 DC 差集回调清退).</summary>
    private void FollowIcons() {
        if (MapSurfaceRef == null) return;
        foreach (var pair in _icons) {
            if (pair.Key == null || pair.Value == null) continue;
            var b = (Vector2)MapSurfaceRef.InverseTransformPoint(pair.Key.transform.position);
            pair.Value.transform.localPosition = new Vector3(b.x, b.y, RedOffset);
        }
    }

    // ===== DC 方法 (其他模块调用) =====

    /// <summary>清空所有标记 (F9/STOP).</summary>
    public void ClearAll() {
        foreach (var m in _queueMarks.Values) { DestroyRoot(m.Root); DestroyRoot(m.LineRoot); }
        _queueMarks.Clear();
        foreach (var i in _impacts.Values) DestroyRoot(i.Root);
        _impacts.Clear();
        foreach (var go in _icons.Values) DestroyRoot(go);
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
                UpdateQueueIndicator(t.Entity, Slot.Queue, idx++, t.SalvoPair, t.Shell, t.Mode, 0f, drawLine: false);
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
        UpdateQueueIndicator(task.Entity, slot, 0, task.SalvoPair, task.Shell, task.Mode, 0f, drawLine: true);
    }

    /// <summary>打击队列指示器 (FC 调用): 红色杀伤圈 + 编号米字数码 (00T/01N/02X; 在炮上 L-N/R-X) + 预瞄线 (仅在炮任务).
    /// 全部挂板面 (不挂实体 — 大地图照片堆叠会盖淡实体挂件), 位置每帧由实体世界坐标反算到板面.
    /// queuePos -1 = 移出队列 (清退).</summary>
    public void UpdateQueueIndicator(GameObject entity, Slot slot, int queuePos, bool salvo, BulletType shell, ChargeMode mode, float leadOffset, bool drawLine = false) {
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
                Line(mark.OuterRoot.transform, pts2[s], pts2[(s + 1) % 4], 0.01f * SurfScale, Color.red);
            }
            _queueMarks[entity] = mark;
        }
        mark.Root.transform.localPosition = new Vector3(entityBoard.x, entityBoard.y, RedOffset);
        mark.LineRoot.transform.localPosition = new Vector3(entityBoard.x, entityBoard.y, ImpactOffset);
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
                    Line(mark.SalvoRoot.transform, pts[s], pts[(s + 1) % 4], 0.01f * SurfScale, Color.red);
                }
            }
        }
        // 红杀伤圈 (板面父级, 板面单位直接画)
        float rKm = ShellData.KillRadiusKm(shell);
        bool pierce = IsArmorPierce(shell);
        RebuildCircle(mark.RadiusRoot.transform, rKm, RedOffset, Color.red, solid: shell == BulletType.DRIL, pierce: pierce,
            ref mark.RadiusKm, ref mark.SegCount, ref mark.Pierce);
        // 编号米字数码 (旧版 SetMarkLabel 逐条复刻, 板面空间: 实体局部字号 × SurfScale): 字号 0.045×5/6, 间距 1.4×, 上方 0.14+dy
        const float segW = 0.045f * 5f / 6f * SurfScale;
        float step = segW * 1.4f;
        float dy = (0.045f / 8f + 0.0222f) * SurfScale;
        string label = BuildQueueLabel(mark);
        if (label != mark.LabelText) {
            mark.LabelText = label;
            ClearChildren(mark.LabelRoot.transform);
            mark.LabelRoot.transform.localPosition = new Vector3(-((label.Length - 1) * step + segW) / 2f, 0.14f * SurfScale + dy, 0f);
            for (int i = 0; i < label.Length; i++) {
                Glyph16Font.DrawCharSegments(mark.LabelRoot.transform, label[i], Color.red, i * step, segW);
            }
        }
        // 弹种标签 (旧版 bulletRoot 同款: 编号上方, 行间距 = 字间距 + 再上抬 1/4 小格避开杀伤圈)
        string bt = shell.ToString();
        if (bt != mark.BulletText) {
            mark.BulletText = bt;
            ClearChildren(mark.BulletRoot.transform);
            mark.BulletRoot.transform.localPosition = new Vector3(-((bt.Length - 1) * step + segW) / 2f, 0.14f * SurfScale + dy + segW * 1.6f + (step - segW) + 0.02775f * SurfScale, 0f);
            for (int i = 0; i < bt.Length; i++) {
                Glyph16Font.DrawCharSegments(mark.BulletRoot.transform, bt[i], Color.red, i * step, segW);
            }
        }
        // 预瞄线 (旧版 BuildTargetLine 同款: 游戏 Dashed=true, 厚 0.006, 纯绿; 只画在炮任务上)
        // LineRoot 直接挂板面 (落点层), 线 z=0 即可; 恒定实体: 建一次, 不用就隐藏 —
        // LineRoot 每帧跟实体走, 线局部端点随 LineRoot 平移不变, 每次重建是浪费 (Destroy 延迟还瞬态堆积)
        if (drawLine && NestRef != null) {
            if (mark.TargetLine == null) {
                var go = new GameObject("FCS2_TargetLine");
                go.transform.SetParent(mark.LineRoot.transform, false);
                mark.TargetLine = go.AddComponent<Il2CppShapes.Line>();
                mark.TargetLine.Thickness = 0.006f;
                mark.TargetLine.Dashed = true;
                mark.TargetLine.Color = Color.green;
                mark.TargetLine.ColorStart = Color.green;
                mark.TargetLine.ColorEnd = Color.green;
                var r = go.GetComponent<Renderer>(); // 渲染队列到顶 (恒定实体不走 Line(), 这里单独设)
                if (r != null) r.material.renderQueue = 5000;
            }
            var nest = (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position) - entityBoard;
            mark.TargetLine.Start = new Vector3(nest.x, nest.y, 0f);
            mark.TargetLine.End = Vector3.zero;
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
    public void ImpactFired(LeftRight side, float aimX, float aimY, BulletType shell, float flightTime) {
        if (MapSurfaceRef == null || NestRef == null) return;
        var board = new Vector2(aimX, aimY); // 落点 = GC 冻结的开火前最后瞄准点 (开火后游戏把标记拉回铁巢, DC 侧缓存不可靠)
        // 边界检查: 落点出地图 (向外扩一小格) 不显示指示器 — 落点打到板外时红线别飞出火控台
        float x0 = GeoMap.MapBottomLeft.x - GeoMap.MapCellSize;
        float x1 = GeoMap.MapBottomLeft.x + 20f * GeoMap.MapCellSize + GeoMap.MapCellSize;
        float y0 = GeoMap.MapBottomLeft.y - GeoMap.MapCellSize;
        float y1 = GeoMap.MapBottomLeft.y + 10f * GeoMap.MapCellSize + GeoMap.MapCellSize;
        if (board.x < x0 || board.x > x1 || board.y < y0 || board.y > y1) return;
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
            FillDot(im.DotRoot.transform, Vector2.zero, 0.012f, Color.red);
            _impacts[side] = im;
        }
        // 每发重画/重置 (弹道终点/弹种可能变)
        im.ImpactBoard = board;
        im.NestBoard = (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position);
        im.Shell = shell;
        im.FlightTime = Mathf.Max(flightTime, 0.01f);
        im.CreatedAt = Time.time;
        im.FlightRemain = float.NaN; // 等 GC 传导倒计时真值
        im.LastShownSecond = -1;
        ClearChildren(im.FixedRoot.transform);
        DashedLine(im.FixedRoot.transform, im.NestBoard, im.ImpactBoard, 0.006f, Color.red, 0.05f, 0.03f);
        // 实线缓存作废: 下一帧 UpdateImpacts 按新弹道重建 (起点=炮口, 终点=新落点)
        if (im.SolidLine != null) { try { UnityEngine.Object.Destroy(im.SolidLine.gameObject); } catch { } im.SolidLine = null; }
        // 红落点圈 (弹种杀伤半径, 随弹种)
        float rKm = ShellData.KillRadiusKm(im.Shell);
        RebuildCircle(im.CircleRoot.transform, rKm, 0f, Color.red, solid: im.Shell == BulletType.DRIL, pierce: IsArmorPierce(im.Shell),
            ref im.RadiusKm, ref im.SegCount, ref im.Pierce);
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

    /// <summary>GC 飞行期持续传导: 游戏倒计时剩余 (与游戏指示器逐帧同步); 0 = 落地隐藏.</summary>
    public void PushImpactRemain(LeftRight side, float remain) {
        if (!_impacts.TryGetValue(side, out var im) || im.Root == null) return;
        im.FlightRemain = remain;
        if (remain <= 0f && im.Root.activeSelf) im.Root.SetActive(false); // 落地: 隐藏 (恒定实体, 不销毁)
    }

    /// <summary>每帧: 落点指示器推进 — 实线未飞段渐短 + 实心红点弹头沿线移动 + 计时数字每秒刷新; 结束隐藏.</summary>
    private void UpdateImpacts() {
        foreach (var im in _impacts.Values) {
            if (im.Root == null || !im.Root.activeSelf) continue; // 未激活 (没在飞) 不动
            // 剩余时间优先吃 GC 传导的游戏倒计时 (与游戏指示器同步); NaN (GC 静默/表没绑) 退回本地计时
            float remain = im.FlightRemain;
            if (float.IsNaN(remain)) remain = im.FlightTime - (Time.time - im.CreatedAt);
            if (remain <= 0f) {
                im.Root.SetActive(false); // 落地: 隐藏 (恒定实体, 不销毁)
                continue;
            }
            float progress = 1f - remain / im.FlightTime; // 0=刚出膛 1=落地
            Vector2 shell = Vector2.Lerp(im.NestBoard, im.ImpactBoard, progress);
            // 实线: 弹头 → 落点 (未飞段) — 只动端点不重建
            if (im.SolidLine == null) {
                im.SolidLine = Line(im.SolidRoot.transform, shell, im.ImpactBoard, 0.006f, Color.red);
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
        DrawIcon(root.transform, t.Kind, color, SurfScale);
        _icons[t.Entity] = root;
    }

    public void RemoveIcon(GameObject go) {
        if (_icons.TryGetValue(go, out var root)) { DestroyRoot(root); _icons.Remove(go); }
    }

    /// <summary>实体图标三遍遍历渲染 (用户定稿):
    /// 第一遍 菱形框 (除参考点外的所有目标) → 第二遍 装甲正方形 → 第三遍 内容符号 (参考点十字/AA/FDC 六芒星/炮兵圆).</summary>
    private static void DrawIcon(Transform parent, EntityKind kind, Color color, float s = 1f) {
        float thin = 0.01f * s, thick = 0.02f * s;
        // 第一遍: 菱形框 (参考点不是目标, 不画框)
        if (kind != EntityKind.Reference) {
            float r = 0.05f * Mathf.Sqrt(2f) * s;
            Line(parent, new Vector2(0f, r), new Vector2(r, 0f), thin, color);
            Line(parent, new Vector2(r, 0f), new Vector2(0f, -r), thin, color);
            Line(parent, new Vector2(0f, -r), new Vector2(-r, 0f), thin, color);
            Line(parent, new Vector2(-r, 0f), new Vector2(0f, r), thin, color);
        }
        // 第二遍: 装甲正方形 (边长 0.1, 半边长 0.05)
        if (kind == EntityKind.Armour) {
            float sq = 0.05f * s;
            Line(parent, new Vector2(-sq, -sq), new Vector2(sq, -sq), thin, color);
            Line(parent, new Vector2(sq, -sq), new Vector2(sq, sq), thin, color);
            Line(parent, new Vector2(sq, sq), new Vector2(-sq, sq), thin, color);
            Line(parent, new Vector2(-sq, sq), new Vector2(-sq, -sq), thin, color);
        }
        // 第三遍: 内容符号
        switch (kind) {
            case EntityKind.Fdc: { // 中心六芒星 (三条线, 夹角 60°)
                float r = 0.0267f * s;
                for (int i = 0; i < 3; i++) {
                    float a = i * 60f * Mathf.Deg2Rad;
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    Line(parent, -dir * r, dir * r, thin, color);
                }
                break;
            }
            case EntityKind.Artillery: { // 中心圆
                float r = 0.0267f * s;
                const int n = 24;
                for (int i = 0; i < n; i++) {
                    float a0 = i * 2f * Mathf.PI / n, a1 = (i + 1) * 2f * Mathf.PI / n;
                    Line(parent, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, thin, color);
                }
                break;
            }
            case EntityKind.Aa: { // 两短竖线 + 2 倍粗底座
                float half = 0.0267f * s, bx = 0.03f * s, x0 = 0.02f * s;
                Line(parent, new Vector2(-x0, -half), new Vector2(-x0, half), thin, color);
                Line(parent, new Vector2(x0, -half), new Vector2(x0, half), thin, color);
                Line(parent, new Vector2(-bx, -half / 2f), new Vector2(bx, -half / 2f), thick, color);
                break;
            }
            case EntityKind.Reference: { // 绿色十字 (半臂 0.05, 非点击目标)
                float r = 0.05f * s;
                Line(parent, new Vector2(-r, 0f), new Vector2(r, 0f), thin, color);
                Line(parent, new Vector2(0f, -r), new Vector2(0f, r), thin, color);
                break;
            }
        }
    }

    // ===== 图元 =====

    private static Il2CppShapes.Line Line(Transform parent, Vector2 a, Vector2 b, float thickness, Color color) {
        var go = new GameObject("FCS2_Seg");
        go.transform.SetParent(parent, false);
        var line = go.AddComponent<Il2CppShapes.Line>();
        line.Thickness = thickness;
        line.Start = new Vector3(a.x, a.y, 0f);
        line.End = new Vector3(b.x, b.y, 0f);
        line.Color = color;
        line.ColorStart = color;
        line.ColorEnd = color;
        // 强制渲染队列拉到顶 (5000 = 上限): 游戏照片层层堆叠大概也是靠 queue 叠的 — 标记永远最后画,
        // 斜视角深度排序穿插 → 半透明/消失的问题根治
        var r = go.GetComponent<Renderer>();
        if (r != null) r.material.renderQueue = 5000;
        return line;
    }

    /// <summary>虚线 (两段循环): dashLen 实线 / gapLen 空.</summary>
    private static void DashedLine(Transform parent, Vector2 a, Vector2 b, float thickness, Color color, float dashLen, float gapLen) {
        float len = (b - a).magnitude;
        var dir = (b - a) / len;
        for (float d = 0f; d < len; d += dashLen + gapLen) {
            float e = Mathf.Min(d + dashLen, len);
            Line(parent, a + dir * d, a + dir * e, thickness, color);
        }
    }

    /// <summary>实心圆点 (8 段扇形填色近似).</summary>
    private static void FillDot(Transform parent, Vector2 center, float radius, Color color) {
        const int n = 8;
        for (int i = 0; i < n; i++) {
            float a0 = i * 2f * Mathf.PI / n, a1 = (i + 1) * 2f * Mathf.PI / n;
            Line(parent, center, center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * radius, radius * 1.6f, color);
        }
    }

    /// <summary>杀伤圈 (旧版 BuildKillCircle 逐条复刻): 段数 = 2πr/(2×dashLen) 取偶 (奇数不轴对称),
    /// 各圈自转 90°/n, 实线覆盖角 = dash/r; solid=整圆 24 段; pierce = 从圆周向内 4 条半半径 X 线.
    /// 全部挂板面, 板面单位直接画.</summary>
    private static void RebuildCircle(Transform root, float rKm, float z, Color color, bool solid, bool pierce, ref float lastR, ref int lastN, ref bool lastPierce) {
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
            Line(root, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, t, color);
        }
        if (pierce) { // 穿甲指示: 从圆周向内 4 条半半径长线, X 型 (对角方向)
            Vector2[] xdirs = { new(1f, 1f), new(-1f, 1f), new(1f, -1f), new(-1f, -1f) };
            foreach (var d in xdirs) {
                Vector2 dn = d.normalized;
                Line(root, dn * r, dn * (r * 0.5f), t, color);
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

    private class QueueIndicator {
        public GameObject Root = null!;
        public GameObject RadiusRoot = null!;
        public GameObject LabelRoot = null!;
        public GameObject BulletRoot = null!;
        public GameObject LineRoot = null!;
        public GameObject OuterRoot = null!;
        public GameObject SalvoRoot = null!;
        public Il2CppShapes.Line? TargetLine; // 预瞄线 (恒定实体: 建一次, 不用就隐藏)
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
        public BulletType Shell;
        public float FlightTime;
        public float CreatedAt;
        public float FlightRemain = float.NaN; // GC 传导的游戏倒计时剩余 (NaN = 未收到, 本地计时兜底)
        public float SegW;      // 计时/弹种标签字号 (板面空间)
        public float Step;      // 字符间距
        public float TimerY;    // 计时标签 y (板面空间)
        public float RadiusKm = -1f;
        public int SegCount;
        public bool Pierce;
        public int LastShownSecond = -1;
        public Il2CppShapes.Line? SolidLine; // 未飞段实线 (缓存, 每帧只动端点)
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
