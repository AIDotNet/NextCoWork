using Ncw.Agent.Llm;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Kernel.Extensions;

/// <summary>
/// 扩展宿主:收集各扩展注册的处理器,按「点的种类」的组合语义派发(06 §2)。
/// 规则:<b>扩展处理器的异常不传播到循环</b>——通知类记日志忽略;就地修改类记日志、保持已做的修改;链式变换类保持上一个值;
/// 闸门类按「放行(弃权)」处理并记诊断,<b>除非注册时给了 <c>onFailure</c></b>(权限扩展:异常按拒绝)。
/// 取消异常(令牌已触发)一律原样传播。处理器按 <see cref="IAgentExtension.Order"/> 排序,相同按注册顺序。
/// </summary>
public sealed class ExtensionHost
{
    private sealed record Entry(int Order, int Seq, string ExtensionId, object Handler, object? OnFailure);

    private sealed record SectionEntry(int Order, int Seq, string ExtensionId, string Name, Func<SessionInfo, string?> Section);

    private readonly Dictionary<string, List<Entry>> _handlers = new(StringComparer.Ordinal);
    private readonly List<SectionEntry> _sections = [];
    private readonly List<Tools.IAgentTool> _tools = [];
    private readonly List<CommandDefinition> _commands = [];
    private int _seq;

    public ExtensionHost(IEnumerable<IAgentExtension> extensions)
    {
        foreach (var extension in extensions)
        {
            extension.Register(new Registrar(this, extension));
        }
    }

    private sealed class Registrar(ExtensionHost host, IAgentExtension extension) : IExtensionRegistry
    {
        public void On<TEvent>(ExtensionPoint<TEvent> point, Func<TEvent, ExtensionContext, CancellationToken, ValueTask> handler) =>
            host.Add(point.Name, extension, handler, null);

        public void Mutate<T>(MutatePoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask> handler)
            where T : class => host.Add(point.Name, extension, handler, null);

        public void Transform<T>(TransformPoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask<T>> handler) =>
            host.Add(point.Name, extension, handler, null);

        public void Gate<TEvent, TDecision>(
            GatePoint<TEvent, TDecision> point,
            Func<TEvent, ExtensionContext, CancellationToken, ValueTask<TDecision>> handler,
            Func<Exception, TDecision>? onFailure = null) =>
            host.Add(point.Name, extension, handler, onFailure);

        public void LastWins<T>(LastWinsPoint<T> point, Func<T, ExtensionContext, CancellationToken, ValueTask<T?>> handler)
            where T : class => host.Add(point.Name, extension, handler, null);

        public void RegisterTool(Tools.IAgentTool tool)
        {
            ArgumentNullException.ThrowIfNull(tool);
            host._tools.Add(tool);
        }

        public void RegisterCommand(CommandDefinition command)
        {
            ArgumentNullException.ThrowIfNull(command);
            host._commands.Add(command);
        }

        public void ContributeSystemSection(string name, Func<SessionInfo, string?> section)
        {
            ArgumentNullException.ThrowIfNull(section);
            host._sections.Add(new SectionEntry(extension.Order, host._seq++, extension.Id, name, section));
            host._sections.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Seq.CompareTo(b.Seq));
        }
    }

    /// <summary>扩展注册的工具,按注册顺序。</summary>
    public IReadOnlyList<Tools.IAgentTool> RegisteredTools => _tools;

    /// <summary>扩展注册的斜杠命令(<c>RegisterCommand</c>),按注册顺序。</summary>
    public IReadOnlyList<CommandDefinition> RegisteredCommands => _commands;

    /// <summary>
    /// 收集所有扩展贡献的系统提示词节(<c>system_sections</c>):异常 = 这一节弃权(记诊断),空节跳过。
    /// 返回 (节名, 正文),已按 <see cref="IAgentExtension.Order"/> 排序。
    /// </summary>
    public IReadOnlyList<(string Name, string Text)> CollectSystemSections(SessionInfo info, ILoopDiagnostics log)
    {
        var result = new List<(string, string)>();
        foreach (var entry in _sections)
        {
            try
            {
                if (entry.Section(info) is { Length: > 0 } text && text.Trim().Length > 0)
                {
                    result.Add((entry.Name, text));
                }
            }
            catch (Exception e)
            {
                log.Warn($"Extension '{entry.ExtensionId}' failed contributing system section '{entry.Name}'.", e);
            }
        }

        return result;
    }

    private void Add(string name, IAgentExtension extension, object handler, object? onFailure)
    {
        if (!_handlers.TryGetValue(name, out var list))
        {
            list = [];
            _handlers[name] = list;
        }

        list.Add(new Entry(extension.Order, _seq++, extension.Id, handler, onFailure));
        list.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Seq.CompareTo(b.Seq));
    }

    private IReadOnlyList<Entry> For(string name) => _handlers.TryGetValue(name, out var list) ? list : [];

    public int HandlerCount(string pointName) => For(pointName).Count;

    private static bool IsCancellation(Exception e, CancellationToken ct) => e is OperationCanceledException && ct.IsCancellationRequested;

    public async ValueTask NotifyAsync<TEvent>(ExtensionPoint<TEvent> point, TEvent ev, ExtensionContext ctx, CancellationToken ct)
    {
        foreach (var entry in For(point.Name))
        {
            try
            {
                await ((Func<TEvent, ExtensionContext, CancellationToken, ValueTask>)entry.Handler)(ev, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (!IsCancellation(e, ct))
            {
                ctx.Log.Warn($"Extension '{entry.ExtensionId}' failed at {point.Name}.", e);
            }
        }
    }

    public async ValueTask MutateAsync<T>(MutatePoint<T> point, T value, ExtensionContext ctx, CancellationToken ct)
        where T : class
    {
        foreach (var entry in For(point.Name))
        {
            try
            {
                await ((Func<T, ExtensionContext, CancellationToken, ValueTask>)entry.Handler)(value, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (!IsCancellation(e, ct))
            {
                ctx.Log.Warn($"Extension '{entry.ExtensionId}' failed at {point.Name}.", e);
            }
        }
    }

    public async ValueTask<T> TransformAsync<T>(TransformPoint<T> point, T value, ExtensionContext ctx, CancellationToken ct)
    {
        foreach (var entry in For(point.Name))
        {
            try
            {
                value = await ((Func<T, ExtensionContext, CancellationToken, ValueTask<T>>)entry.Handler)(value, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (!IsCancellation(e, ct))
            {
                ctx.Log.Warn($"Extension '{entry.ExtensionId}' failed at {point.Name}.", e);
            }
        }

        return value;
    }

    public async ValueTask<TDecision> GateAsync<TEvent, TDecision>(GatePoint<TEvent, TDecision> point, TEvent ev, ExtensionContext ctx, CancellationToken ct)
    {
        var result = point.Default;
        foreach (var entry in For(point.Name))
        {
            TDecision decision;
            try
            {
                decision = await ((Func<TEvent, ExtensionContext, CancellationToken, ValueTask<TDecision>>)entry.Handler)(ev, ctx, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (!IsCancellation(e, ct))
            {
                ctx.Log.Warn($"Extension '{entry.ExtensionId}' failed at {point.Name}.", e);
                if (entry.OnFailure is not Func<Exception, TDecision> onFailure)
                {
                    continue; // 弃权 = 放行
                }

                decision = onFailure(e);
            }

            if (point.ShortCircuits(decision))
            {
                return decision;
            }

            if (point.Merge is { } merge)
            {
                result = merge(result, decision);
            }
        }

        return result;
    }

    public async ValueTask<T> LastWinsAsync<T>(LastWinsPoint<T> point, T value, ExtensionContext ctx, CancellationToken ct)
        where T : class
    {
        var result = value;
        foreach (var entry in For(point.Name))
        {
            try
            {
                if (await ((Func<T, ExtensionContext, CancellationToken, ValueTask<T?>>)entry.Handler)(value, ctx, ct).ConfigureAwait(false) is { } replacement)
                {
                    result = replacement;
                }
            }
            catch (Exception e) when (!IsCancellation(e, ct))
            {
                ctx.Log.Warn($"Extension '{entry.ExtensionId}' failed at {point.Name}.", e);
            }
        }

        return result;
    }

    /// <summary>
    /// 把已注册的扩展接成循环层钩子(02 §1 的 <see cref="LoopHooks"/> 是本表的循环层子集)。
    /// <paramref name="baseline"/> 是会话层的基础装配(取上下文、组装请求、快照工具),扩展在它的草稿上就地修改。
    /// 落库、取引导消息不是扩展点,由会话层直接传入。
    /// </summary>
    public LoopHooks CreateLoopHooks(
        ExtensionContext ctx,
        Func<TurnContext, CancellationToken, ValueTask<TurnPlan>> baseline,
        Func<IReadOnlyList<Message>>? takeSteering = null,
        Func<Message, CancellationToken, ValueTask>? commit = null,
        Func<Tools.ToolContext, Tools.ToolContext>? enrichToolContext = null,
        Func<TurnContext, CancellationToken, ValueTask<bool>>? recoverContextLength = null) => new()
    {
        PrepareTurn = async (turn, ct) =>
        {
            var draft = new TurnDraft(turn, await baseline(turn, ct).ConfigureAwait(false));
            await MutateAsync(LoopExtensionPoints.PrepareTurn, draft, ctx, ct).ConfigureAwait(false);
            return draft.ToPlan();
        },
        BeforeProviderRequest = HandlerCount(LoopExtensionPoints.BeforeProviderRequest.Name) == 0
            ? null
            : (request, ct) => LastWinsAsync(LoopExtensionPoints.BeforeProviderRequest, request, ctx, ct),
        BeforeTool = (call, ct) => GateAsync(LoopExtensionPoints.BeforeTool, call, ctx, ct),
        AfterTool = async (call, result, ct) =>
            (await TransformAsync(LoopExtensionPoints.AfterTool, new AfterToolPayload(call, result), ctx, ct).ConfigureAwait(false)).Result,
        OnTurnEnd = (end, ct) => GateAsync(LoopExtensionPoints.TurnEnd, end, ctx, ct),
        TakeSteering = takeSteering ?? (static () => []),
        CommitMessage = commit ?? (static (_, _) => ValueTask.CompletedTask),
        EnrichToolContext = enrichToolContext,
        RecoverContextLength = recoverContextLength,
    };
}
