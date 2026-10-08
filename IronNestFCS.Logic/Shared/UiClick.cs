using System.Collections;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

/// <summary>按钮点击工具 (自旧 FcsSceneInteractor 抽出, 2.0 共享):
/// 等按钮激活 + 冷却结束 → 按下/抬起. 供 TriggerConsole / PurchaseDeck / GunSystem 硬件驱动使用.</summary>
public static class UiClick {
    public static IEnumerator WaitAndClick(LookAtTarget? button, float timeout = 9f) {
        if (button == null) {
            MelonLogger.Error("[FCS] WaitAndClick: button is null");
            yield break;
        }
        float waited = 0f;
        while (button.isActive == false || button.nextAllowedClickTime > Time.realtimeSinceStartup) {
            if (waited >= timeout) {
                MelonLogger.Error($"[FCS] WaitAndClick: {button.name} not active after {timeout:F0}s, skip click");
                yield break;
            }
            yield return new WaitForSeconds(0.1f);
            waited += 0.1f;
        }
        yield return new WaitForSeconds(0.1f);
        button.OnClickDown();
        yield return new WaitForSeconds(0.1f);
        button.OnClickUp();
    }
}
