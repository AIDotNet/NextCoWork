using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal;

/// <summary>
/// 把 argv 拼成一行写给既有交互 shell 的启动行(2.x <c>terminal-host.ts</c> 的 <c>launchLine</c>)。
/// CLI(claude / codex 这类全屏 TUI)必须跑在交互式终端里,PTY 起的是用户的 shell,没有「spawn 后替换进程」的手段——
/// 把启动行写进去,让 shell 自己启动 CLI,CLI 退出后终端仍是一个活 shell。
/// 空格和引号安全靠引号包裹,按 shell 方言选:POSIX <c>'\''</c>、PowerShell <c>''</c>、cmd 双引号(含 <c>"^&amp;|&lt;&gt;%!</c> 的参数拒绝)。
/// </summary>
public static class LaunchLine
{
    public static string Build(IReadOnlyList<string> argv, ShellDialect dialect)
    {
        if (argv.Count == 0 || argv.Any(static a => a.Contains('\0')))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "launch argv");
        }

        return string.Join(' ', argv.Select(a => Quote(a, dialect)));
    }

    /// <summary>在 shell 里「回车」要发的字符:POSIX 用换行,Windows 的伪控制台里回车是 <c>\r</c>(cmd 需要它才会执行)。</summary>
    public static string Enter(ShellDialect dialect) => dialect == ShellDialect.Posix ? "\n" : "\r";

    private static string Quote(string value, ShellDialect dialect)
    {
        if (value.Length > 0 && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '@' or '%' or '+' or '=' or ':' or ',' or '.' or '/' or '-'))
        {
            return value;
        }

        switch (dialect)
        {
            case ShellDialect.PowerShell:
                return ShellCommands.PowerShellQuote(value);
            case ShellDialect.Cmd:
                if (value.IndexOfAny(['"', '^', '&', '|', '<', '>', '%', '!', '\r', '\n']) >= 0)
                {
                    throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "argument cannot be quoted for cmd.exe");
                }

                return "\"" + value + "\"";
            default:
                return ShellCommands.PosixQuote(value);
        }
    }
}
