using System.Text.Json.Nodes;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Parity.Tests.Rig;

/// <summary>录制里的供应商 / 模型 → 路由器的配置源(两个台架共用)。</summary>
internal sealed class ProviderConfigSource : IProviderConfigSource
{
    public ProviderConfigSource(JsonObject input)
    {
        Providers = [.. input["providers"]!.AsArray().Select(p =>
        {
            var o = p!.AsObject();
            var id = o["id"]!.GetValue<string>();
            return new ProviderConfig(
                id, id, o["baseUrl"]!.GetValue<string>(), o["protocol"]!.GetValue<string>() switch
                {
                    "anthropic" => WireProtocol.Anthropic,
                    "openai-chat" => WireProtocol.OpenAiChat,
                    _ => WireProtocol.OpenAiResponses,
                },
                o["priority"]!.GetValue<int>(), true, $"provider:{id}");
        })];
        Bindings = [.. new[] { input["model"]! }.Concat(input["extraModels"]!.AsArray().Select(m => m!)).Select(m =>
        {
            var o = m.AsObject();
            var providerId = o["providerId"]!.GetValue<string>();
            var protocol = Providers.Single(p => p.Id == providerId).Protocol;
            return new ModelBinding(
                o["alias"]!.GetValue<string>(), providerId, o["upstreamModel"]!.GetValue<string>(),
                o["contextWindow"]!.GetValue<int>(), o["maxOutputTokens"]!.GetValue<int>(), SupportsImages: o["vision"]?.GetValue<bool>() ?? true,
                // 2.x 的别名只写 `capabilities.thinking: true`,由适配器按协议推断;3.0 里这是模型目录给的声明(ModelCatalogService 投影),这里按协议给同一个推断结果。
                Thinking: o["thinking"]?.GetValue<bool>() == false
                    ? null
                    : protocol == WireProtocol.Anthropic ? new ThinkingConfig(ThinkingMode.Budget) : new ThinkingConfig(ThinkingMode.Effort, DefaultEnabled: false),
                ReasoningEfforts: protocol == WireProtocol.Anthropic ? null : [ReasoningEffort.None, ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High]);
        })];
    }

    public IReadOnlyList<ProviderConfig> Providers { get; }

    public IReadOnlyList<ModelBinding> Bindings { get; }

    public bool FailoverEnabled => true;
}
