using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;

namespace Ncw.Agent.Tools;

public enum ChangeKind
{
    Created,
    Modified,
}

/// <param name="Before">首个 before;新建文件为 <c>null</c>。</param>
/// <param name="After">最后一个 after。</param>
/// <param name="InWorkspace"><c>false</c> = 改到了工作区外的文件。</param>
public sealed record PendingChange(EnvPath Path, string DisplayPath, string? Before, string After, ChangeKind Kind, bool InWorkspace);

/// <summary>一次写盘的旁路通知(插件的「文件变化」事件要用)。每一次写盘都发,不只是首次。</summary>
public sealed record FileChanged(EnvPath Path, string DisplayPath, ChangeKind Kind, bool InWorkspace);

/// <summary>
/// 「这次运行里改了哪些文件、改前改后各是什么」(移植自 2.x <c>change-recorder.ts</c>)。采集只发生在写盘之后(Write / Edit)。
/// 合并规则:同一轮多次改同一文件,<b>首个</b> before 黏住、<b>最后一个</b> after 覆盖;<see cref="ChangeKind.Created"/> 一旦置位不被后续的修改翻转
/// (这一轮里新建的文件,撤销时应当整个删掉,而不是回退到某个中间版本)。按 Run 分桶、封顶 <see cref="MaxRuns"/> 个 Run。
/// </summary>
public sealed class ChangeRecorder
{
    public const int MaxRuns = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, PendingChange>> _byRun = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>旁路监听器。抛出的异常被吞掉:它的问题不能连累这次采集。</summary>
    public event Action<FileChanged>? Changed;

    public void Record(RunId run, EnvPath path, string displayPath, string? before, string after, bool inWorkspace)
    {
        ChangeKind notified;
        lock (_gate)
        {
            if (!_byRun.TryGetValue(run.Value, out var bucket))
            {
                bucket = new Dictionary<string, PendingChange>(StringComparer.Ordinal);
                while (_byRun.Count >= MaxRuns && _order.TryDequeue(out var oldest))
                {
                    _byRun.Remove(oldest);
                }

                _byRun[run.Value] = bucket;
                _order.Enqueue(run.Value);
            }

            var key = ReadTracker.Key(path);
            if (bucket.TryGetValue(key, out var existing))
            {
                bucket[key] = existing with { After = after };
                notified = ChangeKind.Modified;
            }
            else
            {
                notified = before is null ? ChangeKind.Created : ChangeKind.Modified;
                bucket[key] = new PendingChange(path, displayPath, before, after, notified, inWorkspace);
            }
        }

        try
        {
            Changed?.Invoke(new FileChanged(path, displayPath, notified, inWorkspace));
        }
        catch (Exception)
        {
            // 旁路监听器的问题
        }
    }

    /// <summary>读走并清空某个 Run 的采集结果(Run 收尾封包时一次性取)。</summary>
    public IReadOnlyList<PendingChange> Take(RunId run)
    {
        lock (_gate)
        {
            if (!_byRun.Remove(run.Value, out var bucket))
            {
                return [];
            }

            return bucket.Values.ToList();
        }
    }
}
