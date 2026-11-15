using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Loop;

/// <summary>
/// 排队的输入。<see cref="Id"/> 就是最终那条用户消息的 id(2.x ★):相等之后「这条排队消息被消费了没有」不需要任何新事件回答——
/// 看见同 id 的 <see cref="MessageEndEvent"/> 才算送达,不是「放进邮箱」就算。重放安全、幂等、恰好一次。
/// </summary>
public sealed record QueuedInput(MessageId Id, IReadOnlyList<Part> Parts, bool Internal = false);

public enum QueueState
{
    /// <summary>排队的消息:当前 Run 以 done 结束并 settle 之后,作为新 Run 开始(follow-up)。</summary>
    Pending,

    /// <summary>用户点了「插话」:当前回合的工具结果落库之后、下一次请求之前送进正在跑的 Run(steer)。</summary>
    Promoted,
}

public sealed record QueueEntry(QueuedInput Input, QueueState State, long PromotedOrder);

/// <summary>队列的权威快照(<c>queue_update</c> 事件的载荷)。<see cref="Rev"/> 单调递增:广播可能乱序到达,客户端只收更新的那份。</summary>
public sealed record QueueSnapshot(long Rev, IReadOnlyList<QueueEntry> Entries, IReadOnlyList<QueuedInput> InternalSteering);

/// <summary>
/// 一个会话的两种输入队列(02 §5,语义按 2.x <c>queued-input.ts</c> + <c>run-registry.ts</c> 的插话信箱 + <c>session-runtime.ts</c> 的续跑规则)。
/// <list type="bullet">
/// <item><b>引导消息(steer)</b>:被 <see cref="Promote"/> 的排队项和内部消息(后台子代理的报告、目标检查)。<see cref="TakeSteering"/> 取走即消费(防止重复注入),
/// 一条一条各自成消息,不合并;空 Parts 的跳过;不打断正在执行的工具批——只在回合边界取。</item>
/// <item><b>排队消息(follow-up)</b>:没被 promote 的项。只有 Run 以 <see cref="RunStatus.Done"/> 结束时 <see cref="TakeFollowUps"/> 才交出;
/// aborted / error 的 Run 不自动续跑(条目原样留着),冷启动也不自动跑。</item>
/// </list>
/// 线程安全。不负责落库回执(<c>session_input_receipts</c> 是会话层的事)和多条合并(mergeBatch 是编排层的事)。
/// </summary>
public sealed class RunInputQueues(TimeProvider clock)
{
    /// <summary>队列条目数软上限(2.x <c>QUEUE_MAX_ITEMS</c>):超过就不是队列了,是便签本。</summary>
    public const int MaxItems = 20;

    private readonly object _gate = new();
    private readonly List<(QueuedInput Input, QueueState State, long Order)> _entries = [];
    private readonly List<QueuedInput> _internal = [];
    private long _rev;
    private long _order;
    private bool _closed;

    /// <summary>入队(一律先是 Pending)。超过上限或同 id 已存在返回 false。</summary>
    public bool Enqueue(QueuedInput input)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaxItems || _entries.Any(e => e.Input.Id == input.Id))
            {
                return false;
            }

            _entries.Add((input, QueueState.Pending, 0));
            _rev++;
            return true;
        }
    }

    /// <summary>「插话」是 toggle:Pending ⇄ Promoted。取消引入后再引入排到已引入者的队尾(2.x ★)。</summary>
    public bool TogglePromote(MessageId id)
    {
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Input.Id == id);
            if (i < 0)
            {
                return false;
            }

            var e = _entries[i];
            _entries[i] = e.State == QueueState.Pending ? (e.Input, QueueState.Promoted, ++_order) : (e.Input, QueueState.Pending, 0);
            _rev++;
            return true;
        }
    }

    /// <summary>编辑内容。不改变状态,也不改变加塞顺序。</summary>
    public bool Edit(MessageId id, IReadOnlyList<Part> parts)
    {
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Input.Id == id);
            if (i < 0)
            {
                return false;
            }

            _entries[i] = (_entries[i].Input with { Parts = parts }, _entries[i].State, _entries[i].Order);
            _rev++;
            return true;
        }
    }

    /// <summary>删除或撤回(「take」)一条。</summary>
    public QueuedInput? Remove(MessageId id)
    {
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Input.Id == id);
            if (i < 0)
            {
                return null;
            }

            var input = _entries[i].Input;
            _entries.RemoveAt(i);
            _rev++;
            return input;
        }
    }

    /// <summary>
    /// 内部引导消息(后台结果、目标检查)。<b>不会被用户侧的整份替换/删除抹掉</b>(2.x ★:渲染层的队列同步不能吞掉后台结果)。
    /// Run 结束(<see cref="Close"/>)之后拒绝。
    /// </summary>
    public bool EnqueueInternalSteering(QueuedInput input)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return false;
            }

            _internal.Add(input with { Internal = true });
            _rev++;
            return true;
        }
    }

    /// <summary>
    /// 取走全部引导消息(Promoted 按引入顺序,然后是内部消息),<b>取走即消费</b>。
    /// 用来接 <see cref="LoopHooks.TakeSteering"/>。
    /// </summary>
    public IReadOnlyList<Message> TakeSteering()
    {
        lock (_gate)
        {
            var taken = _entries.Where(e => e.State == QueueState.Promoted).OrderBy(e => e.Order).Select(e => e.Input).Concat(_internal).ToList();
            if (taken.Count == 0)
            {
                return [];
            }

            _entries.RemoveAll(e => e.State == QueueState.Promoted);
            _internal.Clear();
            _rev++;
            var now = clock.GetUtcNow();
            return taken.Where(t => t.Parts.Count > 0).Select(t => new Message(t.Id, Role.User, t.Parts, now, t.Internal)).ToList();
        }
    }

    /// <summary>Run 结束:此后不再接受内部引导消息(留在队列里的会被会话层当作 follow-up 处理)。</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }

    /// <summary>
    /// Run 结束后该不该、该发哪些 follow-up。只有 <see cref="RunStatus.Done"/> 才交出(promoted 在前,按引入顺序;然后 pending 按入队顺序),
    /// 并把它们从队列移走;其它结局什么都不交出,条目原样留着。
    /// </summary>
    public IReadOnlyList<QueuedInput> TakeFollowUps(RunStatus finalStatus)
    {
        lock (_gate)
        {
            if (finalStatus != RunStatus.Done)
            {
                return [];
            }

            var ordered = _entries.Where(e => e.State == QueueState.Promoted).OrderBy(e => e.Order)
                .Concat(_entries.Where(e => e.State == QueueState.Pending))
                .Select(e => e.Input).Concat(_internal).ToList();
            if (ordered.Count > 0)
            {
                _entries.Clear();
                _internal.Clear();
                _rev++;
            }

            return ordered;
        }
    }

    /// <summary>
    /// 整体替换用户侧条目(保留各自的状态和引入顺序;内部引导消息不动):冷启动从存档恢复、持久化失败时回滚内存、续跑移交 / 放回。
    /// 之后的引入顺序从已有的最大值接着数,不回退。不检查上限和重复 id(调用方给的是自己先前取出的快照或校验过的存档)。
    /// </summary>
    public void Restore(IReadOnlyList<QueueEntry> entries)
    {
        lock (_gate)
        {
            _entries.Clear();
            foreach (var e in entries)
            {
                _entries.Add((e.Input, e.State, e.State == QueueState.Promoted ? e.PromotedOrder : 0));
                _order = Math.Max(_order, e.PromotedOrder);
            }

            _rev++;
        }
    }

    /// <summary>
    /// 取走还没被取走的内部引导消息(不碰用户侧条目)。Run 收尾时用:内部消息没被消费的由编排层按「没确认就收回」重投或退回,
    /// 不能留在这里被下一个 Run 的 <see cref="TakeSteering"/> 顺手取走(那就重复投递了)。
    /// </summary>
    public IReadOnlyList<QueuedInput> TakeInternal()
    {
        lock (_gate)
        {
            if (_internal.Count == 0)
            {
                return [];
            }

            var taken = _internal.ToList();
            _internal.Clear();
            _rev++;
            return taken;
        }
    }

    public QueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new QueueSnapshot(_rev, _entries.Select(e => new QueueEntry(e.Input, e.State, e.Order)).ToList(), _internal.ToList());
        }
    }
}
