using System.Collections.Concurrent;
using System.Globalization;
using Ncw.Client.Actions;

namespace Ncw.Client.Services;

/// <summary>一张图片的字节与 MIME 类型。</summary>
public sealed record ImageBytes(byte[] Data, string Mime);

/// <summary>
/// 把消息里的图片引用变成字节(给缩略图和灯箱用)。引用有两种:<c>data:</c> URL(内联)和 <c>ncw://</c> 附件引用(要向设备分块读取,
/// 远程控制端没有设备的文件系统)。失败返回 null(设备没连上 / 附件已被回收 / 引用不认识),界面显示「无法加载」并提供重试。
/// </summary>
public interface IImageSource
{
    Task<ImageBytes?> LoadAsync(string deviceId, string dataRef, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IImageSource"/> 的真实现。成功的结果按 <c>(设备, 引用)</c> 缓存(最多 <see cref="MaxCachedImages"/> 张,最久没用的先丢),
/// 滚动出又滚回来不重复拉;失败不缓存。单张上限 <see cref="MaxBytes"/>(与附件上限一致),超过当失败。
/// </summary>
public sealed class AttachmentImageSource(AttachmentActions attachments) : IImageSource
{
    public const int MaxCachedImages = 32;

    public const long MaxBytes = 32L * 1024 * 1024;

    private readonly ConcurrentDictionary<(string DeviceId, string Ref), (Task<ImageBytes?> Load, long Stamp)> _cache = [];
    private long _clock;

    public async Task<ImageBytes?> LoadAsync(string deviceId, string dataRef, CancellationToken ct = default)
    {
        var key = (deviceId, dataRef);
        if (_cache.TryGetValue(key, out var hit))
        {
            _cache[key] = (hit.Load, Interlocked.Increment(ref _clock));
            var shared = await hit.Load.WaitAsync(ct).ConfigureAwait(false);
            if (shared is not null)
            {
                return shared;
            }
        }

        var load = FetchAsync(deviceId, dataRef, CancellationToken.None);
        _cache[key] = (load, Interlocked.Increment(ref _clock));
        var result = await load.WaitAsync(ct).ConfigureAwait(false);
        if (result is null)
        {
            _cache.TryRemove(key, out _); // 失败不缓存:下次重试要真的再试一次
        }
        else
        {
            Trim();
        }

        return result;
    }

    private void Trim()
    {
        while (_cache.Count > MaxCachedImages && _cache.OrderBy(kv => kv.Value.Stamp).FirstOrDefault() is { Key: var oldest })
        {
            if (!_cache.TryRemove(oldest, out _))
            {
                break;
            }
        }
    }

    private async Task<ImageBytes?> FetchAsync(string deviceId, string dataRef, CancellationToken ct)
    {
        if (DecodeDataUrl(dataRef) is { } inline)
        {
            return inline;
        }

        if (!dataRef.StartsWith("ncw://", StringComparison.Ordinal))
        {
            return null; // 不认识的引用(file:// / http://):不去取,界面显示无法加载
        }

        using var buffer = new LimitedStream(MaxBytes);
        try
        {
            var mime = await attachments.DownloadAsync(deviceId, dataRef, buffer, ct).ConfigureAwait(false);
            return mime.IsOk && mime.Value is { } type && buffer.Length > 0 ? new ImageBytes(buffer.ToArray(), type) : null;
        }
        catch (InvalidOperationException)
        {
            return null; // 超过上限
        }
    }

    /// <summary><c>data:image/png;base64,…</c> → 字节。只认 base64 的 <c>image/*</c>;超过上限、编码不对都返回 null。</summary>
    public static ImageBytes? DecodeDataUrl(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = value.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        var header = value[5..comma].Split(';');
        if (header.Length < 2 || !header[0].StartsWith("image/", StringComparison.OrdinalIgnoreCase) || !header.Contains("base64", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var payload = value.AsSpan(comma + 1);
        if (payload.Length / 4L * 3 > MaxBytes)
        {
            return null;
        }

        var bytes = new byte[payload.Length / 4 * 3 + 3];
        return Convert.TryFromBase64Chars(payload, bytes, out var written) && written > 0
            ? new ImageBytes(bytes[..written], header[0].ToLower(CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>写满上限就抛 <see cref="InvalidOperationException"/> 的内存流(附件按块读,不能无限涨)。</summary>
    private sealed class LimitedStream(long limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void Check(int count)
        {
            if (Length + count > limit)
            {
                throw new InvalidOperationException("The image is larger than the attachment limit.");
            }
        }
    }
}
