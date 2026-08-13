using UnityEngine;

namespace IronNestFCS.Logic.FCS;

public enum Progress {
    Pending,
    Calculating,
    SelectingBullet,
    DumpingWrongShell,
    LoadingBullet,
    LoadingPowder,
    WaitLoading,
    Aiming,
    AimingAzimuth,
    WaitingForFire,
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
    public Progress progress;
    /// <summary>被 AbortGun 放回队首重试的次数, 防止失败任务无限循环(每次重试都会重新采购/重新解算).</summary>
    public int abortCount;
}