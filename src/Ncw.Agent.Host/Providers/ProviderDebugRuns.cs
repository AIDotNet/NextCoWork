using Ncw.Agent.Kernel.Loop;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Providers;

/// <summary>
/// 正在进行 / 刚结束的调试(<c>providers.debug/&lt;runId&gt;</c> 前缀主题的真源,docs/design/09 §8.4)。快照是到此刻为止的全部事件;
/// 没开始 / 已回收的 runId 给空快照,所以控制端可以先订阅再发起。结束的保留最近 <see cref="MaxKept"/> 个(断线重订还能拿到),再早的回收。
/// 这只是尽力而为的实时视图:权威结果是 <c>providers.debug.run</c> 的返回值。
/// </summary>
internal sealed class ProviderDebugRuns(TopicHub topics, ILoopDiagnostics log)
{
    public const int MaxKept = 16;

    /// <summary>runId 的形状:控制端生成的短 id(会进主题名,不能带斜杠之类)。</summary>
    public const int MaxRunIdLength = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _runs = new(StringComparer.Ordinal);
    private readonly Queue<string> _finished = new();

    private sealed class Entry
    {
        public List<ProviderDebugEventDto> Events { get; } = [];

        public bool Running { get; set; } = true;
    }

    public static bool IsValidRunId(string? runId) =>
        !string.IsNullOrEmpty(runId) && runId.Length <= MaxRunIdLength && runId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public ProviderDebugTopicState State(string runId)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(runId, out var entry)
                ? new ProviderDebugTopicState(runId, entry.Running, [.. entry.Events])
                : new ProviderDebugTopicState(runId, false, []);
        }
    }

    /// <summary>开始一次调试。同一个 runId 正在跑时拒绝(控制端每次生成新 id;重放的幂等请求由路由直接回缓存,不会走到这里)。</summary>
    public void Start(string runId)
    {
        lock (_gate)
        {
            if (_runs.TryGetValue(runId, out var existing) && existing.Running)
            {
                throw new ProtocolException(ErrorCodes.Conflict, "A debug request with this id is already running.");
            }

            _runs[runId] = new Entry();
        }
    }

    public void Add(string runId, ProviderDebugEventDto ev)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var entry))
            {
                return;
            }

            entry.Events.Add(ev);
        }

        try
        {
            topics.Publish(ProviderDebugMethods.TopicPrefix + runId, ProviderDebugEvents.Event, ev, PayloadShapes.GetShape<ProviderDebugEventDto>());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log.Warn("Publishing a provider debug event failed.", e);
        }
    }

    public void Finish(string runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var entry))
            {
                return;
            }

            entry.Running = false;
            _finished.Enqueue(runId);
            while (_finished.Count > MaxKept)
            {
                var old = _finished.Dequeue();
                if (_runs.TryGetValue(old, out var stale) && !stale.Running)
                {
                    _runs.Remove(old);
                }
            }
        }
    }
}
