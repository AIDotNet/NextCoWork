using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Pricing;
using Ncw.Agent.Llm.Thinking;

namespace Ncw.Agent.Catalog;

/// <summary>某个(供应商, 模型)在目录与覆盖合并之后的有效元数据。供应商解析模型绑定、价目表查价都读它。</summary>
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    int ContextWindow,
    int MaxOutputTokens,
    ModelCapabilities Capabilities,
    IReadOnlyList<ReasoningEffort>? ReasoningEfforts,
    ModelPricing? Price,
    string? CatalogProviderId,
    bool Overridden);

/// <summary>用户覆盖的套用(纯函数)。顺序:先「对所有供应商」的,再「指定供应商」的,更具体的后来居上。</summary>
public static class CatalogOverrides
{
    public static IReadOnlyList<CatalogProvider> Apply(IReadOnlyList<CatalogProvider> providers, IReadOnlyList<ModelOverride> overrides)
    {
        if (overrides.Count == 0)
        {
            return providers;
        }

        var ordered = Ordered(overrides);
        var result = new List<CatalogProvider>(providers.Count);
        foreach (var provider in providers)
        {
            var models = new List<CatalogModel>(provider.Models.Count);
            foreach (var model in provider.Models)
            {
                var matches = ordered.Where(o => o.Matches(provider.Id, model.Id)).ToList();
                if (matches.Count == 0)
                {
                    models.Add(model);
                    continue;
                }

                if (matches.Any(o => o.Deleted))
                {
                    continue;
                }

                models.Add(matches.Aggregate(model, ApplyOne));
            }

            result.Add(provider with { Models = models });
        }

        return result;
    }

    /// <summary>
    /// 一个(用户配置的供应商, 模型 id)的有效元数据。目录里找不到、也没有覆盖 → null(调用方用保守缺省值,不编)。
    /// 被用户删除(<see cref="ModelOverride.Deleted"/>)的模型同样返回 null:删除就是「别再按目录给我补这个模型的信息」。
    /// </summary>
    public static ModelInfo? Describe(CatalogSnapshot snapshot, IReadOnlyList<ModelOverride> overrides, string? providerId, string modelId)
    {
        var matches = Ordered(overrides).Where(o => o.Matches(providerId, modelId)).ToList();
        if (matches.Any(o => o.Deleted))
        {
            return null;
        }

        var found = snapshot.Find(providerId, modelId);
        // 目录里的这一条已经套过「对所有供应商」和「按目录供应商」的覆盖,这里再套一遍是幂等的,补上「按用户供应商 id」的那一层。
        if (found is not null)
        {
            var model = matches.Aggregate(found, ApplyOne);
            return ToInfo(model, found.ProviderId, model.Overridden);
        }

        if (matches.Count == 0)
        {
            return null;
        }

        // 目录不认识这个模型(自建网关上的私有模型),但用户给了覆盖:按覆盖合成,没覆盖的字段保持「未知」。
        var blank = new CatalogModel(
            providerId ?? "",
            modelId,
            modelId,
            null,
            0,
            0,
            new ModelCapabilities(false, false, false, false, ["text"], ["text"]),
            null,
            null);
        return ToInfo(matches.Aggregate(blank, ApplyOne), null, true);
    }

    private static ModelInfo ToInfo(CatalogModel m, string? catalogProviderId, bool overridden) => new(
        m.Id, m.Name, m.ContextWindow, m.MaxOutputTokens, m.Capabilities, m.ReasoningEfforts, m.Price, catalogProviderId, overridden);

    private static List<ModelOverride> Ordered(IReadOnlyList<ModelOverride> overrides) =>
        overrides.OrderBy(o => o.ProviderId is null ? 0 : 1).ToList();

    private static CatalogModel ApplyOne(CatalogModel model, ModelOverride o)
    {
        var capabilities = model.Capabilities;
        if (o.ToolCall is { } tool)
        {
            capabilities = capabilities with { ToolCall = tool };
        }

        if (o.Reasoning is { } reasoning)
        {
            capabilities = capabilities with { Reasoning = reasoning };
        }

        if (o.Vision is { } vision)
        {
            var input = capabilities.InputModalities.Where(m => m != "image").ToList();
            if (vision)
            {
                input.Add("image");
            }

            capabilities = capabilities with { InputModalities = input };
        }

        return model with
        {
            Name = string.IsNullOrWhiteSpace(o.DisplayName) ? model.Name : o.DisplayName.Trim(),
            ContextWindow = o.ContextWindow is > 0 ? o.ContextWindow.Value : model.ContextWindow,
            MaxOutputTokens = o.MaxOutputTokens is > 0 ? o.MaxOutputTokens.Value : model.MaxOutputTokens,
            Capabilities = capabilities,
            Price = o.Price is { } price ? ToPricing(model.ProviderId, model.Id, price) : model.Price,
            Overridden = true,
        };
    }

    private static ModelPricing ToPricing(string providerId, string modelId, PriceOverride p) => new(
        providerId,
        modelId,
        p.Currency,
        [new PriceTier(null, new TokenRates(p.Input, p.Output, p.CacheRead, p.CacheWrite, p.CacheWrite1h))],
        Source: "user-override");
}
