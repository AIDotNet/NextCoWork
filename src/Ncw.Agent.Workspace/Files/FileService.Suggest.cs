using Ncw.Agent.Environments;
using Ncw.Core.Domain;

namespace Ncw.Agent.Workspace.Files;

public sealed partial class FileService
{
    private sealed record SuggestIndex(IReadOnlyList<FileSuggestion> Files, bool Truncated, long BuiltAt);

    /// <summary>
    /// 输入框 <c>@</c> 的文件名建议:一次遍历、一份带 TTL 的索引、打分在设备侧(整份清单过协议是上万条,而每敲一个字都想要新结果;回去的永远只有十几条)。
    /// <list type="bullet">
    /// <item>索引按「项目 + 环境 + 连接代」缓存,重连换了连接代就不会用旧环境的结果;TTL 而不是文件监听失效:这个功能对新鲜度要求很低,一次遍历的结果留几秒,连着敲十几下键只走一趟盘。</item>
    /// <item>同时缓存<b>正在建的那个任务</b>:连打三个字会并发进来三次,没有这一条就是三趟全量遍历。</item>
    /// <item>遍历复用 <see cref="Walk"/> 的闸门;根读不到时(项目被删)返回空而不是抛:<c>@</c> 只是个便利入口,它失败不该弹错误,用户照样可以把路径打出来。</item>
    /// </list>
    /// 路径一律是项目相对、<c>/</c> 分隔:它会原样落进草稿、再原样发给模型。
    /// </summary>
    public async ValueTask<SuggestResult> SuggestAsync(FileProject project, string query, int limit, CancellationToken ct = default)
    {
        SuggestIndex index;
        try
        {
            index = await IndexOfAsync(project).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is FilesException { Code: FilesErrorCode.ProjectUnavailable or FilesErrorCode.NotFound } or EnvironmentException { Code: EnvironmentErrorCode.NotFound })
        {
            return new SuggestResult([], false);
        }
        catch (EnvironmentException e)
        {
            throw FilesException.From(e);
        }

        var take = limit <= 0 ? FileLimits.SuggestDefault : Math.Min(limit, FileLimits.SuggestMax);
        return new SuggestResult(FuzzyPath.Rank(index.Files, query ?? "", take, static f => f.Path, static f => f.Name), index.Truncated);
    }

    private static string IndexKey(FileProject project) => $"{project.Id}\n{project.Environment.Id}\n{project.Environment.Generation}";

    private Task<SuggestIndex> IndexOfAsync(FileProject project)
    {
        var key = IndexKey(project);
        lock (_indexGate)
        {
            if (_indexes.TryGetValue(key, out var hit) && _options.Time.GetElapsedTime(hit.BuiltAt) < _options.IndexTtl)
            {
                return Task.FromResult(hit);
            }

            if (_building.TryGetValue(key, out var inflight))
            {
                return inflight;
            }

            // 建索引的任务不绑定某个调用者的取消:别的调用者在等同一个结果。登记在锁里、收尾的移除也在锁里,所以任务再快也不会「先移除、后登记」。
            var task = Task.Run(() => BuildAsync(project, key), CancellationToken.None);
            _building[key] = task;
            return task;
        }
    }

    private async Task<SuggestIndex> BuildAsync(FileProject project, string key)
    {
        try
        {
            var root = await ProjectPaths.RealRootAsync(project, CancellationToken.None).ConfigureAwait(false);
            var walked = await Walk.RunAsync(
                project.Environment.Fs, root, "", _options.MaxWalkEntries, _options.MaxWalkDepth, _options.WalkDeadline, _options.Time, CancellationToken.None).ConfigureAwait(false);
            var index = new SuggestIndex([.. walked.Files.Select(static f => new FileSuggestion(f.Relative, f.Name))], walked.Truncated, _options.Time.GetTimestamp());
            lock (_indexGate)
            {
                _indexes[key] = index;
            }

            return index;
        }
        finally
        {
            lock (_indexGate)
            {
                _building.Remove(key);
            }
        }
    }
}
