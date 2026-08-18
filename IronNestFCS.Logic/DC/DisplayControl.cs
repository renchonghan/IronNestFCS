using System.Collections;
using System.Collections.Generic;
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
    private readonly Dictionary<GameObject, List<Vector3>> _posHist = new(); // 5 帧位置历史 (TWS)
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
        _posHist.Clear();
        _icons.Clear();
    }

    /// <summary>数据循环 25fps.</summary>
    private IEnumerator Loop() {
        while (!_disposed) {
            yield return new WaitForSeconds(0.04f);
            if (RadarPort == null) continue;
            RefreshTargets();
            UpdateIcons();       // 实体图标差集 (在列表→画, 不在→删)
            TrackTokens();       // 令牌拖放: 上地图注册虚拟目标, 拖离自动取消
            if (AutoTask) Sweep();
        }
    }

    /// <summary>SRC → Target 列表: 相对位置 (方位/距离) + TWS 5 帧平均速度.</summary>
    private void RefreshTargets() {
        Targets.Clear();
        var seen = new HashSet<GameObject>();
        foreach (var c in RadarPort.Contacts) {
            if (c == null || c.Entity == null) continue;
            seen.Add(c.Entity);
            Vector2 vel = Vector2.zero;
            if (Tws) vel = TrackVelocity(c.Entity, c.WorldPos);
            Targets.Add(new DcTarget {
                Entity = c.Entity,
                Name = c.Entity.name,
                WorldPos = c.WorldPos,
                Velocity = vel,
                Side = c.Side,
                Kind = c.Kind,
                Armour = c.Armour,
            });
        }
        // 令牌虚拟目标 (位置源由 DC 管理): 混在同一列表, 句柄为令牌
        foreach (var tok in _tokens) {
            if (tok.Key == null || tok.Key.gameObject == null) continue;
            var pos = tok.Key.position;
            seen.Add(tok.Key.gameObject);
            Targets.Add(new DcTarget {
                Entity = tok.Key.gameObject,
                Name = tok.Value,
                WorldPos = pos,
                Velocity = Tws ? TrackVelocity(tok.Key.gameObject, pos) : Vector2.zero,
                Side = Side3.Enemy,
                Kind = EntityKind.Other,
            });
        }
        PruneHistories(seen);
    }

    /// <summary>TWS: 5 帧 (0.2s) 最小二乘线性拟合斜率 → 速度矢量 (km/s).
    /// 均匀采样 t=0..4, 分母 Σ(t-t̄)²=10; 保留时间序列, 以后升二阶导 (加速度) 时同样按最小二乘扩到二次拟合.</summary>
    private Vector2 TrackVelocity(GameObject go, Vector3 pos) {
        if (!_posHist.TryGetValue(go, out var hist)) _posHist[go] = hist = new List<Vector3>();
        hist.Add(pos);
        if (hist.Count > 5) hist.RemoveAt(0);
        if (hist.Count < 5) return Vector2.zero;
        Vector2 slope = Vector2.zero;
        for (int i = 0; i < hist.Count; i++) slope += (Vector2)hist[i] * (i - 2);
        slope /= 10f;
        return slope * 25f / 3.8164f; // 帧斜率 × 25fps → km/s
    }

    private void PruneHistories(HashSet<GameObject> alive) {
        var dead = new List<GameObject>();
        foreach (var k in _posHist.Keys) if (!alive.Contains(k)) dead.Add(k);
        foreach (var k in dead) _posHist.Remove(k);
    }

    /// <summary>实体图标差集 (DC 自己的活): 在列表→画, 不在→删 (渲染线程执行 3D 挂件管理).</summary>
    private void UpdateIcons() {
        foreach (var t in Targets) {
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
                PositionSource = () => LivePos(t.Entity),
                VelocitySource = () => Tws ? t.Velocity : Vector2.zero,
                Priority = PriorityOf(t),
                Shell = SelectedShell,
                Mode = ChargeModeSelection,
            });
        }
        OnSweepQueue?.Invoke(Targets);
    }

    private static Vector3? LivePos(GameObject go) => go == null ? null : go.transform.position;

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
            if (!existing.SalvoPair) {
                existing.SalvoPair = true; // 升级齐射 (FC 派发时两炮同任务 + SyncCommand)
                MelonLogger.Msg($"[DC] upgrade salvo on {existing.Name}");
            }
            else FcPort.RequestCancel(existing);
            return;
        }
        Requests.Add(new FireTask {
            Entity = go,
            Name = go.name,
            PositionSource = () => LivePos(go),
            VelocitySource = () => {
                if (!Tws) return Vector2.zero;
                var t = Targets.Find(x => x.Entity == go);
                return t?.Velocity ?? Vector2.zero;
            },
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
                if (go == null || !go.name.StartsWith("MapToken")) continue;
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
            PositionSource = () => LivePos(token),
            VelocitySource = () => {
                if (!Tws) return Vector2.zero;
                var t = Targets.Find(x => x.Entity == token);
                return t?.Velocity ?? Vector2.zero;
            },
            Priority = 1,
            Shell = SelectedShell,
            Mode = ChargeModeSelection,
        });
    }

    /// <summary>开关 (3D 按钮列/火控台按钮由场景交互层调用).</summary>
    public void SetAutoTask(bool on) { AutoTask = on; }
    public void SetTws(bool on) { Tws = on; if (!on) _posHist.Clear(); }

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
    public Vector2 Velocity;      // TWS 开启时 5 帧平均 (km/s); 关 = 零 (火控自然无预瞄)
    public Side3 Side;
    public EntityKind Kind;
    public int Armour;
}
