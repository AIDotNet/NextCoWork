namespace Ncw.Agent.WebTools.Search;

/// <summary>
/// 搜索服务目录项(2.x <c>shared/domain/search.ts</c> 的 <c>SearchProviderMeta</c>)。名字是品牌名;说明文字是界面文字,不在这一层:
/// 控制端按 <see cref="Id"/> 翻译。<see cref="UnavailableKey"/> 有值 = 这家<b>此刻在世界上就用不了</b>(和本仓库的进度无关),原因由控制端按这个稳定 key 翻译、并禁掉开关。
/// </summary>
public sealed record SearchProviderMeta(string Id, string Name, string KeyUrl, string? UnavailableKey = null);

/// <summary>用户能改的全部(端点写死在适配器里,填错了只会得到一个 404):启用、优先级(升序 = 先用)。</summary>
public sealed record SearchProviderConfig(string Id, bool Enabled, int Priority);

/// <summary>列表行要的一切。<see cref="HasKey"/> / <see cref="Last4"/> 由设备从凭据库算出来,明文永不出设备。</summary>
public sealed record SearchProviderStatus(SearchProviderConfig Config, bool HasKey, string? Last4 = null);

/// <summary>一条搜索结果。<see cref="Provider"/> 是谁给的(结果里要标出来,模型无从判断可信度)。</summary>
public sealed record SearchResult(string Title, string Url, string Snippet, string Provider, string? PublishedAt = null);

/// <summary>
/// 搜索提供方目录与纯函数(2.x <c>shared/domain/search.ts</c>)。顺序 = 界面上的默认排列,也是首次写库时的初始 priority。
/// 「豆包」(火山方舟没有独立的搜索接口,联网只能作为 Responses API 里的内置工具随模型一起调用)和「Bing」(搜索 API 已于 2025-08-11 下线)
/// 照 2.x 留在目录里并标 <c>unavailable</c>:比留一个点了就报错的开关诚实,也比悄悄删掉诚实。
/// </summary>
public static class SearchProviders
{
    public const string Tavily = "tavily";
    public const string Exa = "exa";
    public const string Brave = "brave";
    public const string Serper = "serper";
    public const string SerpApi = "serpapi";
    public const string Metaso = "metaso";
    public const string Doubao = "doubao";
    public const string Bing = "bing";

    public static IReadOnlyList<SearchProviderMeta> Catalog { get; } =
    [
        new(Tavily, "Tavily", "https://app.tavily.com/home"),
        new(Exa, "Exa", "https://dashboard.exa.ai/api-keys"),
        new(Brave, "Brave Search", "https://api-dashboard.search.brave.com/app/keys"),
        new(Serper, "Serper", "https://serper.dev/api-key"),
        new(SerpApi, "SerpApi", "https://serpapi.com/manage-api-key"),
        new(Metaso, "秘塔搜索", "https://metaso.cn/search-api/playground"),
        new(Doubao, "豆包搜索", "https://console.volcengine.com/ark", "websearch.unavailable.doubao"),
        new(Bing, "Bing Search", "https://learn.microsoft.com/lifecycle/announcements/bing-search-api-retirement", "websearch.unavailable.bing"),
    ];

    public static SearchProviderMeta? Find(string id) => Catalog.FirstOrDefault(m => m.Id == id);

    public static bool IsKnown(string id) => Find(id) is not null;

    public static bool IsUsable(string id) => Find(id) is { UnavailableKey: null };

    /// <summary>
    /// API Key 在凭据库里的 ref(和 MCP 的 <c>mcp:</c> 前缀同款)。前缀就是两套密钥不会互相看见的全部保证,所以它是字面量,不由调用方拼。
    /// 2.x 的物理 ref 是 <c>websearch:&lt;id&gt;</c>(经账户作用域换算前),3.0 沿用,接管 2.x 数据后原有的 Key 直接可用。
    /// </summary>
    public static string SecretRef(string id) => $"websearch:{id}";

    /// <summary>末四位。太短的 Key 全打码——一个 5 位的 Key 显示末四位等于显示了它。</summary>
    public static string? Last4(string? key) => key is { Length: >= 8 } ? key[^4..] : null;

    /// <summary>首次启动时写库的那一份:全部关闭,顺序即目录顺序。</summary>
    public static IReadOnlyList<SearchProviderConfig> Defaults() => [.. Catalog.Select((m, i) => new SearchProviderConfig(m.Id, false, i))];

    /// <summary>
    /// 把库里存的几行合到完整目录上,按 priority 升序,平局用目录顺序兜底(确定的:「今天先试 A、明天先试 B」比顺序不合心意难查得多)。
    /// 库里只存「用户动过的那几家」,一家都没配过时表是空的,但设置页要列出全部、搜索要知道完整的优先级序,所以左连接收在这一个函数里。
    /// </summary>
    public static IReadOnlyList<SearchProviderConfig> Merge(IEnumerable<SearchProviderConfig> stored)
    {
        var map = stored.Where(c => IsKnown(c.Id)).GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.Last());
        return [.. Defaults()
            .Select((d, index) => (Config: map.GetValueOrDefault(d.Id) ?? d, Index: index))
            .OrderBy(x => x.Config.Priority)
            .ThenBy(x => x.Index)
            .Select(x => x.Config)];
    }

    /// <summary>
    /// 拖拽排序的落点:<paramref name="ids"/> 是新的顺序,缺的(或认不出的被忽略)掉到末尾并保持原有相对顺序,而不是被删掉——这是排序,不是设置成员。
    /// 返回的 priority 一定是 0..n-1 连续的(界面显示的是「第几位」,留出空洞的话删掉中间一家再拖一次序号会跳)。
    /// </summary>
    public static IReadOnlyList<SearchProviderConfig> ApplyOrder(IReadOnlyList<SearchProviderConfig> current, IEnumerable<string> ids)
    {
        var byId = current.ToDictionary(c => c.Id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<SearchProviderConfig>();
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var config) && seen.Add(id))
            {
                ordered.Add(config);
            }
        }

        ordered.AddRange(current.Where(c => !seen.Contains(c.Id)));
        return [.. ordered.Select((c, i) => c with { Priority = i })];
    }

    /// <summary>
    /// <c>web_search</c> 的调用顺序:已启用、这家还能用、且<b>确实配了 Key</b>。第三个条件不能省:少了它,一个开着但没填 Key 的服务会在每次搜索时白跑一趟 HTTP 才失败——
    /// 用户看到的是「搜索变慢了」,而不是「我忘了填 Key」。
    /// </summary>
    public static IReadOnlyList<SearchProviderStatus> Chain(IEnumerable<SearchProviderStatus> statuses) =>
        [.. statuses.Where(s => s.Config.Enabled && s.HasKey && IsUsable(s.Config.Id)).OrderBy(s => s.Config.Priority)];
}
