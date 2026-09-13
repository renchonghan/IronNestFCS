namespace IronNestFCS.Logic.FCS;

/// <summary>开机自检行状态: 未显现 → 显现 → 完成/失败 (两步揭示: 先文本, 再补 [DONE]).</summary>
public enum BootLineState { Hidden, Active, Done, Fail }

/// <summary>
/// 开机自检日志 (CMD 风黑框, FcsHud.DrawBoot 渲染): 12 行固定序列, 完成/失败文本补满 64 列.
/// 行序 = 真实装配依赖序 (GC → DC_U → FC → DC_D); [DONE]/[FAIL] 由 FcsModule 开机装配状态机置位.
/// </summary>
public sealed class BootLog
{
    public sealed class Line
    {
        public string ActiveText = ""; // 显现态 (信息行 = 终版文本; 模块行 = |- 名)
        public string DoneText = "";   // 完成态整行 ([DONE] 补满 64 列)
        public string FailText = "";   // 失败态整行 ([FAIL] 补满 64 列)
        public BootLineState State = BootLineState.Hidden;
    }

    public readonly Line[] Lines;

    public BootLog()
    {
        Lines = new Line[12];
        for (int i = 0; i < Lines.Length; i++) Lines[i] = new Line();
        // 四模块行: 两步揭示 (显现 = |- 名, 完成 = 横线填充 + [DONE]); 树形缩进 16 空格 (CMD tree 风, 样本定稿)
        string[] mods = { "Gun Control System", "Data Process System", "Fire Control System", "Holography System" };
        for (int i = 0; i < mods.Length; i++) {
            string prefix = $"                |- {mods[i]}";
            Lines[3 + i].ActiveText = prefix;
            Lines[3 + i].DoneText = PadDone(prefix);
            Lines[3 + i].FailText = PadFail(prefix);
        }
    }

    /// <summary>前缀 + 空格 + 横线填充 + [DONE] 补满 64 列 (名字与横线间留一格, 样本定稿).</summary>
    public static string PadDone(string prefix) => prefix + " " + new string('-', 64 - prefix.Length - 1 - " [DONE]".Length) + " [DONE]";

    /// <summary>前缀 + 空格 + 横线填充 + [FAIL] 补满 64 列.</summary>
    public static string PadFail(string prefix) => prefix + " " + new string('-', 64 - prefix.Length - 1 - " [FAIL]".Length) + " [FAIL]";
}
