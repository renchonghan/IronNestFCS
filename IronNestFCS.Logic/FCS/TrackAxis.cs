using System.Collections.Generic;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 单轴 TRAK 追踪器 (25fps 固定节拍, 单发/齐射共用): 天顶星伺服设 1 帧预测值 + 修正项
/// (I = 16 帧误差窗口和 × ki 消除持续滞后, D = 误差差分 × kd 抑制震荡, 差分死区防量化噪声).
/// 每次调用 Step 推进一步, 返回修正量 (调用方叠加到预测值上作为机构设定).
/// 套上并跟踪稳定 (误差+速度连续收住 5 帧) → Locked; 连续 10s 帧间几乎不动 → GaveUp (同时锁 Locked, 调用方不再追).
/// 仰角 (E) 与方位 (H) 通用, 死区按轴实例化.
/// </summary>
public class TrackAxis {
    private readonly float lockDeadband;      // 套上误差死区 (°)
    private const float VelDeadband = 0.1f;   // 套上速度死区
    private const float Ki = 0.05f;
    private const float Kd = 6.0f;            // 稳定性条件 4Ki/Kd < 1
    private const float DDeadband = 0.01f;    // D 项差分死区
    private const float StuckStep = 0.005f;   // 帧间位置变化 < 此值累计卡死 (0.125°/s)
    private const float Dt = 0.04f;           // 25fps

    private readonly Queue<float> _errs = new(16);
    private float _lastErr;
    private float _lastPos = float.NaN;

    public float Stable;
    public float Stuck;
    public bool Locked;
    public bool GaveUp;

    public TrackAxis(float lockDeadband) {
        this.lockDeadband = lockDeadband;
    }

    /// <summary>推进一步: err = 目标-实际, vel = 实际速度, pos = 实际位置. 返回修正量 (叠加到预测值).</summary>
    public float Step(float err, float vel, float pos) {
        // 套上并跟踪稳定: 误差+速度连续收住 5 帧 (0.2s) 才判定 (防瞬时过零)
        if (Mathf.Abs(err) <= lockDeadband && Mathf.Abs(vel) <= VelDeadband) {
            Stable += Dt;
            if (Stable >= 0.2f) Locked = true;
        }
        else { Stable = 0f; Locked = false; }
        // I: 16 帧误差窗口和
        _errs.Enqueue(err);
        if (_errs.Count > 16) _errs.Dequeue();
        float sum = 0f;
        foreach (var e in _errs) sum += e;
        // D: 误差差分 (死区内不计数)
        float d = err - _lastErr;
        if (Mathf.Abs(d) < DDeadband) d = 0f;
        _lastErr = err;
        // 无进展保护: 帧间几乎不动连续 10s → 放弃本轴 (慢速爬行不误判)
        if (!float.IsNaN(_lastPos) && Mathf.Abs(pos - _lastPos) < StuckStep) {
            Stuck += Dt;
            if (Stuck >= 10f) { GaveUp = true; Locked = true; }
        }
        else Stuck = 0f;
        _lastPos = pos;
        return Ki * sum + Kd * d;
    }
}
