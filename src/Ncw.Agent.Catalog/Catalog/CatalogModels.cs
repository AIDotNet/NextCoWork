using System.Runtime.InteropServices;
using Ncw.Agent.Llm.Pricing;
using Ncw.Agent.Llm.Thinking;

namespace Ncw.Agent.Catalog;

/// <summary>
/// 模型能力(models.dev 给得出的那部分)。思考的「方言」(<c>reasoning.effort</c> / <c>thinking.type</c>……)models.dev 不提供,
/// 所以这里只有「会不会推理」和「接受哪几档强度」;怎么把它变成请求参数是 <see cref="ModelBindingResolver"/> 与 Llm 层思考适配器的事。
/// </summary>
public sealed record ModelCapabilities(
    bool ToolCall,
    bool Reasoning,
    bool Attachment,
    bool StructuredOutput,
    IReadOnlyList<string> InputModalities,
    IReadOnlyList<string> OutputModalities)
{
    public bool Vision => InputModalities.Contains("image");

    public bool AcceptsPdf => InputModalities.Contains("pdf");

    public bool AcceptsAudio => InputModalities.Contains("audio");

    public bool AcceptsVideo => InputModalities.Contains("video");

    /// <summary>只产文本的对话模型。能输出图片 / 视频 / 语音的(生图、生视频)不是聊天候选(2.x <c>isChatModelAlias</c> 的同一条判据)。</summary>
    public bool IsChatModel => OutputModalities.Count == 0 || OutputModalities.All(m => m == "text");
}

/// <summary>目录里的一个模型(已套用用户覆盖)。<see cref="ProviderId"/> 是 models.dev 的供应商 id,不是用户配置的供应商 id。</summary>
public sealed record CatalogModel(
    string ProviderId,
    string Id,
    string Name,
    string? Family,
    int ContextWindow,
    int MaxOutputTokens,
    ModelCapabilities Capabilities,
    IReadOnlyList<ReasoningEffort>? ReasoningEfforts,
    ModelPricing? Price,
    string? Status = null,
    string? ReleaseDate = null,
    string? LastUpdated = null,
    bool Overridden = false);

public sealed record CatalogProvider(
    string Id,
    string Name,
    string? Api,
    string? Doc,
    IReadOnlyList<string> Env,
    IReadOnlyList<CatalogModel> Models);

/// <summary>快照的来源:内置种子 / 磁盘缓存 / 刚从网上拉到的。</summary>
public enum CatalogSource
{
    Seed,
    Cache,
    Remote,
}

/// <summary>目录查询。字段都是可选的合取条件。<see cref="Text"/> 在 id、名称、家族里做不分大小写的子串匹配。</summary>
public sealed record CatalogQuery(
    string? ProviderId = null,
    string? ModelId = null,
    string? Text = null,
    bool? ToolCall = null,
    bool? Reasoning = null,
    bool? Vision = null,
    bool? ChatOnly = null,
    int? MinContextWindow = null);

/// <summary>最近一次拉取的状态(设置页「上次更新于……」和最近一次错误)。</summary>
public sealed record CatalogStatus(
    long Version,
    CatalogSource Source,
    DateTimeOffset? FetchedAt,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? NextCheckAt,
    int ModelCount,
    int ProviderCount,
    string? ETag,
    string? Sha256,
    CatalogError? LastError);

/// <summary>结构化的错误(没有界面文字):<paramref name="Key"/> 由控制端翻译,<paramref name="Detail"/> 是英文诊断。</summary>
public sealed record CatalogError(string Key, string Detail, DateTimeOffset At);

public static class CatalogErrorKeys
{
    public const string Network = "catalog.network";
    public const string Timeout = "catalog.timeout";
    public const string HttpStatus = "catalog.httpStatus";
    public const string TooLarge = "catalog.tooLarge";
    public const string InvalidJson = "catalog.invalidJson";
    public const string Shrunk = "catalog.shrunk";
    public const string Redirect = "catalog.redirect";
}

public enum CatalogRefreshOutcome
{
    /// <summary>采用了新数据。</summary>
    Updated,

    /// <summary>304:只更新「上次检查时间」。</summary>
    NotModified,

    /// <summary>拿到了数据但被校验拒绝(缩水、不是有效 JSON):保留上一份。</summary>
    Rejected,

    /// <summary>网络、超时、状态码、超大响应:保留上一份。</summary>
    Failed,
}

public sealed record CatalogRefreshResult(CatalogRefreshOutcome Outcome, CatalogError? Error = null, int ModelCount = 0);

/// <summary>
/// 全量快照 + 版本号。<c>catalog.changed</c> 事件携带的就是这个类型(AGENTS.md §3 第 5 条:首帧与后续广播同一个类型)。
/// 版本号在每一次可见的变化(远端数据、用户覆盖)时单调递增;控制端按版本号判断要不要重新拉。
/// </summary>
public sealed class CatalogSnapshot
{
    private readonly Dictionary<string, CatalogProvider> _providers;
    // 上万个模型里绝大多数 id 只出现一次:桶里先放一个,重名时才建列表
    private readonly Dictionary<string, Bucket> _byModelId;
    private readonly Dictionary<string, Bucket> _byLowerModelId;

    public CatalogSnapshot(
        long version,
        CatalogSource source,
        DateTimeOffset? fetchedAt,
        IReadOnlyList<CatalogProvider> providers)
    {
        Version = version;
        Source = source;
        FetchedAt = fetchedAt;
        Providers = providers;
        _providers = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _byModelId = [];
        _byLowerModelId = [];
        foreach (var provider in providers)
        {
            foreach (var model in provider.Models)
            {
                Add(_byModelId, model.Id, model);
                Add(_byLowerModelId, model.Id.ToLowerInvariant(), model);
            }
        }

        static void Add(Dictionary<string, Bucket> map, string key, CatalogModel model)
        {
            ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out var exists);
            if (!exists)
            {
                bucket = new Bucket(model);
            }
            else
            {
                (bucket.Rest ??= []).Add(model);
            }
        }
    }

    private struct Bucket(CatalogModel first)
    {
        public readonly CatalogModel First = first;

        public List<CatalogModel>? Rest;
    }

    public static CatalogSnapshot Empty { get; } = new(0, CatalogSource.Seed, null, []);

    public long Version { get; }

    public CatalogSource Source { get; }

    public DateTimeOffset? FetchedAt { get; }

    public IReadOnlyList<CatalogProvider> Providers { get; }

    public int ModelCount => Providers.Sum(p => p.Models.Count);

    public CatalogProvider? Provider(string providerId) => _providers.GetValueOrDefault(providerId);

    public CatalogModel? Get(string providerId, string modelId) =>
        Provider(providerId)?.Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));

    /// <summary>
    /// 按模型 id 找(用户配置的供应商不一定是 models.dev 的供应商:聚合站、自建网关……)。顺序:
    /// ① 提示的供应商正好就是目录里的供应商 → 它那一条;② 精确 id,一手厂商优先;③ 不分大小写;④ 去掉网关前缀
    /// (<c>openrouter/anthropic/claude-x</c> → <c>anthropic/claude-x</c> → <c>claude-x</c>)逐段再试。查不到返回 null,不猜。
    /// </summary>
    public CatalogModel? Find(string? providerHint, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var id = modelId.Trim();
        if (providerHint is not null && Get(providerHint, id) is { } direct)
        {
            return direct;
        }

        for (var candidate = id; ; )
        {
            if (_byModelId.TryGetValue(candidate, out var exact))
            {
                return Preferred(exact);
            }

            if (_byLowerModelId.TryGetValue(candidate.ToLowerInvariant(), out var folded))
            {
                return Preferred(folded);
            }

            var slash = candidate.IndexOf('/');
            if (slash < 0 || slash == candidate.Length - 1)
            {
                return null;
            }

            candidate = candidate[(slash + 1)..];
        }
    }

    /// <summary>一手厂商排在聚合站前面;同一档内按目录里的供应商顺序(本身按 id 排序),结果是确定的。</summary>
    private static CatalogModel Preferred(Bucket bucket)
    {
        if (bucket.Rest is not { } rest)
        {
            return bucket.First;
        }

        // 同一档取先出现的那个(供应商顺序),和稳定排序后取第一个一样
        var best = bucket.First;
        var bestRank = FirstParty.RankOf(best.ProviderId);
        foreach (var model in rest)
        {
            var rank = FirstParty.RankOf(model.ProviderId);
            if (rank < bestRank)
            {
                best = model;
                bestRank = rank;
            }
        }

        return best;
    }

    public IReadOnlyList<CatalogModel> Query(CatalogQuery query)
    {
        IEnumerable<CatalogModel> source = query.ProviderId is { } providerId
            ? Provider(providerId)?.Models ?? []
            : Providers.SelectMany(p => p.Models);

        if (query.ModelId is { } modelId)
        {
            source = source.Where(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var text = query.Text.Trim();
            source = source.Where(m => m.Id.Contains(text, StringComparison.OrdinalIgnoreCase)
                || m.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (m.Family?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (query.ToolCall is { } tool)
        {
            source = source.Where(m => m.Capabilities.ToolCall == tool);
        }

        if (query.Reasoning is { } reasoning)
        {
            source = source.Where(m => m.Capabilities.Reasoning == reasoning);
        }

        if (query.Vision is { } vision)
        {
            source = source.Where(m => m.Capabilities.Vision == vision);
        }

        if (query.ChatOnly is { } chat)
        {
            source = source.Where(m => m.Capabilities.IsChatModel == chat);
        }

        if (query.MinContextWindow is { } min)
        {
            source = source.Where(m => m.ContextWindow >= min);
        }

        return source.ToList();
    }
}

/// <summary>一手厂商(自己卖自己模型的)在「同一个模型 id 出现在多处」时排在聚合站前面。</summary>
public static class FirstParty
{
    private static readonly string[] Order =
    [
        "anthropic", "openai", "google", "xai", "deepseek", "moonshotai", "zhipuai", "alibaba", "minimax", "mistral", "cohere", "meta", "xiaomi",
    ];

    public static int RankOf(string providerId)
    {
        var index = Array.IndexOf(Order, providerId);
        return index < 0 ? Order.Length : index;
    }
}
