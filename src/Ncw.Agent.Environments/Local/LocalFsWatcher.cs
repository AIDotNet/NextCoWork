using System.Runtime.CompilerServices;

namespace Ncw.Agent.Environments;

/// <summary>本机目录监视:<see cref="FileSystemWatcher"/> 的事件合并成批。</summary>
internal sealed class LocalFsWatcher : IFsWatcher
{
    private readonly FileSystemWatcher _watcher;
    private readonly FsChangeBatcher _batcher = new();
    private readonly FsWatchOptions _options;
    private readonly PathStyle _style;

    public LocalFsWatcher(string directory, PathStyle style, FsWatchOptions options)
    {
        _options = options;
        _style = style;
        if (!Directory.Exists(directory))
        {
            throw new EnvironmentException(EnvironmentErrorCode.NotFound, directory);
        }

        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = options.Recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Emit(FsChangeKind.Created, e.FullPath);
        _watcher.Changed += (_, e) => Emit(FsChangeKind.Modified, e.FullPath);
        _watcher.Deleted += (_, e) => Emit(FsChangeKind.Deleted, e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            Emit(FsChangeKind.Deleted, e.OldFullPath);
            Emit(FsChangeKind.Created, e.FullPath);
        };
        _watcher.EnableRaisingEvents = true;
    }

    public IAsyncEnumerable<IReadOnlyList<FsChange>> Batches => Read();

    public void Refresh()
    {
        // 系统通知是推送的,没有需要主动刷新的东西
    }

    public ValueTask DisposeAsync()
    {
        _watcher.Dispose();
        _batcher.Complete();
        return ValueTask.CompletedTask;
    }

    private async IAsyncEnumerable<IReadOnlyList<FsChange>> Read([EnumeratorCancellation] CancellationToken ct = default)
    {
        // 释放监视器会让通道完成,枚举随之正常结束
        await foreach (var batch in _batcher.ReadBatchesAsync(_options.Debounce, ct).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private void Emit(FsChangeKind kind, string fullPath)
    {
        try
        {
            _batcher.Add(new FsChange(kind, EnvPath.Parse(fullPath, _style)));
        }
        catch (EnvironmentException)
        {
            // 无法表示成路径的名字(例如非法字符)直接忽略
        }
    }
}
