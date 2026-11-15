using Renci.SshNet;
using Renci.SshNet.Common;

namespace Ncw.Agent.Environments.Ssh;

/// <summary>
/// 一个连接代的全部网络资源:控制连接(exec、端口转发、作为后续跳的隧道)、SFTP 连接,以及串起来的各跳。
/// SSH.NET 没有公开在同一条 SSH 会话上再开 SFTP 通道的办法,所以每个环境是「一条控制连接 + 一条 SFTP 连接」,
/// 两者都在环境存活期间复用(不是每次文件操作新建),经过的跳板机连接也是共享的。
/// </summary>
internal sealed class SshLink : IAsyncDisposable
{
    private readonly List<IDisposable> _owned;
    private readonly CancellationTokenSource _lost = new();
    private int _disposed;

    public SshLink(int generation, SshClient control, SftpClient sftp, List<IDisposable> owned)
    {
        Generation = generation;
        Control = control;
        Sftp = sftp;
        _owned = owned;
        control.ErrorOccurred += OnError;
        sftp.ErrorOccurred += OnError;
    }

    public int Generation { get; }

    public SshClient Control { get; }

    public SftpClient Sftp { get; }

    /// <summary>连接中断或被关闭时取消。进程、监视器等绑定了这一代的句柄都监听它。</summary>
    public CancellationToken Lost => _lost.Token;

    public bool IsAlive => Volatile.Read(ref _disposed) == 0 && !_lost.IsCancellationRequested && Control.IsConnected && Sftp.IsConnected;

    public event Action<SshLink>? Broken;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        _lost.Cancel();
        Control.ErrorOccurred -= OnError;
        Sftp.ErrorOccurred -= OnError;
        // 先关最里面的(目标),再关跳板机,最后关转发端口
        TryDispose(Sftp);
        TryDispose(Control);
        for (var i = _owned.Count - 1; i >= 0; i--)
        {
            TryDispose(_owned[i]);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>从其它地方(例如操作失败)发现连接已经坏了。</summary>
    public void MarkBroken()
    {
        if (!_lost.IsCancellationRequested)
        {
            _lost.Cancel();
            Broken?.Invoke(this);
        }
    }

    // 不能在 SSH.NET 自己的事件线程上做任何收尾(尤其是 Dispose 自己:会等这个线程结束而死锁),一律转到线程池
    private void OnError(object? sender, ExceptionEventArgs e) => _ = Task.Run(MarkBroken);

    private static void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // 关闭路径上的任何错误都不能再往外抛
        }
    }
}

/// <summary>每个环境在内存里缓存的凭据(只在该环境存活期间,不落盘):重连和第二条连接(SFTP)不能再次打扰用户。</summary>
internal sealed class SshCredentialCache
{
    public Dictionary<string, string> Passwords { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Passphrases { get; } = new(StringComparer.Ordinal);

    public void Clear()
    {
        Passwords.Clear();
        Passphrases.Clear();
    }
}

internal sealed class SshConnectContext
{
    public required SshEnvironmentProfile Profile { get; init; }

    public required SshEnvironmentServices Services { get; init; }

    public required SshEnvironmentOptions Options { get; init; }

    public required SshCredentialCache Credentials { get; init; }

    public required string LocalUser { get; init; }
}

/// <summary>
/// 建立一条路线上的连接(跳板机逐跳串联,最后一跳是目标)。
/// <list type="bullet">
/// <item>ProxyJump:在上一跳的连接上开 direct-tcpip(<see cref="ForwardedPortLocal"/>,回环端口),下一跳连到那个端口,
/// 主机指纹仍按<b>真实主机名</b>校验;</item>
/// <item>主机指纹先于认证:未钉扎时先只取指纹并拒绝握手(此时还没发任何凭据),用户确认并钉扎后再正式连接——这样等用户回答不会撞上握手超时;
/// 指纹变化直接失败,不弹确认;</item>
/// <item>认证:私钥(显式的,否则 <c>~/.ssh/id_*</c>)、键盘交互式、密码;凭据提示走 <see cref="ISshCredentialProvider"/>。</item>
/// </list>
/// </summary>
internal static class SshConnector
{
    private const int MaxPasswordAttempts = 3;

    public static async Task<SshLink> ConnectAsync(IReadOnlyList<SshHop> route, SshConnectContext context, int generation, CancellationToken ct)
    {
        var owned = new List<IDisposable>();
        SshClient? control = null;
        try
        {
            SshClient? previous = null;
            for (var i = 0; i < route.Count - 1; i++)
            {
                var jump = await ConnectHopAsync(route[i], previous, i == 0, static info => new SshClient(info), context, owned, ct).ConfigureAwait(false);
                owned.Add(jump);
                previous = jump;
            }

            var target = route[^1];
            control = await ConnectHopAsync(target, previous, route.Count == 1, static info => new SshClient(info), context, owned, ct).ConfigureAwait(false);
            var sftp = await ConnectHopAsync(target, previous, route.Count == 1, static info => new SftpClient(info), context, owned, ct).ConfigureAwait(false);
            return new SshLink(generation, control, sftp, owned);
        }
        catch
        {
            control?.Dispose();
            for (var i = owned.Count - 1; i >= 0; i--)
            {
                try
                {
                    owned[i].Dispose();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                }
            }

            throw;
        }
    }

    private static async Task<T> ConnectHopAsync<T>(SshHop hop, SshClient? via, bool first, Func<ConnectionInfo, T> create,
        SshConnectContext context, List<IDisposable> owned, CancellationToken ct)
        where T : BaseClient
    {
        string host = hop.HostName;
        var port = hop.Port;
        if (via is not null)
        {
            var forward = new ForwardedPortLocal("127.0.0.1", 0u, hop.HostName, (uint)hop.Port);
            try
            {
                via.AddForwardedPort(forward);
                forward.Start();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                forward.Dispose();
                throw SshErrors.MapConnect(e);
            }

            owned.Add(forward);
            host = "127.0.0.1";
            port = (int)forward.BoundPort;
        }

        // 指纹先于一切凭据:这台主机还没有钉扎时,先只取指纹让用户确认——在核对过指纹之前,不问口令(解私钥的口令也不问)、不发任何认证
        if ((await context.Services.HostKeys.GetAsync(hop.HostKeyName, ct).ConfigureAwait(false)).Count == 0)
        {
            await PinHostKeyFirstAsync(hop, host, port, first && via is null, context, ct).ConfigureAwait(false);
        }

        var passwordAttempts = 0;
        bool? offersPassword = null;
        for (;;)
        {
            ct.ThrowIfCancellationRequested();
            var auth = await SshAuth.BuildAsync(hop, context, ct).ConfigureAwait(false);
            try
            {
                return await ConnectWithHostKeyAsync(hop, host, port, first && via is null, create, auth, context, ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.Authentication && auth.CancelRequested)
            {
                throw new EnvironmentException(EnvironmentErrorCode.Cancelled, e.Detail, e);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.Authentication && SshAuth.CanAskPasswordAfterFailure(context) && passwordAttempts < MaxPasswordAttempts)
            {
                // 私钥/键盘交互式都没过:只有服务器接受密码时才问(Auto 模式不会一上来就打扰用户,key-only 的服务器也不会白问)
                offersPassword ??= await ServerOffersPasswordAsync(hop, host, port, first && via is null, context, ct).ConfigureAwait(false);
                if (!offersPassword.Value)
                {
                    throw;
                }

                passwordAttempts++;
                var rejected = context.Credentials.Passwords.Remove(SshAuth.CacheKey(hop));
                var answer = await context.Services.Credentials!.AskAsync(
                    new SshCredentialRequest(SshCredentialKind.Password, hop.HostKeyName, hop.User, PreviousRejected: rejected), ct).ConfigureAwait(false);
                if (answer is null || answer.Count == 0)
                {
                    throw new EnvironmentException(EnvironmentErrorCode.Cancelled, null, e);
                }

                context.Credentials.Passwords[SshAuth.CacheKey(hop)] = answer[0];
            }
        }
    }

    /// <summary>
    /// 只为拿到主机指纹并让用户确认:用只含 <c>none</c> 的认证集合握手。未钉扎时指纹回调会先中止握手,确认并钉扎后再握手一次,
    /// <c>none</c> 被拒绝(认证失败)是预期的结局——指纹已经钉扎,调用方接着用真正的认证方式连。指纹被拒绝 / 变化 / 连不上照常往外抛。
    /// </summary>
    private static async Task PinHostKeyFirstAsync(SshHop hop, string host, int port, bool throughProxy, SshConnectContext context, CancellationToken ct)
    {
        try
        {
            var client = await ConnectWithHostKeyAsync(hop, host, port, throughProxy, static info => new SshClient(info), SshAuth.ProbeOnly(hop), context, ct).ConfigureAwait(false);
            client.Dispose();
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.Authentication)
        {
        }
    }

    /// <summary>
    /// 只发一个 <c>none</c> 认证请求,让服务器告诉我们它接受哪些方式(失败消息里带着列表)。
    /// 只在私钥被拒绝之后、准备问密码之前探测一次;探测本身出问题时按「接受」处理,宁可多问一次也不能让能用密码的服务器连不上。
    /// </summary>
    private static async Task<bool> ServerOffersPasswordAsync(SshHop hop, string host, int port, bool throughProxy, SshConnectContext context, CancellationToken ct)
    {
        try
        {
            var client = await ConnectWithHostKeyAsync(hop, host, port, throughProxy, static info => new SshClient(info), SshAuth.ProbeOnly(hop), context, ct).ConfigureAwait(false);
            client.Dispose();
            return true;
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.Authentication)
        {
            return SshAuth.MessageMentionsPassword(e.InnerException?.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return true;
        }
    }

    private static async Task<T> ConnectWithHostKeyAsync<T>(SshHop hop, string host, int port, bool throughProxy, Func<ConnectionInfo, T> create,
        SshAuth auth, SshConnectContext context, CancellationToken ct)
        where T : BaseClient
    {
        var pinned = await context.Services.HostKeys.GetAsync(hop.HostKeyName, ct).ConfigureAwait(false);
        for (var phase = 0; ; phase++)
        {
            HostKeyInfo? presented = null;
            var verdict = HostKeyVerdict.Unknown;
            var info = CreateConnectionInfo(hop, host, port, throughProxy ? context.Profile.Proxy : null, auth.Methods(), context.Profile.ConnectTimeout);
            // 服务器有多把主机密钥时,优先协商已钉扎的算法,避免换了算法被误判成「指纹变了」
            var preferred = HostKeyPinning.PreferPinned([.. info.HostKeyAlgorithms.Keys], pinned);
            for (var i = 0; i < preferred.Count; i++)
            {
                info.HostKeyAlgorithms.SetPosition(preferred[i], i);
            }

            var client = create(info);
            client.KeepAliveInterval = context.Profile.KeepAliveInterval;
            client.HostKeyReceived += (_, e) =>
            {
                presented = new HostKeyInfo(hop.HostKeyName, e.HostKeyName, HostKeyPinning.NormalizeFingerprint(e.FingerPrintSHA256));
                verdict = HostKeyPinning.Evaluate(pinned, presented);
                e.CanTrust = verdict == HostKeyVerdict.Trusted;
            };

            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                return client;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                client.Dispose();
                if (presented is not null && verdict == HostKeyVerdict.Changed)
                {
                    await context.Services.HostKeyCallback.NotifyChangedAsync(presented, pinned, ct).ConfigureAwait(false);
                    throw new EnvironmentException(EnvironmentErrorCode.HostKeyChanged, presented.Display, e);
                }

                if (presented is not null && verdict == HostKeyVerdict.Unknown && phase == 0)
                {
                    if (!await context.Services.HostKeyCallback.ConfirmNewHostAsync(presented, ct).ConfigureAwait(false))
                    {
                        throw new EnvironmentException(EnvironmentErrorCode.HostKeyRejected, presented.Display, e);
                    }

                    await context.Services.HostKeys.PinAsync(hop.HostKeyName, new PinnedHostKey(presented.Algorithm, presented.FingerprintSha256), ct).ConfigureAwait(false);
                    pinned = await context.Services.HostKeys.GetAsync(hop.HostKeyName, ct).ConfigureAwait(false);
                    continue;
                }

                throw SshErrors.MapConnect(e);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private static ConnectionInfo CreateConnectionInfo(SshHop hop, string host, int port, SshProxy? proxy, AuthenticationMethod[] methods, TimeSpan timeout)
    {
        var info = proxy is null
            ? new ConnectionInfo(host, port, hop.User, methods)
            : new ConnectionInfo(host, port, hop.User, proxy.Kind switch
            {
                SshProxyKind.Http => ProxyTypes.Http,
                SshProxyKind.Socks4 => ProxyTypes.Socks4,
                _ => ProxyTypes.Socks5,
            }, proxy.Host, proxy.Port, proxy.Username, proxy.Password, methods);
        info.Timeout = timeout;
        return info;
    }
}
