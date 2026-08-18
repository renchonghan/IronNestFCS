using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// [DC] SandboxRenderer — 2.0 渲染线程 (独立循环, 每帧, 迁移期未接线).
/// 持续读各数据源画沙盘: GC push 的实时弹道 (弹种 -1 不渲染) / FC 的打击队列信息 (编号/预瞄/杀伤圈) /
/// 炮弹落点指示器 (红线: 虚线全长固定 + 实线未飞段渐短 + 实心红点弹头) / 实体图标 (差集回调).
/// 3D 物件生命周期: 实体死亡/任务取消/令牌离图 → 清退; F9 → 全清.
/// </summary>
public class SandboxRenderer {
    // ===== 3D 指示器状态 =====
    private readonly Dictionary<GameObject, QueueIndicator> _queueMarks = new();
    private readonly List<ImpactIndicator> _impacts = new();
    private readonly Dictionary<GameObject, GameObject> _icons = new();

    private object? _loopHandle;
    private bool _disposed;

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
            UpdateImpacts();   // 红线/弹头位置随剩余时长推进; 结束自毁
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
    }

    /// <summary>实时弹道指示器 (GC 每帧 push): 弹种 -1 = 未选/未就绪, 不渲染. 3D 十字/LR 挂件按数据刷新.</summary>
    public void PushBallistic(LeftRight side, float aimX, float aimY, float killRadiusKm, int bulletType) {
        // 骨架: 3D 瞄准十字挂件管理 (开火前的绿色十字/LR) 待接线期实装, 先只存状态
        _ballistic[side] = (aimX, aimY, killRadiusKm, bulletType);
    }
    private readonly Dictionary<LeftRight, (float, float, float, int)> _ballistic = new();

    /// <summary>打击队列指示器 (FC 调用): 绑定目标 + 槽位/队列位/齐射/弹种/预瞄偏移.
    /// 显示 = 弹种标签 + 编号米字数码 (00T/01N/02X; 在炮上 L-N / R-X).</summary>
    public void UpdateQueueIndicator(GameObject entity, Slot slot, int queuePos, bool salvo, BulletType shell, float leadOffset) {
        if (queuePos < 0) {
            if (_queueMarks.TryGetValue(entity, out var old)) { DestroyRoot(old.Root); _queueMarks.Remove(entity); }
            return; // -1 = 移出队列
        }
        if (!_queueMarks.TryGetValue(entity, out var mark)) {
            mark = new QueueIndicator { Root = new GameObject("FCS_QueueMark") };
            _queueMarks[entity] = mark;
        }
        mark.Slot = slot;
        mark.QueuePos = queuePos;
        mark.Salvo = salvo;
        mark.Shell = shell;
        mark.LeadOffset = leadOffset;
    }

    /// <summary>炮弹落点指示器 (FC 击发时调用一次): 红线 (虚线固定全长 + 实线未飞段渐短 + 实心红点弹头) +
    /// 杀伤圈, 下面计时 上面弹种; 飞行时长结束后自动销毁.</summary>
    public void CreateImpact(Vector3 impactPos, BulletType shell, float flightTime, float nestPosX, float nestPosY) {
        _impacts.Add(new ImpactIndicator {
            Root = new GameObject("FCS_ImpactIndicator"),
            ImpactPos = impactPos,
            Shell = shell,
            FlightTime = flightTime,
            CreatedAt = Time.time,
            NestX = nestPosX,
            NestY = nestPosY,
        });
    }

    /// <summary>实体图标差集回调 (DisplayControl 数据循环调用): 按类型/敌我画图标挂件
    /// (与旧版同款等宽线风格: 装甲=方形+菱形叠加边长相等, FDC=三条线六芒星, 炮兵=圆, AA=两短竖线+2倍粗底座, 参考点=绿色十字).</summary>
    public void SpawnIcon(DcTarget t) {
        if (t == null || t.Entity == null || _icons.ContainsKey(t.Entity)) return;
        var root = new GameObject("FCS2_EntityIcon");
        root.transform.SetParent(t.Entity.transform, false);
        root.transform.localPosition = new Vector3(0f, 0f, -0.02f); // 浮出板面
        Color color = t.Side switch {
            Side3.Friendly => new Color(0.3f, 0.6f, 1f),   // 友军蓝
            Side3.Neutral => new Color(0.2f, 1f, 0.3f),    // 参考点绿
            _ => new Color(1f, 0.25f, 0.2f),               // 敌对红
        };
        DrawIcon(root.transform, t.Kind, color);
        _icons[t.Entity] = root;
    }

    public void RemoveIcon(GameObject go) {
        if (_icons.TryGetValue(go, out var root)) { DestroyRoot(root); _icons.Remove(go); }
    }

    /// <summary>实体图标绘制 (实体局部系, 与旧版同款线宽 0.01/0.005).</summary>
    private static void DrawIcon(Transform parent, EntityKind kind, Color color) {
        const float thin = 0.01f, thick = 0.02f;
        switch (kind) {
            case EntityKind.Armour: {
                // 方形 + 菱形叠加, 边长相等 (0.1)
                float s = 0.05f; // 半边长
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
                // 三条线六芒星 (*): 过中心三条直线, 半径 0.0267, 夹角 60°
                float r = 0.0267f;
                for (int i = 0; i < 3; i++) {
                    float a = i * 60f * Mathf.Deg2Rad;
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    Line(parent, -dir * r, dir * r, thin, color);
                }
                break;
            }
            case EntityKind.Artillery: {
                // 圆 r=0.0267 (24 段)
                float r = 0.0267f;
                const int n = 24;
                for (int i = 0; i < n; i++) {
                    float a0 = i * 2f * Mathf.PI / n, a1 = (i + 1) * 2f * Mathf.PI / n;
                    Line(parent, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r,
                        new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, thin, color);
                }
                break;
            }
            case EntityKind.Aa: {
                // 两条短竖线 (x=±0.02, 高 0.0534) + 2 倍粗底座 (竖线靠下 3/4 处, |_|)
                float half = 0.0267f, bx = 0.03f;
                Line(parent, new Vector2(-0.02f, -half), new Vector2(-0.02f, half), thin, color);
                Line(parent, new Vector2(0.02f, -half), new Vector2(0.02f, half), thin, color);
                Line(parent, new Vector2(-bx, -half / 2f), new Vector2(bx, -half / 2f), thick, color);
                break;
            }
            default: {
                // 参考点/其他: 绿色十字 (非目标)
                float r = 0.0267f;
                Line(parent, new Vector2(-r, 0f), new Vector2(r, 0f), thin, color);
                Line(parent, new Vector2(0f, -r), new Vector2(0f, r), thin, color);
                break;
            }
        }
    }

    /// <summary>一段直线 (Il2CppShapes.Line, 实体局部系).</summary>
    private static void Line(Transform parent, Vector2 a, Vector2 b, float thickness, Color color) {
        var go = new GameObject("FCS2_IconSeg");
        go.transform.SetParent(parent, false);
        var line = go.AddComponent<Il2CppShapes.Line>();
        line.Thickness = thickness;
        line.Start = new Vector3(a.x, a.y, 0f);
        line.End = new Vector3(b.x, b.y, 0f);
        line.Color = color;
        line.ColorStart = color;
        line.ColorEnd = color;
    }

    /// <summary>每帧: 落点指示器推进 — 弹头沿铁巢→落点直线按剩余时长移动; 结束自毁.</summary>
    private void UpdateImpacts() {
        for (int i = _impacts.Count - 1; i >= 0; i--) {
            var im = _impacts[i];
            float remain = im.FlightTime - (Time.time - im.CreatedAt);
            if (remain <= 0f) {
                DestroyRoot(im.Root);
                _impacts.RemoveAt(i);
                continue;
            }
            // 骨架: 红线/弹头 3D 挂件推进 (接线期实装, 画法参照旧 BuildKillCircle/虚线)
        }
    }

    private static void DestroyRoot(GameObject? root) {
        if (root != null) UnityEngine.Object.Destroy(root);
    }

    /// <summary>槽位 (Slot enum): Queue 排队中 / Left / Right / Salvo 在炮上.</summary>
    public enum Slot { Queue, Left, Right, Salvo }

    private class QueueIndicator {
        public GameObject Root = null!;
        public Slot Slot;
        public int QueuePos;
        public bool Salvo;
        public BulletType Shell;
        public float LeadOffset;
    }

    private class ImpactIndicator {
        public GameObject Root = null!;
        public Vector3 ImpactPos;
        public BulletType Shell;
        public float FlightTime;
        public float CreatedAt;
        public float NestX;
        public float NestY;
    }
}
