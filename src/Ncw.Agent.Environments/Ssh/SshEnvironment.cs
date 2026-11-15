using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Ncw.Agent.Environments.Ssh;

/// <summary>
/// 设备通过 SSH 连上的远程主机(06 §3)。
/// <list type="bullet">
/// <item><b>惰性连接,每个环境一条复用连接</b>:第一次使用(或 <see cref="EnsureReadyAsync"/>)时连接;exec、端口转发、跳板机隧道都在同一条控制连接上开通道,
/// SFTP 用同一个环境的第二条连接(SSH.NET 的限制,见 <see cref="SshLink"/>)。</item>
/// <item><b>连接代</b>:每次(重)连成功 <see cref="Generation"/> +1;进程、流、监视器绑定发起时的连接代,连接中断后它们以「连接中断」结束,不会漂移到新连接上。</item>
/// <item><b>断线</b>:保活;连接中断后状态变 <see cref="EnvironmentState.Reconnecting"/>,按 <see cref="ReconnectPolicy"/> 指数退避重连。
/// 认证失败、指纹变化/被拒绝、用户取消这类重试没有意义的错误直接进入 <see cref="EnvironmentState.Failed"/>。正在执行的进程得到
/// <see cref="ProcessExitReason.ConnectionLost"/>,写类文件操作得到 <see cref="EnvironmentErrorCode.ResultUnknown"/>。</item>
/// <item><b>凭据提示是交互</b>:密码、口令、键盘交互式、主机指纹确认都经回调(<see cref="SshEnvironmentServices"/>)发出,本层不产出界面文字。</item>
/// </list>
/// </summary>
public sealed class SshEnvironment : IExecutionEnvironment
{
    private readonly SshEnvironmentProfile _profile;
    private readonly SshEnvironmentServices _services;
    private readonly SshEnvironmentOptions _options;
    private readonly SshCredentialCache _credentials = new();
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly string _localUser;
    private readonly SshFs _fs;
    private SshLink? _link;
    private EnvironmentFacts? _facts;
    private PathGuard? _paths;
    private TaskCompletionSource _changed = NewSignal();
    private Task? _reconnectLoop;
    private EnvironmentState _state = EnvironmentState.Idle;
    private EnvironmentException? _failure;
    private int _generation;
    private bool _disposed;

    public SshEnvironment(EnvironmentId id, SshEnvironmentProfile profile, SshEnvironmentServices services, SshEnvironmentOptions? options = null)
    {
        Id = id;
        _profile = profile;
        _services = services;
        _options = options ?? new SshEnvironmentOptions();
        _localUser = _options.LocalUser ?? Environment.UserName;
        _fs = new SshFs(this);
    }

    public EnvironmentId Id { get; }

    public EnvironmentKind Kind => EnvironmentKind.Ssh;

    public string DisplayName => _profile.Name;

    public int Generation => Volatile.Read(ref _generation);

    public EnvironmentState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged;

    public EnvironmentFacts Facts => _facts ?? throw new EnvironmentException(EnvironmentErrorCode.Disconnected, "not connected yet");

    public PathGuard Paths => _paths ?? throw new EnvironmentException(EnvironmentErrorCode.Disconnected, "not connected yet");

    public IEnvironmentFs Fs => _fs;

    /// <summary>最近一次失败的错误(<see cref="EnvironmentState.Failed"/> 时有值)。</summary>
    public EnvironmentException? LastFailure => _failure;

    /// <summary>
    /// 确保已连接。<see cref="EnvironmentState.Idle"/> 或 <see cref="EnvironmentState.Failed"/> 时发起(重新)连接,这是用户主动的「重新连接」;
    /// 正在连接或重连时等待结果。连接失败抛 <see cref="EnvironmentException"/>。
    /// </summary>
    public async ValueTask EnsureReadyAsync(CancellationToken ct = default)
    {
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            Task wait;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_state == EnvironmentState.Ready && _link is { IsAlive: true })
                {
                    return;
                }

                // 有连接在进行(首次连接或重连):等它出结果;否则(Idle / Failed)由这次调用发起连接
                wait = _state is EnvironmentState.Connecting or EnvironmentState.Reconnecting ? _changed.Task : Task.CompletedTask;
            }

            if (!wait.IsCompleted)
            {
                await wait.WaitAsync(ct).ConfigureAwait(false);
                continue;
            }

            await ConnectAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var probed = await ToolProbe.ProbeAsync(this, Facts, ct).ConfigureAwait(false);
        _facts = probed;
        return probed;
    }

    public async Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default)
    {
        spec.Validate();
        var link = await AcquireLinkAsync(ct).ConfigureAwait(false);
        try
        {
            var launch = RemoteCommands.Build(Facts, spec);
            return SshProcess.Start(Id, link, launch, spec.Stdin, Facts.Os, _options.KillGrace);
        }
        catch (Exception e) when (e is not (OperationCanceledException or EnvironmentException))
        {
            if (SshErrors.IsConnectionLoss(e))
            {
                link.MarkBroken();
            }

            throw SshErrors.Map(e, null, mutating: false);
        }
    }

    public async Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default)
    {
        var link = await AcquireLinkAsync(ct).ConfigureAwait(false);
        if (Facts.Os == EnvironmentOs.Windows)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "terminal on Windows hosts");
        }

        try
        {
            return await Task.Run(() => SshTerminal.Open(link, Facts, spec), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not (OperationCanceledException or EnvironmentException))
        {
            throw SshErrors.Map(e, null, mutating: false);
        }
    }

    public async Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default)
    {
        var forward = await OpenForwardCoreAsync(host, port, ct).ConfigureAwait(false);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync("127.0.0.1", forward.LocalPort, ct).ConfigureAwait(false);
            return new ForwardedStream(client, forward);
        }
        catch (Exception e)
        {
            client.Dispose();
            await forward.DisposeAsync().ConfigureAwait(false);
            if (e is OperationCanceledException)
            {
                throw;
            }

            throw new EnvironmentException(EnvironmentErrorCode.ConnectionFailed, $"{host}:{port}", e);
        }
    }

    public async Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) =>
        await OpenForwardCoreAsync(host, port, ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        SshLink? link;
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            link = _link;
            _link = null;
            loop = _reconnectLoop;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (link is not null)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }

        _credentials.Clear();
        SetState(EnvironmentState.Closed, null);
        _lifetime.Dispose();
    }

    // ---- 连接管理 ----

    /// <summary>取当前连接代的连接。Idle 时惰性连接;连接/重连中最多等 <see cref="SshEnvironmentOptions.ReadyWait"/>;Failed 时报最近一次的错误。</summary>
    internal async ValueTask<SshLink> AcquireLinkAsync(CancellationToken ct)
    {
        using var waitTimeout = new CancellationTokenSource(_options.ReadyWait);
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            Task changed;
            EnvironmentState state;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_state == EnvironmentState.Ready && _link is { IsAlive: true } link)
                {
                    return link;
                }

                state = _state;
                changed = _changed.Task;
            }

            switch (state)
            {
                case EnvironmentState.Idle:
                    await ConnectAsync(ct).ConfigureAwait(false);
                    break;
                case EnvironmentState.Failed:
                    throw _failure ?? new EnvironmentException(EnvironmentErrorCode.Disconnected);
                case EnvironmentState.Ready:
                    // 连接已死但还没被发现:标记损坏,走重连流程
                    _link?.MarkBroken();
                    await Task.Yield();
                    break;
                default:
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, waitTimeout.Token))
                    {
                        try
                        {
                            await changed.WaitAsync(linked.Token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (waitTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
                        {
                            throw new EnvironmentException(EnvironmentErrorCode.Disconnected, "reconnecting");
                        }
                    }

                    break;
            }
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_state == EnvironmentState.Ready && _link is { IsAlive: true })
                {
                    return;
                }
            }

            SetState(EnvironmentState.Connecting, null);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            try
            {
                await ConnectCoreAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (e is OperationCanceledException)
                {
                    // 调用方取消了这次连接:不是环境的故障,回到未连接,下次使用时再连
                    SetState(EnvironmentState.Idle, null);
                    throw;
                }

                var error = SshErrors.MapConnect(e);
                _failure = error;
                SetState(EnvironmentState.Failed, error);
                throw error;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>建立连接、探测环境事实、安装为当前连接代。失败时释放已建立的资源。</summary>
    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        var route = SshRouteResolver.Resolve(_profile.Target, LoadConfig(), _localUser);
        // 连接串行进行(_connectLock);只有安装成功才推进连接代,失败的尝试不消耗它
        var generation = Generation + 1;
        var context = new SshConnectContext { Profile = _profile, Services = _services, Options = _options, Credentials = _credentials, LocalUser = _localUser };
        var link = await SshConnector.ConnectAsync(route, context, generation, ct).ConfigureAwait(false);
        try
        {
            var facts = await DetectFactsAsync(link, ct).ConfigureAwait(false);
            if (_profile.Platform != RemotePlatform.Auto && !PlatformMatches(_profile.Platform, facts.Os))
            {
                throw new EnvironmentException(EnvironmentErrorCode.UnsupportedPlatform, facts.Os.ToString());
            }

            lock (_gate)
            {
                ThrowIfDisposed();
                _link = link;
                Volatile.Write(ref _generation, generation);
                // 保留已探测到的工具信息:重连不应该丢掉它
                _facts = _facts is { Tools.Count: > 0 } previous && previous.Os == facts.Os ? facts with { Tools = previous.Tools, GitVersion = previous.GitVersion } : facts;
                _paths = new PathGuard(_fs, facts.PathStyle);
                _failure = null;
            }

            link.Broken += OnLinkBroken;
            SetState(EnvironmentState.Ready, null);
            if (!link.IsAlive)
            {
                // 安装之前就已经断了
                OnLinkBroken(link);
            }
        }
        catch
        {
            await link.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private SshConfig LoadConfig()
    {
        if (_profile.Target is SshTarget.ConfigAlias { ConfigFile: { Length: > 0 } file })
        {
            return SshConfig.Load(_services.ConfigFiles, file);
        }

        return SshConfig.Load(_services.ConfigFiles);
    }

    private void OnLinkBroken(SshLink link)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_link, link))
            {
                return;
            }

            _link = null;
        }

        _ = Task.Run(() => link.DisposeAsync().AsTask());
        if (_options.Reconnect.MaxAttempts == 0)
        {
            var error = new EnvironmentException(EnvironmentErrorCode.Disconnected);
            _failure = error;
            SetState(EnvironmentState.Failed, error);
            return;
        }

        SetState(EnvironmentState.Reconnecting, null);
        lock (_gate)
        {
            _reconnectLoop = Task.Run(() => ReconnectLoopAsync(_lifetime.Token));
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        var attempts = 0;
        EnvironmentErrorCode? last = null;
        var random = new Random();
        while (!ct.IsCancellationRequested)
        {
            if (!_options.Reconnect.ShouldRetry(attempts, last))
            {
                break;
            }

            attempts++;
            try
            {
                var delay = _options.Reconnect.DelayBefore(attempts, random.NextDouble());
                await (_options.Delay is { } custom ? custom(delay, ct) : Task.Delay(delay, ct)).ConfigureAwait(false);
                await _connectLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    SetState(EnvironmentState.Reconnecting, null);
                    await ConnectCoreAsync(ct).ConfigureAwait(false);
                    return;
                }
                finally
                {
                    _connectLock.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                var error = SshErrors.MapConnect(e);
                last = error.Code;
                _failure = error;
            }
        }

        if (!ct.IsCancellationRequested)
        {
            var failure = _failure ?? new EnvironmentException(EnvironmentErrorCode.Disconnected);
            SetState(EnvironmentState.Failed, failure);
        }
    }

    private async Task<EnvironmentFacts> DetectFactsAsync(SshLink link, CancellationToken ct)
    {
        EnvironmentFacts? facts = null;
        if (_profile.Platform != RemotePlatform.Windows)
        {
            var posix = await ExecAsync(link, RemoteCommands.PosixProbeCommand, ct).ConfigureAwait(false);
            if (posix.Status == 0)
            {
                facts = RemoteCommands.ParsePosixProbe(posix.Output);
            }
        }

        if (facts is null && _profile.Platform is RemotePlatform.Auto or RemotePlatform.Windows)
        {
            var windows = await ExecAsync(link, RemoteCommands.WindowsProbeCommand, ct).ConfigureAwait(false);
            if (windows.Status == 0)
            {
                facts = RemoteCommands.ParseWindowsProbe(windows.Output);
            }
        }

        if (facts is null)
        {
            throw new EnvironmentException(EnvironmentErrorCode.UnsupportedPlatform);
        }

        // home 取 realpath:后面的路径守卫按它判断「在 home 之内」
        try
        {
            var style = facts.PathStyle;
            var home = await link.Sftp.GetAsync(PathRules.ToSftpPath(style, facts.Home), ct).ConfigureAwait(false);
            facts = facts with { Home = PathRules.FromSftpPath(style, home.FullName) };
        }
        catch (Exception e) when (e is Renci.SshNet.Common.SshException or EnvironmentException)
        {
            if (SshErrors.IsConnectionLoss(e))
            {
                throw;
            }

            throw new EnvironmentException(EnvironmentErrorCode.SftpUnavailable, null, e);
        }

        return facts;
    }

    private static async Task<(int Status, string Output)> ExecAsync(SshLink link, string commandText, CancellationToken ct)
    {
        var command = link.Control.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromSeconds(15);
        try
        {
            await command.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SshOperationTimeoutException or TimeoutException)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Timeout, "environment probe", e);
        }

        return (command.ExitStatus ?? -1, command.Result);
    }

    private static bool PlatformMatches(RemotePlatform platform, EnvironmentOs os) => platform switch
    {
        RemotePlatform.Linux => os == EnvironmentOs.Linux,
        RemotePlatform.MacOS => os == EnvironmentOs.MacOS,
        RemotePlatform.Windows => os == EnvironmentOs.Windows,
        _ => true,
    };

    private async Task<SshPortForward> OpenForwardCoreAsync(string host, int port, CancellationToken ct)
    {
        var link = await AcquireLinkAsync(ct).ConfigureAwait(false);
        var forward = new ForwardedPortLocal("127.0.0.1", 0u, host, (uint)port);
        try
        {
            link.Control.AddForwardedPort(forward);
            forward.Start();
            return new SshPortForward(link, forward);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            forward.Dispose();
            if (SshErrors.IsConnectionLoss(e))
            {
                link.MarkBroken();
            }

            throw SshErrors.Map(e, $"{host}:{port}", mutating: false);
        }
    }

    private void SetState(EnvironmentState state, EnvironmentException? error)
    {
        TaskCompletionSource previous;
        int generation;
        lock (_gate)
        {
            if (_state == state && error is null)
            {
                return;
            }

            if (_state == EnvironmentState.Closed)
            {
                return;
            }

            _state = state;
            generation = Generation;
            previous = _changed;
            _changed = NewSignal();
        }

        StatusChanged?.Invoke(this, new EnvironmentStatusChangedEventArgs(Id, state, generation, error?.Code, error?.Detail));
        previous.TrySetResult();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Disconnected, "environment closed");
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>本地回环端口到环境内 host:port 的映射(direct-tcpip)。</summary>
    private sealed class SshPortForward(SshLink link, ForwardedPortLocal forward) : IPortForward
    {
        public string LocalHost => "127.0.0.1";

        public int LocalPort => (int)forward.BoundPort;

        public ValueTask DisposeAsync()
        {
            try
            {
                if (forward.IsStarted)
                {
                    forward.Stop();
                }

                link.Control.RemoveForwardedPort(forward);
                forward.Dispose();
            }
            catch (Exception e) when (e is ObjectDisposedException or SshException or InvalidOperationException)
            {
                // 连接已经没了
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary><see cref="OpenTcpAsync"/> 返回的流:释放时一并关闭 TCP 连接和映射。</summary>
    private sealed class ForwardedStream(TcpClient client, SshPortForward forward) : ForwardingStream(client.GetStream())
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                client.Dispose();
                forward.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            client.Dispose();
            await forward.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>基于 PTY 的 SSH 终端(<c>ShellStream</c>)。只在 POSIX 主机上提供;Windows 主机的 ConPTY 经 OpenSSH 的行为不一,M3 不做。</summary>
internal sealed class SshTerminal : ITerminal
{
    private readonly ShellStream _shell;
    private readonly TaskCompletionSource<ProcessExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SshTerminal(ShellStream shell)
    {
        _shell = shell;
        shell.Closed += (_, _) => _exited.TrySetResult(new ProcessExit(null, null, ProcessExitReason.Exited));
        shell.ErrorOccurred += (_, _) => _exited.TrySetResult(new ProcessExit(null, null, ProcessExitReason.ConnectionLost));
    }

    public Stream Stream => _shell;

    public Task<ProcessExit> Exited => _exited.Task;

    public static SshTerminal Open(SshLink link, EnvironmentFacts facts, TerminalSpec spec)
    {
        var shell = link.Control.CreateShellStream("xterm-256color", (uint)spec.Columns, (uint)spec.Rows, 0, 0, 4096);
        var terminal = new SshTerminal(shell);
        // 登录 shell 起来之后进入工作目录并注入环境变量(命令里的值全部单引号转义)
        var setup = new System.Text.StringBuilder();
        if (spec.Environment is { } environment)
        {
            foreach (var (name, value) in environment)
            {
                if (!CommandSpec.IsValidEnvName(name))
                {
                    throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "environment variable name");
                }

                setup.Append("export ").Append(name).Append('=').Append(ShellCommands.PosixQuote(value)).Append("; ");
            }
        }

        if (spec.WorkingDirectory is { } cwd)
        {
            setup.Append("cd -- ").Append(ShellCommands.PosixQuote(cwd.Value)).Append("; ");
        }

        if (setup.Length > 0)
        {
            shell.WriteLine(setup.ToString().TrimEnd());
        }

        _ = facts;
        return terminal;
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken ct = default)
    {
        _shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        return ValueTask.CompletedTask;
    }

    public ValueTask KillAsync(CancellationToken ct = default)
    {
        // 关掉通道:sshd 给会话首进程(登录 shell)发 SIGHUP,整个会话随之结束。ShellStream 释放时不一定触发 Closed,
        // 所以这里自己把「已终止」报出来(退出码未知:远端不会把它回传给已关闭的通道)
        _exited.TrySetResult(new ProcessExit(null, null, ProcessExitReason.Killed));
        _shell.Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _exited.TrySetResult(new ProcessExit(null, null, ProcessExitReason.Killed));
        _shell.Dispose();
        return ValueTask.CompletedTask;
    }
}
