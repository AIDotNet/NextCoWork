using System.Text.RegularExpressions;

namespace Ncw.Agent.Llm.Thinking;

/// <summary>Claude 自适应思考模型表达「关闭思考」的方式。</summary>
public enum ClaudeOffMode
{
    /// <summary>发 <c>thinking: {type: "disabled"}</c>(Opus 4.6–5、Sonnet 4.6 / 5、Haiku 5.5)。</summary>
    Disabled,

    /// <summary>关不掉:省略 <c>thinking</c>,把强度降到最低档(Opus 5.5、Fable / Mythos)。<c>disabled</c> 会被 400。</summary>
    Omit,

    /// <summary>发 <c>thinking: {type: "between_tools"}</c>,不能带别的字段(Sonnet 5.5)。<c>disabled</c> 会被 400。</summary>
    BetweenTools,
}

/// <summary>
/// 一个 Claude 模型族的思考规则(issue #202)。Claude 4.6 起思考只用 <c>thinking: {type: "adaptive"}</c> + <c>output_config.effort</c>;
/// Opus 4.7+ / Sonnet 5+ / Haiku 5.5 / Fable 直接拒绝 <c>budget_tokens</c>,Opus 5.5 / Sonnet 5.5 / Fable 还拒绝 <c>disabled</c>。
/// </summary>
/// <param name="Efforts">接受的 <c>output_config.effort</c> 档位;其余档位就近收敛,不报错。</param>
/// <param name="SamplingRejected">非默认的 temperature / top_p / top_k 会被 400。</param>
/// <param name="DisplayDefaultsToOmitted">思考块默认不带正文(<c>display: "omitted"</c>),要看到思考摘要得显式要 <c>summarized</c>。</param>
public sealed record ClaudeThinkingProfile(
    ClaudeOffMode Off,
    IReadOnlyList<ReasoningEffort> Efforts,
    bool SamplingRejected,
    bool DisplayDefaultsToOmitted);

/// <summary>
/// 按上游模型名认出需要自适应思考的 Claude 模型。认不出(包括 Opus 4.5 / Sonnet 4.5 / Haiku 4.5 及更早)返回 null,
/// 走原来的 <c>budget_tokens</c> 方言。模型名允许带中转常见的前缀和写法:<c>anthropic/claude-opus-4.6</c>、
/// <c>anthropic.claude-sonnet-5-5</c>、<c>claude-opus-4-6-20260101</c>。
/// </summary>
public static partial class ClaudeThinking
{
    private static readonly ReasoningEffort[] AllEfforts =
        [ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.XHigh, ReasoningEffort.Max];

    // 4.6 没有 xhigh(xhigh 从 Opus 4.7 起才有)。
    private static readonly ReasoningEffort[] Efforts46 =
        [ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.Max];

    [GeneratedRegex(@"(?:^|[/:._-])claude-(opus|sonnet|haiku|fable|mythos)(?:-(\d{1,2})(?:[-.](\d{1,2}))?)?(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Name();

    public static ClaudeThinkingProfile? For(string? upstreamModel)
    {
        if (string.IsNullOrEmpty(upstreamModel) || Name().Match(upstreamModel) is not { Success: true } match)
        {
            return null;
        }

        var family = match.Groups[1].Value.ToLowerInvariant();
        if (family is "fable" or "mythos")
        {
            return new ClaudeThinkingProfile(ClaudeOffMode.Omit, AllEfforts, SamplingRejected: true, DisplayDefaultsToOmitted: true);
        }

        if (!match.Groups[2].Success)
        {
            return null;
        }

        var major = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var version = (major * 100) + minor;

        return family switch
        {
            "opus" when version >= 505 => new(ClaudeOffMode.Omit, AllEfforts, SamplingRejected: true, DisplayDefaultsToOmitted: true),
            "opus" when version >= 407 => new(ClaudeOffMode.Disabled, AllEfforts, SamplingRejected: true, DisplayDefaultsToOmitted: true),
            "opus" when version == 406 => new(ClaudeOffMode.Disabled, Efforts46, SamplingRejected: false, DisplayDefaultsToOmitted: false),
            "sonnet" when version >= 505 => new(ClaudeOffMode.BetweenTools, AllEfforts, SamplingRejected: true, DisplayDefaultsToOmitted: true),
            "sonnet" when version >= 500 => new(ClaudeOffMode.Disabled, AllEfforts, SamplingRejected: true, DisplayDefaultsToOmitted: true),
            "sonnet" when version == 406 => new(ClaudeOffMode.Disabled, Efforts46, SamplingRejected: false, DisplayDefaultsToOmitted: false),
            "haiku" when version >= 500 => new(ClaudeOffMode.Disabled, AllEfforts, SamplingRejected: true, DisplayDefaultsToOmitted: true),
            _ => null,
        };
    }

    /// <summary>把请求的档位收敛到模型接受的档位:低于下限取最低档,不支持的档位取下一个更低的可用档。</summary>
    public static ReasoningEffort Clamp(ReasoningEffort requested, IReadOnlyList<ReasoningEffort> supported)
    {
        if (supported.Contains(requested))
        {
            return requested;
        }

        var lower = supported.Where(e => e < requested).ToList();
        return lower.Count > 0 ? lower.Max() : supported.Min();
    }
}
