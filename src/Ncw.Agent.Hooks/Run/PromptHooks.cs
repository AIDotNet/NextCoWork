using System.Diagnostics;

namespace Ncw.Agent.Hooks;

/// <summary>判定器的结论(与会话目标的判定同一套词)。</summary>
public enum EvaluationKind
{
    Met,
    NotMet,
    Impossible,
    Skipped,
}

/// <param name="Reason">Skipped 时是原因词(<c>timeout</c> 或别的:判定器挂了 / 没配);其余是判定器给的理由。</param>
public sealed record EvaluationVerdict(EvaluationKind Kind, string Reason = "");

/// <param name="Question">已经展开 <c>$ARGUMENTS</c> 的判定问题(含 Hook input)。</param>
/// <param name="EventJson">载荷 JSON(判定器可以把它当作「这一刻发生了什么」放进对话)。</param>
public sealed record EvaluationRequest(
    HookDefinition Hook, string Question, string EventJson, string SessionId, string RunId, string WorkspaceId, TimeSpan Timeout);

/// <summary>
/// prompt 型钩子的判定器(注入的窄口:服务层不知道模型层怎么调、供应商怎么选)。没有注入时 prompt 型钩子记成 <c>Skipped</c>(不阻断):
/// 一条判定钩子不该因为宿主没装判定器就把一次 run 拦下来。<b>判定器挂了 = 不阻断</b>,与命令钩子的 fail-open 一致:一次网络抖动不该变成「你的条件没达成」。
/// </summary>
public interface IHookEvaluator
{
    ValueTask<EvaluationVerdict> EvaluateAsync(EvaluationRequest request, CancellationToken ct);
}

/// <summary>
/// 一条 prompt 型钩子 = 一次判定调用,结果翻译成同一份 <see cref="HookRunReport"/>(调用方只看 decision / outcome / reason,不必知道这一条是 fork 了进程还是发了模型请求)。
/// 映射:<c>Met</c> → Ok;<c>NotMet</c> / <c>Impossible</c> → Blocked + deny(<b>Stop 上的「阻断」意思是这一轮继续跑</b>);<c>Skipped</c> → 超时 / 非 0 非 2 的退出码(自动进诊断)。
/// </summary>
public static class PromptHookRunner
{
    public static async Task<HookRunReport> RunAsync(
        IHookEvaluator? evaluator, HookDefinition hook, HookScope scope, HookPayload payload, string workspaceId, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        long Elapsed() => (long)Stopwatch.GetElapsedTime(started, Stopwatch.GetTimestamp()).TotalMilliseconds;
        if (evaluator is null)
        {
            return new HookRunReport(hook.Id, scope, 1, "", "evaluator unavailable", 0, HookOutcome.Ok, PromptVerdict: Ncw.Agent.Hooks.PromptVerdict.Skipped);
        }

        var json = payload.ToJson();
        var question = Expand(hook.Prompt ?? "", json);
        EvaluationVerdict verdict;
        try
        {
            verdict = await evaluator.EvaluateAsync(
                new EvaluationRequest(hook, question, json, payload.SessionId, payload.RunId, workspaceId, TimeSpan.FromMilliseconds(hook.TimeoutMs)), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            verdict = new EvaluationVerdict(EvaluationKind.Skipped, "error: " + e.GetType().Name);
        }

        var duration = Elapsed();
        switch (verdict.Kind)
        {
            case EvaluationKind.Met:
                return new HookRunReport(hook.Id, scope, 0, "", "", duration, HookOutcome.Ok, Reason: verdict.Reason.Length == 0 ? null : verdict.Reason, PromptVerdict: Ncw.Agent.Hooks.PromptVerdict.Met);
            case EvaluationKind.NotMet or EvaluationKind.Impossible:
                return new HookRunReport(
                    hook.Id, scope, 2, "", verdict.Reason, duration, HookOutcome.Blocked, HookDecision.Deny, verdict.Reason,
                    PromptVerdict: verdict.Kind == EvaluationKind.NotMet ? Ncw.Agent.Hooks.PromptVerdict.NotMet : Ncw.Agent.Hooks.PromptVerdict.Impossible);
            default:
                return verdict.Reason == "timeout"
                    ? new HookRunReport(hook.Id, scope, null, "", "evaluator timed out", duration, HookOutcome.Timeout, PromptVerdict: Ncw.Agent.Hooks.PromptVerdict.Skipped)
                    : new HookRunReport(hook.Id, scope, 1, "", $"evaluator {verdict.Reason}", duration, HookOutcome.Ok, PromptVerdict: Ncw.Agent.Hooks.PromptVerdict.Skipped);
        }
    }

    /// <summary>
    /// 把载荷替进 <c>$ARGUMENTS</c>,并附上 Hook input。<b>只做一遍替换</b>(<c>$ARGUMENTS</c> 出现在载荷文本里的不再二次展开)。
    /// </summary>
    public static string Expand(string prompt, string eventJson) =>
        $"{prompt.Replace("$ARGUMENTS", eventJson, StringComparison.Ordinal)}\n\nHook input:\n{eventJson}";

    /// <summary>「试运行」只预览替换结果、不真调模型:真跑需要一次 run 的上下文(判定器读的是转录,试运行时一条都没有),会得到一个真实场景里永远不会出现的结论。</summary>
    public static string Preview(string prompt, string eventJson) => prompt.Replace("$ARGUMENTS", eventJson, StringComparison.Ordinal);
}
