using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// IMGUI 等宽字体 (自 FcsWindow 抽出, 2.0 共享): 游戏自带 TMP 字体的 sourceFontFile.
/// 优先 Inconsolata-SemiBold (真等宽), 否则第一个有 sourceFontFile 的, 最后回退默认.
/// </summary>
internal static class UiFont {
    private static Font? _mono;

    internal static Font Mono {
        get {
            if (_mono == null) {
                try {
                    Font? fallback = null;
                    foreach (var tf in Resources.FindObjectsOfTypeAll<TMP_FontAsset>()) {
                        if (tf == null || tf.name == null || tf.sourceFontFile == null) continue;
                        var lower = tf.name.ToLower();
                        if (lower.Contains("inconsolata") || lower.Contains("typewriter")) {
                            _mono = tf.sourceFontFile;
                            MelonLogger.Msg($"[FCS] UiFont: picked {tf.name}");
                            break;
                        }
                        fallback ??= tf.sourceFontFile;
                    }
                    if (_mono == null && fallback != null) {
                        _mono = fallback;
                        MelonLogger.Msg("[FCS] UiFont: fallback to first available TMP font");
                    }
                }
                catch (System.Exception ex) {
                    MelonLogger.Msg($"[FCS] UiFont: TMP scan failed: {ex.Message}");
                }
            }
            return _mono ?? GUI.skin.font;
        }
    }
}
