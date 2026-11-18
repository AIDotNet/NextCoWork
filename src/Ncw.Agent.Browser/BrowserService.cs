using System.Collections.Concurrent;
using Ncw.Agent.Browser.Cdp;
using Ncw.Agent.Browser.Launch;
using Ncw.Agent.Browser.Pages;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Browser;

public sealed class BrowserOptions
{
    /// <summary>profile 根目录(本机环境里的绝对路径,通常是 <c>&lt;数据根&gt;/browser</c>)。每个工作区一个子目录:cookie / 登录态按工作区隔离,互不可见。</summary>
    public required EnvPath ProfileRoot { get; init; }

    /// <summary>显式指定的浏览器可执行文件(设置里的路径)。null = 环境变量 <c>NCW_BROWSER_PATH</c>,再没有就探测常见安装位置。</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>读环境变量的函数(装配方传 <c>Environment.GetEnvironmentVariable</c>;本项目不直接读,便于测试)。</summary>
    public Func<string, string?> GetEnvironmentVariable { get; init; } = static _ => null;

    public IBrowserUrlPolicy UrlPolicy { get; init; } = DefaultBrowserUrlPolicy.Instance;

    /// <summary>启动器。默认:经 <see cref="IExecutionEnvironment"/> 起进程的 <see cref="ProcessBrowserLauncher"/>;测试注入假的。</summary>
    public IBrowserLauncher? Launcher { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan NavigationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>标签页闲置多久后被回收;浏览器进程在没有标签、再闲置一个周期后退出。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>回收检查的间隔。<see cref="TimeSpan.Zero"/> = 不起定时器(测试手动调 <see cref="BrowserService.ReapIdleAsync"/>)。</summary>
    public TimeSpan ReapInterval { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxTabsPerWorkspace { get; init; } = 8;
}

/// <summary>谁在操作:标签归属按「工作区 + (Run 或会话)」判定——同一会话的新 Run 可以继续操作之前打开的标签(2.x 同一条规则)。</summary>
public sealed record BrowserCaller(string WorkspaceId, string RunId, string SessionId);

public sealed class BrowserTab
{
    internal BrowserTab(string id, string workspaceId, string ownerRunId, string ownerSessionId, BrowserPage page, DateTimeOffset now)
    {
        Id = id;
        WorkspaceId = workspaceId;
        OwnerRunId = ownerRunId;
        OwnerSessionId = ownerSessionId;
        Page = page;
        CreatedAt = now;
        LastUsed = now;
    }

    public string Id { get; }

    public string WorkspaceId { get; }

    public string OwnerRunId { get; }

    public string OwnerSessionId { get; }

    internal BrowserPage Page { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastUsed { get; internal set; }
}

/// <summary>
/// 浏览器工具的运行时:每个工作区至多一个无头浏览器进程(profile 目录不能被两个进程同时用),进程里有若干标签页,标签页归属于打开它的 Run / 会话。
/// 进程经 <see cref="IExecutionEnvironment"/> 启动;页面闲置、浏览器无标签闲置、Agent 关闭时回收。
/// </summary>
public sealed class BrowserService : IAsyncDisposable
{
    private readonly BrowserOptions _options;
    private readonly IBrowserLauncher _launcher;
    private readonly ConcurrentDictionary<string, WorkspaceBrowser> _browsers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BrowserTab> _tabs = new(StringComparer.Ordinal);
    private readonly ITimer? _timer;
    private int _disposed;

    public BrowserService(BrowserOptions options)
    {
        _options = options;
        _launcher = options.Launcher ?? new ProcessBrowserLauncher(new BrowserLocator(options.GetEnvironmentVariable), options.LaunchTimeout);
        if (options.ReapInterval > TimeSpan.Zero)
        {
            _timer = options.Clock.CreateTimer(_ => _ = ReapIdleAsync(), null, options.ReapInterval, options.ReapInterval);
        }
    }

    public IBrowserUrlPolicy UrlPolicy => _options.UrlPolicy;

    public TimeSpan NavigationTimeout => _options.NavigationTimeout;

    public int OpenTabCount => _tabs.Count;

    public int RunningBrowserCount => _browsers.Count;

    /// <summary>profile 目录:<c>&lt;ProfileRoot&gt;/&lt;工作区 id(净化)&gt;</c>。</summary>
    public EnvPath ProfileDirectory(string workspaceId) => _options.ProfileRoot.Join(SafeName(workspaceId));

    internal static string SafeName(string id)
    {
        var chars = new System.Text.StringBuilder();
        foreach (var c in id)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            {
                chars.Append(c);
            }
            else
            {
                // 其余字符一律十六进制转义:不同 id 不会撞同一个目录,也不会出现 ".." 或分隔符
                chars.Append('%').Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return chars.Length == 0 ? "default" : chars.ToString();
    }

    /// <summary>开一个新标签并导航。浏览器还没起就先起(经 <paramref name="environment"/>)。</summary>
    public async Task<BrowserTab> OpenAsync(BrowserCaller caller, IExecutionEnvironment environment, string url, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var (browser, connection) = await BrowserFor(caller.WorkspaceId, environment, ct).ConfigureAwait(false);
        if (_tabs.Values.Count(t => t.WorkspaceId == caller.WorkspaceId) >= _options.MaxTabsPerWorkspace)
        {
            throw new BrowserException(BrowserErrorKeys.TabLimit, $"This workspace already has {_options.MaxTabsPerWorkspace} browser tabs open. Close one with browser_close first.");
        }

        BrowserPage page;
        try
        {
            page = await BrowserPage.OpenAsync(connection, _options.Clock, ct).ConfigureAwait(false);
        }
        catch (CdpDisconnectedException e)
        {
            Forget(browser);
            throw new BrowserException(BrowserErrorKeys.BrowserGone, "The browser process went away. Call browser_open again.", e);
        }

        var tab = new BrowserTab($"tab-{Guid.NewGuid():N}"[..12], caller.WorkspaceId, caller.RunId, caller.SessionId, page, _options.Clock.GetUtcNow());
        _tabs[tab.Id] = tab;
        try
        {
            await page.NavigateAsync(url, _options.NavigationTimeout, ct).ConfigureAwait(false);
            return tab;
        }
        catch
        {
            await CloseAsync(tab).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<(WorkspaceBrowser Browser, CdpConnection Connection)> BrowserFor(string workspaceId, IExecutionEnvironment environment, CancellationToken ct)
    {
        if (environment.Kind != EnvironmentKind.Local)
        {
            // 在锁之前就拒绝:不为一个注定失败的请求占一个工作区的启动锁
            throw new BrowserException(
                BrowserErrorKeys.RemoteEnvironment,
                $"Browser tools only work in the local environment of the device, but this project runs in '{environment.DisplayName}'. Do not retry; use WebFetch for plain pages instead.");
        }

        WorkspaceBrowser browser;
        while (true)
        {
            browser = _browsers.GetOrAdd(workspaceId, static _ => new WorkspaceBrowser());
            await browser.Gate.WaitAsync(ct).ConfigureAwait(false);
            // 等锁期间它可能已被回收(从表里摘掉了):摘掉的对象不能再起浏览器,否则同一个 profile 会有两个进程
            if (_browsers.TryGetValue(workspaceId, out var current) && ReferenceEquals(current, browser))
            {
                break;
            }

            browser.Gate.Release();
        }

        try
        {
            if (browser.Connection is { IsOpen: true } alive && browser.Launched is not null && !browser.Launched.Exited.IsCompleted)
            {
                return (browser, alive);
            }

            DropTabsOf(browser);
            await browser.TearDownAsync().ConfigureAwait(false);
            var launched = await _launcher.LaunchAsync(new BrowserLaunchRequest(environment, ProfileDirectory(workspaceId), _options.ExecutablePath), ct).ConfigureAwait(false);
            CdpConnection connection;
            try
            {
                connection = await CdpConnection.ConnectAsync(launched.Endpoint, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await launched.DisposeAsync().ConfigureAwait(false);
                throw new BrowserException(BrowserErrorKeys.LaunchFailed, $"Could not connect to the browser's DevTools endpoint: {e.Message}", e);
            }
            catch
            {
                await launched.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            try
            {
                // 下载一律拒绝:页面触发的下载不该悄悄往设备上落文件
                await connection.SendAsync("Browser.setDownloadBehavior", w => w.WriteString("behavior", "deny"), ct: ct).ConfigureAwait(false);
            }
            catch (CdpException)
            {
                // 老版本没有这条命令;不挡使用
            }

            browser.Launched = launched;
            browser.Connection = connection;
            browser.LastUsed = _options.Clock.GetUtcNow();
            _ = WatchAsync(workspaceId, browser, launched, connection);
            return (browser, connection);
        }
        finally
        {
            browser.Gate.Release();
        }
    }

    /// <summary>浏览器进程退出或连接断开:丢掉它的标签。下一次 <see cref="OpenAsync"/> 重新起。</summary>
    private async Task WatchAsync(string workspaceId, WorkspaceBrowser browser, LaunchedBrowser launched, CdpConnection connection)
    {
        await Task.WhenAny(launched.Exited, connection.Closed).ConfigureAwait(false);
        if (ReferenceEquals(browser.Launched, launched))
        {
            Forget(browser);
            _browsers.TryRemove(new KeyValuePair<string, WorkspaceBrowser>(workspaceId, browser));
            await browser.TearDownAsync().ConfigureAwait(false);
        }
    }

    private void Forget(WorkspaceBrowser browser) => DropTabsOf(browser);

    private void DropTabsOf(WorkspaceBrowser browser)
    {
        foreach (var tab in _tabs.Values.Where(t => ReferenceEquals(t.Page.Connection, browser.Connection)).ToList())
        {
            _tabs.TryRemove(tab.Id, out _);
        }
    }

    /// <summary>按 id 找标签并校验归属。归属 = 同工作区,且是同一个 Run 或同一个会话开的。</summary>
    public BrowserTab Resolve(string tabId, BrowserCaller caller)
    {
        if (!_tabs.TryGetValue(tabId, out var tab) || tab.Page.Gone)
        {
            if (tab is not null)
            {
                _tabs.TryRemove(tabId, out _);
            }

            throw new BrowserException(BrowserErrorKeys.TabNotFound, $"Browser tab does not exist: {tabId}. It may have been closed or the browser exited; use browser_tabs to list tabs or browser_open to open a new one.");
        }

        if (tab.WorkspaceId != caller.WorkspaceId)
        {
            throw new BrowserException(BrowserErrorKeys.TabNotOwned, "That browser tab belongs to another workspace.");
        }

        if (tab.OwnerRunId != caller.RunId && tab.OwnerSessionId != caller.SessionId)
        {
            throw new BrowserException(BrowserErrorKeys.TabNotOwned, "You can only operate a browser tab opened by this Agent session.");
        }

        tab.LastUsed = _options.Clock.GetUtcNow();
        return tab;
    }

    /// <summary>这个调用方能操作的标签(同工作区,同 Run 或同会话)。</summary>
    public IReadOnlyList<BrowserTab> List(BrowserCaller caller) =>
        [.. _tabs.Values
            .Where(t => !t.Page.Gone && t.WorkspaceId == caller.WorkspaceId && (t.OwnerRunId == caller.RunId || t.OwnerSessionId == caller.SessionId))
            .OrderBy(t => t.CreatedAt)];

    public async Task CloseAsync(BrowserTab tab)
    {
        _tabs.TryRemove(tab.Id, out _);
        await tab.Page.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>回收闲置的标签页;没有标签且闲置超时的浏览器进程退出。返回回收的(标签数, 进程数)。</summary>
    public async Task<(int Tabs, int Browsers)> ReapIdleAsync()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return (0, 0);
        }

        var now = _options.Clock.GetUtcNow();
        var tabs = 0;
        var browsers = 0;
        foreach (var tab in _tabs.Values.ToList())
        {
            if (now - tab.LastUsed >= _options.IdleTimeout || tab.Page.Gone)
            {
                await CloseAsync(tab).ConfigureAwait(false);
                tabs++;
                if (_browsers.TryGetValue(tab.WorkspaceId, out var owner))
                {
                    owner.LastUsed = now;
                }
            }
        }

        foreach (var (workspaceId, browser) in _browsers.ToList())
        {
            if (_tabs.Values.Any(t => t.WorkspaceId == workspaceId) || now - browser.LastUsed < _options.IdleTimeout)
            {
                continue;
            }

            if (await browser.Gate.WaitAsync(0).ConfigureAwait(false))
            {
                try
                {
                    if (_browsers.TryRemove(new KeyValuePair<string, WorkspaceBrowser>(workspaceId, browser)))
                    {
                        await browser.TearDownAsync().ConfigureAwait(false);
                        browsers++;
                    }
                }
                finally
                {
                    browser.Gate.Release();
                }
            }
        }

        return (tabs, browsers);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _timer?.Dispose();
        _tabs.Clear();
        foreach (var browser in _browsers.Values.ToList())
        {
            await browser.TearDownAsync().ConfigureAwait(false);
        }

        _browsers.Clear();
    }

    private sealed class WorkspaceBrowser
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public LaunchedBrowser? Launched { get; set; }

        public CdpConnection? Connection { get; set; }

        public DateTimeOffset LastUsed { get; set; }

        public async Task TearDownAsync()
        {
            var connection = Connection;
            var launched = Launched;
            Connection = null;
            Launched = null;
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            if (launched is not null)
            {
                try
                {
                    await launched.DisposeAsync().ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // 终止已经发出、进程没在时限内报告退出(满载的 Windows 上出现过):收尾是尽力而为,不让它把服务的释放变成异常(LocalProcess.DisposeAsync 同样处理)
                }
            }
        }
    }
}
