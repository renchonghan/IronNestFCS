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
/// [RD] Radar — 2.0 纯传感器 (实体分类工具已自旧 TacticalRadar 收编, 旧雷达层清退).
/// 两速扫描: 扫描 (2s, 全场景扫实体+敌我分类+存活检测, 死了不报) + 粗跟 (25fps, 已知目标位置刷新).
/// 排除项: 阵亡实体 (游戏留尸并挂 EnemyKillTokens 击杀令牌, 名字兜底扫描会误抓, 两者都不报).
/// </summary>
public class Radar {
    private readonly List<SrcContact> _contacts = new();
    private object? _loopHandle;
    private float _lastScan;
    private float _lastTick;
    private bool _disposed;
    private int _tick;
    private bool _simInjected; // 假信号已注入 (扫描清表后重注; 防每 tick 重复追加堆积)
    private bool _logFirstScan = true; // 首轮扫描打分类日志 (调试完关)

    public IReadOnlyList<SrcContact> Contacts => _contacts;

    /// <summary>雷达电源 (SAR RADAR 按钮): 关 = 不扫描不出目标 (Contacts 清空 — 下游目标全撤), 默认关 (开机先开雷达).</summary>
    public bool Power;

    // ===== 信号注入 (测试设施: 模拟 DBF/预处理机柜前端注入的信号源) =====
    // 假目标载体 = 雷达自建空壳 (无渲染, 名字避开扫描兜底与排除词), 信号从雷达输出端 (Contacts) 注入 —
    // 下游 DC 目标表 / TWS 滤波 / 实体图标 / 右键入队 / FC 解算全走正式流程, 与真实目标无差别 (可实际击打).
    // 代码开关 (不进 UI): 测试设施 — final debug 完成已退役, 再测时改回 true.
    public bool SimInject = false;
    /// <summary>载体母体 (板面局部系; FcsModule 注入 "Draggable Surface").</summary>
    public Transform? MapSurfaceRef;
    private readonly List<(GameObject Shell, Vector3 VelLocal, Vector3 StartLocal, float StartTime)> _sims = new();

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
        foreach (var (shell, _, _, _) in _sims) if (shell != null) UnityEngine.Object.Destroy(shell);
        _sims.Clear();
    }

    private IEnumerator Loop() {
        while (!_disposed) {
            yield return null;
            if (Time.time - _lastTick < 0.04f) continue; // 硬节流 25Hz: 不依赖 WaitForSeconds (协程调度异常时照样锁频)
            _lastTick = Time.time;
            if (!Power) { _contacts.Clear(); continue; } // 雷达关: 无扫描无目标 (下游 DC 目标表随之清空, FC 撤任务)
            if (Time.time - _lastScan > 2f) {
                ScanOnce();
                _lastScan = Time.time;
            }
            StepSimTargets();   // 信号源推进假目标载体 (surface 未注入时跳过, 下 tick 再建)
            InjectSimContacts(); // 假信号注入输出端 (扫描清表之后 — 扫描本身扫不到空壳)
            TrackRefresh();     // 25fps 粗跟: 已知目标位置刷新 (+每 10 帧存活快查)
        }
    }

    /// <summary>全场景扫描: Fire Mission Root 子节点 + 名字兜底; 分类+存活+排除.</summary>
    private void ScanOnce() {
        _contacts.Clear();
        _simInjected = false; // 扫描清表 → 假信号重注
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
            Side3 side;
            EntityKind kind;
            int armour, immune;
            if (!IsUnitAlive(loc, t.gameObject)) return; // 死了不报
            side = ClassifySide(loc, name);
            kind = ClassifyKind(loc, name);
            (armour, immune) = GetArmour(loc);
            if (_logFirstScan) {
                MelonLogger.Msg($"[RD] classify {name}: side={side} kind={kind} armour={armour} immune={immune} icon='{GetIcon(loc)}'");
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
                if (loc != null && !IsUnitAlive(loc, c.Entity)) { _contacts.RemoveAt(i); continue; }
            }
            c.WorldPos = c.Entity.transform.position;
        }
    }

    // ===== 信号注入 =====

    /// <summary>建/推进假目标载体: 空壳挂板面 (surface 未注入时跳过, 下 tick 再建).
    /// 推进 = 时间驱动 (位置 = 起点 + 速度 × 真实时间): 与协程 tick 相位无关 — DC 采样每帧都采到真实位移,
    /// 不会出现 "推进 tick 与采样 tick 锁相 → 位置差分混叠 → 速度被压成残余抖动" (矢量符时有时无事故).</summary>
    private void StepSimTargets() {
        if (!SimInject || MapSurfaceRef == null) return;
        if (_sims.Count == 0) CreateSimTargets();
        foreach (var (shell, vel, start, t0) in _sims) {
            shell.transform.localPosition = new Vector3(start.x, start.y, 0f) + (Vector3)(vel * (Time.time - t0));
        }
    }

    /// <summary>3 个 30 m/s 假目标 (用户口径): 起点绕板面中心 2~4 km, 航向各异 (横向/两向斜穿), 一次看全 TWS 点式轨迹疏密与方向表现.</summary>
    private void CreateSimTargets() {
        var c = new Vector2(
            GeoMap.MapBottomLeft.x + 10f * GeoMap.MapCellSize,
            GeoMap.MapBottomLeft.y + 5f * GeoMap.MapCellSize); // 板面中心 (A-T × 1-10)
        float km = GeoMap.KmPerLocal;
        const float SpdKmS = 0.03f; // 30 m/s → km/s
        float spd = SpdKmS / km;    // → 板面 local/s
        AddSim(c + new Vector2(-3f / km, 2f / km), new Vector2(1f, 0f) * spd);                    // 沿 +x 横穿
        AddSim(c + new Vector2(4f / km, -3f / km), new Vector2(-1f, 0.5f).normalized * spd);      // 左斜穿
        AddSim(c + new Vector2(-2f / km, -4f / km), new Vector2(1f, 1f).normalized * spd);        // 右斜穿
        MelonLogger.Msg($"[RD] sim inject: 3 targets @ 30 m/s");
    }

    private void AddSim(Vector2 localPos, Vector2 velLocal) {
        var shell = new GameObject($"FCS_SimSrc_{_sims.Count + 1:00}"); // 名字避开扫描兜底 (Enemy/Tgt_) 与排除词 (killtokens/phantom)
        shell.transform.SetParent(MapSurfaceRef, false);
        shell.transform.localScale = new Vector3(0.212f, 0.212f, 0.212f); // 对齐实体棋子缩放: 点击盒/挂件世界尺寸与真实实体一致 (默认 1 会放大 ~4.7 倍 → "巨大点击实体"事故)
        shell.transform.localPosition = new Vector3(localPos.x, localPos.y, 0f);
        _sims.Add((shell, velLocal, localPos, Time.time));
    }

    /// <summary>假信号注入 Contacts (仅在扫描清表后 — 模拟 DBF 信号源接在雷达输出端, 下游全走正式流程).
    /// _simInjected 门控: 每 tick 追加会在 2s 扫描窗口内堆积 ~50 份重复条目 → DC 每轮遍历重复空壳,
    /// hist 被零位移样本稀释 ~50 倍 (30 m/s 量成 1 m/s) + TrackMotion 每秒数千次调用 (trak 日志洪水) — 事故见 dev-incident-log.</summary>
    private void InjectSimContacts() {
        if (!SimInject || _simInjected) return;
        _simInjected = true;
        foreach (var (shell, _, _, _) in _sims) {
            _contacts.Add(new SrcContact {
                Entity = shell,
                WorldPos = shell.transform.position,
                Side = Side3.Enemy,
                Kind = EntityKind.Armour,
                Armour = 100,
            });
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
            var icon = GetIcon(loc).ToLower();
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
            var icon = GetIcon(loc).ToLower();
            if (icon.Contains("fire direction") || icon.Contains("observer") || n.Contains("fdc")) return EntityKind.Fdc;
            if (icon.Contains("artillery")) return EntityKind.Artillery;
            if (icon.Contains("anti") && icon.Contains("air")) return EntityKind.Aa;
            if (icon.Contains("refrence") || icon.Contains("reference")) return EntityKind.Reference;
            var (armour, immune) = GetArmour(loc);
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

    /// <summary>排除项: 阵亡实体 (留尸 + EnemyKillTokens 击杀令牌, 名字兜底会误抓, 两者都不报) + Phantom Battery (演示幻影炮组, 旧雷达同款).</summary>
    private static bool IsExcluded(string name) {
        var n = (name ?? "").ToLower();
        return n.Contains("enemykilltokens") || n.Contains("killtokens") || n.Contains("phantom");
    }

    // ===== 实体信息静态工具 (旧 TacticalRadar 收编, 2026-09-06) =====

    /// <summary>实体存活 (旧 TacticalRadar.IsUnitAlive 收编): enabled=false / 死字段 / 非 active = 死了.</summary>
    private static bool IsUnitAlive(EntityLocation loc, GameObject go) {
        if (!go.activeInHierarchy) return false;
        try {
            var type = loc.GetType();
            var enabledProp = type.GetProperty("enabled", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (enabledProp != null) {
                var enabledVal = enabledProp.GetValue(loc);
                if (enabledVal is bool b && !b) return false;
            }
            var entityProp = type.GetProperty("Entity", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (entityProp != null) {
                var entity = entityProp.GetValue(loc);
                if (entity != null) {
                    var entType = entity.GetType();
                    foreach (var f in entType.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) {
                        var fn = f.Name.ToLower();
                        if (fn.Contains("alive") || fn.Contains("dead") || fn.Contains("destroyed") || fn.Contains("health") || fn.Contains("active")) {
                            var val = f.GetValue(entity);
                            if (val is bool bVal) return fn.Contains("alive") || fn.Contains("active") ? bVal : !bVal;
                            if (val is float fVal) return fVal > 0;
                            if (val is int iVal) return iVal > 0;
                        }
                    }
                    foreach (var p in entType.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) {
                        var pn = p.Name.ToLower();
                        if (pn.Contains("alive") || pn.Contains("dead") || pn.Contains("destroyed") || pn.Contains("active")) {
                            var val = p.GetValue(entity);
                            if (val is bool bVal) return pn.Contains("alive") || pn.Contains("active") ? bVal : !bVal;
                        }
                    }
                }
            }
        }
        catch { }
        return go.activeSelf;
    }

    /// <summary>实体 Icon 字符串 (如 "Enemy Field Artillery Observer"), 无则空串.</summary>
    private static string GetIcon(EntityLocation loc) {
        try {
            var type = loc.GetType();
            var entityProp = type.GetProperty("Entity", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (entityProp == null) return "";
            var entity = entityProp.GetValue(loc);
            if (entity == null) return "";
            var iconProp = entity.GetType().GetProperty("Icon", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (iconProp == null) return "";
            var v = iconProp.GetValue(entity);
            if (v is string s) return s;
        }
        catch { }
        return "";
    }

    /// <summary>实体装甲信息: (Armour 值, 免疫弹种数). 装甲目标 = Armour>0 或有免疫弹种 (只有非免疫弹种能处理).</summary>
    private static (int armour, int immune) GetArmour(EntityLocation loc) {
        try {
            var type = loc.GetType();
            var entityProp = type.GetProperty("Entity", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (entityProp == null) return (0, 0);
            var entity = entityProp.GetValue(loc);
            if (entity == null) return (0, 0);
            var entType = entity.GetType();
            int armour = 0;
            var armourProp = entType.GetProperty("Armour", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (armourProp != null) {
                var v = armourProp.GetValue(entity);
                if (v is int i) armour = i;
            }
            int immune = 0;
            var immuneProp = entType.GetProperty("ImmuneShells", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (immuneProp != null) {
                var v = immuneProp.GetValue(entity);
                var list = v as Il2CppSystem.Collections.Generic.List<string>;
                if (list != null) immune = list.Count;
            }
            return (armour, immune);
        }
        catch { }
        return (0, 0);
    }
}
