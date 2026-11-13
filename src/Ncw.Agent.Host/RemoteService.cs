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
public sealed record RemoteSettings(bool Enabled, int Port, bool LanOnly)
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
/// </list>
/// 依赖数据库(信任记录、设置),数据就绪后才能打开。
/// </summary>
public sealed class RemoteService : IDisposable
{
    private const string SettingsKey = "v3.remote";
    private const int MaxFailures = 5;
    private const int MaxRemoteSessions = 32;

    private readonly DeviceIdentity _identity;
    private readonly TopicHub _topics;
    private readonly MethodRouter _router;
    private readonly string _version;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _failureDelay;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, (string Fingerprint, CancellationTokenSource Cts)> _sessions = new();

    private ControllerTrustRepository? _trust;
    private KvStore? _kv;
    private RemoteSettings _settings = RemoteSettings.Default;
    private LanListener? _listener;
    private CancellationTokenSource? _listening;
    private string? _code;
    private int _failures;
    private string? _error;

    public RemoteService(DeviceIdentity identity, MethodRouter router, TopicHub topics, string version, TimeProvider clock, TimeSpan? failureDelay = null)
    {
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
        router.Handle(DeviceMethods.PairBegin, (p, ctx, _) => ValueTask.FromResult(PairBegin(p, ctx)));
        router.Handle(DeviceMethods.PairFinish, PairFinishAsync);
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

    /// <summary>数据就绪:读设置,之前打开过就重新开始监听。</summary>
    public void OnDataReady(AgentDatabase database)
    {
        _trust = new ControllerTrustRepository(database);
        _kv = new KvStore(database);
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
        }

        Publish();
    }

    public RemoteStatus SetEnabled(bool enabled)
    {
        if (_kv is null)
        {
            throw new ProtocolException(ErrorCodes.Unavailable, "Device data is not ready", retryable: true);
        }

        _settings = _settings with { Enabled = enabled };
        _kv.Set(SettingsKey, JsonSerializer.Serialize(_settings, RemoteJsonContext.Default.RemoteSettings));
        if (enabled)
        {
            StartListening();
        }
        else
        {
            StopListening();
        }

        Publish();
        return Status();
    }

    public RemoteStatus Status()
    {
        lock (_gate)
        {
            var port = _listener?.Port;
            return new RemoteStatus(
                _settings.Enabled, port, _settings.Port, _settings.LanOnly,
                port is null ? [] : [.. LanTransport.LocalAddresses().Select(a => a.ToString())],
                _listener is null ? null : _code, _error);
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
    private Role? Authenticate(HelloFrame hello, SessionPeer? peer)
    {
        if (peer is null)
        {
            return null; // 远程监听上不会出现,防御
        }

        if (_trust?.Get(peer.FingerprintHex) is { } trusted && Enum.TryParse<Role>(trusted.Role, ignoreCase: true, out var role) && role >= Role.Viewer)
        {
            _trust.Touch(peer.FingerprintHex, _clock.GetUtcNow().ToUnixTimeMilliseconds());
            return role;
        }

        return Role.Unpaired;
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

    private async Task ServeAsync(Stream stream, System.Net.IPEndPoint remote, ServerOptions options, CancellationToken listening)
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
            Publish();
        }
    }

    private PairBeginResult PairBegin(PairBeginParams p, CallContext context)
    {
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

        var routeToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        _trust.Put(new ControllerTrust(peer.FingerprintHex, pending.Name, "member", now, now),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(routeToken))));
        RotateCode(); // 配对码用一次就换
        return new PairFinishResult(accepted.M2, accepted.Proof, "member", routeToken);
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

    private void Revoke(string fingerprint)
    {
        _trust?.Remove(fingerprint);
        foreach (var (_, session) in _sessions.Where(s => s.Value.Fingerprint == fingerprint))
        {
            session.Cts.Cancel();
        }

        Publish();
    }

    private void Publish() =>
        _topics.Publish(DeviceMethods.RemoteTopic.Name, RemoteEvents.Changed, State(), PayloadShapes.GetShape<RemoteState>());

    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose() => StopListening();

    private sealed record PendingPairing(PairingServer Server, string Code, string Name);
}
