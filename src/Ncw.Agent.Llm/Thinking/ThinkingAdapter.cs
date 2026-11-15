using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Thinking;

public sealed class ThinkingAdapterException(string message, string code) : Exception(message)
{
    /// <summary>机器可读原因,控制端按它翻译。</summary>
    public string Code { get; } = code;
}

/// <summary>请求适配器的预设:<c>Auto</c> 按协议和模型名决定方言。</summary>
public enum AdapterPreset
{
    Auto,
    Anthropic,
    OpenAiChat,
    OpenAiResponses,
    Custom,
}

public sealed record ThinkingAdapterInput(
    WireProtocol Protocol,
    string UpstreamModel,
    ThinkingConfig? Config,
    ResolvedThinking? Reasoning,
    int MaxOutputTokens,
    AdapterPreset Preset = AdapterPreset.Auto,
    IReadOnlyList<ReasoningEffort>? ReasoningEfforts = null);

/// <summary>
/// 思考参数适配(01-llm §6,2.x <c>thinking-adapter.ts</c>):协议无关的 <see cref="ResolvedThinking"/> → 具体请求体形状。
/// 显式关闭必须发出关闭参数(<c>effort: none</c> 或 <c>thinking: disabled</c>),不能省略字段(省略等于用模型默认值);
/// 模型无法关闭时报错,不把 Off 静默还原成默认强度。不支持的声明会剥掉所有已知思考字段。
/// </summary>
public static class ThinkingAdapter
{
    private enum Kind
    {
        Anthropic,
        OpenAiChat,
        OpenAiResponses,
        DeepSeek,
        Glm,
        Custom,
    }

    private static readonly string[] Roots =
        ["thinking", "reasoning", "reasoning_effort", "enable_thinking", "thinking_budget", "thinking_mode", "reasoning_split", "chat_template_kwargs", "thinkingConfig"];

    private static readonly HashSet<string> CustomPaths =
    [
        "thinking", "thinking.enabled", "thinking.type", "thinking.budget_tokens", "thinkingConfig.thinkingLevel",
        "chat_template_kwargs.enable_thinking", "chat_template_kwargs.reasoning_effort", "reasoning", "reasoning.effort",
        "reasoning_effort", "reasoning_split", "enable_thinking", "thinking_budget", "thinking_mode",
    ];

    private static void Strip(JsonObject body)
    {
        foreach (var key in Roots)
        {
            body.Remove(key);
        }
    }

    private static Kind KindFor(ThinkingAdapterInput input)
    {
        if (input.Config?.AnthropicAdaptive == true && input.Preset == AdapterPreset.Custom)
        {
            return ProtocolKind(input.Protocol);
        }

        switch (input.Preset)
        {
            case AdapterPreset.Anthropic:
                return Kind.Anthropic;
            case AdapterPreset.OpenAiChat:
                return Kind.OpenAiChat;
            case AdapterPreset.OpenAiResponses:
                return Kind.OpenAiResponses;
            case AdapterPreset.Custom:
                return Kind.Custom;
        }

        if (input.Protocol == WireProtocol.Anthropic)
        {
            return Kind.Anthropic;
        }

        if (input.Config?.AnthropicAdaptive == true)
        {
            return ProtocolKind(input.Protocol);
        }

        // standardWire:条目声明这家读标准线形时不套按模型名的厂商方言(Ollama 托管的 deepseek/glm 走这里)。
        // 必须放在名字启发式之前:官方 DeepSeek 的条目靠名字启发式补上它家要的 thinking:{type}。
        if (input.Config?.StandardWire == true)
        {
            return ProtocolKind(input.Protocol);
        }

        if (ReasoningReplayPolicy.DeepSeekName().IsMatch(input.UpstreamModel))
        {
            return Kind.DeepSeek;
        }

        if (ReasoningReplayPolicy.GlmName().IsMatch(input.UpstreamModel) || ReasoningReplayPolicy.HunyuanName().IsMatch(input.UpstreamModel))
        {
            return Kind.Glm;
        }

        if (input.Config?.ParameterPath is { } path && path is not ("reasoning_effort" or "reasoning.effort"))
        {
            return Kind.Custom;
        }

        return ProtocolKind(input.Protocol);
    }

    private static Kind ProtocolKind(WireProtocol protocol) => protocol switch
    {
        WireProtocol.Anthropic => Kind.Anthropic,
        WireProtocol.OpenAiChat => Kind.OpenAiChat,
        _ => Kind.OpenAiResponses,
    };

    private static string Wire(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => "none",
        ReasoningEffort.Minimal => "minimal",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.XHigh => "xhigh",
        ReasoningEffort.Max => "max",
        _ => "medium",
    };

    private static ReasoningEffort EffortForBudget(int? tokens) =>
        tokens is null || tokens <= ThinkingBudgets.Of(ThinkingLevel.Minimal) ? ReasoningEffort.Minimal
        : tokens <= ThinkingBudgets.Of(ThinkingLevel.Low) ? ReasoningEffort.Low
        : tokens <= ThinkingBudgets.Of(ThinkingLevel.Medium) ? ReasoningEffort.Medium
        : tokens <= ThinkingBudgets.Of(ThinkingLevel.High) ? ReasoningEffort.High
        : ReasoningEffort.Max;

    private static int BudgetForEffort(ReasoningEffort? effort, int maxOutputTokens)
    {
        var requested = effort is null ? ThinkingBudgets.Of(ThinkingLevel.Medium) : ThinkingBudgets.Of(effort.Value);
        var ceiling = Math.Max(1024, maxOutputTokens - 1024);
        return Math.Min(requested, ceiling);
    }

    private static JsonNode MappedEffort(ThinkingConfig config, ReasoningEffort effort) =>
        config.EffortMap is { } map && map.TryGetValue(effort, out var value) ? Jsn.ToNode(value) : Wire(effort);

    private static JsonNode? CustomValue(ThinkingConfig config, ResolvedThinking reasoning, string path)
    {
        if (config.Mode == ThinkingMode.Effort)
        {
            if (!reasoning.Enabled)
            {
                return config.DisabledValue is { } dv ? Jsn.ToNode(dv)
                    : config.EffortMap is { } m && m.TryGetValue(ReasoningEffort.None, out var none) ? Jsn.ToNode(none) : null;
            }

            return reasoning.Effort is { } effort
                ? MappedEffort(config, effort)
                : throw new ThinkingAdapterException($"Effort thinking is missing the effort level: {path}", "thinking.missingEffort");
        }

        var leaf = path.Split('.')[^1];
        if (config.Mode == ThinkingMode.Budget)
        {
            if (leaf is "enable_thinking" or "thinking_mode" || path is "thinking.enabled" or "reasoning_split")
            {
                throw new ThinkingAdapterException($"A token budget cannot be written to a switch parameter: {path}", "thinking.budgetOnSwitch");
            }

            if (!reasoning.Enabled)
            {
                return null;
            }

            return reasoning.BudgetTokens is { } budget
                ? budget
                : throw new ThinkingAdapterException($"Budget thinking is missing the token budget: {path}", "thinking.missingBudget");
        }

        if (config.Mode == ThinkingMode.Toggle && (path == "thinking_budget" || path.EndsWith(".budget_tokens", StringComparison.Ordinal)))
        {
            throw new ThinkingAdapterException($"A thinking switch cannot be written to a token budget parameter: {path}", "thinking.switchOnBudget");
        }

        if (reasoning.Enabled && config.EnabledValue is { } enabledValue)
        {
            return Jsn.ToNode(enabledValue);
        }

        if (!reasoning.Enabled && config.DisabledValue is { } disabledValue)
        {
            return Jsn.ToNode(disabledValue);
        }

        if (path.EndsWith(".type", StringComparison.Ordinal))
        {
            return reasoning.Enabled ? "enabled" : "disabled";
        }

        if (path == "thinking")
        {
            return new JsonObject { ["type"] = reasoning.Enabled ? "enabled" : "disabled" };
        }

        return reasoning.Enabled;
    }

    private static void SetDotted(JsonObject body, string path, JsonNode? value)
    {
        if (!CustomPaths.Contains(path))
        {
            throw new ThinkingAdapterException($"The thinking parameter path is not on the allow list: {path}", "thinking.pathNotAllowed");
        }

        var parts = path.Split('.');
        var cursor = body;
        foreach (var part in parts[..^1])
        {
            if (cursor[part] is null)
            {
                if (value is null)
                {
                    return;
                }

                var next = new JsonObject();
                cursor[part] = next;
                cursor = next;
                continue;
            }

            cursor = cursor[part] as JsonObject
                ?? throw new ThinkingAdapterException($"The parent of the thinking parameter path is not an object: {path}", "thinking.parentNotObject");
        }

        var leaf = parts[^1];
        if (value is null)
        {
            cursor.Remove(leaf);
        }
        else
        {
            cursor[leaf] = value;
        }
    }

    /// <summary>返回适配后的新请求体(输入不被修改)。</summary>
    public static JsonObject Apply(JsonObject body, ThinkingAdapterInput input)
    {
        var next = Jsn.Clone(body);
        if (input.Config is not { } config)
        {
            return next;
        }

        if (config.Mode == ThinkingMode.Unsupported)
        {
            Strip(next);
            return next;
        }

        // 总是思考:模型 id 本身选择推理,再发厂商开关既多余又会被不少纯推理 API 拒绝。
        if (config.Mode == ThinkingMode.Always)
        {
            Strip(next);
            return next;
        }

        var reasoning = input.Reasoning
            ?? throw new ThinkingAdapterException("The model declares thinking support but the request has no resolved thinking configuration.", "thinking.unresolved");
        var kind = KindFor(input);

        if (reasoning is { Enabled: true, Effort: { } requested } && input.ReasoningEfforts is { } supported && !supported.Contains(requested))
        {
            throw new ThinkingAdapterException($"The model does not support reasoning effort '{Wire(requested)}'.", "thinking.effortUnsupported");
        }

        if (reasoning is { Enabled: false, Explicit: true } && config.Mode == ThinkingMode.Effort
            && input.ReasoningEfforts is { } efforts && !efforts.Contains(ReasoningEffort.None))
        {
            throw new ThinkingAdapterException("The model cannot turn reasoning off; Off will not be silently restored to the default effort.", "thinking.cannotDisable");
        }

        switch (kind)
        {
            case Kind.Anthropic:
                return ApplyAnthropic(next, input, config, reasoning);
            case Kind.OpenAiChat or Kind.OpenAiResponses:
                return ApplyOpenAi(next, input, config, reasoning, kind == Kind.OpenAiResponses);
            case Kind.DeepSeek:
            {
                foreach (var key in new[] { "reasoning", "reasoning_effort", "enable_thinking", "thinking_budget" })
                {
                    next.Remove(key);
                }

                // DeepSeek 的 OpenAI 兼容 API 用独立的扁平 effort 字段,不接受 thinking.budget_tokens。
                if (reasoning.Enabled && config.Mode == ThinkingMode.Effort)
                {
                    next["reasoning_effort"] = MappedEffort(config, reasoning.Effort ?? EffortForBudget(reasoning.BudgetTokens));
                }

                next["thinking"] = new JsonObject { ["type"] = reasoning.Enabled ? "enabled" : "disabled" };
                return next;
            }

            case Kind.Glm:
            {
                foreach (var key in new[] { "reasoning", "reasoning_effort", "enable_thinking", "thinking_budget" })
                {
                    next.Remove(key);
                }

                next["thinking"] = new JsonObject { ["type"] = reasoning.Enabled ? "enabled" : "disabled" };
                if (reasoning.Enabled && config.Mode == ThinkingMode.Effort)
                {
                    next["reasoning_effort"] = MappedEffort(config, reasoning.Effort ?? EffortForBudget(reasoning.BudgetTokens));
                }

                return next;
            }

            default:
            {
                var path = config.ParameterPath?.Trim();
                if (string.IsNullOrEmpty(path))
                {
                    throw new ThinkingAdapterException("A custom thinking adapter needs a parameter path.", "thinking.noPath");
                }

                Strip(next);
                SetDotted(next, path, CustomValue(config, reasoning, path));
                return next;
            }
        }
    }

    private static JsonObject ApplyAnthropic(JsonObject next, ThinkingAdapterInput input, ThinkingConfig config, ResolvedThinking reasoning)
    {
        foreach (var key in new[] { "reasoning", "reasoning_effort", "enable_thinking", "thinking_budget" })
        {
            next.Remove(key);
        }

        if (!reasoning.Enabled)
        {
            // 兼容中转可能默认开启思考:省略字段不等于表达了用户的 Off。
            if (config.AnthropicAdaptive)
            {
                if (input.ReasoningEfforts is null || !input.ReasoningEfforts.Contains(ReasoningEffort.None))
                {
                    throw new ThinkingAdapterException("Turning off Anthropic adaptive thinking is not confirmed for this model.", "thinking.cannotDisable");
                }

                next["thinking"] = new JsonObject { ["type"] = "disabled" };
                if (next["output_config"] is JsonObject outputConfig)
                {
                    outputConfig.Remove("effort");
                    if (outputConfig.Count == 0)
                    {
                        next.Remove("output_config");
                    }
                }

                return next;
            }

            next["thinking"] = new JsonObject { ["type"] = "disabled" };
            return next;
        }

        // standardWire 的 Anthropic 线形(Ollama):档位走 output_config.effort,绝不能同时发 thinking.type——
        // 两个一起发时 effort 被静默无视,永远停在默认档。
        if (config.AnthropicAdaptive)
        {
            var effort = reasoning.Effort ?? throw new ThinkingAdapterException("Anthropic adaptive thinking needs a reasoning effort.", "thinking.missingEffort");
            if (effort is ReasoningEffort.None or ReasoningEffort.Minimal)
            {
                throw new ThinkingAdapterException($"Anthropic adaptive thinking does not support effort '{Wire(effort)}'.", "thinking.effortUnsupported");
            }

            next["thinking"] = new JsonObject { ["type"] = "adaptive" };
            var outputConfig = next["output_config"] as JsonObject ?? new JsonObject();
            outputConfig["effort"] = Wire(effort);
            next["output_config"] = outputConfig;
            return next;
        }

        if (config.StandardWire && reasoning.Effort is { } standardEffort)
        {
            next["output_config"] = new JsonObject { ["effort"] = Wire(standardEffort) };
            return next;
        }

        var budget = reasoning.BudgetTokens ?? BudgetForEffort(reasoning.Effort, input.MaxOutputTokens);
        next["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };

        // 开启 thinking 时 max_tokens 必须大于 budget_tokens,否则 400:不写在这里就会以「高思考档位下必然报错」的形式出现在用户面前。
        // 且 thinking 开启时不接受 temperature。
        next.Remove("temperature");
        if (next["max_tokens"] is JsonValue maxTokens && maxTokens.TryGetValue<int>(out var max) && max <= budget)
        {
            next["max_tokens"] = budget + 4096;
        }

        return next;
    }

    private static JsonObject ApplyOpenAi(JsonObject next, ThinkingAdapterInput input, ThinkingConfig config, ResolvedThinking reasoning, bool responses)
    {
        foreach (var key in new[] { "thinking", "enable_thinking", "thinking_budget", "reasoning", "reasoning_effort" })
        {
            next.Remove(key);
        }

        if (!reasoning.Enabled)
        {
            // 省略 effort 会让当前 OpenAI 推理模型回落到默认(通常是 medium),违背显式 Off。
            if (reasoning.Explicit)
            {
                if (input.ReasoningEfforts is { } efforts && !efforts.Contains(ReasoningEffort.None))
                {
                    throw new ThinkingAdapterException("The model cannot turn reasoning off; Off will not be silently restored to the default effort.", "thinking.cannotDisable");
                }

                var off = MappedEffort(config, ReasoningEffort.None);
                if (responses)
                {
                    next["reasoning"] = new JsonObject { ["effort"] = off };
                }
                else
                {
                    next["reasoning_effort"] = off;
                }
            }

            return next;
        }

        var effort = reasoning.Effort ?? EffortForBudget(reasoning.BudgetTokens);
        var mapped = MappedEffort(config, effort);
        if (responses)
        {
            next["reasoning"] = new JsonObject { ["effort"] = mapped };
        }
        else
        {
            next["reasoning_effort"] = mapped;
        }

        return next;
    }

    /// <summary>
    /// 补丁之后的最后一道闸:<c>always</c> / <c>unsupported</c> 模型不能收到任何思考开关(也包括 <c>extra_body</c> 透传容器);
    /// 会话里显式的 Off 压过已保存的模型补丁。
    /// </summary>
    public static JsonObject Enforce(JsonObject body, ThinkingAdapterInput input)
    {
        var guarded = Jsn.Clone(body);
        if (input.Config is { Mode: ThinkingMode.Unsupported or ThinkingMode.Always })
        {
            Strip(guarded);
            if (guarded["extra_body"] is JsonObject extra)
            {
                Strip(extra);
            }

            return guarded;
        }

        if (input.Config is null || input.Reasoning is not { Explicit: true, Enabled: false })
        {
            return guarded;
        }

        Strip(guarded);
        if (guarded["extra_body"] is JsonObject container)
        {
            Strip(container);
        }

        return Apply(guarded, input);
    }
}
