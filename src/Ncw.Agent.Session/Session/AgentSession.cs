using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Kernel.Session;

/// <summary>会话里已经有一个 Run 在跑,而调用方要做的事(手动压缩、修孤儿)只能在会话空闲时做。</summary>
public sealed class SessionBusyException(string sessionId) : InvalidOperationException($"Session {sessionId} already has a run in progress.")
{
    public string SessionId { get; } = sessionId;
}

/// <summary>会话层的构造参数:长期不变的依赖(Run 级的设置快照在 <see cref="RunRequest.Settings"/>)。</summary>
public sealed class AgentSessionDeps
{
    public required SessionRepository Sessions { get; init; }

    public required ILlmClient Llm { get; init; }

    public required IIdGenerator Ids { get; init; }

    public required TimeProvider Clock { get; init; }

    public required IDelayer Delayer { get; init; }

    /// <summary>项目的执行环境:读项目指令、重读文件、git 状态、工具的 <see cref="SessionToolContext.Execution"/> 都只经它。</summary>
    public required IExecutionEnvironment Environment { get; init; }

    /// <summary>项目根(执行环境里的绝对路径)。</summary>
    public required EnvPath WorkspaceRoot { get; init; }

    /// <summary>一方扩展 + 插件宿主(按 <see cref="IAgentExtension.Order"/> 排序)。</summary>
    public IReadOnlyList<IAgentExtension> Extensions { get; init; } = [];

    public IToolSource? Tools { get; init; }

    /// <summary>别名 → 绑定(路由器的 <c>ResolveBinding</c>);查不到返回 null(按兜底窗口照常发请求,由路由器去报权威错误)。</summary>
    public Func<ModelRef, ModelBinding?> ResolveModel { get; init; } = static _ => null;

    public AgentLoopOptions? LoopOptions { get; init; }

    public ILoopDiagnostics Diagnostics { get; init; } = NullLoopDiagnostics.Instance;
}

/// <summary>
/// 会话层(04):一个会话的「业务外壳」。装配扩展、组装每回合的请求、压缩、落库、修复孤儿调用,把这些作为 <see cref="LoopHooks"/> 交给循环层。
/// <para>
/// <b>单一真源是 Storage</b>(AGENTS.md §3 规则 1):每个 Run 开始时从库里读整段历史(用户可能在两个 Run 之间编辑 / 删除了消息),
/// Run 里提交的每条消息先落库、再进内存、再发 <c>message_end</c>(R6:只在消息完成时写,流式增量只是事件)。
/// </para>
/// <para>
/// <b>会话互斥</b>:一个会话同一时刻最多一个 Run。<see cref="RunAsync"/> 在会话已有 Run 时直接返回 <c>session.busy</c>(没有事件);
/// <b><c>run_settled</c> 的前置条件</b>:Run 的所有消息已落库、会话锁已释放,之后才向 <see cref="IRunSink"/> 发 <see cref="RunSettledEvent"/>、触发 <c>run_settled</c> 扩展点——
/// 下一个 Run 只等它(R8)。编排层(<c>RunRegistry</c>)不在这里:它负责排队、子代理、事件日志和 seq,只用 <see cref="RunRequest"/> 里的钩子
/// (<c>TakeSteering</c>、<c>EnrichToolContext</c>)和 <see cref="IRunSink"/> 接进来。
/// </para>
/// </summary>
public sealed partial class AgentSession
{
    private readonly AgentSessionDeps _deps;
    private readonly ExtensionHost _host;
    private readonly Compactor _compactor;
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private bool _started;

    public AgentSession(string sessionId, string workspaceId, AgentSessionDeps deps)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionId);
        _deps = deps;
        SessionId = sessionId;
        WorkspaceId = workspaceId;
        _host = new ExtensionHost(deps.Extensions);
        _compactor = new Compactor(deps.Llm, deps.Ids, deps.Clock);
    }

    public string SessionId { get; }

    public string WorkspaceId { get; }

    /// <summary>此刻有没有 Run(或手动压缩)占着会话锁。</summary>
    public bool IsBusy => _runLock.CurrentCount == 0;

    private ILoopDiagnostics Log => _deps.Diagnostics;

    private EnvironmentTag Tag => new(_deps.Environment.Id.DeviceId, _deps.Environment.Id.Environment);

    // ───────────────────────── 一个 Run ─────────────────────────

    /// <summary>
    /// 跑一个 Run。<b>不抛</b>(取消也是):结局一律经返回值和事件表达。
    /// 取消令牌触发 = 中止:循环走中止收尾(半截回复提交、补「已中止」结果),然后照常落库、释放锁、发 <c>run_settled</c>。
    /// </summary>
    public async Task<RunOutcome> RunAsync(RunRequest request, IRunSink sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sink);
        if (!_runLock.Wait(0))
        {
            return NotStarted(request, AgentError.Create(
                AgentErrorCode.Conflict, "The session already has a run in progress.", retryable: false, messageKey: SessionErrorKeys.Busy));
        }

        RunOutcome outcome;
        try
        {
            outcome = await RunLockedAsync(request, sink, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // ★ 一个逃出去的异常 = Run 无声消失。意料之外的失败收成错误结局,仍然走收尾和 settled。
            Log.Warn("The run failed with an unexpected exception.", e);
            outcome = ct.IsCancellationRequested && e is OperationCanceledException
                ? new RunOutcome(request.RunId, RunStatus.Aborted, null, new TokenUsage(), CostInfo.NotMetered, 0)
                : new RunOutcome(
                    request.RunId, RunStatus.Error,
                    AgentError.Create(AgentErrorCode.Unknown, $"Unexpected error in the session layer: {e.Message}", false, messageKey: SessionErrorKeys.Internal),
                    new TokenUsage(), CostInfo.NotMetered, 0);
        }
        finally
        {
            _runLock.Release();
        }

        // ★ run_settled:落库(上面已经全部完成)和释放会话锁(上一行)之后才发。没有开始的 Run(会话不存在)没有 run_start,也没有 run_settled。
        if (!outcome.Started)
        {
            return outcome;
        }

        var settled = new RunSettledEvent(request.RunId, outcome.Status);
        await EmitAsync(sink, settled).ConfigureAwait(false);
        // 被放弃的 Run:编排层早已替它 settle 过一次(session 互斥也已放给别人),扩展不能再收到第二个 run_settled。
        if (!SkipIfAbandoned(request, "run_settled"))
        {
            await _host.NotifyAsync(SessionExtensionPoints.RunSettled, settled, new ExtensionContext(request.RunId, SessionId, Log), CancellationToken.None).ConfigureAwait(false);
        }

        return outcome;
    }

    private static RunOutcome NotStarted(RunRequest request, AgentError error) =>
        new(request.RunId, RunStatus.Error, error, new TokenUsage(), CostInfo.NotMetered, 0, Started: false);

    private async Task<RunOutcome> RunLockedAsync(RunRequest request, IRunSink sink, CancellationToken ct)
    {
        var records = LoadHistory();
        if (records is null)
        {
            return NotStarted(request, AgentError.Create(AgentErrorCode.Conflict, $"Session {SessionId} does not exist.", retryable: false, messageKey: SessionErrorKeys.NotFound));
        }

        var ctx = new ExtensionContext(request.RunId, SessionId, Log);
        if (!_started)
        {
            _started = true;
            await _host.NotifyAsync(SessionExtensionPoints.SessionStart, new SessionStartEvent(SessionId, WorkspaceId), ctx, ct).ConfigureAwait(false);
        }

        // input:链式变换(命令展开校验、UserPromptSubmit Hook)
        var transformed = await _host.TransformAsync(SessionExtensionPoints.Input, new InputPayload(request.Input) { Depth = request.Depth }, ctx, ct).ConfigureAwait(false);
        var input = transformed.Messages;
        var newMessages = new List<Message>(input.Count);
        var effectiveRoot = request.WorkspaceRoot ?? _deps.WorkspaceRoot;
        if (transformed.BlockedReason is null)
        {
            foreach (var message in input)
            {
                newMessages.Add(message with { Parts = await FileReferences.NormalizeAsync(message.Parts, _deps.Environment, effectiveRoot, ct).ConfigureAwait(false) });
            }
        }

        // before_run:就地修改运行参数
        var draft = new RunDraft(request);
        draft.HeadSections.AddRange(transformed.HeadSections);
        await _host.MutateAsync(SessionExtensionPoints.BeforeRun, draft, ctx, ct).ConfigureAwait(false);

        var state = await StartStateAsync(request, draft, records, ctx, ct).ConfigureAwait(false);
        state.TailAnchor = newMessages.FirstOrDefault(m => m.Parts.Count > 0)?.Id;
        if (transformed.BlockedReason is { } blocked)
        {
            // 输入被拦下(UserPromptSubmit Hook):不提交输入消息,第一回合就以错误结束(没有请求发给上游)。
            state.Blocked = AgentError.Create(AgentErrorCode.Unknown, blocked, retryable: false, messageKey: transformed.BlockedKey ?? SessionErrorKeys.InputBlocked);
        }

        var hooks = _host.CreateLoopHooks(
            ctx,
            baseline: (turn, token) => PrepareTurnAsync(state, turn, token),
            takeSteering: request.TakeSteering,
            commit: (message, token) => CommitAsync(state, message),
            enrichToolContext: context => Enrich(state, context),
            recoverContextLength: (turn, token) => RecoverContextLengthAsync(state, turn, token));
        var loop = new AgentLoop(_deps.Llm, _deps.Ids, _deps.Clock, _deps.Delayer, _deps.LoopOptions, Log);
        var loopInput = new LoopInput(
            request.RunId, SessionId, WorkspaceId, BuildContext(state.Messages), newMessages, request.Model, state.Options, Tag, request.Depth);

        var usage = new TokenUsage();
        var cost = CostInfo.NotMetered;
        var toolCalls = 0;
        RunEndEvent? end = null;
        await foreach (var ev in loop.RunAsync(loopInput, hooks, ct).ConfigureAwait(false))
        {
            switch (ev)
            {
                case MessageEndEvent { Usage: { } messageUsage } messageEnd:
                    usage = usage.Add(messageUsage);
                    cost = cost.Add(messageEnd.Cost);
                    break;
                case ToolEndEvent:
                    toolCalls++;
                    break;
                case RunEndEvent runEnd:
                    end = runEnd;
                    break;
            }

            await EmitAsync(sink, ev).ConfigureAwait(false);
        }

        // 所有消息都已经在循环里落库(CommitMessage 先于 message_end)。run_end 扩展点在落库之后触发(后台报告、目标暂停、SubagentStop Hook)。
        // 被放弃的 Run 不再触发 run_end 扩展(目标 / 计划 / Hook 会据此写状态、起续跑):编排层已经按 Aborted 收过口了。
        if (end is not null && !SkipIfAbandoned(request, "run_end"))
        {
            await _host.NotifyAsync(LoopExtensionPoints.RunEnd, end, ctx, CancellationToken.None).ConfigureAwait(false);
        }

        return new RunOutcome(request.RunId, end?.Status ?? RunStatus.Error, end?.Error, usage, cost, toolCalls);
    }

    /// <summary>事件的送达永远不会让 Run 失败;中止之后的收尾事件(run_end)仍然要送到,所以不带取消令牌。</summary>
    private async ValueTask EmitAsync(IRunSink sink, AgentEvent ev)
    {
        try
        {
            await sink.EmitAsync(ev, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Warn("The run event sink failed.", e);
        }
    }

    // ───────────────────────── 提交(R6) ─────────────────────────

    /// <summary>
    /// 循环层每条消息完成时调它(助手消息的 <c>message_end</c>、工具结果、引导消息、内部消息、用户输入):先落库,再进内存。
    /// 落库和全文索引、用户消息的消费回执在同一个事务里(Storage 保证)。同一个 id 重放幂等。
    /// </summary>
    private ValueTask CommitAsync(RunState state, Message message)
    {
        // 被放弃的驱动醒来后不许再写库(会话可能已经属于下一个 Run,甚至已被删除;2.x #187)。内存转录也不动:这个 Run 已经没有后文。
        if (SkipIfAbandoned(state.Request, "commit"))
        {
            return ValueTask.CompletedTask;
        }

        _deps.Sessions.Commit(SessionId, MessageCodec.ToRecord(message), state.Request.RunId.Value);
        state.Upsert(message);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 每个落库点(以及随落库而来的扩展通知)在动手之前问一次:这个 Run 被编排层放弃了吗?是就记一条诊断(<see cref="SessionDiagnosticKeys.WriteSkippedAbandoned"/>)并返回 true,调用方跳过。
    /// 回调自己抛异常按「没放弃」处理(检查本身不能让 Run 失败)。
    /// </summary>
    private bool SkipIfAbandoned(RunRequest request, string site)
    {
        bool abandoned;
        try
        {
            abandoned = request.IsAbandoned?.Invoke() == true;
        }
        catch (Exception e)
        {
            Log.Warn("The abandoned-run check threw; treating the run as live.", e);
            return false;
        }

        if (abandoned)
        {
            Log.Warn($"{SessionDiagnosticKeys.WriteSkippedAbandoned} site={site} run={request.RunId.Value} session={SessionId}");
        }

        return abandoned;
    }

    private IReadOnlyList<MessageRecord>? LoadHistory() =>
        _deps.Sessions.Get(SessionId) is null ? null : _deps.Sessions.History(SessionId);

    /// <summary>
    /// 转录 → 发给模型的上下文:最后一个压缩边界(含)之后的消息(<see cref="CompactBoundaries.ForModel"/>),再去掉没有任何模型可见内容的消息
    /// (只剩界面专用块的助手消息、2.x 的 <c>subagent</c> 块转出来的空消息):空内容的消息上游会 400。
    /// 完整转录仍在库里——压缩改变的是「发给模型的范围」,不是聊天记录。
    /// </summary>
    public static IReadOnlyList<Message> BuildContext(IReadOnlyList<Message> transcript) =>
        [.. CompactBoundaries.ForModel(transcript).Where(HasModelVisibleContent)];

    private static bool HasModelVisibleContent(Message message) =>
        message.Parts.Any(p => p is not (ErrorPart or GoalStatusPart or CompactBoundaryPart));

    // ───────────────────────── 孤儿修复(加载历史时) ─────────────────────────

    /// <summary>
    /// 修复库里已有的孤儿工具调用(上一个 Run 崩溃留下的):补一条失败结果,提交成一条 tool_result 消息。幂等(没有孤儿就什么都不做)。
    /// 开始 Run 时循环层也会做同一件事(在新用户消息之前);这里给「只读历史、不跑 Run」的调用方(冷启动加载、导出)用。
    /// 「每个工具调用都必须紧跟它的结果」是最重要的不变量:漏掉一个,下一次请求就是 400。会话里有 Run 在跑时抛 <see cref="SessionBusyException"/>。
    /// </summary>
    public int RepairOrphans()
    {
        if (!_runLock.Wait(0))
        {
            throw new SessionBusyException(SessionId);
        }

        try
        {
            var records = LoadHistory();
            if (records is null)
            {
                return 0;
            }

            var messages = records.Select(MessageCodec.FromRecord).ToList();
            var orphans = Orphans.Find(messages);
            if (orphans.Count == 0)
            {
                return 0;
            }

            var output = new ToolOutput(LoopMessages.RecoveredInterrupted);
            var repair = new Message(
                new MessageId(_deps.Ids.NewId()),
                Role.User,
                [.. orphans.Select(o => (Part)new ToolResultPart(o.CallId, output, true))],
                _deps.Clock.GetUtcNow());
            _deps.Sessions.Commit(SessionId, MessageCodec.ToRecord(repair));
            return orphans.Count;
        }
        finally
        {
            _runLock.Release();
        }
    }

    // ───────────────────────── 输入归一化(给编排层) ─────────────────────────

    /// <summary>
    /// 排队消息进队之前归一化它的文件引用(<see cref="FileReferences"/>),让「插队进来的消息」和首条输入同一个写法。
    /// 编排层(<c>RunInputQueues</c> 的调用方)在入队前调它:循环层的 <c>TakeSteering</c> 是同步的,不能在那里做 realpath。
    /// </summary>
    public ValueTask<IReadOnlyList<Part>> NormalizeInputAsync(IReadOnlyList<Part> parts, CancellationToken ct = default) =>
        FileReferences.NormalizeAsync(parts, _deps.Environment, _deps.WorkspaceRoot, ct);

    private ToolContext Enrich(RunState state, ToolContext context)
    {
        ToolContext enriched = new SessionToolContext(context, _deps.Environment, state.WorkspaceRoot);
        return state.Request.EnrichToolContext is { } more ? more(enriched) : enriched;
    }
}
