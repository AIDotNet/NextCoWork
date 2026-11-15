using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ncw.Protocol.Transport;

namespace Ncw.Relay;

/// <summary>
/// 服务器中继(09 §9、05 B5)。单实例、内存状态:设备注册连接、令牌哈希、进行中的会话都只在内存里,重启后设备自动重新注册。
/// 多实例时按 deviceId 粘性路由(<see cref="IRelayPlacement"/>),不归本实例的设备请求得到 421。
/// 一个进程里可以起多个实例(集成测试用)。
/// </summary>
public sealed class RelayServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RelayApp _relay;

    private RelayServer(WebApplication app, RelayApp relay, string[] urls)
    {
        _app = app;
        _relay = relay;
        Urls = urls;
    }

    /// <summary>实际监听的地址(端口 0 时是系统分配的)。</summary>
    public IReadOnlyList<string> Urls { get; }

    /// <summary>第一个地址的 WebSocket 形式,例如 <c>ws://127.0.0.1:5123</c>。</summary>
    public Uri WebSocketUrl
    {
        get
        {
            var url = new UriBuilder(Urls[0]) { Scheme = Urls[0].StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws" };
            return new Uri(url.Uri.GetLeftPart(UriPartial.Authority));
        }
    }

    /// <summary>宿主开始停止(SIGTERM / Ctrl+C / <c>StopApplication</c>)时触发:入口程序据此退出主流程。</summary>
    public CancellationToken Stopping => _app.Lifetime.ApplicationStopping;

    public RelayMetrics Metrics => _relay.Metrics;

    /// <summary>已登记的设备数(含离线但还有会话的)。</summary>
    public int DeviceCount => _relay.Registry.Count;

    /// <summary>在线(注册连接存在)的设备数。</summary>
    public int DevicesOnline => _relay.Registry.Online;

    /// <summary>某台设备当前登记的路由令牌数;没有登记为 0。</summary>
    public int TokenCount(string deviceId) => _relay.Registry.Find(deviceId)?.TokenCount ?? 0;

    /// <summary>某台设备当前的会话数(含等待设备接入的)。</summary>
    public int SessionCount(string deviceId) => _relay.Registry.Find(deviceId)?.SessionCount ?? 0;

    public bool IsOnline(string deviceId) => _relay.Registry.Find(deviceId)?.IsOnline ?? false;

    public static async Task<RelayServer> StartAsync(RelayOptions options, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        // ASP.NET 的请求日志会把完整 URL(含一次性会话 id)打出来:关掉,中继自己的结构化日志只记元数据。
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        options.ConfigureLogging?.Invoke(builder.Logging);
        builder.WebHost.UseUrls(options.Urls);
        builder.WebHost.UseKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024; // 16 KiB
            kestrel.Limits.MaxRequestBodySize = 1024; // 升级请求没有 body
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
        });
        if (options.CertificatePath is { } certificate)
        {
            builder.WebHost.UseKestrelHttpsConfiguration();
            var loaded = X509Certificate2.CreateFromPemFile(certificate, options.CertificateKeyPath);
            builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = loaded));
        }

        var app = builder.Build();
        var relay = new RelayApp(options, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ncw.Relay"));
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = options.KeepAliveInterval, KeepAliveTimeout = options.KeepAliveTimeout });
        app.Run(relay.HandleAsync);
        app.Lifetime.ApplicationStopping.Register(relay.Stop);

        await app.StartAsync(ct).ConfigureAwait(false);
        var urls = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToArray();
        relay.StartSweeper();
        relay.Log.Listening(string.Join(",", urls));
        return new RelayServer(app, relay, urls);
    }

    /// <summary>停止监听并掐断所有会话(设备和控制端会各自重连)。</summary>
    public async Task StopAsync()
    {
        _relay.Stop();
        await _app.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _relay.Dispose();
    }
}

/// <summary>请求处理:三条 WebSocket 路径(设备注册、设备数据、控制端接入)和 <c>/healthz</c>、<c>/metrics</c>。</summary>
internal sealed class RelayApp : IDisposable
{
    private readonly RelayOptions _options;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationTokenSource _draining = new();
    private readonly CancellationTokenSource _drainBackstop = new();
    private readonly RateLimiter _connectsPerIp;
    private readonly RateLimiter _registersPerIp;
    private readonly RateLimiter _failuresPerIp;
    private readonly RateLimiter _failuresPerDevice;
    private readonly RateLimiter _connectsPerDevice;
    private readonly RateLimiter _controlPerDevice;
    private readonly ConcurrencyGate _connectionsPerIp;
    private Task _sweeper = Task.CompletedTask;

    public RelayApp(RelayOptions options, ILogger log)
    {
        _options = options;
        Log = log;
        Metrics = new RelayMetrics();
        Registry = new DeviceRegistry(Metrics);
        _connectsPerIp = new RateLimiter(options.ConnectsPerIp, options.Clock);
        _registersPerIp = new RateLimiter(options.RegistersPerIp, options.Clock);
        _failuresPerIp = new RateLimiter(options.FailuresPerIp, options.Clock);
        _failuresPerDevice = new RateLimiter(options.FailuresPerDevice, options.Clock);
        _connectsPerDevice = new RateLimiter(options.ConnectsPerDevice, options.Clock);
        _controlPerDevice = new RateLimiter(options.ControlMessagesPerDevice, options.Clock);
        _connectionsPerIp = new ConcurrencyGate(options.MaxConnectionsPerIp);
    }

    public ILogger Log { get; }

    public RelayMetrics Metrics { get; }

    public DeviceRegistry Registry { get; }

    public void StartSweeper() => _sweeper = Task.Run(async () =>
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                _connectsPerIp.Sweep();
                _registersPerIp.Sweep();
                _failuresPerIp.Sweep();
                _failuresPerDevice.Sweep();
                _connectsPerDevice.Sweep();
                _controlPerDevice.Sweep();
            }
        }
        catch (OperationCanceledException)
        {
        }
    });

    /// <summary>已拼接的会话从停机信号到被兜底掐断的最长时间:正常的关闭握手(<c>Pipe</c> 的宽限 3 秒 + 关闭的 2 秒上限)在此之内一定结束。</summary>
    private static readonly TimeSpan DrainBackstop = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 停机。<b>已拼接的会话优雅排空</b>(<see cref="Pipe"/> 给两边发关闭帧、等它们的关闭帧,两边读到干净的 EOF 立刻重连);
    /// 还没拼接的(等设备接入、注册连接、握手中)立即掐断;排空超过 <see cref="DrainBackstop"/> 的兜底掐断。
    /// </summary>
    public void Stop()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        _draining.Cancel();
        Registry.CancelAll(RelayErrors.Unavailable, includePiped: false);
        _stopping.Cancel();
        _ = Task.Delay(DrainBackstop, _options.Clock, _drainBackstop.Token).ContinueWith(
            _ => Registry.CancelAll(RelayErrors.Unavailable, includePiped: true), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _drainBackstop.Cancel();
        _drainBackstop.Dispose();
        _draining.Dispose();
        _stopping.Dispose();
    }

    public Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        switch (path)
        {
            case "/healthz":
                return HealthAsync(context);
            case "/metrics":
                return MetricsAsync(context);
            case RelayProtocol.DevicePath:
                return WithLimitsAsync(context, RegisterDeviceAsync, registration: true);
            case RelayProtocol.DataPath:
                return WithLimitsAsync(context, AttachDataAsync, registration: false);
            case RelayProtocol.ConnectPath:
                return WithLimitsAsync(context, ConnectControllerAsync, registration: false);
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
        }
    }

    // ---- 通用前置检查 -------------------------------------------------------------------------------------------

    private delegate Task Handler(HttpContext context, string deviceId, string remote);

    private async Task WithLimitsAsync(HttpContext context, Handler handler, bool registration)
    {
        var path = context.Request.Path.Value;
        var remote = RemoteOf(context);
        if (!HttpMethods.IsGet(context.Request.Method) || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
            return;
        }

        var deviceId = context.Request.Query["d"].ToString();
        if (!RelayProtocol.IsValidDeviceId(deviceId))
        {
            Refuse(context, StatusCodes.Status400BadRequest, RelayErrors.BadRequest, remote, "");
            return;
        }

        if (_options.Placement.Locate(deviceId) is { IsLocal: false } placement)
        {
            Refuse(context, StatusCodes.Status421MisdirectedRequest, RelayErrors.Misdirected, remote, deviceId);
            if (placement.OwnerUrl is not null)
            {
                context.Response.Headers["X-Ncw-Relay-Owner"] = placement.OwnerUrl;
            }

            return;
        }

        // 失败过多的来源:先于一切比较就拒绝(这样才限制得住暴力尝试)。
        // 设备的数据连接不计入连接速率:它由一次性会话 id 授权,猜错有失败计数管;它的名额由「每 IP 并发」和「每设备 16 个会话」管。
        var isData = path == RelayProtocol.DataPath;
        if (_failuresPerIp.IsExceeded(remote) || (!isData && !_connectsPerIp.TryAcquire(remote)) || (registration && !_registersPerIp.TryAcquire(remote)))
        {
            RateLimited(context, remote, deviceId);
            return;
        }

        using var lease = _connectionsPerIp.TryEnter(remote);
        if (lease is null)
        {
            RateLimited(context, remote, deviceId);
            return;
        }

        Metrics.Connection(1);
        try
        {
            await handler(context, deviceId, remote).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.ConnectionError(e.GetType().Name);
        }
        finally
        {
            Metrics.Connection(-1);
        }
    }

    private void RateLimited(HttpContext context, string remote, string deviceId)
    {
        context.Response.Headers.RetryAfter = "30";
        Refuse(context, StatusCodes.Status429TooManyRequests, RelayErrors.RateLimited, remote, deviceId);
    }

    private void Refuse(HttpContext context, int status, string reason, string remote, string deviceId)
    {
        Metrics.Reject(reason);
        Log.Refused(context.Request.Path.Value ?? "", reason, remote, deviceId);
        context.Response.StatusCode = status;
    }

    private string RemoteOf(HttpContext context)
    {
        if (_options.TrustForwardedFor && context.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded)
        {
            var last = forwarded.Split(',')[^1].Trim();
            if (IPAddress.TryParse(last, out var ip))
            {
                return Normalize(ip);
            }
        }

        return context.Connection.RemoteIpAddress is { } remote ? Normalize(remote) : "unknown";
    }

    private static string Normalize(IPAddress ip) => (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();

    // ---- 健康检查与指标 -----------------------------------------------------------------------------------------

    private static async Task HealthAsync(HttpContext context)
    {
        context.Response.ContentType = "text/plain";
        await context.Response.WriteAsync("ok").ConfigureAwait(false);
    }

    private async Task MetricsAsync(HttpContext context)
    {
        if (!_options.MetricsPublic && !(context.Connection.RemoteIpAddress is { } ip && Ncw.Protocol.Transport.LanTransport.IsLanAddress(ip)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Response.ContentType = "text/plain; version=0.0.4";
        await context.Response.WriteAsync(Metrics.Render()).ConfigureAwait(false);
    }

    // ---- 设备注册连接 -------------------------------------------------------------------------------------------

    private async Task RegisterDeviceAsync(HttpContext context, string deviceId, string remote)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        var ct = lifetime.Token;

        var nonce = RandomNumberGenerator.GetBytes(RelayProtocol.NonceBytes);
        await SendTextAsync(socket, new RelayMessage("challenge") { V = RelayProtocol.Version, Nonce = Convert.ToBase64String(nonce) }, ct).ConfigureAwait(false);

        var register = await ReceiveTextAsync(socket, RelayProtocol.MaxControlMessageBytes, _options.HandshakeTimeout, ct).ConfigureAwait(false);
        if (register is not { Type: "register", V: RelayProtocol.Version, PublicKey: { } publicKeyText, Signature: { } signatureText } ||
            !TryParseTokens(register.Tokens, out var tokens) ||
            !TryBase64(publicKeyText, 128, out var publicKey) || !TryBase64(signatureText, 128, out var signature))
        {
            await FailRegistrationAsync(socket, RelayErrors.BadRequest, remote, deviceId).ConfigureAwait(false);
            return;
        }

        if (!RelayProtocol.VerifyRegistration(publicKey, signature, nonce, deviceId))
        {
            _failuresPerIp.Record(remote);
            await FailRegistrationAsync(socket, RelayErrors.Unauthorized, remote, deviceId).ConfigureAwait(false);
            return;
        }

        var attached = Registry.Attach(deviceId, socket, _options.MaxDevices);
        if (attached is null)
        {
            await FailRegistrationAsync(socket, RelayErrors.Unavailable, remote, deviceId).ConfigureAwait(false);
            return;
        }

        var (entry, previous) = attached.Value;

        try
        {
            if (previous is not null)
            {
                Log.DeviceReplaced(deviceId);
                _ = Pipe.CloseAsync(previous, RelayErrors.Replaced);
            }

            entry.UpdateTokens(tokens, [], [], Metrics);
            await entry.ControlSend.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await SendTextAsync(socket, new RelayMessage("registered") { DeviceId = deviceId }, ct).ConfigureAwait(false);
            }
            finally
            {
                entry.ControlSend.Release();
            }

            Metrics.Registered();
            Log.DeviceRegistered(deviceId, remote, tokens.Length);
            await ControlLoopAsync(socket, entry, deviceId, ct).ConfigureAwait(false);
        }
        finally
        {
            var sessions = entry.SessionCount;
            Registry.Detach(entry, socket);
            Log.DeviceOffline(deviceId, sessions);
        }
    }

    private async Task FailRegistrationAsync(WebSocket socket, string reason, string remote, string deviceId)
    {
        Metrics.RegistrationFailed();
        Metrics.Reject(reason);
        Log.RegistrationFailed(reason, remote, deviceId);
        await Pipe.CloseAsync(socket, reason).ConfigureAwait(false);
    }

    private async Task ControlLoopAsync(WebSocket socket, DeviceEntry entry, string deviceId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await ReceiveTextAsync(socket, RelayProtocol.MaxControlMessageBytes, Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            if (message is null)
            {
                return; // 设备关闭了连接(或发了不合法的东西):下线
            }

            if (!_controlPerDevice.TryAcquire(deviceId))
            {
                Metrics.Reject(RelayErrors.RateLimited);
                await Pipe.CloseAsync(socket, RelayErrors.RateLimited).ConfigureAwait(false);
                return;
            }

            if (message is not { Type: "tokens" } ||
                !TryParseTokens(message.Set, out var set, allowNull: true) ||
                !TryParseTokens(message.Add, out var add, allowNull: true) ||
                !TryParseTokens(message.Remove, out var remove, allowNull: true))
            {
                await Pipe.CloseAsync(socket, RelayErrors.BadRequest).ConfigureAwait(false);
                return;
            }

            var revoked = entry.UpdateTokens(message.Set is null ? null : set, add, remove, Metrics);
            Log.TokensUpdated(deviceId, entry.TokenCount, revoked);
        }
    }

    private static bool TryParseTokens(string[]? hashes, out byte[][] tokens, bool allowNull = false)
    {
        tokens = [];
        if (hashes is null)
        {
            return allowNull;
        }

        if (hashes.Length > RelayProtocol.MaxTokensPerDevice)
        {
            return false;
        }

        var list = new byte[hashes.Length][];
        for (var i = 0; i < hashes.Length; i++)
        {
            var hash = hashes[i];
            if (hash is not { Length: 64 } || !hash.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }

            list[i] = Convert.FromHexString(hash);
        }

        tokens = list;
        return true;
    }

    private static bool TryBase64(string text, int maxBytes, out byte[] bytes)
    {
        bytes = new byte[maxBytes];
        if (text.Length > maxBytes * 2 || !Convert.TryFromBase64String(text, bytes, out var written))
        {
            return false;
        }

        bytes = bytes[..written];
        return true;
    }

    // ---- 控制端接入 ---------------------------------------------------------------------------------------------

    private async Task ConnectControllerAsync(HttpContext context, string deviceId, string remote)
    {
        if (_failuresPerDevice.IsExceeded(deviceId) || !_connectsPerDevice.TryAcquire(deviceId))
        {
            RateLimited(context, remote, deviceId);
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping.Token);
        var ct = lifetime.Token;

        var connect = await ReceiveTextAsync(socket, 4096, _options.HandshakeTimeout, ct).ConfigureAwait(false);
        if (connect is not { Type: "connect", V: RelayProtocol.Version, Token: { Length: > 0 and <= 128 } token } || !token.All(static c => c is > ' ' and < '\u007f'))
        {
            await RefuseControllerAsync(socket, RelayErrors.BadRequest, remote, deviceId).ConfigureAwait(false);
            return;
        }

        // 先问设备在不在线:不在线的设备中继手里没有它的令牌哈希,没法验令牌;「在不在线」本来也不是秘密(deviceId 是公钥哈希,在线与否从连接行为就看得出)。
        var entry = Registry.Find(deviceId);
        if (entry is null || !entry.IsOnline)
        {
            await RefuseControllerAsync(socket, RelayErrors.DeviceOffline, remote, deviceId).ConfigureAwait(false);
            return;
        }

        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(token));
        if (!entry.Matches(hash))
        {
            _failuresPerIp.Record(remote);
            _failuresPerDevice.Record(deviceId);
            await RefuseControllerAsync(socket, RelayErrors.Unauthorized, remote, deviceId).ConfigureAwait(false);
            return;
        }

        var session = entry.TryAddSession(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), Convert.ToHexStringLower(hash), remote, _options.MaxSessionsPerDevice);
        if (session is null)
        {
            await RefuseControllerAsync(socket, RelayErrors.Busy, remote, deviceId).ConfigureAwait(false);
            return;
        }

        Metrics.SessionPending(1);
        var piped = false;
        var started = _options.Clock.GetTimestamp();
        string reason = RelayErrors.Unavailable;
        Pipe.Result? result = null;
        try
        {
            using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, session.Cancel.Token);
            var device = await NotifyAndWaitAsync(entry, session, sessionLifetime.Token).ConfigureAwait(false);
            if (device is null)
            {
                reason = session.CancelReason ?? (ct.IsCancellationRequested ? RelayErrors.Unavailable : RelayErrors.Timeout);
                if (!ct.IsCancellationRequested || session.CancelReason is not null)
                {
                    Metrics.Reject(reason);
                    await Pipe.CloseAsync(socket, reason).ConfigureAwait(false);
                }

                return;
            }

            Metrics.SessionPending(-1);
            piped = true;
            Metrics.SessionStarted();
            Log.SessionStarted(deviceId, session.Id, remote);
            // 拼接阶段不跟着停机信号一起被取消(那会中止两边的连接):停机走 _draining 的优雅排空,硬取消只来自吊销 / 请求中止 / 排空兜底
            using var pipeLifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Cancel.Token);
            await SendTextAsync(socket, new RelayMessage("ready"), pipeLifetime.Token).ConfigureAwait(false);
            result = await Pipe.RunAsync(socket, device, deviceId, _options, Metrics, pipeLifetime.Token, _draining.Token).ConfigureAwait(false);
            reason = result.Reason == RelayErrors.Unavailable && session.CancelReason is not null ? session.CancelReason : result.Reason;
        }
        finally
        {
            if (!piped)
            {
                Metrics.SessionPending(-1);
            }
            else
            {
                Metrics.SessionEnded();
                Log.SessionEnded(deviceId, session.Id, reason, (long)_options.Clock.GetElapsedTime(started).TotalMilliseconds, result?.BytesToDevice ?? 0, result?.BytesToController ?? 0);
            }

            session.Finished.TrySetResult();
            session.DeviceArrived.TrySetCanceled();
            entry.RemoveSession(session.Id);
            Registry.Prune(entry);
        }
    }

    private async Task RefuseControllerAsync(WebSocket socket, string reason, string remote, string deviceId)
    {
        Metrics.Reject(reason);
        Log.Refused(RelayProtocol.ConnectPath, reason, remote, deviceId);
        await Pipe.CloseAsync(socket, reason).ConfigureAwait(false);
    }

    /// <summary>通知设备有新会话,等它的数据连接到达。设备不可达或超时返回 null。</summary>
    private async Task<WebSocket?> NotifyAndWaitAsync(DeviceEntry entry, RelaySession session, CancellationToken ct)
    {
        try
        {
            var control = entry.Control;
            if (control is null)
            {
                return null;
            }

            await entry.ControlSend.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var notice = new RelayMessage("session") { Session = session.Id, TokenHash = session.TokenHashHex };
                await SendTextAsync(control, notice, ct).ConfigureAwait(false);
            }
            finally
            {
                entry.ControlSend.Release();
            }

            return await session.DeviceArrived.Task.WaitAsync(_options.DeviceAcceptTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException or WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---- 设备数据连接 -------------------------------------------------------------------------------------------

    private async Task AttachDataAsync(HttpContext context, string deviceId, string remote)
    {
        var sessionId = context.Request.Query["s"].ToString();
        var session = Registry.Find(deviceId)?.FindSession(sessionId);
        if (session is null || sessionId.Length != 32)
        {
            // 会话 id 是中继生成的一次性随机数,只通过设备的注册连接告诉设备:猜不到,猜错记一次失败。
            _failuresPerIp.Record(remote);
            Refuse(context, StatusCodes.Status403Forbidden, RelayErrors.Unauthorized, remote, deviceId);
            return;
        }

        if (Interlocked.CompareExchange(ref session.Piping, 1, 0) != 0)
        {
            Refuse(context, StatusCodes.Status409Conflict, RelayErrors.Unauthorized, remote, deviceId); // 同一个会话 id 只能接一次
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        if (!session.DeviceArrived.TrySetResult(socket))
        {
            await Pipe.CloseAsync(socket, RelayErrors.Timeout).ConfigureAwait(false); // 控制端已经放弃
            return;
        }

        // 拼接由控制端那边的处理器运行;这里等它结束(返回后 ASP.NET 会关掉这条连接)。
        // 不跟着停机信号返回:停机时拼接要先走完优雅排空(关闭握手),这条连接此刻被关掉就等于中止它。
        try
        {
            await session.Finished.Task.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 设备断开了:拼接那边的读会失败并收尾。
        }
    }

    // ---- 小工具 -------------------------------------------------------------------------------------------------

    private static async Task SendTextAsync(WebSocket socket, RelayMessage message, CancellationToken ct) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(RelayProtocol.Serialize(message)), WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);

    /// <summary>
    /// 读一条文本消息(<paramref name="maxBytes"/> 上限,<paramref name="timeout"/> 内)。超时、关闭、收到二进制、过大或不是合法 JSON 都返回 null。
    /// 超时<b>不取消</b>正在进行的读:取消一次读会让 WebSocket 直接中止,之后就发不出 <c>error</c> 消息了;
    /// 这次读留着,调用方发完错误、关闭连接之后它自然结束(异常在这里观察掉)。
    /// </summary>
    private static async Task<RelayMessage?> ReceiveTextAsync(WebSocket socket, int maxBytes, TimeSpan timeout, CancellationToken ct)
    {
        var buffer = new byte[maxBytes];
        var length = 0;
        var deadline = timeout == Timeout.InfiniteTimeSpan ? long.MaxValue : Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (true)
        {
            var receive = socket.ReceiveAsync(buffer.AsMemory(length), ct).AsTask();
            ValueWebSocketReceiveResult result;
            try
            {
                result = deadline == long.MaxValue
                    ? await receive.ConfigureAwait(false)
                    : await receive.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64))).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _ = receive.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                return null;
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                return null;
            }

            length += result.Count;
            if (result.EndOfMessage)
            {
                return RelayProtocol.Deserialize(buffer.AsSpan(0, length));
            }

            if (length >= maxBytes)
            {
                return null;
            }
        }
    }
}

