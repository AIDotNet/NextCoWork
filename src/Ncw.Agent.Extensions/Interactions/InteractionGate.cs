using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Extensions.Interactions;

public enum ResolutionStatus
{
    /// <summary>有人回答了。</summary>
    Answered,

    /// <summary>中止 / Run 结束 / 取消:以中止拒绝,没有人回答。</summary>
    Aborted,
}

/// <summary>
/// 一次交互是怎么结束的。<see cref="ResolvedBy"/> 是回答者(控制端标识;中止时为 null),<see cref="At"/> 是什么时候——
/// 后到的控制端据此显示「已在另一台设备上处理」(09 §8.3)。
/// </summary>
public sealed record InteractionResolution(
    InteractionId Id,
    RunId RunId,
    string Kind,
    long Version,
    ResolutionStatus Status,
    string? ResolvedBy,
    DateTimeOffset At,
    long Revision);

/// <summary>
/// 全量快照:挂起的交互 + 闸门的修订号。<b>变更通知、首帧、<c>interactions.list</c> 都用这一个类型</b>(状态统一管理第 5 条:没有增量,收到就对账)。
/// </summary>
public sealed record InteractionsSnapshot(long Revision, IReadOnlyList<InteractionRequest> Pending)
{
    public int CountFor(RunId runId) => Pending.Count(p => p.RunId == runId);
}

public enum ConflictReason
{
    /// <summary>已被别人处理(或已被中止)。带 <see cref="RespondResult.Conflict.Resolution"/>。</summary>
    AlreadyResolved,

    /// <summary>仍在挂起,但控制端手里的版本不是当前版本。带 <see cref="RespondResult.Conflict.CurrentVersion"/>。</summary>
    StaleVersion,
}

/// <summary>
/// <c>interactions.respond</c> 的结果。协议层的映射:<see cref="Accepted"/> → 成功;<see cref="Conflict"/> → <c>conflict</c>(带处理者与时间);
/// <see cref="NotFound"/> → <c>not_found</c>(从没有过这个 id,或早已被历史淘汰);<see cref="Invalid"/> → <c>invalid_request</c>(交互仍然挂起)。
/// </summary>
public abstract record RespondResult
{
    private RespondResult()
    {
    }

    public sealed record Accepted(InteractionResolution Resolution) : RespondResult;

    public sealed record Conflict(ConflictReason Reason, InteractionResolution? Resolution = null, long? CurrentVersion = null) : RespondResult;

    public sealed record NotFound : RespondResult;

    public sealed record Invalid(string Reason) : RespondResult;

    public bool IsAccepted => this is Accepted;
}

/// <summary>发起交互被拒绝(不是用户拒绝):子代理不能提问、草稿不合法等。发给模型的是异常消息(英文)。</summary>
public sealed class InteractionRefusedException(string message) : InvalidOperationException(message);

/// <summary>交互日志(可选,注入;03 §4「挂起的交互持久化」):Storage 的实现可以把它落进 SQLite,重启后把仍是 pending 的行标为中止。闸门自己的裁决不依赖它。</summary>
public interface IInteractionJournal
{
    void Opened(InteractionRequest request);

    void Settled(InteractionResolution resolution);
}

/// <summary>写进 Run 事件流的交互事件(02 §3 的 <c>interaction_request</c>):编排层订阅 <see cref="InteractionGate.RunEvent"/>,按 Run 分配 <c>seq</c> 后并进事件流。</summary>
public sealed record InteractionRequestEvent(InteractionRequest Interaction) : AgentEvent;

/// <summary><c>interaction_resolved</c>。<see cref="Response"/> 只在 <see cref="ResolutionStatus.Answered"/> 时有值。</summary>
public sealed record InteractionResolvedEvent(InteractionResolution Resolution, InteractionResponse? Response) : AgentEvent;

/// <summary>
/// 交互闸门(03 §4;2.x <c>kernel/interaction-gate.ts</c> + 持久待决项 <c>device-interactions.ts</c> 的一次性转换)。
/// <list type="bullet">
/// <item><b>没有超时</b>:只有回答或中止能结束一次交互。取消令牌(Run 中止)和 <see cref="RunInteractions.End"/>(<c>run_end</c>)以中止拒绝,并发出 <see cref="InteractionResolvedEvent"/>(Aborted)。</item>
/// <item><b>多端先到先得</b>:<see cref="Respond"/> 在一把锁里原子地「判断仍挂起 → 移出」,只有一个回答者赢;后到的得到 <see cref="RespondResult.Conflict"/>(带谁、什么时候)。
/// 即使答案一模一样,第二次也失败——这是结构自带的性质,不靠比较答案。进程内这把锁就是 SQLite <c>UPDATE … WHERE status='pending'</c> 的等价物(挂起的交互活不过进程重启:重启后 Run 本来就以「被中断」收尾)。</item>
/// <item><b>可查询</b>:<see cref="Snapshot"/> 返回全量快照;变更通知 <see cref="Changed"/> 也是同一个全量类型,按修订号严格有序、串行派发。</item>
/// <item><b>子代理不能提问</b>:<see cref="OpenRun"/> 时 <c>depth &gt; 0</c> 的 Run 发起任何交互都被拒绝(而不是挂起——2.x 真实发生过的死锁:子代理的待决项在界面上走不到用户面前)。</item>
/// <item>回答要校验(数量、非空、选项匹配、修订必须带反馈、<c>allow_always</c> 只接受开放的作用域),校验失败不消耗这次交互。</item>
/// </list>
/// 线程安全。
/// </summary>
public sealed class InteractionGate
{
    private sealed class Entry(InteractionRequest request, TaskCompletionSource<InteractionResponse> completion)
    {
        public InteractionRequest Request { get; } = request;

        public TaskCompletionSource<InteractionResponse> Completion { get; } = completion;
    }

    private readonly Lock _lock = new();
    private readonly Dictionary<InteractionId, Entry> _pending = [];
    private readonly Dictionary<InteractionId, InteractionResolution> _history = [];
    private readonly Queue<InteractionId> _historyOrder = new();
    private readonly Dictionary<RunId, RunInteractions> _runs = [];
    private readonly Queue<Action> _outbox = new();
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _clock;
    private readonly IInteractionJournal? _journal;
    private readonly ILoopDiagnostics _log;
    private readonly int _historyLimit;
    private long _revision;
    private bool _draining;

    public InteractionGate(IIdGenerator ids, TimeProvider? clock = null, IInteractionJournal? journal = null, ILoopDiagnostics? log = null, int historyLimit = 512)
    {
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _clock = clock ?? TimeProvider.System;
        _journal = journal;
        _log = log ?? NullLoopDiagnostics.Instance;
        _historyLimit = Math.Max(1, historyLimit);
    }

    /// <summary>挂起的交互集合变了(登记、回答、中止):全量快照。订阅者在回调里收到的修订号严格递增。</summary>
    public event Action<InteractionsSnapshot>? Changed;

    /// <summary>写进 Run 事件流的 <c>interaction_request</c> / <c>interaction_resolved</c>(和 <see cref="Changed"/> 同一个有序队列派发)。</summary>
    public event Action<RunId, AgentEvent>? RunEvent;

    public long Revision
    {
        get
        {
            lock (_lock)
            {
                return _revision;
            }
        }
    }

    // ───────────────────────── Run 作用域 ─────────────────────────

    /// <summary>
    /// 为一个 Run 打开交互能力。<paramref name="depth"/> &gt; 0(子代理)返回的对象 <see cref="RunInteractions.CanInteract"/> 为 false:发起交互一律被拒绝。
    /// </summary>
    public RunInteractions OpenRun(RunId runId, int depth = 0)
    {
        lock (_lock)
        {
            if (_runs.ContainsKey(runId))
            {
                throw new InvalidOperationException($"Run {runId} already has an open interaction scope.");
            }

            var run = new RunInteractions(this, runId, depth);
            _runs[runId] = run;
            return run;
        }
    }

    // ───────────────────────── 查询 ─────────────────────────

    /// <summary>挂起的交互(全量)。<paramref name="runId"/> 非空只列这个 Run 的。按登记顺序。</summary>
    public InteractionsSnapshot Snapshot(RunId? runId = null)
    {
        lock (_lock)
        {
            return SnapshotLocked(runId);
        }
    }

    private InteractionsSnapshot SnapshotLocked(RunId? runId = null) =>
        new(_revision, _pending.Values
            .Where(e => runId is null || e.Request.RunId == runId)
            .Select(static e => e.Request)
            .OrderBy(static r => r.Version)
            .ToList());

    public InteractionRequest? Get(InteractionId id)
    {
        lock (_lock)
        {
            return _pending.TryGetValue(id, out var entry) ? entry.Request : null;
        }
    }

    /// <summary>已结束交互的去向(最近 N 条)。</summary>
    public InteractionResolution? GetResolution(InteractionId id)
    {
        lock (_lock)
        {
            return _history.GetValueOrDefault(id);
        }
    }

    /// <summary>某个 Run 挂起的交互数(计入 <c>activeRuns</c> 广播;含它自己登记的,子代理不能挂起所以没有后代项)。</summary>
    public int PendingCount(RunId runId)
    {
        lock (_lock)
        {
            return _pending.Values.Count(e => e.Request.RunId == runId);
        }
    }

    // ───────────────────────── 回答 ─────────────────────────

    /// <summary>
    /// 回答一次交互(<c>interactions.respond { id, expectedVersion, response }</c>)。<paramref name="responder"/> 是回答者(已认证控制端的标识),进「谁处理的」。
    /// 判断和移出在同一把锁里:两个控制端同时回答,只有第一个成功。
    /// </summary>
    public RespondResult Respond(InteractionId id, long expectedVersion, InteractionResponse response, string responder)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(responder);
        Entry entry;
        InteractionResolution resolution;
        lock (_lock)
        {
            if (!_pending.TryGetValue(id, out entry!))
            {
                return _history.TryGetValue(id, out var past)
                    ? new RespondResult.Conflict(ConflictReason.AlreadyResolved, past)
                    : new RespondResult.NotFound();
            }

            if (entry.Request.Version != expectedVersion)
            {
                return new RespondResult.Conflict(ConflictReason.StaleVersion, CurrentVersion: entry.Request.Version);
            }

            if (entry.Request.Validate(response) is { } problem)
            {
                return new RespondResult.Invalid(problem);
            }

            resolution = SettleLocked(entry, ResolutionStatus.Answered, responder, response);
        }

        // ★ 先派发通知(interaction_resolved 并进 Run 的事件流),再放行等待者:反过来的话,Run 可能在这条事件进流之前就继续跑、甚至已经结束,
        // 事件要么排在后续事件之后,要么被「终态之后的事件一律丢弃」丢掉(中止路径上必现:取消令牌一触发,循环就开始收尾)。
        Drain();
        entry.Completion.TrySetResult(response);
        return new RespondResult.Accepted(resolution);
    }

    // ───────────────────────── 清理 ─────────────────────────

    /// <summary>
    /// 一个 Run 结束(<c>run_end</c>):清理它挂起的交互并关闭它的作用域(之后再发起会被拒绝)。
    /// <see cref="RunStatus.Done"/> 时 <see cref="InteractionRequest.SurvivesNormalRunEnd"/> 的交互(目标提案)留着。幂等。
    /// </summary>
    internal void EndRun(RunInteractions run, RunStatus status)
    {
        List<Entry> doomed;
        lock (_lock)
        {
            if (!run.MarkClosed())
            {
                return;
            }

            _runs.Remove(run.RunId);
            doomed = _pending.Values.Where(e => e.Request.RunId == run.RunId && !(status == RunStatus.Done && e.Request.SurvivesNormalRunEnd)).ToList();
            foreach (var entry in doomed)
            {
                SettleLocked(entry, ResolutionStatus.Aborted, null, null);
            }
        }

        Drain(); // 先通知,再放行等待者(理由见 Respond)
        foreach (var entry in doomed)
        {
            entry.Completion.TrySetCanceled();
        }
    }

    /// <summary>按条件中止挂起的交互(例如会话删除时取消目标提案)。返回中止的个数。</summary>
    public int AbortWhere(Func<InteractionRequest, bool> predicate)
    {
        List<Entry> doomed;
        lock (_lock)
        {
            doomed = _pending.Values.Where(e => predicate(e.Request)).ToList();
            foreach (var entry in doomed)
            {
                SettleLocked(entry, ResolutionStatus.Aborted, null, null);
            }
        }

        Drain(); // 先通知,再放行等待者(理由见 Respond)
        foreach (var entry in doomed)
        {
            entry.Completion.TrySetCanceled();
        }

        return doomed.Count;
    }

    /// <summary>中止所有挂起的交互(Agent 关闭时)。</summary>
    public int AbortAll() => AbortWhere(static _ => true);

    // ───────────────────────── 发起 ─────────────────────────

    internal async Task<InteractionResponse> RequestAsync(RunInteractions run, InteractionRequest draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ct.ThrowIfCancellationRequested();
        if (!run.CanInteract)
        {
            // 子代理没有人可问:当场拒绝,而不是挂起(挂起就永远不会结算)。与「快照过滤 + 不提供 Interact」是对称的三道闸。
            throw new InteractionRefusedException("A subagent cannot ask the user. Either do this step without the interaction, or report back that the parent agent must handle it.");
        }

        if (draft.ValidateDraft() is { } invalid)
        {
            throw new ArgumentException(invalid, nameof(draft));
        }

        var completion = new TaskCompletionSource<InteractionResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        Entry entry;
        lock (_lock)
        {
            if (run.IsClosed)
            {
                throw new InteractionRefusedException("The run is no longer active, so it cannot ask the user.");
            }

            var version = ++_revision;
            var request = draft with { Id = new InteractionId(_ids.NewId()), RunId = run.RunId, CreatedAt = _clock.GetUtcNow(), Version = version };
            entry = new Entry(request, completion);
            _pending[request.Id] = entry;
            var snapshot = SnapshotLocked();
            _outbox.Enqueue(() =>
            {
                Safely(() => _journal?.Opened(request), "journal");
                Safely(() => RunEvent?.Invoke(request.RunId, new InteractionRequestEvent(request)), "run event");
                Safely(() => Changed?.Invoke(snapshot), "change notification");
            });
        }

        Drain();
        using var registration = ct.Register(static state =>
        {
            var (gate, e, token) = ((InteractionGate, Entry, CancellationToken))state!;
            gate.AbortEntry(e, token);
        }, (this, entry, ct));
        return await completion.Task.ConfigureAwait(false);
    }

    private void AbortEntry(Entry entry, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_pending.ContainsKey(entry.Request.Id))
            {
                return; // 已经被回答或被别的路径中止
            }

            SettleLocked(entry, ResolutionStatus.Aborted, null, null);
        }

        // ★ 先派发 interaction_resolved(Aborted),再放行等待者:取消令牌触发时,等待者一放行循环就开始收尾并发 run_end,
        // 晚一步的 interaction_resolved 会被「终态之后的事件一律丢弃」丢掉。
        Drain();
        entry.Completion.TrySetCanceled(ct);
    }

    // ───────────────────────── 内部 ─────────────────────────

    /// <summary>在锁内把一个挂起项移出、记历史、排好通知。调用方负责之后结算它的 TCS 并 <see cref="Drain"/>。</summary>
    private InteractionResolution SettleLocked(Entry entry, ResolutionStatus status, string? by, InteractionResponse? response)
    {
        var request = entry.Request;
        _pending.Remove(request.Id);
        var revision = ++_revision;
        var resolution = new InteractionResolution(request.Id, request.RunId, request.Kind, request.Version, status, by, _clock.GetUtcNow(), revision);
        _history[request.Id] = resolution;
        _historyOrder.Enqueue(request.Id);
        while (_historyOrder.Count > _historyLimit)
        {
            _history.Remove(_historyOrder.Dequeue());
        }

        var snapshot = SnapshotLocked();
        _outbox.Enqueue(() =>
        {
            Safely(() => _journal?.Settled(resolution), "journal");
            Safely(() => RunEvent?.Invoke(request.RunId, new InteractionResolvedEvent(resolution, request.RedactsResponse ? null : response)), "run event");
            Safely(() => Changed?.Invoke(snapshot), "change notification");
        });
        return resolution;
    }

    /// <summary>串行派发通知:同一时刻只有一个线程在跑回调,所以订阅者看到的修订号严格递增,回调里再触发变更也不会重入。</summary>
    private void Drain()
    {
        lock (_lock)
        {
            if (_draining)
            {
                return;
            }

            _draining = true;
        }

        for (;;)
        {
            Action next;
            lock (_lock)
            {
                if (!_outbox.TryDequeue(out next!))
                {
                    _draining = false;
                    return;
                }
            }

            next();
        }
    }

    private void Safely(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            // 界面 / 日志的通知失败不能改变裁决。
            _log.Warn($"Interaction {what} handler failed.", e);
        }
    }
}

/// <summary>
/// 一个 Run 的交互句柄(03 §1 的 <c>Interact</c> 能力)。<b>子代理拿到的 <see cref="CanInteract"/> 为 false</b>,会话层据此不把它放进工具上下文(能力不可用时直接不提供)。
/// </summary>
public sealed class RunInteractions
{
    private readonly InteractionGate _gate;
    private bool _closed;

    internal RunInteractions(InteractionGate gate, RunId runId, int depth)
    {
        _gate = gate;
        RunId = runId;
        Depth = depth;
    }

    public RunId RunId { get; }

    public int Depth { get; }

    /// <summary>只有顶层 Run 能问人。</summary>
    public bool CanInteract => Depth == 0;

    internal bool IsClosed => Volatile.Read(ref _closed);

    internal bool MarkClosed()
    {
        if (_closed)
        {
            return false;
        }

        _closed = true;
        return true;
    }

    /// <summary>
    /// 发起一次交互并等待回答。没有超时。<paramref name="ct"/>(Run 的中止令牌)触发 → 抛 <see cref="OperationCanceledException"/>,交互以中止收尾;
    /// <see cref="End"/> 也会让等待的调用以 <see cref="OperationCanceledException"/> 结束。回答的类型和请求匹配(<see cref="InteractionRequest.Validate"/> 保证)。
    /// </summary>
    public Task<InteractionResponse> RequestAsync(InteractionRequest draft, CancellationToken ct = default) => _gate.RequestAsync(this, draft, ct);

    /// <summary>Run 结束(<c>run_end</c>):清理它挂起的交互并关闭作用域。幂等。</summary>
    public void End(RunStatus status) => _gate.EndRun(this, status);
}
