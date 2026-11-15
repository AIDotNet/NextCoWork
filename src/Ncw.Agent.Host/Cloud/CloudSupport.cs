using Ncw.Agent.Cloud;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Host.Cloud;

/// <summary><see cref="ICloudStore"/> 的数据库实现(kv 表,键都以 <c>v3.cloud.</c> 开头)。kv 表没有删除接口:删除 = 写空串,读到空串当作没有。</summary>
internal sealed class DbCloudStore(KvStore kv) : ICloudStore
{
    public string? Get(string key) => kv.Get(key) is { Length: > 0 } value ? value : null;

    public void Set(string key, string value) => kv.Set(key, value);

    public void Remove(string key) => kv.Set(key, "");
}

/// <summary>
/// 量数据根 + 工作区占用的磁盘字节数(配额「存储」用)。遍历在后台线程进行,<see cref="Bytes"/> 永远是「最近一次量到的值」(还没量过为 null),
/// 所以配额检查和 <c>cloud.getStatus</c> 不会因为遍历大目录而卡住。符号链接不跟随(避免循环和量到沙箱之外)。
/// </summary>
internal sealed class StorageMeter(IReadOnlyList<string> roots, TimeProvider clock, TimeSpan? maxAge = null)
{
    private readonly TimeSpan _maxAge = maxAge ?? TimeSpan.FromMinutes(10);
    private long _bytes = -1;
    private long _measuredAt;
    private int _running;

    public long? Bytes => Interlocked.Read(ref _bytes) is var value and >= 0 ? value : null;

    /// <summary>数据过期(或从没量过)就在后台量一次。</summary>
    public void RefreshIfStale()
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (Interlocked.Read(ref _bytes) >= 0 && now - Interlocked.Read(ref _measuredAt) < (long)_maxAge.TotalMilliseconds)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Interlocked.Exchange(ref _bytes, Measure());
                Interlocked.Exchange(ref _measuredAt, clock.GetUtcNow().ToUnixTimeMilliseconds());
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private long Measure()
    {
        long total = 0;
        foreach (var root in roots.Distinct(StringComparer.Ordinal))
        {
            total += Walk(root);
        }

        return total;
    }

    private static long Walk(string root)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                {
                    if (entry.LinkTarget is not null)
                    {
                        continue;
                    }

                    if (entry is DirectoryInfo)
                    {
                        pending.Push(entry.FullName);
                    }
                    else if (entry is FileInfo file)
                    {
                        total += file.Length;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 读不到的目录不计(权限 / 并发删除)
            }
        }

        return total;
    }
}

/// <summary><see cref="IUsageSource"/> 的 Host 实现:用量表、Run 注册表、磁盘量。</summary>
internal sealed class CloudUsageSource(UsageRepository usage, RunRegistry registry, StorageMeter storage) : IUsageSource
{
    public long TokensBetween(long fromMs, long toMs)
    {
        var s = usage.Summary(new UsageWindow(fromMs, toMs + 1)); // 窗口不含上界
        return s.InputTokens + s.OutputTokens + s.CacheReadTokens + s.CacheWriteTokens;
    }

    public (long Input, long Output, long Cache) TokenTotals()
    {
        var s = usage.Summary(new UsageWindow(null, long.MaxValue));
        return (s.InputTokens, s.OutputTokens, s.CacheReadTokens + s.CacheWriteTokens);
    }

    public int ActiveRuns() => registry.ActiveRuns().Runs.Length;

    public long? StorageBytes()
    {
        storage.RefreshIfStale();
        return storage.Bytes;
    }
}
