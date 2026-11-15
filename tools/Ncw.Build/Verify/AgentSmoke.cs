using System.Diagnostics;
using System.Security.Cryptography;
using Ncw.Build.Core;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

namespace Ncw.Build.Verify;

internal sealed record SmokeResult(
    string DeviceId,
    string AgentVersion,
    string DeviceInfoVersion,
    string Os,
    int ProtocolVersion,
    TimeSpan ReadyAfter,
    int SecondInstanceExitCode,
    int ShutdownExitCode,
    TimeSpan ShutdownAfter);

/// <summary>
/// 冒烟(沿用 2.x agent-smoke 的思路):把打包产物里的 ncw-agent 当真实进程跑一遍。
/// <list type="number">
/// <item><c>--mode local</c> 起来,报告 ready;</item>
/// <item>本机通道 + 本机令牌握手,<c>device.info</c> 报告版本(必须等于包的版本);</item>
/// <item>同一数据根再起一个:必须被单实例锁拒绝(退出码 75),并且不能抢走第一个的端点;</item>
/// <item>关闭它的 stdin(桌面端优雅关闭用的同一个机制):必须在限时内以 0 退出。</item>
/// </list>
/// </summary>
internal static class AgentSmoke
{
    /// <summary>ncw-agent 拒绝启动第二个实例时的退出码(EX_TEMPFAIL)。</summary>
    public const int AlreadyRunningExitCode = 75;

    public static async Task<SmokeResult> RunAsync(string agentExe, string? expectedVersion, IBuildLog log, TimeSpan timeout, CancellationToken ct)
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "ncw-verify-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dataRoot);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        AgentProcess? first = null;
        try
        {
            var started = Stopwatch.StartNew();
            first = AgentProcess.Start(agentExe, dataRoot, token);
            log.Info($"started ncw-agent pid {first.Id} (data root {dataRoot})");
            var ready = await first.WaitForLineAsync("ncw-agent ready", timeout, ct).ConfigureAwait(false)
                ?? throw new BuildException($"ncw-agent did not report ready within {timeout.TotalSeconds:F0}s.{first.Diagnostics()}");
            var readyAfter = started.Elapsed;
            log.Info($"ready after {readyAfter.TotalMilliseconds:F0} ms: {ready}");

            // 本机通道握手 + device.info
            var endpoint = LocalTransport.DefaultEndpoint(dataRoot);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(timeout);
            var stream = await LocalTransport.ConnectAsync(endpoint, connectTimeout.Token).ConfigureAwait(false);
            var client = await DeviceClient.ConnectAsync(stream, new ClientHello("ncw-build-verify", expectedVersion ?? "0.0.0", LocalToken: token), connectTimeout.Token).ConfigureAwait(false);
            DeviceInfoResult info;
            await using (client.ConfigureAwait(false))
            {
                info = await client.CallAsync(DeviceMethods.Info, Unit.Value, connectTimeout.Token).ConfigureAwait(false);
                log.Info($"handshake OK: protocol {client.Welcome.Version}, role {client.Welcome.Role}, device {info.DeviceId}");
                log.Info($"agent version {client.Welcome.DeviceVersion}, os {info.Os}");
                if (expectedVersion is not null && client.Welcome.DeviceVersion != expectedVersion)
                {
                    throw new BuildException($"ncw-agent reports version {client.Welcome.DeviceVersion}, but the bundle is {expectedVersion}.");
                }

                if (info.AgentVersion != client.Welcome.DeviceVersion)
                {
                    throw new BuildException($"device.info version {info.AgentVersion} differs from the welcome version {client.Welcome.DeviceVersion}.");
                }
            }

            // 单实例锁:第二个实例必须被拒绝,第一个必须不受影响
            using (var second = AgentProcess.Start(agentExe, dataRoot, token))
            {
                var exit = await second.WaitForExitAsync(timeout, ct).ConfigureAwait(false);
                if (exit is null)
                {
                    second.Kill();
                    throw new BuildException("A second ncw-agent on the same data root kept running: the single-instance lock is not working.");
                }

                if (exit != AlreadyRunningExitCode)
                {
                    throw new BuildException($"A second ncw-agent on the same data root exited with {exit}; expected {AlreadyRunningExitCode} (already running).{second.Diagnostics()}");
                }

                log.Info($"second instance on the same data root rejected (exit {exit})");
            }

            if (first.HasExited)
            {
                throw new BuildException($"The first ncw-agent died when the second one was rejected.{first.Diagnostics()}");
            }

            // 优雅关闭
            var stop = Stopwatch.StartNew();
            first.CloseStdin();
            var shutdown = await first.WaitForExitAsync(timeout, ct).ConfigureAwait(false);
            if (shutdown is null)
            {
                first.Kill();
                throw new BuildException($"ncw-agent did not exit within {timeout.TotalSeconds:F0}s after its stdin closed.{first.Diagnostics()}");
            }

            if (shutdown != 0)
            {
                throw new BuildException($"ncw-agent exited with {shutdown} on graceful shutdown; expected 0.{first.Diagnostics()}");
            }

            log.Info($"graceful shutdown OK (exit 0, {stop.Elapsed.TotalMilliseconds:F0} ms)");
            return new SmokeResult(info.DeviceId, client.Welcome.DeviceVersion, info.AgentVersion, info.Os, client.Welcome.Version, readyAfter, AlreadyRunningExitCode, 0, stop.Elapsed);
        }
        finally
        {
            first?.Kill();
            first?.Dispose();
            try
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>一个被测的 ncw-agent 进程:收集输出,可以等待某一行、关 stdin、等退出。</summary>
internal sealed class AgentProcess : IDisposable
{
    private readonly Process _process;
    private readonly List<string> _lines = [];
    private readonly SemaphoreSlim _changed = new(0);

    private AgentProcess(Process process)
    {
        _process = process;
    }

    public int Id => _process.Id;

    public bool HasExited => _process.HasExited;

    public static AgentProcess Start(string exe, string dataRoot, string token) =>
        Start(exe, ["--mode", "local", "--data-root", dataRoot, "--lifeline", "stdin"], new Dictionary<string, string> { ["NCW_LOCAL_TOKEN"] = token });

    /// <summary>通用的「被测进程」:重定向 stdin / stdout / stderr,收集输出(<c>build-relay</c> 的产物自检也用它)。</summary>
    public static AgentProcess Start(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var (key, value) in env)
        {
            psi.Environment[key] = value;
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var agent = new AgentProcess(process);
        process.OutputDataReceived += (_, e) => agent.Add(e.Data);
        process.ErrorDataReceived += (_, e) => agent.Add(e.Data is null ? null : "[stderr] " + e.Data);
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new BuildException($"Cannot start {exe}: {e.Message}", e);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return agent;
    }

    private void Add(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_lines)
        {
            _lines.Add(line);
        }

        _changed.Release();
    }

    public async Task<string?> WaitForLineAsync(string prefix, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            lock (_lines)
            {
                if (_lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) is { } hit)
                {
                    return hit;
                }
            }

            if (_process.HasExited || deadline.Elapsed >= timeout)
            {
                return null;
            }

            await _changed.WaitAsync(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
        }
    }

    public async Task<int?> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
            _process.WaitForExit(); // 排空输出
            return _process.ExitCode;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public void CloseStdin()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    public string Diagnostics()
    {
        lock (_lines)
        {
            return _lines.Count == 0 ? "" : "\n    process output:\n      " + string.Join("\n      ", _lines.TakeLast(20));
        }
    }

    public void Dispose() => _process.Dispose();
}
