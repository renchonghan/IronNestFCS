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
    private readonly Dictionary<GameObject, List<(Vector3 pos, float t)>> _posHist = new(); // TWS: 75 帧环 (位置+时刻, 时间回归 — 采样率无关)
    private readonly Dictionary<GameObject, Vector2> _velEma = new();                     // TWS: 速度输出 EMA 状态 (压静态棋子帧间微抖噪声)
    private readonly Dictionary<GameObject, bool> _velMoving = new();                     // TWS: 静止死区迟滞状态
    private readonly HashSet<GameObject> _icons = new();                     // 已挂图标 (差集用)
    private object? _loopHandle;
    private float _lastTick;
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
        _velEma.Clear();
        _velMoving.Clear();
        _icons.Clear();
    }

    /// <summary>数据循环 25fps (硬节流: 不依赖 WaitForSeconds — 协程调度异常时照样锁频).</summary>
    private IEnumerator Loop() {
        while (!_disposed) {
            yield return null;
            if (Time.time - _lastTick < 0.04f) continue;
            _lastTick = Time.time;
            SyncNestToken();     // 铁巢棋子吸附实际炮位 (旧版同款: 棋子摆偏不导致火控打飞)
            if (RadarPort == null) continue;
            RefreshTargets();
            GroupTrains();       // 列车聚簇: 中心车出 TWS 文本, 其余成员隐藏 (定案后删探针)
            UpdateIcons();       // 实体图标差集 (在列表→画, 不在→删)
            TrackTokens();       // 令牌拖放: 上地图注册虚拟目标, 拖离自动取消
            PruneSwept();        // 去重集合轮询放回 (STP/RST 清队列后目标别再被永久挡住)
            if (AutoTask) Sweep();
        }
    }

    /// <summary>铁巢棋子吸附 (旧版 SyncIronNestToken 同款): 按炮塔实际网格位置放棋子, 紧急转移后跟得上.</summary>
    private ImpactMarkerManager? _impactManager;

    // 测试驱动已移除 (自建实体/令牌驱动均为临时测试设施)

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
    /// 所有目标同轨: 75 帧环一阶定速 (无精跟/粗跟之分).</summary>
    private void RefreshTargets() {
        if (RadarPort == null) return;
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
        foreach (var k in _targetMap.Keys) if (k == null || !alive.Contains(k)) dead.Add(k!);
        foreach (var k in dead) _targetMap.Remove(k);
        PruneHistories(alive);
        Targets.Clear();
        Targets.AddRange(_targetMap.Values); // 渲染差集/扫荡用列表 (引用复用)
    }

    /// <summary>单目标参数更新/建表: TWS 轨迹参数 (定速模型, 75 帧线性滤波).</summary>
    private void UpsertTarget(GameObject go, string name, Vector3 pos, Side3 side, EntityKind kind, int armour, bool isVirtual) {
        Vector2 vel = Vector2.zero, acc = Vector2.zero, jerk = Vector2.zero;
        // 令牌虚拟目标不参与 TWS: 恒定按固定目标算 (拖放棋子无真实运动语义, 速度恒 0 → 火控自然无预瞄)
        if (Tws && !isVirtual) (vel, acc, jerk) = TrackMotion(go, pos);
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
    /// <summary>TWS 运动估计 (定速模型): 75 帧环一阶最小二乘 (线性滤波) → v; a/j 不估 (恒 0).
    /// 窗口 75 帧 (3s): 匀速直线拟合精确, 窗口愈长噪声愈低 (LS 斜率噪声 ∝ 1/√N) — 预瞄点 = 目标 + v×T, v 的帧间波动被飞时放大, 长窗口直接压预瞄抖动;
    /// 变速目标天然滞后 1.5s — 定速模型的既定取舍. FC 走 v-only 闭式解, 渲染退化直线.</summary>
    private (Vector2 v, Vector2 a, Vector2 j) TrackMotion(GameObject go, Vector3 pos) {
        // 差分在世界系做: 板面局部系随 surface 拖动/缩放而变, 静态目标会被误判为移动 (拖地图后一片目标"动"起来);
        // 世界系固定, 差分才反映目标真实运动. 回归出的世界速度经 InverseTransformDirection 转回板面口径 (下游语义不变)
        if (!_posHist.TryGetValue(go, out var hist)) _posHist[go] = hist = new List<(Vector3, float)>();
        hist.Add((pos, Time.time));
        if (hist.Count > 75) hist.RemoveAt(0);
        if (hist.Count < 5) return (Vector2.zero, Vector2.zero, Vector2.zero); // 不足 5 帧: 无跟踪
        Vector3 slope = Vector3.zero;
        Vector3 psum = Vector3.zero, pbar;
        float tsum = 0f, tbar, den = 0f;
        int n = hist.Count;
        // 时间回归 (采样率无关, 双中心化): 斜率 = Σ(p−p̄)(t−t̄) / Σ(t−t̄)² —
        // 硬编码 25fps 换算在游戏帧率 ≠25 时系统性高估速度 (事故 #10);
        // 不中心化则 p 大数 (~20) × Σ(t−t̄) 的 float 舍入残差 (t~300s 时 t̄ 舍入 ~2e-5 → 残差 ~3e-3) 造出假斜率
        // → 位置纹丝不动也输出 0.5~1.6 m/s "速度" (事故: 静态目标矢量符抖); 双中心化后位置全同 ⇒ 斜率精确 0
        for (int i = 0; i < n; i++) { psum += hist[i].pos; tsum += hist[i].t; }
        pbar = psum / n;
        tbar = tsum / n;
        for (int i = 0; i < n; i++) {
            float dt = hist[i].t - tbar;
            den += dt * dt;
            slope += (hist[i].pos - pbar) * dt;
        }
        slope /= den;
        Vector2 vLocal = MapSurfaceRef != null ? (Vector2)MapSurfaceRef.InverseTransformDirection(slope) : (Vector2)slope;
        Vector2 vOut = vLocal * GeoMap.KmPerLocal; // 板面/s → km/s
        // EMA 低通 (α=0.25, τ≈0.16s): 压 LS 帧间噪声; 真实恒速移动 (DC 信号) 不受影响
        if (_velEma.TryGetValue(go, out var prev)) vOut = prev + (vOut - prev) * 0.25f;
        _velEma[go] = vOut;
        // 静止判据 = 窗口净位移 (位置域): 假斜率/摆动/噪声的 net=0, 真移动 net = v×Δt (3.7s 窗口, 0.5 m/s → 1.85m)
        // 阈值 1m (0.0002 world): 位移域判据对 slope 假象免疫 — 之前速度域死区压不干净 (位置全同仍出 0.5~1.6 m/s)
        Vector3 drift = hist[n - 1].pos - hist[0].pos;
        if (drift.magnitude < 0.0002f) { vOut = Vector2.zero; _velMoving[go] = false; }
        else _velMoving[go] = true;
        return (vOut, Vector2.zero, Vector2.zero);
    }

    private void PruneHistories(HashSet<GameObject> alive) {
        var dead = new List<GameObject>();
        foreach (var k in _posHist.Keys) if (!alive.Contains(k)) dead.Add(k);
        foreach (var k in dead) _posHist.Remove(k);
        foreach (var k in dead) _velEma.Remove(k);
        foreach (var k in dead) _velMoving.Remove(k);
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

    /// <summary>扫荡 (舰长席排布): 智能选弹 (AutoTask 不受 SelectedShell 影响) + 集群覆盖比价 + 收益密度排序.
    /// 弹种规则 (EQKE/核弹/特种排除): 装甲 AP (10) 点杀, 成对 ≤APHE 半径 0.25km 一发 APHE (15) 覆盖 (同穿甲能力);
    /// 无甲 DRIL (3) 点杀, 扎堆按 LE (8, r0.15) / HE (10, r0.25) / HCHE (18, r0.55) 覆盖比价 (药价 0.25/包计入).
    /// 集群 = 一发覆盖弹锁种子头 (杀伤半径内必死), 溅射成员跳过不再打; 覆盖半径内有友军 → 禁用该覆盖弹 (别学美国人).
    /// 排序 = 收益密度 (装甲 20 / 无甲 5) / 总成本 降序; 成本相等覆盖胜 (轮次少优先).</summary>
    private readonly HashSet<GameObject> _swept = new();
    private const float PowderCost = 0.25f; // 征用/包 (20 包 = 5 征用)

    private class SweepPlan {
        public DcTarget Seed = null!;
        public BulletType Shell;
        public float Cost;  // 弹+药 (征用)
        public float Value; // 集群总价值
    }

    private void Sweep() {
        var enemies = new List<DcTarget>();
        foreach (var t in Targets) if (t.Side == Side3.Enemy && !t.Virtual && !_swept.Contains(t.Entity)) enemies.Add(t);
        if (enemies.Count == 0) return;
        var friends = new List<Vector2>();
        foreach (var t in Targets) if (t.Side == Side3.Friendly && !t.Virtual) friends.Add(Board(t));
        var consumed = new HashSet<GameObject>();
        var plans = new List<SweepPlan>();

        // 装甲: 配对 (间距 ≤ APHE 半径) → APHE 一发覆盖; 单点 → AP
        var armours = enemies.FindAll(e => e.Armour > 0);
        foreach (var a in armours) {
            if (consumed.Contains(a.Entity)) continue;
            float rAphe = ShellData.KillRadiusKm(BulletType.APHE) / GeoMap.KmPerLocal;
            var pair = armours.Find(b => b != a && !consumed.Contains(b.Entity) && (Board(b) - Board(a)).magnitude <= rAphe);
            consumed.Add(a.Entity);
            if (pair != null) {
                consumed.Add(pair.Entity);
                plans.Add(new SweepPlan { Seed = a, Shell = BulletType.APHE, Cost = 15f + Powder(a), Value = 40f });
            }
            else plans.Add(new SweepPlan { Seed = a, Shell = BulletType.AP, Cost = 10f + Powder(a), Value = 20f });
        }

        // 无甲: 贪心覆盖比价 — DRIL 逐点 vs LE/HE/HCHE 覆盖 (相等成本覆盖胜 = 轮次少)
        var softs = enemies.FindAll(e => e.Armour <= 0);
        foreach (var s in softs) {
            if (consumed.Contains(s.Entity)) continue;
            float p = Powder(s);
            BulletType best = BulletType.DRIL;
            float bestCost = 3f + p;
            var members = new List<DcTarget>();
            foreach (var shell in new[] { BulletType.HCHE, BulletType.HE, BulletType.LE }) {
                float r = ShellData.KillRadiusKm(shell) / GeoMap.KmPerLocal;
                bool friendInside = false;
                foreach (var f in friends) if ((f - Board(s)).magnitude <= r) { friendInside = true; break; }
                if (friendInside) continue; // 覆盖圈里有友军: 禁用该覆盖弹
                var inR = new List<DcTarget>();
                foreach (var o in softs) if (o != s && !consumed.Contains(o.Entity) && (Board(o) - Board(s)).magnitude <= r) inR.Add(o);
                if (inR.Count == 0) continue;
                float coverCost = ShellCost(shell) + p;
                float pointCost = (inR.Count + 1) * (3f + p); // 逐点 DRIL 成本 (含成员)
                if (coverCost <= pointCost && coverCost < bestCost) { best = shell; bestCost = coverCost; members = inR; }
            }
            consumed.Add(s.Entity);
            foreach (var m in members) consumed.Add(m.Entity); // 溅射成员跳过 (杀伤半径内必死)
            plans.Add(new SweepPlan { Seed = s, Shell = best, Cost = bestCost, Value = 5f * (members.Count + 1) });
        }

        // 收益密度降序 → 发请求 (队列顺序 = 派发顺序)
        plans.Sort((x, y) => (y.Value / y.Cost).CompareTo(x.Value / x.Cost));
        foreach (var plan in plans) {
            _swept.Add(plan.Seed.Entity);
            Requests.Add(new FireTask {
                Entity = plan.Seed.Entity,
                Name = plan.Seed.Name,
                Priority = PriorityOf(plan.Seed),
                Shell = plan.Shell,
                Mode = ChargeModeSelection,
            });
        }
        OnSweepQueue?.Invoke(Targets);
    }

    /// <summary>扫荡去重集合轮询放回 (3s 一次): 队列空 (STP/RST 清过 或 本批打完) → 全部放回重新评估 —
    /// 不然目标被 _swept 永久挡住, 再开 AutoTask 死活不入队; 溅射漏网 (成员没死) 也靠这补刀.
    /// 队列活跃 (任务在队/在炮) → 只清死实体.</summary>
    private float _lastSweptPrune;
    private void PruneSwept() {
        if (Time.time - _lastSweptPrune < 3f) return;
        _lastSweptPrune = Time.time;
        bool anyTask = FcPort != null && (FcPort.QueueCount > 0 || FcPort.LeftTask != null || FcPort.RightTask != null);
        if (anyTask) {
            _swept.RemoveWhere(e => e == null); // 阵亡出表: 清死引用
        }
        else {
            _swept.Clear(); // 队列空: 计划作废 (STP/RST) 或本批打完 — 全放回
        }
    }

    /// <summary>目标板面坐标 (集群距离判定, 与渲染/杀伤圈同口径).</summary>
    private Vector2 Board(DcTarget t) =>
        MapSurfaceRef != null ? (Vector2)MapSurfaceRef.InverseTransformPoint(t.WorldPos) : (Vector2)t.WorldPos;

    /// <summary>本发药价 (征用): 0.25/包 × 最小装药包数 (按距铁巢距离查表).</summary>
    private float Powder(DcTarget t) {
        if (NestRef == null || MapSurfaceRef == null) return PowderCost;
        var nb = (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position);
        float distKm = (Board(t) - nb).magnitude * GeoMap.KmPerLocal;
        return PowderCost * BallisticCalculator.MinimumCharge(distKm);
    }

    /// <summary>弹体价 (征用): 自动使用范围; 特种/排除弹返回极大值 (不可用).</summary>
    private static float ShellCost(BulletType bt) => bt switch {
        BulletType.DRIL => 3f,
        BulletType.LE => 8f,
        BulletType.HE => 10f,
        BulletType.HCHE => 18f,
        BulletType.AP => 10f,
        BulletType.APHE => 15f,
        _ => float.MaxValue,
    };

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
                FcPort!.RequestCancel(existing);
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

    /// <summary>列车聚簇 (每 tick, 按命名): 同名前缀成员按近距 (0.35 板面) + 同标量速度并查集成链,
    /// ≥3 车 = 列车; 代表 = 链序 (n+1)/2 号车 (4→2, 5→3, 6→3), 只它出 TWS 文本;
    /// 代表文本横向避让: 右侧重叠车框需要多少右移就挪多少 (TrainShift).
    /// (纯距离聚簇在 1.2 小格阈值下出一堆错误簇 — 弃用; 待有仓库/实体类型信息再改.)</summary>
    private void GroupTrains() {
        foreach (var t in _targetMap.Values) { t.TrainMember = false; t.TrainData = false; t.TrainAnchor = false; t.TrainDataOf = null; t.TrainShift = 0f; }
        if (MapSurfaceRef == null) return;
        var groups = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<DcTarget>>();
        foreach (var t in _targetMap.Values) {
            if (t.Entity == null) continue;
            // 前缀 = 剥尾部序号/分隔符 ('#'/下划线/连字符/括号) 再剥一个尾字母 — 车厢命名 cara/carb/carc/... 各带序号尾, 归同一 'car' 组
            string prefix = System.Text.RegularExpressions.Regex.Replace(t.Name, @"[#_()\-\s]*\d+[#_()\-\s]*$", "");
            if (prefix.Length >= 3 && prefix[^1] >= 'a' && prefix[^1] <= 'z') prefix = prefix[..^1];
            if (prefix.Length == 0) prefix = t.Name;
            if (!groups.TryGetValue(prefix, out var list)) groups[prefix] = list = new System.Collections.Generic.List<DcTarget>();
            list.Add(t);
        }
        foreach (var kv in groups) {
            var list = kv.Value;
            if (list.Count < 3) continue;
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            var parent = new int[list.Count];
            for (int i = 0; i < list.Count; i++) parent[i] = i;
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++) {
                    if (!NearCar(list[i], list[j])) continue;
                    int ri = Find(parent, i), rj = Find(parent, j);
                    if (ri != rj) parent[ri] = rj;
                }
            var chains = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<DcTarget>>();
            for (int i = 0; i < list.Count; i++) {
                int r = Find(parent, i);
                if (!chains.TryGetValue(r, out var chain)) chains[r] = chain = new System.Collections.Generic.List<DcTarget>();
                chain.Add(list[i]);
            }
            foreach (var chain in chains.Values) if (chain.Count >= 3) MarkChain(chain);
        }
    }

    private int Find(int[] parent, int i) {
        while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
        return i;
    }

    /// <summary>两车是否相邻 (板面距离 < 0.35 且标量速度差 < 5 m/s — 移动速度是标量不是矢量).</summary>
    private bool NearCar(DcTarget a, DcTarget b) {
        var pa = (Vector2)MapSurfaceRef!.InverseTransformPoint(a.Entity.transform.position);
        var pb = (Vector2)MapSurfaceRef.InverseTransformPoint(b.Entity.transform.position);
        if (Vector2.Distance(pa, pb) >= 0.35f) return false;
        float va = a.Velocity.magnitude * 1000f, vb = b.Velocity.magnitude * 1000f; // m/s 标量
        return Mathf.Abs(va - vb) < 5f;
    }

    /// <summary>链定案: 数据源 = (n+1)/2 号中心车, 文本锚点 = 最右边车厢 (max 板面 x — 与行车方向无关,
    /// 从左往右/从右往左都挂右端, 文本永远伸向空处不压别的车厢); 横向避让量 = 从锚点算, 右侧重叠车框所需右移的最大值.</summary>
    private void MarkChain(System.Collections.Generic.List<DcTarget> chain) {
        chain.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var midT = chain[(chain.Count + 1) / 2 - 1]; // 4→#2, 5→#3, 6→#3 (数据源)
        var tailT = chain[0];                        // 锚点 = 最右边车厢 (max x)
        var tbx = (Vector2)MapSurfaceRef!.InverseTransformPoint(tailT.Entity.transform.position);
        foreach (var t in chain) {
            var b = (Vector2)MapSurfaceRef.InverseTransformPoint(t.Entity.transform.position);
            if (b.x > tbx.x) { tailT = t; tbx = b; }
        }
        const float frameHalf = 0.03f, textHalf = 0.018f;
        float baseA = 0.15f * GeoMap.MapCellSize; // 文本锚距 = 矢量符同距 (0.15 小格)
        var tb = (Vector2)MapSurfaceRef!.InverseTransformPoint(tailT.Entity.transform.position);
        float shift = 0f;
        foreach (var t in chain) {
            if (t == tailT) continue;
            var b = (Vector2)MapSurfaceRef.InverseTransformPoint(t.Entity.transform.position);
            float dx = b.x - tb.x, dy = b.y - tb.y;
            if (dx <= 0f || Mathf.Abs(dy) > 0.05f) continue; // 只避车尾右侧且纵向重叠的框
            float need = dx + frameHalf + textHalf - baseA;
            if (need > shift) shift = need;
        }
        foreach (var t in chain) t.TrainMember = true;
        midT.TrainData = true;
        tailT.TrainAnchor = true;
        tailT.TrainDataOf = midT;
        tailT.TrainShift = Mathf.Max(0f, shift);
    }

    /// <summary>棋盘边界 (A-T × 1-10 网格, 留 0.2 余量): 令牌世界坐标 → 板面局部判定 (统一 GeoMap.IsOnBoard).</summary>
    private bool IsOnMap(Vector3 worldPos) {
        if (MapSurfaceRef == null) return false;
        return GeoMap.IsOnBoard(MapSurfaceRef.InverseTransformPoint(worldPos), 0.2f);
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
    public Vector2 Velocity;      // TWS 开启时轨迹速度 (km/s); 关 = 零 (火控自然无预瞄)
    public Vector2 Accel;         // TWS 轨迹加速度 (km/s², 三次插值曲线; 一阶/无跟踪 = 0)
    public Vector2 Jerk;          // TWS 轨迹三次项 (km/s³, 弧线延伸用; 一阶/无跟踪 = 0)
    public Side3 Side;
    public EntityKind Kind;
    public int Armour;
    public bool Virtual;          // 令牌虚拟目标: 不画实体图标 (没有内圈菱形框)
    public bool TrainMember;      // 列车成员 (同名前缀 ≥3 车 + 近距链 + 同标量速度)
    public bool TrainData;        // 列车数据源 (链序 (n+1)/2 中心车 — 文本显示它的距离/速度)
    public bool TrainAnchor;      // 列车文本锚点 (名字序最后一节车尾 — 文本挂它右侧, 只它出字)
    public DcTarget? TrainDataOf; // 锚点引用数据源 (中心车; 非锚点恒 null)
    public float TrainShift;      // 锚点文本横向避让量 (板面单位, 避开右侧车框)
}
