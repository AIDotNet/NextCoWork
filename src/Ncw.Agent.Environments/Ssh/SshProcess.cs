using System.Text;
using Renci.SshNet;

namespace Ncw.Agent.Environments.Ssh;

/// <summary>
/// 远程进程:SSH exec 通道。stdout 直接来自通道,stderr 经 <see cref="PidMarkerStream"/> 剥掉开头的 pid 标记。
/// 终止:拿到 pid 后在<b>控制连接的另一条通道</b>上执行 <c>kill</c>(先 TERM 进程组,宽限期后 KILL),拿不到 pid 时直接放弃通道。
/// 连接中断时进程以 <see cref="ProcessExitReason.ConnectionLost"/> 结束,退出码未知(不能报成「正常退出」)。
/// </summary>
internal sealed class SshProcess : IEnvProcess
{
    private readonly SshLink _link;
    private readonly SshCommand _command;
    private readonly PidMarkerStream _stderr;
    private readonly EnvironmentOs _os;
    private readonly TimeSpan _killGrace;
    private readonly CancellationTokenSource _abandon = new();
    private readonly Task<ProcessExit> _exited;
    private int _killRequested;

    private SshProcess(EnvironmentId id, SshLink link, SshCommand command, EnvironmentOs os, TimeSpan killGrace)
    {
        EnvironmentId = id;
        _link = link;
        _command = command;
        _os = os;
        _killGrace = killGrace;
        _stderr = new PidMarkerStream(command.ExtendedOutputStream);
        // 构造即开始执行(RunAsync 的同步部分会打开通道并发出 exec 请求)
        _exited = RunAsync();
    }

    public EnvironmentId EnvironmentId { get; }

    public int Generation => _link.Generation;

    public Stream? Stdin { get; private set; }

    public Stream Stdout => _command.OutputStream;

    public Stream Stderr => _stderr;

    public Task<ProcessExit> Exited => _exited;

    public static SshProcess Start(EnvironmentId id, SshLink link, RemoteLaunch launch, StdinMode stdinMode, EnvironmentOs os, TimeSpan killGrace)
    {
        var command = link.Control.CreateCommand(launch.CommandText);
        command.CommandTimeout = Timeout.InfiniteTimeSpan;
        var process = new SshProcess(id, link, command, os, killGrace);

        // SSH.NET 的输入流只能在执行开始之后创建;命令极短、已经结束时创建会失败——那时 stdin 已经没有意义
        Stream? input = null;
        try
        {
            input = command.CreateInputStream();
        }
        catch (InvalidOperationException)
        {
        }

        if (input is not null)
        {
            var ready = process.InitInputAsync(input, launch.StdinPrefix, stdinMode);
            process.Stdin = stdinMode == StdinMode.Pipe ? new SequencedInput(input, ready) : null;
        }

        return process;
    }

    public async ValueTask KillAsync(CancellationToken ct = default)
    {
        if (_exited.IsCompleted)
        {
            return;
        }

        Interlocked.Exchange(ref _killRequested, 1);
        int? pid = null;
        try
        {
            pid = await _stderr.Pid.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        if (pid is { } id)
        {
            await SendKillAsync(id, force: false).ConfigureAwait(false);
            if (!await WaitExitAsync(_killGrace, ct).ConfigureAwait(false))
            {
                await SendKillAsync(id, force: true).ConfigureAwait(false);
                if (await WaitExitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
                {
                    return;
                }
            }
            else
            {
                return;
            }
        }

        // 拿不到 pid,或者 KILL 也没让通道结束:放弃这条通道
        await _abandon.CancelAsync().ConfigureAwait(false);
        await WaitExitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_exited.IsCompleted)
        {
            try
            {
                await KillAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException)
            {
            }
        }

        try
        {
            Stdin?.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or Renci.SshNet.Common.SshException)
        {
        }

        _command.Dispose();
        _stderr.Dispose();
        _abandon.Dispose();
    }

    private async Task InitInputAsync(Stream input, string? prefix, StdinMode mode)
    {
        try
        {
            if (prefix is not null)
            {
                await input.WriteAsync(Encoding.UTF8.GetBytes(prefix)).ConfigureAwait(false);
            }

            if (mode == StdinMode.Closed)
            {
                await input.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or Renci.SshNet.Common.SshException)
        {
            // 连接已断:退出任务会报告 ConnectionLost
        }
    }

    private async Task<ProcessExit> RunAsync()
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_abandon.Token, _link.Lost);
        try
        {
            // 以「收到 exit-status」为退出,不等通道关闭:后台子进程还攥着 stdout/stderr 时通道会一直开着(和本机进程一样,调用方不能被它们挂住)
            var executing = _command.ExecuteAsync(linked.Token);
            while (!executing.IsCompleted && _command.ExitStatus is null && _command.ExitSignal is null)
            {
                await Task.WhenAny(executing, Task.Delay(40)).ConfigureAwait(false);
            }

            if (!executing.IsCompleted)
            {
                // 通道稍后会随连接或释放而结束;它的异常不再有人关心
                _ = executing.ContinueWith(static t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            else
            {
                await executing.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (_link.Lost.IsCancellationRequested && Volatile.Read(ref _killRequested) == 0)
            {
                return new ProcessExit(null, null, ProcessExitReason.ConnectionLost);
            }

            return new ProcessExit(_command.ExitStatus, SignalNumber(_command.ExitSignal), ProcessExitReason.Killed);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (SshErrors.IsConnectionLoss(e))
            {
                _link.MarkBroken();
            }

            return new ProcessExit(null, null, ProcessExitReason.ConnectionLost);
        }

        var killed = Volatile.Read(ref _killRequested) == 1;
        return new ProcessExit(_command.ExitStatus, SignalNumber(_command.ExitSignal), killed ? ProcessExitReason.Killed : ProcessExitReason.Exited);
    }

    private async Task SendKillAsync(int pid, bool force)
    {
        try
        {
            await Task.Run(() => _link.Control.RunCommand(RemoteCommands.Kill(_os, pid, force))).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Renci.SshNet.Common.SshException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            // 控制连接已断:进程随连接一起结束
        }
    }

    private async Task<bool> WaitExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _exited.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static int? SignalNumber(string? name) => name?.ToUpperInvariant().Replace("SIG", "", StringComparison.Ordinal) switch
    {
        "HUP" => 1,
        "INT" => 2,
        "QUIT" => 3,
        "ABRT" => 6,
        "KILL" => 9,
        "SEGV" => 11,
        "PIPE" => 13,
        "TERM" => 15,
        _ => null,
    };

    /// <summary>Pipe 模式下交给调用方的 stdin:第一次写之前先等环境变量前缀写完,避免两者交错。</summary>
    private sealed class SequencedInput(Stream inner, Task ready) : ForwardingStream(inner)
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await Inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            await Inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}
