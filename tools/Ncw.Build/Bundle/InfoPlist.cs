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

    /// <param name="iconFile"><c>Contents/Resources</c> 里的图标文件名;为 null 不写 <c>CFBundleIconFile</c>(包里没有图标时)。</param>
    public static string Render(string version, string? iconFile = null)
    {
        var core = NumericVersion(version);
        var sb = new StringBuilder();
        void Pair(string key, string value) => sb.Append("  <key>").Append(key).Append("</key>\n  <string>").Append(SecurityElement.Escape(value)).Append("</string>\n");
        void Flag(string key, bool value) => sb.Append("  <key>").Append(key).Append("</key>\n  <").Append(value ? "true" : "false").Append("/>\n");

        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        Pair("CFBundleName", BundleLayout.AppName);
        Pair("CFBundleDisplayName", BundleLayout.AppName);
        Pair("CFBundleIdentifier", BundleId);
        Pair("CFBundleExecutable", BundleLayout.AppName);
        if (iconFile is not null)
        {
            Pair("CFBundleIconFile", iconFile);
        }

        Pair("CFBundlePackageType", "APPL");
        Pair("CFBundleInfoDictionaryVersion", "6.0");
        Pair("CFBundleShortVersionString", core);
        Pair("CFBundleVersion", core);
        Pair("NCWFullVersion", version);
        Pair("LSMinimumSystemVersion", MinimumSystemVersion);
        Pair("LSApplicationCategoryType", "public.app-category.developer-tools");
        Pair("NSPrincipalClass", "NSApplication");
        Flag("NSHighResolutionCapable", true);
        // nextcowork:// 登录回调(docs/design/07 §2):macOS 靠 CFBundleURLTypes 注册。
        sb.Append("  <key>CFBundleURLTypes</key>\n  <array>\n    <dict>\n");
        sb.Append("      <key>CFBundleURLName</key>\n      <string>").Append(BundleId).Append("</string>\n");
        sb.Append("      <key>CFBundleURLSchemes</key>\n      <array>\n        <string>").Append(UrlScheme).Append("</string>\n      </array>\n");
        sb.Append("    </dict>\n  </array>\n");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }
}
