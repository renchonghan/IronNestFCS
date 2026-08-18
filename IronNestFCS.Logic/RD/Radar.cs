using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public enum Side3 { Friendly, Enemy, Neutral }

public enum EntityKind { Other, Infantry, Armour, Fdc, Artillery, Aa, Reference }

/// <summary>SRC 列表元素 (2.0 纯传感器输出): 实体句柄/世界位置/敌我三态/类型/装甲信息.</summary>
public class SrcContact {
    public GameObject Entity = null!;
    public Vector3 WorldPos;
    public Side3 Side;
    public EntityKind Kind;
    public int Armour;
}

/// <summary>
/// [RD] Radar — 2.0 纯传感器 (迁移期与旧 TacticalRadar 并存, 未接线).
/// 两速扫描: 扫描 (1~3s, 全场景扫实体+敌我分类+存活检测, 死了不报) + 粗跟 (25fps, 已知目标位置刷新).
/// 排除项: 阵亡实体 (游戏留尸并挂 EnemyKillTokens 击杀令牌, 名字兜底扫描会误抓, 两者都不报).
/// 分类复用 TacticalRadar 的静态方法 (GetIcon/GetArmour/IsHostile), 旧雷达退役后逻辑收编进本类.
/// </summary>
public class Radar {
    private readonly List<SrcContact> _contacts = new();
    private object? _loopHandle;
    private float _lastScan;
    private bool _disposed;
    private int _tick;
    private bool _logFirstScan = true; // 首轮扫描打分类日志 (调试完关)

    public IReadOnlyList<SrcContact> Contacts => _contacts;

    public void Start() {
        _disposed = false;
        _loopHandle = MelonCoroutines.Start(Loop());
    }

    public void Stop() {
        _disposed = true;
        if (_loopHandle != null) {
            try { MelonCoroutines.Stop(_loopHandle); } catch { }
        }
        _loopHandle = null;
        _contacts.Clear();
    }

    private IEnumerator Loop() {
        while (!_disposed) {
            yield return new WaitForSeconds(0.04f);
            if (Time.time - _lastScan > 2f) {
                ScanOnce();
                _lastScan = Time.time;
            }
            TrackRefresh(); // 25fps 粗跟: 已知目标位置刷新 (+每 10 帧存活快查)
        }
    }

    /// <summary>全场景扫描: Fire Mission Root 子节点 + 名字兜底; 分类+存活+排除.</summary>
    private void ScanOnce() {
        _contacts.Clear();
        var root = GameObject.Find("Fire Mission Root")?.transform;
        if (root != null) {
            for (int i = 0; i < root.childCount; i++) {
                TryAdd(root.GetChild(i));
            }
        }
        // 名字兜底: 场景根层 Enemy/Tgt_ 命名实体 (排除项同旧雷达)
        foreach (var go in GameObject.FindObjectsOfType<GameObject>()) {
            if (go == null || go.transform.parent != null) continue; // 只扫根层, 避免与 Fire Mission Root 重复
            var n = go.name;
            if (!n.StartsWith("Enemy") && !n.Contains("Tgt_")) continue;
            TryAdd(go.transform);
        }
        _logFirstScan = false; // 首轮分类日志打完关
    }

    private void TryAdd(Transform t) {
        if (t == null || t.gameObject == null) return;
        string name = t.name;
        if (IsExcluded(name)) return;
        var loc = t.GetComponent<EntityLocation>();
        if (loc != null) {
            if (!TacticalRadar.IsUnitAlive(loc, t.gameObject)) return; // 死了不报
            var side = ClassifySide(loc, name);
            var kind = ClassifyKind(loc, name);
            var (armour, immune) = TacticalRadar.GetArmour(loc);
            if (_logFirstScan) {
                MelonLogger.Msg($"[RD] classify {name}: side={side} kind={kind} armour={armour} immune={immune} icon='{TacticalRadar.GetIcon(loc)}'");
            }
            _contacts.Add(new SrcContact {
                Entity = t.gameObject,
                WorldPos = t.position,
                Side = side,
                Kind = kind,
                Armour = armour,
            });
            return;
        }
        // 无 EntityLocation: 名字兜底的 (照旧雷达口径, 敌)
        if (name.StartsWith("Enemy") || name.Contains("Tgt_")) {
            _contacts.Add(new SrcContact {
                Entity = t.gameObject,
                WorldPos = t.position,
                Side = Side3.Enemy,
                Kind = EntityKind.Other,
            });
        }
    }

    /// <summary>25fps 粗跟: 位置刷新; 每 10 帧 (0.4s) 存活快查, 阵亡移出列表.</summary>
    private void TrackRefresh() {
        for (int i = _contacts.Count - 1; i >= 0; i--) {
            var c = _contacts[i];
            if (c.Entity == null) { _contacts.RemoveAt(i); continue; }
            if (++_tick % 10 == 0) {
                var loc = c.Entity.GetComponent<EntityLocation>();
                if (loc != null && !TacticalRadar.IsUnitAlive(loc, c.Entity)) { _contacts.RemoveAt(i); continue; }
            }
            c.WorldPos = c.Entity.transform.position;
        }
    }

    /// <summary>敌我三态 (敌/友/中立). IsHostile 旧口径 bool 化, 参考点/友军归非敌; 名字兜底沿用旧规则.</summary>
    private static Side3 ClassifySide(EntityLocation loc, string name) {
        try {
            var role = GetRole(loc);
            if (role >= 0) {
                if ((role & 33554432) != 0) return Side3.Neutral;          // Reference
                bool ally = (role & 2) != 0, enemy = (role & 1) != 0;
                if (ally && !enemy) return Side3.Friendly;
                if (enemy) return Side3.Enemy;
            }
            var icon = TacticalRadar.GetIcon(loc).ToLower();
            if (icon.Contains("friendly") || icon.Contains("frendly")) return Side3.Friendly;
            if (icon.Contains("enemy")) return Side3.Enemy;
        }
        catch { }
        var n = name.ToLower();
        if (n.Contains("reference") || n.Contains("ref")) return Side3.Neutral;
        if (n.Contains("friendly") || n.Contains("ally")) return Side3.Friendly;
        return Side3.Enemy;
    }

    /// <summary>类型分类 (装甲/FDC/炮兵/AA/参考点): icon 字符串为主, 名字兜底.
    /// 注意游戏 icon 拼写: "Refrence" (少个 e), "Frendly" (少个 i); FDC 在部分关卡叫 "Artillery Observer" (须先于炮兵判定).</summary>
    private static EntityKind ClassifyKind(EntityLocation loc, string name) {
        string n = name.ToLower();
        try {
            var icon = TacticalRadar.GetIcon(loc).ToLower();
            if (icon.Contains("fire direction") || icon.Contains("observer") || n.Contains("fdc")) return EntityKind.Fdc;
            if (icon.Contains("artillery")) return EntityKind.Artillery;
            if (icon.Contains("anti") && icon.Contains("air")) return EntityKind.Aa;
            if (icon.Contains("refrence") || icon.Contains("reference")) return EntityKind.Reference;
            var (armour, immune) = TacticalRadar.GetArmour(loc);
            if (armour > 0 || immune > 0) return EntityKind.Armour; // 装甲 = 有装甲值或免疫弹种 (机械化 icon 带 armor 但值全 0, 不算)
        }
        catch { }
        if (n.Contains("fdc")) return EntityKind.Fdc;
        if (n.Contains("artillery")) return EntityKind.Artillery;
        if (n.Contains("aa")) return EntityKind.Aa;
        if (n.Contains("infantry")) return EntityKind.Infantry;
        if (n.Contains("refrence") || n.Contains("reference") || n.Contains("ref")) return EntityKind.Reference;
        return EntityKind.Other;
    }

    private static int GetRole(EntityLocation loc) {
        try {
            var entityProp = loc.GetType().GetProperty("Entity", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (entityProp == null) return -1;
            var entity = entityProp.GetValue(loc);
            if (entity == null) return -1;
            var roleProp = entity.GetType().GetProperty("Role", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (roleProp == null) return -1;
            var v = roleProp.GetValue(entity);
            if (v is int i) return i;
            if (v is Enum e) return System.Convert.ToInt32(e);
        }
        catch { }
        return -1;
    }

    /// <summary>排除项: 阵亡实体 (留尸 + EnemyKillTokens 击杀令牌, 名字兜底会误抓, 两者都不报).</summary>
    private static bool IsExcluded(string name) {
        var n = (name ?? "").ToLower();
        return n.Contains("enemykilltokens") || n.Contains("killtokens");
    }
}
