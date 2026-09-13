using UnityEngine;

namespace IronNestFCS.Logic.FCS;

/// <summary>
/// [FC] FcsHud — 2.0 HUD 面板直出 (内核直出, 渲染每帧, 不经 DisplayControl; 迁移期未接线).
/// 格式: 每行 64 字符 (行首缩进 1 + 内容 62 + 结尾空 1), 分隔线 64 个 '-'.
/// 相位映射/编号格式见 Architecture.md HUD 段 (Action → 相位代号; 00T/01N/02X; 在炮上 L-N / R-X).
/// </summary>
public class FcsHud {
    private const int LineWidth = 64;

    /// <summary>面板底部签名行 (NO SIGNAL/开机自检同款, 恰好 64 列 — 版本与 FcsHostMod MelonInfo 同步).</summary>
    private const string PanelHeader = "IronNest FireControlSystem 2.0.0 --- svr2kos2 & Lancelot_Holland";

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

    /// <summary>实体未初始化占位 (1.0.7 的 "wait for ..." 位): 面板位置/尺寸与正常 HUD 一致 (左上角 20,20; 64 字符 × 17 行深色框),
    /// 内容 = NO SIGNAL ASCII 大字 (5 行, 框内居中). 由 FcsModule 在 hud 未构建时调用.</summary>
    public static void DrawNoSignal() {
        var oldFont = GUI.skin.font;
        var oldSize = GUI.skin.label.fontSize;
        GUI.skin.font = UiFont.Mono;
        GUI.skin.label.fontSize = 14;
        try {
            string[] art = {
                "  _   _  ___    ____ ___ _   _  ____    _    _     ",
                " | \\ | |/ _ \\  / ___|_ _| \\ | |/ ___|  / \\  | |    ",
                " |  \\| | | | | \\___ \\| ||  \\| | |  _  / _ \\ | |    ",
                " | |\\  | |_| |  ___) | || |\\  | |_| |/ ___ \\| |___ ",
                " |_| \\_|\\___/  |____/___|_| \\_|\\____/_/   \\_\\_____|",
            };
            // 顶部分隔线 (64 '-') → 大字 5 行 (居中) → 底部小字行 (定稿文本, 恰好 64 列 — 版本与 FcsHostMod MelonInfo 同步)
            const float lh = 24f; // 行距固定 24f 与正常 HUD 一致 (lineHeight+4 实测比 HUD 矮 ~60px)
            float lineW = GUI.skin.label.CalcSize(new GUIContent(new string('-', LineWidth))).x;
            float w = lineW + 8f;
            float h = 4f + 17 * lh + 8f; // 与正常 HUD 同高 (17 行)
            GUI.Box(new Rect(20, 20, w, h), "");
            GUI.color = Color.green;
            GUI.Label(new Rect(20 + 4f, 20 + 4f, lineW, lh), new string('-', LineWidth)); // 顶部分隔线 (64 '-', 面板同字号 14px)
            // 统一宽 rect + 左对齐: 逐行 CalcSize 会裁剪行尾空格 (A 右下角/L 末行被截事故), 留 2 空格裕
            float maxW = 0f;
            foreach (var line in art) maxW = Mathf.Max(maxW, GUI.skin.label.CalcSize(new GUIContent(line)).x);
            float padW = maxW + GUI.skin.label.CalcSize(new GUIContent("  ")).x;
            float x = 20 + 4f + (w - 8f - padW) / 2f;
            float y = 20 + 4f + lh + (h - 8f - 2 * lh - art.Length * lh) / 2f - lh; // 大字 5 行居中位再上移 1 行 (用户定稿)
            foreach (var line in art) {
                GUI.Label(new Rect(x, y, padW, lh), line);
                y += lh;
            }
            GUI.Label(new Rect(20 + 4f, 20 + 4f + 15 * lh, lineW, lh), new string('-', LineWidth)); // 小字上方分隔线 (第 15 行)
            GUI.Label(new Rect(20 + 4f, 20 + 4f + 16 * lh, lineW + 8f, lh), PanelHeader); // 底部小字行 (第 16 行, rect 与正常 HUD 行同款 +8 裕量 — 精确 64 宽会渲染裁尾)
            GUI.color = Color.white;
        }
        finally {
            GUI.skin.label.fontSize = oldSize;
            GUI.skin.font = oldFont;
        }
    }

    /// <summary>开机自检面板 (进任务自动): CMD 风黑框 — 分隔线先出, 日志从第二行逐行显现;
    /// 框与正常 HUD 同尺寸 (64 列 × 17 行), 顶部分隔线 + 底部签名行与 NO SIGNAL 同款; [FAIL] 行红显.
    /// 由 FcsModule 开机装配状态机驱动.</summary>
    public static void DrawBoot(BootLog boot) {
        var oldFont = GUI.skin.font;
        var oldSize = GUI.skin.label.fontSize;
        GUI.skin.font = UiFont.Mono;
        GUI.skin.label.fontSize = 14;
        try {
            const float lh = 24f; // 行距与正常 HUD 一致 (固定 24f)
            float lineW = GUI.skin.label.CalcSize(new GUIContent(new string('-', LineWidth))).x;
            float w = lineW + 8f;
            float h = 4f + 17 * lh + 8f; // 与正常 HUD/NO SIGNAL 同高 (17 行: 顶线 + 12 行日志 + 底部签名区)
            GUI.Box(new Rect(20, 20, w, h), "");
            GUI.color = Color.green;
            GUI.Label(new Rect(20 + 4f, 20 + 4f, lineW, lh), new string('-', LineWidth)); // 顶部分隔线 (64 '-')
            float y = 20 + 4f + lh;
            foreach (var line in boot.Lines) {
                string? text = line.State switch {
                    BootLineState.Active => line.ActiveText,
                    BootLineState.Done => line.DoneText,
                    BootLineState.Fail => line.FailText,
                    _ => null,
                };
                if (text != null) {
                    GUI.color = line.State == BootLineState.Fail ? Color.red : Color.green; // 失败行红显
                    GUI.Label(new Rect(20 + 4f, y, lineW + 8f, lh), text); // rect 与正常 HUD 行同款 +8 裕量 (精确 64 宽渲染裁尾)
                }
                y += lh;
            }
            GUI.color = Color.green;
            GUI.Label(new Rect(20 + 4f, 20 + 4f + 15 * lh, lineW, lh), new string('-', LineWidth)); // 底部签名行上方分隔线 (第 15 行, NO SIGNAL 同款)
            GUI.Label(new Rect(20 + 4f, 20 + 4f + 16 * lh, lineW + 8f, lh), PanelHeader); // 底部签名行 (第 16 行, +8 裕量 — 精确 64 宽渲染裁尾)
            GUI.color = Color.white;
        }
        finally {
            GUI.skin.label.fontSize = oldSize;
            GUI.skin.font = oldFont;
        }
    }

    private void Draw() {
        const float h = 22f, lh = 24f;
        // 面板尺寸按内容实测: 宽 = 64 字符实测宽 + 边距, 高 = 行数精确累计
        float lineW = GUI.skin.label.CalcSize(new GUIContent(new string('-', LineWidth))).x;
        _panelRect.width = lineW + 8f;
        const int lines = 1 + 1 + 2 + 1 + 2 + 1 + 1 + 8; // 标题 + 分隔×3 + 两炮各2 + 队头 + 队列8行
        _panelRect.height = 4f + lines * lh + 8f;
        GUI.Box(_panelRect, "");
        float x = _panelRect.x + 4f, y = _panelRect.y + 4f;

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
        // 队列区: 左任务队列 (8 行固定) | 右完成队列
        GUI.Label(new Rect(x, y, _panelRect.width, h), $" Task Queue({Fc?.QueueCount ?? 0})                 |Finish Queue({Fc?.Finished.Count ?? 0})");
        y += lh;
        var queue = Fc?.Queue ?? System.Array.Empty<FireTask>();
        var finished = Fc?.Finished ?? System.Array.Empty<FireControl.FinishedEntry>();
        // 队列展开: 齐射对占两行 — 第一行正常, 第二行前导 >>>[SALVO] (1.0.8 同款, 数据层仍是单任务挂双槽)
        var rows = new System.Collections.Generic.List<string>();
        foreach (var t in queue) {
            rows.Add(QueueRow(t));
            if (t.SalvoPair) rows.Add(SalvoRow(t));
        }
        for (int i = 0; i < 8; i++) {
            string left;
            if (rows.Count > 8 && i == 7) left = $"... ({rows.Count - 7} more)"; // 溢出: 末行省略提示 (Minecraft 服务器风格), 前 7 行显示任务
            else left = i < rows.Count ? rows[i] : "";
            string right = i < finished.Count ? FinishRow(finished[finished.Count - 1 - i]) : ""; // 完成队列倒序: 最新在最上面
            GUI.Label(new Rect(x, y, _panelRect.width, h), $" {left,-30}|{right}");
            y += lh;
        }
        GUI.color = Color.white;
    }

    /// <summary>队列行: [预定打击时间] 方位 距离 [弹种固定 4 字符居中] 模式字母; 预定 -1 = [--:--:--].</summary>
    private static string QueueRow(FireTask t) {
        string planned = t.PlannedStrikeTime > 0f ? MissionClock.Format(t.PlannedStrikeTime) : "[--:--:--]";
        char mode = t.Mode switch { ChargeMode.Tight => 'T', ChargeMode.Extra => 'X', _ => 'N' };
        return $"{planned} {t.Angle:000.0} {t.Distance:00.00} {Center(t.Shell.ToString(), 4)} {mode}";
    }

    /// <summary>齐射对第二行: 前导 >>>[SALVO] (10 字符, 与 [--:--:--] 同宽), 数据同首行.</summary>
    private static string SalvoRow(FireTask t) {
        char mode = t.Mode switch { ChargeMode.Tight => 'T', ChargeMode.Extra => 'X', _ => 'N' };
        return $">>>[SALVO] {t.Angle:000.0} {t.Distance:00.00} {Center(t.Shell.ToString(), 4)} {mode}";
    }

    /// <summary>完成行: 抵达时刻 [HH:MM:SS] (无任务时钟 = 横线) 方位 距离 T:-剩余秒.
    /// 剩余/落地直读 Flight 字段 (唯一口径, 判定在 GC — HUD 只显示): 落地 → --.--; Flight null (DUMP 除外不会) → --.--.</summary>
    private static string FinishRow(FireControl.FinishedEntry f) {
        string arrival = MissionClock.Format(f.FireMission + f.Fly);
        float remain = f.Flight != null && !f.Flight.Landed ? f.Flight.Remain : 0f;
        string cd = remain > 0.01f ? $"{remain:00.00}" : "--.--"; // 前缀 T:- 固定, 占位只补 --.--
        return $"{arrival} {f.Task.Angle:000.0} {f.Task.Distance:00.00} T:-{cd}";
    }

    /// <summary>标题行: [IronNest FCS] + [模式档] + [CBC:---] + 任务时钟, 64 字符定宽 (空格数量对样稿).</summary>
    private string TitleLine() {
        string mode = Fc?.Mode switch {
            FireMode.FullAuto => "Full-Auto",
            FireMode.SemiAuto => "Semi-Auto",
            FireMode.PreAiming => "PreAiming",
            _ => "ManualSet", // Manual 6 字符短一截, ManualSet 对齐 9 字符
        };
        float cbt = Fc != null ? Fc.CbtSeconds : float.NaN;
        string cbtStr = float.IsNaN(cbt) ? "CBC:---" : $"CBC:{Mathf.CeilToInt(cbt):000}";
        string clock = MissionClock.Format(MissionClock.Seconds);
        string line = $" [IronNest FCS]  [{mode}]        [{cbtStr}]        {clock} ";
        if (line.Length < LineWidth) line = line.PadRight(LineWidth);
        return line[..LineWidth];
    }

    /// <summary>炮块两行: PHASE x-x NAME | [膛内弹] E A C | FT; 第二行前导 = 编号 (在炮 L-N / R-X).</summary>
    private void DrawGunBlock(GunControl? gun, string label, float x, ref float y, float lh, float h) {
        if (gun == null) {
            GUI.Label(new Rect(x, y, _panelRect.width, h), $" [{label}] PHASE 0-0 IDLE | [NULL] E:--.-- A:---.- C:- | FT:--.--".PadRight(LineWidth));
            y += lh;
            GUI.Label(new Rect(x, y, _panelRect.width, h), $" [--:--:--] ---.- --.-- | [----] E:--.-- A:---.- C:- | T:---.--".PadRight(LineWidth));
            y += lh;
            return;
        }
        // 相位: 自动模式装填段读游戏真实状态码 (码停在 BreachLocked 时 TRAK 起改声称 Action, 防卡 2-5 COFM);
        // 手动 = 纯 Action 映射 (0-0 IDLE, 不读码 — 玩家自己装填, mod 不确认)
        var mode = Fc?.Mode ?? FireMode.Manual;
        var (code, name) = mode == FireMode.Manual || gun.Action >= GunAction.Trak
            ? PhaseCode(gun.Action, mode)
            : (gun.ReloadPhase() ?? PhaseCode(gun.Action, mode));
        // 膛内/药数活读 (玩家手动装填时 HUD 实时跟, 快照会陈旧); 膛空一律 [----] (不用 NULL)
        string chamber = gun.ChamberLive.Length > 0 ? gun.ChamberLive : "----";
        string eStr = float.IsNaN(gun.Elevation) ? "--.--" : $"{gun.Elevation:00.00}";
        string aStr = float.IsNaN(gun.Azimuth) ? "---.-" : $"{gun.Azimuth:000.0}";
        string cStr = gun.ChargesLive > 0 ? gun.ChargesLive.ToString() : "-";
        string ft = !float.IsNaN(gun.FlyTime) && gun.FlyTime > 0.01f ? $"{gun.FlyTime:00.00}" : "--.--";
        var task = Fc != null ? (gun == GunL ? Fc.LeftTask : Fc.RightTask) : null;
        bool isL = gun == GunL;
        // 前导位 = 预定打击时间 (默认 -1 → [--:--:--]); 齐射时右炮行前导 >>>[SALVO] (1.x 同款, 左炮为主炮)
        string id = task != null && task.PlannedStrikeTime > 0f ? MissionClock.Format(task.PlannedStrikeTime) : "[--:--:--]";
        if (!isL && task != null && task.SalvoPair && Fc != null && Fc.LeftTask == task) id = ">>>[SALVO]";
        string ang = task != null ? (task.Dump ? "---.-" : $"{task.Angle:000.0}") : "---.-";
        string dst = task != null ? (task.Dump ? "--.--" : $"{task.Distance:00.00}") : "--.--";
        GUI.Label(new Rect(x, y, _panelRect.width, h),
            $" [{label}] PHASE {code} {name} | [{Center(chamber, 4)}] E:{eStr} A:{aStr} C:{cStr} | FT:{ft}".PadRight(LineWidth));
        y += lh;
        // 第二行 = 火控解析 (与炮无关, FC 解算持续刷新): 解算仰角/方位/装药; 弹种 = 火控指定的弹 (任务给的, 不读膛内); 无任务 [----]
        // T:- = 火控解析飞时 (射表直算, 静态 — 实际倒计时归 Finish Queue 记); 不依赖游戏炮表
        if (task != null && task.Dump) {
            // DUMP 占位 (FC 强制退弹): 前导 >>>>[DUMP], 无诸元 — 只显示膛内弹 (平射打掉的那发)
            GUI.Label(new Rect(x, y, _panelRect.width, h),
                $" >>>>[DUMP] ---.- --.-- | [{Center(chamber, 4)}] E:--.-- A:---.- C:- | T:---.--".PadRight(LineWidth));
            y += lh;
            return;
        }
        float solE = Fc != null ? (isL ? Fc.SolElevL : Fc.SolElevR) : float.NaN;
        int solC = Fc != null ? (isL ? Fc.SolChargeL : Fc.SolChargeR) : -1;
        string solEStr = task != null && !float.IsNaN(solE) ? $"{solE:00.00}" : "--.--";
        string solCStr = solC > 0 ? solC.ToString() : "-";
        string solShell = task != null ? task.Shell.ToString() : "----";
        float solFly = task != null && solC > 0 ? ShellData.FlightTime(task.Distance, solC) : float.NaN;
        string t2 = !float.IsNaN(solFly) ? $"{solFly:00.00}" : "--.--"; // 前缀 T:- 固定, 占位只补 --.--
        GUI.Label(new Rect(x, y, _panelRect.width, h),
            $" {id} {ang} {dst} | [{Center(solShell, 4)}] E:{solEStr} A:{ang} C:{solCStr} | T:-{t2}".PadRight(LineWidth));
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
