namespace Ncw.Desktop.Sidecar;

/// <summary>
/// 找 ncw-agent。约定(和 <c>tools/Ncw.Build</c> 的 bundle 一致):桌面可执行文件所在目录下的 <c>agent/ncw-agent[.exe]</c>。
/// macOS 的应用包里 <c>AppContext.BaseDirectory</c> 就是 <c>Contents/MacOS/</c>;开发构建由 <c>Ncw.Desktop.csproj</c> 复制到输出目录的 <c>agent/</c>。
/// <c>NCW_AGENT_PATH</c> 优先(开发、测试、CI 指向别的产物)。
/// </summary>
public static class AgentLocator
{
    public static string ExecutableName(bool windows) => windows ? "ncw-agent.exe" : "ncw-agent";

    public static string? Locate(string baseDirectory, Func<string, string?> env, bool windows, Func<string, bool> fileExists)
    {
        if (env("NCW_AGENT_PATH") is { Length: > 0 } custom && fileExists(custom))
        {
            return custom;
        }

        var path = Path.Combine(baseDirectory, "agent", ExecutableName(windows));
        return fileExists(path) ? path : null;
    }

    public static string? Locate() =>
        Locate(AppContext.BaseDirectory, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(), File.Exists);
}
