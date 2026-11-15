using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host;

/// <summary>远程开关的持久设置(kv 表 <c>v3.remote</c>)。</summary>
/// <param name="RelayUrl">中继地址(规范化的 <c>wss://…</c>);null = 关闭中继(默认)。远程开关打开且配了地址时,设备向中继注册并接收会话(09 §9)。</param>
public sealed record RemoteSettings(bool Enabled, int Port, bool LanOnly, string? RelayUrl = null)
{
    public static readonly RemoteSettings Default = new(false, LanTransport.DefaultPort, true);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoteSettings))]
internal sealed partial class RemoteJsonContext : JsonSerializerContext;

/// <summary>
/// 局域网远程连接(docs/design/09 §3、§4):远程开关、TCP 监听、TLS 1.3 双向认证、配对码 + SRP 配对、信任记录、吊销。
/// <list type="bullet">
/// <item>远程连接按对端公钥指纹查 <c>controller_trust</c>:查到给记录里的角色,查不到是 <see cref="Role.Unpaired"/>(只能配对)。</item>
/// <item>配对码 6 位,只在内存里;配对成功或连续 5 次失败后更换。失败后稍作延迟,限制在线猜测的速度。</item>
/// <item>关掉开关:停止监听并断开所有远程连接;吊销:断开这个控制端的所有连接。本机连接不受影响。</item>
/// <item>中继(09 §9):配了中继地址时,设备另外向中继注册(设备私钥签名、路由令牌哈希),经中继来的会话交给同一个 <c>SecureChannel</c> + <c>ServerSession</c>;
/// 经中继的会话只接受已配对的控制端,且中继出示的令牌必须属于 TLS 里的那个控制端;配对只能在局域网 / 扫码(直连)完成。吊销同时作废路由令牌。</item>
/// </list>
/// 依赖数据库(信任记录、设置),数据就绪后才能打开。
/// </summary>
public sealed class RemoteService : IDisposable
{
    private const string SettingsKey = "v3.remote";
    private const int MaxFailures = 5;
    private const int MaxRemoteSessions = 32;
    private const int MaxQrTokens = 8;
    private static readonly TimeSpan QrLifetime = TimeSpan.FromMinutes(10);

    private readonly DeviceIdentity _identity;
    private readonly TopicHub _topics;
    private readonly MethodRouter _router;
    private readonly string _version;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _failureDelay;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, (string Fingerprint, CancellationTokenSource Cts)> _sessions = new();

    private readonly List<(byte[] Token, long ExpiresAt)> _qrTokens = [];

    private ControllerTrustRepository? _trust;
    private KvStore? _kv;
    private RemoteSettings _settings = RemoteSettings.Default;
    private LanListener? _listener;
    private CancellationTokenSource? _listening;
    private RelayRegistration? _relay;
    private CancellationTokenSource? _relaySessions;
    private string? _code;
    private int _failures;
    private string? _error;

    private readonly RelayClientOptions? _relayOptions;

    // 云端模式(02 A6):没有局域网监听、没有配对码,信任记录由编排服务下发,中继地址来自启动配置。见 UseCloudProfile。
    private bool _cloud;

    public RemoteService(DeviceIdentity identity, MethodRouter router, TopicHub topics, string version, TimeProvider clock, TimeSpan? failureDelay = null, RelayClientOptions? relayOptions = null)
    {
        _relayOptions = relayOptions;
        _identity = identity;
        _router = router;
        _topics = topics;
        _version = version;
        _clock = clock;
        _failureDelay = failureDelay ?? TimeSpan.FromSeconds(1);

        topics.Register(DeviceMethods.RemoteTopic, State);
        router.Handle(DeviceMethods.RemoteGetStatus, (_, _, _) => ValueTask.FromResult(Status()));
        router.Handle(DeviceMethods.RemoteSetEnabled, (p, _, _) => ValueTask.FromResult(SetEnabled(p.Enabled)));
        router.Handle(DeviceMethods.RemoteRotateCode, (_, _, _) =>
        {
            RotateCode();
            return ValueTask.FromResult(Status());
        });
        router.Handle(DeviceMethods.TrustRevoke, (p, _, _) =>
        {
            Revoke(p.Fingerprint);
            return ValueTask.FromResult(Unit.Value);
        });
        router.Handle(DeviceMethods.RemoteConfigure, (p, _, _) => ValueTask.FromResult(Configure(p.Port, p.LanOnly, p.RelayUrl)));
        router.Handle(DeviceMethods.TrustSetRole, (p, _, _) =>
        {
            SetRole(p.Fingerprint, p.Role);
            return ValueTask.FromResult(Unit.Value);
        });
        router.Handle(DeviceMethods.PairBegin, (p, ctx, _) => ValueTask.FromResult(PairBegin(p, ctx)));
        router.Handle(DeviceMethods.PairFinish, PairFinishAsync);
        router.Handle(DeviceMethods.PairClaim, PairClaimAsync);
        router.Handle(DeviceMethods.PairCreateQr, (_, _, _) => ValueTask.FromResult(CreateQr()));
    }

    /// <summary>已连接的远程会话数(云端空闲检测用)。</summary>
    public int ConnectedSessions => _sessions.Count;

    /// <summary>有控制端连上 / 断开(云端空闲检测据此刷新「最后活动时间」)。在会话线程上触发。</summary>
    public event Action? SessionsChanged;

    /// <summary>
    /// 切换到云端档(<c>--mode cloud</c>):远程只经中继,中继地址固定(来自启动配置,不可改);不监听局域网;没有配对码、二维码配对、远程开关和配置——
    /// 这些方法返回 <c>unsupported</c>(<c>cloud.pairing.unsupported</c> / <c>cloud.managed</c>)。信任记录只能由编排服务的指令改(<see cref="PutTrust"/> 等)。
    /// 不会自动开始注册:云端运行时在处理完编排服务积压的指令(例如休眠期间到达的吊销)之后才调 <see cref="StartCloudRelay"/>,避免唤醒的空档里已被吊销的控制端能连上。
    /// </summary>
    internal void UseCloudProfile(Uri relayUrl)
    {
        _cloud = true;
        _settings = new RemoteSettings(true, LanTransport.DefaultPort, true, RelayAddress.ToText(relayUrl));
    }

    /// <summary>云端:开始向中继注册(数据必须已就绪)。</summary>
    internal void StartCloudRelay() => StartRelay();

    private void RequireLocalMode(string key)
    {
        if (_cloud)
        {
            throw Services.ServiceMapping.Error(ErrorCodes.Unsupported, key, "This operation is not available on a cloud agent", false);
        }
    }

    /// <summary>当前配对码(测试和界面用;远程开关关闭时为 null)。</summary>
    public string? PairingCode
    {
        get
        {
            lock (_gate)
            {
                return _code;
            }
        }
    }

    /// <summary>实际监听的端口(没打开为 null)。</summary>
    public int? Port
    {
        get
        {
            lock (_gate)
            {
                return _listener?.Port;
            }
        }
    }

    /// <summary>配置的中继地址(规范化文本);没配置为 null。<c>device.info</c> 带给已配对的控制端。</summary>
    public string? RelayUrl => _settings.RelayUrl;

    /// <summary>到中继的注册状态(测试和界面用);没有启用中继为 null。</summary>
    public RelayStatus? RelayStatus
    {
        get
        {
            lock (_gate)
            {
                return _relay?.Status;
            }
        }
    }

    /// <summary>数据就绪:读设置,之前打开过就重新开始监听。</summary>
    public void OnDataReady(AgentDatabase database)
    {
        _trust = new ControllerTrustRepository(database);
        _kv = new KvStore(database);
        if (_cloud)
        {
            Publish();
            return;
        }

        if (_kv.Get(SettingsKey) is { } json)
        {
            try
            {
                _settings = JsonSerializer.Deserialize(json, RemoteJsonContext.Default.RemoteSettings) ?? RemoteSettings.Default;
            }
            catch (JsonException)
            {
                _settings = RemoteSettings.Default;
            }
        }

        if (_settings.Enabled)
        {
            StartListening();
            StartRelay();
        }

        Publish();
    }

    public RemoteStatus SetEnabled(bool enabled)
    {
        RequireLocalMode(CloudErrorKeys.ManagedByOrchestrator);
        if (_kv is null)
        {
            throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);
        }

        _settings = _settings with { Enabled = enabled };
        _kv.Set(SettingsKey, JsonSerializer.Serialize(_settings, RemoteJsonContext.Default.RemoteSettings));
        if (enabled)
        {
            StartListening();
            StartRelay();
        }
        else
        {
            StopListening();
            StopRelay();
        }

        Publish();
        return Status();
    }

    /// <summary>
    /// 改端口、「仅允许局域网地址」或中继地址。正在监听时用新设置重新监听(远程连接断开后会自动重连);
    /// 中继地址 null = 不改,空串 = 关闭中继;改了就断开旧中继上的会话并向新的中继注册。
    /// </summary>
    public RemoteStatus Configure(int? port, bool? lanOnly, string? relayUrl = null)
    {
        RequireLocalMode(CloudErrorKeys.ManagedByOrchestrator);
        if (_kv is null)
        {
            throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);
        }

        if (port is { } p && p is < 1024 or > 65535)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "Port must be between 1024 and 65535");
        }

        string? relay = _settings.RelayUrl;
        if (relayUrl is not null)
        {
            if (relayUrl.Length == 0)
            {
                relay = null;
            }
            else if (RelayAddress.TryNormalize(relayUrl, out var url))
            {
                relay = RelayAddress.ToText(url);
            }
            else
            {
                throw new ProtocolException(ErrorCodes.InvalidRequest, "The relay address must be wss://host[:port] (ws:// is only allowed for local and LAN addresses)");
            }
        }

        var next = _settings with { Port = port ?? _settings.Port, LanOnly = lanOnly ?? _settings.LanOnly, RelayUrl = relay };
        if (next == _settings)
        {
            return Status();
        }

        var listenerChanged = next.Port != _settings.Port || next.LanOnly != _settings.LanOnly;
        var relayChanged = next.RelayUrl != _settings.RelayUrl;
        _settings = next;
        _kv.Set(SettingsKey, JsonSerializer.Serialize(_settings, RemoteJsonContext.Default.RemoteSettings));
        if (_settings.Enabled)
        {
            if (listenerChanged)
            {
                StopListening();
                StartListening();
            }

            if (relayChanged)
            {
                StopRelay();
                StartRelay();
            }
        }

        Publish();
        return Status();
    }

    public RemoteStatus Status()
    {
        lock (_gate)
        {
            var port = _listener?.Port;
            var relay = _relay?.Status;
            return new RemoteStatus(
                _settings.Enabled, port, _settings.Port, _settings.LanOnly,
                port is null ? [] : [.. LanTransport.LocalAddresses().Select(a => a.ToString())],
                _listener is null ? null : _code, _error,
                _settings.RelayUrl,
                _settings.RelayUrl is null ? null : relay is null ? "off" : relay.State.ToString().ToLowerInvariant(),
                relay?.State == RelayConnectionState.Failed ? relay.Error : null);
        }
    }

    public RemoteState State()
    {
        var connected = _sessions.Values.Select(s => s.Fingerprint).ToHashSet(StringComparer.Ordinal);
        var controllers = _trust?.List()
            .Select(t => new TrustedControllerDto(t.Fingerprint, t.Name, t.Role, t.PairedAt, t.LastSeenAt, connected.Contains(t.Fingerprint)))
            .ToArray() ?? [];
        return new RemoteState(Status(), controllers);
    }

    /// <summary>远程连接的握手策略:按公钥指纹查信任记录。</summary>
    private Role? Authenticate(HelloFrame hello, SessionPeer? peer) => peer is null ? null : AuthenticateTrust(peer); // peer 为 null:远程监听上不会出现,防御

    private Role? AuthenticateTrust(SessionPeer peer)
    {
        if (_trust?.Get(peer.FingerprintHex) is { } trusted && Enum.TryParse<Role>(trusted.Role, ignoreCase: true, out var role) && role >= Role.Viewer)
        {
            _trust.Touch(peer.FingerprintHex, _clock.GetUtcNow().ToUnixTimeMilliseconds());
            return role;
        }

        return Role.Unpaired;
    }

    /// <summary>
    /// 经中继来的会话的握手策略:只接受已配对的控制端(中继来的人没有「先配对」的机会),
    /// 并且中继出示的令牌哈希必须是<b>这个控制端</b>的路由令牌——偷到令牌的人没有对应的 TLS 私钥,同一个令牌也不能让别的控制端借用。
    /// 中继是不可信的:它报错令牌哈希只会让握手被拒,不会授予任何东西。
    /// </summary>
    private Role? AuthenticateRelay(SessionPeer? peer, string tokenHash)
    {
        if (peer is null || _trust?.RouteTokenHash(peer.FingerprintHex) is not { } expected)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(tokenHash)))
        {
            return null;
        }

        return AuthenticateTrust(peer) is { } role && role >= Role.Viewer ? role : null;
    }

    private void StartListening()
    {
        lock (_gate)
        {
            if (_listener is not null)
            {
                return;
            }

            try
            {
                _listener = LanTransport.Listen(_settings.Port, _settings.LanOnly);
                _error = null;
            }
            catch (System.Net.Sockets.SocketException)
            {
                _error = "port-unavailable";
                return;
            }

            _code = NewCode();
            _failures = 0;
            _listening = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _listening.Token);
        }
    }

    private void StopListening()
    {
        lock (_gate)
        {
            _listening?.Cancel(); // 每条远程会话的令牌都链接在它上面:一起断开
            _listening?.Dispose();
            _listening = null;
            _listener?.Dispose();
            _listener = null;
            _code = null;
            _qrTokens.Clear();
        }
    }

    private async Task AcceptLoopAsync(LanListener listener, CancellationToken ct)
    {
        var options = new ServerOptions(_identity.DeviceId, _version, Transports.Remote, Authenticate);
        await foreach (var (stream, remote) in listener.AcceptAsync(ct).ConfigureAwait(false))
        {
            if (_sessions.Count >= MaxRemoteSessions)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => ServeAsync(stream, remote, options, ct), CancellationToken.None);
        }
    }

    // ---- 中继 ---------------------------------------------------------------------------------------------------

    private void StartRelay()
    {
        lock (_gate)
        {
            if (_relay is not null || !_settings.Enabled || _trust is null ||
                !RelayAddress.TryNormalize(_settings.RelayUrl, out var url))
            {
                return;
            }

            _relaySessions = new CancellationTokenSource();
            var relay = new RelayRegistration(url, _identity.Keys, _trust.RouteTokenHashes(), OnRelaySessionAsync, _relayOptions);
            relay.StatusChanged += _ => Publish();
            _relay = relay;
            relay.Start();
        }
    }

    private void StopRelay()
    {
        RelayRegistration? relay;
        CancellationTokenSource? sessions;
        lock (_gate)
        {
            relay = _relay;
            sessions = _relaySessions;
            _relay = null;
            _relaySessions = null;
        }

        // 经中继的会话随之断开(和关远程开关时局域网会话一起断开一样)。
        sessions?.Cancel();
        sessions?.Dispose();
        if (relay is not null)
        {
            _ = Task.Run(async () =>
            {
                await relay.DisposeAsync().ConfigureAwait(false);
                Publish();
            });
        }
    }

    /// <summary>把当前有效的路由令牌哈希推给中继(配对、吊销之后)。不等。</summary>
    private void SyncRelayTokens() => _ = SyncRelayTokensAsync();

    /// <summary>上限:推送卡在一条写不动的连接上时,不能让调用方(编排指令的回执)无限等。</summary>
    private static readonly TimeSpan RelaySyncWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 把当前有效的路由令牌哈希推给中继,在「已发出」时完成(见 <see cref="RelayRegistration.SetTokensAsync"/>)。云端的信任指令要等它再回执:
    /// 回执之后编排服务才会把路由令牌交给控制端,控制端马上经中继来连——令牌还没推出去的话中继回 <c>unauthorized</c>,
    /// 控制端(仅中继可达的云端设备)会把设备判成已吊销、不再重试。最多等 <see cref="RelaySyncWait"/>。
    /// </summary>
    private async Task SyncRelayTokensAsync()
    {
        RelayRegistration? relay;
        lock (_gate)
        {
            relay = _relay;
        }

        if (relay is null || _trust is null)
        {
            return;
        }

        try
        {
            await relay.SetTokensAsync(_trust.RouteTokenHashes()).WaitAsync(RelaySyncWait).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 推送还在路上:回执不再等它(令牌最终会到;重连注册时也会带上)
        }
    }

    private async Task OnRelaySessionAsync(RelayAcceptedSession accepted)
    {
        CancellationToken token;
        lock (_gate)
        {
            token = _relaySessions?.Token ?? new CancellationToken(canceled: true);
        }

        if (_sessions.Count >= MaxRemoteSessions || token.IsCancellationRequested)
        {
            await accepted.Stream.DisposeAsync().ConfigureAwait(false);
            return;
        }

        var tokenHash = accepted.TokenHash;
        var options = new ServerOptions(_identity.DeviceId, _version, Transports.Remote, (_, peer) => AuthenticateRelay(peer, tokenHash));
        await ServeAsync(accepted.Stream, null, options, token).ConfigureAwait(false);
    }

    /// <summary>经中继的会话没有来源 IP;配对只能在局域网 / 扫码(直连)完成(09 §3、§9)。</summary>
    private static void RequireDirect(CallContext context)
    {
        if (context.Peer is { Remote: null })
        {
            throw new ProtocolException(ErrorCodes.Forbidden, "Pairing is only available on the local network");
        }
    }

    private async Task ServeAsync(Stream stream, System.Net.IPEndPoint? remote, ServerOptions options, CancellationToken listening)
    {
        SecureChannel channel;
        try
        {
            channel = await SecureChannel.AcceptAsync(stream, _identity.Keys, listening).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SecureChannelException or OperationCanceledException or IOException)
        {
            return; // 握手失败(不是我们的协议、超时、证书不合法):直接断开
        }

        var peer = new SessionPeer(channel.PeerFingerprint, channel.LocalFingerprint, channel.Exporter, remote);
        var id = Guid.NewGuid();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(listening);
        _sessions[id] = (peer.FingerprintHex, cts);
        SessionsChanged?.Invoke();
        Publish();
        try
        {
            await new ServerSession(new FrameStream(channel.Stream), _router, _topics, options, peer).RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _sessions.TryRemove(id, out _);
            await channel.DisposeAsync().ConfigureAwait(false);
            SessionsChanged?.Invoke();
            Publish();
        }
    }

    private PairBeginResult PairBegin(PairBeginParams p, CallContext context)
    {
        RequireLocalMode(CloudErrorKeys.PairingUnsupported);
        RequireDirect(context);
        string code;
        lock (_gate)
        {
            code = _code ?? throw new ProtocolException(ErrorCodes.Unavailable, "Remote connections are turned off", retryable: false);
        }

        if (_trust is null)
        {
            throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);
        }

        var server = new PairingServer(_identity.DeviceId, code);
        var name = string.IsNullOrWhiteSpace(p.ClientName) ? "Controller" : p.ClientName.Trim()[..Math.Min(p.ClientName.Trim().Length, 64)];
        context.Items["pair"] = new PendingPairing(server, code, name);
        return new PairBeginResult(server.Salt, server.B);
    }

    private async ValueTask<PairFinishResult> PairFinishAsync(PairFinishParams p, CallContext context, CancellationToken ct)
    {
        RequireLocalMode(CloudErrorKeys.PairingUnsupported);
        RequireDirect(context);
        if (!context.Items.TryRemove("pair", out var state) || state is not PendingPairing pending || context.Peer is not { } peer || _trust is null)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "Call pair.begin first");
        }

        var transcript = new PairingTranscript(peer.Exporter, peer.Fingerprint, peer.LocalFingerprint);
        bool stillCurrent;
        lock (_gate)
        {
            stillCurrent = _code == pending.Code;
        }

        if (!stillCurrent || pending.Server.Finish(p.A, p.M1, p.Proof, transcript) is not { } accepted)
        {
            var rotated = false;
            lock (_gate)
            {
                if (++_failures >= MaxFailures && _code is not null)
                {
                    _code = NewCode();
                    _failures = 0;
                    rotated = true;
                }
            }

            if (rotated)
            {
                Publish();
            }

            await Task.Delay(_failureDelay, ct).ConfigureAwait(false); // 限制在线猜测的速度
            throw new ProtocolException(ErrorCodes.Unauthorized, "Pairing failed");
        }

        var routeToken = Trust(peer, pending.Name);
        RotateCode(); // 配对码用一次就换
        return new PairFinishResult(accepted.M2, accepted.Proof, "member", routeToken, _settings.RelayUrl);
    }

    /// <summary>记下新配对的控制端,返回给它的中继路由令牌(库里只存哈希)。</summary>
    private string Trust(SessionPeer peer, string name)
    {
        var routeToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        _trust!.Put(new ControllerTrust(peer.FingerprintHex, name, "member", now, now), RelayProtocol.HashToken(routeToken));
        SyncRelayTokens();
        return routeToken;
    }

    /// <summary>生成配对二维码:一次性 128 位密钥,10 分钟有效。远程开关没打开时不能生成。</summary>
    private PairQrResult CreateQr()
    {
        RequireLocalMode(CloudErrorKeys.PairingUnsupported);
        var port = Port ?? throw new ProtocolException(ErrorCodes.Unavailable, "Remote connections are turned off", retryable: false);
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var expires = now + (long)QrLifetime.TotalMilliseconds;
        var token = RandomNumberGenerator.GetBytes(PairingQr.TokenLength);
        lock (_gate)
        {
            _qrTokens.RemoveAll(t => t.ExpiresAt <= now);
            if (_qrTokens.Count >= MaxQrTokens)
            {
                _qrTokens.RemoveAt(0);
            }

            _qrTokens.Add((token, expires));
        }

        var addresses = LanTransport.LocalAddresses().Select(a => PairingQr.FormatAddress(a.ToString(), port)).ToArray();
        var qr = new PairingQr(_identity.DeviceId, _identity.Keys.Fingerprint, addresses, token, expires);
        return new PairQrResult(qr.ToUri(), expires);
    }

    /// <summary>
    /// 扫码配对:控制端在 TLS 握手时已经按二维码里的指纹钉扎了设备,这里只要出示一次性密钥。
    /// 逐个常数时间比较(不因为命中位置不同而泄露时间),成功就把这枚密钥作废。
    /// </summary>
    private async ValueTask<PairClaimResult> PairClaimAsync(PairClaimParams p, CallContext context, CancellationToken ct)
    {
        RequireLocalMode(CloudErrorKeys.PairingUnsupported);
        RequireDirect(context);
        if (context.Peer is not { } peer || _trust is null)
        {
            throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);
        }

        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var matched = false;
        lock (_gate)
        {
            _qrTokens.RemoveAll(t => t.ExpiresAt <= now);
            var hit = -1;
            for (var i = 0; i < _qrTokens.Count; i++)
            {
                if (CryptographicOperations.FixedTimeEquals(_qrTokens[i].Token, p.Token) && hit < 0)
                {
                    hit = i;
                }
            }

            if (hit >= 0)
            {
                _qrTokens.RemoveAt(hit);
                matched = true;
            }
        }

        if (!matched)
        {
            await Task.Delay(_failureDelay, ct).ConfigureAwait(false); // 限制在线猜测的速度
            throw new ProtocolException(ErrorCodes.Unauthorized, "Pairing failed");
        }

        var name = string.IsNullOrWhiteSpace(p.ClientName) ? "Controller" : p.ClientName.Trim()[..Math.Min(p.ClientName.Trim().Length, 64)];
        var routeToken = Trust(peer, name);
        Publish();
        return new PairClaimResult("member", routeToken, _settings.RelayUrl);
    }

    private void RotateCode()
    {
        lock (_gate)
        {
            if (_listener is null)
            {
                return;
            }

            _code = NewCode();
            _failures = 0;
        }

        Publish();
    }

    private void SetRole(string fingerprint, string role)
    {
        if (role is not ("owner" or "member" or "viewer"))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown role '{role}'");
        }

        if (_trust is null || !_trust.SetRole(fingerprint, role))
        {
            throw new ProtocolException(ErrorCodes.NotFound, "No such controller");
        }

        // 会话的角色在握手时定下:断开它,重连后按新角色。
        Disconnect(fingerprint);
        Publish();
    }

    private void Disconnect(string fingerprint)
    {
        foreach (var (_, session) in _sessions.Where(s => s.Value.Fingerprint == fingerprint))
        {
            session.Cts.Cancel();
        }
    }

    private void Revoke(string fingerprint) => _ = RevokeAsync(fingerprint);

    private Task RevokeAsync(string fingerprint)
    {
        _trust?.Remove(fingerprint);
        // 先断开这个控制端的所有连接(局域网和中继),再作废它在中继上的路由令牌(中继也会掐断用这个令牌的会话)。
        Disconnect(fingerprint);
        var synced = SyncRelayTokensAsync();
        Publish();
        return synced;
    }

    // ---- 云端:编排服务下发的信任记录(09 §4「云端设备」)-------------------------------------------------------------

    private static bool IsHex64(string? text) => text is { Length: 64 } && text.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>校验一条下发的信任记录;不合法返回 false(指令按 <c>invalid</c> 拒绝,不部分应用)。</summary>
    internal static bool IsValidTrust(Ncw.Protocol.Orchestration.TrustEntry? entry) =>
        entry is not null && IsHex64(entry.Fingerprint) && IsHex64(entry.RouteTokenHash)
        && entry.Role is "owner" or "member" or "viewer" && entry.Name is { Length: <= 64 };

    /// <summary>
    /// 下发(或覆盖)一个控制端的信任记录,并把它的路由令牌哈希推给中继。角色变了就断开它现有的连接(会话的角色在握手时定下,重连后按新角色)。幂等。
    /// </summary>
    internal Task PutTrustAsync(Ncw.Protocol.Orchestration.TrustEntry entry)
    {
        var trust = _trust ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);
        var existing = trust.Get(entry.Fingerprint);
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        trust.Put(new ControllerTrust(entry.Fingerprint, string.IsNullOrWhiteSpace(entry.Name) ? "Controller" : entry.Name.Trim(), entry.Role, existing?.PairedAt ?? now, existing?.LastSeenAt), entry.RouteTokenHash);
        if (existing is not null && existing.Role != entry.Role)
        {
            Disconnect(entry.Fingerprint);
        }

        var synced = SyncRelayTokensAsync();
        Publish();
        return synced;
    }

    /// <summary>吊销:立即断开这个控制端的所有连接并作废它的路由令牌(<see cref="Revoke"/> 同一条路径)。不存在也不是错误(幂等)。在令牌撤销已发给中继时完成。</summary>
    internal Task RevokeTrustAsync(string fingerprint) => RevokeAsync(fingerprint);

    /// <summary>整体替换:列表里的写入(覆盖),不在列表里的吊销。唤醒后对账用。在全部令牌推送都已发给中继时完成。</summary>
    internal Task ReplaceTrustAsync(IReadOnlyCollection<Ncw.Protocol.Orchestration.TrustEntry> entries)
    {
        var synced = new List<Task>();
        foreach (var entry in entries)
        {
            synced.Add(PutTrustAsync(entry));
        }

        var keep = entries.Select(static e => e.Fingerprint).ToHashSet(StringComparer.Ordinal);
        foreach (var existing in _trust?.List() ?? [])
        {
            if (!keep.Contains(existing.Fingerprint))
            {
                synced.Add(RevokeAsync(existing.Fingerprint));
            }
        }

        return Task.WhenAll(synced);
    }

    /// <summary>断开所有远程会话(休眠前)。</summary>
    internal void DisconnectAll()
    {
        foreach (var (_, session) in _sessions)
        {
            session.Cts.Cancel();
        }
    }

    /// <summary>
    /// 停止对外服务并等收尾(云端休眠 / 升级前):不再监听 / 注册,断开所有会话,<b>等中继注册真正注销</b>(设备在中继上消失,控制端立刻得到 <c>device_offline</c>,
    /// 而不是等超时)。之后 <see cref="Dispose"/> 仍然安全。
    /// </summary>
    internal async Task StopAsync()
    {
        StopListening();
        RelayRegistration? relay;
        CancellationTokenSource? sessions;
        lock (_gate)
        {
            relay = _relay;
            sessions = _relaySessions;
            _relay = null;
            _relaySessions = null;
        }

        sessions?.Cancel();
        sessions?.Dispose();
        DisconnectAll();
        if (relay is not null)
        {
            await relay.DisposeAsync().ConfigureAwait(false);
        }

        // 等会话任务自己收尾(最多 2 秒):它们在取消后会走 finally 摘掉自己
        for (var i = 0; i < 40 && !_sessions.IsEmpty; i++)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        Publish();
    }

    private void Publish() =>
        _topics.Publish(DeviceMethods.RemoteTopic.Name, RemoteEvents.Changed, State(), PayloadShapes.GetShape<RemoteState>());

    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        StopListening();
        StopRelay();
    }

    private sealed record PendingPairing(PairingServer Server, string Code, string Name);
}
