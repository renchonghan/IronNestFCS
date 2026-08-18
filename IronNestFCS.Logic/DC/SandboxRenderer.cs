using System.Collections;
using System.Collections.Generic;
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
    public Transform? NestRef;       // 铁巢/炮塔 (预瞄线/落点线起点)

    // ===== 3D 指示器状态 =====
    private readonly Dictionary<GameObject, QueueIndicator> _queueMarks = new();
    private readonly List<ImpactIndicator> _impacts = new();
    private readonly Dictionary<GameObject, GameObject> _icons = new();
    private readonly Dictionary<LeftRight, BallisticMark> _ballistic = new();

    private object? _loopHandle;
    private bool _disposed;

    private const float ZGreen = 0f;       // 绿十字+瞄准圈 (最上)
    private const float ZImpact = -0.02f;  // 落点指示器 (浮一层)
    private const float ZRed = -0.025f;    // 红杀伤圈+编号 (贴板; 旧版 -0.03 偶尔被拍回来的侦察照片盖住, 上提一点点)

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
        }
    }

    // ===== DC 方法 (其他模块调用) =====

    /// <summary>清空所有标记 (F9/STOP).</summary>
    public void ClearAll() {
        foreach (var m in _queueMarks.Values) DestroyRoot(m.Root);
        _queueMarks.Clear();
        foreach (var i in _impacts) DestroyRoot(i.Root);
        _impacts.Clear();
        foreach (var go in _icons.Values) DestroyRoot(go);
        _icons.Clear();
        foreach (var b in _ballistic.Values) DestroyRoot(b.Root);
        _ballistic.Clear();
    }

    /// <summary>实时弹道指示器 (GC 每帧 push): 开火前的绿色瞄准十字/LR 那套 + 外圈 (当前任务弹种杀伤半径).
    /// 弹种 -1 = 未选/未就绪, 不渲染. 板面局部坐标, z=0 最上层.</summary>
    public void PushBallistic(LeftRight side, Vector2 boardPos, float killRadiusKm, int bulletType) {
        if (MapSurfaceRef == null) return;
        if (bulletType < 0) {
            if (_ballistic.TryGetValue(side, out var oldB) && oldB.Root != null) { DestroyRoot(oldB.Root); _ballistic.Remove(side); }
            return;
        }
        if (!_ballistic.TryGetValue(side, out var mark) || mark.Root == null) {
            mark = new BallisticMark { Root = new GameObject(side == LeftRight.Left ? "FCS2_AimLeft" : "FCS2_AimRight") };
            mark.Root.transform.SetParent(MapSurfaceRef, false);
            _ballistic[side] = mark;
        }
        mark.Root.transform.localPosition = new Vector3(boardPos.x, boardPos.y, ZGreen);
        if (mark.RadiusRoot == null) {
            mark.RadiusRoot = new GameObject("FCS2_AimRadius");
            mark.RadiusRoot.transform.SetParent(mark.Root.transform, false);
            mark.CrossRoot = new GameObject("FCS2_AimCross");
            mark.CrossRoot.transform.SetParent(mark.Root.transform, false);
            // CS 准星式四臂 + L/R 字标 (复用旧 BuildAimMark 参数)
            const float gap = 0.02f, armLen = 0.045f;
            Vector2[] dirs = { new(0f, 1f), new(0f, -1f), new(1f, 0f), new(-1f, 0f) };
            foreach (var d in dirs) {
                Line(mark.CrossRoot.transform, d * gap, d * (gap + armLen), 0.005f, Color.green);
            }
            float sideSign = side == LeftRight.Left ? -1f : 1f;
            const float tagScale = 0.0225f;
            float tagCenter = sideSign * ((gap + armLen) + tagScale * 1.5f);
            var tagRoot = new GameObject("FCS2_AimTag");
            tagRoot.transform.SetParent(mark.CrossRoot.transform, false);
            tagRoot.transform.localPosition = new Vector3(tagCenter - tagScale * 0.5f, -0.8f * tagScale, 0f); // 字形中心骑线 (同旧版)
            Glyph16Font.DrawCharSegments(tagRoot.transform, side == LeftRight.Left ? 'L' : 'R', Color.green, 0f, tagScale);
        }
        RebuildCircle(mark.RadiusRoot.transform, killRadiusKm, ZGreen, Color.green, solid: false, pierce: false, ref mark.RadiusKm, ref mark.SegCount, ref mark.Pierce);
    }

    /// <summary>打击队列指示器批量同步 (FC 每帧调用): 队列任务 + 在炮任务 → 逐实体更新指示器 (槽位/队列位/弹种/预瞄).</summary>
    public void UpdateQueueIndicator(FireControl fc) {
        var seen = new HashSet<GameObject>();
        int idx = 1;
        foreach (var t in fc.Queue) {
            if (t.Entity != null) {
                seen.Add(t.Entity);
                UpdateQueueIndicator(t.Entity, Slot.Queue, idx++, false, t.Shell, 0f);
            }
        }
        SyncOne(fc.LeftTask, Slot.Left, seen);
        SyncOne(fc.RightTask, Slot.Right, seen);
        // 不在队列也不在炮上的旧标记: 清退
        var gone = new List<GameObject>();
        foreach (var k in _queueMarks.Keys) if (!seen.Contains(k)) gone.Add(k);
        foreach (var k in gone) {
            DestroyRoot(_queueMarks[k].Root);
            _queueMarks.Remove(k);
        }
    }

    private void SyncOne(FireTask? task, Slot slot, HashSet<GameObject> seen) {
        if (task?.Entity == null) return;
        seen.Add(task.Entity);
        UpdateQueueIndicator(task.Entity, slot, 0, false, task.Shell, 0f);
    }

    /// <summary>打击队列指示器 (FC 调用): 红色杀伤圈 + 编号米字数码 (00T/01N/02X; 在炮上 L-N/R-X) + 预瞄线, z 贴板.
    /// queuePos -1 = 移出队列 (清退).</summary>
    public void UpdateQueueIndicator(GameObject entity, Slot slot, int queuePos, bool salvo, BulletType shell, float leadOffset) {
        if (MapSurfaceRef == null) return;
        if (queuePos < 0) {
            if (_queueMarks.TryGetValue(entity, out var old)) { DestroyRoot(old.Root); _queueMarks.Remove(entity); }
            return;
        }
        if (!_queueMarks.TryGetValue(entity, out var mark)) {
            mark = new QueueIndicator {
                Root = new GameObject("FCS2_QueueMark"),
                RadiusRoot = new GameObject("FCS2_QueueRadius"),
                LabelRoot = new GameObject("FCS2_QueueLabel"),
                LineRoot = new GameObject("FCS2_QueueLine"),
            };
            mark.Root.transform.SetParent(MapSurfaceRef, false);
            mark.RadiusRoot.transform.SetParent(mark.Root.transform, false);
            mark.LabelRoot.transform.SetParent(mark.Root.transform, false);
            mark.LineRoot.transform.SetParent(mark.Root.transform, false);
            _queueMarks[entity] = mark;
        }
        var lp = (Vector2)MapSurfaceRef.InverseTransformPoint(entity.transform.position);
        mark.Root.transform.localPosition = new Vector3(lp.x, lp.y, ZRed);
        mark.Slot = slot;
        mark.QueuePos = queuePos;
        mark.Salvo = salvo;
        mark.Shell = shell;
        // 红杀伤圈
        float rKm = ShellData.KillRadiusKm(shell);
        bool pierce = IsArmorPierce(shell);
        RebuildCircle(mark.RadiusRoot.transform, rKm, ZRed, Color.red, solid: shell == BulletType.DRIL, pierce: pierce, ref mark.RadiusKm, ref mark.SegCount, ref mark.Pierce);
        // 编号米字数码: 队列 = 两位顺位 + 装药模式字母; 在炮上 = L/R + '-' + 模式字母
        string label = BuildQueueLabel(mark);
        if (label != mark.LabelText) {
            mark.LabelText = label;
            foreach (Transform c in mark.LabelRoot.transform) DestroyRoot(c.gameObject);
            DrawGlyphLine(mark.LabelRoot.transform, label, Color.red, 0.0225f);
        }
        // 预瞄线: 铁巢 → 目标 (绿虚线, 连到槽位目标)
        if (NestRef != null && mark.NestPos != (Vector2?)null) {
            var nest = (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position);
            foreach (Transform c in mark.LineRoot.transform) DestroyRoot(c.gameObject);
            DashedLine(mark.LineRoot.transform, nest, lp, 0.008f, new Color(0.2f, 1f, 0.3f), 0.05f, 0.03f);
            mark.NestPos = nest;
        }
        else mark.NestPos = null;
    }

    private static string BuildQueueLabel(QueueIndicator m) {
        string modeLetter = "N";
        switch (m.Slot) {
            case Slot.Left: return $"L-{modeLetter}";
            case Slot.Right: return $"R-{modeLetter}";
            case Slot.Salvo: return $"S-{modeLetter}";
            default: return $"{m.QueuePos:00}{modeLetter}";
        }
    }

    /// <summary>炮弹落点指示器 (FC 击发时调用一次): 红色落点 + 杀伤圈, 下面计时 上面弹种;
    /// 红线 (全红): 虚线固定 (全长弹道) + 实线逐渐缩短 (未飞段) + 实心红点 (弹头). z=-0.02 浮一层.
    /// 飞行时长结束后自动销毁. 齐射两发 = 各建一个 (落点相同也分开建).</summary>
    public void CreateImpact(Vector3 impactWorld, BulletType shell, float flightTime) {
        if (MapSurfaceRef == null || NestRef == null) return;
        var root = new GameObject("FCS2_ImpactIndicator");
        root.transform.SetParent(MapSurfaceRef, false);
        var im = new ImpactIndicator {
            Root = root,
            ImpactBoard = (Vector2)MapSurfaceRef.InverseTransformPoint(impactWorld),
            NestBoard = (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position),
            Shell = shell,
            FlightTime = Mathf.Max(flightTime, 0.01f),
            CreatedAt = Time.time,
        };
        im.FixedRoot = new GameObject("FCS2_ImpactFixed");
        im.SolidRoot = new GameObject("FCS2_ImpactSolid");
        im.DotRoot = new GameObject("FCS2_ImpactDot");
        im.CircleRoot = new GameObject("FCS2_ImpactCircle");
        im.TimerRoot = new GameObject("FCS2_ImpactTimer");
        foreach (var c in new[] { im.FixedRoot, im.SolidRoot, im.DotRoot, im.CircleRoot, im.TimerRoot })
            c.transform.SetParent(root.transform, false);
        // 虚线固定 (全长弹道)
        DashedLine(im.FixedRoot.transform, im.NestBoard, im.ImpactBoard, 0.008f, Color.red, 0.05f, 0.03f);
        // 红落点圈 (弹种杀伤半径)
        float rKm = ShellData.KillRadiusKm(shell);
        RebuildCircle(im.CircleRoot.transform, rKm, 0f, Color.red, solid: shell == BulletType.DRIL, pierce: IsArmorPierce(shell),
            ref im.RadiusKm, ref im.SegCount, ref im.Pierce);
        im.CircleRoot.transform.localPosition = new Vector3(im.ImpactBoard.x, im.ImpactBoard.y, ZImpact);
        _impacts.Add(im);
    }

    /// <summary>每帧: 落点指示器推进 — 实线未飞段渐短 + 实心红点弹头沿线移动 + 计时数字每秒刷新; 结束自毁.</summary>
    private void UpdateImpacts() {
        for (int i = _impacts.Count - 1; i >= 0; i--) {
            var im = _impacts[i];
            float remain = im.FlightTime - (Time.time - im.CreatedAt);
            if (remain <= 0f) {
                DestroyRoot(im.Root);
                _impacts.RemoveAt(i);
                continue;
            }
            float progress = 1f - remain / im.FlightTime; // 0=刚出膛 1=落地
            Vector2 shell = Vector2.Lerp(im.NestBoard, im.ImpactBoard, progress);
            // 实线: 弹头 → 落点 (未飞段)
            foreach (Transform c in im.SolidRoot.transform) DestroyRoot(c.gameObject);
            Line(im.SolidRoot.transform, shell, im.ImpactBoard, 0.008f, Color.red);
            // 实心红点 (小实心圆)
            foreach (Transform c in im.DotRoot.transform) DestroyRoot(c.gameObject);
            FillDot(im.DotRoot.transform, shell, 0.012f, Color.red);
            // 计时数字 (整秒, 下面)
            int sec = Mathf.CeilToInt(remain);
            if (sec != im.LastShownSecond) {
                im.LastShownSecond = sec;
                foreach (Transform c in im.TimerRoot.transform) DestroyRoot(c.gameObject);
                DrawGlyphLine(im.TimerRoot.transform, sec.ToString(), Color.red, 0.0225f);
                im.TimerRoot.transform.localPosition = new Vector3(im.ImpactBoard.x - sec.ToString().Length * 0.0225f / 2f, im.ImpactBoard.y - 0.08f, ZImpact);
            }
        }
    }

    // ===== 实体图标 (差集回调) =====
    public void SpawnIcon(DcTarget t) {
        if (t == null || t.Entity == null || _icons.ContainsKey(t.Entity)) return;
        var root = new GameObject("FCS2_EntityIcon");
        root.transform.SetParent(t.Entity.transform, false);
        root.transform.localPosition = new Vector3(0f, 0f, -0.02f); // 浮出板面 (同旧版挂件层)
        Color color = t.Side switch {
            Side3.Friendly => new Color(0.3f, 0.6f, 1f),
            Side3.Neutral => new Color(0.2f, 1f, 0.3f),
            _ => new Color(1f, 0.25f, 0.2f),
        };
        DrawIcon(root.transform, t.Kind, color);
        _icons[t.Entity] = root;
    }

    public void RemoveIcon(GameObject go) {
        if (_icons.TryGetValue(go, out var root)) { DestroyRoot(root); _icons.Remove(go); }
    }

    private static void DrawIcon(Transform parent, EntityKind kind, Color color) {
        const float thin = 0.01f, thick = 0.02f;
        switch (kind) {
            case EntityKind.Armour: {
                float s = 0.05f;
                Line(parent, new Vector2(-s, -s), new Vector2(s, -s), thin, color);
                Line(parent, new Vector2(s, -s), new Vector2(s, s), thin, color);
                Line(parent, new Vector2(s, s), new Vector2(-s, s), thin, color);
                Line(parent, new Vector2(-s, s), new Vector2(-s, -s), thin, color);
                Line(parent, new Vector2(0f, s), new Vector2(s, 0f), thin, color);
                Line(parent, new Vector2(s, 0f), new Vector2(0f, -s), thin, color);
                Line(parent, new Vector2(0f, -s), new Vector2(-s, 0f), thin, color);
                Line(parent, new Vector2(-s, 0f), new Vector2(0f, s), thin, color);
                break;
            }
            case EntityKind.Fdc: {
                float r = 0.0267f;
                for (int i = 0; i < 3; i++) {
                    float a = i * 60f * Mathf.Deg2Rad;
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    Line(parent, -dir * r, dir * r, thin, color);
                }
                break;
            }
            case EntityKind.Artillery: {
                float r = 0.0267f;
                const int n = 24;
                for (int i = 0; i < n; i++) {
                    float a0 = i * 2f * Mathf.PI / n, a1 = (i + 1) * 2f * Mathf.PI / n;
                    Line(parent, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, thin, color);
                }
                break;
            }
            case EntityKind.Aa: {
                float half = 0.0267f, bx = 0.03f;
                Line(parent, new Vector2(-0.02f, -half), new Vector2(-0.02f, half), thin, color);
                Line(parent, new Vector2(0.02f, -half), new Vector2(0.02f, half), thin, color);
                Line(parent, new Vector2(-bx, -half / 2f), new Vector2(bx, -half / 2f), thick, color);
                break;
            }
            default: {
                float r = 0.0267f;
                Line(parent, new Vector2(-r, 0f), new Vector2(r, 0f), thin, color);
                Line(parent, new Vector2(0f, -r), new Vector2(0f, r), thin, color);
                break;
            }
        }
    }

    // ===== 图元 =====

    private static void Line(Transform parent, Vector2 a, Vector2 b, float thickness, Color color) {
        var go = new GameObject("FCS2_Seg");
        go.transform.SetParent(parent, false);
        var line = go.AddComponent<Il2CppShapes.Line>();
        line.Thickness = thickness;
        line.Start = new Vector3(a.x, a.y, 0f);
        line.End = new Vector3(b.x, b.y, 0f);
        line.Color = color;
        line.ColorStart = color;
        line.ColorEnd = color;
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

    /// <summary>杀伤圈 (虚线圆, 半径定段数保持虚线周期; solid=整圆, pierce=圈内 X 指示线). 参数变化才重建.</summary>
    private static void RebuildCircle(Transform root, float rKm, float z, Color color, bool solid, bool pierce, ref float lastR, ref int lastN, ref bool lastPierce) {
        float rBoard = rKm * GeoMap.MapCellSize;
        int n = rBoard < 0.12f ? 24 : (rBoard < 0.35f ? 40 : 64);
        if (Mathf.Abs(rKm - lastR) < 0.0001f && n == lastN && pierce == lastPierce && root.childCount > 0) return;
        lastR = rKm;
        lastN = n;
        lastPierce = pierce;
        foreach (Transform c in root) DestroyRoot(c.gameObject);
        const float dashFrac = 0.6f; // 虚线占空比 (旧圈线风格)
        for (int i = 0; i < n; i++) {
            if (!solid && i % 2 == 1) continue; // 隔段空 = 虚线
            float a0 = i * 2f * Mathf.PI / n;
            float a1 = (i + (solid ? 1f : dashFrac)) * 2f * Mathf.PI / n;
            var p0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * rBoard;
            var p1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * rBoard;
            Line(root, p0, p1, 0.01f * GeoMap.MapCellSize, color);
        }
        if (pierce) { // 穿甲: 圈内 X 指示线
            Line(root, new Vector2(-rBoard * 0.6f, rBoard * 0.6f), new Vector2(rBoard * 0.6f, -rBoard * 0.6f), 0.008f * GeoMap.MapCellSize, color);
            Line(root, new Vector2(-rBoard * 0.6f, -rBoard * 0.6f), new Vector2(rBoard * 0.6f, rBoard * 0.6f), 0.008f * GeoMap.MapCellSize, color);
        }
    }

    /// <summary>编号米字数码: 逐字符画 (Glyph16Font 复用), 字符间距 = 字宽.</summary>
    private static void DrawGlyphLine(Transform parent, string text, Color color, float scale) {
        for (int i = 0; i < text.Length; i++) {
            Glyph16Font.DrawCharSegments(parent, text[i], color, i * scale, scale);
        }
    }

    private static bool IsArmorPierce(BulletType bt) => bt is BulletType.AP or BulletType.APHE or BulletType.EQKE or BulletType.ATMC;

    private static void DestroyRoot(GameObject? root) {
        if (root != null) UnityEngine.Object.Destroy(root);
    }

    public enum Slot { Queue, Left, Right, Salvo }

    private class QueueIndicator {
        public GameObject Root = null!;
        public GameObject RadiusRoot = null!;
        public GameObject LabelRoot = null!;
        public GameObject LineRoot = null!;
        public Slot Slot;
        public int QueuePos;
        public bool Salvo;
        public BulletType Shell;
        public string LabelText = "";
        public float RadiusKm = -1f;
        public int SegCount;
        public bool Pierce;
        public Vector2? NestPos;
    }

    private class ImpactIndicator {
        public GameObject Root = null!;
        public GameObject FixedRoot = null!;
        public GameObject SolidRoot = null!;
        public GameObject DotRoot = null!;
        public GameObject CircleRoot = null!;
        public GameObject TimerRoot = null!;
        public Vector2 ImpactBoard;
        public Vector2 NestBoard;
        public BulletType Shell;
        public float FlightTime;
        public float CreatedAt;
        public float RadiusKm = -1f;
        public int SegCount;
        public bool Pierce;
        public int LastShownSecond = -1;
    }

    private class BallisticMark {
        public GameObject? Root;
        public GameObject? RadiusRoot;
        public GameObject? CrossRoot;
        public float RadiusKm = -1f;
        public int SegCount;
        public bool Pierce;
    }
}
