using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Ncw.Agent.Gateway;
using Ncw.Agent.Host.Providers;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Storage.Credentials;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Services;

/// <summary>
/// 开放网关在 Host 里的装配(docs/design/09 §11)。
/// <list type="bullet">
/// <item><b>默认关闭</b>;开关是设置 <c>gateway.enabled</c>(和「允许远程连接」是两个独立的开关);设置一变就对账:开 / 关 / 换端口 / 换监听范围,一次只做一件(串行),最后一份设置获胜、不遗留监听器。
/// 故障转移只影响下一次路由,不重绑端口、不断开 SSE 客户端。</item>
/// <item><b>监听范围</b>:<c>allowRemote</c> 假 = 只绑回环;真 = 绑全部地址,但只收回环 / 私有网段来源。期望端口被占用时换一个随机的(状态里带实际端口)。</item>
/// <item><b>API 密钥</b>绑定角色(viewer / member),32 字节随机数的十六进制,存在凭据表里(NCK1,<c>gateway:key:&lt;role&gt;</c>);首次取时生成;换新密钥旧的立刻失效;常数时间比较;只经 <c>gateway.getKey</c>(owner + 仅本机通道)单次取回,不进状态 / 事件 / 设置。</item>
/// <item><b>同一张方法表</b>:网关的请求以密钥绑定的角色、<c>Remote</c> 承载进 Agent 的方法路由,所以本机专用的方法天然调不到(见 <see cref="GatewayExposure"/>)。
/// 预检:<c>runs.start</c> 在会话忙时拒绝(409 busy)而不是排队——开放网关的 Run 不排队(<c>RunOrigin.Gateway</c> 的语义)。</item>
/// </list>
/// </summary>
internal sealed class GatewayRuntime : IGatewayAuth, IAsyncDisposable
{
    public static readonly string[] KeyRoles = ["viewer", "member"];

    private readonly SettingsService _settings;
    private readonly CredentialStore _credentials;
    private readonly MethodRouter _router;
    private readonly TopicHub _topics;
    private readonly Conversation.ConversationService _conversation;
    private readonly ProviderRuntime? _providers;
    private readonly TimeProvider _clock;
    private readonly string _version;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _keyGate = new();
    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);
    private GatewayServer? _server;
    private string? _configuration;
    private string? _error;
    private bool _stopped;

    public GatewayRuntime(
        SettingsService settings, CredentialStore credentials, MethodRouter router, TopicHub topics, Conversation.ConversationService conversation, ProviderRuntime? providers,
        TimeProvider clock, string version)
    {
        _settings = settings;
        _credentials = credentials;
        _router = router;
        _topics = topics;
        _conversation = conversation;
        _providers = providers;
        _clock = clock;
        _version = version;
        _settings.Changed += (_, _) => _ = Task.Run(ReconcileAsync);
    }

    /// <summary>测试缝(<c>InternalsVisibleTo Ncw.Agent.Host.Tests</c>):对账的串行闸门。测试占住它来让多次对账排队,复现「被取代的对账提前返回」的竞态。</summary>
    internal SemaphoreSlim ReconcileGate => _gate;

    // ───────────────────────── 生命周期 ─────────────────────────

    /// <summary>
    /// 对账:让监听状态和设置一致。串行;每次拿到闸门都读<b>当前</b>设置去对账,所以快速连点时最后一份设置获胜。
    /// 对账是幂等的(监听和设置已经一致就直接返回)。<b>不要</b>加「版本号被后来的调用超过就提前返回」这类优化:
    /// 设置变更事件会 <c>Task.Run</c> 一次对账,<c>setEnabled</c> 的处理器自己又 <c>await</c> 一次——被取代的那次提前返回时,
    /// 取代它的那次可能还没起监听,处理器就会读到 <c>Port == null</c>。保证是:任何一次对账返回时,监听状态已和它拿到闸门时的设置一致。
    /// </summary>
    public async Task ReconcileAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped)
            {
                return;
            }

            var s = _settings.Current().Gateway;
            var wanted = $"{(s.AllowRemote ? "any" : "loopback")}:{s.PreferredPort}";
            _error = null;
            if (s.Enabled && _server is not null && _configuration == wanted)
            {
                return;
            }

            var previous = _server;
            _server = null;
            _configuration = null;
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            if (!s.Enabled)
            {
                return;
            }

            _server = Start(s, out _error);
            _configuration = _server is null ? null : wanted;
        }
        finally
        {
            _gate.Release();
        }
    }

    private GatewayServer? Start(GatewaySettings s, out string? error)
    {
        var options = new GatewayOptions
        {
            Router = _router,
            Topics = _topics,
            Auth = this,
            Guard = Guard,
            Title = "NextCoWork Agent API",
            Version = _version,
            Clock = _clock,
        };
        try
        {
            error = null;
            return GatewayServer.Start(options, s.PreferredPort, s.AllowRemote);
        }
        catch (SocketException)
        {
            try
            {
                // 期望端口被占用:换一个系统分配的(状态里带实际端口)
                error = "gateway.portInUse";
                return GatewayServer.Start(options, 0, s.AllowRemote);
            }
            catch (SocketException)
            {
                error = "gateway.cannotListen";
                return null;
            }
        }
    }

    /// <summary>预检:开放网关的 Run 不排队——<c>runs.start</c> 在会话忙时直接 409。</summary>
    private ProtocolException? Guard(MethodDef method, byte[] parameters, GatewayCaller caller)
    {
        if (method.Name != ConversationMethods.RunsStart.Name)
        {
            return null;
        }

        try
        {
            var start = FrameCodec.DecodePayload(parameters, ConversationMethods.RunsStart.ParamsShape);
            if (_conversation.Registry.ActiveTopLevelRun(start.SessionId) is not null || _conversation.Registry.IsSessionBusy(start.SessionId))
            {
                return new ProtocolException(ErrorCodes.Busy, $"Session {start.SessionId} has a run in progress; the gateway does not queue.", retryable: true);
            }
        }
        catch (Exception e) when (e is ProtocolException or InvalidOperationException or IOException or FormatException)
        {
            // 参数畸形:交给方法路由给出规范的 invalid_request
        }

        return null;
    }

    // ───────────────────────── 状态 ─────────────────────────

    public GatewayStatusDto Status()
    {
        var s = _settings.Current();
        var health = _providers is null ? [] : _providers.Router.Health().Select(static h => new GatewayHealthDto(
            h.ProviderId, h.Healthy, h.Score, h.ConsecutiveFailures, h.CooldownUntil?.ToUnixTimeMilliseconds(), h.LastLatencyMs, h.LastError, h.LastCheckedAt.ToUnixTimeMilliseconds())).ToArray();
        return new GatewayStatusDto(s.Gateway.Enabled, _server?.Port, s.Gateway.AllowRemote ? "0.0.0.0" : "127.0.0.1", s.Gateway.AllowRemote, s.Failover, health, _error);
    }

    /// <summary>真实可访问的地址(<c>0.0.0.0</c> 是绑定地址,不是可访问地址;允许局域网时含本机的局域网 IPv4)。</summary>
    public string[] Addresses()
    {
        if (_server is not { } server)
        {
            return [];
        }

        var hosts = new List<string> { "127.0.0.1" };
        if (_settings.Current().Gateway.AllowRemote)
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(static n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (var address in nic.GetIPProperties().UnicastAddresses.Select(static a => a.Address))
                {
                    if (address.AddressFamily == AddressFamily.InterNetwork && Ncw.Protocol.Transport.LanTransport.IsLanAddress(address))
                    {
                        hosts.Add(address.ToString());
                    }
                }
            }
        }

        return [.. hosts.Distinct().Select(h => $"http://{h}:{server.Port}")];
    }

    public GatewayStatusDto ResetHealth(string? providerId)
    {
        _providers?.Router.ResetHealth(string.IsNullOrEmpty(providerId) ? null : providerId);
        return Status();
    }

    public string OpenApi() => OpenApiExporter.Export(_router.Methods, "NextCoWork Agent API", _version);

    // ───────────────────────── 密钥 ─────────────────────────

    private static string Reference(string role) => $"gateway:key:{role}";

    private static string RequireRole(string role) =>
        KeyRoles.Contains(role) ? role : throw ServiceMapping.Invalid("gateway.invalid.role", $"'{role}' is not a gateway key role (viewer, member).");

    /// <summary>取(没有就生成)某个角色的密钥。</summary>
    public string GetKey(string role)
    {
        role = RequireRole(role);
        lock (_keyGate)
        {
            if (_keys.TryGetValue(role, out var cached))
            {
                return cached;
            }

            var key = _credentials.Get(Reference(role));
            if (string.IsNullOrEmpty(key))
            {
                key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                _credentials.Set(Reference(role), key);
            }

            return _keys[role] = key;
        }
    }

    /// <summary>换新密钥,旧的立刻失效。</summary>
    public string RotateKey(string role)
    {
        role = RequireRole(role);
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        lock (_keyGate)
        {
            _credentials.Set(Reference(role), key);
            return _keys[role] = key;
        }
    }

    /// <summary>核对 API 密钥(常数时间比较,每个角色都比一遍,不因为早匹配而泄漏时序)。</summary>
    public GatewayCaller? Authenticate(string bearer)
    {
        if (bearer.Length is 0 or > 256)
        {
            return null;
        }

        GatewayCaller? found = null;
        var presented = System.Text.Encoding.UTF8.GetBytes(bearer);
        foreach (var role in KeyRoles)
        {
            string key;
            try
            {
                key = GetKey(role);
            }
            catch (Exception e) when (e is CryptographicException or Microsoft.Data.Sqlite.SqliteException)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(presented, System.Text.Encoding.UTF8.GetBytes(key)))
            {
                found = new GatewayCaller(role, role == "viewer" ? Role.Viewer : Role.Member);
            }
        }

        return found;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _stopped = true;
            var previous = _server;
            _server = null;
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
