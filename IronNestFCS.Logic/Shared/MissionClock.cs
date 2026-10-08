using Il2Cpp;
using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 任务时钟读取 (自 FSC.MissionSeconds 抽出, 2.0 共享): 玩家手腕 MissionWatch 的 GenericTimerSceneSync,
/// 自 10:00:00 起向上累计秒数; 无表/未走 NaN. 缓存引用, 失效自动重找. 线程常驻 — 谁都能随时读.
/// </summary>
public static class MissionClock {
    private static GenericTimerSceneSync? _sync;

    public static float Seconds {
        get {
            if (_sync == null) {
                foreach (var s in Resources.FindObjectsOfTypeAll<GenericTimerSceneSync>()) {
                    if (s == null || s.TimerID != "MissionTime" || s.CurrentTime <= 0f) continue;
                    _sync = s;
                    break;
                }
            }
            if (_sync == null) return float.NaN;
            try { return _sync.CurrentTime; }
            catch { _sync = null; return float.NaN; }
        }
    }

    /// <summary>秒数 → [HH:MM:SS], 无时钟 [--:--:--].</summary>
    public static string Format(float sec) {
        if (float.IsNaN(sec) || sec <= 0f) return "[--:--:--]";
        return $"[{(int)sec / 3600:00}:{(int)sec / 60 % 60:00}:{(int)sec % 60:00}]";
    }

    /// <summary>热重载: 清缓存.</summary>
    public static void Reset() => _sync = null;
}
