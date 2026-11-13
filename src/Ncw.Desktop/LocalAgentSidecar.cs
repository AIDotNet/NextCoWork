using System.Diagnostics;
using System.Security.Cryptography;

namespace Ncw.Desktop;

/// <summary>
/// 本机 Agent(sidecar)的守护:找到 ncw-agent、拉起、等它报告 ready、意外退出时按退避重启、退出时结束它。
/// 本机令牌每次桌面端启动随机生成,只经环境变量传给子进程(docs/design/09 §5)。
/// Agent 带着 --parent-pid 运行,桌面端崩溃时它自己退出,不会留下孤儿进程。
/// </summary>
public sealed class LocalAgentSidecar : IAsyncDisposable
{
    private readonly string _dataRoot;
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private Task _supervisor = Task.CompletedTask;

    public LocalAgentSidecar(string dataRoot)
    {
        _dataRoot = dataRoot;
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    public string Token { get; }

    public string DataRoot => _dataRoot;

    /// <summary>
    /// 开发阶段的数据根:<c>~/.next-cowork/agent-dev</c>(<c>NCW_AGENT_DATA_ROOT</c> 可改)。
    /// 2.x 数据接管(04 §4)做好之前,不碰 2.x 的 <c>~/.next-cowork/data</c>。
    /// </summary>
    public static string DefaultDataRoot =>
        Environment.GetEnvironmentVariable("NCW_AGENT_DATA_ROOT") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".next-cowork", "agent-dev");

    /// <summary>
    /// 找 ncw-agent:<c>NCW_AGENT_PATH</c>,否则应用目录下的 <c>agent/ncw-agent</c>(构建时由 Ncw.Desktop.csproj 复制进来,
    /// 发布包里是 NativeAOT 的单文件)。
    /// </summary>
    public static string? Locate()
    {
        if (Environment.GetEnvironmentVariable("NCW_AGENT_PATH") is { Length: > 0 } custom && File.Exists(custom))
        {
            return custom;
        }

        var name = OperatingSystem.IsWindows() ? "ncw-agent.exe" : "ncw-agent";
        var path = Path.Combine(AppContext.BaseDirectory, "agent", name);
        return File.Exists(path) ? path : null;
    }

    public void Start() => _supervisor = Task.Run(SuperviseAsync);

    /// <summary>等到当前这一次启动的 Agent 报告 ready。</summary>
    public Task WaitReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

    private async Task SuperviseAsync()
    {
        var restarts = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            if (Locate() is not { } executable)
            {
                // 找不到 Agent:连接会一直失败,界面显示离线。隔一会儿再找(开发时可能还在构建)。
                await DelayAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                continue;
            }

            var psi = new ProcessStartInfo(executable,
                ["--mode", "local", "--data-root", _dataRoot, "--parent-pid", Environment.ProcessId.ToString()])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.Environment["NCW_LOCAL_TOKEN"] = Token;

            try
            {
                _process = Process.Start(psi);
            }
            catch (Exception)
            {
                _process = null;
            }

            if (_process is null)
            {
                await DelayAsync(Backoff(++restarts)).ConfigureAwait(false);
                continue;
            }

            var ready = _ready;
            _ = Task.Run(async () =>
            {
                while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (line.StartsWith("ncw-agent ready", StringComparison.Ordinal))
                    {
                        ready.TrySetResult();
                    }
                }
            });
            _ = Task.Run(async () =>
            {
                while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                    // 先丢掉;诊断页做好后写进日志(07 §3「诊断」)。
                }
            });

            try
            {
                await _process.WaitForExitAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // 意外退出:新的一轮启动用新的 ready 信号,退避后重启。
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await DelayAsync(Backoff(++restarts)).ConfigureAwait(false);
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt, 5))));

    private async Task DelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_process is { HasExited: false } process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        try
        {
            await _supervisor.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _process?.Dispose();
        _lifetime.Dispose();
    }
}
