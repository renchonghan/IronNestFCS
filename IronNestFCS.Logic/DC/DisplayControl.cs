using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// [DC] DisplayControl — 2.0 显控台数据循环 (迁移期: 未接线).
/// 数据循环 25fps: 铁巢位置 / Radar SRC → Target 列表 (相对位置 + TWS 5 帧平均速度) / 实体图标差集 / 操作响应 (请求).
/// 渲染不在这里 — 独立渲染线程 <see cref="SandboxRenderer"/>.
/// 状态端口 (FC 读): Target 列表 + 火控请求 + AutoFire/AutoTask 开关位.
/// </summary>
public class DisplayControl {
    public Radar? RadarPort;                 // SRC 数据源 (FcsModule 注入)
    public Transform? NestRef;               // 铁巢 (相对位置基准)
    public Transform? MapSurfaceRef;         // 沙盘表面 (局部系换算)
    public FireControl? FcPort;              // 右键 toggle (入队/升级齐射/取消) 用

    // ===== 状态端口 =====
    public readonly List<DcTarget> Targets = new();
    public readonly List<FireTask> Requests = new();   // 火控请求 (FC 消费后清)
    public bool AutoFire;
    public bool AutoTask;
    public bool Tws;                                  // TWS 开关 (关 = 速度矢量给空, 火控自然无预瞄)

    // ===== 内部 =====
    private readonly Dictionary<GameObject, DcTarget> _targetMap = new();    // 目标参数表 (对象复用, FC 直读; Entity → 位置/轨迹参数)
    private readonly Dictionary<GameObject, List<Vector3>> _posHist = new(); // 粗跟: 5 帧位置环 (所有目标)
    private readonly Dictionary<GameObject, List<Vector3>> _fineHist = new();// 精跟: 25 帧深度缓存 (上炮目标, 二阶曲线)
    private readonly HashSet<GameObject> _icons = new();                     // 已挂图标 (差集用)
    private object? _loopHandle;
    private bool _disposed;

    /// <summary>显控排布 (舰长席): AutoTask 扫荡 = 只从敌对目标按优先级发请求; 优先级 FDC > 炮兵 > 装甲 > 其他.</summary>
    public System.Action<IReadOnlyList<DcTarget>>? OnSweepQueue;

    public void Start() {
        _disposed = false;
        _loopHandle = MelonCoroutines.Start(Loop());
    }

    public void Stop() {
        _disposed = true;
        if (_loopHandle != null) { try { MelonCoroutines.Stop(_loopHandle); } catch { } }
        _loopHandle = null;
        Targets.Clear();
        Requests.Clear();
        _targetMap.Clear();
        _posHist.Clear();
        _fineHist.Clear();
        _icons.Clear();
    }

    /// <summary>数据循环 25fps.</summary>
    private IEnumerator Loop() {
        while (!_disposed) {
            yield return new WaitForSeconds(0.04f);
            SyncNestToken();     // 铁巢棋子吸附实际炮位 (旧版同款: 棋子摆偏不导致火控打飞)
            if (RadarPort == null) continue;
            RefreshTargets();
            UpdateIcons();       // 实体图标差集 (在列表→画, 不在→删)
            TrackTokens();       // 令牌拖放: 上地图注册虚拟目标, 拖离自动取消
            if (AutoTask) Sweep();
        }
    }

    /// <summary>铁巢棋子吸附 (旧版 SyncIronNestToken 同款): 按炮塔实际网格位置放棋子, 紧急转移后跟得上.</summary>
    private ImpactMarkerManager? _impactManager;
    private void SyncNestToken() {
        if (NestRef == null) return;
        if (_impactManager == null) {
            _impactManager = GameObject.Find("---ImpactMarkerManager")?.GetComponent<ImpactMarkerManager>();
        }
        if (_impactManager == null || _impactManager.turretController == null) return;
        var tb = _impactManager.turretController.turretBase;
        if (tb == null) return;
        var grid = tb.localPosition;
        NestRef.localPosition = new Vector3(
            GeoMap.MapBottomLeft.x + grid.x * GeoMap.MapCellSize,
            GeoMap.MapBottomLeft.y + grid.y * GeoMap.MapCellSize,
            NestRef.localPosition.z);
    }

    /// <summary>SRC → 目标参数表 (对象复用, FC 直读): 相对位置 (方位/距离) + TWS 轨迹参数 (速度/加速度).
    /// 粗跟 (所有目标): 5 帧环一阶; 精跟 (上炮目标): 25 深度缓存二阶曲线 (过渡期一阶输出).</summary>
    private void RefreshTargets() {
        var alive = new HashSet<GameObject>();
        foreach (var c in RadarPort.Contacts) {
            if (c == null || c.Entity == null) continue;
            alive.Add(c.Entity);
            UpsertTarget(c.Entity, c.Entity.name, c.WorldPos, c.Side, c.Kind, c.Armour, false);
        }
        // 令牌虚拟目标 (位置源由 DC 管理): 同表, 句柄为令牌
        foreach (var tok in _tokens) {
            if (tok.Key == null || tok.Key.gameObject == null) continue;
            alive.Add(tok.Key.gameObject);
            UpsertTarget(tok.Key.gameObject, tok.Value, tok.Key.position, Side3.Enemy, EntityKind.Other, 0, true);
        }
        // 消失的目标 (阵亡/离图): 出表 (FC 读不到 → 撤任务)
        var dead = new List<GameObject>();
        foreach (var k in _targetMap.Keys) if (k == null || !alive.Contains(k)) dead.Add(k);
        foreach (var k in dead) _targetMap.Remove(k);
        PruneHistories(alive);
        Targets.Clear();
        Targets.AddRange(_targetMap.Values); // 渲染差集/扫荡用列表 (引用复用)
    }

    /// <summary>单目标参数更新/建表: 精跟 = 上炮任务目标 (活读 FC 槽位), TWS 轨迹参数按粗/精跟分档.</summary>
    private void UpsertTarget(GameObject go, string name, Vector3 pos, Side3 side, EntityKind kind, int armour, bool isVirtual) {
        bool fine = FcPort != null && (FcPort.LeftTask?.Entity == go || FcPort.RightTask?.Entity == go);
        Vector2 vel = Vector2.zero, acc = Vector2.zero, jerk = Vector2.zero;
        if (Tws) (vel, acc, jerk) = TrackMotion(go, pos, fine);
        if (_targetMap.TryGetValue(go, out var t)) {
            t.WorldPos = pos;
            t.Velocity = vel;
            t.Accel = acc;
            t.Jerk = jerk;
            t.Side = side;
            t.Kind = kind;
            t.Armour = armour;
        }
        else _targetMap[go] = new DcTarget {
            Entity = go, Name = name, WorldPos = pos, Velocity = vel, Accel = acc, Jerk = jerk,
            Side = side, Kind = kind, Armour = armour, Virtual = isVirtual,
        };
    }

    /// <summary>目标参数表查询 (FC 读参): Entity → 位置/轨迹参数. null = 目标失效 (FC 撤任务).</summary>
    public DcTarget? GetTarget(GameObject go) {
        if (go == null) return null;
        return _targetMap.TryGetValue(go, out var t) ? t : null;
    }

    /// <summary>TWS 运动估计 (轨迹参数: 速度/加速度/三次项, km/s, km/s², km/s³):
    /// 粗跟 (所有目标): 5 帧环一阶 LS 直出; 精跟 (上炮目标): 25 深度缓存 — 进入时压入粗跟记录接续 (不丢历史),
    /// 缓存满 25 帧 (采样帧 1/9/17/25 = a-b-c-d 三段轨迹固定) 转三次插值曲线; 积累期继续一阶输出; 退出精跟降回粗跟 (清缓存).</summary>
    private (Vector2 v, Vector2 a, Vector2 j) TrackMotion(GameObject go, Vector3 pos, bool fine) {
        if (!_posHist.TryGetValue(go, out var hist)) _posHist[go] = hist = new List<Vector3>();
        hist.Add(pos);
        if (hist.Count > 5) hist.RemoveAt(0);
        if (fine) {
            if (!_fineHist.TryGetValue(go, out var fh)) {
                _fineHist[go] = fh = new List<Vector3>();
                fh.AddRange(hist); // 粗跟记录压入 (接续, 不丢历史)
            }
            fh.Add(pos);
            if (fh.Count > 25) fh.RemoveAt(0);
            if (fh.Count >= 25) return ThirdOrder(fh); // 缓存满 (帧 1/9/17/25 = 4 采样点): a-b-c-d 三段轨迹固定 → 三次插值曲线 (e' 预测)
        }
        else {
            _fineHist.Remove(go); // 退出精跟: 降回粗跟
        }
        if (hist.Count < 5) return (Vector2.zero, Vector2.zero, Vector2.zero); // 不足 5 帧: 无跟踪
        // 一阶: 最近 5 帧最小二乘斜率 (Σ(t-t̄)²=10); 精跟过渡期同样走这里
        Vector2 slope = Vector2.zero;
        for (int i = 0; i < hist.Count; i++) slope += (Vector2)hist[i] * (i - 2);
        slope /= 10f;
        return (slope * 25f / 3.8164f, Vector2.zero, Vector2.zero); // 帧斜率 × 25fps → km/s
    }

    /// <summary>三次插值曲线解析 (精跟, 缓存满 25 帧): 采样点帧 1/9/17/25 (索引 0/8/16/24, 间隔 8 帧 = 0.32s),
    /// 三次插值多项式过 4 点 (插值样条/SAI2 手感, 无尖角), 最新点 P₃ 端点导数 = 牛顿后差闭式:
    /// v = [11P₃-18P₂+9P₁-2P₀]/(6h), a = [2P₃-5P₂+4P₁-P₀]/h², j = [P₃-3P₂+3P₁-P₀]/h³.
    /// 曲线延伸 P(d+T) = p + vT + ½aT² + ⅙jT³ (e' 在弧上, 不是切线直线); 匀速直线 Δ²=Δ³=0 自动退化纯 v.
    /// 返回 (v, a, j) km/s, km/s², km/s³.</summary>
    private static (Vector2 v, Vector2 a, Vector2 j) ThirdOrder(List<Vector3> hist) {
        const float h = 8f * 0.04f; // 采样间隔 8 帧 = 0.32s (25fps)
        Vector2 p0 = hist[0], p1 = hist[8], p2 = hist[16], p3 = hist[24];
        Vector2 v = (p3 * 11f - p2 * 18f + p1 * 9f - p0 * 2f) / (6f * h);
        Vector2 a = (p3 * 2f - p2 * 5f + p1 * 4f - p0) / (h * h);
        Vector2 j = (p3 - p2 * 3f + p1 * 3f - p0) / (h * h * h);
        return (v / 3.8164f, a / 3.8164f, j / 3.8164f); // 世界单位 → km
    }

    private void PruneHistories(HashSet<GameObject> alive) {
        var dead = new List<GameObject>();
        foreach (var k in _posHist.Keys) if (!alive.Contains(k)) dead.Add(k);
        foreach (var k in dead) _posHist.Remove(k);
        dead.Clear();
        foreach (var k in _fineHist.Keys) if (!alive.Contains(k)) dead.Add(k);
        foreach (var k in dead) _fineHist.Remove(k);
    }

    /// <summary>实体图标差集 (DC 自己的活): 在列表→画, 不在→删 (渲染线程执行 3D 挂件管理); 令牌虚拟目标不画.</summary>
    private void UpdateIcons() {
        foreach (var t in Targets) {
            if (t.Virtual) continue; // 令牌虚拟目标: 无实体图标 (内圈菱形框只属于真实目标)
            if (_icons.Add(t.Entity)) OnIconSpawn?.Invoke(t);
        }
        var remove = new List<GameObject>();
        foreach (var go in _icons) {
            bool alive = false;
            foreach (var t in Targets) if (t.Entity == go) { alive = true; break; }
            if (!alive) remove.Add(go);
        }
        foreach (var go in remove) {
            _icons.Remove(go);
            OnIconRemove?.Invoke(go);
        }
    }

    /// <summary>扫荡 (舰长席排布): 敌对目标按优先级 FDC > 炮兵 > 装甲 > 其他 发请求 (持续, 去重).</summary>
    private readonly HashSet<GameObject> _swept = new();
    private void Sweep() {
        foreach (var t in Targets) {
            if (t.Side != Side3.Enemy || _swept.Contains(t.Entity)) continue;
            _swept.Add(t.Entity);
            Requests.Add(new FireTask {
                Entity = t.Entity,
                Name = t.Name,
                Priority = PriorityOf(t),
                Shell = SelectedShell,
                Mode = ChargeModeSelection,
            });
        }
        OnSweepQueue?.Invoke(Targets);
    }

    private static int PriorityOf(DcTarget t) {
        switch (t.Kind) {
            case EntityKind.Fdc: return 4;
            case EntityKind.Artillery: return 3;
            case EntityKind.Armour: return 2;
            default: return 1;
        }
    }

    // ===== 目标输入 (用户操作 → 请求) =====
    /// <summary>当前选中弹种 (弹种按钮列, 右键时快照进请求).</summary>
    public BulletType SelectedShell = BulletType.AP;
    public ChargeMode ChargeModeSelection = ChargeMode.Normal;

    private readonly Dictionary<Transform, string> _tokens = new(); // 令牌 → 虚拟目标名称

    /// <summary>右键实体 toggle: 无任务 → 入队; 已有 → 升级齐射; 已齐射 → 取消.</summary>
    public void RightClickEntity(GameObject go) {
        if (go == null) return;
        var existing = FcPort?.FindEntityTask(go);
        if (existing != null) {
            // 上炮任务不许改计划 (只能取消, 1.x 口径); 队列中: 右键升级齐射, 再右键取消
            if (existing.SalvoPair || (FcPort != null && FcPort.IsOnGun(existing))) {
                FcPort.RequestCancel(existing);
                MelonLogger.Msg($"[DC] cancel task on {existing.Name}");
            }
            else {
                existing.SalvoPair = true;
                MelonLogger.Msg($"[DC] upgrade salvo on {existing.Name}");
            }
            return;
        }
        Requests.Add(new FireTask {
            Entity = go,
            Name = go.name,
            Priority = 1,
            Shell = SelectedShell,
            Mode = ChargeModeSelection,
        });
    }

    /// <summary>拖令牌上地图 → 注册虚拟目标 (带名称); 拖离地图 → 移除 (位置源失效, FC 撤任务).</summary>
    public void AddToken(Transform token, string name) => _tokens[token] = name;
    public void RemoveToken(Transform token) {
        _tokens.Remove(token);
        // 位置源失效 → FC 撤任务 (令牌被拿离地图)
        var task = FcPort?.FindEntityTask(token.gameObject);
        if (task != null) FcPort?.RequestCancel(task);
    }

    // ===== 令牌拖放检测 (MapToken_* 棋子) =====
    private readonly HashSet<Transform> _trackedTokens = new();
    private readonly Dictionary<Transform, bool> _tokenOnMap = new();
    private float _lastTokenScan;

    /// <summary>每帧: 新令牌发现 (1s 一批) + 上下地图边界判定.</summary>
    private void TrackTokens() {
        if (Time.time - _lastTokenScan > 1f) {
            _lastTokenScan = Time.time;
            foreach (var go in GameObject.FindObjectsOfType<GameObject>()) {
                // 标记令牌只注册 T1-10 (MapToken_Artillery 系列); 击杀/侦察/参考点令牌不注册虚拟目标
                if (go == null || !go.name.StartsWith("MapToken_Artillery") || go.name.Contains("Killed")) continue;
                if (_trackedTokens.Add(go.transform)) _tokenOnMap[go.transform] = IsOnMap(go.transform.position);
            }
        }
        foreach (var token in _trackedTokens) {
            if (token == null) continue;
            bool inside = IsOnMap(token.position);
            bool was = _tokenOnMap.TryGetValue(token, out var w) && w;
            _tokenOnMap[token] = inside;
            if (inside && !was) {
                AddToken(token, token.name); // 拖上地图: 注册虚拟目标 (名称 = 令牌名)
                MelonLogger.Msg($"[DC] token '{token.name}' placed on map");
            }
            else if (!inside && was) {
                RemoveToken(token);          // 拖离地图: 位置源失效 → FC 撤任务
                MelonLogger.Msg($"[DC] token '{token.name}' left map, task removed");
            }
        }
    }

    /// <summary>棋盘边界 (A-T × 1-10 网格, 留 0.2 余量): 令牌世界坐标 → 板面局部判定.</summary>
    private bool IsOnMap(Vector3 worldPos) {
        if (MapSurfaceRef == null) return false;
        var lp = MapSurfaceRef.InverseTransformPoint(worldPos);
        float x0 = GeoMap.MapBottomLeft.x - 0.2f, x1 = GeoMap.MapBottomLeft.x + 20f * GeoMap.MapCellSize + 0.2f;
        float y0 = GeoMap.MapBottomLeft.y - 0.2f, y1 = GeoMap.MapBottomLeft.y + 10f * GeoMap.MapCellSize + 0.2f;
        return lp.x >= x0 && lp.x <= x1 && lp.y >= y0 && lp.y <= y1;
    }

    /// <summary>地图上令牌右键 → 虚拟目标入队 (与实体右键同款 toggle 语义).</summary>
    public void RightClickToken(GameObject token) {
        if (token == null) return;
        var existing = FcPort?.FindEntityTask(token);
        if (existing != null) {
            if (!existing.SalvoPair) existing.SalvoPair = true;
            else FcPort?.RequestCancel(existing);
            return;
        }
        Requests.Add(new FireTask {
            Entity = token,
            Name = token.name,
            Priority = 1,
            Shell = SelectedShell,
            Mode = ChargeModeSelection,
        });
    }

    /// <summary>开关 (3D 按钮列/火控台按钮由场景交互层调用).</summary>
    public void SetAutoTask(bool on) { AutoTask = on; }
    public void SetTws(bool on) { Tws = on; if (!on) { _posHist.Clear(); _fineHist.Clear(); } }

    // ===== 渲染线程回调 (SandboxRenderer 挂接) =====
    public System.Action<DcTarget>? OnIconSpawn;    // 新实体 → 画图标
    public System.Action<GameObject>? OnIconRemove; // 阵亡/离图 → 删图标

    /// <summary>火控请求消费 (FC 每帧取走).</summary>
    public List<FireTask> DrainRequests() {
        var copy = new List<FireTask>(Requests);
        Requests.Clear();
        return copy;
    }
}

/// <summary>DC Target 列表元素 (实体 + 令牌虚拟目标混列).</summary>
public class DcTarget {
    public GameObject Entity = null!;
    public string Name = "";
    public Vector3 WorldPos;
    public Vector2 Velocity;      // TWS 开启时轨迹速度 (km/s); 关 = 零 (火控自然无预瞄)
    public Vector2 Accel;         // TWS 轨迹加速度 (km/s², 三次插值曲线; 一阶/无跟踪 = 0)
    public Vector2 Jerk;          // TWS 轨迹三次项 (km/s³, 弧线延伸用; 一阶/无跟踪 = 0)
    public Side3 Side;
    public EntityKind Kind;
    public int Armour;
    public bool Virtual;          // 令牌虚拟目标: 不画实体图标 (没有内圈菱形框)
}
