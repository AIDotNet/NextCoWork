using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog;

/// <summary>
/// 别名绑定 + 目录元数据 → Llm 层路由用的 <see cref="ModelBinding"/>(2.x <c>modelBindingResolver.resolve</c> 的 3.0 版)。
/// 字段的归属沿用 2.x 的 <c>catalogOverrides</c> 语义:<b>用户显式设过的字段(列在别名的 <c>catalogOverrides</c> 里)归供应商别名,其余跟随目录</b>。
/// 这样 models.dev 更新后上下文窗口、能力会自动跟上,而用户改过的不会被冲掉。
/// <para>
/// 接管 2.x 数据时,老别名没有 <c>catalogOverrides</c>(2.x 靠推断):沿用同一个保守规则——别名里存着的值不是「导入占位缺省」
/// (窗口 200000、输出 8192、能力 vision=true)就当用户的;存着的 <c>thinkingConfig</c> / <c>reasoningEfforts</c> / <c>requestAdapter</c>
/// 一律照用(它们当年来自 2.x 已核实的内置目录,比 models.dev 推出来的更可信)。
/// </para>
/// <para>
/// 思考方言 models.dev 不提供。目录推出的配置只覆盖有把握的两类(宁可少一个开关,也不发一个会被上游 400 的参数):
/// ① models.dev 给了推理强度档位 → <c>Effort</c> 模式;② 一手厂商(Anthropic 协议的推理模型 → <c>Budget</c>,默认不主动开启;
/// OpenAI / Google 经 OpenAI 族协议 → <c>Effort</c>)。其余推理模型按「不支持思考参数」处理,用户可以在别名里显式声明。
/// </para>
/// </summary>
public static class ModelBindingResolver
{
    public static ModelBinding Resolve(ModelAliasRecord alias, ProviderRecord provider, ModelInfo? info)
    {
        var overrides = OverridesOf(alias) ?? InferOverrides(alias);
        var contextWindow = overrides.Contains("contextWindow") || info is not { ContextWindow: > 0 } ? alias.ContextWindow : info.ContextWindow;
        var maxOutput = overrides.Contains("maxOutputTokens") || info is not { MaxOutputTokens: > 0 } ? alias.MaxOutputTokens : info.MaxOutputTokens;
        // 目录校验沿用 2.x:输出上限不超过上下文窗口。
        maxOutput = Math.Min(maxOutput, contextWindow);

        var protocol = alias.TextProtocolOverride ?? provider.TextProtocol ?? WireProtocol.OpenAiChat;
        var storedVision = alias.Capability("visionInput") ?? alias.Capability("vision");
        var visionOwned = overrides.Contains("capabilities.vision") || overrides.Contains("capabilities.visionInput");
        var supportsImages = visionOwned ? storedVision ?? true : info?.Capabilities.Vision ?? storedVision ?? true;

        var storedThinking = ReadThinking(alias.Extra["thinkingConfig"]);
        var storedEfforts = ReadEfforts(alias.Extra["reasoningEfforts"]);
        var efforts = overrides.Contains("reasoningEfforts") && storedEfforts is not null ? storedEfforts : info?.ReasoningEfforts;
        var thinking = overrides.Contains("thinkingConfig") && storedThinking is not null ? storedThinking : Derive(info, protocol, efforts);

        var adapter = ReadAdapter(alias.Extra["requestAdapter"]);
        return new ModelBinding(
            alias.Alias,
            alias.ProviderId,
            alias.UpstreamModel,
            contextWindow,
            maxOutput,
            alias.IsEnabled,
            supportsImages,
            alias.TextProtocolOverride,
            thinking,
            efforts,
            adapter.Preset,
            adapter.Patches);
    }

    /// <summary>
    /// 老记录(没有 <c>catalogOverrides</c>,2.x 靠推断)的保守推断:存着的值不是「导入占位缺省」(窗口 200000、输出 8192、vision=true)就当用户的;
    /// 存着的思考声明 / 档位一律当用户的(它们当年来自 2.x 已核实的内置目录)。
    /// </summary>
    public static HashSet<string> InferOverrides(ModelAliasRecord alias)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (alias.ContextWindow != ModelAliasRecord.DefaultContextWindow)
        {
            set.Add("contextWindow");
        }

        if (alias.MaxOutputTokens != ModelAliasRecord.DefaultMaxOutputTokens)
        {
            set.Add("maxOutputTokens");
        }

        if ((alias.Capability("visionInput") ?? alias.Capability("vision")) == false)
        {
            set.Add("capabilities.vision");
        }

        if (alias.DisplayName is not null)
        {
            set.Add("displayName");
        }

        if (ReadThinking(alias.Extra["thinkingConfig"]) is not null)
        {
            set.Add("thinkingConfig");
        }

        if (ReadEfforts(alias.Extra["reasoningEfforts"]) is not null)
        {
            set.Add("reasoningEfforts");
        }

        return set;
    }

    /// <summary>别名上显式声明的「归供应商」字段集合;null = 老记录(没有这个字段)。</summary>
    public static HashSet<string>? OverridesOf(ModelAliasRecord alias) =>
        alias.Extra["catalogOverrides"] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToHashSet(StringComparer.Ordinal)
            : null;

    /// <summary>目录推出来的思考声明(见类注释)。目录里找不到这个模型 → null:不碰请求体,不假装知道。</summary>
    public static ThinkingConfig? Derive(ModelInfo? info, WireProtocol protocol, IReadOnlyList<ReasoningEffort>? efforts)
    {
        if (info is null)
        {
            return null;
        }

        if (!info.Capabilities.Reasoning)
        {
            return new ThinkingConfig(ThinkingMode.Unsupported, DefaultEnabled: false);
        }

        if (efforts is { Count: > 0 })
        {
            var preferred = efforts.Contains(ReasoningEffort.Medium) ? ReasoningEffort.Medium : efforts.First(e => e != ReasoningEffort.None);
            return new ThinkingConfig(ThinkingMode.Effort, DefaultEnabled: true, DefaultEffort: preferred);
        }

        var vendor = info.CatalogProviderId;
        if (protocol == WireProtocol.Anthropic && vendor == "anthropic")
        {
            return new ThinkingConfig(ThinkingMode.Budget, DefaultEnabled: false, DefaultBudgetTokens: 10_000, ParameterPath: "thinking.budget_tokens");
        }

        if (protocol != WireProtocol.Anthropic && vendor is "openai" or "google")
        {
            return new ThinkingConfig(ThinkingMode.Effort, DefaultEnabled: true, DefaultEffort: ReasoningEffort.Medium);
        }

        return new ThinkingConfig(ThinkingMode.Unsupported, DefaultEnabled: false);
    }

    // ── 别名里存着的 JSON → Llm 类型(接管 2.x 的数据必须读得懂)──

    public static ThinkingConfig? ReadThinking(JsonNode? node)
    {
        if (node is not JsonObject o || Str(o["mode"]) is not { } mode)
        {
            return null;
        }

        ThinkingMode? parsed = mode switch
        {
            "unsupported" => ThinkingMode.Unsupported,
            "always" => ThinkingMode.Always,
            "toggle" => ThinkingMode.Toggle,
            "effort" => ThinkingMode.Effort,
            "budget" => ThinkingMode.Budget,
            _ => null,
        };
        if (parsed is null)
        {
            return null;
        }

        Dictionary<ReasoningEffort, JsonElement>? map = null;
        if (o["effortMap"] is JsonObject effortMap)
        {
            map = [];
            foreach (var (key, value) in effortMap)
            {
                if (ModelsDevParser.ParseEffort(key) is { } effort && value is not null)
                {
                    map[effort] = JsonSerializer.Deserialize(value.ToJsonString(), ModelsDevElementContext.Default.JsonElement);
                }
            }
        }

        ReasoningReplay? replay = Str(o["reasoningReplay"]) switch
        {
            "text-required" => ReasoningReplay.TextRequired,
            "opaque-only" => ReasoningReplay.OpaqueOnly,
            _ => null,
        };
        return new ThinkingConfig(
            parsed.Value,
            Bool(o["defaultEnabled"]) ?? true,
            ModelsDevParser.ParseEffort(Str(o["defaultEffort"])),
            o["defaultBudgetTokens"] is JsonValue b && b.TryGetValue<int>(out var budget) ? budget : null,
            Str(o["parameterPath"]),
            Element(o["enabledValue"]),
            Element(o["disabledValue"]),
            map,
            Bool(o["standardWire"]) == true,
            Bool(o["anthropicAdaptive"]) == true,
            replay);
    }

    public static IReadOnlyList<ReasoningEffort>? ReadEfforts(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        return array.Select(n => ModelsDevParser.ParseEffort(Str(n))).OfType<ReasoningEffort>().Distinct().ToList();
    }

    public static (AdapterPreset Preset, IReadOnlyList<PatchRule>? Patches) ReadAdapter(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return (AdapterPreset.Auto, null);
        }

        var preset = Str(o["preset"]) switch
        {
            "anthropic" => AdapterPreset.Anthropic,
            "openai-chat" => AdapterPreset.OpenAiChat,
            "openai-responses" => AdapterPreset.OpenAiResponses,
            "custom" => AdapterPreset.Custom,
            _ => AdapterPreset.Auto,
        };
        var patches = new List<PatchRule>();
        if (o["patches"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not JsonObject p || Str(p["path"]) is not { } path)
                {
                    continue;
                }

                PatchOp? op = Str(p["op"]) switch
                {
                    "add" => PatchOp.Add,
                    "replace" => PatchOp.Replace,
                    "remove" => PatchOp.Remove,
                    _ => null,
                };
                if (op is not null)
                {
                    patches.Add(new PatchRule(op.Value, path, Element(p["value"])));
                }
            }
        }

        return (preset, patches.Count == 0 ? null : patches);
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static JsonElement? Element(JsonNode? node) =>
        node is null ? null : JsonSerializer.Deserialize(node.ToJsonString(), ModelsDevElementContext.Default.JsonElement);
}
