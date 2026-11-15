using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Permissions;

namespace Ncw.Agent.Hooks;

/// <summary>
/// 保存前的校验(2.x 渲染层的 <c>validateHook</c> / <c>warnHook</c> 里属于领域规则的部分:界面表单不是校验的唯一一道关——协议口也过它)。
/// 返回稳定 key(<see cref="HookErrorKeys"/>),控制端翻译;null = 通过。
/// </summary>
public static class HookValidation
{
    /// <summary>软警告的 key:阻断型钩子用了前缀 matcher。<b>不硬拦</b>,界面出一条黄条。</summary>
    public const string WeakMatcher = "hooks.warn.weakMatcher";

    public static string? Validate(HookDefinition hook)
    {
        if (!HookEvents.IsValid(hook.Event))
        {
            return HookErrorKeys.BadEvent;
        }

        if (hook.Matcher is { } matcher && matcher.Trim().Length > 0 && !PermissionRules.IsValid(matcher))
        {
            return HookErrorKeys.BadMatcher;
        }

        if (hook.TimeoutMs <= 0)
        {
            return HookErrorKeys.BadTimeout;
        }

        if (hook.TimeoutMs > HookLimits.MaxTimeoutMs)
        {
            return HookErrorKeys.TimeoutTooLong;
        }

        if ((hook.Description?.Length ?? 0) > HookLimits.DescriptionMax)
        {
            return HookErrorKeys.TooLong;
        }

        if (hook.Kind == HookKind.Prompt)
        {
            var prompt = hook.Prompt?.Trim() ?? "";
            return prompt.Length == 0 ? HookErrorKeys.EmptyPrompt : prompt.Length > HookLimits.PromptMax ? HookErrorKeys.TooLong : null;
        }

        var command = hook.Command?.Trim() ?? "";
        if (command.Length == 0)
        {
            return HookErrorKeys.EmptyCommand;
        }

        return command.Length > HookLimits.CommandMax ? HookErrorKeys.TooLong : ValidateEnv(hook.Env);
    }

    /// <summary>环境变量:个数、合法变量名、值无 NUL 且有长度上限、不得占用 <c>NCW_</c> 前缀(那是 Agent 自己设给钩子的)。</summary>
    public static string? ValidateEnv(IReadOnlyDictionary<string, string> env)
    {
        if (env.Count > HookLimits.EnvMaxCount)
        {
            return HookErrorKeys.BadEnv;
        }

        foreach (var (name, value) in env)
        {
            if (!CommandSpec.IsValidEnvName(name) || name.StartsWith("NCW_", StringComparison.Ordinal) || value.Contains('\0') || value.Length > HookLimits.EnvValueMax)
            {
                return HookErrorKeys.BadEnv;
            }
        }

        return null;
    }

    /// <summary>
    /// 阻断型钩子用了前缀规则(<c>Bash(rm:*)</c>)。前缀规则带着一条 shell 接续符的保护,那是给「放行」设计的(不命中就不放行,偏保守);
    /// 用在「拦截」上方向正好反了(<c>rm -rf / &amp;&amp; echo ok</c> 直接绕过去)。<b>只对工具类事件成立</b>:<c>Stop</c> 手里没有工具,matcher 被整个忽略。
    /// </summary>
    public static bool HasWeakBlockingMatcher(string hookEvent, string? matcher)
    {
        if (!HookEvents.Blocking.Contains(hookEvent, StringComparer.Ordinal) || !HookEvents.ToolScoped.Contains(hookEvent, StringComparer.Ordinal))
        {
            return false;
        }

        return PermissionRules.Parse(matcher ?? "")?.Specifier?.EndsWith(":*", StringComparison.Ordinal) == true;
    }

    public static string? Warn(HookDefinition hook) => HasWeakBlockingMatcher(hook.Event, hook.Matcher) ? WeakMatcher : null;
}
