using System.Collections;
using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// [DC] SandboxRenderer — 2.0 渲染线程 (独立循环, 每帧, FcsModule 已接线).
/// 持续读各数据源画沙盘: GC/FC push 的实时弹道 (绿十字+瞄准圈) / FC 的打击队列信息 (红杀伤圈+编号+预瞄线) /
/// 炮弹落点指示器 (红线: 虚线全长固定 + 实线未飞段渐短 + 实心红点弹头 + 红落点圈) / 实体图标 (差集回调).
/// 分层 (层级偏移见下: 绿 0 / 落点+图标 0.001 / 红圈+编号 0.002; 渲染次序以 renderQueue 为主, z 为同队列内次级排序).
/// 3D 物件生命周期: 实体死亡/任务取消/令牌离图 → 清退; F9 → 全清.
/// </summary>
public class SandboxRenderer {
    public Transform? MapSurfaceRef; // "Draggable Surface" (板面局部系, 板面层挂件母体)
    public Transform? FireMissionRootRef; // "Fire Mission Root" (实体挂件母体 — 旧版菱形框同空间, 实体位置在此空间才是板面坐标)
    public Transform? NestRef;       // 铁巢/炮塔 (预瞄线/落点线起点)
    /// <summary>TWS 开关活读 (FcsModule 注入): 关 → 速度矢量符整体不显示.</summary>
    public System.Func<bool>? TwsActive;
    /// <summary>核弹开火事件 (FcsModule 注入 NukeAlarm): GC 击发确认 (手动/自动通用) → 响 Launch.</summary>
    public System.Action? OnNukeFired;
    /// <summary>核弹装填确认 (FcsModule 活读注入: CANFIRE 且膛内 ATMC — 旋转开始点; 手动装填同样覆盖).</summary>
    public bool NukeArmed;

    // ===== 3D 指示器状态 =====
    private readonly Dictionary<GameObject, QueueIndicator> _queueMarks = new(UnityRefComparer.Instance); // 引用相等键: 销毁对象 instanceID 归零, 默认哈希下两个不同销毁对象会被判等 (清退误判)
    private readonly HashSet<GameObject> _seenBuffer = new(UnityRefComparer.Instance); // 队列指示器帧内去重缓冲 (每帧 Clear 复用, 免每帧分配)
    private readonly List<GameObject> _goneBuffer = new();                              // 清退候选缓冲 (同上)
    private readonly Dictionary<LeftRight, ImpactIndicator> _impacts = new(); // 恒定实体: 每炮一套 (虚线/实线/点), 不用就隐藏
    private readonly Dictionary<LeftRight, TrackIndicator> _tracks = new();     // 火控目标线 (开火前解算, 绿点线) 每炮一条
    private readonly Dictionary<LeftRight, TrackIndicator> _finals = new();     // 最终轨迹线 (击发冻结缩短, 细实线) 每炮一条
    private const float TrackDotDia = 0.006f; // 轨迹点直径 (= 线宽 0.006); 点距 = 均分 len/(n-1) (最小 0.0075, 无上限, 最多 32 点)
    private readonly Dictionary<GameObject, IconEntry> _icons = new();
    private readonly Dictionary<LeftRight, BallisticMark> _ballistic = new();

    private object? _loopHandle;
    private bool _disposed;
    private float _nukeT0 = -1f;         // 核弹挂架时刻 (转速 S 型爬升起点; 无核弹在炮/在飞时 -1)
    private float _nukeInactiveAt = -1f; // 失活时刻 (复位保持窗起点 — 从失活瞬间起算, 不是从 t0)

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
    private const float NukeSpinZeta = 1.2f; // 核弹转速爬升过阻尼参数 (S 型: ~2s 到 84%, ~3s 到 95%)
    private const float NukeSpinWn = 2.0f;

    /// <summary>过阻尼阶跃 (0→1, S 型爬升 — 与风螺转速曲线同款).</summary>
    private static float NukeRamp(float t) {
        if (t <= 0f) return 0f;
        float z = NukeSpinZeta, w = NukeSpinWn;
        float s1 = -w * (z - Mathf.Sqrt(z * z - 1f));
        float s2 = -w * (z + Mathf.Sqrt(z * z - 1f));
        return 1f + (s1 * Mathf.Exp(s2 * t) - s2 * Mathf.Exp(s1 * t)) / (s2 - s1);
    }

    /// <summary>过阻尼阶跃积分 (相位 = 目标转速 × 此积分 — 转速缓慢爬升, 相位连续无跳变).</summary>
    private static float NukeRampInt(float t) {
        if (t <= 0f) return 0f;
        float z = NukeSpinZeta, w = NukeSpinWn;
        float s1 = -w * (z - Mathf.Sqrt(z * z - 1f));
        float s2 = -w * (z + Mathf.Sqrt(z * z - 1f));
        float d = s2 - s1;
        return t + (s1 / s2 * Mathf.Exp(s2 * t) - s2 / s1 * Mathf.Exp(s1 * t)) / d - (s1 / s2 - s2 / s1) / d;
    }

    /// <summary>核弹旋转全局相位 (rad): 转速按过阻尼 S 型爬升至 60°/s — 瞄准圈/队列圈/落弹环共用同一时钟, 开火瞬间相位连续无割裂.</summary>
    private float NukePhase => _nukeT0 >= 0f ? Mathf.PI / 3f * NukeRampInt(Time.time - _nukeT0) : 0f;
    // 速度矢量符尺寸 (板面单位, 用户定稿): 空心圆半径 0.005, 静止点半径 = 其一半 (面积 1/4)
    private const float SpeedR = 0.005f;     // 矢量符圆半径 (线长基准 r: 0.5r/1r/1.5r 对数档)
    private const float SpeedDotR = 0.0025f; // 静止点半径 (空心圆半径一半)

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
            UpdateQueueBreath(); // 队列杀伤圈呼吸 (0.5Hz 透明度脉动)
            UpdateNukeClock();   // 核弹挂架时钟 (转速爬升起点)
        }
    }

    /// <summary>核弹挂架时钟: 有 ATMC 在炮 (瞄准圈) 或在飞 (落弹环) 记 t0, 全无复位 — 下次挂架重新 S 型爬升.</summary>
    private void UpdateNukeClock() {
        bool active = NukeArmed; // 装填确认 (CANFIRE + 膛内核弹) = 旋转开始点; 手动装填同覆盖
        if (!active) {
            foreach (var kv in _impacts) {
                var im = kv.Value;
                bool expanding = im.Landed && im.NukeLandedAt >= 0f && Time.time - im.NukeLandedAt < 2f; // 落地扩散动画 (2s) 期间相位不断
                if (im.Root != null && im.Root.activeSelf && im.Shell == BulletType.ATMC && (!im.Landed || expanding)) { active = true; break; }
            }
        }
        // 复位加 1.5s 保持 (从失活瞬间起算): 开火后 armed 掉、落弹指示器 ~1s 才接上 — 空隙内相位不断, 开火交接无停顿
        if (active) {
            _nukeInactiveAt = -1f;
            if (_nukeT0 < 0f) _nukeT0 = Time.time;
        }
        else if (_nukeT0 >= 0f) {
            if (_nukeInactiveAt < 0f) _nukeInactiveAt = Time.time;
            else if (Time.time - _nukeInactiveAt > 1.5f) _nukeT0 = -1f;
        }
    }

    /// <summary>核弹队列辐射标呼吸 (周期 3s: 1s 升 → 0.5s 保持 1 → 1s 降 → 0.5s 保持 0): 只有 ATMC 的辐射标闪, 最外圈杀伤圈保持常显.
    /// 弹种切换时 RebuildCircle 重画线 (alpha 回 1), 无需显式复位.</summary>
    private void UpdateQueueBreath() {
        float p = Time.time % 3f;
        float alpha;
        if (p < 1f) alpha = p;              // 1s 升 (0→1)
        else if (p < 1.5f) alpha = 1f;      // 0.5s 保持 1
        else if (p < 2.5f) alpha = 2.5f - p; // 1s 降 (1→0)
        else alpha = 0f;                    // 0.5s 保持 0
        foreach (var kv in _queueMarks) {
            var mark = kv.Value;
            if (mark.Root == null || mark.Shell != BulletType.ATMC) continue;
            BreathRoot(mark, alpha);
        }
    }

    private static void BreathRoot(QueueIndicator mark, float alpha) {
        var root = mark.RadiationRoot;
        if (root == null || !root.activeSelf) return;
        if (mark.RadiationLines == null) // 重画/清空时置空, 惰性重建 — 避免每帧 GetComponentsInChildren 分配
            mark.RadiationLines = new List<Il2CppShapes.Line>(root.transform.GetComponentsInChildren<Il2CppShapes.Line>());
        foreach (var line in mark.RadiationLines) line.Color = new Color(1f, 0f, 0f, alpha);
    }

    /// <summary>实体图标位置跟随 (板面父级, 每帧由实体世界坐标反算; 死亡实体由 DC 差集回调清退) + 速度矢量符活读.</summary>
    private void FollowIcons() {
        if (MapSurfaceRef == null) return;
        foreach (var pair in _icons) {
            if (pair.Key == null || pair.Value == null) continue;
            var b = (Vector2)MapSurfaceRef.InverseTransformPoint(pair.Key.transform.position);
            pair.Value.Root.transform.localPosition = new Vector3(b.x, b.y, RedOffset);
            UpdateSpeedVector(pair.Value);
            UpdateTwsInfo(pair.Value);
        }
    }

    /// <summary>TWS 信息文本 (菱形框右侧, 两排中心与矢量符同距): 上排距离 xx.xxkm / 下排速度 m/s
    /// (≥100 整数, 10~100 一位小数, <10 两位小数 — 无 xxx.x 形态; 空格对齐不补 0); 静止单行只有距离;
    /// TWS 关/参考点不显示. 两行总高 ≈ 菱形框高 (行高 0.025 × 2 ≈ 菱形 0.05).</summary>
    private void UpdateTwsInfo(IconEntry e) {
        if (e.TextRoot == null) return;
        // 列车成员: 只让车尾锚点出文本, 其余成员 (含中心数据源) 的框留白
        if (e.Target.TrainMember && !e.Target.TrainAnchor) { e.TextRoot.SetActive(false); return; }
        if (TwsActive == null || !TwsActive() || e.Target.Kind == EntityKind.Reference || MapSurfaceRef == null || NestRef == null) {
            e.TextRoot.SetActive(false);
            return;
        }
        // 锚点文本横向避让 (避开右侧重叠车框 — GroupTrains 算的 TrainShift)
        e.TextRoot.transform.localPosition = new Vector3(0.15f * GeoMap.MapCellSize + e.Target.TrainShift, 0f, 0f);
        var data = e.Target.TrainDataOf ?? e.Target; // 列车: 数据 = 中心车 (距离/速度都取它)
        var tb = (Vector2)MapSurfaceRef.InverseTransformPoint(data.Entity.transform.position);
        var nb = (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position);
        float km = Vector2.Distance(tb, nb) * GeoMap.KmPerLocal; // 板面 → km (一律经 KmPerLocal)
        float ms = MsPerKm(data.Velocity);                        // km/s → m/s
        string dist = FmtDist(km);
        string spd = ms >= 1f ? FmtSpd(ms) : "";                  // <1 m/s 静止: 单行
        if (dist == e.DistText && spd == e.SpdText) return;       // 没变不重画
        e.DistText = dist;
        e.SpdText = spd;
        const float segW = 0.006f;  // 字号 (字高 H = 1.6×segW; 原 2/3 再 ×3/4 — 用户定稿)
        float step = segW * 0.7f;   // 半宽字形 (宽 segW/2) 的字符间距
        // 右对齐 + 块框居中: 对齐点按最长文本宽 (速度 " 12.3m/s" 8 字符; 距离最长 7) — 短行左边补空右缘整齐;
        // 块框 (MaxLen 宽) 中心保持在锚点 (最长行中心 = 原居中位置), 不因右对齐整体偏右
        const int MaxLen = 8;
        float rowShift = (MaxLen - 1) * step / 2f; // 框半宽: 整体左移让框中心回锚点
        // 行布局 (字形格从基线 y=0 向上长 H): 两行 = 上排基线 +0.25H / 下排基线 −1.25H (行间留 0.5H 空档, 块心在锚点);
        // 单行 = 基线 −0.5H (垂直居中)
        ClearChildren(e.Line1Root.transform);
        for (int i = 0; i < dist.Length; i++)
            Glyph16Font.DrawCharSegmentsNarrow(e.Line1Root.transform, dist[i], e.Color, i * step + (MaxLen - dist.Length) * step - rowShift, segW);
        e.Line1Root.transform.localPosition = new Vector3(0f, spd.Length > 0 ? 0.4f * segW : -0.8f * segW, 0f);
        ClearChildren(e.Line2Root.transform);
        if (spd.Length > 0) {
            for (int i = 0; i < spd.Length; i++)
                Glyph16Font.DrawCharSegmentsNarrow(e.Line2Root.transform, spd[i], e.Color, i * step + (MaxLen - spd.Length) * step - rowShift, segW);
            e.Line2Root.transform.localPosition = new Vector3(0f, -2f * segW, 0f);
            e.Line2Root.SetActive(true);
        } else e.Line2Root.SetActive(false);
        e.TextRoot.SetActive(true);
    }

    /// <summary>距离文本: xx.xx + km, 数字前空格对齐不补 0.</summary>
    private static string FmtDist(float km) => $"{km,5:F2}km";

    /// <summary>速度文本: ≥100 整数 / 10~100 一位小数 / <10 两位小数 (无 xxx.x 形态), 空格对齐不补 0.</summary>
    private static string FmtSpd(float ms) {
        string s = ms >= 100f ? ms.ToString("F0") : ms >= 10f ? ms.ToString("F1") : ms.ToString("F2");
        return $"{s,5}m/s";
    }

    /// <summary>速度矢量符 (战雷 TTS 同款): 目标中心往下 0.15 格, 静止 = 点, 运动 = 空心圆 (半径 SpeedR) + 速度方向线 (自圆周伸出).
    /// 线长对数连续: 1-10 m/s → 0.5r, 10-100 → 1r, 100-1000 → 1.5r, 超 1000 封顶 2r (档内 log10 平滑, 无跳变).
    /// 数据源 = DC TWS 滤波速度 (板面局部系 km/s, 方向即板面方向); TWS 关 → 矢量符整体不显示 (速度语义只在跟踪时存在).</summary>
    private void UpdateSpeedVector(IconEntry e) {
        if (e.SpeedRoot == null) return;
        // TWS 关: 矢量符整体不显示 (不是静止点 — 速度语义只在 TWS 跟踪时存在)
        if (TwsActive != null && !TwsActive()) {
            e.CircleRoot.SetActive(false);
            e.DotRoot.SetActive(false);
            e.SpeedLine.gameObject.SetActive(false);
            return;
        }
        Vector2 v = e.Target.Velocity;
        Vector2 dir;
        float vMps = MsPerKm(v); // km/s → m/s
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
    /// 弹种 -1 = 未就绪 (CANFIRE 前), 不渲染. AllReady 时十字四象限各出一对直角弯 (拐点卡在四臂内角). 板面局部坐标.
    /// 十字下方 = 飞行时间两位数字 (炮给的, 瞄准期实时自解), 与弹种标签同字号.</summary>
    public void PushBallistic(LeftRight side, Vector2 boardPos, float killRadiusKm, int bulletType, bool ready = false, float flyTime = float.NaN) {
        if (MapSurfaceRef == null) return;
        if (bulletType < 0) {
            if (_ballistic.TryGetValue(side, out var oldB) && oldB.Root != null) { DestroyRoot(oldB.Root); _ballistic.Remove(side); }
            return;
        }
        const float gap = 0.02f, armLen = 0.045f;
        const float top = gap + armLen; // 臂端距中心
        var mark = EnsureAimMark(side, gap, top);
        mark.Root.transform.localPosition = new Vector3(boardPos.x, boardPos.y, GreenOffset);
        RebuildAimCorners(mark, ready, gap, armLen);
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
        // 外圈跟任务弹种: 穿甲弹带 X 指示 (旧版同款; ATMC = 辐射标 + 外圈带六刻度)
        RebuildCircle(mark.RadiusRoot.transform, killRadiusKm, GreenOffset, Color.green, solid: false, pierce: IsArmorPierce((BulletType)bulletType),
            (BulletType)bulletType, ref mark.RadiusKm, ref mark.SegCount, ref mark.Pierce, ref mark.PierceBt);
        if ((BulletType)bulletType == BulletType.ATMC) RebuildRadiation(mark.RadiationRoot!.transform, killRadiusKm, Color.green, GreenPrio, ref mark.RadiationKm);
        else if (mark.RadiationRoot != null && mark.RadiationRoot.transform.childCount > 0) { ClearChildren(mark.RadiationRoot.transform); mark.RadiationKm = -1f; }
        // 核弹装填确认后转 (全局相位, 与队列/落弹环同步 — 开火瞬间无割裂): 只转辐射标 (外圈虚线圆/刻度保持静止, 与落弹点口径一致)
        var nukeRot = NukeRot();
        if (mark.RadiationRoot != null) mark.RadiationRoot.transform.localRotation = (BulletType)bulletType == BulletType.ATMC && NukeArmed ? nukeRot : Quaternion.identity;
    }

    /// <summary>瞄准十字实体 (恒定: 每炮一套, 建一次 — 含十字四臂 + L/R 字标; 子根惰性建).</summary>
    private BallisticMark EnsureAimMark(LeftRight side, float gap, float top) {
        if (!_ballistic.TryGetValue(side, out var mark) || mark.Root == null) {
            mark = new BallisticMark { Root = new GameObject(side == LeftRight.Left ? "FCS2_AimLeft" : "FCS2_AimRight") };
            mark.Root.transform.SetParent(MapSurfaceRef, false);
            _ballistic[side] = mark;
        }
        if (mark.RadiusRoot == null) {
            mark.RadiusRoot = new GameObject("FCS2_AimRadius");
            mark.RadiusRoot.transform.SetParent(mark.Root.transform, false);
            mark.RadiationRoot = new GameObject("FCS2_AimRadiation");
            mark.RadiationRoot.transform.SetParent(mark.Root.transform, false);
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
        return mark;
    }

    /// <summary>AllReady 生气符号 (四象限直角弯, 拐点卡在准星四臂内角 (±gap,±gap), 边长 = 半臂长):
    /// 每弯 = 一条边从外侧到拐点 + 一条边从拐点朝外; 未就绪拆掉.</summary>
    private static void RebuildAimCorners(BallisticMark mark, bool ready, float gap, float armLen) {
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
    }

    /// <summary>Unity 对象引用相等比较器 (绕过 == 假空重载): 销毁对象 instanceID 归零, 默认哈希下两个不同的销毁对象会被判等 — 队列标记键/去重集合必须用它.</summary>
    private sealed class UnityRefComparer : IEqualityComparer<GameObject> {
        public static readonly UnityRefComparer Instance = new();
        public bool Equals(GameObject? x, GameObject? y) => ReferenceEquals(x, y);
        public int GetHashCode(GameObject obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>菱形四边 (选中框/齐射圈/实体图标通用): 半径与线宽板面单位.</summary>
    private static void DrawDiamond(Transform root, float radius, float thickness, Color color, int prio) {
        var pts = new[] {
            new Vector3(0f, radius, 0f), new Vector3(radius, 0f, 0f),
            new Vector3(0f, -radius, 0f), new Vector3(-radius, 0f, 0f),
        };
        for (int s = 0; s < 4; s++) {
            Line(root, pts[s], pts[(s + 1) % 4], thickness, color, prio);
        }
    }

    /// <summary>编号/弹种标签几何 (队列标记与落点指示器同口径): 字号 0.045×5/6×SurfScale, 间距 1.4×字宽, 行上抬基准 dy.</summary>
    private (float segW, float step, float dy) LabelMetrics() {
        float segW = 0.045f * 5f / 6f * SurfScale;
        return (segW, segW * 1.4f, (0.045f / 8f + 0.0222f) * SurfScale);
    }

    /// <summary>km/s → m/s (TWS 速度显示/矢量符同口径).</summary>
    private static float MsPerKm(Vector2 vKm) => vKm.magnitude * 1000f;

    /// <summary>核弹辐射标 R = 杀伤半径/3 (板面单位).</summary>
    private static float NukeRadius(float rKm) => rKm * GeoMap.MapCellSize / 3f;

    /// <summary>辐射标收拢终点 s_stop = 0.125/rKm (内圈直径 = 线宽 5t 即停), clamp ≤1.</summary>
    private static float NukeSStop(float radiusKm) => Mathf.Min(1f, 0.125f / Mathf.Max(0.1f, radiusKm));

    /// <summary>核弹全局相位旋转 (瞄准圈/队列圈/落弹环同步, 开火瞬间无割裂).</summary>
    private Quaternion NukeRot() => Quaternion.Euler(0f, 0f, NukePhase * Mathf.Rad2Deg);

    /// <summary>打击队列指示器批量同步 (FC 每帧调用): 队列任务 + 在炮任务 → 逐实体更新指示器.
    /// 炮击指示线只画在炮任务上 (L/R 两条, 旧版同款); 排队任务只有圈+编号.</summary>
    public void UpdateQueueIndicator(FireControl fc) {
        _seenBuffer.Clear();
        var seen = _seenBuffer;
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
        _goneBuffer.Clear();
        var gone = _goneBuffer;
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

    /// <summary>打击队列指示器 (FC 调用): 红色杀伤圈 + 编号米字数码 (01T/02N/03X 起; 在炮上 L-N/R-X) + 预瞄线 (仅在炮任务).
    /// 全部挂板面 (不挂实体 — 大地图照片堆叠会盖淡实体挂件), 位置每帧由实体世界坐标反算到板面.</summary>
    public void UpdateQueueIndicator(GameObject entity, Slot slot, int queuePos, bool salvo, BulletType shell, ChargeMode mode, Vector2 aimEnd, bool drawLine = false) {
        if (MapSurfaceRef == null) return;
        var entityBoard = (Vector2)MapSurfaceRef.InverseTransformPoint(entity.transform.position);
        if (!_queueMarks.TryGetValue(entity, out var mark)) {
            mark = new QueueIndicator {
                Root = new GameObject("FCS2_QueueMark"),
                RadiusRoot = new GameObject("FCS2_QueueRadius"),
                RadiationRoot = new GameObject("FCS2_QueueRadiation"),
                LabelRoot = new GameObject("FCS2_QueueLabel"),
                BulletRoot = new GameObject("FCS2_QueueBullet"),
                LineRoot = new GameObject("FCS2_QueueLine"),
                OuterRoot = new GameObject("FCS2_QueueOuter"),
                SalvoRoot = new GameObject("FCS2_QueueSalvo"),
            };
            mark.Root.transform.SetParent(MapSurfaceRef, false);
            mark.RadiusRoot.transform.SetParent(mark.Root.transform, false);
            mark.RadiationRoot.transform.SetParent(mark.Root.transform, false);
            mark.LabelRoot.transform.SetParent(mark.Root.transform, false);
            mark.BulletRoot.transform.SetParent(mark.Root.transform, false);
            mark.LineRoot.transform.SetParent(MapSurfaceRef, false); // 预瞄线直接挂板面 (落点层), 不跟队列标记根
            mark.SalvoRoot.transform.SetParent(mark.Root.transform, false);
            mark.OuterRoot.transform.SetParent(mark.Root.transform, false);
            // 第二圈菱形 (旧版 outerSegs 同款: 选中/排队即出, 半径 0.05×√2×1.35, 线宽 0.01, 板面单位 ×SurfScale)
            DrawDiamond(mark.OuterRoot.transform, 0.05f * Mathf.Sqrt(2f) * 1.35f * SurfScale, 0.01f * SurfScale, Color.red, RedPrio);
            _queueMarks[entity] = mark;
        }
        // 根在实体 (菱形选中框留目标); 编号/弹种标签/杀伤圈跟预瞄点 (hasAim 时偏移); 无预瞄回实体
        mark.Root.transform.localPosition = new Vector3(entityBoard.x, entityBoard.y, RedOffset);
        mark.LineRoot.transform.localPosition = new Vector3(entityBoard.x, entityBoard.y, GreenOffset); // 预瞄线根在实体 (线终点 = aimEnd); 绿权重 (绿层, 永远浮红线上)
        bool hasAim = !float.IsNaN(aimEnd.x) && !float.IsNaN(aimEnd.y) && aimEnd.sqrMagnitude > 1e-8f;
        Vector2 aimOff = hasAim ? aimEnd - entityBoard : Vector2.zero;
        mark.RadiusRoot.transform.localPosition = new Vector3(aimOff.x, aimOff.y, 0f); // 圈跟预瞄点
        mark.RadiationRoot.transform.localPosition = new Vector3(aimOff.x, aimOff.y, 0f); // 辐射标同位移
        mark.Slot = slot;
        mark.QueuePos = queuePos;
        mark.Shell = shell;
        mark.Mode = mode;
        // 齐射第三圈菱形 (旧版 SetMarkSalvo 同款: 半径 0.05×√2×1.7, 线宽 0.01, 板面单位 ×SurfScale)
        if (mark.Salvo != salvo) {
            mark.Salvo = salvo;
            ClearChildren(mark.SalvoRoot.transform);
            if (salvo) {
                DrawDiamond(mark.SalvoRoot.transform, 0.05f * Mathf.Sqrt(2f) * 1.7f * SurfScale, 0.01f * SurfScale, Color.red, RedPrio);
            }
        }
        // 红杀伤圈 (板面父级, 板面单位直接画; ATMC = 辐射标 + 外圈带六刻度)
        float rKm = ShellData.KillRadiusKm(shell);
        bool pierce = IsArmorPierce(shell);
        RebuildCircle(mark.RadiusRoot.transform, rKm, RedOffset, Color.red, solid: shell == BulletType.DRIL, pierce: pierce, shell,
            ref mark.RadiusKm, ref mark.SegCount, ref mark.Pierce, ref mark.PierceBt, RedPrio);
        if (shell == BulletType.ATMC) {
            float oldRad = mark.RadiationKm;
            RebuildRadiation(mark.RadiationRoot.transform, rKm, Color.red, RedPrio, ref mark.RadiationKm);
            if (oldRad != mark.RadiationKm || mark.RadiationLines == null) mark.RadiationLines = null; // 实际重画/首帧: 呼吸线缓存作废 (惰性重建)
        }
        else if (mark.RadiationRoot.transform.childCount > 0) { ClearChildren(mark.RadiationRoot.transform); mark.RadiationKm = -1f; }
        // 核弹圈同全局相位旋转 (与绿瞄准圈/落弹环同步) — 上炮且装填确认才转, 排队不转; 只转辐射标, 外圈虚线圆/刻度静止
        var nukeRot = NukeRot();
        mark.RadiationRoot.transform.localRotation = shell == BulletType.ATMC && mark.Slot != Slot.Queue && NukeArmed ? nukeRot : Quaternion.identity;
        // 编号米字数码 (旧版 SetMarkLabel 逐条复刻, 板面空间: 实体局部字号 × SurfScale): 字号 0.045×5/6, 间距 1.4×, 上方 0.14+dy
        var (segW, step, dy) = LabelMetrics();
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
        char modeLetter = ChargeModeLabel.Letter(m.Mode);
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
    /// 剩余/落地直读 Flight 字段 (统一口径 — GC 每帧更新 + 本地外推兜底), 不另传飞时参数.</summary>
    public void ImpactFired(LeftRight side, float aimX, float aimY, BulletType shell, Flight flight) {
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
        var im = EnsureImpact(side);
        // 每发重画/重置 (弹道终点/弹种可能变)
        im.ImpactBoard = board;
        im.NestBoard = (Vector2)MapSurfaceRef.InverseTransformPoint(NestRef.position);
        im.Shell = shell;
        im.Flight = flight; // 剩余/落地统一口径 (GC 每帧更新 Flight, 这里只存引用直读字段)
        im.LastShownSecond = -1;
        im.Landed = false;
        im.NukeLandedAt = -1f; // 上一发扩散残留复位
        // 上一发落地后隐藏的飞行件恢复激活 (虚线一直留着, 见 LandImpact)
        im.SolidRoot.SetActive(true);
        im.DotRoot.SetActive(true);
        im.TimerRoot.SetActive(true);
        im.BulletRoot.SetActive(true);
        im.CircleRoot.SetActive(true);
        TransferFinalTrack(side, flight);
        // 红色固定虚线 (全长弹道, 落地保留): 与 A1 同款 — 游戏 Dashed 材质单线, 保持红色 (恒定实体, 建一次)
        if (im.FixedLine == null) {
            im.FixedLine = CreateDashedLine(im.FixedRoot.transform, "FCS2_ImpactFixed", 0.006f, Color.red, ImpactPrio);
        }
        im.FixedLine.Start = new Vector3(im.NestBoard.x, im.NestBoard.y, 0f);
        im.FixedLine.End = new Vector3(im.ImpactBoard.x, im.ImpactBoard.y, 0f);
        im.FixedLine.gameObject.SetActive(true);
        // 实线缓存作废: 下一帧 UpdateImpacts 按新弹道重建 (起点=炮口, 终点=新落点)
        if (im.SolidLine != null) { try { UnityEngine.Object.Destroy(im.SolidLine.gameObject); } catch { } im.SolidLine = null; }
        // 红落点圈 (弹种杀伤半径, 随弹种; ATMC = 辐射标 + 外圈带六刻度 + 飞行收拢三角)
        float rKm = ShellData.KillRadiusKm(im.Shell);
        RebuildCircle(im.CircleRoot.transform, rKm, 0f, Color.red, solid: im.Shell == BulletType.DRIL, pierce: IsArmorPierce(im.Shell), im.Shell,
            ref im.RadiusKm, ref im.SegCount, ref im.Pierce, ref im.PierceBt, ImpactPrio);
        im.CircleRoot.transform.localPosition = new Vector3(im.ImpactBoard.x, im.ImpactBoard.y, ImpactOffset);
        if (im.Shell == BulletType.ATMC) {
            im.NukeR = NukeRadius(rKm); // 核弹 R = 杀伤半径/3 (板面单位)
            RebuildRadiation(im.RadiationRoot.transform, rKm, Color.red, ImpactPrio, ref im.RadiationKm);
            im.RadiationRoot.transform.localPosition = new Vector3(im.ImpactBoard.x, im.ImpactBoard.y, ImpactOffset);
            im.RadiationRoot.transform.localScale = Vector3.one; // 上一发收拢残留复位
            im.RadiationLines.Clear();
            foreach (var line in im.RadiationRoot.transform.GetComponentsInChildren<Il2CppShapes.Line>())
                im.RadiationLines.Add((line, line.Thickness)); // 线宽基准缓存 (收缩补偿用)
            im.TriRoot.transform.localScale = Vector3.one; // 三角跟缩残留复位
            im.TriRoot.transform.localPosition = new Vector3(im.ImpactBoard.x, im.ImpactBoard.y, ImpactOffset); // 三角锚点 = 落点 (根挂板面原点, 不跟落点会聚拢到地图中心)
            BuildTris(im);
            OnNukeFired?.Invoke(); // 核弹开火 → Launch 警报 (每发一次)
        } else {
            if (im.RadiationRoot.transform.childCount > 0) { ClearChildren(im.RadiationRoot.transform); im.RadiationKm = -1f; }
            ClearTris(im);
        }
        im.RadiationRoot.SetActive(im.Shell == BulletType.ATMC);
        im.TriRoot.SetActive(im.Shell == BulletType.ATMC);
        RebuildImpactLabels(im);
        // 淡入预写 alpha 0 (只核弹的辐射标+三角 — 消除开火瞬间到下一帧 UpdateImpacts 之间的 ~16ms 全亮闪)
        if (im.Shell == BulletType.ATMC) {
            foreach (var (line, _) in im.RadiationLines) line.Color = new Color(1f, 0f, 0f, 0f);
            for (int k = 0; k < im.TriLines.Length; k++) if (im.TriLines[k] != null) im.TriLines[k].Color = new Color(1f, 0f, 0f, 0f);
        }
        im.Root.SetActive(true);
    }

    /// <summary>落点指示器实体 (恒定: 每炮一套, 建一次 — 不新建不销毁).</summary>
    private ImpactIndicator EnsureImpact(LeftRight side) {
        if (_impacts.TryGetValue(side, out var im) && im.Root != null) return im;
        im = new ImpactIndicator { Root = new GameObject("FCS2_ImpactIndicator") };
        im.Root.transform.SetParent(MapSurfaceRef, false);
        im.FixedRoot = new GameObject("FCS2_ImpactFixed");
        im.SolidRoot = new GameObject("FCS2_ImpactSolid");
        im.DotRoot = new GameObject("FCS2_ImpactDot");
        im.CircleRoot = new GameObject("FCS2_ImpactCircle");
        im.RadiationRoot = new GameObject("FCS2_ImpactRadiation");
        im.TriRoot = new GameObject("FCS2_ImpactTris");
        im.TimerRoot = new GameObject("FCS2_ImpactTimer");
        im.BulletRoot = new GameObject("FCS2_ImpactBullet");
        foreach (var c in new[] { im.FixedRoot, im.SolidRoot, im.DotRoot, im.CircleRoot, im.RadiationRoot, im.TriRoot, im.TimerRoot, im.BulletRoot })
            c.transform.SetParent(im.Root.transform, false);
        // 红线/红点挂 ImpactOffset 同层 (圈/字/核弹三角在各自动作里单独设 z, 这里只抬线层)
        foreach (var c in new[] { im.FixedRoot, im.SolidRoot, im.DotRoot })
            c.transform.localPosition = new Vector3(0f, 0f, ImpactOffset);
        // 实心红点 (圆画在局部原点, 后续移动父级位置即可, 只建一次)
        FillDot(im.DotRoot.transform, Vector2.zero, 0.012f, Color.red, ImpactPrio);
        _impacts[side] = im;
        return im;
    }

    /// <summary>目标轨迹线: 击发 → 火控线参数转移给最终线 (冻结, 倒计时驱动缩短), 火控线立即释放 (下个任务解算接管).
    /// 不看 activeSelf: 击发后 FC push 冻结已把线隐藏, 数据仍在.
    /// 转移校验: ① LastValid 3s 窗口 — 静态目标无预瞄 (v=0) 不 push, tr 里是上一发 (假目标) 的旧参数, 超窗拒转移
    /// (真目标开火不再把假目标轨迹复活成最终线); ② 参数有效性 — 交汇点非 NaN 且飞时为正</summary>
    private void TransferFinalTrack(LeftRight side, Flight flight) {
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
    }

    /// <summary>弹种标签 (飞行时挪到队列编号位: 与队列时 L-X 同 y = 0.14 + dy; 板面空间 = 实体空间常量 × SurfScale) + 计时基线.</summary>
    private void RebuildImpactLabels(ImpactIndicator im) {
        var (segW, step, dy) = LabelMetrics();
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
    }

    /// <summary>核弹扩散启动 (飞行计时到 0 或落地事件都行, 只触发一次): 扇叶/三角/外圈藏, 中心内圈实体复用为扩散环.</summary>
    private static void StartNukeExpand(ImpactIndicator im) {
        if (im.NukeLandedAt >= 0f) return;
        im.CircleRoot.SetActive(false);
        im.TriRoot.SetActive(false);
        for (int i = 48; i < im.RadiationLines.Count; i++) im.RadiationLines[i].Line.gameObject.SetActive(false); // 内圈 48 段在前, 扇叶 78 线在后
        im.NukeLandedAt = Time.time;
    }

    /// <summary>落地对账: Flight 判定的落地时刻, 目标彼时实际位置 vs 落点 — 直接差 = 打远/打近 (不依赖铁巢/开火计时).</summary>
    private void LogLanding(LeftRight side, ImpactIndicator im) {
        if (MapSurfaceRef == null || !_finals.TryGetValue(side, out var fin) || fin.Target == null) return;
        var tb = (Vector2)MapSurfaceRef.InverseTransformPoint(fin.Target.position);
        MelonLogger.Msg($"[DC] landing {side}: t={Time.time:F2} impact=({im.ImpactBoard.x:F3},{im.ImpactBoard.y:F3}) target=({tb.x:F3},{tb.y:F3}) d={Vector2.Distance(im.ImpactBoard, tb):F3}板面");
    }

    /// <summary>落地: 飞行件全部隐藏 (实线/红点/计时/弹种标签/杀伤圈) — 只有红色虚线弹道保留到下一次开火
    /// (下一发 ImpactFired 恢复并重画; 恒定实体不销毁).
    /// 核弹例外: 圈/辐射标/三角保留 — 中心收敛图标 1s 扩散回整杀伤圈后再藏 (航拍反馈接管前).</summary>
    private static void LandImpact(ImpactIndicator im) {
        if (im.Landed) return;
        im.Landed = true;
        im.SolidRoot.SetActive(false);
        im.DotRoot.SetActive(false);
        im.TimerRoot.SetActive(false);
        im.BulletRoot.SetActive(false);
        if (im.Shell == BulletType.ATMC) {
            StartNukeExpand(im); // 扩散动画起点 (扇叶/三角/外圈藏, 内圈复用扩散)
            return;
        }
        im.CircleRoot.SetActive(false);
        im.RadiationRoot.SetActive(false); // 核弹辐射标同属飞行件
        im.TriRoot.SetActive(false);
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
            if (fin.Flight.FlyTime <= 0.01f) continue; // 退化数据 (飞时 0): 本帧不动, 防 NaN 传播
            float progress = 1f - fin.Flight.Remain / fin.Flight.FlyTime; // 0=刚出膛 1=落地 (Flight 统一口径)
            var head = TrackCurve(fin.P0Board, fin.VBoard, fin.ABoard, fin.JBoard, fin.T0 * progress);
            fin.Solid.Start = new Vector3(head.x, head.y, 0f);
            fin.Solid.End = new Vector3(fin.AimBoard.x, fin.AimBoard.y, 0f); // 终点 = 开火瞬间交汇点 (不动)
            fin.DotRoot.transform.localPosition = new Vector3(fin.AimBoard.x, fin.AimBoard.y, GreenOffset); // 交汇点不动
        }
    }

    private static Vector2 TrackCurve(Vector2 p0, Vector2 v, Vector2 a, Vector2 j, float t) =>
        p0 + v * t + 0.5f * a * t * t + j * (t * t * t) / 6f;

    private const int TrackSamples = 64; // 轨迹曲线采样点数 (BuildDots/SampleAt 共用; Pts 数组长 = 采样数 + 1)

    /// <summary>沿曲线 τ∈[t0,t1] 铺点 (点式虚线): TrackSamples 点采样累计弧长, 点位置弧长插值定位, 点方向 = 轨迹切线.
    /// 点距 = 均分 len/(n-1): 最小 0.008 (实测手感值), 无上限, 最多 32 点 —
    /// 轨迹长 ∝ 目标速度, 点疏密直观反映速度. 返回铺出的点数, 多余由调用方隐藏.</summary>
    private static int BuildDots(TrackIndicator tr, Vector2 p0, float t0, float t1) {
        const int S = TrackSamples;
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
        int lo = 0, hi = TrackSamples;
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
            if (im.Root == null || !im.Root.activeSelf) continue; // 未激活 不动
            if (im.Flight == null) continue;
            if (im.Flight.Landed && !im.Landed) { LogLanding(kv.Key, im); LandImpact(im); } // 落地: 飞行件隐藏
            if (im.NukeLandedAt >= 0f) {
                // 核弹扩散动画: 收拢结束 (最后 1.5s 刚完, 不等落地事件) 中心内圈匀速 2s:
                // 1s 扩到杀伤半径 (0.6R×s=3R → s=5), 继续同速率扩到 s=10, 第 2 秒 alpha 1→0 归零后全部隐藏
                float e = Mathf.Clamp01((Time.time - im.NukeLandedAt) / 2f);
                im.RadiationRoot.transform.localRotation = NukeRot();
                if (e >= 1f) {
                    im.RadiationRoot.SetActive(false);
                    im.NukeLandedAt = -1f; // 扩散完 (alpha 已归 0)
                } else {
                    float sStop = NukeSStop(im.RadiusKm);
                    float s = Mathf.Lerp(sStop, 10f, e); // 匀速: 1s 到杀伤半径 (s=5), 继续扩到 10
                    im.RadiationRoot.transform.localScale = new Vector3(s, s, s);
                    float fadeOut = Mathf.Clamp01((e - 0.5f) / 0.5f); // 第 2 秒 alpha 1→0
                    foreach (var (line, t) in im.RadiationLines) {
                        line.Thickness = t / s;
                        line.Color = new Color(1f, 0f, 0f, 1f - fadeOut);
                    }
                }
                continue;
            }
            if (im.Landed) continue; // 非核弹已落地: 飞行件已在 LandImpact 藏好
            if (im.Flight.FlyTime <= 0.01f) continue; // 退化数据 (飞时 0): 本帧不动, 防 NaN 传播
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
            // 核弹指示器旋转 + 三角收拢 (恒定实体每帧只动端点):
            // 整体逆时针 60°/s (= 1/6 圈/s — 三对称转 120° 即"归位", 视觉周期 2s);
            // 开火后 1s 透明度淡入 (旋转开始不生硬); 收拢终点 = 顶角重合 (d→h: 内顶点压在圆心, 三三角不再叠成一团)
            if (im.Shell == BulletType.ATMC && im.TriLines[0] != null) {
                var rot = NukeRot(); // 全局相位 — 与瞄准圈/队列圈同步, 开火瞬间无割裂
                im.RadiationRoot.transform.localRotation = rot; // 辐射标跟转 (内圈圆旋转对称无感, 扇叶/刻度转)
                im.TriRoot.transform.localRotation = rot;
                // 淡入 (只辐射标+三角, 外圈/红线/字不参与): 完全透明 1s (传导延迟), 再 1s 缓慢浮现 (alpha 0→1)
                float fade = Mathf.Clamp01((im.Flight.FlyTime - remain - 1f) / 1f);
                foreach (var (line, _) in im.RadiationLines) line.Color = new Color(1f, 0f, 0f, fade);
                for (int k = 0; k < im.TriLines.Length; k++) if (im.TriLines[k] != null) im.TriLines[k].Color = new Color(1f, 0f, 0f, fade);
                float denom = im.Flight.FlyTime - 1.5f;
                if (denom < 0.001f) denom = im.Flight.FlyTime; // 短弹道: 收拢压到全程
                float frac = Mathf.Clamp01((im.Flight.FlyTime - remain) / denom);
                float h = 0.5f * im.NukeR / Mathf.Sqrt(3f); // 顶点距 (边长 0.5R)
                float d = Mathf.Lerp(2.8f * im.NukeR, h, frac); // 从 2.8R 收到 h (顶角重合于圆心)
                // 最后 1.5s: 辐射告警收拢 — s: 1 → s_stop = 0.125/rKm (内圈直径 = 线宽 5t 即停, 再缩没意义);
                // 三角跟缩 ts = min(1, s/0.208) — 保证外廓 0.5R·ts ≤ 扇叶外缘 2.4R·s (扇叶缩过 0.5R 后三角同步缩);
                // 两处线宽都补偿 (世界线宽不随缩放走)
                float sStop = NukeSStop(im.RadiusKm);
                float shrinkP = Mathf.Clamp01((1.5f - remain) / 1.5f);
                float s = Mathf.Lerp(1f, sStop, shrinkP);
                float ts = Mathf.Min(1f, s / 0.208f);
                im.RadiationRoot.transform.localScale = new Vector3(s, s, s);
                im.TriRoot.transform.localScale = new Vector3(ts, ts, ts);
                if (s > 0.001f) {
                    foreach (var (line, t) in im.RadiationLines) line.Thickness = t / s;
                }
                if (ts > 0.05f) {
                    float tTri = 0.05f * GeoMap.MapCellSize / ts; // 三角线宽补偿 (基准 5t)
                    for (int k = 0; k < im.TriLines.Length; k++) if (im.TriLines[k] != null) im.TriLines[k].Thickness = tTri;
                }
                for (int k = 0; k < 3; k++) {
                    float a = Mathf.PI / 6f + k * 2f * Mathf.PI / 3f; // 30/150/270° (与扇叶互补)
                    TrisVerts(a, d, h, out var p1, out var p2, out var p3);
                    im.TriLines[k * 3].Start = new Vector3(p1.x, p1.y, 0f);
                    im.TriLines[k * 3].End = new Vector3(p2.x, p2.y, 0f);
                    im.TriLines[k * 3 + 1].Start = new Vector3(p2.x, p2.y, 0f);
                    im.TriLines[k * 3 + 1].End = new Vector3(p3.x, p3.y, 0f);
                    im.TriLines[k * 3 + 2].Start = new Vector3(p3.x, p3.y, 0f);
                    im.TriLines[k * 3 + 2].End = new Vector3(p1.x, p1.y, 0f);
                }
                // 收拢结束 (最后 1.5s 刚完, remain≤0): 内圈开始扩散 — 用计时不等落地事件
                if (remain <= 0f) StartNukeExpand(im);
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
        // TWS 信息文本 (菱形框右侧): 两排中心 = (a,0), 与矢量符 (0,-a) 到目标同距 (用户定稿)
        var textRoot = new GameObject("FCS2_TwsInfo");
        textRoot.transform.SetParent(root.transform, false);
        textRoot.transform.localPosition = new Vector3(0.15f * GeoMap.MapCellSize, 0f, 0f);
        var line1 = new GameObject("FCS2_TwsDist");
        line1.transform.SetParent(textRoot.transform, false);
        var line2 = new GameObject("FCS2_TwsSpd");
        line2.transform.SetParent(textRoot.transform, false);
        _icons[t.Entity] = new IconEntry {
            Root = root, Target = t, SpeedRoot = speedRoot!, CircleRoot = circleRoot!, DotRoot = dotRoot!, SpeedLine = line!,
            TextRoot = textRoot, Line1Root = line1, Line2Root = line2, Color = color,
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
            DrawDiamond(parent, 0.05f * Mathf.Sqrt(2f) * s, thin, color, prio);
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
    /// 各圈自转 90°/n, 实线覆盖角 = dash/r; solid=整圆 24 段; pierce = 穿甲指示 (AP 等 = 从圆周向内 4 条半半径 X 线;
    /// ATMC = 外圈带 2.8R~3R 六短辐刻度). 全部挂板面, 板面单位直接画.</summary>
    private static void RebuildCircle(Transform root, float rKm, float z, Color color, bool solid, bool pierce, BulletType bt, ref float lastR, ref int lastN, ref bool lastPierce, ref BulletType lastBt, int prio = GreenPrio) {
        float rB = rKm * GeoMap.MapCellSize;               // 板面单位 (段数/虚线长按板面算)
        const float dashB = 0.01f;                         // KillDashLen (板面单位)
        int n = solid ? 24 : Mathf.Max(4, (int)Mathf.Round(2f * Mathf.PI * rB / (2f * dashB)));
        if (!solid && (n & 1) != 0) n--; // 虚线段数取偶数: 奇数段图案不轴对称, 看着歪
        if (Mathf.Abs(rKm - lastR) < 0.0001f && n == lastN && pierce == lastPierce && bt == lastBt && root.childCount > 0) return;
        lastR = rKm;
        lastN = n;
        lastPierce = pierce;
        lastBt = bt;
        ClearChildren(root);
        float r = rB;                                       // 板面半径
        float dash = dashB;                                 // 板面虚线弧长
        float t = 0.02f * GeoMap.MapCellSize;               // KillThick 2× (板面单位, 用户定稿)
        for (int s = 0; s < n; s++) {
            float a0 = Mathf.PI * 2f * s / n + (solid ? 0f : Mathf.PI / (2f * n)); // 各圈各自转 90°/n
            float a1 = a0 + (solid ? Mathf.PI * 2f / n : dash / r);                // 实线覆盖角
            Line(root, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r, new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r, t, color, prio);
        }
        if (pierce) {
            if (bt == BulletType.ATMC) {
                // 核弹穿甲指示: 外圈带 6 条短辐线 (2.8R~3R, 每 60°, 随辐射标顺时针 30°) — 辐射警示刻度 (X 留给其他穿甲弹)
                for (int s = 0; s < 6; s++) {
                    float a = Mathf.PI * 2f * s / 6f - Mathf.PI / 6f;
                    var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    Line(root, d * (r * 2.8f / 3f), d * r, t, color, prio);
                }
            } else {
                // 穿甲指示: 从圆周向内 4 条半半径长线, X 型 (对角方向)
                Vector2[] xdirs = { new(1f, 1f), new(-1f, 1f), new(1f, -1f), new(-1f, -1f) };
                foreach (var d in xdirs) {
                    Vector2 dn = d.normalized;
                    Line(root, dn * r, dn * (r * 0.5f), t, color, prio);
                }
            }
        }
    }

    /// <summary>核弹辐射标 (ATMC 专属): 内圈实线圆 (0.6R, 线宽 5×) + 3 扇叶 (0.9R~2.4R, 线宽 5×, 两条径向边 + 0.9R 内弧/2.4R 外弧, 闭合成形);
    /// 整体顺时针转 30° (轴 −30/90/210°). 段数加密 (圆 48 段/弧 12 弦) 消小半径棱角. R = 杀伤半径/3, 板面单位直接画.</summary>
    private static void RebuildRadiation(Transform root, float rKm, Color color, int prio, ref float lastR) {
        if (Mathf.Abs(rKm - lastR) < 0.0001f && root.childCount > 0) return;
        lastR = rKm;
        ClearChildren(root);
        float t = 0.01f * GeoMap.MapCellSize; // 基准线宽
        float tC = t * 5f;                    // 中心圆线宽 5× (用户定稿)
        float tB = t * 5f;                    // 扇叶线宽 5× (用户定稿)
        float R = NukeRadius(rKm);
        const int segs = 48; // 中心圆段数 (24 段小半径有棱角)
        for (int i = 0; i < segs; i++) { // 内圈圆 (0.6R)
            float a0 = i * 2f * Mathf.PI / segs, a1 = (i + 1) * 2f * Mathf.PI / segs;
            Line(root, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * (0.6f * R), new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * (0.6f * R), tC, color, prio);
        }
        for (int b = 0; b < 3; b++) { // 3 扇叶 (顺时针 30°: 轴 −30/90/210°, ±30° 径向边 + 内弧 0.9R/外弧 2.4R 各 12 弦, 闭合成形)
            float axis = b * 2f * Mathf.PI / 3f - Mathf.PI / 6f;
            for (int e = -1; e <= 1; e += 2) {
                float a = axis + e * Mathf.PI / 6f;
                var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                Line(root, d * (0.9f * R), d * (2.4f * R), tB, color, prio);
            }
            const int arcSegs = 12;
            for (int s = 0; s < arcSegs; s++) {
                float a0 = axis - Mathf.PI / 6f + s * (Mathf.PI / 3f) / arcSegs;
                float a1 = axis - Mathf.PI / 6f + (s + 1) * (Mathf.PI / 3f) / arcSegs;
                Line(root, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * (2.4f * R), new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * (2.4f * R), tB, color, prio);
                Line(root, new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * (0.9f * R), new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * (0.9f * R), tB, color, prio);
            }
        }
    }

    /// <summary>核弹飞行三角构建 (恒定实体 9 线): 3 个等边三角 (边长 0.5R), 中心在 2.8R 圈, 一角指圆心,
    /// 30/150/270° (与扇叶互补), 线宽 5× 基准; 位置由 UpdateImpacts 每帧改端点 (收拢动画).</summary>
    private void BuildTris(ImpactIndicator im) {
        ClearChildren(im.TriRoot.transform);
        float t2 = 0.05f * GeoMap.MapCellSize; // 线宽 5× 基准 (用户定稿)
        float h = 0.5f * im.NukeR / Mathf.Sqrt(3f); // 顶点距 (边长 0.5R)
        for (int k = 0; k < 3; k++) {
            float a = Mathf.PI / 6f + k * 2f * Mathf.PI / 3f; // 30/150/270° (随辐射标顺时针 30°, 与扇叶互补)
            TrisVerts(a, 2.8f * im.NukeR, h, out var p1, out var p2, out var p3);
            im.TriLines[k * 3] = Line(im.TriRoot.transform, p1, p2, t2, Color.red, ImpactPrio);
            im.TriLines[k * 3 + 1] = Line(im.TriRoot.transform, p2, p3, t2, Color.red, ImpactPrio);
            im.TriLines[k * 3 + 2] = Line(im.TriRoot.transform, p3, p1, t2, Color.red, ImpactPrio);
        }
    }

    /// <summary>核弹三角清空 (非 ATMC 弹种/重画前).</summary>
    private static void ClearTris(ImpactIndicator im) {
        ClearChildren(im.TriRoot.transform);
        for (int i = 0; i < im.TriLines.Length; i++) im.TriLines[i] = null!;
    }

    /// <summary>等边三角三顶点: 中心 = 方向 a × 距 d, 顶点 1 指向圆心 (中心向圆心退 h = 边长/√3), 顶点 2/3 = ±120° 旋转.</summary>
    private static void TrisVerts(float a, float d, float h, out Vector2 p1, out Vector2 p2, out Vector2 p3) {
        var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        var c = dir * d;
        var off = -dir * h;
        p1 = c + off;
        p2 = c + new Vector2(off.x * -0.5f - off.y * 0.8660254f, off.x * 0.8660254f - off.y * 0.5f);
        p3 = c + new Vector2(off.x * -0.5f + off.y * 0.8660254f, -off.x * 0.8660254f - off.y * 0.5f);
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
        public GameObject TextRoot = null!;   // TWS 信息文本 (菱形框右侧, 两排中心 = (a,0))
        public GameObject Line1Root = null!;  // 距离行 (上, 静止时居中单行)
        public GameObject Line2Root = null!;  // 速度行 (下, 静止隐藏)
        public Color Color;                   // 图标色 (敌红/友蓝/参考点绿 — 文本同色)
        public string DistText = "";          // 距离字符串缓存 (变了才重画)
        public string SpdText = "";           // 速度字符串缓存 (空 = 静止单行)
    }

    private class QueueIndicator {
        public GameObject Root = null!;
        public GameObject RadiusRoot = null!;
        public GameObject RadiationRoot = null!; // 核弹辐射标 (ATMC 专属, 跟杀伤圈同层同位移)
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
        public float RadiationKm = -1f;
        public int SegCount;
        public bool Pierce;
        public BulletType PierceBt = (BulletType)(-1); // 穿甲指示弹种缓存 (ATMC 刻度 vs X 要重画)
        public List<Il2CppShapes.Line>? RadiationLines; // 辐射标线缓存 (呼吸着色; 重画/清空置空惰性重建 — 免每帧 GetComponentsInChildren 分配)
    }

    private class ImpactIndicator {
        public GameObject Root = null!;
        public GameObject FixedRoot = null!;
        public GameObject SolidRoot = null!;
        public GameObject DotRoot = null!;
        public GameObject CircleRoot = null!;
        public GameObject RadiationRoot = null!; // 核弹辐射标 (ATMC 专属, 同落点圈层)
        public GameObject TriRoot = null!;       // 核弹飞行收拢三角 (恒定实体 9 线, 每帧动端点)
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
        public float RadiationKm = -1f;
        public float NukeR;     // 核弹 R = 杀伤半径/3 (板面单位, 三角几何基准)
        public int SegCount;
        public bool Pierce;
        public BulletType PierceBt = (BulletType)(-1); // 穿甲指示弹种缓存 (ATMC 刻度 vs X 要重画)
        public int LastShownSecond = -1;
        public Il2CppShapes.Line? SolidLine; // 未飞段实线 (缓存, 每帧只动端点)
        public readonly Il2CppShapes.Line[] TriLines = new Il2CppShapes.Line[9]; // 3 三角 × 3 边
        public readonly List<(Il2CppShapes.Line Line, float Thickness)> RadiationLines = new(); // 辐射标线宽缓存 (收缩期等比缩位置 + 线宽补偿)
        public float NukeLandedAt = -1f; // 核弹落地时刻 (扩散动画起点; -1 = 未落地/扩散完)
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
        public GameObject? RadiationRoot; // 核弹辐射标 (ATMC 专属, 跟绿圈同层)
        public GameObject? CrossRoot;
        public GameObject CornerRoot = null!;
        public GameObject BulletRoot = null!;
        public GameObject FlyRoot = null!;
        public bool Ready;
        public string BulletText = "";
        public string FlyText = "";
        public float RadiusKm = -1f;
        public float RadiationKm = -1f;
        public int SegCount;
        public bool Pierce;
        public BulletType PierceBt = (BulletType)(-1); // 穿甲指示弹种缓存 (ATMC 刻度 vs X 要重画)
    }
}
