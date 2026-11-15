using System.Net;
using System.Net.WebSockets;
using System.Text;
using Ncw.Protocol.Security;

namespace Ncw.Protocol.Transport;

/// <summary>中继地址的规范化与校验(设备的「中继地址」设置和控制端记录的 <c>relay</c> 共用)。</summary>
public static class RelayAddress
{
    /// <summary>
    /// 接受 <c>wss://host[:port][/prefix]</c>;<c>ws://</c> 只允许本机 / 局域网地址(开发和测试用):令牌在明文连接上会被旁路看到,
    /// 虽然端到端 TLS 保护内容,令牌泄露仍会让别人占用设备的会话名额。没有查询串、片段、用户名;结尾的 <c>/</c> 去掉。
    /// </summary>
    public static bool TryNormalize(string? text, out Uri url)
    {
        url = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512 || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("ws" or "wss") || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            return false;
        }

        if (uri.Scheme == "ws" && !IsLocalHost(uri.Host))
        {
            return false;
        }

        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') };
        url = builder.Uri;
        return true;
    }

    private static bool IsLocalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && LanTransport.IsLanAddress(ip);
    }

    /// <summary>规范化后的地址的文本形式(不带结尾的 <c>/</c>),存进设置 / 记录里用。</summary>
    public static string ToText(Uri url) => url.GetLeftPart(UriPartial.Path).TrimEnd('/');

    internal static Uri Combine(Uri relay, string path, string deviceId, string? session = null)
    {
        var builder = new UriBuilder(relay)
        {
            Path = relay.AbsolutePath.TrimEnd('/') + path,
            Query = "d=" + Uri.EscapeDataString(deviceId) + (session is null ? "" : "&s=" + Uri.EscapeDataString(session)),
        };
        return builder.Uri;
    }
}

/// <summary>控制端记录的设备的中继路由(09 §4 的 <c>relay: route</c>):中继地址 + 配对时拿到的路由令牌。</summary>
public sealed record RelayRoute(Uri Url, string Token);

/// <summary>控制端的中继承载:经中继接入一台设备,得到一条就绪的字节流(上面接着跑和局域网完全相同的 TLS)。</summary>
public static class RelayTransport
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 连接中继并出示令牌;中继确认设备在线、令牌有效、设备接上数据连接后返回。
    /// 失败抛 <see cref="RelayException"/>:<c>device_offline</c>、<c>unauthorized</c>、<c>busy</c>、<c>rate_limited</c> 等是中继明确的回答,
    /// <c>unavailable</c> 是中继连不上 / 连接中途断了。
    /// </summary>
    public static async Task<Stream> ConnectAsync(RelayRoute route, string deviceId, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20); // 不设的话默认无限:中继那一跳静默消失时 WebSocket 自己永远不会判死
        socket.Options.CollectHttpResponseDetails = true; // 升级被拒绝时(429 / 421 / 400)要读得到状态码
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    await socket.ConnectAsync(RelayAddress.Combine(route.Url, RelayProtocol.ConnectPath, deviceId), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is WebSocketException or HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    throw Rejected(socket, e);
                }
            }

            var connect = RelayProtocol.Serialize(new RelayMessage("connect") { V = RelayProtocol.Version, Token = route.Token });
            await socket.SendAsync(Encoding.UTF8.GetBytes(connect), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);

            // 设备接上数据连接之前,中继最多等它 10 秒;这里留出余量。
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            var message = await ReceiveControlAsync(socket, wait.Token).ConfigureAwait(false);
            switch (message?.Type)
            {
                case "ready":
                    return new WebSocketStream(socket);
                case "error":
                    throw new RelayException(message.Code ?? RelayErrors.Unavailable, message.Message ?? "The relay refused the connection");
                default:
                    throw new RelayException(RelayErrors.Unavailable, "The relay closed the connection");
            }
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static RelayException Rejected(ClientWebSocket socket, Exception inner)
    {
        // 升级之前被拒绝(限流 429、负载均衡 421、中继没起来……):HTTP 状态映射成同样的错误码。
        var code = socket.HttpStatusCode switch
        {
            HttpStatusCode.TooManyRequests => RelayErrors.RateLimited,
            HttpStatusCode.MisdirectedRequest => RelayErrors.Misdirected,
            HttpStatusCode.BadRequest => RelayErrors.BadRequest,
            _ => RelayErrors.Unavailable,
        };
        return new RelayException(code, "Could not reach the relay", inner);
    }

    /// <summary>读下一条文本控制消息;连接关闭、收到二进制或不是合法 JSON 返回 null。</summary>
    internal static async Task<RelayMessage?> ReceiveControlAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[RelayProtocol.MaxControlMessageBytes];
        var length = 0;
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                length += result.Count;
                if (result.EndOfMessage)
                {
                    return result.MessageType == WebSocketMessageType.Text ? RelayProtocol.Deserialize(buffer.AsSpan(0, length)) : null;
                }

                if (length >= buffer.Length)
                {
                    return null;
                }
            }
        }
        catch (WebSocketException)
        {
            return null;
        }
    }
}

/// <summary>设备注册连接的状态(设置页显示用)。</summary>
public enum RelayConnectionState
{
    /// <summary>没有配置中继,或远程开关关着。</summary>
    Off,

    /// <summary>正在连接 / 重连。</summary>
    Connecting,

    /// <summary>已注册,等待会话。</summary>
    Registered,

    /// <summary>最近一次尝试失败(<see cref="RelayStatus.Error"/> 是原因),退避后重试。</summary>
    Failed,
}

public sealed record RelayStatus(RelayConnectionState State, string? Error = null);

/// <summary>一个经中继接入的会话:拼接好的字节流(接着做 TLS 握手)和控制端出示的令牌哈希。</summary>
public sealed record RelayAcceptedSession(Stream Stream, string TokenHash);

/// <summary>设备端重试的节奏(测试里缩短)。</summary>
public sealed record RelayClientOptions(TimeSpan InitialBackoff, TimeSpan MaxBackoff)
{
    public static readonly RelayClientOptions Default = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
}

/// <summary>
/// 设备端的中继承载(09 §9):连上中继,用设备私钥对挑战签名注册,把路由令牌哈希推给中继,
/// 中继通知有新会话时为它开一条数据连接,把拼接好的字节流交给 <paramref name="onSession"/>(接着和局域网完全一样:
/// <see cref="SecureChannel.AcceptAsync"/> + <c>ServerSession</c>)。连接断了(中继重启、网络切换)自动退避重连并重新注册。
/// </summary>
public sealed class RelayRegistration : IAsyncDisposable
{
    private readonly Uri _relay;
    private readonly PeerIdentity _identity;
    private readonly Func<RelayAcceptedSession, Task> _onSession;
    private readonly RelayClientOptions _options;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _gate = new();
    private string[] _tokens;
    private ClientWebSocket? _control;
    private RelayStatus _status = new(RelayConnectionState.Off);
    private Task _loop = Task.CompletedTask;

    public RelayRegistration(Uri relay, PeerIdentity identity, IReadOnlyCollection<string> tokenHashes, Func<RelayAcceptedSession, Task> onSession, RelayClientOptions? options = null)
    {
        _relay = relay;
        _identity = identity;
        _onSession = onSession;
        _options = options ?? RelayClientOptions.Default;
        _tokens = [.. tokenHashes];
    }

    public RelayStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>状态变化(在后台线程上触发)。</summary>
    public event Action<RelayStatus>? StatusChanged;

    public void Start() => _loop = Task.Run(RunAsync);

    /// <summary>
    /// 整体替换当前有效的路由令牌哈希并立即推给中继(配对、吊销时调用)。没连上时只记下,注册时一并发送。
    /// 被移除的哈希对应的会话由中继立即掐断。
    /// </summary>
    public void SetTokens(IReadOnlyCollection<string> tokenHashes)
    {
        lock (_gate)
        {
            _tokens = [.. tokenHashes];
        }

        _ = PushTokensAsync();
    }

    private async Task PushTokensAsync()
    {
        try
        {
            await _sendGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                ClientWebSocket? control;
                string[] tokens;
                lock (_gate)
                {
                    control = _control;
                    tokens = _tokens; // 在锁里取最新的:并发的两次推送,后到的一定发出最新值
                }

                if (control is { State: WebSocketState.Open })
                {
                    await SendAsync(control, new RelayMessage("tokens") { Set = tokens }, _lifetime.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _sendGate.Release();
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // 连接断了:重连时会带着最新的令牌重新注册。
        }
    }

    private async Task RunAsync()
    {
        var delay = _options.InitialBackoff;
        while (!_lifetime.IsCancellationRequested)
        {
            var registered = false;
            try
            {
                Set(new RelayStatus(RelayConnectionState.Connecting));
                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20); // 不设的话默认无限:中继那一跳静默消失时 WebSocket 自己永远不会判死
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.ConnectAsync(RelayAddress.Combine(_relay, RelayProtocol.DevicePath, _identity.DeviceId), timeout.Token).ConfigureAwait(false);
                }

                await RegisterAsync(socket).ConfigureAwait(false);
                registered = true;
                delay = _options.InitialBackoff;
                Set(new RelayStatus(RelayConnectionState.Registered));
                await ServeAsync(socket).ConfigureAwait(false);
                Set(new RelayStatus(RelayConnectionState.Failed, RelayErrors.Unavailable));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (RelayException e)
            {
                Set(new RelayStatus(RelayConnectionState.Failed, e.Code));
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
            {
                Set(new RelayStatus(RelayConnectionState.Failed, RelayErrors.Unavailable));
            }
            finally
            {
                lock (_gate)
                {
                    _control = null;
                }
            }

            try
            {
                // 断开之后稍等再连(带抖动,避免中继重启时所有设备同时涌回);刚注册成功后断开的从最短退避重新开始。
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, (int)Math.Max(1, delay.TotalMilliseconds / 4)));
                await Task.Delay(delay + jitter, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!registered)
            {
                delay = TimeSpan.FromTicks(Math.Min(_options.MaxBackoff.Ticks, delay.Ticks * 2));
            }
        }

        Set(new RelayStatus(RelayConnectionState.Off));
    }

    private async Task RegisterAsync(ClientWebSocket socket)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var challenge = await RelayTransport.ReceiveControlAsync(socket, timeout.Token).ConfigureAwait(false);
        if (challenge is { Type: "error" })
        {
            throw new RelayException(challenge.Code ?? RelayErrors.Unavailable, challenge.Message ?? "The relay refused the registration");
        }

        if (challenge is not { Type: "challenge", Nonce: { } nonceText } || !TryDecode(nonceText, out var nonce))
        {
            throw new RelayException(RelayErrors.BadRequest, "The relay did not send a challenge");
        }

        string[] tokens;
        lock (_gate)
        {
            tokens = _tokens;
        }

        var register = new RelayMessage("register")
        {
            V = RelayProtocol.Version,
            PublicKey = Convert.ToBase64String(RelayProtocol.PublicKeyInfo(_identity)),
            Signature = Convert.ToBase64String(RelayProtocol.Sign(_identity, nonce)),
            Tokens = tokens,
        };
        await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            await SendAsync(socket, register, timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _control = socket; // register 发出之后才对令牌推送可见:推送绝不会抢在 register 前面
            }
        }
        finally
        {
            _sendGate.Release();
        }

        // 中继在发 registered 之前,设备就已经对控制端可见了:这之间到的会话通知先记下,注册完成后一并处理。
        RelayMessage? reply;
        List<RelayMessage> early = [];
        while ((reply = await RelayTransport.ReceiveControlAsync(socket, timeout.Token).ConfigureAwait(false)) is { Type: "session" })
        {
            early.Add(reply);
        }

        if (reply is { Type: "error" })
        {
            throw new RelayException(reply.Code ?? RelayErrors.Unavailable, reply.Message ?? "The relay refused the registration");
        }

        if (reply is not { Type: "registered" })
        {
            throw new RelayException(RelayErrors.Unavailable, "The relay closed the connection during registration");
        }

        foreach (var message in early)
        {
            Accept(message);
        }

        // 注册期间令牌可能又变了(推送那时还没有 _control 可发):补推一次最新的。
        _ = PushTokensAsync();
    }

    private async Task ServeAsync(ClientWebSocket socket)
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var message = await RelayTransport.ReceiveControlAsync(socket, _lifetime.Token).ConfigureAwait(false);
            switch (message)
            {
                case { Type: "session" }:
                    Accept(message);
                    break;
                case { Type: "error" }:
                    throw new RelayException(message.Code ?? RelayErrors.Unavailable, message.Message ?? "The relay closed the registration");
                case null:
                    return; // 对端关闭、断开或发了不合法的东西:重新注册
            }
        }
    }

    private void Accept(RelayMessage message)
    {
        if (message is { Session: { Length: > 0 } session, TokenHash: { } tokenHash })
        {
            _ = Task.Run(() => AcceptAsync(session, tokenHash), CancellationToken.None);
        }
    }

    private async Task AcceptAsync(string session, string tokenHash)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20); // 不设的话默认无限:中继那一跳静默消失时 WebSocket 自己永远不会判死
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(RelayAddress.Combine(_relay, RelayProtocol.DataPath, _identity.DeviceId, session), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or OperationCanceledException or IOException)
        {
            socket.Dispose();
            return; // 控制端那边会因为「设备没接上」得到 timeout
        }

        // 数据连接的所有权在 WebSocketStream 手里:它释放时先发关闭帧、等对端的关闭帧(最多 2 秒)再中止。
        // 这里不能直接 socket.Dispose():那是立即中止,会把设备刚发出的数据(例如 hello 阶段的「拒绝」帧)连同关闭握手一起丢掉,
        // 中继看到的是「设备没走关闭握手就断了」,控制端看到的是「连接丢失」而不是明确的拒绝(docs/parity.md「并发不稳定测试的根因与修复」)。
        var stream = new WebSocketStream(socket);
        try
        {
            await _onSession(new RelayAcceptedSession(stream, tokenHash)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SecureChannelException)
        {
        }
        finally
        {
            stream.Dispose(); // 幂等:会话层已经释放过就什么也不做
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, RelayMessage message, CancellationToken ct) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(RelayProtocol.Serialize(message)), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);

    private static bool TryDecode(string base64, out byte[] bytes)
    {
        bytes = new byte[RelayProtocol.NonceBytes];
        return Convert.TryFromBase64String(base64, bytes, out var written) && written == RelayProtocol.NonceBytes;
    }

    private void Set(RelayStatus status)
    {
        lock (_gate)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        ClientWebSocket? control;
        lock (_gate)
        {
            control = _control;
        }

        try
        {
            control?.Abort();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }
}
