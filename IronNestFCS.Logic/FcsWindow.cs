using IronNestFCS.Logic.FCS;
using Il2CppTMPro;
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
    private Rect panelRect = new(20, 20, 440, 150);

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

        float extra = 0f;
        extra += lineH * 4; // 两炮各两行
        extra += lineH * (fcs.PendingCount + 1);
        extra += 12f;

        panelRect.height = 150f + extra;

        // 背景框
        GUI.Box(panelRect, "");

        float x = panelRect.x + 8f;
        float w = panelRect.width - 16f;
        float y = panelRect.y + 4f;

        var oldColor = GUI.color;
        GUI.color = ClrTitle;
        GUI.Label(new Rect(x, y, w, h), "IronNest FCS");
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
        y += 4f;

        if (!fcs.IsBound)
        {
            GUI.Label(new Rect(x, y, w, h), "Waiting for scene...");
            y += lineH;
            GUI.Label(new Rect(x, y, w, h), "Press F9 to reload");
            return;
        }

        y = DrawGunRow("GUN-L", fcs.LeftGun, fcs.LeftTask, x, y, w, lineH);
        DrawDivider(x, y, w);
        y += 4f;
        y = DrawGunRow("GUN-R", fcs.RightGun, fcs.RightTask, x, y, w, lineH);
        DrawDivider(x, y, w);
        y += 4f;

        GUI.color = ClrLabel;
        GUI.Label(new Rect(x, y, w, h), $"Queue: {fcs.PendingCount}");
        GUI.color = oldColor;
        y += lineH;

        foreach (var item in fcs.QueueCan)
        {
            GUI.Label(new Rect(x, y, w, h),
                $"  T{item.targetId}  {ConvertPosition(item.position)}  {item.angel,5:F1}°/{item.distance,5:F2}km  {item.bulletType}");
            y += lineH;
        }
    }

    /// <summary>进度枚举 -> 相位代号与名称 (火控台风格).</summary>
    private static (string code, string name) PhaseCode(Progress p) => p switch
    {
        Progress.Pending => ("0-1", "CALL"),
        Progress.Calculating => ("0-1", "CALL"),
        Progress.SelectingBullet => ("0-2", "SELC"),
        Progress.DumpingWrongShell => ("0-3", "DUMP"),
        Progress.LoadingBullet => ("1-2", "BLLD"),
        Progress.LoadingPowder => ("1-3", "PWDR"),
        Progress.WaitLoading => ("1-4", "LOAD"),
        Progress.Aiming => ("2-1", "EAIM"),
        Progress.AimingAzimuth => ("2-2", "HAIM"),
        Progress.WaitingForFire => ("2-3", "WAIT"),
        Progress.BackToIdle => ("2-4", "RSET"),
        Progress.Finished => ("0-0", "IDLE"),
        Progress.Failed => ("0-0", "FAIL"),
        _ => ("0-0", "IDLE"),
    };

    /// <summary>
    /// 面板字体: Font.CreateDynamicFontFromOSFont 被游戏 IL2CPP 裁剪 (Method unstripping failed),
    /// 所有 OS 字体都创建不了. 改走游戏自带 TMP 字体的 sourceFontFile:
    /// 游戏里有 Inconsolata-SemiBold (真等宽), 优先它; 否则取第一个有 sourceFontFile 的; 最后回退默认字体.
    /// </summary>
    private static Font? _monoFont;
    private static Font MonoFont
    {
        get
        {
            if (_monoFont == null)
            {
                try
                {
                    Font? fallback = null;
                    foreach (var tf in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                    {
                        if (tf == null || tf.name == null || tf.sourceFontFile == null) continue;
                        var lower = tf.name.ToLower();
                        if (lower.Contains("inconsolata") || lower.Contains("typewriter"))
                        {
                            _monoFont = tf.sourceFontFile;
                            MelonLogger.Msg($"[FCS] MonoFont: picked {tf.name}");
                            break;
                        }
                        fallback ??= tf.sourceFontFile;
                    }
                    if (_monoFont == null && fallback != null)
                    {
                        _monoFont = fallback;
                        MelonLogger.Msg("[FCS] MonoFont: fallback to first available TMP font");
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Msg($"[FCS] MonoFont: TMP scan failed: {ex.Message}");
                }
            }
            return _monoFont ?? GUI.skin.font;
        }
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
        var oldColor = GUI.color;
        var (code, phase) = task == null ? ("0-0", "IDLE") : PhaseCode(task.progress);

        Color stateColor = task?.progress switch
        {
            Progress.Failed => ClrFailed,
            Progress.Finished => ClrGreen,
            Progress.Pending => ClrLabel,
            null => ClrIdle,
            _ => ClrActive
        };

        GUI.color = stateColor;

        // ===== 行 1: 炮当前实际状态 (与任务无关, 手动装填也如实显示) =====
        string chambered = gun.BulletInChamber() ?? "NULL";
        float el = gun.ActualElevation();
        float az = fcs.Turret.CurrentAngle();
        int loaded = gun.LoadedPowderCharges();
        // 剩余飞行时间: 游戏炮兵计时表 GunStopwatch 的倒计时读数, 未倒计时为 NaN
        float remain = gun.RemainingFlightSeconds();
        string elStr = float.IsNaN(el) ? "--.--" : $"{el:00.00}";
        string azStr = float.IsNaN(az) ? "---.-" : $"{az:000.0}";
        string cStr = loaded > 0 ? loaded.ToString() : "-";
        string flightStr = !float.IsNaN(remain) && remain > 0.01f ? $"{remain:00.0}S" : "--.-S";

        GUI.Label(new Rect(x, y, w, 22f),
            $"[{gunLabel}] PHASE {code} {phase} | {CenterPad(chambered, 4)} E:{elStr} A:{azStr} C:{cStr} T:{flightStr}");
        y += lineH;

        // ===== 行 2: 火控解 (RQTA + 目标方位/距离 + 解算诸元), 无任务时全横线 =====
        if (task == null)
        {
            GUI.Label(new Rect(x, y, w, 22f),
                "[--:--:--] ---.- --.-- | ---- E:--.-- A:---.- C:- T:--.-S");
        }
        else
        {
            int planned = FcsCalc.Charge(task.distance);
            // 总飞行时间: WaitForFire 时从炮兵计时器拷贝的火控解, 不随飞行倒数
            float total = task.impactTime;
            string totalStr = total > 0.01f ? $"{total:00.0}S" : "--.-S";
            GUI.Label(new Rect(x, y, w, 22f),
                $"[--:--:--] {task.angel:000.0} {task.distance:00.00} |{CenterPad(task.bulletType.ToString(), 4)} E:{FcsCalc.Elevation(task.distance):00.00} A:{task.angel:000.0} C:{planned} T:{totalStr}");
        }
        GUI.color = oldColor;
        return y + lineH;
    }

    private static void DrawDivider(float x, float y, float w)
    {
        var oldColor = GUI.color;
        GUI.color = ClrDiv;
        GUI.Label(new Rect(x, y, w, 1f), "");
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
