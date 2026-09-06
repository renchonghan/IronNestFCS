using Il2Cpp;
using IronNestFCS.Logic.FCS;
using MelonLoader;

namespace IronNestFCS.Logic;

public enum LeftRight {
    Left,
    Right,
}

/// <summary>
/// 硬件绑定壳 (2.0): 只做游戏对象查找与绑定 (炮 / 炮塔 / 击发确认台 / 弹道计算台), 不碰调度与渲染 —
/// 那些全在 RD/DC/FC/GC 四模块. 1.x 的调度/沙盘标记/场景交互/旧雷达层已清退 (2026-09-06).
/// </summary>
public class FSC
{
    public readonly BallisticCalculator BallisticCalculator = new();
    public readonly GunSystem LeftGun = new();
    public readonly GunSystem RightGun = new();
    public readonly Turret Turret = new();
    public readonly TriggerConsole TriggerConsole = new();

    public bool IsBound { get; private set; }

    /// <summary>查找并绑定游戏对象. 返回 false 表示当前场景还没有目标控件.</summary>
    public bool TryBind()
    {
        IsBound = BallisticCalculator.TryBind()
                  && LeftGun.TryBind("Left")
                  && RightGun.TryBind("Right")
                  && Turret.TryBind()
                  && TriggerConsole.TryBind();
        MelonLogger.Msg("[FCS] Initialize: " + (IsBound ? "success" : "failed"));
        return IsBound;
    }

    /// <summary>释放: 硬件引用随 ALC 卸载回收 (无协程无补丁, 无需显式清理).</summary>
    public void Dispose()
    {
    }
}
