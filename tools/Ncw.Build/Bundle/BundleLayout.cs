using Ncw.Build.Core;

namespace Ncw.Build.Bundle;

/// <summary>
/// 桌面应用包里 sidecar 的约定位置(docs/design/04 §3;桌面端的 <c>LocalAgentSidecar.Locate</c> 按同一约定找):
/// 桌面可执行文件所在目录下的 <c>agent/</c>,里面是 ncw-agent 和同目录的 e_sqlite3 原生库。
/// <list type="bullet">
/// <item>macOS:<c>NextCoWork.app/Contents/MacOS/agent/NextCoWork Agent.app/Contents/MacOS/NextCoWork Agent</c>(<c>AppContext.BaseDirectory</c> 就是 Contents/MacOS/)。
/// Agent 是嵌套的后台应用、可执行文件改名,活动监视器里才显示「NextCoWork Agent」和应用图标(docs/design/04 §7.1「进程名称与图标」)。</item>
/// <item>Windows / Linux:<c>NextCoWork/agent/ncw-agent[.exe]</c>。</item>
/// </list>
/// 清单 <c>bundle.json</c> 放在包的外面(和包同级):macOS 的包签名之后里面任何文件都不能再改。
/// </summary>
internal sealed record BundleLayout(Rid Rid, string OutputDir)
{
    public const string AppName = "NextCoWork";

    public const string AgentDirName = "agent";

    /// <summary>macOS 上 Agent 的显示名 = 嵌套包名 = 包里的可执行文件名(桌面端 <c>AgentLocator.MacAppName</c> 是同一个字面值)。</summary>
    public const string MacAgentName = "NextCoWork Agent";

    public const string ManifestFileName = "bundle.json";

    public bool IsMacBundle => Rid.Os == OsFamily.MacOs;

    public string AppRoot => Path.Combine(OutputDir, IsMacBundle ? AppName + ".app" : AppName);

    /// <summary>桌面可执行文件所在目录(同时是 sidecar 定位的基准目录)。</summary>
    public string ExecutableDir => IsMacBundle ? Path.Combine(AppRoot, "Contents", "MacOS") : AppRoot;

    /// <summary>桌面可执行文件旁边的 <c>agent/</c>(<c>AgentLocator</c> 从这里找)。</summary>
    public string AgentRoot => Path.Combine(ExecutableDir, AgentDirName);

    /// <summary>macOS:嵌套的 <c>NextCoWork Agent.app</c>。</summary>
    public string MacAgentApp => Path.Combine(AgentRoot, MacAgentName + ".app");

    /// <summary>Agent 可执行文件和 e_sqlite3 原生库所在的目录(macOS 是嵌套包的 Contents/MacOS)。</summary>
    public string AgentDir => IsMacBundle ? Path.Combine(MacAgentApp, "Contents", "MacOS") : AgentRoot;

    public string DesktopExe => Path.Combine(ExecutableDir, Rid.DesktopExecutable);

    /// <summary>Agent 可执行文件:macOS 上改名为 <see cref="MacAgentName"/>(进程名就是它),其他平台是发布出来的 <c>ncw-agent[.exe]</c>。</summary>
    public string AgentExe => Path.Combine(AgentDir, IsMacBundle ? MacAgentName : Rid.AgentExecutable);

    public string MacAgentInfoPlist => Path.Combine(MacAgentApp, "Contents", "Info.plist");

    public string MacAgentIconPath => Path.Combine(MacAgentApp, "Contents", "Resources", MacIconFileName);

    public string InfoPlist => Path.Combine(AppRoot, "Contents", "Info.plist");

    /// <summary>.app 里的图标文件名(Info.plist 的 <c>CFBundleIconFile</c> 写不带扩展名的名字)。</summary>
    public const string MacIconFileName = "AppIcon.icns";

    public string MacIconPath => Path.Combine(ResourcesDir, MacIconFileName);

    public string ResourcesDir => Path.Combine(AppRoot, "Contents", "Resources");

    public string ManifestPath => Path.Combine(OutputDir, ManifestFileName);

    /// <summary>Velopack 的打包目录:macOS 直接用 .app,其他平台是应用目录。</summary>
    public string PackDir => AppRoot;
}
