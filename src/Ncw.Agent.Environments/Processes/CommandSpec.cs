namespace Ncw.Agent.Environments;

public enum StdinMode
{
    /// <summary>标准输入立刻关闭(EOF)。默认:一个等输入的命令(不带 -m 的 git commit、npm login)不会永远挂住。</summary>
    Closed,

    /// <summary>保持打开,调用方通过 <see cref="IEnvProcess.Stdin"/> 写,写完自己关(MCP stdio、Hook 喂 JSON 用)。</summary>
    Pipe,
}

/// <summary>
/// 要在环境里执行的命令。两种形式二选一:
/// <list type="bullet">
/// <item><see cref="Exec"/>:程序 + 参数向量,不经过 shell,没有引号转义问题;</item>
/// <item><see cref="Shell"/>:一行 shell 命令,由环境按自己的 shell 方言执行(本机看 <c>Facts.Shell</c>,远程同理)。</item>
/// </list>
/// 进程树终止(杀掉整棵树而不只是那一个进程)是所有 <see cref="IEnvProcess"/> 的固有行为,不是选项。
/// </summary>
public sealed record CommandSpec
{
    public string? Program { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public string? ShellCommand { get; init; }

    /// <summary>工作目录;<c>null</c> = 环境的默认目录(本机是 Agent 进程的当前目录,SSH 是登录目录)。</summary>
    public EnvPath? WorkingDirectory { get; init; }

    /// <summary>在继承的环境变量之上追加/覆盖的变量。SSH 环境里通过 stdin 传输,不会出现在任何命令行里(避免泄漏到远端 <c>ps</c>)。</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public StdinMode Stdin { get; init; } = StdinMode.Closed;

    public static CommandSpec Exec(string program, params string[] args) => new() { Program = program, Args = args };

    public static CommandSpec Shell(string command) => new() { ShellCommand = command };

    /// <summary>校验形式:恰好一种,且不含 NUL;环境变量名合法。</summary>
    public void Validate()
    {
        if ((Program is null) == (ShellCommand is null))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "exactly one of Program or ShellCommand is required");
        }

        var text = Program ?? ShellCommand!;
        if (text.Length == 0 || text.Contains('\0') || Args.Any(static a => a.Contains('\0')))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "command must be non-empty and contain no NUL");
        }

        if (Environment is not null)
        {
            foreach (var (name, value) in Environment)
            {
                if (!IsValidEnvName(name) || value.Contains('\0'))
                {
                    throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "invalid environment variable");
                }
            }
        }
    }

    public static bool IsValidEnvName(string name)
    {
        if (name.Length == 0 || char.IsAsciiDigit(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
            {
                return false;
            }
        }

        return true;
    }
}

public enum ProcessExitReason
{
    /// <summary>自己退出(含被外部信号杀死,此时 <see cref="ProcessExit.Signal"/> 可能有值)。</summary>
    Exited,

    /// <summary>被 <see cref="IEnvProcess.KillAsync"/> 终止。</summary>
    Killed,

    /// <summary>SSH 连接中断,进程的结果未知(对模型表现为「连接中断」,不是退出码)。</summary>
    ConnectionLost,
}

/// <param name="ExitCode">退出码;连接中断时为 <c>null</c>。</param>
/// <param name="Signal">终止它的信号编号(能判断时)。</param>
public readonly record struct ProcessExit(int? ExitCode, int? Signal, ProcessExitReason Reason);

/// <summary>环境里运行的一个进程。输出是原始字节流;要截断、带超时的一次性执行用 <see cref="ProcessRunner"/>。</summary>
public interface IEnvProcess : IAsyncDisposable
{
    EnvironmentId EnvironmentId { get; }

    /// <summary>进程启动时的环境连接代。</summary>
    int Generation { get; }

    /// <summary><see cref="StdinMode.Pipe"/> 时可写;<see cref="StdinMode.Closed"/> 时为 <c>null</c>。</summary>
    Stream? Stdin { get; }

    Stream Stdout { get; }

    Stream Stderr { get; }

    Task<ProcessExit> Exited { get; }

    /// <summary>终止整棵进程树:先礼貌地请求退出,宽限期后强杀。返回时进程已经结束(或连接已断开)。重复调用无副作用。</summary>
    ValueTask KillAsync(CancellationToken ct = default);
}
