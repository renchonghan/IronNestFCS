using System.Collections.Generic;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 单轴 TRAK 追踪器 (25fps 固定节拍, 单发/齐射共用): 天顶星伺服设 1 帧预测值 + 修正项.
/// I = 变积分 (|err| 超 iWindowOff 清窗不积分, iWindowFull 内全额, 之间线性衰减到 0) × ki × 16 帧误差窗口和 —
/// 大误差不进记忆, 消除超调; D = 两帧差分 (err - 前两帧 err, a4-a2) × kd 抑制震荡 —
/// 交替高频抖动互相抵消, 对真实斜率等效刹车加倍. 差分死区防量化噪声.
/// 每次调用 Step 推进一步, 返回修正量 (调用方叠加到预测值上作为机构设定).
/// 套上并跟踪稳定 (误差+速度连续收住 5 帧) → Locked; 误差超死区但帧间几乎不动连续 10s → GaveUp (同时锁 Locked, 调用方不再追).
/// 误差在死区内不算卡 (套上跟稳静默正常, 远处慢目标帧间转角极小不误判).
/// 仰角 (E) 与方位 (H) 通用, 死区按轴实例化.
/// </summary>
public class TrackAxis {
    private readonly float lockDeadband;      // 套上误差死区 (°)
    private readonly float iWindowFull;       // 变积分: 全额积分区 (|err| 上限), 按轴调 (E 0.1 / H 0.3)
    private readonly float iWindowOff;        // 变积分: 积分完全关闭区 (|err| 下限, ≥此值清窗), 按轴调 (E 0.5 / H 1)
    private const float VelDeadband = 0.1f;   // 套上速度死区
    private const float Ki = 0.05f;
    private const float Kd = 3.0f;            // 作用于两帧差分 (等效刹车与旧单帧差分 ×6 相同; 稳定性 4Ki/Kd < 1 依旧)
    private const float DDeadband = 0.01f;    // D 项差分死区
    private const float StuckStep = 0.005f;   // 帧间位置变化 < 此值累计卡死 (0.125°/s)
    private const float Dt = 0.04f;           // 25fps

    private readonly Queue<float> _errs = new(16);
    private float _err1Ago = float.NaN;       // 上帧误差 (D 两帧差分用)
    private float _err2Ago = float.NaN;       // 上上帧误差 (D 两帧差分用)
    private float _lastPos = float.NaN;

    public float Stable;
    public float Stuck;
    public bool Locked;
    public bool GaveUp;

    public TrackAxis(float lockDeadband, float iWindowFull, float iWindowOff) {
        this.lockDeadband = lockDeadband;
        this.iWindowFull = iWindowFull;
        this.iWindowOff = iWindowOff;
    }

    /// <summary>推进一步: err = 目标-实际, vel = 实际速度, pos = 实际位置. 返回修正量 (叠加到预测值).</summary>
    public float Step(float err, float vel, float pos) {
        // 套上并跟踪稳定: 误差+速度连续收住 5 帧 (0.2s) 才判定 (防瞬时过零)
        if (Mathf.Abs(err) <= lockDeadband && Mathf.Abs(vel) <= VelDeadband) {
            Stable += Dt;
            if (Stable >= 0.2f) Locked = true;
        }
        else { Stable = 0f; Locked = false; }
        // I: 变积分 — |err| 大时清窗不积分 (大误差不进记忆, 防收敛后旧记忆补踢一脚造成超调)
        float iTerm = 0f;
        float ae = Mathf.Abs(err);
        if (ae < iWindowOff) {
            float beta = ae <= iWindowFull ? 1f : 1f - (ae - iWindowFull) / (iWindowOff - iWindowFull);
            _errs.Enqueue(err);
            if (_errs.Count > 16) _errs.Dequeue();
            float sum = 0f;
            foreach (var e in _errs) sum += e;
            iTerm = Ki * beta * sum;
        }
        else _errs.Clear();
        // D: 两帧差分 (a4-a2), 交替高频抖动 (帧间正负交替) 互相抵消, 真实斜率响应加倍
        float d = float.IsNaN(_err2Ago) ? 0f : err - _err2Ago;
        if (Mathf.Abs(d) < DDeadband) d = 0f;
        _err2Ago = _err1Ago;
        _err1Ago = err;
        // 无进展保护: 误差超出死区 (伺服有活干) 但帧间几乎不动连续 10s → 放弃本轴.
        // 误差在死区内 = 已套上跟稳, 静默即正常 — 远处慢目标 (如列车) 角速度远低于阈值, 不能按位置变化量判卡
        if (Mathf.Abs(err) > lockDeadband && !float.IsNaN(_lastPos) && Mathf.Abs(pos - _lastPos) < StuckStep) {
            Stuck += Dt;
            if (Stuck >= 10f) { GaveUp = true; Locked = true; }
        }
        else Stuck = 0f;
        _lastPos = pos;
        return iTerm + Kd * d;
    }
}
