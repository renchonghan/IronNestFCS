using System.Collections.Generic;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 单轴 TRAK 追踪器 (25fps 固定节拍, 单发/齐射共用): 天顶星伺服设 1 帧预测值 + 修正项.
/// I = 变积分 (|err| 超 iWindowOff 清窗不积分, iWindowFull 内全额, 之间线性衰减到 0) × ki × 16 帧误差窗口和 —
/// 大误差不进记忆, 消除超调; D = 两帧差分 (err - 前两帧 err, a4-a2) × kd 抑制震荡 —
/// 交替高频抖动互相抵消, 对真实斜率等效刹车加倍. 差分死区防量化噪声.
/// 每次调用 Step 推进一步, 返回修正量 (调用方叠加到预测值上作为机构设定).
/// 套上并跟踪稳定 (误差连续收住 0.5s, 不看速度 — 高速目标持续转动会永远不稳) → Locked;
/// 误差超死区但帧间几乎不动连续 10s → GaveUp (同时锁 Locked, 调用方不再追).
/// 误差在死区内不算卡 (套上跟稳静默正常, 远处慢目标帧间转角极小不误判).
/// 仰角 (E) 与方位 (H) 通用, 死区按轴实例化.
/// </summary>
public class TrackAxis {
    private float _lockDeadband;              // 锁定判定死区 (°) — 与修正死区独立; 动态口径 (弹道学: 落点偏移 ≤ 混凝土弹 R/5)
    public float LockDeadband { get => _lockDeadband; set => _lockDeadband = value; }
    private readonly float ctrlDeadband;      // 修正死区 (°): |err| 在此内不输出修正 (防量化抖动; 死区太宽 PID 总是偏一点)
    private readonly float iWindowFull;       // 变积分: 全额积分区 (|err| 上限), 按轴调 (E 0.1 / H 0.3)
    private readonly float iWindowOff;        // 变积分: 积分完全关闭区 (|err| 下限, ≥此值清窗), 按轴调 (E 0.5 / H 1)
    private readonly float kp;                // P 系数 (误差增益; 现传 0 — P 由调用方预测值直设承担)
    private readonly float ki;                // I 系数 (滑动窗口积分; 0.05)
    private readonly float kd;                // D 系数 (两帧差分), 按轴调 (E 5.2 防超调 / H 3.0)
    private readonly int bufferSize;          // I 误差窗长度 (帧)
    private const float DDeadband = 0.01f;    // D 项差分死区
    private const float StuckStep = 0.005f;   // 帧间位置变化 < 此值累计卡死 (0.125°/s)
    private const float Dt = 0.04f;           // 25fps

    private readonly Queue<float> _errs;
    private float _err1Ago = float.NaN;       // 上帧误差
    private float _err2Ago = float.NaN;       // 上上帧误差 (D 两帧差分)
    private float _lastPos = float.NaN;

    public float Stable;
    public float Stuck;
    public bool Locked;
    public bool GaveUp;
    public float Deadband => _lockDeadband; // 套上死区 (未选中炮 H 对位判定复用)

    public TrackAxis(float lockDeadband, float ctrlDeadband, float iWindowFull, float iWindowOff, float kp, float ki, float kd, int bufferSize) {
        this._lockDeadband = lockDeadband;
        this.ctrlDeadband = ctrlDeadband;
        this.iWindowFull = iWindowFull;
        this.iWindowOff = iWindowOff;
        this.kp = kp;
        this.ki = ki;
        this.kd = kd;
        this.bufferSize = bufferSize;
        _errs = new Queue<float>(bufferSize);
    }

    /// <summary>新任务开始: 清误差窗/历史/判定位 (GC 任务链换目标时调用).</summary>
    public void ResetForTask() {
        _errs.Clear();
        _err1Ago = _err2Ago = float.NaN;
        _lastPos = float.NaN;
        Stable = Stuck = 0f;
        Locked = GaveUp = false;
    }

    /// <summary>旧版 3 参兼容 (vel 忽略 — 速度不参与稳定判定; 旧 FSC 未清退前编译用).</summary>
    public float Step(float err, float vel, float pos) => Step(err, pos);

    /// <summary>推进一步: err = 目标-实际, pos = 实际位置 (卡死检测用). 返回修正量 (叠加到预测值).</summary>
    public float Step(float err, float pos) {
        // 套上并跟踪稳定: 误差连续收住 0.5s 才判定 (防瞬时过零);
        // 不看速度 — 高速运动目标炮持续转动, 速度恒超死区会永远不稳
        if (Mathf.Abs(err) <= _lockDeadband) {
            Stable += Dt;
            if (Stable >= 0.5f) Locked = true;
        }
        else { Stable = 0f; Locked = false; }
        // 修正死区 (与锁定判定独立): 误差太小不输出修正, 防量化抖动 (死区过宽 → 总偏一点)
        if (Mathf.Abs(err) <= ctrlDeadband) {
            _errs.Clear();
            return 0f;
        }
        // I: 变积分 — |err| 大时清窗不积分 (大误差不进记忆, 防收敛后旧记忆补踢一脚造成超调)
        float iTerm = 0f;
        float ae = Mathf.Abs(err);
        if (ae < iWindowOff) {
            float beta = ae <= iWindowFull ? 1f : 1f - (ae - iWindowFull) / (iWindowOff - iWindowFull);
            _errs.Enqueue(err);
            if (_errs.Count > bufferSize) _errs.Dequeue();
            float sum = 0f;
            foreach (var e in _errs) sum += e;
            iTerm = ki * beta * sum;
        }
        else _errs.Clear();
        // D: 两帧差分 (0.08s) — 交替高频抖动互相抵消, 真实斜率响应加倍;
        // 四帧窗口试过不好用 (刹车变钝), 回两帧; 抖动从 kd 取值压
        float d = float.IsNaN(_err2Ago) ? 0f : err - _err2Ago;
        if (Mathf.Abs(d) < DDeadband) d = 0f;
        _err2Ago = _err1Ago;
        _err1Ago = err;
        // 无进展保护: 误差超出死区 (伺服有活干) 但帧间几乎不动连续 10s → 放弃本轴.
        // 误差在死区内 = 已套上跟稳, 静默即正常 — 远处慢目标 (如列车) 角速度远低于阈值, 不能按位置变化量判卡
        if (Mathf.Abs(err) > _lockDeadband && !float.IsNaN(_lastPos) && Mathf.Abs(pos - _lastPos) < StuckStep) {
            Stuck += Dt;
            if (Stuck >= 10f) { GaveUp = true; Locked = true; }
        }
        else Stuck = 0f;
        _lastPos = pos;
        return kp * err + iTerm + kd * d;
    }
}
