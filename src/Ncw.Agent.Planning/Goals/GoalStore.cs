using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning.Goals;

/// <summary>
/// 目标变了(全量:这一刻的目标,或 <c>null</c> = 没有)。<see cref="Cleared"/> 是被清掉的原因(只在清除时有);<see cref="Message"/> 是带外写了标记的那条助手消息
/// (只给界面:应用它时要保留流里还没提交的块)。控制端按 <c>deviceId + sessionId</c> 镜像一份;首帧(<c>goal.get</c>)和后续变更是同一个类型。
/// </summary>
public sealed record GoalChange(string SessionId, ActiveGoal? Goal, GoalClearReason? Cleared = null, Message? Message = null);

/// <summary>
/// 目标状态机(2.x <c>goal/state.ts</c>):一份<b>进程内</b>状态 + 一条通知。
/// <para>
/// 为什么不落盘:落盘的是转录里那条 <c>goal_status</c> 标记,重载后靠 <see cref="GoalRestore"/> 从它反扫。状态本身另存一份,会让「重载应用后一个早就结束的目标还在文件里」
/// 和「用户手改文件叠出两条目标」两件事都发生。
/// </para>
/// <para>
/// 一个会话同时只有一个目标:<see cref="Set"/> 必须<b>先清旧的</b>再装新的。漏掉的话一条会话里会同时挂着两个判定:判定跑两遍、账单翻倍,而两遍的结论还可能不一样。
/// 线程安全;监听器在锁外同步调用,抛异常不影响状态。
/// </para>
/// </summary>
public sealed class GoalStore
{
    private sealed class Entry(ActiveGoal goal)
    {
        public ActiveGoal Goal { get; set; } = goal;

        public CancellationTokenSource Cts { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _goals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initialized = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
    private readonly Action<string, Exception>? _onListenerError;

    public GoalStore(Action<string, Exception>? onListenerError = null) => _onListenerError = onListenerError;

    public event Action<GoalChange>? Changed;

    public bool WasInitialized(string sessionId)
    {
        lock (_gate)
        {
            return _initialized.Contains(sessionId);
        }
    }

    public void MarkInitialized(string sessionId)
    {
        lock (_gate)
        {
            _initialized.Add(sessionId);
        }
    }

    /// <summary>清除或替换目标时,它还在路上的判定一起取消。</summary>
    public CancellationToken? Signal(string sessionId)
    {
        lock (_gate)
        {
            return _goals.TryGetValue(sessionId, out var entry) ? entry.Cts.Token : null;
        }
    }

    public ActiveGoal? Get(string sessionId)
    {
        lock (_gate)
        {
            return _goals.TryGetValue(sessionId, out var entry) ? entry.Goal : null;
        }
    }

    public IReadOnlyDictionary<string, ActiveGoal> All()
    {
        lock (_gate)
        {
            return _goals.ToDictionary(kv => kv.Key, kv => kv.Value.Goal, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 清除 / 替换的「版本」。用户的更新意图(设定新目标、手动清除)让它前进,作废手里还拿着的批准条;自动完成(达成 / 判不可能)<b>不</b>前进。
    /// </summary>
    public long Revision(string sessionId)
    {
        lock (_gate)
        {
            return _revisions.GetValueOrDefault(sessionId);
        }
    }

    /// <summary>目标生效——先摘旧的并取消旧判定,再装新的。</summary>
    public void Set(string sessionId, ActiveGoal goal)
    {
        Clear(sessionId, GoalClearReason.Superseded);
        lock (_gate)
        {
            _initialized.Add(sessionId);
            _goals[sessionId] = new Entry(goal);
        }

        Notify(new GoalChange(sessionId, goal));
    }

    /// <summary>只改状态(判未达成后累加轮数那条路)。没有目标返回 <c>null</c>。</summary>
    public ActiveGoal? Update(string sessionId, Func<ActiveGoal, ActiveGoal> patch)
    {
        ActiveGoal next;
        lock (_gate)
        {
            if (!_goals.TryGetValue(sessionId, out var entry))
            {
                return null;
            }

            next = patch(entry.Goal);
            entry.Goal = next;
        }

        Notify(new GoalChange(sessionId, next));
        return next;
    }

    /// <summary>
    /// 清状态 + 广播。★ <paramref name="reason"/> 在这里就决定了版本是否前进,而且调用方就是在这一行决定它的:清除的原因是遥测唯一的分叉依据(<c>Met</c> 和 <c>UserClear</c> 在用户眼里是两件完全不同的事)。
    /// </summary>
    public ActiveGoal? Clear(string sessionId, GoalClearReason reason)
    {
        Entry? entry;
        lock (_gate)
        {
            // 用户更新的意图会让手里还拿着的批准条作废;自动完成不会
            if (reason is not (GoalClearReason.Met or GoalClearReason.Impossible))
            {
                _revisions[sessionId] = _revisions.GetValueOrDefault(sessionId) + 1;
            }

            _initialized.Add(sessionId);
            if (!_goals.Remove(sessionId, out entry))
            {
                return null;
            }
        }

        try
        {
            entry.Cts.Cancel();
        }
        catch (Exception)
        {
            // 取消回调抛异常不能让目标清不掉
        }

        Notify(new GoalChange(sessionId, null, reason));
        return entry.Goal;
    }

    /// <summary>带外写了标记:把更新后的助手消息随一次变更广播出去(目标本身没变)。</summary>
    internal void NotifyMarker(string sessionId, Message message) => Notify(new GoalChange(sessionId, Get(sessionId), null, message));

    private void Notify(GoalChange change)
    {
        var handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<GoalChange>)handler)(change);
            }
            catch (Exception e)
            {
                // 一个坏掉的监听者不该让目标本身设不上
                _onListenerError?.Invoke("A goal change listener threw.", e);
            }
        }
    }
}
