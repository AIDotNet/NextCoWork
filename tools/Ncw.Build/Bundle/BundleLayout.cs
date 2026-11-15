using Ncw.Build.Core;

namespace Ncw.Build.Bundle;

/// <summary>
/// 桌面应用包里 sidecar 的约定位置(docs/design/04 §3;桌面端的 <c>LocalAgentSidecar.Locate</c> 按同一约定找):
/// 桌面可执行文件所在目录下的 <c>agent/</c>,里面是 ncw-agent 和同目录的 e_sqlite3 原生库。
/// <list type="bullet">
/// <item>macOS:<c>NextCoWork.app/Contents/MacOS/agent/ncw-agent</c>(<c>AppContext.BaseDirectory</c> 就是 Contents/MacOS/)。</item>
/// <item>Windows / Linux:<c>NextCoWork/agent/ncw-agent[.exe]</c>。</item>
/// </list>
/// 清单 <c>bundle.json</c> 放在包的外面(和包同级):macOS 的包签名之后里面任何文件都不能再改。
/// </summary>
internal sealed record BundleLayout(Rid Rid, string OutputDir)
{
    public const string AppName = "NextCoWork";

    public const string AgentDirName = "agent";

    public const string ManifestFileName = "bundle.json";

    public bool IsMacBundle => Rid.Os == OsFamily.MacOs;

    public string AppRoot => Path.Combine(OutputDir, IsMacBundle ? AppName + ".app" : AppName);

    /// <summary>桌面可执行文件所在目录(同时是 sidecar 定位的基准目录)。</summary>
    public string ExecutableDir => IsMacBundle ? Path.Combine(AppRoot, "Contents", "MacOS") : AppRoot;

    public string AgentDir => Path.Combine(ExecutableDir, AgentDirName);

    public string DesktopExe => Path.Combine(ExecutableDir, Rid.DesktopExecutable);

    public string AgentExe => Path.Combine(AgentDir, Rid.AgentExecutable);

    public string InfoPlist => Path.Combine(AppRoot, "Contents", "Info.plist");

    public string ResourcesDir => Path.Combine(AppRoot, "Contents", "Resources");

    public string ManifestPath => Path.Combine(OutputDir, ManifestFileName);

    /// <summary>Velopack 的打包目录:macOS 直接用 .app,其他平台是应用目录。</summary>
    public string PackDir => AppRoot;
}
