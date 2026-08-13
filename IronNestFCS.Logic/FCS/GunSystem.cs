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

    private TextMeshPro shellId;

    public bool TryBind(string surfix) {
        this._surfix = surfix;
        
        var gunSystem = GameObject.Find("Gun System " + surfix).transform;
        var reloadingConsole = gunSystem.Find("--Reloading Console");
        if (reloadingConsole == null) {
            MelonLogger.Error($"[FCS] GunSystem {surfix}: Can't find --Reloading Console");
            return false;
        }

        remainingCharges = reloadingConsole.GetComponentInChildren<OdometerDisplay>();
        
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
        DebugDumpReloadState(); // [临时调试] 定位 4 相位灯与实装药包指示器, 用完注释掉本行
        // ProbeFlightTimer();     // [临时调试] 已找到读数源: GunStopwatch.previousCountingDownRemainingSeconds. 备用
        // ProbeArtilleryTimer();  // [临时调试] 已确认: 炮兵计时器就是 GunController.PredictedImpactTime, 实时倒数. 备用
        return true;
    }

    /// <summary>
    /// [临时调试] 递归枚举炮管与装填控制台的完整结构 (每个物体的组件与读数),
    /// 并反射 dump GunController / ArtilleryReloadController 的字段, 用于:
    /// 1. 定位每炮 4 个相位灯 (P1 炮弹就绪 / P2 炮弹已推入 / P3 药包就绪 / P4 药包推入);
    /// 2. 定位"实装药包数"指示器 (推进去几个显示几个的建模);
    /// 3. 找装填状态索引 / 已装药包数的直接字段.
    /// 用完后注释掉 TryBind 末尾的调用即可, 方法保留备用.
    /// </summary>
    private void DebugDumpReloadState() {
        MelonLogger.Msg($"[FCS_DEBUG] ===== GunSystem {_surfix} reload dump =====");
        var gunSystem = GameObject.Find("Gun System " + _surfix)?.transform;
        if (gunSystem != null) {
            DumpChildrenRecursive(gunSystem, 0);
        }
        DumpReloadStates();
        DumpFields(gunController, "GunController");
        DumpFields(reloadController, "ArtilleryReloadController");
        MelonLogger.Msg($"[FCS_DEBUG] ===== end {_surfix} =====");
    }

    /// <summary>
    /// 递归 dump 层级时用 TryCast (按 Il2Cpp 真实类指针判定) 识别组件类型:
    /// GetComponents&lt;Component&gt;() 会把元素按基类封箱, `is` 判断全部失效,
    /// 但 TryCast 走原生类检查, 命中后再解包读取数值即可.
    /// </summary>
    private static void DumpChildrenRecursive(Transform t, int depth) {
        var go = t.gameObject;
        var parts = new List<string>();
        foreach (var c in go.GetComponents<Component>()) {
            if (c is Transform) continue; // 不刷屏
            try {
                parts.Add($"Odometer={c.Cast<OdometerDisplay>().CurrentNumber}");
                continue;
            }
            catch { }
            try {
                parts.Add($"TMP=\"{c.Cast<TextMeshPro>().text}\"");
                continue;
            }
            catch { }
            try {
                var r = c.Cast<Renderer>();
                try { parts.Add($"Renderer(mat={r.material?.name}, emis={r.material?.GetColor("_EmissionColor")})"); }
                catch { parts.Add($"Renderer(mat={r.material?.name})"); }
                continue;
            }
            catch { }
            try {
                parts.Add($"Light(on={c.Cast<Light>().enabled})");
                continue;
            }
            catch { }
            parts.Add(TrueTypeName(c));
        }
        MelonLogger.Msg($"[FCS_DEBUG] {new string(' ', depth * 2)}{go.name} active={go.activeSelf} [{string.Join(", ", parts)}]");
        // FMOD / LOD / SFX 子树纯噪音, 只记一行不再下钻
        if (go.name.StartsWith("FMOD") || go.name.StartsWith("LOD") || go.name.StartsWith("SFX")) return;
        for (int i = 0; i < t.childCount; i++) {
            DumpChildrenRecursive(t.GetChild(i), depth + 1);
        }
    }

    /// <summary>拿组件被基类封箱前的真实 Il2Cpp 类型名 (GetIl2CppType 读原生类指针).</summary>
    private static string TrueTypeName(Component c) {
        try {
            var il2 = c.GetIl2CppType();
            return il2?.Name ?? c.GetType().Name;
        }
        catch { return c.GetType().Name; }
    }

    /// <summary>dump 装填状态定义表 (ReloadStateDef 列表) + 当前状态明细, 用于建立 index -> phase 映射.</summary>
    private void DumpReloadStates() {
        if (reloadController == null) return;
        var states = reloadController.reloadStates;
        MelonLogger.Msg($"[FCS_DEBUG] --- reloadStates count={states?.Count} CurrentStateIndex={reloadController.CurrentStateIndex} ---");
        if (states == null) return;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (int i = 0; i < states.Count; i++) {
            try {
                var s = states[i];
                var t = s.GetType();
                var props = string.Join(", ", t.GetProperties(all)
                    .Select(p => {
                        try { return $"{p.Name}={p.GetValue(s)}"; }
                        catch { return $"{p.Name}=<err>"; }
                    }));
                MelonLogger.Msg($"[FCS_DEBUG]   state[{i}] props: {props}");
            }
            catch (Exception ex) { MelonLogger.Msg($"[FCS_DEBUG]   state[{i}] err: {ex.Message}"); }
        }
        try {
            var cur = reloadController.CurrentState;
            MelonLogger.Msg($"[FCS_DEBUG]   CurrentState = {cur} (null={cur == null})");
            if (cur != null) {
                var t = cur.GetType();
                foreach (var f in t.GetFields(all)) {
                    try { MelonLogger.Msg($"[FCS_DEBUG]   CurrentState.{f.Name} = {f.GetValue(cur)}"); }
                    catch { }
                }
                foreach (var p in t.GetProperties(all)) {
                    try { MelonLogger.Msg($"[FCS_DEBUG]   CurrentState.{p.Name} = {p.GetValue(cur)}"); }
                    catch { }
                }
            }
        }
        catch (Exception ex) { MelonLogger.Msg($"[FCS_DEBUG]   CurrentState dump err: {ex.Message}"); }
        DumpLightAnimators();
    }

    /// <summary>dump 4 个相位灯的 Animator 参数名, 确认亮灭由哪个参数/子物体驱动.</summary>
    private void DumpLightAnimators() {
        var gunSystem = GameObject.Find("Gun System " + _surfix)?.transform;
        var console = gunSystem?.Find("--Reloading Console");
        if (console == null) return;
        for (int i = 0; i < console.childCount; i++) {
            var child = console.GetChild(i);
            if (!child.name.Contains("Progress Light") && child.name != "ready to fire") continue;
            var anim = child.GetComponent<Animator>();
            if (anim == null) continue;
            var paramDescs = new List<string>();
            foreach (var p in anim.parameters) {
                paramDescs.Add($"{p.name}({p.type})");
            }
            MelonLogger.Msg($"[FCS_DEBUG]   LightAnim {child.name}: params=[{string.Join(", ", paramDescs)}]");
        }
    }

    internal static void DumpFields(object? obj, string label) {
        if (obj == null) return;
        var type = obj.GetType();
        MelonLogger.Msg($"[FCS_DEBUG] --- {label} ({type.FullName}) fields ---");
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance)) {
            try { MelonLogger.Msg($"[FCS_DEBUG]   field {f.Name} = {f.GetValue(obj)} ({f.FieldType.Name})"); }
            catch { MelonLogger.Msg($"[FCS_DEBUG]   field {f.Name} = <err> ({f.FieldType.Name})"); }
        }
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            try { MelonLogger.Msg($"[FCS_DEBUG]   prop  {p.Name} = {p.GetValue(obj)} ({p.PropertyType.Name})"); }
            catch { MelonLogger.Msg($"[FCS_DEBUG]   prop  {p.Name} = <err> ({p.PropertyType.Name})"); }
        }
    }

    /// <summary>
    /// [临时调试] 全场景扫描"炮兵计时器": 游戏自带的剩余飞行时间读数.
    /// 物体名/组件真实类型名含 timer/impact/time/flight/ballistic/countdown 的,
    /// 打印层级路径并用原生反射 dump 字段. 用完注释掉 TryBind 末尾的调用即可, 方法保留备用.
    /// </summary>
    private static bool _timerProbeRan = false;

    private static void ProbeArtilleryTimer() {
        if (_timerProbeRan) return;
        _timerProbeRan = true;
        MelonLogger.Msg("[FCS_DEBUG] ===== artillery timer probe =====");
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>()) {
            if (go == null || go.name == null) continue;
            string goLower = go.name.ToLower();
            bool nameHit = goLower.Contains("timer") || goLower.Contains("impact") || goLower.Contains("time")
                || goLower.Contains("flight") || goLower.Contains("ballistic") || goLower.Contains("countdown");
            foreach (var c in go.GetComponents<Component>()) {
                string tn;
                try { tn = TrueTypeName(c); } catch { continue; }
                string lower = tn.ToLower();
                bool typeHit = lower.Contains("timer") || lower.Contains("impact") || lower.Contains("time")
                    || lower.Contains("flight") || lower.Contains("ballistic") || lower.Contains("countdown");
                if (!nameHit && !typeHit) continue;
                MelonLogger.Msg($"[FCS_DEBUG] TIMER? {HierarchyPath(go.transform)} [{tn}]");
                DumpNativeFields(c, tn);
            }
        }
        MelonLogger.Msg("[FCS_DEBUG] ===== end timer probe =====");
    }

    /// <summary>transform 层级路径 (Root/Child/Grandchild), 用于在场景里定位.</summary>
    private static string HierarchyPath(Transform t) {
        var parts = new List<string>();
        while (t != null) {
            parts.Insert(0, t.name ?? "?");
            t = t.parent;
        }
        return string.Join("/", parts);
    }

    /// <summary>
    /// 用原生反射 dump 组件字段: 封箱后的 Component 走托管 GetType() 只会拿到基类字段,
    /// 必须用 GetIl2CppType 拿 Il2CppSystem.Type 再原生反射.
    /// </summary>
    private static void DumpNativeFields(Component c, string label) {
        try {
            var t = c.GetIl2CppType();
            var flags = Il2CppSystem.Reflection.BindingFlags.Public
                | Il2CppSystem.Reflection.BindingFlags.NonPublic
                | Il2CppSystem.Reflection.BindingFlags.Instance;
            foreach (var f in t.GetFields(flags)) {
                try { MelonLogger.Msg($"[FCS_DEBUG]   nfield {label}.{f.Name} = {f.GetValue(c)}"); }
                catch { MelonLogger.Msg($"[FCS_DEBUG]   nfield {label}.{f.Name} = <err>"); }
            }
            foreach (var p in t.GetProperties(flags)) {
                try { MelonLogger.Msg($"[FCS_DEBUG]   nprop  {label}.{p.Name} = {p.GetValue(c)}"); }
                catch { MelonLogger.Msg($"[FCS_DEBUG]   nprop  {label}.{p.Name} = <err>"); }
            }
        }
        catch (Exception ex) {
            MelonLogger.Msg($"[FCS_DEBUG]   native dump err: {ex.Message}");
        }
    }

    /// <summary>
    /// [临时调试] 炮兵计时表探针: 找 Gun Watch / MissionWatch / Time To Impact 相关物体
    /// (抬炮指针转的表盘 + 击发后倒计时的数字表), dump 组件原生字段找读数源.
    /// 用完注释掉 TryBind 末尾的调用即可.
    /// </summary>
    private void ProbeFlightTimer() {
        MelonLogger.Msg("[FCS_DEBUG] TIMER? ===== Gun Watch probe =====");
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>()) {
            if (go == null || go.name == null) continue;
            if (!go.name.Contains("MissionWatch") && !go.name.Contains("Gun Watch") && !go.name.Contains("Time To Impact")
                && !go.name.Contains("ImpactTimeDial") && !go.name.Contains("Odomiter Output")) continue;
            DumpWatchSubtree(go.transform, 0);
        }
        // 地图落点标记: GunStopwatch.cachedImpactManager 指向它, 倒计时数值可能存这里
        foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>()) {
            if (go == null || go.name == null) continue;
            if (!go.name.Contains("ImpactMarker") && !go.name.StartsWith("ImpactLocation_")) continue;
            MelonLogger.Msg($"[FCS_DEBUG] TIMER? MARKER {HierarchyPath(go.transform)}");
            foreach (var c in go.GetComponents<Component>()) {
                if (c is Transform) continue;
                string tn = TrueTypeName(c);
                if (tn.Contains("Impact") || tn.Contains("Marker") || tn.Contains("Timer") || tn.Contains("Location")) {
                    MelonLogger.Msg($"[FCS_DEBUG] TIMER?   comp [{tn}]");
                    DumpNativeFields(c, tn);
                }
            }
        }
        MelonLogger.Msg("[FCS_DEBUG] TIMER? ===== end Gun Watch probe =====");
    }

    /// <summary>递归 dump 计时表子树, 有读数价值的组件用原生反射打字段.</summary>
    private static void DumpWatchSubtree(Transform t, int depth) {
        if (depth > 6) return;
        var go = t.gameObject;
        var parts = new List<string>();
        foreach (var c in go.GetComponents<Component>()) {
            if (c is Transform) continue;
            string tn = TrueTypeName(c);
            try { parts.Add($"Odo={c.Cast<OdometerDisplay>().CurrentNumber}"); continue; } catch { }
            try { parts.Add($"TMP=\"{c.Cast<TextMeshPro>().text}\""); continue; } catch { }
            try {
                var sw = c.Cast<GunStopwatch>();
                parts.Add(tn);
                DumpFields(sw, $"{go.name}.GunStopwatch");
                continue;
            } catch { }
            try {
                var anim = c.Cast<Animator>();
                parts.Add(tn);
                var pds = new List<string>();
                foreach (var p in anim.parameters) pds.Add($"{p.name}={anim.GetFloat(p.name)}");
                MelonLogger.Msg($"[FCS_DEBUG] TIMER?   anim {go.name} [{string.Join(", ", pds)}]");
                continue;
            } catch { }
            parts.Add(tn);
            if (tn.Contains("Timer") || tn.Contains("Watch") || tn.Contains("Odometer") || tn.Contains("Dial") || tn.Contains("Gauge")) {
                DumpNativeFields(c, $"{go.name}.{tn}");
            }
        }
        if (go.name.Contains("Dial") || go.name.Contains("Needle")) {
            parts.Add($"rot={t.localEulerAngles}");
        }
        MelonLogger.Msg($"[FCS_DEBUG] TIMER?   {new string(' ', depth * 2)}{go.name} [{string.Join(", ", parts)}]");
        for (int i = 0; i < t.childCount; i++) {
            DumpWatchSubtree(t.GetChild(i), depth + 1);
        }
    }

    public bool CanFire() {
        return gunController != null && gunController.CanFire;
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
    /// 正式版的装填状态索引是数据驱动的, 空闲时可能处于不同的 CurrentStateIndex
    /// 因此不把某个固定索引当作"可装填". 只依据控制器实际的 working, 炮闩锁定和炮管运动状态判断
    /// </summary>
    private IEnumerator WaitForReloadReady() {
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
    public IEnumerator LoadBullet(BulletType type) {
        // 上一发的退壳/炮闩/复位机构可能仍在工作. 先等待真实机构状态空闲
        // 再开始下一轮弹仓和推弹操作, 避免连续射击时过早点击后续控件
        yield return WaitForReloadReady();

        RefreshBullets();
        var index = bullets.IndexOf(type.ToString());
        if (index == -1) {
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
            yield break;
        }

        yield return WaitForReloadReady();
        yield return FcsSceneInteractor.WaitAndClick(loadBulletButton!);
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
            yield return FcsSceneInteractor.WaitAndClick(powderButtons[i]);
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

    public IEnumerator LoadPowder(int count) {
        // 推药杆引用可能因 reload 重建而失效, 重新绑定
        if (loadPowderButton == null || loadPowderButton.gameObject == null) {
            var gunSystem = GameObject.Find("Gun System " + _surfix)?.transform;
            var reloadingConsole = gunSystem?.Find("--Reloading Console");
            loadPowderButton = reloadingConsole?.FindChild("Universal Button Charge Rammer (1)")
                ?.GetComponent<LookAtTarget>();
            if (loadPowderButton == null) {
                MelonLogger.Error($"[GunSystem] LoadPowder: rammer button missing");
                yield break;
            }
        }
        yield return SelectPowder(count);
        yield return FcsSceneInteractor.WaitAndClick(loadPowderButton);
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
    
    public int RemainingCharges() {
        return (int)remainingCharges.CurrentNumber;
    }

    /// <summary>实装药包数 (P4 推入确认后生效, 之前为 0). 弹种保护状态机的关键输入.</summary>
    public int LoadedPowderCharges() {
        return gunController == null ? 0 : gunController.PowderCharges;
    }

    /// <summary>炮弹预计飞行时间 (游戏 GunController 弹道预测, 未解算时为 0).</summary>
    public float PredictedImpactTime() {
        return gunController == null ? 0f : gunController.PredictedImpactTime;
    }

    /// <summary>炮管实际仰角 (面板行 1 用, 未绑定时返回 NaN).</summary>
    public float ActualElevation() {
        return gunController == null ? float.NaN : gunController.CurrentElevation;
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
    public float RemainingFlightSeconds() {
        if (_stopwatch == null) return float.NaN;
        if (_stopwatch.state == "CountingDown") return _stopwatch.previousCountingDownRemainingSeconds;
        if (!string.IsNullOrEmpty(BulletInChamber()) && _stopwatch.lastPredictedTravelTime > 0.01f)
            return _stopwatch.lastPredictedTravelTime;
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
