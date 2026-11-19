using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;
using Ncw.Agent.Tools;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Runs;

/// <summary>注册表的构造参数。时钟、ID 可注入;测试用假驱动 / 假工厂 / 手动时钟。</summary>
public sealed class RunRegistryDeps
{
    /// <summary>设备 id:权威广播的 key(<c>(deviceId, runId)</c>)。</summary>
    public required string DeviceId { get; init; }

    public required SessionRepository Sessions { get; init; }

    public required IRunDriver Driver { get; init; }

    public required IRunRequestFactory Requests { get; init; }

    public required IQueueStore Queue { get; init; }

    /// <summary>只用来在排队的消息入队前归一化文件引用(<c>AgentSession.NormalizeInputAsync</c>);没有就原样入队。</summary>
    public ISessionDirectory? SessionDirectory { get; init; }

    public IIdGenerator Ids { get; init; } = new UlidIdGenerator();

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public ILoopDiagnostics Log { get; init; } = NullLoopDiagnostics.Instance;

    public RunRegistryOptions Options { get; init; } = new();

    public IReadOnlyList<IRunExtension> Extensions { get; init; } = [];

    public IInteractionView? Interactions { get; init; }

    /// <summary>后台 Shell 注册表(工具层)。Run 结束 / 中止不杀后台 Shell(那正是它的用途);应用退出和会话删除时才清理(05 §8)。</summary>
    public BackgroundShells? Shells { get; init; }
}

/// <summary>「订阅 + 快照」的结果:根 Run 的快照和它当前所有后代的快照,订阅已挂在它们全部身上(之后的新后代自动继承)。</summary>
public sealed record AttachResult(RunSnapshot Root, IReadOnlyList<RunSnapshot> Descendants);

/// <summary>删除会话前的活体检查(2.x 的 <c>sessionActivity</c>):按真实状态分类,而不是一个布尔值。</summary>
public sealed record SessionActivity(
    IReadOnlyList<RunHandle> Live,
    IReadOnlyList<RunHandle> Stopping,
    IReadOnlyList<string> Finalizing,
    IReadOnlyList<string> Operations);

/// <summary>
/// 编排层的核心(05):Run 的创建、运行、终态、settle,会话互斥,事件日志与订阅,<c>activeRuns</c> 全量权威广播,排队消息。
/// <para>
/// <b>Launch 顺序</b>:创建 → 取会话互斥 → 绑订阅(启动时绑定的 + 继承父 Run 的)→ 记录 Run 开始 → 广播 → 驱动(<c>Task</c>)→ <c>finally</c> 里 settle(只执行一次):
/// 记录 Run 结束 → <b>释放会话互斥 → 发 <c>run_settled</c></b> → 扩展 → 续跑排队消息 → 回收。
/// 会话层(<c>AgentSession</c>)自己也会发一个 <c>run_settled</c>,但那时注册表的互斥还没放,所以编排层把它拦下,自己在释放互斥之后再发(R8:下一个 Run 只等这个事件)。
/// </para>
/// <para>
/// <b>权威广播</b>(状态统一管理规则 5):Run 创建、<c>run_end</c>、每次交互发起和处理,都全量重播一次 <see cref="ActiveRunIndex"/>(顶层 + 子 Run,
/// 类型与首帧 <see cref="ActiveRuns"/> 相同,由 Core 里<b>一个</b>生成函数 <see cref="ActiveRunIndexBuilder"/> 派生)。控制端丢了任何一条结束事件,都能被这条广播收口。
/// </para>
/// 线程安全。共享结构(Run 表、互斥表)由一把锁维护,不在锁里调用任何回调;对外查询返回不可变快照。
/// </summary>
public sealed class RunRegistry : IRunHandleHost, IQueueHost, IDisposable
{
    private sealed record Reservation(object Token, RunId? Run);

    private readonly object _gate = new();
    private readonly Dictionary<RunId, RunHandle> _runs = [];
    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<RunId, ITimer> _watchdogs = [];
    private readonly object _buildGate = new();
    private readonly Queue<ActiveRunIndex> _outbox = new();
    private readonly RunRegistryDeps _deps;
    private readonly RunRegistryOptions _options;
    private readonly SessionQueues _queues;
    private readonly ITimer _reapTimer;
    private readonly ITimer? _rebroadcastTimer;
    private long _activeRev;
    private bool _draining;
    private bool _shuttingDown;
    private bool _disposed;

    public RunRegistry(RunRegistryDeps deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        _deps = deps;
        _options = deps.Options;
        _queues = new SessionQueues(deps.Sessions, deps.Queue, deps.Requests, deps.Ids, deps.Clock, deps.Log, deps.Options, this);
        _queues.Changed += update => RaiseEach(QueueChanged, update, "A queue subscriber threw.");
        _reapTimer = deps.Clock.CreateTimer(static state => ((RunRegistry)state!).ReapFinished(), this, _options.ReapInterval, _options.ReapInterval);
        if (_options.ActiveRebroadcast > TimeSpan.Zero)
        {
            _rebroadcastTimer = deps.Clock.CreateTimer(static state => ((RunRegistry)state!).RebroadcastIfRunning(), this, _options.ActiveRebroadcast, _options.ActiveRebroadcast);
        }
    }

    public string DeviceId => _deps.DeviceId;

    /// <summary>
    /// 「谁在跑」变了:全量的 <see cref="ActiveRunIndex"/>(带单调的 <c>Rev</c>,按序派发,订阅者收到的 Rev 严格递增)。
    /// 它不依赖任何 Run 订阅:没订阅过的 Run(定时任务起的、重启后还没打开的会话)结束时,控制端同样被它收口。回调在派发线程上串行执行。
    /// </summary>
    public event Action<ActiveRunIndex>? ActiveRunsChanged;

    /// <summary>排队消息变了(<c>queue_update</c>):全量快照,按全局 <c>Rev</c> 有序。</summary>
    public event Action<QueueUpdate>? QueueChanged;

    /// <summary>一个已结束的 Run 被回收了:Host 据此摘掉挂在它身上的主题 / 引用。</summary>
    public event Action<RunHandle>? RunReaped;

    private ILoopDiagnostics Log => _deps.Log;

    TimeProvider IRunHandleHost.Clock => _deps.Clock;

    ILoopDiagnostics IRunHandleHost.Log => _deps.Log;

    // ───────────────────────── 启动 ─────────────────────────

    /// <summary>
    /// 所有入口的唯一发起点(05 §1)。被拒绝(会话忙、RunId 重复、正在退出)时返回 <see cref="LaunchResult.Refused"/>,不抛、不留任何半成品。
    /// 子代理(<see cref="RunLaunch.ParentRunId"/>)先登记到父 Run、继承父 Run 的订阅,再启动。
    /// </summary>
    public LaunchResult Launch(RunLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.SessionId.Length == 0 || launch.RunId.Value.Length == 0)
        {
            return new LaunchResult.Refused(RunRefusal.InvalidRequest, "A run needs a run id and a session id.");
        }

        RunHandle handle;
        lock (_gate)
        {
            if (_shuttingDown || _disposed)
            {
                return new LaunchResult.Refused(RunRefusal.ShuttingDown, "The agent is shutting down.");
            }

            // ★ 重复 runId 拒绝:静默复用会让两个 Run 共享转录。
            if (_runs.ContainsKey(launch.RunId))
            {
                return new LaunchResult.Refused(RunRefusal.DuplicateRunId, $"Run {launch.RunId} already exists.");
            }

            if (_reservations.ContainsKey(launch.SessionId))
            {
                return new LaunchResult.Refused(RunRefusal.SessionBusy, $"Session {launch.SessionId} has a run or an operation in progress.");
            }

            handle = new RunHandle(launch, this, _options.LogMaxEntries, _options.LogMaxBytes);
            _runs[launch.RunId] = handle;
            _reservations[launch.SessionId] = new Reservation(new object(), launch.RunId);

            // 先订阅,再启动:启动时绑定的订阅 + 子代理继承父 Run 的订阅,都在第一个事件之前挂好。
            foreach (var sub in launch.Subscriptions)
            {
                handle.AddSubscriber(sub);
            }

            if (launch.ParentRunId is { } parentId && _runs.TryGetValue(parentId, out var parent))
            {
                parent.AddChild(launch.RunId);
                foreach (var sub in parent.SubscribersSnapshot())
                {
                    handle.AddSubscriber(sub);
                }
            }
        }

        RecordRun(handle, "running", endedAt: null);
        foreach (var ext in _deps.Extensions)
        {
            Guard(() => ext.OnCreated(handle), "OnCreated");
        }

        BroadcastActive();
        _queues.OnRunStarted(handle, launch);

        var request = Augment(handle, launch.Request);
        handle.Completion = Task.Run(() => DriveAsync(handle, request));
        return new LaunchResult.Started(handle);
    }

    /// <summary>
    /// 用户发送。会话已有运行中的顶层 Run 时,这句话进队列(用户按的是回车,要的是「这句话会被发出去」,而不是一个「会话忙」);
    /// 否则起一个新 Run。开放网关(<see cref="RunOrigin.Gateway"/>)不排队,会话忙就拒绝。
    /// </summary>
    public async Task<SendResult> SendAsync(UserSend send)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (send.Origin == RunOrigin.User && ActiveTopLevelRun(send.SessionId) is not null)
        {
            var parts = await NormalizeAsync(send.SessionId, send.WorkspaceId, send.Parts).ConfigureAwait(false);
            return new SendResult.Queued(_queues.Enqueue(send.SessionId, send.MessageId, parts, send.Options));
        }

        var message = new Message(send.MessageId, Role.User, send.Parts, _deps.Clock.GetUtcNow());
        RunRequest request;
        try
        {
            request = _deps.Requests.Create(new RunTemplate(send.RunId, send.SessionId, send.WorkspaceId, [message], send.Options, send.Origin));
        }
        catch (Exception e)
        {
            Log.Warn("The run request could not be assembled.", e);
            return new SendResult.Refused(RunRefusal.InvalidRequest, e.Message);
        }

        var result = Launch(new RunLaunch(send.SessionId, send.WorkspaceId, request)
        {
            Origin = send.Origin,
            Options = send.Options,
            Subscriptions = send.Subscriptions,
            PermissionMode = send.PermissionMode.Length > 0 ? send.PermissionMode : send.Options.PermissionMode,
        });
        return result switch
        {
            LaunchResult.Started started => new SendResult.StartedRun(started.Handle),
            LaunchResult.Refused refused => new SendResult.Refused(refused.Reason, refused.Detail),
            _ => throw new InvalidOperationException(),
        };
    }

    private async ValueTask<IReadOnlyList<Part>> NormalizeAsync(string sessionId, string workspaceId, IReadOnlyList<Part> parts)
    {
        if (_deps.SessionDirectory is not { } directory || !parts.Any(p => p is FileRefPart))
        {
            return parts;
        }

        try
        {
            return await directory.Get(sessionId, workspaceId).NormalizeInputAsync(parts).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warn("The queued input could not be normalised; queueing it as typed.", e);
            return parts;
        }
    }

    /// <summary>编排层的两个钩子:引导队列(顶层 Run 接会话队列)和工具上下文(依次套各扩展)。其余原样交给会话层。</summary>
    private RunRequest Augment(RunHandle handle, RunRequest request)
    {
        var innerSteering = request.TakeSteering;
        Func<IReadOnlyList<Message>>? steering = handle.IsTopLevel
            ? () =>
            {
                var queued = _queues.TakeSteering(handle);
                IReadOnlyList<Message> extra = innerSteering is null ? [] : innerSteering();
                return extra.Count == 0 ? queued : [.. queued, .. extra];
            }
        : innerSteering;
        var innerEnrich = request.EnrichToolContext;
        ToolContext Enrich(ToolContext context)
        {
            foreach (var ext in _deps.Extensions)
            {
                try
                {
                    context = ext.EnrichToolContext(handle, context);
                }
                catch (Exception e)
                {
                    Log.Warn("A run extension threw while enriching the tool context.", e);
                }
            }

            return innerEnrich is null ? context : innerEnrich(context);
        }

        var innerAbandoned = request.IsAbandoned;
        return request with
        {
            TakeSteering = steering,
            EnrichToolContext = Enrich,
            // 会话层的每个落库点据此判断「这个 Run 已被看门狗 / 删除会话强收」(05 §12)。
            IsAbandoned = innerAbandoned is null ? () => handle.Abandoned : () => handle.Abandoned || innerAbandoned(),
        };
    }

    // ───────────────────────── 驱动与 settle ─────────────────────────

    private async Task DriveAsync(RunHandle handle, RunRequest request)
    {
        try
        {
            var outcome = await _deps.Driver.RunAsync(handle, request, new HandleSink(handle), handle.Token).ConfigureAwait(false);
            if (!outcome.Started)
            {
                // 没开始(会话不存在 / 会话层的互斥):没有任何事件,补一个自洽的 run_start → run_end(error)。
                handle.Finish(RunStatus.Error, outcome.Error ?? AgentError.Create(AgentErrorCode.Unknown, "The run did not start.", false, messageKey: RunErrorKeys.NotStarted));
            }
            else if (handle.State == RunState.Running)
            {
                // 驱动没有发 run_end 就返回了(不守规矩的驱动):以它的结局收口。
                handle.Finish(outcome.Status, outcome.Error);
            }
        }
        catch (Exception e)
        {
            if (handle.Token.IsCancellationRequested && e is OperationCanceledException)
            {
                handle.Finish(RunStatus.Aborted);
            }
            else
            {
                Log.Warn($"The driver of run {handle.RunId} failed.", e);
                handle.Finish(RunStatus.Error, AgentError.Create(AgentErrorCode.Unknown, $"Unexpected error in the run driver: {e.Message}", false, messageKey: RunErrorKeys.Internal));
            }
        }
        finally
        {
            // ★ 无论如何都收尾:否则会话互斥永远不放,排队的永远发不出去。
            if (handle.State == RunState.Running)
            {
                handle.Finish(RunStatus.Error, AgentError.Create(AgentErrorCode.Unknown, "The run ended without a result.", false, messageKey: RunErrorKeys.Internal));
            }

            Settle(handle);
        }
    }

    /// <summary>
    /// 收尾(只执行一次):记录 Run 结束 → 释放会话互斥 → 发 <c>run_settled</c>(R8:落库和释放互斥之后) → 扩展 → 续跑排队消息 → 触发回收。
    /// <c>run_end</c> 比落库早,所以下一个 Run 只能在它之后开始。
    /// </summary>
    private void Settle(RunHandle handle)
    {
        if (!handle.TryBeginSettle())
        {
            return;
        }

        StopWatchdog(handle);
        RecordRun(handle, StateName(handle.State), handle.EndedAt);
        lock (_gate)
        {
            if (_reservations.TryGetValue(handle.SessionId, out var reservation) && reservation.Run == handle.RunId)
            {
                _reservations.Remove(handle.SessionId);
            }
        }

        handle.Emit(new RunSettledEvent(handle.RunId, handle.State.ToStatus()));
        foreach (var ext in _deps.Extensions)
        {
            Guard(() => ext.OnSettled(handle), "OnSettled");
        }

        _queues.OnRunSettled(handle);
        ReapFinished();
    }

    private void RecordRun(RunHandle handle, string status, DateTimeOffset? endedAt)
    {
        try
        {
            _deps.Sessions.SetRunRecord(handle.RunId.Value, handle.SessionId, status, handle.StartedAt.ToUnixTimeMilliseconds(), endedAt?.ToUnixTimeMilliseconds());
        }
        catch (Exception e)
        {
            // 会话不存在 / 库不可写:Run 照常走(会话层自己会报 session.notFound),记录丢一条不能让 Run 失败。
            Log.Warn($"The run record of {handle.RunId} could not be written.", e);
        }
    }

    private static string StateName(RunState state) => state switch
    {
        RunState.Running => "running",
        RunState.Done => "done",
        RunState.Aborted => "aborted",
        _ => "error",
    };

    /// <summary>逐个调用事件的订阅者:多播委托遇到第一个抛异常的就会停下,排在它后面的订阅者从此收不到,所以不能直接 <c>Invoke</c>。</summary>
    private void RaiseEach<T>(Action<T>? handlers, T arg, string failure)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(arg);
            }
            catch (Exception e)
            {
                Log.Warn(failure, e);
            }
        }
    }

    private void Guard(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Warn($"A run extension threw in {what}.", e);
        }
    }

    /// <summary>会话层的事件汇:丢弃 Abandoned 之后的一切;拦下会话层自己的 <c>run_settled</c>(编排层在释放互斥之后自己发)。</summary>
    private sealed class HandleSink(RunHandle handle) : IRunSink
    {
        public ValueTask EmitAsync(AgentEvent ev, CancellationToken ct)
        {
            if (ev is RunSettledEvent || handle.Abandoned)
            {
                return ValueTask.CompletedTask;
            }

            handle.Emit(ev);
            return ValueTask.CompletedTask;
        }
    }

    // IRunHandleHost
    void IRunHandleHost.OnEvent(RunHandle run, AgentEvent ev, long seq)
    {
        switch (ev)
        {
            case RunEndEvent end:
                foreach (var ext in _deps.Extensions)
                {
                    Guard(() => ext.OnEnded(run, end.Status, end.Error), "OnEnded");
                }

                // 这个 Run 不再是「运行中」:权威广播重播一次(丢了任何一条结束事件,控制端都能被它收口)。
                BroadcastActive();
                break;
            case MessageEndEvent { Message.Role: Role.User } message when run.IsTopLevel:
                _queues.OnMessageEnd(run, message.MessageId);
                break;
        }
    }

    void IRunHandleHost.OnAbortRequested(RunHandle run)
    {
        foreach (var ext in _deps.Extensions)
        {
            Guard(() => ext.OnAbortRequested(run), "OnAbortRequested");
        }

        StartWatchdog(run);
    }

    /// <summary>
    /// 停止的兜底(2.x #187)。正常路径上 abort 之后驱动几毫秒内就会自己收尾;但只要驱动里有任何一处不认取消令牌的 await 卡住,Run 就永远停在运行中——
    /// 计时一直走、停止按钮没反应、会话互斥一直攥着、排队的永远发不出去。宽限期一过还没有 <c>run_end</c>,就由这里替它收口。
    /// 已经有 <c>run_end</c> 的(驱动正在落库)不动:那时放锁,下一个 Run 就会和还在写库的驱动交错。
    /// </summary>
    private void StartWatchdog(RunHandle run)
    {
        lock (_gate)
        {
            // 看「收尾已开始」而不是「已收尾」:驱动可能在中止请求到这里之前就收到取消、在别的线程上开始收尾,
            // 那边的 StopWatchdog 已经执行过了,这时再建看门狗就没人摘——定时器和 RunHandle 一直留在字典里。
            // Settle 先标记收尾开始、再在同一把锁里摘看门狗,所以检查和登记放在锁里,两种先后都不会漏。
            if (_watchdogs.ContainsKey(run.RunId) || run.IsSettling)
            {
                return;
            }

            _watchdogs[run.RunId] = _deps.Clock.CreateTimer(static state =>
            {
                var (registry, handle) = ((RunRegistry, RunHandle))state!;
                registry.OnWatchdog(handle);
            }, (this, run), _options.AbortGrace, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopWatchdog(RunHandle run)
    {
        ITimer? timer;
        lock (_gate)
        {
            _watchdogs.Remove(run.RunId, out timer);
        }

        timer?.Dispose();
    }

    private void OnWatchdog(RunHandle run)
    {
        if (run.IsSettled || run.State != RunState.Running)
        {
            return;
        }

        Log.Warn($"Run {run.RunId} did not stop within {_options.AbortGrace.TotalSeconds:0.#} s of being aborted; forcing it to end.");
        Abandon(run);
    }

    private void Abandon(RunHandle run)
    {
        run.Abandoned = true;
        run.Finish(RunStatus.Aborted, AgentError.Create(AgentErrorCode.Aborted, "The run did not stop in time and was ended by the watchdog.", false, messageKey: RunErrorKeys.Abandoned));
        Settle(run);
    }

    // ───────────────────────── 中止 ─────────────────────────

    /// <summary>中止(2.x 的 <c>abort(runId, cascade)</c>):级联时父 Run 停,子 Run 一起停(五件事的第 5 件)。不存在的 Run 不抛。</summary>
    public void Abort(RunId runId, bool cascade = true)
    {
        RunHandle? handle;
        lock (_gate)
        {
            _runs.TryGetValue(runId, out handle);
        }

        if (handle is null)
        {
            return;
        }

        if (cascade)
        {
            foreach (var child in handle.Children)
            {
                Abort(child, true);
            }
        }

        handle.Abort();
    }

    /// <summary>停掉全部运行中的 Run(应用退出)。</summary>
    public void AbortAll()
    {
        foreach (var handle in Snapshot().Where(h => h.State == RunState.Running))
        {
            handle.Abort();
        }
    }

    // ───────────────────────── 查询 ─────────────────────────

    private List<RunHandle> Snapshot()
    {
        lock (_gate)
        {
            return [.. _runs.Values];
        }
    }

    public RunHandle? Get(RunId runId)
    {
        lock (_gate)
        {
            return _runs.GetValueOrDefault(runId);
        }
    }

    /// <summary>这条会话此刻正在跑的<b>顶层</b> Run(子代理有自己的派生会话,不算在内)。</summary>
    public RunHandle? ActiveTopLevelRun(string sessionId)
    {
        lock (_gate)
        {
            return _runs.Values.FirstOrDefault(h => h.SessionId == sessionId && h.IsTopLevel && h.State == RunState.Running);
        }
    }

    /// <summary>外层工作区 Tab 的运行中角标就是数这个——数据源是注册表,不是任何界面状态。</summary>
    public IReadOnlyList<RunHandle> RunningIn(string workspaceId) => [.. Snapshot().Where(h => h.WorkspaceId == workspaceId && h.State == RunState.Running)];

    public IReadOnlyList<RunId> ActiveRunIds() => [.. Snapshot().Where(h => h.State == RunState.Running).Select(h => h.RunId)];

    /// <summary>
    /// 某个 Run 名下<b>还在跑</b>的子 Run。★ 数的是「还在跑的」,不是 <c>Children.Count</c>——后者是累计的(只加不减),用它当并发闸门的话,
    /// 一次 Run 里派满 N 个子代理之后就再也派不出第五个了,哪怕它们早就全部结束。
    /// </summary>
    public IReadOnlyList<RunHandle> ActiveChildrenOf(RunId runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var parent))
            {
                return [];
            }

            return [.. parent.Children.Select(id => _runs.GetValueOrDefault(id)).OfType<RunHandle>().Where(h => h.State == RunState.Running)];
        }
    }

    public int ActiveChildCount(RunId runId) => ActiveChildrenOf(runId).Count;

    /// <summary>全局仍在运行的子 Run id(子代理并发队列的死锁判据要拿它和「正卡在队列里等名额的 Run」求交集)。</summary>
    public IReadOnlyList<RunId> ActiveSubagentRunIds() => [.. Snapshot().Where(h => h.ParentRunId is not null && h.State == RunState.Running).Select(h => h.RunId)];

    public int ActiveSubagentCount => ActiveSubagentRunIds().Count;

    /// <summary>父会话名下仍在运行的后台子代理(跨父 Run 找,父 Run 被回收之后也找得到)。</summary>
    public IReadOnlyList<RunHandle> ActiveBackgroundChildrenOfSession(string sessionId) =>
        [.. Snapshot().Where(h => h.ParentSessionId == sessionId && h.Background is not null && h.State == RunState.Running && !h.AbortRequested)];

    /// <summary>一个 Run 连同它派出的全部后代 RunId(深度优先)。订阅继承、attach 的补齐都按这份集合来。</summary>
    public IReadOnlyList<RunId> DescendantRunIds(RunId runId)
    {
        lock (_gate)
        {
            return _runs.TryGetValue(runId, out var root) ? DescendantsLocked(root) : [];
        }
    }

    private List<RunId> DescendantsLocked(RunHandle root)
    {
        var result = new List<RunId>();
        var seen = new HashSet<RunId> { root.RunId };
        var stack = new Stack<RunHandle>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            foreach (var id in stack.Pop().Children)
            {
                if (seen.Add(id) && _runs.TryGetValue(id, out var child))
                {
                    result.Add(id);
                    stack.Push(child);
                }
            }
        }

        return result;
    }

    // ───────────────────────── 订阅 / 快照 ─────────────────────────

    /// <summary>创建一个订阅(还没有跟着任何 Run)。启动前绑定用 <see cref="RunLaunch.Subscriptions"/>,已有的 Run 用 <see cref="Attach"/>。</summary>
    public RunSubscription Subscribe(IRunListener listener, SubscriptionOptions? options = null) =>
        new(listener, options ?? new SubscriptionOptions(), _deps.Clock, _deps.Log);

    /// <summary>
    /// <c>attach</c>:订阅这个 Run 和它<b>所有后代</b>,并取快照。「订阅 + 取快照」对每个 Run 在它的锁里原子完成(2.x 的顺序 ★:刷新 → 订阅 → 快照,否则会漏事件或重复),
    /// 同时与「创建子 Run 时继承订阅」互斥(注册表锁),所以新建的后代不会既被继承、又被当成已有的快照一遍。
    /// <paramref name="sinceSeq"/> 只对根 Run 生效;后代给全量(<see cref="CursorStatus.Fresh"/>)。Run 不存在(从没有过 / 已被回收)返回 null,客户端退回库里的终态。
    /// </summary>
    public AttachResult? Attach(RunId runId, long? sinceSeq, RunSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        RunSnapshot rootSnapshot;
        var descendants = new List<RunSnapshot>();
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var root))
            {
                return null;
            }

            rootSnapshot = root.Attach(subscription, sinceSeq, 0);
            foreach (var id in DescendantsLocked(root))
            {
                descendants.Add(_runs[id].Attach(subscription, null, 0));
            }
        }

        // 待处理交互数在注册表锁之外取(闸门有自己的锁;它只是信息,不参与原子性)。
        return new AttachResult(
            rootSnapshot with { PendingInteractions = _deps.Interactions?.PendingCount(rootSnapshot.RunId) ?? 0 },
            [.. descendants.Select(d => d with { PendingInteractions = _deps.Interactions?.PendingCount(d.RunId) ?? 0 })]);
    }

    /// <summary>不订阅的快照(<c>sinceSeq</c> 之后的事件、状态、子 Run)。Run 不存在返回 null。</summary>
    public RunSnapshot? SnapshotOf(RunId runId, long? sinceSeq = null) => Get(runId) is { } handle ? handle.Snapshot(sinceSeq, Pending(handle)) : null;

    private int Pending(RunHandle handle) => _deps.Interactions?.PendingCount(handle.RunId) ?? 0;

    /// <summary>
    /// 给一个 Run 追加事件(编排层之外产生的:交互闸门的 <c>interaction_request</c> / <c>interaction_resolved</c>、子代理层的 <c>subagent_*</c>)——按 Run 分配 <c>seq</c> 后并进事件流。
    /// Run 已结束或不存在时返回 0。
    /// </summary>
    public long Post(RunId runId, AgentEvent ev) => Get(runId)?.Emit(ev) ?? 0;

    /// <summary>交互发起 / 处理了:重播 <c>activeRuns</c>(「有个审批在等你」走全局角标,不走正文:没有窗口在看这条会话时正文一条都不推,而那时恰恰最该让用户知道)。</summary>
    public void NotifyInteractionsChanged() => BroadcastActive();

    // ───────────────────────── activeRuns 权威广播 ─────────────────────────

    /// <summary>
    /// 当前的权威索引(<c>device</c> 主题的首帧)。和后续广播同一个类型、同一个生成函数(<see cref="ActiveRunIndexBuilder"/>);Rev 是最近一次广播的版本(不递增)。
    /// </summary>
    public ActiveRunIndex ActiveRuns()
    {
        lock (_buildGate)
        {
            return BuildIndex(_activeRev);
        }
    }

    private void RebroadcastIfRunning()
    {
        if (!_disposed && ActiveRunIds().Count > 0)
        {
            BroadcastActive();
        }
    }

    private ActiveRunIndex BuildIndex(long rev)
    {
        var handles = Snapshot();
        // 待处理交互数在注册表锁之外取(闸门有自己的锁)。
        var facts = handles.Select(h => new RunFact(
            h.RunId.Value, h.SessionId, h.WorkspaceId, h.ParentRunId?.Value, h.ParentSessionId,
            h.State.ToPhase(), h.StartedAt, h.State == RunState.Running ? Pending(h) : 0)).ToList();
        return ActiveRunIndexBuilder.Build(_deps.DeviceId, facts, rev);
    }

    /// <summary>
    /// 全量重播。★ 子 Run 的起止也广播:渲染层的子代理卡片、活动胶囊、只读子会话三处都拿它对账——以前只为顶层 Run 通知,子 Run 的结束只能靠事件流送到,漏一条卡片就永远停在「运行中」。
    /// 构建和排队在同一把锁里,所以版本号与内容同序;派发在锁外串行(一个派发者),订阅者收到的 Rev 严格递增。
    /// </summary>
    private void BroadcastActive()
    {
        lock (_buildGate)
        {
            _outbox.Enqueue(BuildIndex(++_activeRev));
        }

        lock (_gate)
        {
            if (_draining)
            {
                return;
            }

            _draining = true;
        }

        try
        {
            while (true)
            {
                ActiveRunIndex? next;
                lock (_buildGate)
                {
                    if (!_outbox.TryDequeue(out next))
                    {
                        lock (_gate)
                        {
                            _draining = false;
                        }

                        return;
                    }
                }

                // 控制端断开 / 观察者异常不能打断权威执行,也不能让排在它后面的订阅者收不到。
                RaiseEach(ActiveRunsChanged, next, "An active-runs subscriber threw.");
            }
        }
        catch
        {
            lock (_gate)
            {
                _draining = false;
            }

            throw;
        }
    }

    // ───────────────────────── 会话互斥与操作 ─────────────────────────

    /// <summary>有 Run 或历史操作占着这条会话——包括 <c>run_end</c> 之后、<c>run_settled</c> 之前那一段(收尾落盘中,下一个 Run 不能开始,R8)。</summary>
    public bool IsSessionBusy(string sessionId)
    {
        lock (_gate)
        {
            return _reservations.ContainsKey(sessionId);
        }
    }

    /// <summary>手动压缩等跨 await 的历史操作:由注册表持有互斥,而不是依赖某个窗口的按钮。忙时抛 <see cref="SessionBusyException"/>。释放只清自己拿的那把(旧的释放函数不会清掉重新获取的锁)。</summary>
    public IDisposable AcquireOperation(string sessionId)
    {
        lock (_gate)
        {
            if (_reservations.ContainsKey(sessionId))
            {
                throw new SessionBusyException(sessionId);
            }

            var token = new object();
            _reservations[sessionId] = new Reservation(token, null);
            return new OperationLease(this, sessionId, token);
        }
    }

    private void ReleaseOperation(string sessionId, object token)
    {
        lock (_gate)
        {
            if (_reservations.TryGetValue(sessionId, out var reservation) && reservation.Token == token)
            {
                _reservations.Remove(sessionId);
            }
        }
    }

    private sealed class OperationLease(RunRegistry registry, string sessionId, object token) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                registry.ReleaseOperation(sessionId, token);
            }
        }
    }

    /// <summary>
    /// 这几条会话此刻「到底谁占着」——删除会话前的活体检查,按真实状态分类。删除被「会话忙」挡住时,要分得清是真的有 Agent 在跑(该问用户要不要停),
    /// 还是残留的条目(该清掉、照删)。<c>Live</c>:运行中且没被中止;<c>Stopping</c>:已经按过停止、状态还停在运行中(驱动要么正在退,要么卡死了);
    /// <c>Finalizing</c>:互斥被一个已经结束(或已不在表里)的 Run 占着——收尾落盘中,正常是毫秒级;<c>Operations</c>:没有 Run 的历史操作占着。
    /// </summary>
    public SessionActivity Activity(IEnumerable<string> sessionIds)
    {
        var ids = sessionIds.ToHashSet(StringComparer.Ordinal);
        var live = new List<RunHandle>();
        var stopping = new List<RunHandle>();
        var finalizing = new List<string>();
        var operations = new List<string>();
        lock (_gate)
        {
            foreach (var handle in _runs.Values)
            {
                if (!ids.Contains(handle.SessionId) || handle.State != RunState.Running)
                {
                    continue;
                }

                (handle.AbortRequested ? stopping : live).Add(handle);
            }

            foreach (var (sessionId, reservation) in _reservations)
            {
                if (!ids.Contains(sessionId))
                {
                    continue;
                }

                if (reservation.Run is not { } owner)
                {
                    operations.Add(sessionId);
                }
                else if (!_runs.TryGetValue(owner, out var handle) || handle.State != RunState.Running)
                {
                    finalizing.Add(sessionId);
                }
            }
        }

        return new SessionActivity(live, stopping, finalizing, operations);
    }

    /// <summary>
    /// 删除会话的最后一步:把这些会话名下还挂着的 Run 强制判成 Aborted、互斥强制放掉。★ 只在「已经中断过、等过一段宽限期仍没收尾」之后调用。
    /// 被强制收掉的驱动如果之后还醒过来:它的事件被丢弃,settle 只收一次——都不会把会话复活。返回强制收口的 Run 数 + 放掉的互斥数。
    /// </summary>
    public int ForceSettleSessions(IEnumerable<string> sessionIds)
    {
        var ids = sessionIds.ToHashSet(StringComparer.Ordinal);
        var n = 0;
        foreach (var handle in Snapshot().Where(h => ids.Contains(h.SessionId) && h.State == RunState.Running))
        {
            handle.Abort();
            Abandon(handle);
            n++;
        }

        lock (_gate)
        {
            foreach (var id in ids)
            {
                if (_reservations.Remove(id))
                {
                    n++;
                }
            }
        }

        return n;
    }

    // ───────────────────────── 排队消息 ─────────────────────────

    /// <summary>某会话队列的当前全量快照(<c>session/&lt;id&gt;</c> 主题的队列半边;和 <see cref="QueueChanged"/> 同一个类型)。</summary>
    public QueueUpdate QueueOf(string sessionId) => _queues.Current(sessionId);

    /// <summary>排队一条消息(<c>queue.enqueue</c>)。文件引用先按会话层的规则归一化(和首条输入同一个写法)。落盘失败抛 <see cref="QueuePersistenceException"/>。</summary>
    public async Task<QueueResult> EnqueueAsync(string sessionId, string workspaceId, MessageId? id, IReadOnlyList<Part> parts, SendOptions options)
    {
        var normalized = await NormalizeAsync(sessionId, workspaceId, parts).ConfigureAwait(false);
        return _queues.Enqueue(sessionId, id, normalized, options);
    }

    /// <summary>「插话」(<c>queue.promote</c>):toggle。运行中 → 下一个回合边界送进去;空闲 → 立即发送。</summary>
    /// <summary>
    /// 记下这个会话「上一次的发送档位」(内部输入——目标 kickoff、汇报——在没有别的档位可用时续用它)。用户发消息时由注册表自己记;
    /// 这里给「会话还没发过消息、用户先设了目标」那一条路:控制端随 <c>goal.set</c> 带来的档位。
    /// </summary>
    public void RememberOptions(string sessionId, SendOptions options) => _queues.RememberOptions(sessionId, options);

    public QueueResult TogglePromote(string sessionId, MessageId id) => _queues.TogglePromote(sessionId, id);

    public QueueResult EditQueued(string sessionId, MessageId id, IReadOnlyList<Part> parts) => _queues.Edit(sessionId, id, parts);

    /// <summary>删除一条(<c>queue.remove</c>)。</summary>
    public QueueResult DropQueued(string sessionId, MessageId id) => _queues.Drop(sessionId, id);

    /// <summary>撤回:出队并交回内容。</summary>
    public QueueResult TakeQueued(string sessionId, MessageId id) => _queues.Take(sessionId, id);

    public QueueResult RetagQueuedPermission(string sessionId, string mode) => _queues.RetagPermission(sessionId, mode);

    public QueueResult RetagQueuedMode(string sessionId, string mode) => _queues.RetagMode(sessionId, mode);

    /// <summary>用户在停止 / 报错 / 重启之后手动继续(与自动续跑走同一条路径)。</summary>
    public QueueResult ResumeQueue(string sessionId) => _queues.Resume(sessionId);

    /// <summary>内部输入(后台子代理报告、目标检查):见 <see cref="InternalDelivery"/>。</summary>
    public InternalDelivery DeliverInternal(string sessionId, InternalInput input) => _queues.DeliverInternal(sessionId, input);

    // IQueueHost
    RunHandle? IQueueHost.ActiveTopLevelRun(string sessionId) => ActiveTopLevelRun(sessionId);

    bool IQueueHost.IsSessionBusy(string sessionId) => IsSessionBusy(sessionId);

    LaunchResult IQueueHost.Launch(RunLaunch launch) => Launch(launch);

    // ───────────────────────── 回收 ─────────────────────────

    /// <summary>
    /// 回收已结束的 Run(转录在 SQLite 里,内存里那份事件日志只是给重载 / 晚到的 attach 用的)。一个 Run 只有同时满足这些才回收:
    /// 已 settle(互斥已放开);没有订阅者;<b>没有任何运行中的后代</b>(一直往下查——后台子代理的收尾要沿父子关系一路爬到根,中间任何一层被回收,那条链就断在那里);
    /// 没有挂起的交互(交互闸门说了算);已经结束满 TTL——或者已结束 Run 的日志总量超过预算,那就不等 TTL,从最早结束的回收起(其余条件照样要满足)。
    /// </summary>
    public int ReapFinished(DateTimeOffset? now = null)
    {
        var at = now ?? _deps.Clock.GetUtcNow();
        var reaped = new List<RunHandle>();
        lock (_gate)
        {
            var candidates = new List<RunHandle>();
            long finishedBytes = 0;
            foreach (var handle in _runs.Values)
            {
                if (handle.State == RunState.Running)
                {
                    continue;
                }

                finishedBytes += handle.RetainedLogBytes;
                if (handle.IsSettled
                    && handle.SubscriberCount == 0
                    && !(_reservations.TryGetValue(handle.SessionId, out var r) && r.Run == handle.RunId)
                    && !HasRunningDescendantLocked(handle.RunId, [])
                    && Pending(handle) == 0)
                {
                    candidates.Add(handle);
                }
            }

            candidates.Sort((a, b) => Nullable.Compare(a.EndedAt, b.EndedAt));
            foreach (var handle in candidates)
            {
                var expired = at - (handle.EndedAt ?? DateTimeOffset.MinValue) >= _options.FinishedRunTtl;
                if (!expired && finishedBytes <= _options.FinishedCacheBytes)
                {
                    continue;
                }

                _runs.Remove(handle.RunId);
                finishedBytes -= handle.RetainedLogBytes;
                reaped.Add(handle);
            }
        }

        foreach (var handle in reaped)
        {
            handle.Dispose();
            RaiseEach(RunReaped, handle, "A run-reaped subscriber threw.");
        }

        return reaped.Count;
    }

    private bool HasRunningDescendantLocked(RunId runId, HashSet<RunId> seen)
    {
        if (!seen.Add(runId) || !_runs.TryGetValue(runId, out var run))
        {
            return false;
        }

        foreach (var childId in run.Children)
        {
            if (_runs.TryGetValue(childId, out var child) && (child.State == RunState.Running || HasRunningDescendantLocked(childId, seen)))
            {
                return true;
            }
        }

        return false;
    }

    // ───────────────────────── 退出 / 会话删除 ─────────────────────────

    /// <summary>
    /// 应用退出:停掉全部 Run、等它们收尾(有上限)、冲掉排队消息的存档、杀掉后台 Shell。
    /// ★ 后台 Shell 不属于任何一个 Run(这正是后台的意义),所以 <see cref="AbortAll"/> 一个也带不走——而独立进程组不随主进程消失:
    /// 不杀的话,用户重启应用后端口还占着,界面上再也找不到是谁占的。
    /// </summary>
    public async Task ShutdownAsync()
    {
        lock (_gate)
        {
            _shuttingDown = true;
        }

        AbortAll();
        await WaitForAsync(Snapshot()).ConfigureAwait(false);
        _queues.Flush();
        if (_deps.Shells is { } shells)
        {
            await shells.ShutdownAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 会话删除:停掉这些会话名下的 Run、等它们收尾,然后杀掉属于它们的后台 Shell(后台 Shell 不随 Run 结束而结束,但会话没了就没有任何人能再看见它们),
    /// 并丢掉内存里的排队状态(存档由会话删除一并清掉)。调用方在删除会话行<b>之前</b>调它(要靠 <c>runs</c> 表找出这些会话的 Run id)。
    /// </summary>
    public async Task ForgetSessionsAsync(IEnumerable<string> sessionIds)
    {
        var ids = sessionIds.ToList();
        var set = ids.ToHashSet(StringComparer.Ordinal);
        var running = Snapshot().Where(h => set.Contains(h.SessionId) && h.State == RunState.Running).ToList();
        foreach (var handle in running)
        {
            Abort(handle.RunId, cascade: true);
        }

        await WaitForAsync(running).ConfigureAwait(false);

        if (_deps.Shells is { } shells)
        {
            var runIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                foreach (var run in _deps.Sessions.RunIds(id))
                {
                    runIds.Add(run);
                }
            }

            foreach (var handle in Snapshot().Where(h => set.Contains(h.SessionId)))
            {
                runIds.Add(handle.RunId.Value);
            }

            foreach (var shell in shells.List().Where(s => s.Status == BackgroundShellStatus.Running && runIds.Contains(s.RunId.Value)))
            {
                await shells.KillAsync(shell.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }

        foreach (var id in ids)
        {
            _queues.Forget(id);
        }
    }

    private async Task WaitForAsync(List<RunHandle> handles)
    {
        var tasks = handles.Select(h => h.Completion).OfType<Task>().ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(_options.ShutdownWait, _deps.Clock).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("Some runs did not settle in time.");
        }
        catch (Exception e)
        {
            Log.Warn("A run driver faulted while waiting for it.", e);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shuttingDown = true;
        }

        _reapTimer.Dispose();
        _rebroadcastTimer?.Dispose();
        _queues.Dispose();
        List<ITimer> timers;
        lock (_gate)
        {
            timers = [.. _watchdogs.Values];
            _watchdogs.Clear();
        }

        foreach (var timer in timers)
        {
            timer.Dispose();
        }
    }
}
