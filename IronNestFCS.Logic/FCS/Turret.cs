using System.Collections;
using System.Reflection;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public class Turret {
    private TurretController? _turret;
    private PropertyInfo? _currentAngleProp; // 实际方位角属性, TryBind 时反射定位


    public bool TryBind() {
        var turretObj = GameObject.Find("TurretSystem");
        if (turretObj == null) {
            MelonLogger.Error("[FCS] Aiming: Can't find TurretSystem");
            return false;
        }
        _turret = turretObj.GetComponent<TurretController>();
        // 实际方位角: 优先 CurrentAngleCompass (与 task.angel 同号), 找不到再退回原始 CurrentAngle
        // 注意不能用同一个循环找两个名字: CurrentAngle 在元数据里排在前面会先被命中
        foreach (var p in _turret.GetType().GetProperties()) {
            if (p.Name == "CurrentAngleCompass") { _currentAngleProp = p; break; }
        }
        if (_currentAngleProp == null) {
            foreach (var p in _turret.GetType().GetProperties()) {
                if (p.Name == "CurrentAngle") { _currentAngleProp = p; break; }
            }
        }
        MelonLogger.Msg($"[FCS] Turret: actual angle via {_currentAngleProp?.Name ?? "none"}");
        return true;
    }

    /// <summary>实际方位角 (找不到属性时返回 NaN, 面板显示横线).</summary>
    public float CurrentAngle() {
        if (_currentAngleProp == null || _turret == null) return float.NaN;
        try { return Convert.ToSingle(_currentAngleProp.GetValue(_turret)); }
        catch { return float.NaN; }
    }

    /// <summary>炮塔旋转速度反馈 (TRAK 双环速度环用, 未绑定时返回 0).</summary>
    public float RotationVelocity() {
        return _turret == null ? 0f : _turret.rotationVelocity;
    }
    
    /// <summary>只设炮塔目标方位一次, 不等待到位 (TRAK PID 追踪循环 25fps 调用).</summary>
    public void SetDesiredRotation(float angle) {
        if (_turret == null) {
            MelonLogger.Error("[FCS] Aiming: unbound TurretController");
            return;
        }
        _turret.DesiredRotation = -angle;
    }

    public IEnumerator SetRotation(float angle) {
        if (_turret == null) {
            MelonLogger.Error("[FCS] Aiming: unbound TurretController");
            yield break;
        }

        _turret.DesiredRotation = -angle;
        yield return new WaitForSeconds(1f);
        while (_turret.rotationVelocity != 0) {
            yield return new WaitForSeconds(1f);
        }
    }
    
}