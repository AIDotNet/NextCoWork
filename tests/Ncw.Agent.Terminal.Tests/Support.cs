using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Terminal.Tests;

/// <summary>把 <see cref="ITerminal.Stream"/> 的输出攒起来的读取器(UTF-8 解码跨块安全)。</summary>
internal sealed class Collector : IAsyncDisposable
{
    private readonly ITerminal _terminal;
    private readonly Task _pump;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _text = new();
    private readonly object _gate = new();

    public Collector(ITerminal terminal)
    {
        _terminal = terminal;
        _pump = Task.Run(Pump);
    }

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    public Task Completed => _pump;

    public Task SendAsync(string text) => _terminal.Stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

    public Task SendLineAsync(string text) => SendAsync(text + "\n");

    public async Task WaitForAsync(string expected, int timeoutMs = 15_000)
    {
        try
        {
            await Wait.UntilAsync(() => Text.Contains(expected, StringComparison.Ordinal), timeoutMs, $"output containing '{expected}'");
        }
        catch (TimeoutException e)
        {
            // 「got」必须是超时那一刻的输出:写在上面那个插值串里的话,取到的是开始等待时的(几乎总是空的),会让人误以为终端什么都没输出
            throw new TimeoutException($"{e.Message}; got: {Text}");
        }
    }

    private async Task Pump()
    {
        var buffer = new byte[8192];
        var chars = new char[8192];
        for (;;)
        {
            int read;
            try
            {
                read = await _terminal.Stream.ReadAsync(buffer);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            if (read == 0)
            {
                return;
            }

            lock (_gate)
            {
                var count = _decoder.GetChars(buffer, 0, read, chars, 0);
                _text.Append(chars, 0, count);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _terminal.DisposeAsync();
        await _pump.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

internal static class Wait
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

            await Task.Delay(20);
        }
    }

    public static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ncw-term-" + Guid.NewGuid().ToString("N")[..12]));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public EnvPath Env => EnvPath.Parse(Path, OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class Env
{
    /// <summary>真 PTY 的用例只在 POSIX 上跑(Windows 的 ConPTY 另有 <c>WindowsConPtyTests</c>,在 Windows 机器上才执行)。</summary>
    public static bool Posix => !OperatingSystem.IsWindows();

    public static LocalEnvironment NewLocal(LocalPtyOptions? pty = null, string? shell = null) =>
        new("dev", new LocalEnvironmentOptions
        {
            Shell = shell,
            TerminalFactory = LocalTerminals.CreateFactory(pty ?? new LocalPtyOptions { KillGrace = TimeSpan.FromMilliseconds(500) }),
            KillGrace = TimeSpan.FromMilliseconds(500),
        });
}
