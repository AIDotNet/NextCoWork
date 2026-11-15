namespace Ncw.Agent.Environments;

public enum EnvironmentOs
{
    Linux,
    MacOS,
    Windows,
}

/// <summary>环境里 shell 的方言。引号转义、环境变量注入、参数传递都按它选适配器。</summary>
public enum ShellDialect
{
    Posix,
    Cmd,
    PowerShell,
}

/// <summary>
/// 环境事实(06 §2):操作系统、架构、默认 shell、home、路径风格,以及探测到的可用工具(rg、git……)。
/// 工具信息要靠 <see cref="IExecutionEnvironment.ProbeToolsAsync"/> 探测后才有,之前 <see cref="Tools"/> 为空。
/// </summary>
public sealed record EnvironmentFacts
{
    public required EnvironmentOs Os { get; init; }

    public required string OsVersion { get; init; }

    public string Architecture { get; init; } = "";

    /// <summary>默认 shell 的路径或名字(POSIX 是 <c>/bin/bash</c> 这类,Windows 是 <c>pwsh.exe</c> / <c>cmd.exe</c>)。</summary>
    public required string Shell { get; init; }

    public required string Home { get; init; }

    public required string Hostname { get; init; }

    public required string Username { get; init; }

    public string? GitVersion { get; init; }

    public IReadOnlySet<string> Tools { get; init; } = EmptyTools;

    public PathStyle PathStyle => Os == EnvironmentOs.Windows ? PathStyle.Windows : PathStyle.Posix;

    public ShellDialect ShellDialect => ShellCommands.DialectOf(Shell);

    /// <summary>这个环境原生的换行(只用于生成内容;读已有文件要按文件本身的换行,见 <see cref="TextNewlines"/>)。</summary>
    public string NewLine => Os == EnvironmentOs.Windows ? "\r\n" : "\n";

    public bool HasTool(string name) => Tools.Contains(name);

    private static readonly IReadOnlySet<string> EmptyTools = new HashSet<string>(0);
}
