namespace IronNestFCS.Logic.FCS;

/// <summary>
/// 版本单一来源 (csproj Version → AssemblyName.Version): 开机自检/面板签名行动态读, 不再硬编码.
/// 宿主 MelonInfo 属性受 attribute 限制必须字面量 — 改版本时与 csproj 手动同步 (唯一剩余同步点).
/// </summary>
public static class FcsVersion {
    public static readonly string Str = Read();

    private static string Read() {
        try {
            var v = typeof(FcsVersion).Assembly.GetName().Version;
            if (v != null && (v.Major != 0 || v.Minor != 0 || v.Build != 0))
                return $"{v.Major}.{v.Minor}.{v.Build}"; // 四段变三段 (Revision 不显示)
        }
        catch { }
        return "2.1.0"; // 兜底 (程序集版本读不到时回老值)
    }
}
