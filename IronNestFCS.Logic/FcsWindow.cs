using IronNestFCS.Logic.FCS;
using MelonLoader;
using UnityEngine;

namespace IronNestFCS.Logic;

/// <summary>
/// 火控系统的 IMGUI 窗口. 只负责绘制与把用户操作转发给 <see cref="FSC"/>.
/// 不含领域逻辑 - 按钮点击后调用 logic 的方法.
///
/// 实现说明: 在 MelonLoader IL2CPP 下, MelonMod.OnGUI 每帧只触发一次,
/// 无法保证 IMGUI 所需的 Layout / event 多 pass. GUILayout 依赖 Layout pass
/// 预算尺寸, pass 不一致时 controlID 会错位, 表现为"只有第一个按钮能点".
/// 因此这里改用绝对 Rect 的 GUI.* API(不走布局系统), 并且不套 GUI.Window
/// (避免回调委托封送丢失 pass). 控件 controlID 仅取决于调用顺序, 稳定可靠.
/// </summary>
public class FcsWindow
{
    private readonly FSC fcs;

    private bool showWindow = true;
    private Rect panelRect = new(20, 20, 450, 150);

    private static readonly Color ClrTitle = new(0.00f, 1.00f, 0.35f);
    private static readonly Color ClrLabel = new(0.00f, 1.00f, 0.35f);
    private static readonly Color ClrIdle = new(0.20f, 0.90f, 0.40f);
    private static readonly Color ClrActive = new(0.00f, 1.00f, 0.50f);
    private static readonly Color ClrWarning = new(0.30f, 1.00f, 0.40f);
    private static readonly Color ClrFailed = new(0.95f, 0.25f, 0.20f);
    private static readonly Color ClrGreen = new(0.00f, 1.00f, 0.45f);
    private static readonly Color ClrWhite = Color.white;
    private static readonly Color ClrDiv = new(0.10f, 0.70f, 0.30f);
    private static readonly Color ClrSweep = new(1.00f, 0.50f, 0.15f);

    /// <summary>面板整行宽 (字符数): 标题/分隔线按此宽排版, 时钟右对齐到同一列.</summary>
    private const int LineWidth = 60;

    public bool AutoSweepEnabled { get; set; }

    public FcsWindow(FSC fcs) => this.fcs = fcs;

    public void OnGui()
    {
        if (!showWindow) return;

        var oldFont = GUI.skin.font;
        var oldFontSize = GUI.skin.label.fontSize;
        GUI.skin.font = MonoFont;
        GUI.skin.label.fontSize = 14; // TMP sourceFontFile 默认字号很大, 这里固定面板字号
        try
        {
            DrawPanel();
        }
        finally
        {
            GUI.skin.label.fontSize = oldFontSize;
            GUI.skin.font = oldFont;
        }
    }

    private void DrawPanel()
    {
        float h = 22f;
        float lineH = h + 2f;

        // 面板高度逐行精确计算 (不再用 base+extra 的估计法)
        float panelH = 4f + lineH;                       // 标题
        if (AutoSweepEnabled) panelH += lineH;           // 扫荡提示
        panelH += lineH;                                 // 分隔线
        panelH += lineH * 4;                             // 两炮各两行
        panelH += lineH * 2;                             // 两条分隔线
        panelH += lineH;                                 // 队列表头
        panelH += lineH * 8;                             // 队列固定 8 行
        if (fcs.PendingOverflow || fcs.FinishedOverflow) panelH += lineH; // ...(x more)
        panelH += 8f;                                    // 底部余量

        panelRect.height = panelH;

        // 背景框
        GUI.Box(panelRect, "");

        float x = panelRect.x + 8f;
        float w = panelRect.width - 16f;
        float y = panelRect.y + 4f;

        var oldColor = GUI.color;
        GUI.color = ClrTitle;
        // 标题行 (60 列): 左标题 | 中间反炮兵倒计时 CBC:sssS (未激活/已停 CBC:---S, 8 字符块在 60 列里左右各 26 完美居中) | 右任务时钟 [HH:MM:SS] (无表 [--:--:--])
        float cbt = fcs.CbtSeconds;
        string cbtStr = $"CBC:{(float.IsNaN(cbt) ? "---" : $"{Mathf.CeilToInt(cbt):000}")}S";
        string titleLine = "IronNest FCS".PadRight(26) + cbtStr;
        titleLine += MissionClockStr(fcs.MissionSeconds).PadLeft(Math.Max(10, LineWidth - titleLine.Length));
        GUI.Label(new Rect(x, y, w, h), titleLine);
        GUI.color = oldColor;
        y += lineH;

        if (AutoSweepEnabled)
        {
            GUI.color = ClrSweep;
            GUI.Label(new Rect(x, y, w, h), "[Sweep ON]");
            GUI.color = oldColor;
            y += lineH;
        }

        DrawDivider(x, y, w);
        y += lineH;

        if (!fcs.IsBound)
        {
            GUI.Label(new Rect(x, y, w, h), "Waiting for scene...");
            y += lineH;
            GUI.Label(new Rect(x, y, w, h), "Press F9 to reload");
            return;
        }

        y = DrawGunRow("GUN-L", fcs.LeftGun, fcs.LeftTask, x, y, w, lineH);
        DrawDivider(x, y, w);
        y += lineH;
        y = DrawGunRow("GUN-R", fcs.RightGun, fcs.RightTask, x, y, w, lineH);
        DrawDivider(x, y, w);
        y += lineH;

        // ===== 队列区: 左任务队列 | 右完成队列 =====
        // 整行按 28 字符左列 + "|" + 右列 拼接成单条字符串 (等宽字体, 空位纯空格)
        GUI.color = ClrLabel;
        GUI.Label(new Rect(x, y, w, h),
            $"{"Task Queue(" + fcs.PendingCount + ")",-28}|Finish Queue({fcs.FinishedCount})");
        GUI.color = oldColor;
        y += lineH;

        var pending = fcs.QueueCan.Take(8).ToList();
        var finished = fcs.FinishedQueue.TakeLast(8).Reverse().ToList(); // 最新在前
        GUI.color = ClrLabel; // 队列区与整体一致的荧光绿
        for (int i = 0; i < 8; i++) // 固定 8 行, 空位纯空格, 中间的 | 常驻
        {
            string left = i < pending.Count
                ? $"{(pending[i].salvoFollower ? ">>>[SALVO]" : "[--:--:--]")} {pending[i].angel:000.0} {pending[i].distance:00.00}  {CenterPad(pending[i].bulletType.ToString(), 4)}"
                : "";
            string right = i < finished.Count
                ? BuildFinishLine(finished[i])
                : "";
            GUI.Label(new Rect(x, y, w, h), $"{left,-28}|{right}");
            y += lineH;
        }
        if (fcs.PendingOverflow || fcs.FinishedOverflow)
        {
            string leftOver = fcs.PendingOverflow ? $"...({fcs.PendingCount - 8} more)" : "";
            string rightOver = fcs.FinishedOverflow ? $"...({fcs.FinishedOverflowCount} more)" : "";
            GUI.Label(new Rect(x, y, w, h), $"{leftOver,-28}|{rightOver}");
            y += lineH;
        }
        GUI.color = oldColor;
    }

    /// <summary>进度枚举 -> 相位代号与名称 (火控台风格).</summary>
    private static (string code, string name) PhaseCode(Progress p) => p switch
    {
        Progress.Pending => ("1-0", "PEND"),
        Progress.Calculating => ("1-1", "CALL"),
        Progress.SelectingBullet => ("1-2", "SELC"),
        Progress.DumpingWrongShell => ("1-3", "DUMP"),
        Progress.LoadingBullet => ("2-1", "BLRD"),
        Progress.RammingBullet => ("2-2", "BLLD"),
        Progress.LoadingPowder => ("2-3", "PWDR"),
        Progress.WaitLoading => ("2-4", "LOAD"),
        Progress.ConfirmingCharge => ("2-5", "COFM"),
        Progress.Aiming => ("3-1", "TRAK"),
        Progress.AimingAzimuth => ("3-1", "TRAK"),
        Progress.WaitingForFire => ("3-1", "TRAK"),
        Progress.Fire => ("3-2", "FIRE"),
        Progress.BackToIdle => ("3-3", "RSET"),
        Progress.Finished => ("0-0", "IDLE"),
        Progress.Failed => ("0-0", "FAIL"),
        _ => ("0-0", "IDLE"),
    };

    // 面板字体抽到 Shared/UiFont (2.0 共享), 这里直接复用
    private static Font MonoFont => UiFont.Mono;

    /// <summary>任务时钟秒数 -> [HH:MM:SS], 无时钟/未走时 [--:--:--].</summary>
    private static string MissionClockStr(float missionSec)
    {
        if (float.IsNaN(missionSec) || missionSec <= 0f) return "[--:--:--]";
        return $"[{(int)missionSec / 3600:00}:{(int)missionSec / 60 % 60:00}:{(int)missionSec % 60:00}]";
    }

    /// <summary>在固定宽度内居中 (弹种位: HE -> "  HE  ", HCHE -> " HCHE ").</summary>
    private static string CenterPad(string s, int width)
    {
        int pad = width - s.Length;
        if (pad <= 0) return s;
        int left = pad / 2;
        return new string(' ', left) + s + new string(' ', pad - left);
    }

    private float DrawGunRow(string gunLabel, GunSystem gun, ArtilleryTask? task, float x, float y, float w, float lineH)
    {
        y = DrawGunPhaseRow(gunLabel, gun, task, x, y, w, lineH);
        return DrawGunSolutionRow(gun, task, x, y, w, lineH);
    }

    private static Color StateColor(ArtilleryTask? task) => task?.progress switch
    {
        Progress.Failed => ClrFailed,
        Progress.Finished => ClrGreen,
        Progress.Pending => ClrLabel,
        null => ClrIdle,
        _ => ClrActive
    };

    /// <summary>行 1: 炮当前实际状态 (与任务无关, 手动装填也如实显示).</summary>
    private float DrawGunPhaseRow(string gunLabel, GunSystem gun, ArtilleryTask? task, float x, float y, float w, float lineH)
    {
        var oldColor = GUI.color;
        var (code, phase) = task == null ? ("0-0", "IDLE") : PhaseCode(task.progress);
        GUI.color = StateColor(task);
        string chambered = gun.BulletInChamber() ?? "NULL";
        float el = gun.ActualElevation();
        float az = fcs.Turret.CurrentAngle();
        int loaded = gun.LoadedPowderCharges();
        // 剩余飞行时间: 游戏炮兵计时表 GunStopwatch 的倒计时读数, 未倒计时为 NaN
        float remain = gun.RemainingFlightSeconds();
        string elStr = float.IsNaN(el) ? "--.--" : $"{el:00.00}";
        string azStr = float.IsNaN(az) ? "---.-" : $"{az:000.0}";
        string cStr = loaded > 0 ? loaded.ToString() : "-";
        // 行 1 FT: 仰角就位锁存前显示活飞行时间 (实时跟仰角), 锁存后显示总飞行时间
        float ft = task != null && task.impactTime > 0.01f ? task.impactTime : remain;
        string ftStr = !float.IsNaN(ft) && ft > 0.01f ? $"{ft:00.0}S" : "--.-S";

        GUI.Label(new Rect(x, y, w, 22f),
            $"[{gunLabel}] PHASE {code} {phase} | {CenterPad(chambered, 4)} E:{elStr} A:{azStr} C:{cStr} | FT:{ftStr}");
        GUI.color = oldColor;
        return y + lineH;
    }

    /// <summary>行 2: 火控解 (RQTA + 目标方位/距离 + 解算诸元), 无任务时全横线. 齐射跟随炮行首 >>>[SALVO].</summary>
    private float DrawGunSolutionRow(GunSystem gun, ArtilleryTask? task, float x, float y, float w, float lineH)
    {
        var oldColor = GUI.color;
        GUI.color = StateColor(task);
        if (task == null)
        {
            GUI.Label(new Rect(x, y, w, 22f),
                "[--:--:--] ---.- --.-- | ---- E:--.-- A:---.- C:- | T:---.-S");
        }
        else
        {
            // 行 2 T:-: 击发后显示目标倒计时 (计时表), 击发前/落地后横线
            float cd = gun.CountdownRemainingSeconds();
            string cdStr = !float.IsNaN(cd) && cd > 0.01f ? $"{cd:00.0}S" : "--.-S";
            // 真实解算快照: 仰角与装药量来自弹道计算器输出, 未解算时显示横线
            string solE = task.calculatedElevation > 0.01f ? $"{task.calculatedElevation:00.00}" : "--.--";
            string solC = task.charge > 0 ? task.charge.ToString() : "-";
            // 击发后前导位显示抵达时刻 (击发时钟 + 飞行时间), 击发前保持占位
            string arrival = task.fireMissionTime > 0f ? MissionClockStr(task.fireMissionTime + task.impactTime) : "[--:--:--]";
            GUI.Label(new Rect(x, y, w, 22f),
                $"{(task.salvoFollower ? ">>>[SALVO]" : arrival)} {task.angel:000.0} {task.distance:00.00} | {CenterPad(task.bulletType.ToString(), 4)} E:{solE} A:{task.angel:000.0} C:{solC} | T:-{cdStr}");
        }
        GUI.color = oldColor;
        return y + lineH;
    }

    /// <summary>完成队列行: 抵达时刻 [HH:MM:SS] (击发时钟+飞行时间, 无时钟占位) + 目标方位距离 + 该发的实时剩余飞行时间 T:- (落地后横线).</summary>
    private static string BuildFinishLine(FinishedTask f)
    {
        float remain = f.task.impactTime - (Time.time - f.fireTime);
        string cd = remain > 0.01f ? $"{remain:00.0}S" : "--.-S"; // T:- 前缀自带一个杠, 这里只需 --.-S
        return $"{MissionClockStr(f.task.fireMissionTime + f.task.impactTime)} {f.task.angel:000.0} {f.task.distance:00.00} T:-{cd}";
    }

    private static void DrawDivider(float x, float y, float w)
    {
        var oldColor = GUI.color;
        GUI.color = ClrDiv;
        GUI.Label(new Rect(x, y, w, 22f), new string('-', LineWidth)); // 等宽横分割线, 与标题行同宽
        GUI.color = oldColor;
    }

    /// <summary> 计算坐标点所对应的区域字符串 </summary>
    public static string ConvertPosition(Vector3 position)
    {
        int leterIndex = (int)position.x;
        string zoneCol = leterIndex >= 0 && leterIndex < 26 ? ((char)('A' + leterIndex)).ToString() : "#";
        int zoneRow = (int)position.y + 1;
        int subCol = (int)(position.x * 10) % 10;  // B: 第一位小数
        int subRow = (int)(position.y * 10) % 10;  // B: 第一位小数
        return $"{zoneCol}{zoneRow} {subCol}:{subRow}";
    }
}
