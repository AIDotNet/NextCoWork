using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Ncw.Relay;

/// <summary>
/// 运行指标(Prometheus 文本格式,<c>/metrics</c>)。只有计数和量表,<b>没有</b>令牌、deviceId、来源 IP 或任何内容——
/// 标签只有固定的拒绝原因。线程安全。
/// </summary>
public sealed class RelayMetrics
{
    private readonly ConcurrentDictionary<string, long> _rejections = new(StringComparer.Ordinal);
    private long _registrations;
    private long _registrationFailures;
    private long _sessionsStarted;
    private long _sessionsEnded;
    private long _bytesToDevice;
    private long _bytesToController;
    private long _devicesOnline;
    private long _sessionsActive;
    private long _sessionsPending;
    private long _connections;
    private long _revocationsClosed;
    private long _idleClosed;

    public long Registrations => Interlocked.Read(ref _registrations);

    public long RegistrationFailures => Interlocked.Read(ref _registrationFailures);

    public long SessionsStarted => Interlocked.Read(ref _sessionsStarted);

    public long SessionsEnded => Interlocked.Read(ref _sessionsEnded);

    public long BytesToDevice => Interlocked.Read(ref _bytesToDevice);

    public long BytesToController => Interlocked.Read(ref _bytesToController);

    public long DevicesOnline => Interlocked.Read(ref _devicesOnline);

    public long SessionsActive => Interlocked.Read(ref _sessionsActive);

    public long Connections => Interlocked.Read(ref _connections);

    public long IdleClosed => Interlocked.Read(ref _idleClosed);

    public long RevocationsClosed => Interlocked.Read(ref _revocationsClosed);

    /// <summary>某个原因(<c>Ncw.Protocol</c> 的 <c>RelayErrors</c>)被拒绝的次数。</summary>
    public long Rejected(string reason) => _rejections.GetValueOrDefault(reason);

    internal void Registered() => Interlocked.Increment(ref _registrations);

    internal void RegistrationFailed() => Interlocked.Increment(ref _registrationFailures);

    internal void DeviceOnline(int delta) => Interlocked.Add(ref _devicesOnline, delta);

    internal void SessionPending(int delta) => Interlocked.Add(ref _sessionsPending, delta);

    internal void SessionStarted()
    {
        Interlocked.Increment(ref _sessionsStarted);
        Interlocked.Increment(ref _sessionsActive);
    }

    internal void SessionEnded()
    {
        Interlocked.Increment(ref _sessionsEnded);
        Interlocked.Decrement(ref _sessionsActive);
    }

    internal void Connection(int delta) => Interlocked.Add(ref _connections, delta);

    internal void Bytes(bool towardDevice, int count) => Interlocked.Add(ref towardDevice ? ref _bytesToDevice : ref _bytesToController, count);

    internal void Reject(string reason) => _rejections.AddOrUpdate(reason, 1, static (_, n) => n + 1);

    internal void ClosedByRevocation() => Interlocked.Increment(ref _revocationsClosed);

    internal void ClosedByIdle() => Interlocked.Increment(ref _idleClosed);

    public string Render()
    {
        var sb = new StringBuilder();
        Gauge(sb, "ncw_relay_devices_online", "Devices currently registered", DevicesOnline);
        Gauge(sb, "ncw_relay_sessions_active", "Controller sessions currently piped", SessionsActive);
        Gauge(sb, "ncw_relay_sessions_pending", "Controller sessions waiting for the device to attach", Interlocked.Read(ref _sessionsPending));
        Gauge(sb, "ncw_relay_connections", "WebSocket connections currently open", Connections);
        Counter(sb, "ncw_relay_registrations_total", "Successful device registrations", Registrations);
        Counter(sb, "ncw_relay_registration_failures_total", "Failed device registrations", RegistrationFailures);
        Counter(sb, "ncw_relay_sessions_total", "Controller sessions piped", SessionsStarted);
        Counter(sb, "ncw_relay_sessions_ended_total", "Controller sessions ended", SessionsEnded);
        Counter(sb, "ncw_relay_bytes_to_device_total", "Bytes forwarded controller to device", BytesToDevice);
        Counter(sb, "ncw_relay_bytes_to_controller_total", "Bytes forwarded device to controller", BytesToController);
        Counter(sb, "ncw_relay_idle_closed_total", "Sessions closed for idling", IdleClosed);
        Counter(sb, "ncw_relay_revocation_closed_total", "Sessions closed because their token was revoked", RevocationsClosed);
        sb.Append("# HELP ncw_relay_rejected_total Refused attempts by reason\n# TYPE ncw_relay_rejected_total counter\n");
        foreach (var (reason, count) in _rejections.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            sb.Append(CultureInfo.InvariantCulture, $"ncw_relay_rejected_total{{reason=\"{reason}\"}} {count}\n");
        }

        return sb.ToString();
    }

    private static void Gauge(StringBuilder sb, string name, string help, long value) =>
        sb.Append(CultureInfo.InvariantCulture, $"# HELP {name} {help}\n# TYPE {name} gauge\n{name} {value}\n");

    private static void Counter(StringBuilder sb, string name, string help, long value) =>
        sb.Append(CultureInfo.InvariantCulture, $"# HELP {name} {help}\n# TYPE {name} counter\n{name} {value}\n");
}
