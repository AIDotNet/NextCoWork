using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal;

/// <summary>解析完成的 PTY 启动参数(跨平台共用):程序、参数、工作目录、完整的子进程环境。</summary>
internal sealed record PtyLaunch(string Program, IReadOnlyList<string> Args, string? WorkingDirectory, IReadOnlyList<KeyValuePair<string, string>> Environment, int Columns, int Rows)
{
    /// <summary>
    /// 把 <see cref="TerminalSpec"/> 解析成启动参数。环境变量合并次序(同 2.x <c>local.ts</c> 的 openTerminal 与 <c>mergeChildEnv</c>):
    /// 继承 ← 注入(<paramref name="injected"/>,不覆盖用户自己已有的)← <c>TERM=xterm-256color</c> ← 调用方给的。
    /// </summary>
    public static PtyLaunch Resolve(TerminalSpec spec, EnvironmentFacts facts, IReadOnlyDictionary<string, string>? injected, IEnumerable<KeyValuePair<string, string>> inherited)
    {
        ValidateSize(spec.Columns, spec.Rows);
        var windows = OperatingSystem.IsWindows();
        var merged = new Dictionary<string, string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (name, value) in inherited)
        {
            if (name.Length > 0 && !name.Contains('=') && !name.Contains('\0') && !value.Contains('\0'))
            {
                merged[name] = value;
            }
        }

        if (injected is not null)
        {
            foreach (var (name, value) in injected)
            {
                merged.TryAdd(name, value);
            }
        }

        merged["TERM"] = "xterm-256color";
        if (!windows && !merged.ContainsKey("LANG") && !merged.ContainsKey("LC_ALL") && !merged.ContainsKey("LC_CTYPE"))
        {
            // 从桌面启动的应用常常没有 locale:不给的话 bash 把多字节字符当成问号
            merged["LANG"] = OperatingSystem.IsMacOS() ? "en_US.UTF-8" : "C.UTF-8";
        }

        if (spec.Environment is { } explicitEnvironment)
        {
            foreach (var (name, value) in explicitEnvironment)
            {
                if (!CommandSpec.IsValidEnvName(name) || value.Contains('\0'))
                {
                    throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "invalid environment variable");
                }

                merged[name] = value;
            }
        }

        string? cwd = null;
        if (spec.WorkingDirectory is { } directory)
        {
            cwd = directory.Value;
            if (!Directory.Exists(cwd))
            {
                throw new EnvironmentException(File.Exists(cwd) ? EnvironmentErrorCode.NotDirectory : EnvironmentErrorCode.NotFound, cwd);
            }
        }

        var shell = facts.Shell;
        if (string.IsNullOrEmpty(shell) || shell.Contains('\0'))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "shell");
        }

        return new PtyLaunch(shell, ShellArgs(shell), cwd, merged.ToList(), spec.Columns, spec.Rows);
    }

    /// <summary>
    /// 交互 shell 的参数。POSIX:认得 <c>-l</c> 的 shell 以登录 shell 启动(桌面启动的应用继承的 PATH 只有系统那几条,
    /// macOS「终端」与 VS Code 都以登录 shell 起,这里对齐,2.x 的 <c>terminalShellArgs</c>);PowerShell 去掉启动横幅;cmd 不加参数。
    /// </summary>
    public static IReadOnlyList<string> ShellArgs(string shell)
    {
        if (ShellCommands.DialectOf(shell) == ShellDialect.PowerShell)
        {
            return ["-NoLogo"];
        }

        if (OperatingSystem.IsWindows() || ShellCommands.DialectOf(shell) != ShellDialect.Posix)
        {
            return [];
        }

        var name = shell[(shell.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return name is "sh" or "bash" or "zsh" or "dash" or "ksh" or "mksh" or "fish" ? ["-l"] : [];
    }

    private static void ValidateSize(int columns, int rows)
    {
        if (columns is < 1 or > 32767 || rows is < 1 or > 32767)
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "terminal size must be 1..32767");
        }
    }
}
