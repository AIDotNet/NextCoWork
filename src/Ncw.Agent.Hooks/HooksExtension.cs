using System.Collections.Concurrent;
using System.Text;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Hooks;

/// <summary>发给模型的英文常量(不是界面文字)。</summary>
public static class HookPrompts
{
    /// <summary>Stop 钩子阻断收尾、让这一轮继续时,那条内部消息的开头(与目标续跑同一个前缀,格式与 CC 一致)。</summary>
    public const string StopFeedbackPrefix = "Stop hook feedback:";

    public const string StopBlockedFallback = "A Stop hook blocked stopping without giving a reason.";

    public const string PostToolBlockedFallback = "A PostToolUse hook rejected the result of this tool call.";

    public const string PreToolBlockedFallback = "A PreToolUse hook blocked this call.";

    public const string InputBlockedFallback = "A UserPromptSubmit hook blocked this prompt.";
}

/// <summary>被钩子拦下的输入的稳定 key(<c>AgentError.MessageKey</c>,控制端翻译)。</summary>
public static class HookErrorMessageKeys
{
    public const string PromptBlocked = "hooks.promptBlocked";
}

/// <summary>
/// Hooks 这个一方扩展(06 §3):<c>Id = hooks</c>,<c>Order = 100</c>(目标 500、待办 600——<c>turn_end</c> 闸门按 Order 取第一个不是 Stop 的决定,所以 Stop 钩子的阻断先于目标的续跑 / 强停 / 推迟)。
/// 每个工作区一个实例(项目钩子在这个工作区里读;和 <c>SubagentExtension</c> 一样)。使用的扩展点:
/// <list type="bullet">
/// <item><c>input</c>:<c>UserPromptSubmit</c>(阻断 = Run 以错误结束且输入不落库;<c>additionalContext</c> 进头块、<b>不进记录</b>)。只对顶层 Run、非内部输入;</item>
/// <item><c>before_run</c>:只记「这个 Run 是不是子代理」(后面的 <c>turn_end</c> / <c>run_end</c> 的扩展上下文里没有深度);</item>
/// <item><c>after_tool</c>:<c>PostToolUse</c>(<b>只能追加</b>反馈、翻转 <c>IsError</c>;退出码 2 翻成错误——「你的改动被 lint 钩子拒绝了」是模型能据此行动的信息);</item>
/// <item><c>turn_end</c> 闸门:<c>Stop</c>(阻断 = 本回合继续,带 <c>stop_hook_active</c> 防无限循环);只对顶层 Run;</item>
/// <item><c>run_end</c>:顶层 Run 没走过 <c>turn_end</c> 就收尾(出错 / 中止)时补一次不等待的 <c>Stop</c>(只跑命令型);子 Run 收尾发不等待的 <c>SubagentStop</c>。</item>
/// </list>
/// <c>PreToolUse</c> 不在这里:它走权限扩展在链里<b>调用</b>的接入口 <see cref="HooksService.PreToolSource"/>(钩子的 deny 要压过 allow 规则、ask 要压过档位放行,
/// 这些发生在链的中间,<c>before_tool</c> 的组合语义表达不了);<c>Notification</c> 经 <see cref="HooksService.AttachNotifications"/>。
/// </summary>
public sealed class HooksExtension : IAgentExtension
{
    public const string ExtensionId = "hooks";

    private sealed class RunInfo(int depth)
    {
        public int Depth { get; } = depth;

        public bool StopFired { get; set; }
    }

    private readonly HooksService _service;
    private readonly string _workspaceId;
    private readonly IIdGenerator _ids;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, RunInfo> _runs = new(StringComparer.Ordinal);

    internal HooksExtension(HooksService service, string workspaceId, IIdGenerator ids, TimeProvider clock)
    {
        _service = service;
        _workspaceId = workspaceId;
        _ids = ids;
        _clock = clock;
    }

    public string Id => ExtensionId;

    public int Order => 100;

    public void Register(IExtensionRegistry registry)
    {
        registry.Transform(SessionExtensionPoints.Input, OnInputAsync);
        registry.Mutate(SessionExtensionPoints.BeforeRun, (draft, ctx, _) =>
        {
            _runs[ctx.RunId.Value] = new RunInfo(draft.Request.Depth);
            return ValueTask.CompletedTask;
        });
        registry.Transform(LoopExtensionPoints.AfterTool, OnAfterToolAsync);
        registry.Gate(LoopExtensionPoints.TurnEnd, OnTurnEndAsync);
        registry.On(LoopExtensionPoints.RunEnd, OnRunEnd);
    }

    private int DepthOf(RunId runId) => _runs.TryGetValue(runId.Value, out var info) ? info.Depth : 0;

    // ───────────────────────── UserPromptSubmit ─────────────────────────

    private async ValueTask<InputPayload> OnInputAsync(InputPayload payload, ExtensionContext ctx, CancellationToken ct)
    {
        // 子 Run 没有「用户提交了提示词」这回事;内部输入(目标 kickoff、后台汇报)也不是用户提交的。
        var user = payload.Messages.Where(m => !m.Internal && m.Role == Role.User && m.Parts.Count > 0).ToList();
        if (payload.Depth > 0 || user.Count == 0)
        {
            return payload;
        }

        if (await _service.TryResolveAsync(_workspaceId, ct).ConfigureAwait(false) is not { } target)
        {
            return payload;
        }

        var prompt = string.Join('\n', user.SelectMany(m => m.Parts.OfType<TextPart>()).Select(p => p.Text));
        var reports = await _service.RunEventAsync(
            new HookEventRequest(HookEvents.UserPromptSubmit, target.Environment, target.Root, ctx.SessionId, ctx.RunId.Value, _workspaceId)
            { Customize = p => p with { Prompt = prompt } }, ct).ConfigureAwait(false);
        if (reports.Count == 0)
        {
            return payload;
        }

        if (reports.FirstOrDefault(r => r.Blocks) is { } blocked)
        {
            return payload with
            {
                BlockedReason = string.IsNullOrWhiteSpace(blocked.Reason) ? HookPrompts.InputBlockedFallback : blocked.Reason,
                BlockedKey = HookErrorMessageKeys.PromptBlocked,
            };
        }

        // 注入走头块(只进发出去的那份消息流):不出现在用户的聊天气泡里,也不被重放到旧会话。
        var injected = string.Join('\n', reports.Select(r => r.AdditionalContext ?? "").Where(s => s.Length > 0));
        return injected.Length == 0 ? payload : payload with { HeadSections = [.. payload.HeadSections, $"<hook-context>\n{injected}\n</hook-context>"] };
    }

    // ───────────────────────── PostToolUse ─────────────────────────

    private async ValueTask<AfterToolPayload> OnAfterToolAsync(AfterToolPayload payload, ExtensionContext ctx, CancellationToken ct)
    {
        if (await _service.TryResolveAsync(_workspaceId, ct).ConfigureAwait(false) is not { } target)
        {
            return payload;
        }

        var result = payload.Result;
        var output = string.Join('\n', result.Content.OfType<TextPart>().Select(p => p.Text));
        var reports = await _service.RunEventAsync(
            new HookEventRequest(HookEvents.PostToolUse, target.Environment, target.Root, ctx.SessionId, ctx.RunId.Value, _workspaceId)
            {
                ToolInternalId = payload.Invocation.Tool.Info.InternalId,
                ToolName = payload.Invocation.Name,
                ToolInput = payload.Invocation.Input,
                Customize = p => p with { ToolOutput = output, ToolIsError = result.IsError },
            }, ct).ConfigureAwait(false);
        if (reports.Count == 0)
        {
            return payload;
        }

        var blocked = reports.Any(r => r.Outcome == HookOutcome.Blocked);
        var context = string.Join('\n', reports.Select(r => r.AdditionalContext ?? r.Reason ?? "").Where(s => s.Length > 0));
        if (blocked && context.Length == 0)
        {
            context = HookPrompts.PostToolBlockedFallback;
        }

        if (context.Length == 0)
        {
            return payload;
        }

        // 只追加,不改写:界面、记录、模型看到的必须是同一份(Kernel 还会再校验一遍)。
        // 反馈包进 <hook-feedback>(2.x 的 agent-session.ts 同一个写法):让模型分得清「工具自己的输出」和「钩子脚本说的话」。
        // 内容块之间用一个换行连接,所以块首的换行凑成 2.x 的「输出 + 空行 + 标签」。
        return payload with { Result = result with { Content = [.. result.Content, new TextPart($"\n<hook-feedback>\n{context.Trim()}\n</hook-feedback>")], IsError = result.IsError || blocked } };
    }

    // ───────────────────────── Stop ─────────────────────────

    private async ValueTask<TurnEndDecision> OnTurnEndAsync(TurnEnd end, ExtensionContext ctx, CancellationToken ct)
    {
        if (DepthOf(end.RunId) > 0)
        {
            return TurnEndDecision.Stop;
        }

        if (_runs.TryGetValue(end.RunId.Value, out var info))
        {
            info.StopFired = true;
        }

        if (await _service.TryResolveAsync(_workspaceId, ct).ConfigureAwait(false) is not { } target)
        {
            return TurnEndDecision.Stop;
        }

        var reports = await _service.RunEventAsync(
            new HookEventRequest(HookEvents.Stop, target.Environment, target.Root, ctx.SessionId, ctx.RunId.Value, _workspaceId)
            { Customize = p => p with { Status = "done", StopHookActive = end.StopHookActive } }, ct).ConfigureAwait(false);
        var blocking = reports.Where(r => r.Blocks).ToList();
        if (blocking.Count == 0)
        {
            return TurnEndDecision.Stop;
        }

        // 阻断收尾 = 这一轮继续跑:反馈作为一条 internal 用户消息交回模型。
        var reasons = string.Join('\n', blocking.Select(r => string.IsNullOrWhiteSpace(r.Reason) ? HookPrompts.StopBlockedFallback : r.Reason));
        var message = new Message(
            new MessageId(_ids.NewId()), Role.User, [new TextPart($"{HookPrompts.StopFeedbackPrefix}\n{reasons}")], _clock.GetUtcNow(), Internal: true);
        return TurnEndDecision.Continue(message);
    }

    // ───────────────────────── run_end:Stop 兜底 / SubagentStop ─────────────────────────

    private ValueTask OnRunEnd(RunEndEvent end, ExtensionContext ctx, CancellationToken ct)
    {
        _runs.TryRemove(ctx.RunId.Value, out var info);
        var status = end.Status switch { RunStatus.Done => "done", RunStatus.Aborted => "aborted", _ => "error" };
        var depth = info?.Depth ?? 0;
        if (depth > 0)
        {
            Notify(HookEvents.SubagentStop, ctx, status, commandOnly: false);
        }
        else if (info is { StopFired: false })
        {
            // 同一轮收尾不能把 Stop 事件发两遍(用户的通知脚本会响两次):走过 turn_end 的这里跳过。
            // 中止 / 出错的收尾只通知命令型,不能再发起一次判定,也不能阻断(没有「继续」可言)。
            Notify(HookEvents.Stop, ctx, status, commandOnly: true);
        }

        return ValueTask.CompletedTask;
    }

    private void Notify(string hookEvent, ExtensionContext ctx, string status, bool commandOnly)
    {
        var workspaceId = _workspaceId;
        _service.Fire(async token =>
        {
            if (await _service.TryResolveAsync(workspaceId, token).ConfigureAwait(false) is not { } target)
            {
                return;
            }

            await _service.RunEventAsync(
                new HookEventRequest(hookEvent, target.Environment, target.Root, ctx.SessionId, ctx.RunId.Value, workspaceId)
                {
                    CommandOnly = commandOnly,
                    Customize = p => p with { Status = status, StopHookActive = hookEvent == HookEvents.Stop ? false : null },
                }, token).ConfigureAwait(false);
        });
    }
}

/// <summary>权限扩展在链里调用的 PreToolUse 接入口(03 §3 判定链第 3–6 步)。钩子失败 = 弃权(权限扩展按失败放行处理并记诊断)。</summary>
internal sealed class HookPreToolSource(HooksService service) : IPreToolVerdictSource
{
    public async ValueTask<HookVerdict> EvaluateAsync(PreToolContext context, CancellationToken ct)
    {
        var run = context.Run;
        var reports = await service.RunEventAsync(
            new HookEventRequest(HookEvents.PreToolUse, run.Environment, run.Root, run.SessionId, run.RunId.Value, run.WorkspaceId)
            {
                ToolInternalId = context.Call.Tool.Info.InternalId,
                ToolName = context.Call.Name,
                ToolInput = context.Input,
            }, ct).ConfigureAwait(false);
        if (reports.Count == 0)
        {
            return HookVerdict.None;
        }

        string? deny = null;
        if (reports.FirstOrDefault(r => r.Blocks) is { } blocked)
        {
            deny = string.IsNullOrWhiteSpace(blocked.Reason) ? HookPrompts.PreToolBlockedFallback : blocked.Reason;
        }

        return new HookVerdict(deny, reports.Any(r => r.Decision == HookDecision.Allow), reports.Any(r => r.Decision == HookDecision.Ask));
    }
}

public sealed partial class HooksService
{
    private HookPreToolSource? _preTool;

    /// <summary>PreToolUse 的接入口:交给 <c>PermissionExtension(hooks: …)</c>。</summary>
    public IPreToolVerdictSource PreToolSource => _preTool ??= new HookPreToolSource(this);

    /// <summary>某个工作区的 Hooks 会话扩展(<c>AgentSessionDeps.Extensions</c>)。</summary>
    public IAgentExtension CreateExtension(string workspaceId) =>
        new HooksExtension(this, workspaceId, _ids, _options.Clock);

    /// <summary>项目落点;解析失败(项目没了 / 环境不可用)按「没有」处理并记诊断——钩子的失败不该变成 Run 的失败。</summary>
    internal async ValueTask<HookProjectTarget?> TryResolveAsync(string workspaceId, CancellationToken ct)
    {
        try
        {
            return await _options.ResolveProject(workspaceId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            Log.Warn($"[hook] the project environment for {workspaceId} is not available; hooks are skipped.", e);
            return null;
        }
    }

    /// <summary>
    /// <c>Notification</c>(「有个弹窗在等你」的那一刻,典型用法是 <c>osascript -e 'display notification …'</c>):订阅交互闸门的 <c>tool_permission</c> 请求。
    /// <b>fire-and-forget</b>:不 await、不看结果——审批弹窗已经在等人了,再为一条通知脚本多等几秒只会让用户更晚看到它。
    /// </summary>
    public void AttachNotifications(InteractionGate gate, PermissionRuns runs)
    {
        gate.RunEvent += (runId, ev) =>
        {
            if (ev is not InteractionRequestEvent { Interaction: ToolPermission permission } || runs.Get(runId) is not { } run)
            {
                return;
            }

            Fire(async token => await RunEventAsync(
                new HookEventRequest(HookEvents.Notification, run.Environment, run.Root, run.SessionId, runId.Value, run.WorkspaceId)
                {
                    ToolInternalId = permission.InternalId,
                    ToolName = permission.ToolName,
                    ToolInput = permission.Input,
                    Customize = p => p with { Notification = new HookNotification("tool_permission", permission.ToolName) },
                }, token).ConfigureAwait(false));
        };
    }
}
