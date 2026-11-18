using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Tools.Tests;

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 30_000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"timed out waiting for {what ?? "condition"}");
            }

            await Task.Delay(15);
        }
    }

    public static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>假进程:输出管道由测试写入(真的管道,读取方走的是真实的异步读取路径),退出由测试触发。</summary>
public sealed class FakeProcess : IEnvProcess
{
    private readonly AnonymousPipeServerStream _out = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream _err = new(PipeDirection.Out);
    private readonly TaskCompletionSource<ProcessExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _kills;

    public FakeProcess()
    {
        Stdout = new AnonymousPipeClientStream(PipeDirection.In, _out.ClientSafePipeHandle);
        Stderr = new AnonymousPipeClientStream(PipeDirection.In, _err.ClientSafePipeHandle);
    }

    public EnvironmentId EnvironmentId => EnvironmentId.Local("dev-1");

    public int Generation => 0;

    public Stream? Stdin => null;

    public Stream Stdout { get; }

    public Stream Stderr { get; }

    public Task<ProcessExit> Exited => _exited.Task;

    public int Kills => Volatile.Read(ref _kills);

    public bool Disposed { get; private set; }

    public void Emit(bool stderr, string text) => EmitRaw(stderr, Encoding.UTF8.GetBytes(text));

    public void EmitRaw(bool stderr, byte[] bytes)
    {
        var stream = stderr ? _err : _out;
        stream.Write(bytes);
        stream.Flush();
    }

    /// <summary>进程退出:和真进程一样,退出时它自己那端的管道也关了(读取方读到 EOF)。</summary>
    public void Finish(int? code)
    {
        _exited.TrySetResult(new ProcessExit(code, null, ProcessExitReason.Exited));
        _out.Dispose();
        _err.Dispose();
    }

    public ValueTask KillAsync(CancellationToken ct = default)
    {
        Interlocked.Increment(ref _kills);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _out.Dispose();
        _err.Dispose();
        Stdout.Dispose();
        Stderr.Dispose();
        return ValueTask.CompletedTask;
    }
}
