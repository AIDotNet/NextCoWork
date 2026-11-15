using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Domain;

namespace Ncw.Agent.Kernel.Session;

public sealed partial class AgentSession
{
    /// <summary>一个 Run 的可变状态(每个 Run 一个;长期状态只在 Storage)。钩子由循环层的单个生产者任务顺序调用,所以这里不加锁。</summary>
    private sealed class RunState
    {
        public required RunRequest Request { get; init; }

        public required RunDraft Draft { get; init; }

        public required ExtensionContext Ctx { get; init; }

        /// <summary>完整转录(库里的整段历史 + 本 Run 提交的消息)。发给模型的是它的 <see cref="BuildContext"/> 投影。</summary>
        public required List<Message> Messages { get; init; }

        public required GenerationOptions Options { get; init; }

        /// <summary>有效窗口:压缩判据和压力条的分母。</summary>
        public required int EffectiveWindow { get; init; }

        /// <summary>协议窗口:只用于硬校验,永远不受「最大上下文」开关影响。</summary>
        public required int ProtocolWindow { get; init; }

        /// <summary>
        /// ★ 运行级常量:时间、环境事实、项目指令、git 状态都在 Run 开始时定死(进稳定前缀),
        /// 放进每回合现算的内容里,等于每一回合为同一句话重新破一次 prompt cache。
        /// </summary>
        public required DateTimeOffset Now { get; init; }

        public EnvironmentPromptFacts? Environment { get; init; }

        public string? Instructions { get; init; }

        public GitContext? Git { get; init; }

        public MessageId? TailAnchor { get; set; }

        /// <summary>输入被扩展拦下:第一个回合直接以这个错误结束。</summary>
        public AgentError? Blocked { get; set; }

        public FileMutationQueue FileMutations { get; } = new();

        /// <summary>连续自动压缩失败的次数,到 <see cref="Compactor.MaxConsecutiveFailures"/> 熔断(不封顶的话,确定性失败会变成每一回合白发一次摘要请求)。</summary>
        public int CompactFailures { get; set; }

        /// <summary>上一次装配的输入量(压缩判据读的那个数),被动压缩记「压缩前占用」用。</summary>
        public long LastInputTokens { get; set; }

        /// <summary>被动压缩(<c>RecoverContextLength</c>)没有回合计划可以带事件,先攒着,下一个 <c>PrepareTurn</c> 放进计划里(保证事件在循环的事件流里有确定的位置)。</summary>
        public List<AgentEvent> PendingEvents { get; } = [];

        public void Upsert(Message message)
        {
            for (var i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].Id == message.Id)
                {
                    Messages[i] = message;
                    return;
                }
            }

            Messages.Add(message);
        }
    }

    private async Task<RunState> StartStateAsync(RunRequest request, RunDraft draft, IReadOnlyList<MessageRecord> records, ExtensionContext ctx, CancellationToken ct)
    {
        var binding = _deps.ResolveModel(request.Model);
        // ★ 查不到别名时不在这里报错,用兜底参数照常组装:让路由器的错误成为唯一的权威错误(它分得清「没有配置」「都在冷却」「没有已启用的供应商」)。
        var protocolWindow = binding?.ContextWindow is > 0 ? binding.ContextWindow : ContextWindows.FallbackWindow;
        var options = new GenerationOptions(
            ContextWindows.ResolveMaxOutputTokens(request.Settings.MaxOutputTokens, binding?.ContextWindow), request.Thinking);

        EnvironmentPromptFacts? facts = null;
        string? instructions = null;
        GitContext? git = null;
        var env = _deps.Environment;
        try
        {
            await env.EnsureReadyAsync(ct).ConfigureAwait(false);
            facts = EnvironmentPromptFacts.Of(env);
            if (request.Settings.ProjectInstructions)
            {
                var found = await ProjectInstructions.DiscoverAsync(
                    env, _deps.WorkspaceRoot, request.Settings.GlobalInstructions, request.Settings.ImportedInstructions, ct).ConfigureAwait(false);
                instructions = found.Text;
                foreach (var diagnostic in found.Diagnostics)
                {
                    Log.Warn($"Project instructions file skipped: {diagnostic.Key} ({diagnostic.Path})");
                }
            }

            git = await GitContextReader.ReadAsync(env, _deps.WorkspaceRoot, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is EnvironmentException or OperationCanceledException)
        {
            // 环境不可用:不读指令 / git,Run 照常开始——第一次工具调用会得到清楚的环境错误;中止由循环的收尾处理
            Log.Warn("The execution environment was not available while preparing the run.", e);
        }

        return new RunState
        {
            Request = request,
            Draft = draft,
            Ctx = ctx,
            Messages = [.. records.Select(MessageCodec.FromRecord)],
            Options = options,
            EffectiveWindow = ContextWindows.Effective(binding?.ContextWindow, request.MaxContext),
            ProtocolWindow = protocolWindow,
            Now = _deps.Clock.GetUtcNow(),
            Environment = facts,
            Instructions = instructions,
            Git = git,
        };
    }

    // ───────────────────────── 每回合:组装请求 ─────────────────────────

    /// <summary>
    /// 每个回合开始前(循环层的 <c>PrepareTurn</c> 基础装配,扩展的 <c>prepare_turn</c> 在它的草稿上就地修改):
    /// 刷新工具快照 → 组装请求 → 超过阈值就压缩(再组装)→ 硬校验 → 发 <c>context_usage</c>。
    /// </summary>
    private async ValueTask<TurnPlan> PrepareTurnAsync(RunState state, TurnContext turn, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // 被放弃的驱动在回合边界上直接收尾:不再组装请求、不再压缩、不再给上游发请求(白花钱),结局按已被编排层判定的 Aborted。
        if (SkipIfAbandoned(state.Request, "prepare_turn"))
        {
            return TurnPlan.Stop(RunStatus.Aborted);
        }

        if (state.Blocked is { } blocked)
        {
            return TurnPlan.Stop(RunStatus.Error, blocked);
        }

        var events = new List<AgentEvent>(state.PendingEvents);
        state.PendingEvents.Clear();

        // ★ 每回合取一次快照:同一份快照有两个用途——下发给模型的工具列表,和稍后按名字找回工具,必须是同一份。
        var tools = SnapshotTools(state);
        var assembled = AssembleTurn(state, turn, tools);

        // 占用过了阈值就在发请求之前压缩(Claude Code 每轮开头的 autocompact)
        if (state.Request.Settings.AutoCompact && assembled.Usage.ShouldCompact && state.CompactFailures < Compactor.MaxConsecutiveFailures
            && await CompactCoreAsync(state, "auto", assembled.InputTokens, instructions: null, events, ct).ConfigureAwait(false))
        {
            // 压缩成功后重新组装:上游真值描述的是压缩之前那份上下文,边界之前的消息不在新的上下文里,锚点自然对不上,判据退回纯估算。
            assembled = AssembleTurn(state, turn, tools);
        }

        // 压缩(摘要请求可能很慢)期间被放弃:压缩已经被跳过,这一回合也不再发请求。
        if (SkipIfAbandoned(state.Request, "prepare_turn"))
        {
            return TurnPlan.Stop(RunStatus.Aborted, events: events);
        }

        state.LastInputTokens = assembled.InputTokens;
        // ★ 在请求发出前发,不在收到响应后发:压力条要在这一轮真的挤爆之前就让用户看见
        events.Add(new ContextUsageEvent(assembled.Usage.Used, assembled.Usage.Window, assembled.Usage.ShouldCompact, assembled.Usage.Segments));

        // 协议窗口的硬校验(400 之前最后一道拦网),读的是和压缩判据同一个数
        if (assembled.InputTokens + state.Options.MaxOutputTokens > state.ProtocolWindow)
        {
            var error = AgentError.Create(
                AgentErrorCode.ContextLength,
                $"The conversation ({assembled.InputTokens} tokens) plus the reserved output ({state.Options.MaxOutputTokens}) exceeds the model context window ({state.ProtocolWindow}).",
                retryable: false,
                messageKey: SessionErrorKeys.ContextTooLong,
                messageParams: new Dictionary<string, string>
                {
                    ["used"] = assembled.InputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["window"] = state.ProtocolWindow.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            return TurnPlan.Stop(RunStatus.Error, error, events);
        }

        return TurnPlan.Run(assembled.Request, tools, events);
    }

    private Assembled AssembleTurn(RunState state, TurnContext turn, ToolSnapshot tools)
    {
        var context = BuildContext(state.Messages);
        var info = new SessionInfo(SessionId, WorkspaceId, state.Request.Depth, Tag);
        var sections = _host.CollectSystemSections(info, Log);
        var systemInput = new SystemPromptInput(
            _deps.WorkspaceRoot.Value,
            state.Now,
            state.Environment,
            state.Draft.AgentPrompt,
            state.Request.Settings.Personalization,
            state.Draft.ModePrompt,
            sections,
            tools);
        var reminder = new ReminderContext
        {
            ProjectInstructions = state.Instructions,
            HeadSections = state.Draft.HeadSections,
            Git = state.Git,
            StateSections = state.Draft.StateSections,
            TailAnchor = state.TailAnchor,
        };
        return ContextAssembler.Assemble(new AssembleInput(
            context, tools, SystemPrompt.Parts(systemInput), state.Options, reminder, state.EffectiveWindow, KnownInputTokens(turn, context)));
    }

    /// <summary>
    /// 「上一次上游真值 + 其后新增消息的估算」(Claude Code 的 <c>tokenCountWithEstimation</c>,2.x 的 <c>usageAnchor</c>)。
    /// 只读 chars/4 估算的话,代码 / JSON / CJK 偏低 25%+,用户看着圆环过了窗口,判据却说还早。
    /// 锚点是发出那次请求时转录的最后一条消息;它不在当前上下文里(压缩之后、或第一回合没有真值)就没有真值可用,判据退回纯估算。
    /// </summary>
    private static long? KnownInputTokens(TurnContext turn, IReadOnlyList<Message> context)
    {
        if (turn.LastUsage is not { } usage || turn.LastUsageAnchor is not { } anchor)
        {
            return null;
        }

        var at = -1;
        for (var i = context.Count - 1; i >= 0; i--)
        {
            if (context[i].Id == anchor)
            {
                at = i;
                break;
            }
        }

        return at < 0 ? null : TokenEstimator.PromptTokens(usage) + TokenEstimator.Messages(context.Skip(at + 1));
    }

    private ToolSnapshot SnapshotTools(RunState state)
    {
        var baseContext = Enrich(state, new ToolContext(
            state.Request.RunId, "", SessionId, state.Request.Depth, Tag, NullProgress.Instance, state.FileMutations));
        var bindings = new List<ToolBinding>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        void Add(ToolBinding binding)
        {
            if (state.Draft.ToolFilter is { } filter && !filter(binding.Tool.Info))
            {
                return;
            }

            if (!ToolInfo.IsValidName(binding.ExternalName) || !names.Add(binding.ExternalName))
            {
                Log.Warn($"Tool '{binding.ExternalName}' was left out of the snapshot: invalid or duplicate name.");
                return;
            }

            bindings.Add(binding);
        }

        foreach (var binding in _deps.Tools?.Snapshot(baseContext) ?? [])
        {
            Add(binding);
        }

        // 扩展注册的工具(tool_registry):外部名 = 内部 id
        foreach (var tool in _host.RegisteredTools)
        {
            if (tool.IsEnabled(baseContext))
            {
                Add(new ToolBinding(tool.Info.InternalId, tool));
            }
        }

        return new ToolSnapshot(bindings);
    }

    /// <summary>
    /// 被动压缩(2.x ★):上游以「提示词太长」拒绝而本地估算没拦住时,压缩一次再重发这一个回合。循环层保证只在一个内容字节都没有时、每回合最多问一次。
    /// 熔断照常生效;压缩失败返回 false(循环照常报 <c>ContextLength</c> 错误)。
    /// </summary>
    private async ValueTask<bool> RecoverContextLengthAsync(RunState state, TurnContext turn, CancellationToken ct)
    {
        if (!state.Request.Settings.AutoCompact || state.CompactFailures >= Compactor.MaxConsecutiveFailures)
        {
            return false;
        }

        var pre = turn.LastUsage is { } usage ? TokenEstimator.PromptTokens(usage) : state.LastInputTokens;
        return await CompactCoreAsync(state, "auto", Math.Max(pre, state.LastInputTokens), instructions: null, state.PendingEvents, ct).ConfigureAwait(false);
    }
}
