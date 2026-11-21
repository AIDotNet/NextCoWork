using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Thinking;

public enum ThinkingMode
{
    /// <summary>模型不支持思考参数:任何思考字段都会被剥掉,补丁注入也不行。</summary>
    Unsupported,

    /// <summary>总是思考,由模型 id 本身决定:不发任何开关。</summary>
    Always,

    /// <summary>只有开 / 关。</summary>
    Toggle,

    /// <summary>token 预算。</summary>
    Budget,

    /// <summary>强度档位。</summary>
    Effort,
}

/// <summary>某个供应商 × 模型绑定接受的推理强度(<c>None</c> = 可以关闭)。</summary>
public enum ReasoningEffort
{
    None,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>模型目录对这个绑定的思考声明。</summary>
public sealed record ThinkingConfig(
    ThinkingMode Mode,
    bool DefaultEnabled = true,
    ReasoningEffort? DefaultEffort = null,
    int? DefaultBudgetTokens = null,
    string? ParameterPath = null,
    JsonElement? EnabledValue = null,
    JsonElement? DisabledValue = null,
    IReadOnlyDictionary<ReasoningEffort, JsonElement>? EffortMap = null,
    bool StandardWire = false,
    bool AnthropicAdaptive = false,
    ReasoningReplay? ReasoningReplay = null);

/// <summary>统一入参(<see cref="ThinkingLevel"/>)应用到一个模型的声明上的结果:协议无关,适配器消费它,从不需要猜 auto / xhigh / 预算是什么意思。</summary>
public sealed record ResolvedThinking(
    ThinkingMode Mode,
    bool Enabled,
    bool Explicit,
    ReasoningEffort? Effort = null,
    int? BudgetTokens = null);

public static class ThinkingBudgets
{
    public const int Minimum = 1024;
    private const int OutputHeadroom = 1024;

    /// <summary>统一档位对应的预算:minimal 1024 … max 64000。</summary>
    public static int Of(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Minimal => 1024,
        ThinkingLevel.Low => 4096,
        ThinkingLevel.Medium => 10_000,
        ThinkingLevel.High => 21_333,
        ThinkingLevel.XHigh => 32_000,
        ThinkingLevel.Max => 64_000,
        _ => 0,
    };

    public static int Of(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => 0,
        ReasoningEffort.Minimal => 1024,
        ReasoningEffort.Low => 4096,
        ReasoningEffort.Medium => 10_000,
        ReasoningEffort.High => 21_333,
        ReasoningEffort.XHigh => 32_000,
        ReasoningEffort.Max => 64_000,
        _ => 10_000,
    };

    /// <summary>预算上限 <c>maxOutputTokens - 1024</c>,下限 1024;满足不了返回 null(调用方降级为关闭,而不是报错)。</summary>
    public static int? Clamp(int wanted, int maxOutputTokens)
    {
        var ceiling = maxOutputTokens - OutputHeadroom;
        return ceiling < Minimum ? null : Math.Max(Minimum, Math.Min(wanted, ceiling));
    }
}

public static class ThinkingResolver
{
    private static ReasoningEffort EffortFor(ThinkingLevel level, ReasoningEffort fallback) => level switch
    {
        ThinkingLevel.Minimal => ReasoningEffort.Minimal,
        ThinkingLevel.Low => ReasoningEffort.Low,
        ThinkingLevel.Medium => ReasoningEffort.Medium,
        ThinkingLevel.High => ReasoningEffort.High,
        ThinkingLevel.Max => ReasoningEffort.Max,
        ThinkingLevel.XHigh => ReasoningEffort.XHigh,
        _ => fallback,
    };

    /// <summary>
    /// 统一档位 + 模型声明 → 协议无关的思考决定。不支持的模型返回 null:保证之后不会发出任何思考字段。
    /// 预算上限取这次真的会发出去的 max_tokens;满足不了下限时降级为关闭,而不是报错。
    /// </summary>
    public static ResolvedThinking? Resolve(
        ThinkingLevel level,
        ThinkingConfig? config,
        int maxOutputTokens,
        IReadOnlyList<ReasoningEffort>? efforts = null)
    {
        if (config is null || config.Mode == ThinkingMode.Unsupported)
        {
            return null;
        }

        var isExplicit = level != ThinkingLevel.Auto;
        var defaultEffort = config.DefaultEffort is { } d && (efforts is null || efforts.Contains(d))
            ? d
            : efforts?.Cast<ReasoningEffort?>().FirstOrDefault(e => e != ReasoningEffort.None) ?? ReasoningEffort.Medium;
        var canDisable = !(config.Mode == ThinkingMode.Effort && efforts is { Count: > 0 } && !efforts.Contains(ReasoningEffort.None));
        var enabled = config.Mode == ThinkingMode.Always
            || (level == ThinkingLevel.Off
                ? false
                : level == ThinkingLevel.Auto
                    ? (config.DefaultEnabled || !canDisable) && !(config.Mode == ThinkingMode.Effort && defaultEffort == ReasoningEffort.None)
                    : true);

        var baseResult = new ResolvedThinking(config.Mode, enabled, isExplicit);
        if (!enabled || config.Mode == ThinkingMode.Always)
        {
            return baseResult;
        }

        if (config.Mode == ThinkingMode.Effort)
        {
            return baseResult with { Effort = EffortFor(level, defaultEffort) };
        }

        // Budget 对 Anthropic 这类适配器也有用:目录里描述成简单开关的模型仍可能需要预算。
        var wanted = config.Mode == ThinkingMode.Toggle || level == ThinkingLevel.Auto
            ? config.DefaultBudgetTokens ?? ThinkingBudgets.Of(ThinkingLevel.Medium)
            : ThinkingBudgets.Of(level);
        return ThinkingBudgets.Clamp(wanted, maxOutputTokens) is { } budget
            ? baseResult with { BudgetTokens = budget }
            : baseResult with { Enabled = false };
    }
}

public static partial class ReasoningReplayPolicy
{
    [GeneratedRegex(@"(?:^|/)(?:deepseek|deep-seek)[/:._-]", RegexOptions.IgnoreCase)]
    internal static partial Regex DeepSeekName();

    [GeneratedRegex(@"(?:^|/)(?:glm|chatglm)[/:._-]", RegexOptions.IgnoreCase)]
    internal static partial Regex GlmName();

    [GeneratedRegex(@"(?:^|/)(?:hy4-preview|hy3(?:-preview)?)(?:$|[/:._-])", RegexOptions.IgnoreCase)]
    internal static partial Regex HunyuanName();

    /// <summary>
    /// 这次请求该按哪种方言回传历史思考。官方 OpenAI 输入侧 reasoning item 的 content 上限是 0(带正文整轮 400),
    /// DeepSeek 思考模式反过来要求正文全文回传(缺了同样 400),两边互斥,所以判定必须发生在知道上游是谁的这一层。
    /// 顺序:显式声明 &gt; 声明读标准线形的托管方(Ollama 跑 deepseek-* 名字的模型但读标准线形)&gt; 模型名兜底 &gt; 默认 opaque-only。
    /// </summary>
    public static ReasoningReplay For(ThinkingConfig? config, string upstreamModel)
    {
        if (config?.ReasoningReplay is { } explicitReplay)
        {
            return explicitReplay;
        }

        if (config?.StandardWire == true)
        {
            return ReasoningReplay.OpaqueOnly;
        }

        return DeepSeekName().IsMatch(upstreamModel) ? ReasoningReplay.TextRequired : ReasoningReplay.OpaqueOnly;
    }
}
