using System.Threading.Channels;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Kernel.Loop;

/// <summary>一次 Run 的执行状态(每次 <see cref="AgentLoop.RunAsync"/> 一个)。分三个文件:回合循环、流式一次请求、工具执行。</summary>
internal sealed partial class RunExecution
{
    /// <summary>上游自己报了「已取消」(而调用方的令牌没触发):同样走中止路径。</summary>
    private sealed class AbortSignalException() : OperationCanceledException("The upstream reported the request as aborted.");

    private enum TurnKind
    {
        /// <summary>回合结束,继续下一个。</summary>
        Continue,

        /// <summary>Run 到此结束。</summary>
        Final,
    }

    /// <summary>
    /// <paramref name="TakeSteeringAfter"/>:回合边界要取引导消息(工具结果落库之后、下一次请求之前);
    /// <paramref name="Taken"/>:已经取走、等 turn_end 之后再提交的引导消息(没有工具调用的回合先取一次,因为有了它就不必问 OnTurnEnd)。
    /// </summary>
    private sealed record TurnResult(TurnKind Kind, RunStatus Status = RunStatus.Done, AgentError? Error = null, bool TakeSteeringAfter = false, IReadOnlyList<Message>? Taken = null)
    {
        public static TurnResult Next(bool takeSteering = true, IReadOnlyList<Message>? taken = null) => new(TurnKind.Continue, TakeSteeringAfter: takeSteering, Taken: taken);

        public static TurnResult Finish(RunStatus status = RunStatus.Done, AgentError? error = null) => new(TurnKind.Final, status, error);
    }

    private readonly ILlmClient _llm;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _clock;
    private readonly IDelayer _delayer;
    private readonly AgentLoopOptions _options;
    private readonly ILoopDiagnostics _diagnostics;
    private readonly LoopInput _input;
    private readonly LoopHooks _hooks;
    private readonly ChannelWriter<AgentEvent> _events;
    private readonly CancellationToken _ct;

    /// <summary>上下文 + 本 Run 已提交的全部消息(孤儿检测和 TurnContext 的依据)。</summary>
    private readonly List<Message> _transcript;

    private int _turnIndex = -1;
    private bool _turnOpen;
    private int _toolCallsThisRun;
    private int _stoppedTurnStreak;
    private bool _stopHookActive;

    /// <summary>本回合已经因为上游 <c>ContextLength</c> 做过一次被动压缩;一个回合正常结束(没有错误)后复位。</summary>
    private bool _contextRecovered;

    private TokenUsage? _lastUsage;

    private MessageId? _lastUsageAnchor;

    /// <summary>正在流式输出的助手消息:中止时靠它把半截回复救回来(用户已经在屏幕上读到的字,不能因为他点了停止就凭空消失)。</summary>
    private Streaming? _pending;

    public RunExecution(
        ILlmClient llm, IIdGenerator ids, TimeProvider clock, IDelayer delayer, AgentLoopOptions options, ILoopDiagnostics diagnostics,
        LoopInput input, LoopHooks hooks, ChannelWriter<AgentEvent> events, CancellationToken ct)
    {
        _llm = llm;
        _ids = ids;
        _clock = clock;
        _delayer = delayer;
        _options = options;
        _diagnostics = diagnostics;
        _input = input;
        _hooks = hooks;
        _events = events;
        _ct = ct;
        _transcript = [.. input.Context];
    }

    private void Emit(AgentEvent ev) => _events.TryWrite(ev);

    private MessageId NewId() => new(_ids.NewId());

    private DateTimeOffset Now() => _clock.GetUtcNow();

    /// <summary>★ 不抛异常:Run 的结局一律经 <see cref="RunEndEvent"/> 表达,一个逃出去的异常等于「Run 无声消失」。</summary>
    public async Task ExecuteAsync()
    {
        RunStatus status;
        AgentError? error = null;
        try
        {
            Emit(new RunStartEvent(_input.RunId, _input.SessionId, _input.Model));
            (status, error) = await RunCoreAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (_ct.IsCancellationRequested || e is AbortSignalException)
        {
            status = RunStatus.Aborted;
            await GuardedAsync(FinalizeAbortAsync).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _diagnostics.Warn("The run failed with an unexpected exception.", e);
            status = RunStatus.Error;
            error = AgentError.Create(AgentErrorCode.Unknown, $"Unexpected error in the agent loop: {e.Message}", false, messageKey: LoopErrorKeys.Internal);
            await GuardedAsync(() => CloseOrphansAsync(LoopMessages.RunFailed)).ConfigureAwait(false);
        }

        CloseTurn();
        Emit(new RunEndEvent(status, error));
        _events.TryComplete();
    }

    private async Task GuardedAsync(Func<Task> cleanup)
    {
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // 收尾自己出错(例如落库一直失败):记下来,不能让它盖掉 Run 的真实结局。
            _diagnostics.Warn("Run cleanup failed.", e);
        }
    }

    private void CloseTurn()
    {
        if (_turnOpen)
        {
            _turnOpen = false;
            Emit(new TurnEndEvent(_turnIndex));
        }
    }

    // ───────────────────────── 回合循环 ─────────────────────────

    /// <summary>
    /// ★ 继续用循环,不用递归(2.x 的目标模式长时间运行曾因递归爆栈)。没有回合上限:
    /// 停止条件只有中止、错误、模型不再调工具且 OnTurnEnd 不要求继续、全部工具结果 Terminate。
    /// </summary>
    private async Task<(RunStatus Status, AgentError? Error)> RunCoreAsync()
    {
        // ★ 上一个 Run 崩溃留下的孤儿调用,必须在新用户消息之前补,否则新消息会插在调用和结果之间。
        await CloseOrphansAsync(LoopMessages.RecoveredInterrupted).ConfigureAwait(false);
        foreach (var message in _input.NewMessages)
        {
            if (message.Parts.Count > 0)
            {
                await CommitAnnouncedAsync(message).ConfigureAwait(false);
            }
        }

        for (;;)
        {
            _ct.ThrowIfCancellationRequested();
            var plan = await _hooks.PrepareTurn(
                new TurnContext(_input.RunId, _input.SessionId, _turnIndex + 1, _transcript.ToArray(), _input.Model, _input.Options, _toolCallsThisRun, _lastUsage, _lastUsageAnchor),
                _ct).ConfigureAwait(false);
            foreach (var ev in plan.Events)
            {
                Emit(ev);
            }

            if (plan.IsStop)
            {
                return (plan.StopStatus, plan.StopError);
            }

            _turnIndex++;
            _turnOpen = true;
            Emit(new TurnStartEvent(_turnIndex));
            var result = await RunTurnAsync(plan).ConfigureAwait(false);
            CloseTurn();
            if (result.Kind == TurnKind.Final)
            {
                return (result.Status, result.Error);
            }

            // 引导消息:工具结果落库之后、下一次请求之前(插入位置必须在工具结果之后,否则会把调用和结果拆开)。
            var steering = result.Taken ?? (result.TakeSteeringAfter ? _hooks.TakeSteering() : []);
            await CommitSteeringAsync(steering).ConfigureAwait(false);
        }
    }

    private async Task<int> CommitSteeringAsync(IReadOnlyList<Message> steering)
    {
        var count = 0;
        foreach (var message in steering)
        {
            // 空消息直接跳过:提交一条空消息,下一次请求会被上游以「空内容」拒掉,而队列那边它已经离开了(2.x ★)。
            if (message.Parts.Count == 0)
            {
                continue;
            }

            await CommitAnnouncedAsync(message with { Role = Role.User }).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    private async Task<TurnResult> RunTurnAsync(TurnPlan plan)
    {
        // 请求里的工具列表总以快照为准:同一个快照既告诉模型有哪些工具,也用来按名字找回工具(2.x ★)。
        var request = plan.Request! with { Tools = plan.Tools.ModelTools };
        if (_hooks.BeforeProviderRequest is { } patch)
        {
            request = await patch(request, _ct).ConfigureAwait(false);
        }

        var streaming = await StreamWithResumeAsync(request).ConfigureAwait(false);

        // 上游真值(2.x 的 usageAnchor):只在收到 MessageEnd 的请求上记;锚点是发出这次请求时转录的最后一条消息。
        if (streaming.Asm.Completed)
        {
            _lastUsage = streaming.Asm.Usage;
            _lastUsageAnchor = _transcript.Count > 0 ? _transcript[^1].Id : null;
        }

        // ── 被动压缩(2.x ★):上游说提示词太长、而且一个内容字节都没有 → 让会话层压缩一次,丢弃这个回合重来;每回合最多一次 ──
        if (streaming.Asm.Error is { Code: AgentErrorCode.ContextLength }
            && streaming.Asm.Parts().Count == 0
            && !_contextRecovered
            && _hooks.RecoverContextLength is { } recover)
        {
            _contextRecovered = true;
            bool recovered;
            try
            {
                recovered = await Abortable(
                    recover(new TurnContext(_input.RunId, _input.SessionId, _turnIndex, _transcript.ToArray(), _input.Model, _input.Options, _toolCallsThisRun, _lastUsage, _lastUsageAnchor), _ct),
                    _ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _diagnostics.Warn("RecoverContextLength failed; the error is reported as usual.", e);
                recovered = false;
            }

            if (recovered)
            {
                _pending = null;
                if (streaming is { Started: true, Id: { } discarded })
                {
                    Emit(new MessageDiscardEvent(discarded));
                }

                return TurnResult.Next(takeSteering: false);
            }
        }

        if (streaming.Asm.Error is null)
        {
            _contextRecovered = false;
        }

        // ── 汇总成助手消息并提交(空消息不提交)──
        var message = BuildAssistant(streaming, aborted: false);
        var error = streaming.Asm.Error;
        var calls = message.Parts.OfType<ToolCallPart>().ToList();
        var committed = await CommitAssistantAsync(streaming, message).ConfigureAwait(false);
        _pending = null;
        if (committed)
        {
            foreach (var call in calls)
            {
                _runCalls.TryAdd(call.CallId, true);
            }
        }

        if (error is not null)
        {
            await CloseOrphansAsync(LoopMessages.StreamFailed).ConfigureAwait(false);
            return TurnResult.Finish(RunStatus.Error, error);
        }

        if (streaming.Asm.Stop == StopReason.MaxTokens && calls.Count > 0)
        {
            // pi 规则:撞上输出上限时,所有工具调用都判失败并提示重发(参数大概率被截断),模型有机会重新发出。
            await FailAllAsync(calls, LoopMessages.OutputLimit).ConfigureAwait(false);
            return TurnResult.Next();
        }

        if (calls.Count > 0)
        {
            _toolCallsThisRun += calls.Count;
            _stoppedTurnStreak = 0;
            var batch = await ExecuteToolsAsync(calls, plan.Tools).ConfigureAwait(false);
            return batch.AllTerminate ? TurnResult.Finish() : TurnResult.Next();
        }

        return await EndOfTurnAsync(message, streaming.Asm.Stop).ConfigureAwait(false);
    }

    /// <summary>
    /// 模型不再调用工具、本来要停的时候。顺序(2.x ★,位置被两头夹死):
    /// ① 队列里已经有引导消息 = 有活要干,不是停,也不必问 OnTurnEnd;② 问 OnTurnEnd(只在 end_turn 时;异常被吞,否则会无限重试);
    /// ③ 它要求继续就提交内部消息;④ 它没说立即结束的话,再看一次引导消息(等它判定的这段时间里可能又来了)。
    /// </summary>
    private async Task<TurnResult> EndOfTurnAsync(Message assistant, StopReason? stop)
    {
        var early = _hooks.TakeSteering().Where(m => m.Parts.Count > 0).ToList();
        if (early.Count > 0)
        {
            return TurnResult.Next(taken: early);
        }

        var decision = TurnEndDecision.Stop;
        if (stop == StopReason.EndTurn)
        {
            try
            {
                decision = await Abortable(
                    _hooks.OnTurnEnd(
                        new TurnEnd(_input.RunId, _input.SessionId, _turnIndex, assistant, stop, _transcript.ToArray(), _toolCallsThisRun, _stoppedTurnStreak, _stopHookActive),
                        _ct),
                    _ct).ConfigureAwait(false);
                _stopHookActive = true;
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // ★ 判定器抛异常必须在这里兜住:它每个 end_turn 回合都会被调用,一次逃出去的异常 = 无限重试同一个请求。
                _diagnostics.Warn("OnTurnEnd failed; the turn is treated as a normal stop.", e);
            }
        }

        _ct.ThrowIfCancellationRequested();
        if (decision.Notice is { } notice)
        {
            Emit(new NotificationEvent(notice.Key, notice.Detail));
        }

        if (decision is { Action: TurnEndAction.Continue, InternalMessage.Parts.Count: > 0 })
        {
            _stoppedTurnStreak++;
            await CommitAnnouncedAsync(decision.InternalMessage with { Role = Role.User, Internal = true }).ConfigureAwait(false);
            return TurnResult.Next(takeSteering: false);
        }

        if (decision.Action != TurnEndAction.Finish)
        {
            var late = _hooks.TakeSteering().Where(m => m.Parts.Count > 0).ToList();
            if (late.Count > 0)
            {
                return TurnResult.Next(taken: late);
            }
        }

        return TurnResult.Finish();
    }

    // ───────────────────────── 提交 ─────────────────────────

    /// <summary>提交一条 start / end 一起发的消息(用户、引导、内部、工具结果)。先落库,成功后才发 message_end。</summary>
    private async Task CommitAnnouncedAsync(Message message)
    {
        Emit(new MessageStartEvent(message.Id, message.Role));
        await CommitOrDiscardAsync(message).ConfigureAwait(false);
        _transcript.Add(message);
        Emit(new MessageEndEvent(message));
    }

    /// <summary>★ 永远不提交空的助手消息(上游会以「空内容」400)。已经发过 start 的丢弃事件补上,保证 start 总有收尾。</summary>
    private async Task<bool> CommitAssistantAsync(Streaming streaming, Message message)
    {
        if (message.Parts.Count == 0)
        {
            if (streaming is { Started: true, Id: { } started })
            {
                Emit(new MessageDiscardEvent(started));
            }

            return false;
        }

        EnsureStarted(streaming); // 只有一个 ErrorPart 的消息(还没有任何内容事件)也要先有 start
        await CommitOrDiscardAsync(message).ConfigureAwait(false);
        _transcript.Add(message);
        Emit(new MessageEndEvent(message, streaming.Asm.Usage, streaming.Asm.Cost, message.Stop));
        return true;
    }

    /// <summary>落库;失败时先告诉界面丢弃这条消息(保证 start 总有收尾),再把异常交给上层(Run 以错误结束)。</summary>
    private async Task CommitOrDiscardAsync(Message message)
    {
        try
        {
            await _hooks.CommitMessage(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Emit(new MessageDiscardEvent(message.Id));
            throw;
        }
    }

    // ───────────────────────── 孤儿修复 / 中止收尾 ─────────────────────────

    /// <summary>
    /// 给所有没有结果的调用补一条失败结果,提交成一条 tool_result 消息。四处都要修:
    /// 会话开始时(上一个 Run 崩溃留下的)、出错时、中止时、流中断放弃时。幂等:没有孤儿就什么都不做。
    /// </summary>
    private async Task CloseOrphansAsync(string reason)
    {
        var orphans = Orphans.Find(_transcript);
        if (orphans.Count == 0)
        {
            return;
        }

        var output = new ToolOutput(reason);
        foreach (var orphan in orphans)
        {
            // 这个 Run 里提交的调用补一对 start / end(卡片先出现再结束);上个 Run 留下的只补 end(卡片在历史里)。
            if (_runCalls.ContainsKey(orphan.CallId) && _startedCalls.TryAdd(orphan.CallId, true))
            {
                Emit(new ToolStartEvent(orphan.CallId, orphan.Name, orphan.Input, _input.Environment));
            }

            if (_endedCalls.TryAdd(orphan.CallId, true))
            {
                Emit(new ToolEndEvent(orphan.CallId, output, true, 0));
            }
        }

        var parts = orphans.Select(o => (Part)new ToolResultPart(o.CallId, output, true)).ToList();
        await CommitAnnouncedAsync(new Message(NewId(), Role.User, parts, Now())).ConfigureAwait(false);
    }

    /// <summary>
    /// 中止收尾(02 §6 第 4 件事):① 半截回复提交(没收尾的工具调用块丢弃,不制造孤儿;标 Aborted,回放时整条跳过);
    /// ② 给仍然没有结果的调用补「已中止」。漏掉第 ② 步,下一次请求就是 400。
    /// </summary>
    private async Task FinalizeAbortAsync()
    {
        if (_pending is { } pending)
        {
            _pending = null;
            var partial = BuildAssistant(pending, aborted: true);
            await CommitAssistantAsync(pending, partial).ConfigureAwait(false);
            foreach (var call in partial.Parts.OfType<ToolCallPart>())
            {
                _runCalls.TryAdd(call.CallId, true);
            }
        }

        await CloseOrphansAsync(LoopMessages.Interrupted).ConfigureAwait(false);
    }

    /// <summary>可中止的等待:取消令牌触发时立即返回(不等一个不认令牌的实现),被丢下的任务的异常被观察掉。</summary>
    private static async Task<T> Abortable<T>(ValueTask<T> work, CancellationToken ct)
    {
        var task = work.AsTask();
        try
        {
            return await task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !task.IsCompleted)
        {
            _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}
