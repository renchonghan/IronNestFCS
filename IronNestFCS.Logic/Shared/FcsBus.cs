using System;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 2.0 模块端口总线 (迁移期): 全局唯一硬件的读写口.
/// 炮塔由 FC 直控 (目标方位), 执行器 (GC) 只经此口读实际方位/角速度并在被选中时写 TRAK 修正后的设定;
/// 击发钮全局一个 (统一火控), DUMP 平射例外也经此口.
/// FC 初始化时装填, GC/DC 只使用. 热重载时 FcsModule 重装填.
/// </summary>
public static class FcsBus {
    /// <summary>实际方位回读 (°), 未装填返回 NaN.</summary>
    public static Func<float>? TurretRead;
    /// <summary>方位角速度 (°/s), 未装填返回 0.</summary>
    public static Func<float>? TurretVelRead;
    /// <summary>设定炮塔目标方位 (TRAK 修正后值).</summary>
    public static Action<float>? TurretSet;
    /// <summary>击发钮 (统一火控 / DUMP 平射).</summary>
    public static Action? Fire;
}
