using System.Security;
using System.Text;

namespace Ncw.Build.Bundle;

/// <summary>macOS 应用包的 Info.plist。bundle id 沿用 2.x 的 <c>com.nextcowork.app</c>,通知、登录项和钥匙串条目才能延续(docs/design/04 §2)。</summary>
internal static class InfoPlist
{
    public const string BundleId = "com.nextcowork.app";

    /// <summary>最低 macOS 版本。**待核实**:取 .NET 10 / Avalonia 12 的平台下限,2.9 桥接版的预检(04 §2)要用同一个值。</summary>
    public const string MinimumSystemVersion = "14.0";

    public const string UrlScheme = "nextcowork";

    /// <summary>CFBundleShortVersionString / CFBundleVersion 只接受数字点号:去掉预发布和构建后缀,完整版本放在自定义键里。</summary>
    public static string NumericVersion(string version)
    {
        var core = version.Split('+')[0].Split('-')[0];
        return core;
    }

    /// <summary>嵌套的 Agent 包的 bundle id(主包的子 id)。</summary>
    public const string AgentBundleId = BundleId + ".agent";

    /// <param name="iconFile"><c>Contents/Resources</c> 里的图标文件名;为 null 不写 <c>CFBundleIconFile</c>(包里没有图标时)。</param>
    public static string Render(string version, string? iconFile = null) =>
        Write(version, BundleLayout.AppName, BundleId, iconFile, sb =>
        {
            Pair(sb, "LSApplicationCategoryType", "public.app-category.developer-tools");
            Pair(sb, "NSPrincipalClass", "NSApplication");
            Flag(sb, "NSHighResolutionCapable", true);
            // nextcowork:// 登录回调(docs/design/07 §2):macOS 靠 CFBundleURLTypes 注册。
            sb.Append("  <key>CFBundleURLTypes</key>\n  <array>\n    <dict>\n");
            sb.Append("      <key>CFBundleURLName</key>\n      <string>").Append(BundleId).Append("</string>\n");
            sb.Append("      <key>CFBundleURLSchemes</key>\n      <array>\n        <string>").Append(UrlScheme).Append("</string>\n      </array>\n");
            sb.Append("    </dict>\n  </array>\n");
        });

    /// <summary>
    /// 嵌套的 <c>NextCoWork Agent.app</c>:后台应用(<c>LSBackgroundOnly</c>,不进 Dock、没有菜单),名字和图标给活动监视器看
    /// (docs/design/04 §7.1「进程名称与图标」)。不注册任何 URL scheme。
    /// </summary>
    public static string RenderAgent(string version, string? iconFile = null) =>
        Write(version, BundleLayout.MacAgentName, AgentBundleId, iconFile, sb => Flag(sb, "LSBackgroundOnly", true));

    private static string Write(string version, string name, string bundleId, string? iconFile, Action<StringBuilder> extra)
    {
        var core = NumericVersion(version);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        Pair(sb, "CFBundleName", name);
        Pair(sb, "CFBundleDisplayName", name);
        Pair(sb, "CFBundleIdentifier", bundleId);
        Pair(sb, "CFBundleExecutable", name);
        if (iconFile is not null)
        {
            Pair(sb, "CFBundleIconFile", iconFile);
        }

        Pair(sb, "CFBundlePackageType", "APPL");
        Pair(sb, "CFBundleInfoDictionaryVersion", "6.0");
        Pair(sb, "CFBundleShortVersionString", core);
        Pair(sb, "CFBundleVersion", core);
        Pair(sb, "NCWFullVersion", version);
        Pair(sb, "LSMinimumSystemVersion", MinimumSystemVersion);
        extra(sb);
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }

    private static void Pair(StringBuilder sb, string key, string value) =>
        sb.Append("  <key>").Append(key).Append("</key>\n  <string>").Append(SecurityElement.Escape(value)).Append("</string>\n");

    private static void Flag(StringBuilder sb, string key, bool value) =>
        sb.Append("  <key>").Append(key).Append("</key>\n  <").Append(value ? "true" : "false").Append("/>\n");
}
