using System.Collections;
using System.Reflection;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;


public enum BulletType {
    AP = 1,
    APHE = 2,
    ATMC = 3,
    CLMN = 4,
    CYAN = 5,
    DRIL = 6,
    EQKE = 7,
    FLCH = 8,
    HCHE = 9,
    HE = 10,
    INCN = 11,
    LE = 12,
    PCLM = 13,
    PHGN = 14,
    PRPG = 15,
    SMK = 16,
    STAR = 17,
    TEAR = 18,
    THRM = 19,
    WP = 20,
}

public class GunSystem {
    private const float MinimumPostShotRecoverySeconds = 13f;

    private string _surfix = "";

    private CylinderShellSelector? shellSelector;
    
    private List<string?> bullets = new();
    private LookAtTarget? nextBulletButton;
    private LookAtTarget? loadBulletButton;
    private List<LookAtTarget> powderButtons = new();
    private LookAtTarget? loadPowderButton;
    private GunController? gunController;
    private ArtilleryReloadController? reloadController;
    private LinearSliderInteractable? elevationLever;
    private OdometerDisplay? remainingCharges;
    private OdometerDisplay? selectedCharges;

    private TextMeshPro shellId = null!; // 绑定后非空 (TryBind 失败不读)

    public bool TryBind(string surfix) {
        this._surfix = surfix;
        
        var gunSystem = GameObject.Find("Gun System " + surfix).transform;
        var reloadingConsole = gunSystem.Find("--Reloading Console");
        if (reloadingConsole == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find --Reloading Console");
            return false;
        }

        remainingCharges = reloadingConsole.GetComponentInChildren<OdometerDisplay>();
        // 实拉药包杆数 (P3 就可见, PowderCharges 要到 P4 后才有值)
        // 该里程表挂在 "Calculated Charge Display (1)" (实际装药量表) 下, 不在装填控制台里
        // Transform.Find/FindChild 只查直接子级, 这里用递归查找
        selectedCharges = FindChildDeep(gunSystem, "Odomiter Counter Selected Charges")?.GetComponent<OdometerDisplay>();
        MelonLogger.Msg($"[FCS] GunSystem {surfix}: selectedCharges bound={selectedCharges != null}, " + $"value={selectedCharges?.CurrentNumber}"); // 绑定失败时读数恒 0, 这条日志留着排查用
        
        nextBulletButton = 
            reloadingConsole.Find("Universal Button Move Cylinder")
                .GetComponent<LookAtTarget>();    
        shellSelector = gunSystem.GetComponentInChildren<CylinderShellSelector>();
        
        shellId = GameObject.Find("Shell ID " + surfix)
            .GetComponent<TextMeshPro>();
        var loadShell = reloadingConsole.FindChild("Universal Button Load shell Rammer");
        if (loadShell == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find Universal Button Load shell Rammer");
            return false;
        }
        loadBulletButton = loadShell.GetComponent<LookAtTarget>();

        var powderController = reloadingConsole.Find("PowderChargeController");
        for (var i = 0; i < powderController.childCount; ++i) {
            var child = powderController.GetChild(i);
            if (!child.name.StartsWith("Button Dispencer")) continue;
            var button = child.GetComponent<LookAtTarget>();
            if (button == null) {
                MelonLogger.Error($"[FCS] GunSystem {surfix}: Found {child.name} but lack of LookAtTarget Component");
                return false;
            }
            powderButtons.Add(button);
        }

        loadPowderButton = reloadingConsole.FindChild("Universal Button Charge Rammer (1)").GetComponent<LookAtTarget>();
        gunController = GameObject.Find("Gun"+surfix).GetComponent<GunController>();
        reloadController = gunController?.artilleryReloadController;
        BindStopwatch();
        elevationLever = GameObject.Find(".Elevation Lever Baseplate")?.transform.FindChild(".Elevation Lever " + surfix).GetComponent<LinearSliderInteractable>();
        return true;
    }

    public bool CanFire() {
        return gunController != null && gunController.CanFire;
    }

    /// <summary>深度优先递归按名字找子物体 (Transform.Find/FindChild 只查直接子级).</summary>
    private static Transform? FindChildDeep(Transform root, string name) {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++) {
            var hit = FindChildDeep(root.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>只设仰角杆值一次, 不等待到位 (TRAK PID 追踪循环 25fps 调用).</summary>
    public void SetElevationValue(float elevation) {
        if (elevationLever == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Elevation lever unbound");
            return;
        }
        elevationLever.SetSliderValue(elevation);
    }

    public IEnumerator SetElevation(float elevation) {
        if (elevationLever == null || gunController == null) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Elevation lever or gun controller unbound");
            yield break;
        }
        elevationLever.SetSliderValue(elevation);
        yield return new WaitForSeconds(0.1f);
        // 无进展检测: 慢速瞄准(仰角一直在动)可以等任意久; 只有连续一段时间
        // 仰角几乎无变化(判定卡死)才放弃, 避免无限等待与后续超时误杀
        var lastElevation = gunController.CurrentElevation;
        var stagnantFor = 0f;
        const float stagnationThreshold = 10f; // 秒
        const float progressEpsilon = 0.2f;    // 度
        while (!Mathf.Approximately(gunController.CurrentElevation, elevation)) {
            elevationLever.SetSliderValue(elevation);
            yield return new WaitForSeconds(1f);
            var current = gunController.CurrentElevation;
            if (Mathf.Abs(current - lastElevation) < progressEpsilon) {
                stagnantFor += 1f;
                if (stagnantFor >= stagnationThreshold) {
                    MelonLogger.Error($"[FCS] GunSystem {_surfix}: 升仰角无进展 {stagnantFor:F0}s, " +
                                      $"当前 {current:F2}° 目标 {elevation:F2}°, 放弃本次瞄准。");
                    yield break;
                }
            }
            else {
                stagnantFor = 0f;
            }
            lastElevation = current;
        }
    }
    
    public string? BulletInChamber() {
        return gunController?.ChamberedShellBlueprint?.shellDefinition?.ShellId.Replace("PLCM", "PCLM");
    }
    
    public bool IsChamberEmpty() {
        return BulletInChamber() == null;
    }

    private void RefreshBullets() {
        bullets.Clear();
        if (shellSelector == null) return;
        foreach (var shell in shellSelector.bullets) {
            bullets.Add(shell?.GetComponent<ShellBlueprint>()?.shellDefinition?.ShellId.Replace("PLCM", "PCLM"));
        }
        MelonLogger.Msg($"[FCS] GunSystem {_surfix}: Cylinder bullets: {string.Join(", ", bullets)}");
    }

    public void NextBullet() {
        if (nextBulletButton == null) return;
        nextBulletButton.OnClickDown();
    }
    
    /// <summary>
    /// 装填机构就绪等待: 正式版的装填状态索引是数据驱动的, 空闲时可能处于不同的 CurrentStateIndex
    /// 因此不把某个固定索引当作"可装填". 只依据控制器实际的 working, 炮闩锁定和炮管运动状态判断
    /// (TRAK 开始前也用它等机构停稳 — 装填完炮管还有回落动作, 不等就追会鬼畜).
    /// </summary>
    public IEnumerator WaitForReloadReady() {
        while (gunController != null) {
            var mechanismReady = reloadController == null || !reloadController.working;
            var breechReady = !gunController.ExternalReloadLoweringLocked;
            var motionReady = gunController.elevationChangeVelocity == 0;
            if (mechanismReady && breechReady && motionReady)
                yield break;

            yield return new WaitForSeconds(0.1f);
        }
    }

    /// <summary>
    /// 装填指定弹种: 先把弹仓转到目标弹, 再按装填. 转弹仓每步之间要等 1 秒
    /// (游戏有转动动画/物理). 返回 IEnumerator, 调用方用 yield return 等待它跑完
    /// 必须走协程而非 async: continuation 要留在主线程才能安全访问 IL2CPP 对象
    /// </summary>
    /// <summary>转弹仓直到目标弹种位于待装位 (面板 1-2 SELC 阶段).</summary>
    public IEnumerator RotateCylinderTo(BulletType type) {
        // 上一发的退壳/炮闩/复位机构可能仍在工作. 先等待真实机构状态空闲
        // 再开始下一轮弹仓操作, 避免连续射击时过早点击后续控件
        yield return WaitForReloadReady();

        RefreshBullets();
        if (!bullets.Contains(type.ToString())) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: " +
                              $"No {type} available in cylinder, current bullets: {string.Join(", ", bullets)}");
            yield break;
        }

        for (var i = 0; i < bullets.Count; ++i) {
            if (bullets[0] == type.ToString()) {
                break;
            };
            NextBullet();
            yield return new WaitForSeconds(1.5f);
            RefreshBullets();
        }
        if (bullets[0] != type.ToString()) {
            MelonLogger.Error($"[FCS] GunSystem {_surfix}: Can't find {type} after rotation, " +
                              $"current: {string.Join(", ", bullets)}");
        }
    }

    /// <summary>按推弹按钮把炮弹送上推弹架 (面板 2-1 DLRD 阶段).</summary>
    public IEnumerator PressRammer() {
        yield return WaitForReloadReady();
        yield return UiClick.WaitAndClick(loadBulletButton!);
    }

    /// <summary>按完推弹按钮后等推弹机启动 (装填状态机进入 ShellRamming), 10 秒兜底.</summary>
    public IEnumerator WaitRammingStart() {
        float waited = 0f;
        while (waited < 10f) {
            var key = reloadController?.CurrentState?.stateKey;
            if (key == "ShellRamming") yield break;
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;
        }
        MelonLogger.Msg($"[FCS] GunSystem {_surfix}: WaitRammingStart timeout, proceed");
    }

    private IEnumerator SelectPowder(int count) {
        for (var i = 0; i < count; i++) {
            // 装药按钮引用可能因 reload 重建而失效, 失效时重新扫描绑定
            if (i >= powderButtons.Count || powderButtons[i] == null || powderButtons[i].gameObject == null) {
                RefreshPowderButtons();
                if (i >= powderButtons.Count || powderButtons[i] == null) {
                    MelonLogger.Error($"[GunSystem] SelectPowder: button {i} invalid after refresh");
                    yield break;
                }
            }
            yield return UiClick.WaitAndClick(powderButtons[i]);
        }
    }

    /// <summary>重新扫描装药按钮(PowderChargeController 下的 Button Dispencer)</summary>
    private void RefreshPowderButtons() {
        powderButtons.Clear();
        var gunSystem = GameObject.Find("Gun System " + _surfix)?.transform;
        var reloadingConsole = gunSystem?.Find("--Reloading Console");
        var powderController = reloadingConsole?.Find("PowderChargeController");
        if (powderController == null) return;
        for (var i = 0; i < powderController.childCount; ++i) {
            var child = powderController.GetChild(i);
            if (!child.name.StartsWith("Button Dispencer")) continue;
            var button = child.GetComponent<LookAtTarget>();
            if (button != null) powderButtons.Add(button);
        }
    }

    /// <summary>
    /// 等推弹机把炮弹推进到位: 游戏装填状态机离开 ShellRamming 状态 (P2 炮弹已推入).
    /// 必须先观察到 ShellRamming 才算数, 避免点击后机构尚未启动就误判完成;
    /// 兜底: 膛内出现炮弹也算完成 (机构动作极快、错过 ShellRamming 轮询时用); 30 秒兜底.
    /// </summary>
    public IEnumerator WaitShellRammed() {
        bool seenRamming = false;
        float waited = 0f;
        while (waited < 30f) {
            var key = reloadController?.CurrentState?.stateKey;
            if (key == "ShellRamming") {
                seenRamming = true;
            }
            else if (seenRamming || BulletInChamber() != null) {
                yield break;
            }
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;
        }
        MelonLogger.Msg($"[FCS] GunSystem {_surfix}: WaitShellRammed timeout, proceed");
    }

    /// <summary>拉指定数量的药包杆 (PWDR 补拉用, 按钮逐个带 9s 超时).</summary>
    public IEnumerator PullPowders(int count) {
        yield return SelectPowder(count);
    }

    /// <summary>按推药按钮 (P4 推药入膛). 先等游戏机构停稳 (状态确认, 与 PressRammer 同款),
    /// 避免拉杆/机构动作中按推药钮不激活 (Charge Rammer 9s 超时的根因).</summary>
    public IEnumerator RamPowder() {
        yield return WaitForReloadReady();
        // 推药杆引用可能因 reload 重建而失效, 重新绑定
        if (loadPowderButton == null || loadPowderButton.gameObject == null) {
            var gunSystem = GameObject.Find("Gun System " + _surfix)?.transform;
            var reloadingConsole = gunSystem?.Find("--Reloading Console");
            loadPowderButton = reloadingConsole?.FindChild("Universal Button Charge Rammer (1)")
                ?.GetComponent<LookAtTarget>();
            if (loadPowderButton == null) {
                MelonLogger.Error($"[GunSystem] RamPowder: rammer button missing");
                yield break;
            }
        }
        yield return UiClick.WaitAndClick(loadPowderButton);
    }

    public bool HaveBulletInCylinder(BulletType type) {
        RefreshBullets();
        return bullets.Contains(type.ToString());
    }
    
    public bool HaveEmptyShellInCylinder() {
        RefreshBullets();
        return bullets.Contains(null);
    }

    public IEnumerator WaitBackToIdle() {
        // 保留原来的 13 秒最小恢复窗口, 但同时要求正式版装填机构真正结束工作
        // 这样下一任务不会只因为炮管停止运动就过早进入装填
        var minimumRecoveryUntil = Time.realtimeSinceStartup + MinimumPostShotRecoverySeconds;
        while (gunController != null) {
            var minimumDelayDone = Time.realtimeSinceStartup >= minimumRecoveryUntil;
            var mechanismReady = reloadController == null || !reloadController.working;
            var breechReady = !gunController.ExternalReloadLoweringLocked;
            var motionReady = gunController.elevationChangeVelocity == 0;
            if (minimumDelayDone && mechanismReady && breechReady && motionReady)
                yield break;

            yield return new WaitForSeconds(0.1f);
        }
    }

    public IEnumerator WaitFire() {
        while (gunController != null && !gunController.pendingReload) {
            yield return new WaitForSeconds(0.1f);
        }
    }

    /// <summary>击发完成标志 (pendingReload 已置位, TRAK 手动模式检测玩家击发用).</summary>
    public bool HasFired() {
        return gunController != null && gunController.pendingReload;
    }
    
    public int RemainingCharges() {
        return remainingCharges != null ? (int)remainingCharges.CurrentNumber : 0;
    }

    /// <summary>实装药包数 (P4 推入确认后生效, 之前为 0). 弹种保护状态机的关键输入.</summary>
    public int LoadedPowderCharges() {
        return gunController == null ? 0 : gunController.PowderCharges;
    }

    /// <summary>实拉药包杆数 (Selected Charges 里程表, P3 阶段就可见, 用于 PWDR 补拉/超量判定).</summary>
    public int SelectedPowderCharges() {
        return selectedCharges == null ? 0 : (int)selectedCharges.CurrentNumber;
    }

    /// <summary>推弹机是否正在把炮弹推入 (装填状态机 ShellRamming). 检查点用: 此时膛内读数不可信.</summary>
    public bool IsShellRamming() {
        return reloadController?.CurrentState?.stateKey == "ShellRamming";
    }

    /// <summary>游戏装填状态机当前状态码 (HUD 相位显示 / COFM 实装确认用).</summary>
    public string? ReloadStateKey() {
        try { return reloadController?.CurrentState?.stateKey; } catch { return null; }
    }

    /// <summary>装填状态码序判定: 当前码是否已达/超过目标码 (码已过时不盲等, 实机抓的完整序).</summary>
    public bool ReloadStateAtOrAfter(string target) {
        string[] order = { "GuideDeploy", "BreechOpen", "ShellRamming", "SelectPowderCharge", "RamCharges", "CloseShellGuide", "FinalSequence", "BreachLocked" };
        var key = ReloadStateKey();
        int iK = key == null ? -1 : System.Array.IndexOf(order, key);
        return iK >= System.Array.IndexOf(order, target);
    }

    /// <summary>等装填状态机进入目标码 (状态确认替代盲等, 20s 兜底).</summary>
    public IEnumerator WaitReloadState(string key, float timeout = 20f) {
        float waited = 0f;
        while (waited < timeout) {
            if (ReloadStateKey() == key) yield break;
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;
        }
        MelonLogger.Msg($"[FCS] GunSystem {_surfix}: WaitReloadState '{key}' timeout (now '{ReloadStateKey()}')");
    }

    /// <summary>炮弹预计飞行时间 (游戏 GunController 弹道预测, 未解算时为 0).</summary>
    public float PredictedImpactTime() {
        return gunController == null ? 0f : gunController.PredictedImpactTime;
    }

    /// <summary>炮管实际仰角 (面板行 1 用, 未绑定时返回 NaN).</summary>
    public float ActualElevation() {
        return gunController == null ? float.NaN : gunController.CurrentElevation;
    }

    /// <summary>仰角速度反馈 (TRAK 双环速度环用, 未绑定时返回 0).</summary>
    public float ElevationVelocity() {
        return gunController == null ? 0f : gunController.elevationChangeVelocity;
    }

    /// <summary>
    /// 炮兵计时表 (Gun Watch 上的 GunStopwatch): 击发后游戏自动倒计时的读数源.
    /// watchedGun 指向对应炮, 找到与 gunController 匹配的那块表.
    /// </summary>
    private GunStopwatch? _stopwatch;

    private void BindStopwatch() {
        _stopwatch = null;
        if (gunController == null) return;
        foreach (var sw in Resources.FindObjectsOfTypeAll<GunStopwatch>()) {
            if (sw == null || sw.watchedGun != gunController) continue;
            _stopwatch = sw;
            MelonLogger.Msg($"[FCS] GunSystem {_surfix}: bound GunStopwatch on {sw.gameObject.name}");
            break;
        }
        if (_stopwatch == null) MelonLogger.Error($"[FCS] GunSystem {_surfix}: GunStopwatch not found");
    }

    /// <summary>
    /// 游戏炮兵计时表读数: 倒计时中返回剩余秒数; 未倒计时但膛内有弹 (瞄准阶段)
    /// 返回表针当前的预计飞行时间; 膛空/落地后返回 NaN (面板显示横线).
    /// </summary>
    /// <summary>纯倒计时剩余秒数 (仅 CountingDown 状态, 否则 NaN). 行 2 T:- 的数据源.</summary>
    public float CountdownRemainingSeconds() {
        if (_stopwatch == null || _stopwatch.state != "CountingDown") return float.NaN;
        return _stopwatch.previousCountingDownRemainingSeconds;
    }

    /// <summary>击发后取炮兵计时表真值: (倒计时起点, 闩锁总飞行时间). 未倒计时/表未绑返回 null.</summary>
    public (float startTime, float travelTime)? StopwatchLatch() {
        if (_stopwatch == null || _stopwatch.state != "CountingDown") return null;
        return ((float)_stopwatch.countdownStartTime, _stopwatch.latchedTravelTime);
    }

    public float RemainingFlightSeconds() {
        if (_stopwatch == null) return float.NaN;
        if (_stopwatch.state == "CountingDown") return _stopwatch.previousCountingDownRemainingSeconds;
        // 瞄准阶段: 读活变量 PredictedImpactTime (抬炮实时更新), 击发后才由计时表倒数
        if (!string.IsNullOrEmpty(BulletInChamber()) && gunController != null && gunController.PredictedImpactTime > 0.01f)
            return gunController.PredictedImpactTime;
        return float.NaN;
    }

    private float _lastFireTime = -1f;   // Time.time, 击发时刻快照
    private float _lastFlightTime = -1f; // 击发时的预计飞行时间快照

    /// <summary>击发确认后由 FSC 调用, 记录开火快照, 供面板显示剩余飞行时间.</summary>
    public void RecordFire(float flightTime) {
        _lastFireTime = Time.time;
        _lastFlightTime = flightTime;
    }

    /// <summary>剩余飞行时间: 已击发 = 快照飞行时间 - 已飞时长; 未击发 = 当前预计飞行时间.</summary>
    public float RemainingFlightTime() {
        if (_lastFireTime >= 0f && _lastFlightTime > 0.01f) {
            return Math.Max(0f, _lastFlightTime - (Time.time - _lastFireTime));
        }
        return PredictedImpactTime();
    }

    /// <summary>炮管当前状态快照(用于智能跳过装填与面板显示)</summary>
    public struct GunState {
        public string? ChamberedShell;
        public bool CanFire;
        public bool PendingReload;
        public float ElevationVelocity;
        public float CurrentElevation;
        public int ChargesRemaining;
        public string[] CylinderBullets;
    }

    public GunState GetState() {
        RefreshBullets();
        return new GunState {
            ChamberedShell = BulletInChamber(),
            CanFire = gunController != null && gunController.CanFire,
            PendingReload = gunController != null && gunController.pendingReload,
            ElevationVelocity = gunController != null ? gunController.elevationChangeVelocity : 0f,
            CurrentElevation = gunController != null ? gunController.CurrentElevation : 0f,
            ChargesRemaining = RemainingCharges(),
            CylinderBullets = bullets.Where(b => b != null).ToArray()!
        };
    }

}
