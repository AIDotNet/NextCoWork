using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Ncw.Protocol.Security;
using Ncw.Protocol.Transport;

namespace Ncw.Relay.Tests;

/// <summary>可以手动拨动的时钟。</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = 1_000_000;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks + _ticks, TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
}

/// <summary>收集日志(消息 + 结构化参数),断言里不能出现令牌。</summary>
internal sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines.ToArray();

    public string All => string.Join('\n', _lines);

    public ILogger CreateLogger(string categoryName) => new Capture(this);

    public void Dispose()
    {
    }

    private sealed class Capture(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = formatter(state, exception);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                text += " | " + string.Join(' ', pairs.Select(p => $"{p.Key}={p.Value}"));
            }

            owner._lines.Enqueue($"{logLevel} {text} {exception}");
        }
    }
}

/// <summary>记录中继转发的全部字节(只给测试)。</summary>
internal sealed class TrafficRecorder
{
    private readonly object _gate = new();
    private readonly List<byte> _toDevice = [];
    private readonly List<byte> _toController = [];

    public RelayTap Tap => (_, towardDevice, bytes) =>
    {
        lock (_gate)
        {
            (towardDevice ? _toDevice : _toController).AddRange(bytes.ToArray());
        }
    };

    public byte[] All
    {
        get
        {
            lock (_gate)
            {
                return [.. _toDevice, .. _toController];
            }
        }
    }

    public bool Contains(ReadOnlySpan<byte> needle) => All.AsSpan().IndexOf(needle) >= 0;
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > until)
            {
                throw new TimeoutException(what);
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>起一个进程内的中继。</summary>
internal static class TestRelay
{
    public static Task<RelayServer> StartAsync(RelayOptions? configure = null, LogCapture? log = null, int port = 0, Func<RelayOptions, RelayOptions>? tweak = null)
    {
        var o = configure ?? new RelayOptions();
        o = new RelayOptions
        {
            Urls = [$"http://127.0.0.1:{port}"],
            MaxMessageBytes = o.MaxMessageBytes,
            HandshakeTimeout = o.HandshakeTimeout,
            DeviceAcceptTimeout = o.DeviceAcceptTimeout,
            IdleTimeout = o.IdleTimeout,
            MaxSessionsPerDevice = o.MaxSessionsPerDevice,
            MaxDevices = o.MaxDevices,
            MaxConnectionsPerIp = o.MaxConnectionsPerIp,
            ConnectsPerIp = o.ConnectsPerIp,
            RegistersPerIp = o.RegistersPerIp,
            FailuresPerIp = o.FailuresPerIp,
            FailuresPerDevice = o.FailuresPerDevice,
            ConnectsPerDevice = o.ConnectsPerDevice,
            ControlMessagesPerDevice = o.ControlMessagesPerDevice,
            MaxBytesPerSecondPerSession = o.MaxBytesPerSecondPerSession,
            KeepAliveInterval = o.KeepAliveInterval,
            KeepAliveTimeout = o.KeepAliveTimeout,
            Placement = o.Placement,
            Clock = o.Clock,
            Tap = o.Tap,
            ConfigureLogging = log is null ? null : logging => logging.AddProvider(log),
        };
        return RelayServer.StartAsync(tweak?.Invoke(o) ?? o);
    }
}

/// <summary>一台假设备:真实的 <see cref="RelayRegistration"/>,收到会话后跑给定的处理(默认原样回显)。</summary>
internal sealed class FakeDevice : IAsyncDisposable
{
    private readonly RelayRegistration _registration;
    private int _sessions;
    private int _active;

    public FakeDevice(Uri relay, PeerIdentity? identity = null, IReadOnlyCollection<string>? tokenHashes = null, Func<RelayAcceptedSession, Task>? onSession = null)
    {
        Identity = identity ?? PeerIdentity.Create("fake-device");
        _registration = new RelayRegistration(relay, Identity, tokenHashes ?? [], async s =>
        {
            Interlocked.Increment(ref _sessions);
            Interlocked.Increment(ref _active);
            try
            {
                await (onSession ?? Echo)(s).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }, new RelayClientOptions(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(400)));
    }

    public PeerIdentity Identity { get; }

    public string DeviceId => Identity.DeviceId;

    public RelayStatus Status => _registration.Status;

    public int SessionsAccepted => Volatile.Read(ref _sessions);

    public int ActiveSessions => Volatile.Read(ref _active);

    public void Start() => _registration.Start();

    public void SetTokens(params string[] tokenHashes) => _registration.SetTokens(tokenHashes);

    public static async Task Echo(RelayAcceptedSession session)
    {
        await using var stream = session.Stream;
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var n = await stream.ReadAsync(buffer).ConfigureAwait(false);
            if (n == 0)
            {
                return;
            }

            await stream.WriteAsync(buffer.AsMemory(0, n)).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => _registration.DisposeAsync();
}

internal static class Tokens
{
    public static string New() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => RelayProtocol.HashToken(token);
}

/// <summary>手写的设备注册客户端:测试协议层的各种违规(假签名、畸形消息……)。</summary>
internal sealed class RawDeviceConnection : IAsyncDisposable
{
    public ClientWebSocket Socket { get; } = new();

    public static async Task<RawDeviceConnection> OpenAsync(Uri relay, string deviceId)
    {
        var raw = new RawDeviceConnection();
        await raw.Socket.ConnectAsync(new Uri($"{relay.ToString().TrimEnd('/')}{RelayProtocol.DevicePath}?d={deviceId}"), CancellationToken.None);
        return raw;
    }

    public async Task<RelayMessage?> ReceiveAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[RelayProtocol.MaxControlMessageBytes];
        var length = 0;
        while (true)
        {
            var result = await Socket.ReceiveAsync(buffer.AsMemory(length), cts.Token);
            if (result.MessageType != WebSocketMessageType.Text)
            {
                return null;
            }

            length += result.Count;
            if (result.EndOfMessage)
            {
                return RelayProtocol.Deserialize(buffer.AsSpan(0, length));
            }
        }
    }

    public Task SendAsync(RelayMessage message) =>
        Socket.SendAsync(Encoding.UTF8.GetBytes(RelayProtocol.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        Socket.Abort();
        Socket.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>手写的控制端接入客户端:能在「就绪」之后发畸形消息(过大、文本)。</summary>
internal static class RawController
{
    public static async Task<(ClientWebSocket Socket, RelayMessage? First)> OpenAsync(Uri relay, string deviceId, string token)
    {
        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"{relay.ToString().TrimEnd('/')}{RelayProtocol.ConnectPath}?d={deviceId}"), CancellationToken.None);
        var connect = RelayProtocol.Serialize(new RelayMessage("connect") { V = RelayProtocol.Version, Token = token });
        await socket.SendAsync(Encoding.UTF8.GetBytes(connect), WebSocketMessageType.Text, true, CancellationToken.None);
        using var cts = new CancellationTokenSource(10_000);
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, cts.Token);
        return (socket, result.MessageType == WebSocketMessageType.Text ? RelayProtocol.Deserialize(buffer.AsSpan(0, result.Count)) : null);
    }
}
