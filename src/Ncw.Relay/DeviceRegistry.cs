using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Ncw.Protocol.Transport;

namespace Ncw.Relay;

/// <summary>
/// 一次控制端会话(从令牌验过、通知设备开始,到拼接结束)。设备的数据连接到达后 <see cref="DeviceArrived"/> 完成;
/// 掐断(吊销、空闲、关闭)取消 <see cref="Cancel"/>。
/// </summary>
internal sealed class RelaySession(string id, string tokenHashHex, string remote)
{
    public string Id { get; } = id;

    /// <summary>控制端出示的令牌的哈希(十六进制)。设备注册连接上推来的令牌集合不再包含它时,会话被掐断。</summary>
    public string TokenHashHex { get; } = tokenHashHex;

    public string Remote { get; } = remote;

    public TaskCompletionSource<WebSocket> DeviceArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>设备数据连接的处理器等它:拼接结束后才能返回(返回后 ASP.NET 就会关掉那条连接)。</summary>
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationTokenSource Cancel { get; } = new();

    public string? CancelReason { get; set; }

    public int Piping;
}

/// <summary>一台已注册的设备:注册连接、当前有效的路由令牌哈希、进行中的会话。</summary>
internal sealed class DeviceEntry(string deviceId)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, RelaySession> _sessions = new(StringComparer.Ordinal);
    private byte[][] _tokens = [];

    public string DeviceId { get; } = deviceId;

    /// <summary>当前的注册连接;离线为 null。</summary>
    public WebSocket? Control { get; private set; }

    public SemaphoreSlim ControlSend { get; } = new(1, 1);

    public int SessionCount
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public bool IsOnline
    {
        get
        {
            lock (_gate)
            {
                return Control is not null;
            }
        }
    }

    public int TokenCount
    {
        get
        {
            lock (_gate)
            {
                return _tokens.Length;
            }
        }
    }

    /// <summary>换上新的注册连接(设备重连时),返回被顶掉的旧连接。</summary>
    public WebSocket? Attach(WebSocket control)
    {
        lock (_gate)
        {
            var previous = Control;
            Control = control;
            return previous;
        }
    }

    /// <summary>注册连接结束;只有它仍是当前连接时才清掉(被顶掉的旧连接结束不影响新连接)。返回设备是否因此离线。</summary>
    public bool Detach(WebSocket control)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(Control, control))
            {
                return false;
            }

            Control = null;
            return true;
        }
    }

    /// <summary>设备已离线且没有会话,可以从目录里删掉。</summary>
    public bool IsIdle
    {
        get
        {
            lock (_gate)
            {
                return Control is null && _sessions.Count == 0;
            }
        }
    }

    /// <summary>
    /// 令牌哈希是否在集合里。逐个常数时间比较并且<b>不提前返回</b>:耗时只和集合大小有关,不泄露命中的位置。
    /// 令牌是 256 位随机数,哈希也是单向的;这里的常数时间只是不留任何旁路。
    /// </summary>
    public bool Matches(ReadOnlySpan<byte> tokenHash)
    {
        byte[][] tokens;
        lock (_gate)
        {
            tokens = _tokens;
        }

        var hit = false;
        foreach (var candidate in tokens)
        {
            hit |= CryptographicOperations.FixedTimeEquals(candidate, tokenHash);
        }

        return hit;
    }

    /// <summary>
    /// 更新令牌集合:<paramref name="set"/> 非空时整体替换,否则先加 <paramref name="add"/> 再去掉 <paramref name="remove"/>。
    /// 返回被掐断的会话数(它们的令牌不在新集合里)。
    /// </summary>
    public int UpdateTokens(byte[][]? set, byte[][] add, byte[][] remove, RelayMetrics metrics)
    {
        List<RelaySession> revoked = [];
        lock (_gate)
        {
            var next = set is not null
                ? [.. set]
                : _tokens.ToList();
            foreach (var token in add)
            {
                if (!next.Any(t => t.AsSpan().SequenceEqual(token)))
                {
                    next.Add(token);
                }
            }

            next.RemoveAll(t => remove.Any(r => r.AsSpan().SequenceEqual(t)));
            _tokens = [.. next];
            foreach (var session in _sessions.Values)
            {
                var hash = Convert.FromHexString(session.TokenHashHex);
                if (!_tokens.Any(t => CryptographicOperations.FixedTimeEquals(t, hash)))
                {
                    revoked.Add(session);
                }
            }
        }

        foreach (var session in revoked)
        {
            session.CancelReason = RelayErrors.Revoked;
            metrics.ClosedByRevocation();
            session.Cancel.Cancel();
        }

        return revoked.Count;
    }

    /// <summary>占一个会话名额;满了返回 null。</summary>
    public RelaySession? TryAddSession(string id, string tokenHashHex, string remote, int max)
    {
        lock (_gate)
        {
            if (_sessions.Count >= max)
            {
                return null;
            }

            var session = new RelaySession(id, tokenHashHex, remote);
            _sessions[id] = session;
            return session;
        }
    }

    public RelaySession? FindSession(string id)
    {
        lock (_gate)
        {
            return _sessions.GetValueOrDefault(id);
        }
    }

    public void RemoveSession(string id)
    {
        lock (_gate)
        {
            _sessions.Remove(id);
        }
    }

    /// <summary>
    /// 关闭中继时掐断会话。<paramref name="includePiped"/> 为假时跳过已经拼接的(设备数据连接已到):它们走优雅排空,不能被硬取消。
    /// </summary>
    public void CancelAll(string reason, bool includePiped = true)
    {
        RelaySession[] sessions;
        lock (_gate)
        {
            sessions = [.. _sessions.Values];
        }

        foreach (var session in sessions)
        {
            if (!includePiped && Volatile.Read(ref session.Piping) == 1)
            {
                continue;
            }

            session.CancelReason = reason;
            session.Cancel.Cancel();
        }
    }
}

/// <summary>
/// 内存里的设备目录(中继唯一的状态,重启即清空——设备会自动重新注册)。
/// 注册、离线清理走一把锁(都是低频操作);查找是无锁读。
/// </summary>
internal sealed class DeviceRegistry(RelayMetrics metrics)
{
    private readonly ConcurrentDictionary<string, DeviceEntry> _devices = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public int Count => _devices.Count;

    public int Online => _devices.Values.Count(d => d.IsOnline);

    public DeviceEntry? Find(string deviceId) => _devices.GetValueOrDefault(deviceId);

    /// <summary>登记(或重新登记)设备的注册连接。目录满了返回 null。返回的第二项是被顶掉的旧连接。</summary>
    public (DeviceEntry Entry, WebSocket? Replaced)? Attach(string deviceId, WebSocket control, int maxDevices)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(deviceId, out var entry))
            {
                if (_devices.Count >= maxDevices)
                {
                    return null;
                }

                entry = new DeviceEntry(deviceId);
                _devices[deviceId] = entry;
            }

            var previous = entry.Attach(control);
            if (previous is null)
            {
                metrics.DeviceOnline(1);
            }

            return (entry, previous);
        }
    }

    public void Detach(DeviceEntry entry, WebSocket control)
    {
        lock (_gate)
        {
            if (entry.Detach(control))
            {
                metrics.DeviceOnline(-1);
            }

            Prune(entry);
        }
    }

    /// <summary>设备离线又没有会话了:从目录里删掉。</summary>
    public void Prune(DeviceEntry entry)
    {
        lock (_gate)
        {
            if (entry.IsIdle)
            {
                _devices.TryRemove(new KeyValuePair<string, DeviceEntry>(entry.DeviceId, entry));
            }
        }
    }

    public void CancelAll(string reason, bool includePiped = true)
    {
        foreach (var entry in _devices.Values)
        {
            entry.CancelAll(reason, includePiped);
        }
    }
}
