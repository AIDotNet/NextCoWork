using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Pricing;
using Ncw.Agent.Llm.Thinking;

namespace Ncw.Agent.Catalog;

// ── models.dev api.json 的线格式(源生成,不用反射)──
// 容错的对象是「条目」而不是整份文件:一个模型的某个字段类型不对,只丢这一个模型(2.x 的 isRecord 逐项判断同理),整份数据照常采用。

internal sealed class MdProvider
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Api { get; set; }

    public string? Doc { get; set; }

    public string[]? Env { get; set; }
}

internal sealed class MdModel
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Family { get; set; }

    public bool? Attachment { get; set; }

    public bool? Reasoning { get; set; }

    public bool? ToolCall { get; set; }

    public bool? StructuredOutput { get; set; }

    public string? Status { get; set; }

    public string? ReleaseDate { get; set; }

    public string? LastUpdated { get; set; }

    public MdModalities? Modalities { get; set; }

    public MdLimit? Limit { get; set; }

    /// <summary>形状多变(<c>context_over_200k</c>、<c>tiers</c>……),手写读。</summary>
    public JsonElement? Cost { get; set; }

    public JsonElement? ReasoningOptions { get; set; }
}

internal sealed class MdModalities
{
    public string[]? Input { get; set; }

    public string[]? Output { get; set; }
}

internal sealed class MdLimit
{
    public double? Context { get; set; }

    public double? Output { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(MdProvider))]
[JsonSerializable(typeof(MdModel))]
internal sealed partial class ModelsDevJsonContext : JsonSerializerContext;

public sealed class CatalogParseException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>解析结果:供应商(带模型)和被丢掉的条目数。</summary>
public sealed record ParsedCatalog(IReadOnlyList<CatalogProvider> Providers, int Skipped)
{
    public int ModelCount => Providers.Sum(p => p.Models.Count);
}

/// <summary>
/// models.dev <c>api.json</c> → 目录(2.x <c>models-dev.ts</c> 与 <c>models-dev-catalog.ts</c> 的解析部分,纯函数,不碰网络也不碰库)。
/// <para>
/// ★ <b>价格只收 USD 计价的一手厂商</b>(<see cref="ModelsDevOptions.UsdVendors"/>,沿用 2.x 的 <c>SYNC_VENDORS</c>):models.dev 的记录没有币种字段,
/// 国内厂商里有人民币计价的,混进来会把 ¥ 当成 $,是「看着合理的错数」。其余厂商的模型照常进目录(元数据可用),只是没有价格 → 费用 <c>Unknown</c>,
/// 用户可以用本地覆盖补一个带币种的价格。
/// </para>
/// </summary>
public static class ModelsDevParser
{
    /// <summary>
    /// 内存:整份 api.json 有好几 MB,一次 <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/> 要再分配约 2.5 倍大小的元数据表(大对象堆,
    /// 用完要等完整 GC 才回收)。所以顶层用 <see cref="Utf8JsonReader"/> 流式走,每次只为一个供应商建文档;结果里重复出现的字符串、
    /// 输入输出类型、推理强度、能力组合共用同一个实例(上万个模型里这些值只有几十到几百种)。
    /// </summary>
    public static ParsedCatalog Parse(ReadOnlyMemory<byte> json, string fetchedAt, ModelsDevOptions? options = null) =>
        Parse(json.Span, fetchedAt, options);

    /// <inheritdoc cref="Parse(ReadOnlyMemory{byte}, string, ModelsDevOptions?)"/>
    public static ParsedCatalog Parse(ReadOnlySpan<byte> json, string fetchedAt, ModelsDevOptions? options = null)
    {
        options ??= ModelsDevOptions.Default;
        var shared = new SharedValues();
        var providers = new List<(string Key, CatalogProvider? Provider)>();
        var skipped = 0;
        var nonObjectRoot = false;
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = true });
            if (!reader.Read())
            {
                throw new CatalogParseException("models.dev returned invalid JSON.");
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                // 先把整份读完:不是 JSON 的报「不是 JSON」,是 JSON 但根不是对象的才报「不是对象」(和整份解析时的判定顺序一致)
                reader.Skip();
                nonObjectRoot = true;
            }
            else
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var key = reader.GetString()!;
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        skipped++;
                        continue;
                    }

                    using var document = JsonDocument.ParseValue(ref reader);
                    providers.Add((key, ParseProvider(key, document.RootElement, fetchedAt, options, shared, ref skipped)));
                }
            }

            // 根值之后还有内容:读取器在这里抛 JsonException
            if (reader.Read())
            {
                throw new CatalogParseException("models.dev returned invalid JSON.");
            }
        }
        catch (JsonException e)
        {
            throw new CatalogParseException("models.dev returned invalid JSON.", e);
        }

        if (nonObjectRoot)
        {
            throw new CatalogParseException("models.dev did not return an object.");
        }

        // 供应商按 key 排序(稳定排序:重复的 key 保持文件里的先后),和整份解析时一致
        return new ParsedCatalog(
            [.. providers.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Provider).OfType<CatalogProvider>()],
            skipped);
    }

    private static CatalogProvider? ParseProvider(string key, JsonElement element, string fetchedAt, ModelsDevOptions options, SharedValues shared, ref int skipped)
    {
        if (!element.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
        {
            skipped++;
            return null;
        }

        MdProvider header;
        try
        {
            header = JsonSerializer.Deserialize(element, ModelsDevJsonContext.Default.MdProvider) ?? new MdProvider();
        }
        catch (JsonException)
        {
            header = new MdProvider();
        }

        var providerId = string.IsNullOrEmpty(header.Id) ? key : header.Id;
        var usd = options.UsdVendors.Contains(providerId);
        var list = new List<CatalogModel>();
        foreach (var modelEntry in models.EnumerateObject())
        {
            MdModel? model;
            try
            {
                model = modelEntry.Value.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize(modelEntry.Value, ModelsDevJsonContext.Default.MdModel)
                    : null;
            }
            catch (JsonException)
            {
                model = null;
            }

            if (model is null)
            {
                skipped++;
                continue;
            }

            var id = string.IsNullOrEmpty(model.Id) ? modelEntry.Name : model.Id;
            list.Add(ToCatalogModel(providerId, shared.String(id), model, usd, fetchedAt, shared));
        }

        return new CatalogProvider(
            providerId,
            string.IsNullOrWhiteSpace(header.Name) ? providerId : header.Name!,
            header.Api,
            header.Doc,
            header.Env ?? [],
            list.OrderBy(m => m.Id, StringComparer.Ordinal).ToList());
    }

    private static CatalogModel ToCatalogModel(string providerId, string id, MdModel m, bool priced, string fetchedAt, SharedValues shared)
    {
        var input = shared.Modalities(m.Modalities?.Input);
        var output = shared.Modalities(m.Modalities?.Output);
        var context = Positive(m.Limit?.Context);
        var maxOutput = Positive(m.Limit?.Output);
        // 目录校验沿用 2.x:输出上限不超过上下文窗口(聚合源偶尔两者写反)。
        if (context > 0 && maxOutput > context)
        {
            maxOutput = context;
        }

        var efforts = shared.Efforts(ReadEfforts(m.ReasoningOptions));
        return new CatalogModel(
            providerId,
            id,
            string.IsNullOrWhiteSpace(m.Name) ? id : shared.String(m.Name!),
            shared.String(m.Family),
            context,
            maxOutput,
            shared.Capabilities(new ModelCapabilities(m.ToolCall == true, m.Reasoning == true, m.Attachment == true, m.StructuredOutput == true, input, output)),
            efforts,
            priced && m.Cost is { } cost ? ReadPricing(cost, providerId, id, fetchedAt) : null,
            shared.String(m.Status),
            shared.String(m.ReleaseDate),
            shared.String(m.LastUpdated));
    }

    private static int Positive(double? value) =>
        value is { } v && double.IsFinite(v) && v > 0 ? (int)Math.Min(v, int.MaxValue) : 0;

    private static List<ReasoningEffort>? ReadEfforts(JsonElement? options)
    {
        if (options is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        foreach (var option in array.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object
                || option.Prop("type") is not { ValueKind: JsonValueKind.String } type || type.GetString() != "effort"
                || option.Prop("values") is not { ValueKind: JsonValueKind.Array } values)
            {
                continue;
            }

            var result = new List<ReasoningEffort>();
            foreach (var value in values.EnumerateArray())
            {
                if (value.ValueKind == JsonValueKind.String && ParseEffort(value.GetString()) is { } effort && !result.Contains(effort))
                {
                    result.Add(effort);
                }
            }

            return result.Count == 0 ? null : result;
        }

        return null;
    }

    public static ReasoningEffort? ParseEffort(string? value) => value switch
    {
        "none" => ReasoningEffort.None,
        "minimal" => ReasoningEffort.Minimal,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high" => ReasoningEffort.High,
        "xhigh" => ReasoningEffort.XHigh,
        "max" => ReasoningEffort.Max,
        _ => null,
    };

    // ── 价格(2.x models-dev.ts 的 rateOf / tiersOf)──

    private static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static decimal? Number(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } e && e.TryGetDecimal(out var d) && d >= 0 ? d : null;

    private static TokenRates? RateOf(JsonElement cost)
    {
        if (Number(cost.Prop("input")) is not { } input || Number(cost.Prop("output")) is not { } output)
        {
            return null;
        }

        return new TokenRates(input, output, Number(cost.Prop("cache_read")), Number(cost.Prop("cache_write")));
    }

    /// <summary>
    /// 阶梯:优先 <c>tiers</c>(type=context,按 size 升序),退回 <c>context_over_200k</c>。每个条目的费率是「超过 size 之后」的价,
    /// 所以第 i 档的上界是第 i 个 size,末档无上界。任何一档缺 input/output 就整模型按单档处理 —— 宁可少一个阶梯也不编。
    /// </summary>
    private static IReadOnlyList<PriceTier> TiersOf(JsonElement cost, TokenRates baseRate)
    {
        var steps = new List<(long Size, TokenRates Rate)>();
        if (cost.Prop("tiers") is { ValueKind: JsonValueKind.Array } raw)
        {
            foreach (var tier in raw.EnumerateArray())
            {
                if (tier.Prop("tier") is not { ValueKind: JsonValueKind.Object } marker
                    || marker.Prop("type") is not { ValueKind: JsonValueKind.String } type || type.GetString() != "context")
                {
                    continue;
                }

                if (Number(marker.Prop("size")) is not { } size || size <= 0 || RateOf(tier) is not { } rate)
                {
                    return [new PriceTier(null, baseRate)];
                }

                steps.Add(((long)size, rate));
            }
        }
        else if (cost.Prop("context_over_200k") is { ValueKind: JsonValueKind.Object } over && RateOf(over) is { } overRate)
        {
            steps.Add((200_000, overRate));
        }

        if (steps.Count == 0)
        {
            return [new PriceTier(null, baseRate)];
        }

        steps.Sort((a, b) => a.Size.CompareTo(b.Size));
        var tiers = new List<PriceTier>();
        var previous = baseRate;
        foreach (var (size, rate) in steps)
        {
            tiers.Add(new PriceTier(size, previous));
            previous = rate;
        }

        tiers.Add(new PriceTier(null, previous));
        return tiers;
    }

    internal static ModelPricing? ReadPricing(JsonElement cost, string providerId, string modelId, string fetchedAt)
    {
        if (RateOf(cost) is not { } baseRate)
        {
            return null;
        }

        return new ModelPricing(
            providerId,
            modelId,
            Currency.Usd,
            TiersOf(cost, baseRate),
            Source: $"{ModelsDevOptions.Url}#{providerId}/{modelId}",
            FetchedAt: fetchedAt);
    }
}

/// <summary>
/// 一次解析里共用的值。只在解析期间存在,解析完随之丢弃;留下的是被共用的那些实例。
/// 共用的数组 / 列表对外是只读接口,目录里没有人改它们(改模型走 <c>with</c> 生成新记录)。
/// </summary>
internal sealed class SharedValues
{
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _modalities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReasoningEffort[]> _efforts = new(StringComparer.Ordinal);
    private readonly Dictionary<ModelCapabilities, ModelCapabilities> _capabilities = [];

    [return: NotNullIfNotNull(nameof(value))]
    public string? String(string? value)
    {
        if (value is null)
        {
            return null;
        }

        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_strings, value, out var exists);
        if (!exists)
        {
            slot = value;
        }

        return slot!;
    }

    /// <summary>小写化后的输入 / 输出类型(<c>["text","image"]</c> 这类组合只有几十种)。</summary>
    public string[] Modalities(string[]? values)
    {
        if (values is null || values.Length == 0)
        {
            return [];
        }

        var lowered = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            lowered[i] = String(values[i].ToLowerInvariant());
        }

        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_modalities, string.Join('\n', lowered), out var exists);
        if (!exists)
        {
            slot = lowered;
        }

        return slot!;
    }

    public ReasoningEffort[]? Efforts(List<ReasoningEffort>? efforts)
    {
        if (efforts is null)
        {
            return null;
        }

        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_efforts, string.Join(',', efforts), out var exists);
        if (!exists)
        {
            slot = [.. efforts];
        }

        return slot!;
    }

    /// <summary>记录按值相等;数组已经共用,所以同样的能力组合得到同一个实例。</summary>
    public ModelCapabilities Capabilities(ModelCapabilities capabilities)
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_capabilities, capabilities, out var exists);
        if (!exists)
        {
            slot = capabilities;
        }

        return slot!;
    }
}

public sealed class ModelsDevOptions
{
    public const string Url = "https://models.dev/api.json";

    public static ModelsDevOptions Default { get; } = new();

    /// <summary>USD 计价的一手厂商(models.dev 的 provider id)。只有这些的价格被采用。</summary>
    public IReadOnlySet<string> UsdVendors { get; init; } = new HashSet<string>(StringComparer.Ordinal) { "anthropic", "openai", "google", "xai" };
}
