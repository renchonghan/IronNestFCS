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

    /// <summary>实体图标差集回调 (DisplayControl 数据循环调用).</summary>
    public void SpawnIcon(DcTarget t) { /* 骨架: 按类型/敌我画图标挂件 (装甲/FDC/炮兵/AA/参考点), 接线期实装 */ }
    public void RemoveIcon(GameObject go) { if (_icons.TryGetValue(go, out var root)) { DestroyRoot(root); _icons.Remove(go); } }

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
