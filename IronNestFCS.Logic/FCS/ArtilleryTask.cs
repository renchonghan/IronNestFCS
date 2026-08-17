using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public enum Progress {
    Pending,
    Calculating,
    SelectingBullet,
    DumpingWrongShell,
    LoadingBullet,
    RammingBullet,
    LoadingPowder,
    WaitLoading,
    ConfirmingCharge,
    Aiming,
    AimingAzimuth,
    WaitingForFire,
    Fire,
    BackToIdle,
    Finished,
    Failed,
}

/// <summary>完成队列条目: 击发时刻的快照, T:- 为该发的实时剩余飞行时间 (impactTime 在 task 上锁存).</summary>
public class FinishedTask {
    public ArtilleryTask task = null!;
    public float fireTime; // Time.time 击发时刻
}

public class ArtilleryTask {
    /// <summary>火控 UID: 首次入队时递增分配, 不显示, 仅日志追溯用 (重试/插队保持原号).</summary>
    public int fireControlId;
    public int targetId;
    public float angel;
    public float distance;
    public Vector3 position;
    public BulletType bulletType;
    /// <summary>齐射跟随炮任务: 右键二次升级时与主任务一同入队, 面板该行显示 >>>[SALVO].</summary>
    public bool salvoFollower;
    /// <summary>齐射主任务引用 (仅跟随任务持有), 取消时两个任务一起出队.</summary>
    public ArtilleryTask? salvoLeader;
    /// <summary>火控解总飞行时间: WaitForFire 击发前从炮兵计时器拷贝, 未拷贝时为 0.</summary>
    public float impactTime;
    /// <summary>击发时刻 (Time.time), 0 = 未击发. 地图菱形框下方的落点计时器用.</summary>
    public float fireTime;
    /// <summary>弹道计算器真实输出的仰角, 解算后快照 (计算器全局唯一, 必须按任务快照).</summary>
    public float calculatedElevation;
    /// <summary>本轮实际用于计算的装药量 (实装足量按实装, 否则按需求, 退弹轮为已装或 1).</summary>
    public int charge;
    public Progress progress;
    /// <summary>被 AbortGun 放回队首重试的次数, 防止失败任务无限循环(每次重试都会重新采购/重新解算).</summary>
    public int abortCount;
}