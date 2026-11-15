using System.Collections.Immutable;

namespace Ncw.Core.Domain;

/// <summary>
/// Run 的生命周期阶段。<see cref="Unknown"/> 只在控制端出现:设备断开时,运行中的状态既不能当成结束,也不能继续算作运行中(规则 7)。
/// </summary>
public enum RunPhase
{
    Unknown,
    Running,
    Done,
    Error,
    Aborted,
}

/// <summary>Run 状态的单调性(AGENTS.md §3 规则 6)。Agent 和控制端共用,不许各自再判一遍。</summary>
public static class RunLifecycle
{
    public static bool IsTerminal(RunPhase phase) => phase is RunPhase.Done or RunPhase.Error or RunPhase.Aborted;

    /// <summary>排名:未知 &lt; 运行中 &lt; 终态。</summary>
    public static int Rank(RunPhase phase) => phase switch
    {
        RunPhase.Unknown => 0,
        RunPhase.Running => 1,
        _ => 2,
    };

    /// <summary>
    /// 合并两份关于同一个 Run 的说法:终态压过运行中,运行中压过未知——不论哪一份更晚到。重放的旧事件、乱序的广播都因此不能把状态改回运行中。
    /// 两个终态保留 <paramref name="current"/>(先到的那个:终态一旦写下就不再改)。
    /// </summary>
    public static RunPhase Merge(RunPhase current, RunPhase incoming) => Rank(incoming) > Rank(current) ? incoming : current;
}

/// <summary>一个 Run 的事实(Agent 的运行注册表里读出来的),<see cref="ActiveRunIndexBuilder"/> 的输入。</summary>
public sealed record RunFact(
    string RunId,
    string SessionId,
    string WorkspaceId,
    string? ParentRunId,
    string? ParentSessionId,
    RunPhase Phase,
    DateTimeOffset StartedAt,
    int PendingInteractions);

/// <summary>
/// <c>activeRuns</c> 权威广播(docs/design/08-agent-runtime/05 §2)的<b>唯一</b>生成函数:首帧(<c>device</c> 主题的快照)和每一次后续广播都调它,
/// 顶层半边和子代理半边出自同一次遍历(2.x 里两边各写一遍判据,漏掉一边就是一颗永远不灭的圆点)。
/// </summary>
public static class ActiveRunIndexBuilder
{
    /// <param name="deviceId">设备 id。</param>
    /// <param name="facts">注册表里<b>所有</b> Run 的事实(含已结束但还没回收的:后代遍历要靠它们的父子关系)。</param>
    /// <param name="rev">这一帧的版本号。</param>
    public static ActiveRunIndex Build(string deviceId, IReadOnlyCollection<RunFact> facts, long rev)
    {
        var byId = new Dictionary<string, RunFact>(facts.Count, StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            byId[fact.RunId] = fact;
        }

        foreach (var fact in byId.Values)
        {
            if (fact.ParentRunId is { } parent)
            {
                if (!children.TryGetValue(parent, out var list))
                {
                    children[parent] = list = [];
                }

                list.Add(fact.RunId);
            }
        }

        var runs = ImmutableArray.CreateBuilder<ActiveRun>();
        var subagents = ImmutableArray.CreateBuilder<ActiveSubagent>();
        foreach (var fact in byId.Values.Where(f => f.Phase == RunPhase.Running).OrderBy(f => f.StartedAt).ThenBy(f => f.RunId, StringComparer.Ordinal))
        {
            if (fact.ParentRunId is null)
            {
                // 顶层:待处理交互数含所有后代(一直往下查)。
                runs.Add(new ActiveRun(deviceId, fact.RunId, fact.SessionId, fact.WorkspaceId, PendingWithDescendants(fact.RunId, byId, children)));
                continue;
            }

            // 子 Run:sessionId 用父会话;父 Run 已回收时用 parentSessionId(后台子代理活得比父 Run 长是常态)。
            byId.TryGetValue(fact.ParentRunId, out var parentFact);
            var sessionId = parentFact?.SessionId ?? fact.ParentSessionId;
            if (sessionId is null)
            {
                continue;
            }

            subagents.Add(new ActiveSubagent(deviceId, fact.RunId, fact.ParentRunId, sessionId, parentFact?.WorkspaceId ?? fact.WorkspaceId, fact.StartedAt));
        }

        return new ActiveRunIndex(deviceId, runs.ToImmutable())
        {
            Subagents = subagents.ToImmutable(),
            Rev = rev,
        };
    }

    private static int PendingWithDescendants(string runId, Dictionary<string, RunFact> byId, Dictionary<string, List<string>> children)
    {
        var total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(runId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id))
            {
                continue;
            }

            if (byId.TryGetValue(id, out var fact))
            {
                total += fact.PendingInteractions;
            }

            if (children.TryGetValue(id, out var kids))
            {
                foreach (var kid in kids)
                {
                    stack.Push(kid);
                }
            }
        }

        return total;
    }
}
