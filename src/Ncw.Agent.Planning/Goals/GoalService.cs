using System.Globalization;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Runs;
using Ncw.Agent.Llm;
using Ncw.Core.Domain;

namespace Ncw.Agent.Extensions.Planning.Goals;

/// <summary>
/// 会话目标的运行时(2.x <c>goal/runtime.ts</c> + <c>ipc/goal.ts</c> 的业务部分):目标的设定 / 清除 / 恢复、转录标记、回合末判定接线、空转刹车、后台推迟与空闲唤醒、模型自提目标。
/// <para>
/// 真源在 Agent(AGENTS.md §3 规则 1):目标是进程内状态(<see cref="GoalStore"/>),落盘的只有转录里的 <c>goal_status</c> 标记。控制端通过 <see cref="Changed"/>(全量 <see cref="GoalChange"/>)镜像,
/// 首帧(<see cref="Get"/>)和后续变更同一个类型。线程安全:状态在一把锁里,判定(<c>await</c>)和投递在锁外。
/// </para>
/// </summary>
public sealed partial class GoalService : IDisposable
{
    private readonly GoalOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, GoalRunContext> _contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITimer> _timers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Attempts, DateTimeOffset RetryAt)> _checkinFailures = new(StringComparer.Ordinal);

    public GoalService(GoalOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Store = new GoalStore((message, error) => _options.Log.Warn(message, error));
        Markers = new GoalMarkers(options.Transcript, options.Ids, options.Clock, (message, error) => _options.Log.Warn(message, error));
        Store.Changed += OnStoreChanged;
    }

    public GoalStore Store { get; }

    public GoalMarkers Markers { get; }

    /// <summary>目标变了(设定 / 轮数 / 清除 / 带外标记):全量 <see cref="GoalChange"/>。Host 据此推 <c>goal</c> 主题。</summary>
    public event Action<GoalChange>? Changed;

    private DateTimeOffset Now => _options.Clock.GetUtcNow();

    private void OnStoreChanged(GoalChange change)
    {
        if (change.Goal is null)
        {
            // 目标没了:它的定时器、重试状态和 Run 上下文跟着走
            lock (_gate)
            {
                ClearTimerLocked(change.SessionId);
                _checkinFailures.Remove(change.SessionId);
                _contexts.Remove(change.SessionId);
            }
        }

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
                _options.Log.Warn("A goal change subscriber threw.", e);
            }
        }
    }

    private void Log(string name, params (string Key, object? Value)[] fields) =>
        _options.Telemetry?.Invoke(name, fields.ToDictionary(f => f.Key, f => Convert.ToString(f.Value, CultureInfo.InvariantCulture) ?? "", StringComparer.Ordinal));

    // ───────────────────────── 查询与恢复 ─────────────────────────

    /// <summary>
    /// 幂等的重载恢复:<c>origin = restored</c>、轮数 0、不 kickoff、不挂定时器。★ 历史<b>只在第一次</b>才读,且只读最后一条带标记的助手消息——
    /// 打开长会话时整段转录解析一遍,会让首页历史迟迟回不来。
    /// </summary>
    public ActiveGoal? Restore(string sessionId)
    {
        if (Store.WasInitialized(sessionId))
        {
            return Store.Get(sessionId);
        }

        Store.MarkInitialized(sessionId);
        var last = _options.Transcript.LastWithGoalStatus(sessionId);
        if (last is not null && GoalRestore.RestorableCondition([last.Message]) is { } condition)
        {
            Activate(sessionId, condition, GoalOrigin.Restored);
        }

        return Store.Get(sessionId);
    }

    /// <summary>当前的目标(首帧,<c>goal.get</c>)。先恢复。</summary>
    public ActiveGoal? Get(string sessionId) => Restore(sessionId);

    // ───────────────────────── 设定与清除 ─────────────────────────

    /// <summary>用户手动设目标(<c>goal.set</c>):校验 → 撤掉待决的提案 → 生效 → 把 kickoff 当内部输入交付(Run 在跑进它的引导队列,空闲就起一个内部 Run)。</summary>
    public GoalSetResult SetByUser(string sessionId, string condition)
    {
        var normalized = GoalConditions.Normalize(condition);
        if (normalized.Length == 0)
        {
            return new GoalSetResult.Invalid(GoalInvalidReason.Empty, 0);
        }

        if (normalized.Length > GoalConditions.Max)
        {
            return new GoalSetResult.Invalid(GoalInvalidReason.TooLong, normalized.Length);
        }

        CancelProposals(sessionId);
        var kickoff = Activate(sessionId, normalized, GoalOrigin.User);
        var goal = Store.Get(sessionId);
        if (goal is null || kickoff is null)
        {
            return new GoalSetResult.Invalid(GoalInvalidReason.Empty, 0);
        }

        var delivery = _options.Deliver(sessionId, new InternalInput(kickoff, Options: null, GoalId: goal.Id));
        return new GoalSetResult.Ok(goal, delivery);
    }

    /// <summary>用户清除目标(<c>goal.clear</c>)。先恢复(清除的是「转录里那个」,不是「内存里恰好没有」),撤掉待决的提案。</summary>
    public void Clear(string sessionId)
    {
        Restore(sessionId);
        CancelProposals(sessionId);
        Deactivate(sessionId, GoalClearReason.UserClear);
    }

    /// <summary>
    /// 目标生效。返回 kickoff 的正文(发给主模型的内部消息);条件为空 / 超长 / 是恢复返回 <c>null</c>。每次设定都换一个新身份——条件一字不改也算换了一个目标。
    /// 同时盖「已设定」标记(挂在最后一条助手消息上,不新增消息;还没有助手消息就先排队)。
    /// </summary>
    public IReadOnlyList<Part>? Activate(string sessionId, string condition, GoalOrigin origin)
    {
        var normalized = GoalConditions.Normalize(condition);
        if (normalized.Length == 0 || normalized.Length > GoalConditions.Max)
        {
            return null;
        }

        var now = Now;
        if (Store.Get(sessionId) is { } previous)
        {
            Log("goal_cleared", ("reason", "superseded"), ("iterations", previous.Iterations), ("origin", OriginName(previous.Origin)), ("durationMs", Math.Max(0, (now - previous.SetAt).TotalMilliseconds)));
        }

        // 替换目标时保留这个 Run 的协调口(上下文),Store.Set 清旧目标会触发清理
        GoalRunContext? context;
        lock (_gate)
        {
            _contexts.TryGetValue(sessionId, out context);
        }

        var goal = new ActiveGoal(_options.Ids.NewId(), normalized, origin, now, _options.SessionTokens(sessionId));
        Store.Set(sessionId, goal);
        if (context is not null)
        {
            lock (_gate)
            {
                _contexts[sessionId] = context;
            }
        }

        Log("goal_set", ("promptLength", normalized.Length), ("origin", OriginName(origin)));
        if (origin == GoalOrigin.Restored)
        {
            return null;
        }

        RecordMarker(sessionId, new GoalStatusPart(false, normalized, Set: true, Origin: OriginName(origin), Iterations: 0));
        return [new TextPart(GoalPrompts.KickoffMessage(normalized))];
    }

    /// <summary>
    /// 清除目标。只有用户清除盖「已清除」标记(<b>活过</b>收尾:恢复读最后一条标记,被合并掉的话,用户清掉的目标下次打开会自己回来);
    /// 关会话 / 退出不是用户清除,持久的活跃标记留给下次启动。
    /// </summary>
    public GoalStatusPart? Deactivate(string sessionId, GoalClearReason reason)
    {
        var goal = Store.Clear(sessionId, reason);
        if (goal is null)
        {
            return null;
        }

        var durationMs = (long)Math.Max(0, (Now - goal.SetAt).TotalMilliseconds);
        Log("goal_cleared", ("reason", ReasonName(reason)), ("iterations", goal.Iterations), ("durationMs", durationMs), ("origin", OriginName(goal.Origin)));
        var part = new GoalStatusPart(false, goal.Condition, Cleared: true, Iterations: goal.Iterations, DurationMs: durationMs,
            Tokens: reason == GoalClearReason.UserClear ? GoalTokens(sessionId, goal) : goal.Tokens);
        if (reason == GoalClearReason.UserClear)
        {
            RecordMarker(sessionId, part);
        }

        return part;
    }

    private long GoalTokens(string sessionId, ActiveGoal goal) => Math.Max(0, _options.SessionTokens(sessionId) - goal.TokensAtStart);

    private void RecordMarker(string sessionId, GoalStatusPart part)
    {
        if (Markers.Record(sessionId, part) is { } message)
        {
            Store.NotifyMarker(sessionId, message);
        }
    }

    /// <summary>有助手消息可以挂了:落上排着的标记(回合末、Run 结束时调)。</summary>
    public void FlushMarkers(string sessionId)
    {
        if (Markers.FlushPending(sessionId) is { } message)
        {
            Store.NotifyMarker(sessionId, message);
        }
    }

    /// <summary>把回合末判定的标记写上去(只经这一条路,运行时自己不写两遍)。</summary>
    public void AttachStatus(string sessionId, GoalStatusPart status) => RecordMarker(sessionId, status);

    // ───────────────────────── Run 绑定 ─────────────────────────

    /// <summary>
    /// 主 Run 开始(<c>before_run</c>):绑定这个 Run 的上下文,清掉空闲定时器(这个 Run 自己的回合末会重新算)。
    /// ★ 用户开口那一轮(<paramref name="userMessage"/>)把空闲计数归零——用户回来了,三次空闲检查的额度重新开始;内部输入不归零。
    /// </summary>
    public void BindRun(string sessionId, GoalRunContext context, bool userMessage)
    {
        lock (_gate)
        {
            _checkinFailures.Remove(sessionId);
            _contexts[sessionId] = context;
            ClearTimerLocked(sessionId);
        }

        if (Store.Get(sessionId) is null)
        {
            return;
        }

        if (userMessage)
        {
            Store.Update(sessionId, g => g with { IdleCheckinCount = 0 });
        }
    }

    /// <summary>
    /// 手动中止 / 出错:暂停唤醒,但保留条件给用户的下一轮。★ 轮换目标 id:已经在路上的检查(引导队列里、投递中)和还没回来的判定,拿着的是旧 id,各层都把它们丢掉,又不清掉条件。
    /// </summary>
    public void Pause(string sessionId)
    {
        lock (_gate)
        {
            ClearTimerLocked(sessionId);
            _checkinFailures.Remove(sessionId);
            _contexts.Remove(sessionId);
        }

        if (Store.Get(sessionId) is not null)
        {
            Store.Update(sessionId, g => g with { Id = _options.Ids.NewId() });
        }
    }

    /// <summary>Run 正常收尾:不在推迟态就放掉定时器和上下文;推迟态要留着(后台还在跑,空闲唤醒要用它们)。</summary>
    public void Release(string sessionId)
    {
        if (Store.Get(sessionId)?.DeferredSince is not null)
        {
            return;
        }

        lock (_gate)
        {
            ClearTimerLocked(sessionId);
            _contexts.Remove(sessionId);
        }
    }

    /// <summary>会话结束(删除 / 退出):清目标(不盖标记)、停唤醒、丢排着的标记。</summary>
    public void EndSession(string sessionId)
    {
        Deactivate(sessionId, GoalClearReason.SessionEnded);
        Pause(sessionId);
        Markers.Forget(sessionId);
    }

    public void StopAll()
    {
        List<string> sessions;
        lock (_gate)
        {
            sessions = [.. Store.All().Keys.Concat(_contexts.Keys).Concat(_timers.Keys).Distinct(StringComparer.Ordinal)];
        }

        foreach (var sessionId in sessions)
        {
            EndSession(sessionId);
        }
    }

    public void Dispose() => StopAll();

    /// <summary>此刻挂着的定时器数(测试和诊断用)。</summary>
    public int TimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    // ───────────────────────── Run 句柄(GoalRunExtension 登记) ─────────────────────────

    private readonly Dictionary<RunId, RunHandle> _handles = [];
    private readonly HashSet<RunId> _abortedRuns = [];

    internal void NoteRunCreated(RunHandle run)
    {
        lock (_gate)
        {
            _handles[run.RunId] = run;
        }
    }

    internal RunHandle? HandleOf(RunId runId)
    {
        lock (_gate)
        {
            return _handles.GetValueOrDefault(runId);
        }
    }

    /// <summary>Run 收尾:句柄不再留着,但「被中止过」要记住(批准条在 Run 正常收尾之后仍然有效,在中止之后不)。最多记 256 个。</summary>
    internal void NoteRunSettled(RunHandle run)
    {
        lock (_gate)
        {
            _handles.Remove(run.RunId);
            if (run.AbortRequested || run.State == RunState.Aborted)
            {
                if (_abortedRuns.Count >= 256)
                {
                    _abortedRuns.Clear();
                }

                _abortedRuns.Add(run.RunId);
            }
        }
    }

    private bool IsLive(RunId runId)
    {
        if (_options.RunIsLive is { } custom)
        {
            return custom(runId);
        }

        var handle = HandleOf(runId);
        return handle is null || handle is { State: RunState.Running, AbortRequested: false };
    }

    private bool WasAborted(RunId runId)
    {
        if (_options.RunAborted is { } custom)
        {
            return custom(runId);
        }

        lock (_gate)
        {
            return _abortedRuns.Contains(runId) || (_handles.TryGetValue(runId, out var handle) && handle.AbortRequested);
        }
    }

    internal TimeProvider Clock => _options.Clock;

    internal string NewId() => _options.Ids.NewId();

    internal ModelRef? FreezeEvaluatorModel() => _options.EvaluatorModel();

    internal GoalRunContext? ContextOf(string sessionId)
    {
        lock (_gate)
        {
            return _contexts.GetValueOrDefault(sessionId);
        }
    }

    private void ClearTimerLocked(string sessionId)
    {
        if (_timers.Remove(sessionId, out var timer))
        {
            timer.Dispose();
        }
    }

    internal static string OriginName(GoalOrigin origin) => origin switch
    {
        GoalOrigin.User => "user",
        GoalOrigin.ProposalDirect => "proposal_direct",
        GoalOrigin.ProposalApproved => "proposal_approved",
        _ => "restored",
    };

    internal static string ReasonName(GoalClearReason reason) => reason switch
    {
        GoalClearReason.UserClear => "user_clear",
        GoalClearReason.Superseded => "superseded",
        GoalClearReason.Met => "met",
        GoalClearReason.Impossible => "impossible",
        GoalClearReason.SessionEnded => "session_ended",
        _ => "run_failed",
    };
}
