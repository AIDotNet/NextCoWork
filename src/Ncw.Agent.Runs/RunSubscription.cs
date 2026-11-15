using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Kernel.Runs;

/// <summary>同一个 Run 的一批连续到达的事件(批内按 <c>seq</c> 升序)。</summary>
public sealed record RunEventBatch(RunId RunId, IReadOnlyList<SequencedEvent> Events)
{
    public long LastSeq => Events[^1].Seq;
}

public enum ResyncReason
{
    /// <summary>结构性事件送不出去(控制端的发送缓冲满了):永不悄悄丢弃,改为要求控制端不带游标重新订阅(09 §8.2)。</summary>
    BacklogOverflow,
}

/// <summary>
/// 订阅的接收方(Host 的连接适配:往控制端的发送缓冲里写)。回调在发送线程上串行执行,<b>不要在里面回调注册表的写方法</b>。
/// </summary>
public interface IRunListener
{
    /// <summary>
    /// 送一批事件。返回 false = 送不出去(发送缓冲超上限,09 §8.2 的流控):只有增量的批整批丢掉(Run 照常运行,日志才是真源,
    /// <c>message_end</c> 会带完整消息把它补上);含结构性事件的批不能丢,订阅随即收到 <see cref="Resync"/> 并结束。
    /// </summary>
    bool TryDeliver(IReadOnlyList<RunEventBatch> batches);

    /// <summary>设备无法继续:客户端必须不带游标重新订阅。之后这个订阅不会再收到任何事件。</summary>
    void Resync(ResyncReason reason);
}

public sealed record SubscriptionOptions
{
    /// <summary>
    /// 手机控制端在 <c>hello</c> 里声明的合并窗口(02 A3);泵按控制端各自合并。0 = 不合并,每条事件立即送。
    /// 只有文本、思考、工具参数增量会被攒起来;结构性事件立即送,并且带上它前面攒着的增量(保序)。
    /// </summary>
    public TimeSpan Coalesce { get; init; } = TimeSpan.FromMilliseconds(16);

    /// <summary>攒满这么多条就送,不等窗口(2.x 的单批上限 64)。</summary>
    public int MaxBatch { get; init; } = 64;
}

/// <summary>
/// 一个控制端对一棵 Run 树的订阅(<c>run/&lt;id&gt;</c> 主题,自动包含后代,09 §8.2)+ 它自己的事件泵。
/// <para>
/// 订阅<b>先于</b>启动:<see cref="RunLaunch.Subscriptions"/> 在 Run 的第一个事件之前就挂上;子 Run 创建时继承父 Run 的全部订阅(2.x ★:不继承,
/// 子 Run 的事件落进没人听的泵里整批丢弃,一切正常、没有报错、界面上什么都不发生)。已有的 Run 用 <see cref="RunRegistry.Attach"/>:
/// 「订阅 + 取快照」在 Run 的锁里原子完成,所以快照里的事件不会再推一遍,快照之后的事件一条不漏(2.x 的「先刷新泵、再订阅、后快照」在结构上成立)。
/// </para>
/// 事件被攒在订阅自己的缓冲里,没有订阅者时 Run 不产生任何推送开销(Run 照常运行,日志才是真源)。
/// </summary>
public sealed class RunSubscription : IDisposable
{
    private readonly IRunListener _listener;
    private readonly SubscriptionOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILoopDiagnostics _log;
    private readonly object _bufferGate = new();
    private readonly object _deliverGate = new();
    private readonly List<(RunId Run, SequencedEvent Event)> _buffer = [];
    private readonly List<RunHandle> _followed = [];
    private ITimer? _timer;
    private bool _closed;

    internal RunSubscription(IRunListener listener, SubscriptionOptions options, TimeProvider clock, ILoopDiagnostics log)
    {
        _listener = listener;
        _options = options;
        _clock = clock;
        _log = log;
    }

    /// <summary>因为积压溢出被丢掉的整批增量数(只增不减)。</summary>
    public int DroppedBatches { get; private set; }

    public bool IsClosed
    {
        get
        {
            lock (_bufferGate)
            {
                return _closed;
            }
        }
    }

    internal void Follow(RunHandle run)
    {
        lock (_bufferGate)
        {
            if (_closed || _followed.Contains(run))
            {
                return;
            }

            _followed.Add(run);
        }
    }

    internal bool Follows(RunHandle run)
    {
        lock (_bufferGate)
        {
            return _followed.Contains(run);
        }
    }

    internal void Unfollow(RunHandle run)
    {
        lock (_bufferGate)
        {
            _followed.Remove(run);
        }
    }

    /// <summary>由 <see cref="RunHandle"/> 在它的锁里调用(只入缓冲,不送):返回是否需要立即送(结构性事件 / 攒满)。</summary>
    internal bool Enqueue(RunId run, long seq, AgentEvent ev)
    {
        lock (_bufferGate)
        {
            if (_closed)
            {
                return false;
            }

            _buffer.Add((run, new SequencedEvent(seq, ev)));
            if (!IsCoalescable(ev) || _buffer.Count >= _options.MaxBatch || _options.Coalesce <= TimeSpan.Zero)
            {
                return true;
            }

            _timer ??= _clock.CreateTimer(static state => ((RunSubscription)state!).Flush(), this, _options.Coalesce, Timeout.InfiniteTimeSpan);
            return false;
        }
    }

    /// <summary>
    /// 只有这三种增量值得攒(2.x ★):其余都是结构性的——消息提交、工具起止、待决交互、run 结束——它们要么改变界面结构,要么用户正等着它出现。
    /// 这同时实现了「内容块边界立即 flush」:块边界事件本身就不可合批。
    /// </summary>
    internal static bool IsCoalescable(AgentEvent ev) => ev is MessageDeltaEvent;

    /// <summary>把缓冲里的事件送出去(定时器、结构性事件、手动刷新都走这里)。串行:同一个订阅的两批不会并发送。</summary>
    public void Flush()
    {
        lock (_deliverGate)
        {
            List<(RunId Run, SequencedEvent Event)> taken;
            lock (_bufferGate)
            {
                _timer?.Dispose();
                _timer = null;
                if (_buffer.Count == 0 || _closed)
                {
                    return;
                }

                taken = [.. _buffer];
                _buffer.Clear();
            }

            var batches = new List<RunEventBatch>();
            var start = 0;
            for (var i = 1; i <= taken.Count; i++)
            {
                if (i < taken.Count && taken[i].Run == taken[start].Run)
                {
                    continue;
                }

                batches.Add(new RunEventBatch(taken[start].Run, [.. taken.Skip(start).Take(i - start).Select(t => t.Event)]));
                start = i;
            }

            bool accepted;
            try
            {
                accepted = _listener.TryDeliver(batches);
            }
            catch (Exception e)
            {
                // 控制端断开 / 观察者异常不能打断权威执行(2.x notifyDelivery)。
                _log.Warn("A run subscription listener threw while delivering events.", e);
                accepted = true;
            }

            if (accepted)
            {
                return;
            }

            if (taken.TrueForAll(t => IsCoalescable(t.Event.Event)))
            {
                // 没人收得下:整批丢弃,不丢单条。
                DroppedBatches++;
                return;
            }

            // 结构性事件永不悄悄丢弃:送不出去就让客户端重同步。
            Close();
            try
            {
                _listener.Resync(ResyncReason.BacklogOverflow);
            }
            catch (Exception e)
            {
                _log.Warn("A run subscription listener threw while resyncing.", e);
            }
        }
    }

    /// <summary>取消订阅:不再收到任何事件(缓冲里没送出的丢弃)。幂等。</summary>
    public void Dispose() => Close();

    private void Close()
    {
        List<RunHandle> followed;
        lock (_bufferGate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _timer?.Dispose();
            _timer = null;
            _buffer.Clear();
            followed = [.. _followed];
            _followed.Clear();
        }

        foreach (var run in followed)
        {
            run.RemoveSubscriber(this);
        }
    }

    /// <summary>Run 已 settle:把最后的缓冲送出去,然后不再跟着它(订阅本身保持有效,后代 / 其他 Run 仍然跟)。</summary>
    internal void Finish(RunHandle run)
    {
        Flush();
        Unfollow(run);
    }
}
