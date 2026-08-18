using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// [FC] FcsHud — 2.0 HUD 面板直出 (内核直出, 渲染每帧, 不经 DisplayControl; 迁移期未接线).
/// 格式: 每行 64 字符 (行首缩进 1 + 内容 62 + 结尾空 1), 分隔线 64 个 '-'.
/// 相位映射/编号格式见 Architecture.md HUD 段 (Action → 相位代号; 00T/01N/02X; 在炮上 L-N / R-X).
/// </summary>
public class FcsHud {
    private const int LineWidth = 64;

    public FireControl? Fc;
    public GunControl? GunL;
    public GunControl? GunR;

    private Rect _panelRect = new(20, 20, 560, 260);

    public void OnGui() {
        if (Fc == null) return;
        var oldFont = GUI.skin.font;
        var oldSize = GUI.skin.label.fontSize;
        GUI.skin.font = UiFont.Mono;
        GUI.skin.label.fontSize = 14;
        try { Draw(); }
        finally {
            GUI.skin.label.fontSize = oldSize;
            GUI.skin.font = oldFont;
        }
    }

    private void Draw() {
        const float h = 22f, lh = 24f;
        float panelH = 4f + lh * 10 + 8f;
        _panelRect.height = panelH;
        GUI.Box(_panelRect, "");
        float x = _panelRect.x + 2f, y = _panelRect.y + 4f;

        GUI.color = new Color(0f, 1f, 0.35f);
        GUI.Label(new Rect(x, y, _panelRect.width, h), TitleLine());
        y += lh;
        GUI.Label(new Rect(x, y, _panelRect.width, h), new string('-', LineWidth));
        y += lh;
        DrawGunBlock(GunL, "GUN-L", x, ref y, lh, h);
        GUI.Label(new Rect(x, y, _panelRect.width, h), new string('-', LineWidth));
        y += lh;
        DrawGunBlock(GunR, "GUN-R", x, ref y, lh, h);
        GUI.Label(new Rect(x, y, _panelRect.width, h), new string('-', LineWidth));
        y += lh;
        // 队列区: 骨架只出队头 (FC 队列 push 待接)
        GUI.Label(new Rect(x, y, _panelRect.width, h), $" Task Queue({Fc?.QueueCount ?? 0})                 |Finish Queue");
        GUI.color = Color.white;
    }

    /// <summary>标题行: [IronNest FCS] + [模式档] + [CBC:---S] + 任务时钟, 64 字符定宽.</summary>
    private string TitleLine() {
        string mode = Fc?.Mode switch {
            FireMode.FullAuto => "Full-Auto",
            FireMode.SemiAuto => "Semi-Auto",
            FireMode.PreAiming => "PreAiming",
            _ => "Manual",
        };
        float cbt = Fc != null ? Fc.CbtSeconds : float.NaN;
        string cbtStr = float.IsNaN(cbt) ? "CBC:---S" : $"CBC:{Mathf.CeilToInt(cbt):000}S";
        string clock = MissionClock.Format(MissionClock.Seconds);
        string line = $" [IronNest FCS]   [{mode}]      [{cbtStr}]        {clock} ";
        if (line.Length < LineWidth) line = line.PadRight(LineWidth);
        return line[..LineWidth];
    }

    /// <summary>炮块两行: PHASE x-x NAME | [膛内弹] E A C | FT; 第二行前导 = 编号 (在炮 L-N / R-X).</summary>
    private void DrawGunBlock(GunControl? gun, string label, float x, ref float y, float lh, float h) {
        if (gun == null) {
            GUI.Label(new Rect(x, y, _panelRect.width, h), $" [{label}] PHASE 0-0 IDLE | [NULL] E:--.-- A:---.- C:- | FT:--.-S".PadRight(LineWidth));
            y += lh;
            GUI.Label(new Rect(x, y, _panelRect.width, h), $" [--:--:--] ---.- --.-- | [----] E:--.-- A:---.- C:- | T:---.-S".PadRight(LineWidth));
            y += lh;
            return;
        }
        var (code, name) = PhaseCode(gun.Action, Fc?.Mode ?? FireMode.Manual);
        string chamber = gun.Chamber.Length > 0 ? gun.Chamber : (gun.Action == GunAction.Trak ? "----" : "NULL");
        string eStr = float.IsNaN(gun.Elevation) ? "--.--" : $"{gun.Elevation:00.00}";
        string aStr = float.IsNaN(gun.Azimuth) ? "---.-" : $"{gun.Azimuth:000.0}";
        string cStr = gun.Charges > 0 ? gun.Charges.ToString() : "-";
        string ft = !float.IsNaN(gun.FlyTime) && gun.FlyTime > 0.01f ? $"{gun.FlyTime:00.0}S" : "--.-S";
        string modeLetter = "N"; // 骨架: 装药模式由 FC 任务传出 (待接)
        string id = gun == GunL ? $"L-{modeLetter}" : $"R-{modeLetter}";
        GUI.Label(new Rect(x, y, _panelRect.width, h),
            $" [{label}] PHASE {code} {name} | [{Center(chamber, 4)}] E:{eStr} A:{aStr} C:{cStr} | FT:{ft}".PadRight(LineWidth));
        y += lh;
        string t2 = !float.IsNaN(gun.FlyTime) && gun.Fired ? $"{gun.FlyTime:00.0}S" : "--.-S";
        GUI.Label(new Rect(x, y, _panelRect.width, h),
            $" [{id}] ---.- --.-- | [{Center(chamber, 4)}] E:{eStr} A:{aStr} C:{cStr} | T:-{t2}".PadRight(LineWidth));
        y += lh;
    }

    /// <summary>Action → 相位代号 (对齐游戏相位灯): WAIT 1-1 (自动模式无任务) / 0-0 IDLE 仅 Manual.</summary>
    private static (string, string) PhaseCode(GunAction action, FireMode mode) {
        switch (action) {
            case GunAction.Idle: return mode == FireMode.Manual ? ("0-0", "IDLE") : ("1-1", "WAIT");
            case GunAction.Selc: return ("1-2", "SELC");
            case GunAction.Dump: return ("1-3", "DUMP");
            case GunAction.Shrd: return ("2-1", "SHRD");
            case GunAction.Shld: return ("2-2", "SHLD");
            case GunAction.Pwdr: return ("2-3", "PWDR");
            case GunAction.Load: return ("2-4", "LOAD");
            case GunAction.Trak: return ("3-1", "TRAK");
            case GunAction.Rest: return ("3-3", "REST");
            default: return ("0-0", "FALL");
        }
    }

    private static string Center(string s, int width) {
        int pad = width - s.Length;
        if (pad <= 0) return s;
        return new string(' ', pad / 2) + s + new string(' ', pad - pad / 2);
    }
}
