using System.Runtime.CompilerServices;

namespace Ncw.Agent.Environments;

/// <summary>
/// 轮询式目录监视(06 §3「文件监视」):SFTP 没有变更通知,改为对打开的目录定时扫描、与上一次快照比较,
/// 并在执行工具后由调用方 <see cref="Refresh"/> 立即扫一次。只依赖 <see cref="IEnvironmentFs"/>,所以任何没有原生通知的环境都能用。
/// 扫描时连接中断等临时错误只跳过这一轮(环境可能正在重连);目录本身消失时报告它和快照里的所有条目被删除。
/// </summary>
internal sealed class PollingFsWatcher : IFsWatcher
{
    private readonly IEnvironmentFs _fs;
    private readonly EnvPath _directory;
    private readonly FsWatchOptions _options;
    private readonly FsChangeBatcher _batcher = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _refresh = new(0);
    private readonly Task _loop;

    public PollingFsWatcher(IEnvironmentFs fs, EnvPath directory, FsWatchOptions options)
    {
        _fs = fs;
        _directory = directory;
        _options = options;
        _loop = Task.Run(RunAsync);
    }

    public IAsyncEnumerable<IReadOnlyList<FsChange>> Batches => Read();

    public void Refresh()
    {
        if (_refresh.CurrentCount == 0)
        {
            _refresh.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _batcher.Complete();
        _stop.Dispose();
        _refresh.Dispose();
    }

    private async IAsyncEnumerable<IReadOnlyList<FsChange>> Read([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var batch in _batcher.ReadBatchesAsync(_options.Debounce, ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        Dictionary<string, Entry>? previous = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var current = await TryScanAsync(ct).ConfigureAwait(false);
                if (current is not null)
                {
                    if (previous is not null)
                    {
                        Diff(previous, current);
                    }

                    previous = current;
                }

                try
                {
                    await _refresh.WaitAsync(_options.PollInterval, ct).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 释放
        }
    }

    /// <summary>扫描一次。临时错误返回 <c>null</c>(保持上一次快照不变);目录不存在返回空快照(会产生删除事件)。</summary>
    private async Task<Dictionary<string, Entry>?> TryScanAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            await ScanAsync(_directory, result, ct).ConfigureAwait(false);
            return result;
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            return result;
        }
        catch (EnvironmentException)
        {
            return null;
        }
    }

    private async Task ScanAsync(EnvPath directory, Dictionary<string, Entry> into, CancellationToken ct)
    {
        foreach (var entry in await _fs.ListAsync(directory, ct).ConfigureAwait(false))
        {
            if (into.Count >= _options.MaxEntries)
            {
                return;
            }

            var path = directory.Join(entry.Name);
            into[path.Value] = new Entry(path, entry.Info.Kind, entry.Info.Size, entry.Info.ModifiedAt);
            if (_options.Recursive && entry.Info.IsDirectory)
            {
                try
                {
                    await ScanAsync(path, into, ct).ConfigureAwait(false);
                }
                catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.PermissionDenied)
                {
                    // 扫描途中被删掉或没有权限的子目录:跳过
                }
            }
        }
    }

    private void Diff(Dictionary<string, Entry> previous, Dictionary<string, Entry> current)
    {
        foreach (var (key, entry) in current)
        {
            if (!previous.TryGetValue(key, out var old))
            {
                _batcher.Add(new FsChange(FsChangeKind.Created, entry.Path));
            }
            else if (old.Kind != entry.Kind || old.Size != entry.Size || old.ModifiedAt != entry.ModifiedAt)
            {
                _batcher.Add(new FsChange(FsChangeKind.Modified, entry.Path));
            }
        }

        foreach (var (key, entry) in previous)
        {
            if (!current.ContainsKey(key))
            {
                _batcher.Add(new FsChange(FsChangeKind.Deleted, entry.Path));
            }
        }
    }

    private sealed record Entry(EnvPath Path, FsEntryKind Kind, long Size, DateTimeOffset ModifiedAt);
}
