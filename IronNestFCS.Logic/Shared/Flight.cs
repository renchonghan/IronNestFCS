using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 一发炮弹的飞行状态 (GC 击发时创建, 每帧由 GC 唯一更新; 消费方 DC 渲染/HUD 完成队列 只读字段).
/// 炮表单槽信号 (CountdownRemainingSeconds) 的 NaN/冻结语义解释收口在本类 — 这是全链唯一的
/// "这发弹什么时候落地" 判定 (review C1: 旧有四套各自为政的落地/剩余逻辑, 已合并):
///   活读有效 → 与游戏指示器同步 (min(gc, 本地外推));
///   冻结 (切任务抢表, 帧间降速 < 0.05 不刷新基准) 或 NaN (倒计时未启动/无下任务流程收尾清表) → 本地外推 (GcLag 基准差锁定);
///   本地归零 = 落地封存 (唯一落地口径 — 炮表冻结/清表/未启动都不影响).
/// 切任务起链不碰本对象 — 上一发继续由它计时 (传导不因新任务断, 游戏不会干这种奇怪事).
/// </summary>
public class Flight {
    public float FlyTime;           // 锁存总飞时 (出膛时刻)
    public float FiredAtLocal;      // Time.time 出膛时刻 (本地计时基准)
    public float FiredAtMission;    // 任务时钟出膛时刻 (NaN = 无表)
    public float Remain;            // 剩余秒 (统一口径, 每帧更新)
    public bool Landed;             // 落地封存 (之后 Remain 恒 0)
    public float GcRaw = float.NaN; // 原始炮表值 (诊断)

    private float _lastGc = float.NaN;
    private float _gcLag = float.NaN; // 本地 − 炮表 基准差 (倒计时启动滞后 ~1s, 恒差锁定)

    /// <summary>每帧由 GC 循环调用一次 (gcRaw = CountdownRemainingSeconds 原始值, nowLocal = Time.time).</summary>
    public void Update(float gcRaw, float nowLocal) {
        if (Landed) return;
        GcRaw = gcRaw;
        float local = FlyTime - (nowLocal - FiredAtLocal);
        if (!float.IsNaN(gcRaw) && (float.IsNaN(_lastGc) || Mathf.Abs(gcRaw - _lastGc) > 0.05f)) {
            _lastGc = gcRaw;
            _gcLag = local - gcRaw; // gc 正常降速: 刷新基准差
        }
        float lagged = local - (float.IsNaN(_gcLag) ? 0f : _gcLag);
        Remain = float.IsNaN(gcRaw) ? lagged : Mathf.Min(gcRaw, lagged);
        if (local <= 0f) { Landed = true; Remain = 0f; } // 本地归零 = 落地 (与红线同基准, 出膛时刻起算)
    }
}
