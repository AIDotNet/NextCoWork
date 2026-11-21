namespace Ncw.Agent.Extensions.Permissions;

/// <summary>PreToolUse 钩子 / 插件拦截器跑完之后归纳出来的那几种表态。</summary>
/// <param name="Deny">有人明确拒绝(带理由)。</param>
/// <param name="Allow">有钩子明确放行。插件拦截器不能放行(它的 allow 视为弃权,见 <see cref="IToolInterceptor"/>)。</param>
/// <param name="Ask">有人要求「问人」。</param>
/// <param name="UpdatedInput">PreToolUse 钩子改写后的工具入参(Claude Code <c>updatedInput</c>):存在时权限扩展用新入参重跑规则检查并交付 <see cref="Kernel.Loop.PreflightDecision.Edit"/>。</param>
public readonly record struct HookVerdict(string? Deny = null, bool Allow = false, bool Ask = false, System.Text.Json.JsonElement? UpdatedInput = null)
{
    public static HookVerdict None => default;
}

public readonly record struct EarlyDecision(bool Denied, string? Reason = null)
{
    public static EarlyDecision Continue => default;
}

public enum FinalKind
{
    Deny,
    Allow,

    /// <summary>交给 AI 审核器(<c>auto</c> 档 + 破坏性操作)。</summary>
    Review,

    /// <summary>发起交互,问人。</summary>
    Prompt,
}

public readonly record struct FinalDecision(FinalKind Kind, string? Reason = null);

/// <summary>
/// 权限判定的<b>顺序</b>(03 §3,2.x <c>kernel/permission-decision.ts</c>):纯函数。顺序就是语义——谁排在谁前面决定了「一条 allow 规则能不能盖过一条 deny 规则」。
/// 拆成「钩子之前 / 钩子之后」两段,是因为跑钩子有副作用(起进程):拿到 early deny 就直接返回,物理上到不了跑钩子那一步。
/// </summary>
public static class PermissionDecisions
{
    /// <summary>
    /// 钩子<b>之前</b>那两层。联网开关排第一:一条 allow 规则也不该能把用户关掉的开关重新打开。
    /// deny 桶排第二,<b>在钩子之前</b>:deny 是「连档位都放宽不了」的那一层,一条钩子不该能把它打开;命中时钩子根本不会被执行。
    /// </summary>
    public static EarlyDecision DecideBeforeHooks(GateOutcome gate, string? denyRule)
    {
        if (gate.Kind == GateKind.Deny)
        {
            return new EarlyDecision(true, gate.Reason);
        }

        if (denyRule is not null)
        {
            return new EarlyDecision(
                true,
                $"This call matches the deny rule `{denyRule}` in the project's permission settings. Do not try to reach the same result through another tool.");
        }

        return EarlyDecision.Continue;
    }

    /// <summary>
    /// 钩子<b>之后</b>的其余各层。优先级(高 → 低):
    /// ① 钩子 deny(排在 allow 桶之前,否则用户点过一次「以后都允许」就等于永久绕开了所有安全钩子);
    /// ② 强制问人(钩子 ask 或 ask 桶;压过 allow 与档位);
    /// ③ 钩子 allow;④ 档位放行 / allow 桶;⑤ auto + 破坏性 → AI 审核;⑥ 其余 → 问人。
    /// </summary>
    public static FinalDecision DecideAfterHooks(GateOutcome gate, HookVerdict hook, string? askRule, string? allowRule, bool autoReview)
    {
        if (hook.Deny is { } reason)
        {
            return new FinalDecision(FinalKind.Deny, reason);
        }

        var forcedAsk = hook.Ask || askRule is not null;
        if (!forcedAsk && (hook.Allow || gate.Kind == GateKind.Allow || allowRule is not null))
        {
            return new FinalDecision(FinalKind.Allow);
        }

        return autoReview && !forcedAsk ? new FinalDecision(FinalKind.Review) : new FinalDecision(FinalKind.Prompt);
    }
}

public enum ActionPreflightKind
{
    Allow,

    /// <summary>要用户确认:调用方把真实的工具与入参交给控制端,确认后再执行。</summary>
    Ask,

    Deny,
}

/// <summary><see cref="PermissionExtension.PreflightActionAsync"/> 的结论。<paramref name="Reason"/> 只在 <see cref="ActionPreflightKind.Deny"/> 时有值(给用户 / widget 看的说明)。</summary>
public readonly record struct ActionPreflight(ActionPreflightKind Kind, string? Reason = null);
