namespace Ncw.Desktop.Sidecar;

/// <summary>
/// 找 ncw-agent。约定(和 <c>tools/Ncw.Build</c> 的 bundle 一致):桌面可执行文件所在目录下的 <c>agent/</c>。
/// <list type="bullet">
/// <item>macOS:<c>agent/NextCoWork Agent.app/Contents/MacOS/NextCoWork Agent</c>——嵌套的后台应用,活动监视器里显示「NextCoWork Agent」和应用图标
/// (docs/design/04 §7.1「进程名称与图标」)。没有它时退回 <c>agent/ncw-agent</c>(IDE 直接起 <c>bin/</c> 下的可执行文件)。</item>
/// <item>Windows / Linux:<c>agent/ncw-agent[.exe]</c>。</item>
/// </list>
/// macOS 的应用包里 <c>AppContext.BaseDirectory</c> 就是 <c>Contents/MacOS/</c>;开发构建由 <c>Ncw.Desktop.csproj</c> 复制到输出目录的 <c>agent/</c>。
/// <c>NCW_AGENT_PATH</c> 优先(开发、测试、CI 指向别的产物)。
/// </summary>
public static class AgentLocator
{
    /// <summary>macOS 上 Agent 的显示名 = 嵌套包里的可执行文件名(正好 16 个字符,内核的进程名不截断)。</summary>
    public const string MacAppName = "NextCoWork Agent";

    /// <summary>macOS 上嵌套包里的可执行文件,相对 <c>agent/</c>。</summary>
    public static readonly string MacBundledExecutable = Path.Combine(MacAppName + ".app", "Contents", "MacOS", MacAppName);

    public static string ExecutableName(bool windows) => windows ? "ncw-agent.exe" : "ncw-agent";

    /// <summary>pid 复用防护:接上已有进程时只认这两个进程名。</summary>
    public static bool IsAgentProcessName(string processName) =>
        processName.StartsWith("ncw-agent", StringComparison.Ordinal) || processName == MacAppName;

    public static string? Locate(string baseDirectory, Func<string, string?> env, bool windows, Func<string, bool> fileExists)
    {
        if (env("NCW_AGENT_PATH") is { Length: > 0 } custom && fileExists(custom))
        {
            return custom;
        }

        var agentDir = Path.Combine(baseDirectory, "agent");
        if (!windows && Path.Combine(agentDir, MacBundledExecutable) is var bundled && fileExists(bundled))
        {
            return bundled;
        }

        var path = Path.Combine(agentDir, ExecutableName(windows));
        return fileExists(path) ? path : null;
    }

    public static string? Locate() =>
        Locate(AppContext.BaseDirectory, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), File.Exists);
}
