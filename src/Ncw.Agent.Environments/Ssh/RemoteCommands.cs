using System.Text;
using System.Text.Json;

namespace Ncw.Agent.Environments.Ssh;

/// <summary>一次远程执行要发送的东西:命令文本(经登录 shell 解释),以及要排在 stdin 最前面的一行(环境变量,不进命令行)。</summary>
public sealed record RemoteLaunch(string CommandText, string? StdinPrefix);

/// <summary>
/// 远程命令的构造(移植自 2.x <c>environment/ssh/command.ts</c> 的 <c>remoteCommand</c> / <c>remoteProcessRequest</c> / 探测命令,06 §3「远程操作系统」)。
/// 纯函数,单测直接检查文本。要点:
/// <list type="bullet">
/// <item>所有参数都单引号转义后作为 <c>sh -c</c> 的位置参数传入,脚本本身不含用户数据,没有二次解释;</item>
/// <item>环境变量不放命令行(会出现在远端 <c>ps</c> 里):作为 stdin 的第一行,由包装脚本读取并 <c>eval</c>;</item>
/// <item>脚本一开始把自己的 pid 写到 stderr(<see cref="PidMarker"/>):sshd 为每个 exec 请求新建会话和进程组,pid 就是进程组 id,
/// 终止时 <c>kill -- -pid</c> 能带走整棵树(06 的「<c>setsid</c> 加进程组」);Windows 用 <c>taskkill /T</c>。</item>
/// </list>
/// </summary>
public static class RemoteCommands
{
    public const string PidMarker = "NCW-PID:";

    // 位置参数: $1=工作目录(可空)  之后:Shell 形式 $2=shell $3=命令;Exec 形式 $2..=程序和参数
    private const string PosixHeader = "printf \"NCW-PID:%s\\n\" \"$$\" >&2; ";
    private const string PosixReadEnv = "IFS= read -r ncw_env || exit 125; eval \"$ncw_env\"; ";
    // SSH.NET 的输入流在「从没写过任何数据」时释放不会发出 EOF,所以关闭 stdin 不能靠它:包装脚本自己把 stdin 重定向到 /dev/null(环境变量头读完之后)
    private const string PosixCloseStdin = "exec </dev/null; ";
    private const string PosixChdir = "if [ -n \"$1\" ]; then cd -- \"$1\" || exit 126; fi; shift; ";

    public static RemoteLaunch Build(EnvironmentFacts facts, CommandSpec spec)
    {
        spec.Validate();
        var cwd = spec.WorkingDirectory?.Value ?? "";
        if (cwd.Contains('\0'))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidPath);
        }

        return facts.Os == EnvironmentOs.Windows ? BuildWindows(facts, spec, cwd) : BuildPosix(facts, spec, cwd);
    }

    private static RemoteLaunch BuildPosix(EnvironmentFacts facts, CommandSpec spec, string cwd)
    {
        var hasEnv = spec.Environment is { Count: > 0 };
        var body = spec.ShellCommand is not null ? "exec \"$1\" -c \"$2\"" : "exec \"$@\"";
        var script = PosixHeader + (hasEnv ? PosixReadEnv : "") + (spec.Stdin == StdinMode.Closed ? PosixCloseStdin : "") + PosixChdir + body;

        var args = new List<string> { cwd };
        if (spec.ShellCommand is { } command)
        {
            args.Add(facts.Shell);
            args.Add(command);
        }
        else
        {
            args.Add(spec.Program!);
            args.AddRange(spec.Args);
        }

        var text = $"exec /bin/sh -c {ShellCommands.PosixQuote(script)} ncw {string.Join(' ', args.Select(ShellCommands.PosixQuote))}";
        return new RemoteLaunch(text, hasEnv ? PosixEnvLine(spec.Environment!) + "\n" : null);
    }

    /// <summary>
    /// 环境变量行:<c>NAME=$(printf '\ooo…'.); NAME=${NAME%.}; export NAME;</c>。每个字节都写成八进制转义,值里的任何字符(含换行、引号、<c>%</c>)都安全;
    /// 末尾的 <c>.</c> 防止命令替换吞掉结尾换行,随后立刻去掉。
    /// </summary>
    internal static string PosixEnvLine(IReadOnlyDictionary<string, string> environment)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in environment)
        {
            if (!CommandSpec.IsValidEnvName(name))
            {
                throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "environment variable name");
            }

            var octal = string.Concat(Encoding.UTF8.GetBytes(value).Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
            sb.Append(name).Append("=$(printf '").Append(octal).Append(".'); ")
                .Append(name).Append("=${").Append(name).Append("%.}; export ").Append(name).Append("; ");
        }

        return sb.ToString().TrimEnd();
    }

    private static RemoteLaunch BuildWindows(EnvironmentFacts facts, CommandSpec spec, string cwd)
    {
        var hasEnv = spec.Environment is { Count: > 0 };
        var sb = new StringBuilder("$ErrorActionPreference = 'Stop'; ");
        sb.Append("[Console]::Error.WriteLine('").Append(PidMarker).Append("' + $PID); ");
        if (hasEnv)
        {
            // stdin 第一行是 Base64(UTF-8 JSON):先读出来设成环境变量,再运行命令;超过 128 KiB 视为异常
            sb.Append("$source=[Console]::OpenStandardInput(); $header=[Collections.Generic.List[byte]]::new(); ")
                .Append("while (($byte=$source.ReadByte()) -ge 0 -and $byte -ne 10) { if ($header.Count -gt 131072) { exit 125 }; $header.Add([byte]$byte) }; ")
                .Append("$values=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String([Text.Encoding]::ASCII.GetString($header.ToArray()))) | ConvertFrom-Json; ")
                .Append("$values.PSObject.Properties | ForEach-Object { [Environment]::SetEnvironmentVariable($_.Name, [string]$_.Value, 'Process') }; ");
        }

        if (cwd.Length > 0)
        {
            sb.Append("Set-Location -LiteralPath ").Append(ShellCommands.PowerShellQuote(cwd)).Append("; ");
        }

        sb.Append("$global:LASTEXITCODE = 0; ");
        if (spec.ShellCommand is { } command)
        {
            sb.Append("& ([ScriptBlock]::Create(").Append(ShellCommands.PowerShellQuote(command)).Append(")); ");
        }
        else
        {
            sb.Append("& ").Append(string.Join(' ', new[] { spec.Program! }.Concat(spec.Args).Select(ShellCommands.PowerShellQuote))).Append("; ");
        }

        sb.Append("if (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }; exit $LASTEXITCODE");
        _ = facts;
        var prefix = hasEnv ? Convert.ToBase64String(Encoding.UTF8.GetBytes(WindowsEnvJson(spec.Environment!))) + "\n" : null;
        return new RemoteLaunch(PowerShellCommandLine(sb.ToString()), prefix);
    }

    private static string WindowsEnvJson(IReadOnlyDictionary<string, string> environment)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in environment)
            {
                writer.WriteString(name, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string PowerShellCommandLine(string script) =>
        $"powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand {ShellCommands.EncodePowerShell(script)}";

    /// <summary>
    /// 终止命令。POSIX:先对进程组发信号(<paramref name="force"/> 为假是 TERM,为真是 KILL),进程组不存在时退到只杀那个 pid;
    /// Windows:<c>taskkill /T /F</c>(没有礼貌退出的等价物,两种都是强杀)。
    /// </summary>
    public static string Kill(EnvironmentOs os, int pid, bool force)
    {
        if (os == EnvironmentOs.Windows)
        {
            return $"taskkill /PID {pid} /T /F";
        }

        var signal = force ? "KILL" : "TERM";
        return $"exec /bin/sh -c {ShellCommands.PosixQuote($"kill -s {signal} -- \"-$1\" 2>/dev/null || kill -s {signal} \"$1\" 2>/dev/null; exit 0")} ncw {pid}";
    }

    // ---- 环境探测 ----

    public const string PosixProbe =
        "printf '%s\\0' \"$(uname -s)\" \"$(uname -r)\" \"$(hostname)\" \"$(id -un)\" \"$HOME\" \"${SHELL:-/bin/sh}\" \"$(uname -m)\"";

    public static string PosixProbeCommand => $"/bin/sh -c {ShellCommands.PosixQuote(PosixProbe)}";

    public static string WindowsProbeCommand => PowerShellCommandLine(
        "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); "
        + "@{os='win32';osVersion=[Environment]::OSVersion.VersionString;hostname=[Environment]::MachineName;"
        + "username=[Environment]::UserName;home=$HOME;shell=(Get-Process -Id $PID).Path;arch=$env:PROCESSOR_ARCHITECTURE} | ConvertTo-Json -Compress");

    /// <summary>解析 POSIX 探测输出。不是 Linux / macOS(例如 FreeBSD)或字段缺失返回 <c>null</c>。</summary>
    public static EnvironmentFacts? ParsePosixProbe(string output)
    {
        var parts = output.Split('\0');
        if (parts.Length < 6 || parts.Take(6).Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        var os = parts[0].Trim() switch
        {
            "Darwin" => EnvironmentOs.MacOS,
            "Linux" => EnvironmentOs.Linux,
            _ => (EnvironmentOs?)null,
        };
        if (os is null)
        {
            return null;
        }

        return new EnvironmentFacts
        {
            Os = os.Value,
            OsVersion = parts[1].Trim(),
            Hostname = parts[2].Trim(),
            Username = parts[3].Trim(),
            Home = parts[4].Trim(),
            Shell = NormalizeRemoteShell(parts[5].Trim()),
            Architecture = parts.Length > 6 ? parts[6].Trim() : "",
        };
    }

    /// <summary>登录 shell 是 fish / nu / csh 这类语法和 POSIX 不兼容的,退到 <c>/bin/sh</c>(模型写的是 POSIX 语法)。</summary>
    internal static string NormalizeRemoteShell(string shell)
    {
        var name = shell[(shell.LastIndexOf('/') + 1)..];
        return name is "bash" or "zsh" or "sh" or "dash" or "ksh" or "ash" ? shell : "/bin/sh";
    }

    public static EnvironmentFacts? ParseWindowsProbe(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output.TrimStart('﻿').Trim());
            var root = document.RootElement;
            string? Get(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;
            if (Get("os") != "win32" || Get("osVersion") is not { } version || Get("hostname") is not { } host
                || Get("username") is not { } user || Get("home") is not { } home || Get("shell") is not { } shell)
            {
                return null;
            }

            return new EnvironmentFacts
            {
                Os = EnvironmentOs.Windows,
                OsVersion = version,
                Hostname = host,
                Username = user,
                Home = home,
                Shell = shell,
                Architecture = Get("arch") ?? "",
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
