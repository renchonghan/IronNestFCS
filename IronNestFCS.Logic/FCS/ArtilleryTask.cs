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

public class ArtilleryTask {
    public int targetId;
    public float angel;
    public float distance;
    public Vector3 position;
    public BulletType bulletType;
    /// <summary>火控解总飞行时间: WaitForFire 击发前从炮兵计时器拷贝, 未拷贝时为 0.</summary>
    public float impactTime;
    /// <summary>弹道计算器真实输出的仰角, 解算后快照 (计算器全局唯一, 必须按任务快照).</summary>
    public float calculatedElevation;
    /// <summary>本轮实际用于计算的装药量 (实装足量按实装, 否则按需求, 退弹轮为已装或 1).</summary>
    public int charge;
    public Progress progress;
    /// <summary>被 AbortGun 放回队首重试的次数, 防止失败任务无限循环(每次重试都会重新采购/重新解算).</summary>
    public int abortCount;
}