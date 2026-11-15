using System.ComponentModel;
using System.Diagnostics;

namespace Ncw.Agent.Environments;

/// <summary>本机进程。<see cref="IEnvProcess.KillAsync"/> 终止整棵进程树(见 <see cref="ProcessTree"/>)。</summary>
internal sealed class LocalProcess : IEnvProcess
{
    private readonly Process _process;
    private readonly TimeSpan _killGrace;
    private readonly Task<ProcessExit> _exited;
    private int _killRequested;

    private LocalProcess(EnvironmentId id, Process process, TimeSpan killGrace)
    {
        EnvironmentId = id;
        _process = process;
        _killGrace = killGrace;
        _exited = WaitAsync();
    }

    public EnvironmentId EnvironmentId { get; }

    public int Generation => 0;

    public Stream? Stdin { get; private set; }

    public Stream Stdout => _process.StandardOutput.BaseStream;

    public Stream Stderr => _process.StandardError.BaseStream;

    public Task<ProcessExit> Exited => _exited;

    public static LocalProcess Start(EnvironmentId id, ProcessStartInfo startInfo, StdinMode stdin, TimeSpan killGrace)
    {
        var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            process.Dispose();
            throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"{startInfo.FileName}: {e.Message}", e);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            process.Dispose();
            throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, $"{startInfo.FileName}: {e.Message}", e);
        }

        var result = new LocalProcess(id, process, killGrace);
        if (stdin == StdinMode.Pipe)
        {
            result.Stdin = process.StandardInput.BaseStream;
        }
        else
        {
            // 立刻关闭:等输入的命令得到 EOF,而不是永远挂在一个没有人会写的管道上
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // 进程已经退出
            }
        }

        return result;
    }

    public async ValueTask KillAsync(CancellationToken ct = default)
    {
        if (_exited.IsCompleted)
        {
            return;
        }

        Interlocked.Exchange(ref _killRequested, 1);
        await ProcessTree.TerminateAsync(_process, _exited, _killGrace).ConfigureAwait(false);
        await _exited.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_exited.IsCompleted)
        {
            try
            {
                await KillAsync().ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 杀不掉就放弃,不让释放卡住
            }
        }

        Stdin?.Dispose();
        _process.Dispose();
    }

    private async Task<ProcessExit> WaitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        var code = _process.ExitCode;
        var killed = Volatile.Read(ref _killRequested) == 1;
        // POSIX 上被信号杀死的进程,.NET 报告 128 + 信号编号
        int? signal = !OperatingSystem.IsWindows() && code > 128 && code < 160 ? code - 128 : null;
        return new ProcessExit(code, signal, killed ? ProcessExitReason.Killed : ProcessExitReason.Exited);
    }
}
