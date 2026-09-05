using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// [GC] SalvoDirector — 齐射导演 (1.x 单协程带双炮同款): 一条协程同时驱动两门炮.
/// 每步两炮的动作跑在独立子协程里 (等待由 Unity 驱动, 可靠), 导演用完成标志汇合 — 都完成才走下一步
/// (不许一炮还在补药另一炮已推弹进膛). 手动驱动枚举器方案已弃: Il2Cpp 反射拿不到 WaitForSeconds 秒数.
/// 预处理 (1-x SELC/DUMP): 各炮独立推进互不牵制; 同步区 (2-1 SHRD 起): 双炮并行逐步 + 每步 0.5s 死区.
/// 弹药都就绪 → 双炮并行 TRAK (FC 齐射分支开火) → 双炮并行 REST → 回 IDLE.
/// 任一炮 FALL / 任务被撤 (DesiredCharge&lt;0) / 手动 → 全停 (FALL 交 FC 换策略).
/// 左炮 Loop 启动本导演并登记两炮句柄; 子协程句柄全部回收 (F9 防泄漏).
/// </summary>
internal static class SalvoDirector {
    /// <summary>导演起的子协程句柄 (结束/异常时回收, F9 防泄漏).</summary>
    private static readonly List<object> _children = new();

    internal static IEnumerator Run(GunControl l, GunControl r) {
        l.SalvoActive = r.SalvoActive = true;
        l.ResetForTask();
        r.ResetForTask();
        MelonLogger.Msg($"[GC] salvo director begin L={l.Chamber}/{l.Charges} R={r.Chamber}/{r.Charges}");
        try {
            // 预处理 (1-x: SELC/DUMP): 各炮独立推进互不牵制 (该退弹退弹), 先到同步区的等后到的
            yield return PrepBoth(l, r);
            if (l.Action == GunAction.Fall || r.Action == GunAction.Fall) yield break;
            // 同步区 (2-1 SHRD 起): 两炮并行逐步, 都完成才走下一步 — 相位严格一致
            while (!l.Stopped && !r.Stopped) {
                if (l.DesiredCharge < 0 || r.DesiredCharge < 0) yield break; // 任务被撤
                var sl = l.DecidePrepStep();
                var sr = r.DecidePrepStep();
                if (sl == null && sr == null) break; // 双炮弹药就绪 → TRAK
                yield return ExecBoth(l, sl, r, sr);
                if (l.Action == GunAction.Fall || r.Action == GunAction.Fall) {
                    MelonLogger.Error($"[GC] salvo FALL L={l.Action} R={r.Action}, stop director");
                    yield break; // FALL 交 FC 换策略
                }
                // 死区: 两炮都确认真实相位结束 (码确认在例程内) 再等 0.75s — 机构停稳/共享分配器让出才走下一步,
                // 防一炮抢跑 (另一炮还在用分配器, 按钮不激活 → 9s 超时错位)
                yield return new WaitForSeconds(0.75f);
            }
            if (l.Action == GunAction.Fall || r.Action == GunAction.Fall) yield break;

            // TRAK: 双炮并行持续追踪; FC 齐射分支等双炮 AllReady+Arm → 击发 → 双炮 Fired → 停
            var trakL = StartChild(l.RunTrak());
            var trakR = StartChild(r.RunTrak());
            while (!l.Stopped && !r.Stopped && (!l.Fired || !r.Fired)) {
                if (l.DesiredCharge < 0 || r.DesiredCharge < 0) break; // 任务被撤: 退出 TRAK
                yield return new WaitForSeconds(0.04f);
            }
            StopChild(trakL);
            StopChild(trakR);

            // REST: 双炮并行复位 (RunRest = Exec(Rest) + 回 IDLE)
            yield return ExecBoth(l, new GunControl.SalvoStep { Action = GunAction.Rest, Deadline = 30f, Routine = l.RunRest },
                                  r, new GunControl.SalvoStep { Action = GunAction.Rest, Deadline = 30f, Routine = r.RunRest });
        }
        finally {
            foreach (var h in _children) { try { MelonCoroutines.Stop(h); } catch { } }
            _children.Clear();
            l.ResetActionIdle();
            r.ResetActionIdle();
            l.ClearTaskHandle();
            r.ClearTaskHandle();
            // SalvoActive 保持 true 直到 FC 撤任务 (DesiredCharge<0 由 Loop 复位), 防止任务未撤时导演重启
        }
    }

    /// <summary>预处理: 各炮独立跑到同步起始点 2-1 (SHRD) 或弹药就绪, 互不等待 (该退弹退弹/该买弹买弹);
    /// 先到同步区的等后到的 (同步区外互不牵制). 任务被撤/手动/停 → 提前退.</summary>
    private static IEnumerator PrepBoth(GunControl l, GunControl r) {
        bool lAtSync = false, rAtSync = false; // 已到同步区 (就绪或下一步 >= SHRD)
        bool lBusy = false, rBusy = false;     // 本炮预处理步执行中
        while ((!lAtSync || !rAtSync || lBusy || rBusy) && !l.Stopped && !r.Stopped
               && l.Action != GunAction.Fall && r.Action != GunAction.Fall) {
            if (!lBusy && !lAtSync) {
                if (l.DesiredCharge < 0) lAtSync = true; // 任务撤了: 不再推进, 外层退出
                else {
                    var s = l.DecidePrepStep();
                    if (s == null || s.Value.Action >= GunAction.Shrd) lAtSync = true;
                    else { lBusy = true; StartChild(Wrap(l, s.Value, () => lBusy = false)); }
                }
            }
            if (!rBusy && !rAtSync) {
                if (r.DesiredCharge < 0) rAtSync = true;
                else {
                    var s = r.DecidePrepStep();
                    if (s == null || s.Value.Action >= GunAction.Shrd) rAtSync = true;
                    else { rBusy = true; StartChild(Wrap(r, s.Value, () => rBusy = false)); }
                }
            }
            yield return new WaitForSeconds(0.04f);
        }
    }

    /// <summary>两炮并行执行各自的一步 (动作不同也没关系), 都完成才返回; 有炮 FALL 提前撤.</summary>
    private static IEnumerator ExecBoth(GunControl l, GunControl.SalvoStep? sl, GunControl r, GunControl.SalvoStep? sr) {
        bool doneL = sl == null, doneR = sr == null;
        object? hL = null, hR = null;
        if (sl != null) hL = StartChild(Wrap(l, sl.Value, () => doneL = true));
        if (sr != null) hR = StartChild(Wrap(r, sr.Value, () => doneR = true));
        while ((!doneL || !doneR) && l.Action != GunAction.Fall && r.Action != GunAction.Fall) {
            yield return new WaitForSeconds(0.04f);
        }
        if (hL != null) StopChild(hL);
        if (hR != null) StopChild(hR);
    }

    /// <summary>子协程包装: 完成时回调置位 (异常也置位, FALL 由 Exec 内部判定).</summary>
    private static IEnumerator Wrap(GunControl g, GunControl.SalvoStep s, System.Action done) {
        try { yield return g.RunStep(s); }
        finally { done(); }
    }

    private static object StartChild(IEnumerator it) {
        var h = MelonCoroutines.Start(it);
        _children.Add(h);
        return h;
    }

    private static void StopChild(object? h) {
        if (h == null) return;
        try { MelonCoroutines.Stop(h); } catch { }
        _children.Remove(h);
    }
}
