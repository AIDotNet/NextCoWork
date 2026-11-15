using System.Text;
using System.Text.RegularExpressions;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Browser.Launch;

/// <summary>要启动的浏览器:哪个环境、哪个 profile 目录。</summary>
public sealed record BrowserLaunchRequest(IExecutionEnvironment Environment, EnvPath ProfileDirectory, string? ExecutableOverride);

/// <summary>一个已经起来、DevTools 端点可连的浏览器。释放 = 杀掉整棵进程树。</summary>
public sealed class LaunchedBrowser(Uri endpoint, Task exited, Func<ValueTask> dispose, string executable) : IAsyncDisposable
{
    public Uri Endpoint { get; } = endpoint;

    /// <summary>浏览器进程退出(崩溃 / 被杀 / 用户关掉)后完成。</summary>
    public Task Exited { get; } = exited;

    public string Executable { get; } = executable;

    public ValueTask DisposeAsync() => dispose();
}

/// <summary>启动浏览器的唯一接缝。生产实现经 <c>IExecutionEnvironment.OpenProcessAsync</c>;测试注入假的(指向假 CDP 服务)。</summary>
public interface IBrowserLauncher
{
    Task<LaunchedBrowser> LaunchAsync(BrowserLaunchRequest request, CancellationToken ct);
}

/// <summary>在设备上找 Chrome / Edge / Chromium。找不到不下载(下载引入网络与签名问题),给结构化错误。</summary>
public sealed class BrowserLocator(Func<string, string?> getEnvironmentVariable)
{
    public const string OverrideVariable = "NCW_BROWSER_PATH";

    /// <summary>找到可执行文件的绝对路径。显式指定的路径不存在 = <see cref="BrowserErrorKeys.ExecutableMissing"/>,不退回到别的浏览器(用户指定了就是要它)。</summary>
    public async Task<EnvPath> LocateAsync(IExecutionEnvironment env, string? explicitPath, CancellationToken ct)
    {
        var style = env.Facts.PathStyle;
        var configured = !string.IsNullOrWhiteSpace(explicitPath) ? explicitPath : getEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = EnvPath.Parse(configured.Trim(), style);
            if (!await IsFileAsync(env, path, ct).ConfigureAwait(false))
            {
                throw new BrowserException(BrowserErrorKeys.ExecutableMissing, $"The configured browser executable does not exist: {path}. Ask the user to fix the browser path setting.");
            }

            return path;
        }

        foreach (var candidate in Candidates(env.Facts, getEnvironmentVariable))
        {
            ct.ThrowIfCancellationRequested();
            EnvPath path;
            try
            {
                path = EnvPath.Parse(candidate, style);
            }
            catch (EnvironmentException)
            {
                continue;
            }

            if (await IsFileAsync(env, path, ct).ConfigureAwait(false))
            {
                return path;
            }
        }

        throw new BrowserException(
            BrowserErrorKeys.NotInstalled,
            "No Chrome, Edge or Chromium installation was found on this device, and browser tools never download one. Tell the user to install Google Chrome (or Microsoft Edge) or point the browser path setting at an existing executable. Do not retry.");
    }

    private static async Task<bool> IsFileAsync(IExecutionEnvironment env, EnvPath path, CancellationToken ct)
    {
        try
        {
            return !(await env.Fs.StatAsync(path, ct).ConfigureAwait(false)).IsDirectory;
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory or EnvironmentErrorCode.PermissionDenied or EnvironmentErrorCode.InvalidPath)
        {
            return false;
        }
    }

    /// <summary>按偏好顺序的候选路径(Chrome 优先于 Edge 优先于 Chromium)。公开给测试。</summary>
    public static IEnumerable<string> Candidates(EnvironmentFacts facts, Func<string, string?> env)
    {
        switch (facts.Os)
        {
            case EnvironmentOs.MacOS:
                foreach (var root in new[] { "/Applications", $"{facts.Home}/Applications" })
                {
                    yield return $"{root}/Google Chrome.app/Contents/MacOS/Google Chrome";
                    yield return $"{root}/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
                    yield return $"{root}/Chromium.app/Contents/MacOS/Chromium";
                    yield return $"{root}/Brave Browser.app/Contents/MacOS/Brave Browser";
                }

                break;
            case EnvironmentOs.Windows:
                foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "LocalAppData" })
                {
                    if (env(variable) is not { Length: > 0 } root)
                    {
                        continue;
                    }

                    var trimmed = root.TrimEnd('\\', '/');
                    yield return $"{trimmed}\\Google\\Chrome\\Application\\chrome.exe";
                    yield return $"{trimmed}\\Microsoft\\Edge\\Application\\msedge.exe";
                    yield return $"{trimmed}\\Chromium\\Application\\chrome.exe";
                }

                break;
            default:
                var names = new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "microsoft-edge", "microsoft-edge-stable", "chrome" };
                foreach (var dir in (env("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
                {
                    foreach (var name in names)
                    {
                        yield return $"{dir.TrimEnd('/')}/{name}";
                    }
                }

                foreach (var fixedPath in new[] { "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/opt/google/chrome/chrome", "/snap/bin/chromium", "/usr/bin/microsoft-edge" })
                {
                    yield return fixedPath;
                }

                break;
        }
    }
}

/// <summary>
/// 生产启动器:经 <see cref="IExecutionEnvironment.OpenProcessAsync"/> 起 <c>--headless=new --remote-debugging-port=0</c>,从 stderr 读 <c>DevTools listening on ws://…</c>。
/// 只支持本机环境(SSH 上的浏览器要端口转发,留给后续;给结构化错误,不假装能用)。
/// </summary>
public sealed partial class ProcessBrowserLauncher(BrowserLocator locator, TimeSpan launchTimeout) : IBrowserLauncher
{
    [GeneratedRegex(@"DevTools listening on (ws://[^\s]+)[\r\n]")]
    private static partial Regex Listening();

    public async Task<LaunchedBrowser> LaunchAsync(BrowserLaunchRequest request, CancellationToken ct)
    {
        var env = request.Environment;
        if (env.Kind != EnvironmentKind.Local)
        {
            throw new BrowserException(
                BrowserErrorKeys.RemoteEnvironment,
                $"Browser tools only work in the local environment of the device, but this project runs in '{env.DisplayName}'. Do not retry; use WebFetch for plain pages instead.");
        }

        var executable = await locator.LocateAsync(env, request.ExecutableOverride, ct).ConfigureAwait(false);
        await env.Fs.MkdirAsync(request.ProfileDirectory, recursive: true, ct).ConfigureAwait(false);

        var args = new List<string>
        {
            "--headless=new",
            "--remote-debugging-port=0",
            $"--user-data-dir={request.ProfileDirectory}",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-extensions",
            "--disable-sync",
            "--disable-default-apps",
            "--disable-background-networking",
            "--disable-component-update",
            "--hide-scrollbars",
            "--mute-audio",
            "--window-size=1280,720",
        };

        // 容器里常以 root 运行,Chrome 在 root 下拒绝启动沙箱;只在这种情况下关沙箱(云端 Agent 的沙箱本身就是隔离边界)。
        if (env.Facts.Os == EnvironmentOs.Linux && env.Facts.Username == "root")
        {
            args.Add("--no-sandbox");
        }

        args.Add("about:blank");

        IEnvProcess process;
        try
        {
            process = await env.OpenProcessAsync(CommandSpec.Exec(executable.Value, [.. args]), ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e)
        {
            throw new BrowserException(BrowserErrorKeys.LaunchFailed, $"The browser could not be started: {e.Detail ?? e.Code.ToString()}", e);
        }

        // 后台排空两路输出(管道写满会把浏览器挂住),只留 stderr 的尾巴供诊断
        var stderrTail = new StringBuilder();
        var endpoint = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = DrainAsync(process.Stdout, null, null);
        _ = DrainAsync(process.Stderr, stderrTail, endpoint);
        _ = process.Exited.ContinueWith(
            _ => endpoint.TrySetException(new BrowserException(BrowserErrorKeys.LaunchFailed, $"The browser exited during startup.{Tail(stderrTail)}")),
            TaskScheduler.Default);

        try
        {
            var uri = await endpoint.Task.WaitAsync(launchTimeout, ct).ConfigureAwait(false);
            return new LaunchedBrowser(uri, process.Exited, () => process.KillAsync(), executable.Value);
        }
        catch (TimeoutException)
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw new BrowserException(BrowserErrorKeys.LaunchFailed, $"The browser did not report its DevTools endpoint within {launchTimeout.TotalSeconds:0} seconds.{Tail(stderrTail)}");
        }
        catch
        {
            await process.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static string Tail(StringBuilder text)
    {
        lock (text)
        {
            return text.Length == 0 ? "" : $" Browser output: {text.ToString().Trim()}";
        }
    }

    private static async Task DrainAsync(Stream stream, StringBuilder? tail, TaskCompletionSource<Uri>? endpoint)
    {
        var buffer = new byte[4096];
        var window = new StringBuilder();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                var text = Encoding.UTF8.GetString(buffer, 0, read);
                if (tail is not null)
                {
                    lock (tail)
                    {
                        tail.Append(text);
                        if (tail.Length > 2048)
                        {
                            tail.Remove(0, tail.Length - 2048);
                        }
                    }
                }

                if (endpoint is { Task.IsCompleted: false })
                {
                    window.Append(text);
                    if (Listening().Match(window.ToString()) is { Success: true } match && Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri))
                    {
                        endpoint.TrySetResult(uri);
                    }
                    else if (window.Length > 8192)
                    {
                        window.Remove(0, window.Length - 4096);
                    }
                }
            }
        }
        catch
        {
            // 进程结束 / 流关闭:排空结束
        }
    }
}
