using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Kernel.Session;

public sealed partial class AgentSession
{
    /// <summary>
    /// 压缩一次(自动 / 被动共用):模型把边界之后的整段对话写成摘要,摘要 + 最近读过的文件作为一条带 <see cref="CompactBoundaryPart"/> 的 <b>Internal 用户消息</b>提交进转录,
    /// 此后上下文从这条消息开始。成功返回 true。事件(<c>compacting</c> → <c>compacted</c> / <c>failed</c> / <c>disabled</c>)追加到 <paramref name="events"/>,由调用方放进回合计划。
    /// ★ 边界消息走提交(落库):只改内存的话重开会话就回到压缩前,而用户在界面上什么也看不到。
    /// ★ 压缩模型解析在 <c>compacting</c> 事件<b>之后</b>:它可能 warn 一行并回落,不该让界面上的「正在压缩」晚出现。
    /// </summary>
    private async Task<bool> CompactCoreAsync(RunState state, string trigger, long preTokens, string? instructions, List<AgentEvent> events, CancellationToken ct)
    {
        var auto = trigger == "auto";
        // 被放弃的 Run 不再压缩:既不白发一次摘要请求,也不落库边界消息(不算失败,不计熔断,也不发状态事件)。
        if (SkipIfAbandoned(state.Request, "compact"))
        {
            return false;
        }

        events.Add(new ContextStatusEvent(ContextStatusPhase.Compacting, trigger));
        var settings = state.Request.Settings;
        var binding = CompactBinding.Resolve(
            _deps.ResolveModel, state.Request.Model, state.Request.Thinking, settings.Compaction, settings.WorkspaceCompaction,
            settings.MaxOutputTokens, message => Log.Warn(message));
        if (binding is null)
        {
            // 连会话模型都解析不到(供应商全删了 / 别名没了):走失败路径,由路由器去报权威错误
            return Failed(state, trigger, auto, "The compaction model is not available.", events);
        }

        var gate = await _host.GateAsync(SessionExtensionPoints.BeforeCompact, new CompactGateInput(SessionId, trigger, instructions), state.Ctx, ct).ConfigureAwait(false);
        if (gate is CompactGateDecision.CancelDecision cancel)
        {
            return Failed(state, trigger, auto, cancel.Reason, events);
        }

        var attachments = new CompactAttachmentDraft(SessionId, CompactBoundaries.ForModel(state.Messages));
        await _host.MutateAsync(SessionExtensionPoints.CompactAttachments, attachments, state.Ctx, ct).ConfigureAwait(false);

        CompactResult result;
        try
        {
            result = await _compactor.CompactAsync(
                new CompactRequest
                {
                    Messages = state.Messages,
                    Trigger = trigger,
                    Instructions = instructions,
                    PreTokens = preTokens,
                    AutoContinue = auto,
                    Binding = binding,
                    Context = new RequestContext(WorkspaceId, $"{state.Request.RunId.Value}:compact", SessionId),
                    FileTools = settings.FileTools,
                    ReadFile = (path, token) => Compactor.ReadAttachableFileAsync(_deps.Environment, state.WorkspaceRoot, path, token),
                    ExtraAttachments = attachments.Texts,
                    CustomSummary = (gate as CompactGateDecision.CustomDecision)?.Summary,
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result = new CompactResult.Failed(AgentError.Create(AgentErrorCode.Unknown, e.Message, retryable: true));
        }

        if (result is CompactResult.Failed failed)
        {
            return Failed(state, trigger, auto, failed.Error.Message, events);
        }

        // 摘要请求可能很慢:它期间 Run 被放弃的话,边界消息不能落库,after_compact 也不发。
        if (SkipIfAbandoned(state.Request, "compact_commit"))
        {
            return false;
        }

        var ok = (CompactResult.Ok)result;
        await CommitAsync(state, ok.Message).ConfigureAwait(false);
        state.CompactFailures = 0;
        // ★ 落库的消息必须在事件流里露面(2.x:边界消息走 message_commit):控制端的镜像只靠事件和重同步更新,
        // 边界消息悄悄进库的话,界面上没有分隔线,而下一次重载又突然出现。位置同 2.x:compacting → 边界消息 → compacted。
        events.Add(new MessageStartEvent(ok.Message.Id, ok.Message.Role));
        events.Add(new MessageEndEvent(ok.Message));
        events.Add(new ContextStatusEvent(ContextStatusPhase.Compacted, trigger));
        await _host.NotifyAsync(SessionExtensionPoints.AfterCompact, new AfterCompactEvent(SessionId, ok.Message, ok.Boundary), state.Ctx, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    /// <summary>失败计数 + 熔断相位。恒返回 false,让调用点写成 <c>return Failed(…)</c>。手动压缩不计数、不熔断(用户明确要求的,失败了就说失败)。</summary>
    private bool Failed(RunState state, string trigger, bool auto, string reason, List<AgentEvent> events)
    {
        Log.Warn($"Compaction failed: {reason}");
        if (!auto)
        {
            events.Add(new ContextStatusEvent(ContextStatusPhase.Failed, trigger));
            return false;
        }

        state.CompactFailures++;
        events.Add(new ContextStatusEvent(
            state.CompactFailures >= Compactor.MaxConsecutiveFailures ? ContextStatusPhase.Disabled : ContextStatusPhase.Failed, trigger));
        return false;
    }

    // ───────────────────────── 手动压缩 ─────────────────────────

    /// <summary>手动 <c>/compact [说明]</c> 的结果:成功时 <see cref="Boundary"/> 是已经落库的边界消息;失败时 <see cref="Error"/> 带 key。</summary>
    public sealed record ManualCompactOutcome(Message? Boundary, AgentError? Error, IReadOnlyList<AgentEvent> Events);

    /// <summary>
    /// 手动压缩。<b>只能在会话空闲时用</b>(占会话锁;会话里有 Run 在跑时抛 <see cref="SessionBusyException"/>)。和自动压缩走同一份代码
    /// (<see cref="Compactor"/>、同一个压缩模型绑定解析),区别只有:触发方式记为 <c>manual</c>、续接语不要求「别问了接着干」、失败不熔断。
    /// </summary>
    public async Task<ManualCompactOutcome> CompactNowAsync(
        ModelRef model, ThinkingLevel thinking, SessionSettings? settings = null, string? instructions = null, CancellationToken ct = default)
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
                return new ManualCompactOutcome(null, AgentError.Create(AgentErrorCode.Conflict, $"Session {SessionId} does not exist.", false, messageKey: SessionErrorKeys.NotFound), []);
            }

            var runId = new RunId("compact-" + _deps.Ids.NewId());
            var request = new RunRequest(runId, [], model, thinking) { Settings = settings ?? new SessionSettings() };
            var state = await StartStateAsync(request, new RunDraft(request), records, new ExtensionContext(runId, SessionId, Log), ct).ConfigureAwait(false);
            var events = new List<AgentEvent>();
            // 边界之后还没有任何真实对话 = 刚压过,再压一次只会拿摘要去摘要摘要
            if (!CompactBoundaries.ForModel(state.Messages).Any(m => !m.Internal))
            {
                return new ManualCompactOutcome(
                    null, AgentError.Create(AgentErrorCode.Conflict, "Nothing to compact yet.", false, messageKey: CompactionErrorKeys.NothingToCompact), events);
            }

            var pre = TokenEstimator.Messages(BuildContext(state.Messages));
            if (await CompactCoreAsync(state, "manual", pre, instructions, events, ct).ConfigureAwait(false))
            {
                return new ManualCompactOutcome(state.Messages[^1], null, events);
            }

            return new ManualCompactOutcome(null, AgentError.Create(AgentErrorCode.Provider, "Compaction failed.", true), events);
        }
        finally
        {
            _runLock.Release();
        }
    }
}
