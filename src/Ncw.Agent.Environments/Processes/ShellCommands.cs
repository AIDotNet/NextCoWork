using System.Text;

namespace Ncw.Agent.Environments;

/// <summary>怎么启动一个 shell 来执行一行命令。</summary>
/// <param name="VerbatimArguments">非空时必须原样作为整条命令行参数传给进程(Windows cmd.exe 的引号规则和 CommandLineToArgv 不一样,不能让运行时再引一次)。此时 <see cref="Args"/> 为空。</param>
public readonly record struct ShellInvocation(string FileName, IReadOnlyList<string> Args, string? VerbatimArguments = null);

/// <summary>
/// 「在这个环境里跑一行 shell 命令,用哪个 shell、怎么传参」——一处答案(移植自 2.x <c>environment/shell.ts</c> 与 <c>kernel/node-spawn.ts</c> 的 <c>shellCommandArgs</c>)。
/// 纯函数,本机和 SSH 共用:Windows 的引号规则以后一定还要动,两处各写一份的结果是只有一份被改。
/// </summary>
public static class ShellCommands
{
    public static ShellDialect DialectOf(string shell)
    {
        var name = shell;
        var cut = name.LastIndexOfAny(['/', '\\']);
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }

        name = name.ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.Ordinal))
        {
            name = name[..^4];
        }

        return name switch
        {
            "cmd" => ShellDialect.Cmd,
            "powershell" or "pwsh" => ShellDialect.PowerShell,
            // Windows 上的 bash.exe(Git for Windows / WSL)也是 POSIX 方言
            _ => ShellDialect.Posix,
        };
    }

    public static ShellInvocation Build(string shell, ShellDialect dialect, string command) => dialect switch
    {
        // cmd 的命令行自带一对引号包住整条命令,/s 让它只剥最外层的一对
        ShellDialect.Cmd => new ShellInvocation(shell, [], $"/d /s /c \"{command}\""),
        // PowerShell 用 -EncodedCommand 传入,绕开所有引号转义
        ShellDialect.PowerShell => new ShellInvocation(shell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", EncodePowerShell(command)]),
        _ => new ShellInvocation(shell, ["-c", command]),
    };

    /// <summary>POSIX 单引号转义:<c>a'b</c> → <c>'a'\''b'</c>。所有 POSIX shell(含 fish、csh 的单引号语义)都认。</summary>
    public static string PosixQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    public static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>PowerShell <c>-EncodedCommand</c> 要的 UTF-16LE + Base64。</summary>
    public static string EncodePowerShell(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
