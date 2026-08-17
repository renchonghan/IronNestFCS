using System.Collections.Generic;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 弹种数据: 从游戏 ShellDefinition 资产读取杀伤半径与速度倍率曲线.
/// 实测: 所有弹种 ShellSpeed=0.7 且速度曲线相同, 弹种只影响 ImpactRadius (km, 半径).
/// 曲线: c1=0.30 c2=0.3728 c3=0.5464 c4=0.7536 c5=0.9272 c6=1.0.
/// 射表公式: 仰角(度) = 距离(km) x 12 / 药包; 飞行时间(s) = 仰角 / (1.4 x 倍率).
/// </summary>
public static class ShellData {
    private static readonly Dictionary<string, float> _killRadius = new();
    private static AnimationCurve? _speedCurve;

    /// <summary>扫描场景里所有 ShellDefinition 资产, 建立 ShellId -> 杀伤半径映射, 并缓存速度曲线.</summary>
    public static void Init() {
        _killRadius.Clear();
        _speedCurve = null;
        var flags = Il2CppSystem.Reflection.BindingFlags.Public
            | Il2CppSystem.Reflection.BindingFlags.NonPublic
            | Il2CppSystem.Reflection.BindingFlags.Instance;
        foreach (var so in Resources.FindObjectsOfTypeAll<ScriptableObject>()) {
            if (so == null) continue;
            if (so.GetIl2CppType().Name != "ShellDefinition") continue;
            try {
                string? shellId = null;
                float radius = -1f;
                foreach (var f in so.GetIl2CppType().GetFields(flags)) {
                    try {
                        if (f.Name == "ShellId") shellId = f.GetValue(so)?.ToString()?.Trim();
                        else if (f.Name == "ImpactRadius") radius = f.GetValue(so).Unbox<float>();
                        else if (f.Name == "chargeToSpeedMultiplier" && _speedCurve == null) {
                            _speedCurve = new AnimationCurve(f.GetValue(so).Pointer);
                        }
                    }
                    catch { }
                }
                if (shellId != null && radius >= 0f) _killRadius[shellId] = radius;
            }
            catch { }
        }
        MelonLogger.Msg($"[FCS] ShellData: {_killRadius.Count} shells, speedCurve={_speedCurve != null}");
    }

    /// <summary>杀伤半径 (km, 游戏 ImpactRadius 原值). 未知弹种回落 0.625 km.</summary>
    public static float KillRadiusKm(BulletType bt) {
        var id = bt.ToString();
        if (_killRadius.TryGetValue(id, out var r)) return r;
        if (_killRadius.TryGetValue(id.Replace("PCLM", "PLCM"), out r)) return r; // 游戏侧 PCLM 叫 PLCM
        return 0.625f;
    }

    /// <summary>速度倍率 (游戏 chargeToSpeedMultiplier 曲线). 曲线缺失回落 1.</summary>
    public static float SpeedMult(int charge) => _speedCurve != null ? _speedCurve.Evaluate(charge) : 1f;

    /// <summary>仰角 (度) = 距离 (km) x 12 / 药包. 与游戏计算器输出一致 (射表).</summary>
    public static float ElevationDeg(float distanceKm, int charge) => distanceKm * 12f / Mathf.Max(1, charge);

    /// <summary>
    /// 飞行时间 (s) = 距离 (km) x 10/7 / 速度倍率.
    /// 实测拟合: 各药包实测系数 D(c)=1.4 x mult(c) x 6/c 全部命中 (c1 2.52 / c2 1.57 / c3 1.53 /
    /// c4 1.58 / c5 1.56 / c6 1.40); 联立仰角 = 12d/c 即得本式. 6 包时退化为 d x 10/7.
    /// </summary>
    public static float FlightTime(float distanceKm, int charge) => distanceKm * (10f / 7f) / SpeedMult(Mathf.Max(1, charge));
}
