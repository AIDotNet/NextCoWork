using System.Text.RegularExpressions;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;

namespace Ncw.Agent.Tools;

/// <summary>Bash / 后台 Shell 的限额(沿用 2.x)。</summary>
public static class ShellLimits
{
    public const int DefaultTimeoutMs = 120_000;

    /// <summary>最长 10 分钟。再长的活应该拆开,或者用 <c>run_in_background</c>。</summary>
    public const int MaxTimeoutMs = 600_000;

    /// <summary>
    /// 前台命令每路输出(stdout / stderr)给模型的字符上限。超过就<b>保留尾部</b>(命令的结尾通常最有用:错误信息、最后的进度),
    /// 完整输出存临时文件并在结果里给出路径(pi)。两路相加 + 说明文字低于循环层的 64K 截断线。
    /// </summary>
    public const int MaxOutputChars = 30_000;

    /// <summary>
    /// 同时在跑的后台 shell 上限。后台进程不随 Run 结束而结束,而模型看不到自己开了多少个:
    /// 没有闸门时一次「起服务 → 看日志 → 再起一个」的循环能在一次会话里留下几十个还占着端口的进程。
    /// </summary>
    public const int MaxRunning = 8;

    /// <summary>连同已结束的一起记这么多条;超了从最早结束的那条开始淘汰。</summary>
    public const int MaxTracked = 24;

    /// <summary>单条 shell 的 stdout / stderr 各自的缓冲上限。超出后丢最早的,并置 <c>Dropped</c>。</summary>
    public const int MaxBufferChars = 256 * 1024;

    /// <summary>BashOutput 一次回读给模型的上限。比前台小:日志是会一直长的,而它每轮都可能读一次。</summary>
    public const int MaxReadChars = 16_000;

    /// <summary>界面「看一眼」用的输出尾巴长度。</summary>
    public const int PeekChars = 8 * 1024;
}

/// <summary>
/// 一个后台 shell 的结局。<c>Killed</c> 与 <c>Exited</c> 分开:模型读到 <c>exited(143)</c> 会去猜是什么信号打死了它,
/// 而「是我们自己杀的」这件事没有任何办法从退出码反推出来。
/// </summary>
public enum BackgroundShellStatus
{
    Running,
    Exited,
    Killed,
}

/// <param name="Id">给模型的短 id(<c>bash_1</c>)。进程内唯一且<b>不复用</b>:复用的话,模型拿着旧 id 读到的是另一条命令的输出,而两边都不会报错。</param>
/// <param name="ExitCode"><c>Exited</c> 时才有意义;被信号带走或连接中断时为 <c>null</c>。</param>
public sealed record BackgroundShellInfo(
    string Id,
    string Command,
    string? Description,
    EnvPath Cwd,
    BackgroundShellStatus Status,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    RunId RunId,
    string CallId,
    EnvironmentId Environment);

/// <summary>一次回读的结果。<b>读过即清空</b>,所以两次读之间的新输出正好不重不漏。<see cref="Dropped"/>:缓冲满过,最早的一段已经丢了(必须告诉模型,否则它会拿一段缺了头的日志去归因)。</summary>
public sealed record ShellRead(BackgroundShellInfo Info, string Stdout, string Stderr, bool Dropped);

public sealed record ShellStartRequest(string Command, EnvPath Cwd, string? Description, RunId RunId, string CallId);

/// <summary>一条前台命令(Run + 调用):「停这一条」「转后台」用它定位。</summary>
public sealed record ForegroundCall(RunId RunId, string CallId, string Command);

/// <summary>注册表抛出的、给模型看的英文原因:工具原样转成失败结果。</summary>
public sealed class ShellRegistryException(string message) : Exception(message);

/// <summary>
/// 一条从前台转走、仍在跑的进程(带着它已经收集的输出)。只能由 Bash 工具造出来,交给 <see cref="IShellRegistry.Adopt"/>;
/// 收编失败时调用方负责 <see cref="KillAsync"/>(否则它会成为一个谁也看不见、也停不了的孤儿)。
/// </summary>
public sealed class DetachedShell
{
    internal DetachedShell(IExecutionEnvironment environment, IEnvProcess process, ShellCapture capture)
    {
        Environment = environment;
        Process = process;
        Capture = capture;
    }

    internal IExecutionEnvironment Environment { get; }

    internal IEnvProcess Process { get; }

    internal ShellCapture Capture { get; }

    public async ValueTask KillAsync()
    {
        await Process.KillAsync(CancellationToken.None).ConfigureAwait(false);
        await Process.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// 工具与编排层之间唯一的接触面(03 §1 的 <c>Shells</c> 能力;同 2.x 的 <c>ShellBridge</c>)。失败一律抛 <see cref="ShellRegistryException"/>
/// (模型读得懂的英文原因),由工具转成失败结果。默认实现是 <see cref="BackgroundShells"/>。
/// </summary>
public interface IShellRegistry
{
    /// <summary>
    /// 前台命令:登记一个「只停这一条命令」的句柄,返回注销用的 <see cref="IDisposable"/>。<b>必须</b>在 finally 里释放:
    /// 漏掉的话注册表会攥着一个早就跑完的 callId,用户下一次点那张卡上的停止,停的是一条不存在的命令。
    /// </summary>
    IDisposable Hold(ForegroundCall call, Action stop, Action? detach);

    int RunningCount { get; }

    ValueTask<BackgroundShellInfo> StartAsync(ShellStartRequest request, IExecutionEnvironment environment, CancellationToken ct);

    /// <summary>收编一条从前台转走、仍在跑的进程,从此它就是一个普通的后台 shell。名额满了抛 <see cref="ShellRegistryException"/>。</summary>
    BackgroundShellInfo Adopt(DetachedShell shell, ShellStartRequest request);

    ShellRead Read(string id, Regex? filter);

    ValueTask<BackgroundShellInfo> KillAsync(string id, CancellationToken ct);

    IReadOnlyList<BackgroundShellInfo> List();
}
