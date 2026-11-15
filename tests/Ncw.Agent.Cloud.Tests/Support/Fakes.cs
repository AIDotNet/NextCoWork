using System.Security.Cryptography;
using Ncw.Agent.Cloud;
using Ncw.Agent.Storage.Credentials;
using Ncw.Protocol.Orchestration;

namespace Ncw.Agent.Cloud.Tests.Support;

/// <summary>手动推进的时钟:定时器只在 <see cref="Advance"/> 时触发(按到期顺序,周期定时器重复触发)。</summary>
public sealed class ManualClock(DateTimeOffset origin) : TimeProvider
{
    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public long Due = long.MaxValue;
        public long Period = -1;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._now + (long)dueTime.TotalMilliseconds;
                Period = period == Timeout.InfiniteTimeSpan ? -1 : (long)period.TotalMilliseconds;
                return true;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return origin.AddMilliseconds(_now);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>推进时间,依次触发到期的定时器。</summary>
    public void Advance(TimeSpan by)
    {
        var target = Interlocked.Read(ref _now) + (long)by.TotalMilliseconds;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = Math.Max(_now, next.Due);
                next.Due = next.Period > 0 ? next.Due + next.Period : long.MaxValue;
            }

            next.Fire();
        }
    }
}

/// <summary>内存里的云端状态存储。</summary>
public sealed class MemoryCloudStore : ICloudStore
{
    private readonly Dictionary<string, string> _values = [];

    public string? Get(string key)
    {
        lock (_values)
        {
            return _values.TryGetValue(key, out var v) ? v : null;
        }
    }

    public void Set(string key, string value)
    {
        lock (_values)
        {
            _values[key] = value;
        }
    }

    public void Remove(string key)
    {
        lock (_values)
        {
            _values.Remove(key);
        }
    }
}

/// <summary>可编程的用量来源。</summary>
public sealed class FakeUsage : IUsageSource
{
    public long Tokens { get; set; }

    public int Active { get; set; }

    public long? Storage { get; set; }

    public long TokensBetween(long fromMs, long toMs) => Tokens;

    public (long Input, long Output, long Cache) TokenTotals() => (Tokens, 0, 0);

    public int ActiveRuns() => Active;

    public long? StorageBytes() => Storage;
}

/// <summary>假 KMS:AES-256-GCM 包裹,加密上下文作 AAD(和真实 KMS 的 EncryptionContext 同一个语义);可以注入「暂时连不上」和「拒绝」。</summary>
public sealed class FakeKms(string keyId = "kms-key-1") : IKeyWrap
{
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public string KeyId => keyId;

    public int Wraps;
    public int Unwraps;

    /// <summary>true = 模拟网络不通(抛 <see cref="HttpRequestException"/>)。</summary>
    public bool Down { get; set; }

    /// <summary>true = KMS 拒绝解开(密钥被禁用)。</summary>
    public bool Deny { get; set; }

    public ValueTask<byte[]> WrapAsync(ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> context, CancellationToken ct)
    {
        if (Down)
        {
            throw new HttpRequestException("kms unreachable");
        }

        Interlocked.Increment(ref Wraps);
        var blob = new byte[12 + plaintext.Length + 16];
        RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(blob.AsSpan(0, 12), plaintext.Span, blob.AsSpan(12, plaintext.Length), blob.AsSpan(blob.Length - 16), context.Span);
        return ValueTask.FromResult(blob);
    }

    public ValueTask<byte[]> UnwrapAsync(ReadOnlyMemory<byte> wrapped, string wrappedKeyId, ReadOnlyMemory<byte> context, CancellationToken ct)
    {
        if (Down)
        {
            throw new HttpRequestException("kms unreachable");
        }

        if (Deny)
        {
            throw new KeyWrapException("denied");
        }

        Interlocked.Increment(ref Unwraps);
        var blob = wrapped.Span;
        var plain = new byte[blob.Length - 28];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(blob[..12], blob[12..^16], blob[^16..], plain, context.Span);
        }
        catch (CryptographicException e)
        {
            throw new KeyWrapException("wrong key or context", e);
        }

        return ValueTask.FromResult(plain);
    }
}

/// <summary>签发指令的小工具(编排服务一侧)。</summary>
public sealed class Issuer
{
    public Issuer(string kid = "orch-1")
    {
        Kid = kid;
        var (seed, pub) = EnvelopeSigning.GenerateKeyPair();
        Seed = seed;
        PublicKey = pub;
        Signer = new CommandSigner(kid, seed);
    }

    public string Kid { get; }

    public string Seed { get; }

    public string PublicKey { get; }

    public CommandSigner Signer { get; }

    public OrchestratorKeys Keys => new(new Dictionary<string, byte[]> { [Kid] = Convert.FromBase64String(PublicKey) });

    public CommandBody Body(string type, string deviceId, long seq, DateTimeOffset now, TimeSpan? lifetime = null) =>
        new(type) { DeviceId = deviceId, Seq = seq, IssuedAt = now.ToUnixTimeMilliseconds(), ExpiresAt = (now + (lifetime ?? TimeSpan.FromMinutes(10))).ToUnixTimeMilliseconds() };
}
