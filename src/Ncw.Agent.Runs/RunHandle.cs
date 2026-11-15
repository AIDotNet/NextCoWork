using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Runs;

/// <summary>
/// <c>run/&lt;id&gt;</c> 的快照(09 §8.2):状态、当前 <c>seq</c>、<c>seq</c> 之后的事件(过滤而不是切片)、子 Run 列表、挂起交互数。
/// 挂起交互本身只有交互闸门一个真源(Host 从闸门取,和这份快照一起组装成线上的 <c>snapshot</c>)。
/// </summary>
public sealed record RunSnapshot(
    RunId RunId,
    string SessionId,
    string WorkspaceId,
    RunId? ParentRunId,
    int Depth,
    RunState State,
    AgentError? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long Seq,
    CursorStatus Cursor,
    IReadOnlyList<SequencedEvent> Events,
    int PendingInteractions,
    IReadOnlyList<RunId> Children,
    bool LogTrimmed,
    bool Settled);

/// <summary>后台子代理的元数据(2.x 的 <c>backgroundTask</c>),跟着它的权威 handle 走。子代理层设置。</summary>
public sealed record RunBackground(string Type, string Description);

/// <summary>handle 回调编排层(包内)。</summary>
internal interface IRunHandleHost
{
    TimeProvider Clock { get; }

    ILoopDiagnostics Log { get; }

    /// <summary>事件已进日志、已入订阅缓冲(在 handle 的锁之外调用)。</summary>
    void OnEvent(RunHandle run, AgentEvent ev, long seq);

    /// <summary>有人要求中止(只发信号)。编排层据此启动看门狗。</summary>
    void OnAbortRequested(RunHandle run);
}

/// <summary>
/// 一个 Run 的权威句柄(05 §1)。RunId 由客户端生成;持有只追加的事件日志(递增 <c>seq</c>)、子 Run 集合(<b>只增不减</b>,所以并发门限必须按「运行中的」子 Run 数,
/// 不能按集合大小)、订阅、实时权限模式、<see cref="Abandoned"/> 标记。
/// <para>
/// <b>状态单调</b>(AGENTS.md §3 规则 6):<see cref="State"/> 只会从 <see cref="RunState.Running"/> 走向终态,且只由第一个被接受的 <c>run_end</c> 写下。
/// 终态之后到达的事件(迟到的、重放的旧事件——包括旧的 <c>run_start</c>)一律丢弃并记警告,不推进 <c>seq</c>;唯一的例外是紧随终态的 <c>run_settled</c>(恰好一次)。
/// </para>
/// </summary>
public sealed class RunHandle : IDisposable
{
    private readonly IRunHandleHost _host;
    private readonly RunEventLog _log;
    private readonly object _gate = new();
    private readonly List<RunSubscription> _subscribers = [];
    private readonly HashSet<RunId> _children = [];
    private readonly HashSet<string> _startedCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<AgentEvent>> _parkedByCall = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();
    private readonly ModelRef _model;
    private long _seq;
    private bool _started;
    private bool _settledEmitted;
    private bool _abortRequested;
    private int _settling;
    private bool _abandoned;

    internal RunHandle(RunLaunch launch, IRunHandleHost host, int logMaxEntries, long logMaxBytes)
    {
        _host = host;
        _log = new RunEventLog(logMaxEntries, logMaxBytes);
        _model = launch.Request.Model;
        RunId = launch.RunId;
        SessionId = launch.SessionId;
        WorkspaceId = launch.WorkspaceId;
        ParentRunId = launch.ParentRunId;
        ParentSessionId = launch.ParentSessionId;
        Depth = launch.Depth;
        Origin = launch.Origin;
        Options = launch.Options;
        LivePermissionMode = launch.PermissionMode;
        StartedAt = host.Clock.GetUtcNow();
    }

    public RunId RunId { get; }

    public string SessionId { get; }

    public string WorkspaceId { get; }

    public RunId? ParentRunId { get; }

    public string? ParentSessionId { get; }

    public int Depth { get; }

    public RunOrigin Origin { get; }

    public SendOptions? Options { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>顶层 Run = 没有父 Run(子代理有自己的派生会话,不算「这条会话的 Run」)。</summary>
    public bool IsTopLevel => ParentRunId is null && Depth == 0;

    /// <summary>
    /// 权限模式的<b>当前生效值</b>(不透明字符串)。用户中途把界面上的权限药丸调宽,应该立刻对这个 Run <b>接下来</b>的工具调用生效。
    /// 只影响还没做出的判定,不会回头解出已经在等用户点的那个弹窗(2.x ★)。
    /// </summary>
    public string LivePermissionMode
    {
        get
        {
            lock (_gate)
            {
                return field;
            }
        }

        set
        {
            lock (_gate)
            {
                field = value;
            }
        }
    }

    /// <summary>后台子代理的元数据;子代理层设置。</summary>
    public RunBackground? Background { get; set; }

    /// <summary>驱动任务(应用退出时等它收尾用)。</summary>
    public Task? Completion { get; internal set; }

    /// <summary>★ 必传给每个工具的取消令牌:只断流不断工具会留下僵尸进程。</summary>
    public CancellationToken Token => _cts.Token;

    public bool AbortRequested
    {
        get
        {
            lock (_gate)
            {
                return _abortRequested;
            }
        }
    }

    /// <summary>
    /// 用户按了停止,驱动却在宽限期内一直没收尾,被看门狗强行收口了(2.x #187)。置位之后这个 Run 在注册表里已经是终态、会话互斥也已放开——
    /// 驱动哪天自己醒过来,它的事件一律丢弃。
    /// </summary>
    public bool Abandoned
    {
        get => Volatile.Read(ref _abandoned);
        internal set => Volatile.Write(ref _abandoned, value);
    }

    public RunState State
    {
        get
        {
            lock (_gate)
            {
                return field;
            }
        }

        private set;
    } = RunState.Running;

    public AgentError? Error { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>已分配的最后一个 seq。永远单调递增,不受裁剪影响。</summary>
    public long Seq
    {
        get
        {
            lock (_gate)
            {
                return _seq;
            }
        }
    }

    /// <summary><c>run_settled</c> 已发(落库和释放会话互斥之后)。回收要求它为真。</summary>
    public bool IsSettled
    {
        get
        {
            lock (_gate)
            {
                return _settledEmitted;
            }
        }
    }

    public int SubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>这份日志现在大约占多少字节(回收已结束 Run 时按它记账)。</summary>
    public long RetainedLogBytes
    {
        get
        {
            lock (_gate)
            {
                return _log.RetainedBytes;
            }
        }
    }

    public bool HasStarted
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    public IReadOnlyList<RunId> Children
    {
        get
        {
            lock (_gate)
            {
                return [.. _children];
            }
        }
    }

    internal ModelRef Model => _model;

    // ─────────────────────────── 发射 ───────────────────────────

    /// <summary>
    /// 追加一个事件,分配 <c>seq</c> 并推给订阅者。被丢弃(终态之后的迟到事件、重复的 <c>run_end</c> / <c>run_settled</c>)时返回 0。
    /// ★ 「关于某次工具调用」的事件(<see cref="IToolCallScopedEvent"/>:进度、子代理起止 / 更新)在这次调用的 <c>tool_start</c> 之前到达时先暂存,
    /// <c>tool_start</c> 一落下就紧跟着落(各自分配 <c>seq</c>):工具线程经 <c>Post</c> 直接投事件,而循环自己的 <c>tool_start</c> 要经事件通道,CPU 吃紧时前者会先到——
    /// 控制端对「还没有卡片的进度」只能丢掉,排队的排位就这样永远显示不出来(Integration 的排位用例整程序集并发时稳定失败的根因)。暂存的事件返回当前最后的 <c>seq</c>(非 0:已接受)。
    /// </summary>
    public long Emit(AgentEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        long seq;
        var appended = new List<(AgentEvent Event, long Seq)>(1);
        List<RunSubscription>? flush = null;
        RunSubscription[]? finishing = null;
        lock (_gate)
        {
            if (!Accepts(ev))
            {
                _host.Log.Warn($"Run {RunId} is {State}; dropped late event {ev.GetType().Name}.");
                return 0;
            }

            if (ev is IToolCallScopedEvent scoped && !_startedCalls.Contains(scoped.CallId))
            {
                if (!_parkedByCall.TryGetValue(scoped.CallId, out var waiting))
                {
                    _parkedByCall[scoped.CallId] = waiting = [];
                }

                waiting.Add(ev);
                return Math.Max(_seq, 1);
            }

            seq = AppendLocked(ev, appended, ref flush, ref finishing);
            if (ev is ToolStartEvent start)
            {
                _startedCalls.Add(start.CallId);
                if (_parkedByCall.Remove(start.CallId, out var waiting))
                {
                    foreach (var parked in waiting)
                    {
                        AppendLocked(parked, appended, ref flush, ref finishing);
                    }
                }
            }
            else if (ev is RunEndEvent && _parkedByCall.Count > 0)
            {
                // 这次调用从来没有 tool_start(循环保证每个调用都有,走到这里是异常路径):暂存的事件没有可以挂的卡片,丢弃并记一笔
                _host.Log.Warn($"Run {RunId} ended with {_parkedByCall.Values.Sum(static l => l.Count)} event(s) waiting for a tool_start that never came; dropped.");
                _parkedByCall.Clear();
            }
        }

        if (flush is not null)
        {
            foreach (var sub in flush)
            {
                sub.Flush();
            }
        }

        if (finishing is not null)
        {
            foreach (var sub in finishing)
            {
                sub.Finish(this);
            }
        }

        foreach (var (appendedEvent, appendedSeq) in appended)
        {
            _host.OnEvent(this, appendedEvent, appendedSeq);
        }

        return seq;
    }

    /// <summary>在 <c>_gate</c> 里把一个已通过 <see cref="Accepts"/> 的事件落进日志、更新状态并入各订阅者的缓冲。</summary>
    private long AppendLocked(AgentEvent ev, List<(AgentEvent Event, long Seq)> appended, ref List<RunSubscription>? flush, ref RunSubscription[]? finishing)
    {
        var seq = ++_seq;
        _log.Append(seq, ev);
        switch (ev)
        {
            case RunStartEvent:
                _started = true;
                break;
            case RunEndEvent end:
                State = RunStates.Of(end.Status);
                Error = end.Error;
                EndedAt = _host.Clock.GetUtcNow();
                break;
            case RunSettledEvent:
                _settledEmitted = true;
                break;
        }

        foreach (var sub in _subscribers)
        {
            if (sub.Enqueue(RunId, seq, ev))
            {
                (flush ??= []).Add(sub);
            }
        }

        if (ev is RunSettledEvent)
        {
            finishing = [.. _subscribers];
            _subscribers.Clear();
        }

        appended.Add((ev, seq));
        return seq;
    }

    private bool Accepts(AgentEvent ev) => ev switch
    {
        // run_settled 紧随终态、恰好一次;还在运行时来的 run_settled 是错的。
        RunSettledEvent => State.IsTerminal() && !_settledEmitted,
        _ => !State.IsTerminal(),
    };

    /// <summary>
    /// 收口成终态(驱动抛了异常、看门狗、没开始就结束)。从没发过 <c>run_start</c> 时先补一个,保证事件流自洽。
    /// 只在还没有终态时生效;返回是否真的写下了这个终态。
    /// </summary>
    public bool Finish(RunStatus status, AgentError? error = null)
    {
        if (State.IsTerminal())
        {
            return false;
        }

        if (!HasStarted)
        {
            Emit(new RunStartEvent(RunId, SessionId, _model));
        }

        return Emit(new RunEndEvent(status, error)) > 0;
    }

    /// <summary>settle 只收一次:被看门狗强行收口过的 Run,驱动日后真醒过来也不能再放一次别人的锁、再续一次队列。</summary>
    internal bool TryBeginSettle() => Interlocked.Exchange(ref _settling, 1) == 0;

    // ─────────────────────────── 中止 ───────────────────────────

    /// <summary>
    /// 中止只发信号,不直接改 Run 状态(02 §6):状态由循环在收尾时写成 Aborted;编排层另有看门狗兜底。
    /// 一次中止要完成的五件事里,这里只管取消令牌(上游 / 工具 / 交互闸门都挂在同一个令牌上);补「已中止」结果由循环做,级联子 Run 由注册表做。
    /// </summary>
    public bool Abort()
    {
        lock (_gate)
        {
            if (State.IsTerminal() || _abortRequested)
            {
                return false;
            }

            _abortRequested = true;
        }

        try
        {
            _cts.Cancel();
        }
        catch (Exception e)
        {
            _host.Log.Warn("A cancellation callback threw while aborting a run.", e);
        }

        _host.OnAbortRequested(this);
        return true;
    }

    // ─────────────────────────── 子 Run / 订阅 ───────────────────────────

    internal void AddChild(RunId child)
    {
        lock (_gate)
        {
            _children.Add(child);
        }
    }

    internal RunSubscription[] SubscribersSnapshot()
    {
        lock (_gate)
        {
            return [.. _subscribers];
        }
    }

    /// <summary>挂上订阅(Run 的第一个事件之前:启动时绑定 / 子 Run 继承)。已 settle 的 Run 不再接受。</summary>
    internal void AddSubscriber(RunSubscription sub)
    {
        lock (_gate)
        {
            if (_settledEmitted || sub.IsClosed || _subscribers.Contains(sub))
            {
                return;
            }

            _subscribers.Add(sub);
            sub.Follow(this);
        }
    }

    internal void RemoveSubscriber(RunSubscription sub)
    {
        lock (_gate)
        {
            _subscribers.Remove(sub);
        }
    }

    // ─────────────────────────── 快照 / 重放 ───────────────────────────

    /// <summary>
    /// <c>seq</c> 之后的事件(过滤)。日志会裁剪,序号可能有空洞:客户端<b>不</b>对重放事件做「seq != last + 1」的补齐,应用完之后直接以快照的 <c>seq</c> 为准(2.x ★)。
    /// </summary>
    public IReadOnlyList<SequencedEvent> Since(long sinceSeq)
    {
        lock (_gate)
        {
            return _log.Since(sinceSeq);
        }
    }

    /// <summary>
    /// 取快照。<paramref name="sinceSeq"/> 为 null = 全量(<see cref="CursorStatus.Fresh"/>);给了且还在日志里 = 只给其后的事件;游标早已被硬上限挤出去 =
    /// <see cref="CursorStatus.Lost"/>(给现存的全部事件,客户端以快照重建)。
    /// </summary>
    public RunSnapshot Snapshot(long? sinceSeq = null, int pendingInteractions = 0)
    {
        lock (_gate)
        {
            return SnapshotLocked(sinceSeq, pendingInteractions);
        }
    }

    /// <summary>原子地「取快照 + 挂订阅」:之后发出的事件一条不漏、快照里已有的一条不重。</summary>
    internal RunSnapshot Attach(RunSubscription sub, long? sinceSeq, int pendingInteractions)
    {
        lock (_gate)
        {
            var snapshot = SnapshotLocked(sinceSeq, pendingInteractions);
            if (!_settledEmitted && !sub.IsClosed && !_subscribers.Contains(sub))
            {
                _subscribers.Add(sub);
                sub.Follow(this);
            }

            return snapshot;
        }
    }

    private RunSnapshot SnapshotLocked(long? sinceSeq, int pendingInteractions)
    {
        var cursor = sinceSeq is null ? CursorStatus.Fresh : sinceSeq.Value < _log.TrimmedThrough ? CursorStatus.Lost : CursorStatus.Continued;
        var events = cursor == CursorStatus.Continued ? _log.Since(sinceSeq!.Value) : _log.Since(0);
        return new RunSnapshot(
            RunId, SessionId, WorkspaceId, ParentRunId, Depth, State, Error, StartedAt, EndedAt, _seq, cursor, events,
            pendingInteractions, [.. _children], _log.LogTrimmed, _settledEmitted);
    }

    public void Dispose() => _cts.Dispose();
}
