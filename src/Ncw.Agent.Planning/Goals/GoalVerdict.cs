using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ncw.Agent.Extensions.Planning.Goals;

public enum GoalSkipReason
{
    Timeout,
    Error,
    NoModel,
    Deferred,
    TranscriptEmpty,
}

/// <summary>判定器的一次结论。</summary>
public abstract record GoalVerdict
{
    private GoalVerdict()
    {
    }

    public sealed record Met(string Reason) : GoalVerdict;

    public sealed record NotMet(string Reason) : GoalVerdict;

    public sealed record Impossible(string Reason) : GoalVerdict;

    /// <summary>这次没判成(超时 / 出错 / 没有模型 / 转录空)。<b>不是</b>未达成:不算一轮,也不清目标。</summary>
    public sealed record Skipped(GoalSkipReason Reason) : GoalVerdict;
}

public static partial class GoalVerdicts
{
    /// <summary>
    /// 判定器那段文本 → <see cref="GoalVerdict"/>。契约是「JSON 写进 system prompt + 容错解析」,不给请求加 <c>responseFormat</c>:结构化输出在各家的支持程度不一样,
    /// 而供应商是用户自己挑的。容错解析跨家一致。
    /// ★ 解析失败<b>不是</b> <c>NotMet</c>:把工具的故障算在用户的目标头上,会让一次供应商抖动变成一条「你的目标没达成」——用户据此去改一个本来没问题的条件。
    /// </summary>
    public static GoalVerdict Parse(string text)
    {
        var payload = ExtractJson(text);
        if (payload is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return new GoalVerdict.Skipped(GoalSkipReason.Error);
        }

        string? reason = null;
        if (obj.TryGetProperty("reason", out var r))
        {
            if (r.ValueKind != JsonValueKind.String)
            {
                return new GoalVerdict.Skipped(GoalSkipReason.Error);
            }

            reason = r.GetString();
        }

        var impossible = false;
        if (obj.TryGetProperty("impossible", out var i))
        {
            if (i.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return new GoalVerdict.Skipped(GoalSkipReason.Error);
            }

            impossible = i.GetBoolean();
        }

        var trimmed = reason?.Trim() ?? "";
        if (ok.GetBoolean())
        {
            return new GoalVerdict.Met(trimmed);
        }

        return impossible ? new GoalVerdict.Impossible(trimmed) : new GoalVerdict.NotMet(trimmed);
    }

    /// <summary>
    /// 从一段可能夹着围栏和解释文字的模型输出里抠出那个 JSON 值。取向照抄审核器的容错解析:先试整段,再试 <c>```json</c> 围栏,最后试「第一个 <c>{</c> 到最后一个 <c>}</c>」。
    /// 三条都不成才算读不懂。第一个能解析成对象或数组的候选就是答案(数组随后过不了契约,所以 <c>[true]</c> 是读不懂,而不是继续往后找)。
    /// </summary>
    private static JsonElement? ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var candidates = new List<string> { trimmed };
        try
        {
            if (Fenced().Match(trimmed) is { Success: true } fenced)
            {
                candidates.Add(fenced.Groups[1].Value.Trim());
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // 放弃围栏这一条,其它候选照试
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            candidates.Add(trimmed[start..(end + 1)]);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    return doc.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // 试下一种写法
            }
        }

        return null;
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)\s*```", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex Fenced();
}
