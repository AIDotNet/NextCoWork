using PolyType;

namespace Ncw.Protocol.Methods;

// 搜索提供方(docs/design/10 的 `websearch` 模块 → `websearch.*`,能力 `websearch`):`web_search` 工具背后的搜索服务的启停、顺序、API Key、连通性测试。
// 目录(哪几家、各自的品牌名、领 Key 的地址、「此刻在世界上就用不了」的家及其稳定原因 key)在 Agent 里,控制端只展示;说明文字是界面文字,控制端按 Id 翻译。
// ★ 明文 Key 只有一个方向:进。`setCredential` 写进 NCK1 凭据库之后,任何方法都读不回它,回程只带 HasKey / Last4(太短的 Key 不给 Last4);
// 没有 reveal 方法——`web_search` 工具自己也拿不到 Key(只有搜索服务经 ISearchConfigSource 取)。
// 读 member 起(状态里没有任何凭据内容),写和测试 owner(Key 是设备的秘密;「测试」真发一次最小查询、花额度)。`test` 只回通不通,不回搜索结果(否则它就成了绕过联网开关的搜索入口)。

/// <param name="Id">稳定 id:<c>tavily</c> / <c>exa</c> / <c>brave</c> / <c>serper</c> / <c>serpapi</c> / <c>metaso</c> / <c>doubao</c> / <c>bing</c>。</param>
/// <param name="Name">品牌名(不翻译)。</param>
/// <param name="KeyUrl">「获取 Key」的链接,由控制端在系统浏览器里打开。</param>
/// <param name="UnavailableKey">非 null = 这家此刻就用不了(不是「还没做」),值是稳定原因 key(<c>websearch.unavailable.doubao</c> / <c>.bing</c>),控制端翻译并禁掉开关;Agent 也拒绝启用它。</param>
/// <param name="Priority">升序 = 先用;列表按它排好序,且是 0..n-1 连续的。</param>
public sealed record WebSearchProviderDto(string Id, string Name, string KeyUrl, string? UnavailableKey, bool Enabled, int Priority, bool HasKey, string? Last4);

/// <summary>
/// <c>websearch</c> 主题的状态,同时是 <c>websearch.changed</c> 事件的数据与 <c>websearch.list</c> 的结果(全量,首帧与后续广播同一个类型,AGENTS.md §3 规则 5)。
/// <see cref="Version"/> 单调递增,控制端只收更新的那份。
/// </summary>
public sealed record WebSearchTopicState(long Version, WebSearchProviderDto[] Providers);

public static class WebSearchEvents
{
    public const string Changed = "websearch.changed";
}

public sealed record WebSearchRefParams(string Id);

public sealed record WebSearchSetEnabledParams(string Id, bool Enabled);

/// <param name="Ids">新的顺序。应是全集;缺的(或认不出的)掉到末尾并保持原有相对顺序,不会被删掉——这是排序,不是设置成员。</param>
public sealed record WebSearchReorderParams(string[] Ids);

public sealed record WebSearchCredentialSetParams(string Id, string ApiKey);

public sealed record WebSearchCredentialInfoDto(bool HasKey, string? Last4);

/// <param name="Key">稳定 key:失败时 <c>websearch.test.unavailable</c> / <c>.noAdapter</c> / <c>.noKey</c> / <c>.failed</c>;成功但这次查询没有结果时 <c>websearch.test.noResults</c>(Key 是好的)。</param>
/// <param name="Detail">失败时服务商说了什么(英文原话,不含 Key)。</param>
public sealed record WebSearchTestResultDto(bool Ok, long? LatencyMs, string? Key, string? Detail);

/// <summary>搜索提供方方法表(能力 <see cref="Capabilities.WebSearch"/>,承载 本机 + 远程)。</summary>
public static class WebSearchMethods
{
    public static readonly MethodDef<Unit, WebSearchTopicState> List = Q<Unit, WebSearchTopicState>("websearch.list", Role.Member);

    public static readonly MethodDef<WebSearchSetEnabledParams, WebSearchTopicState> SetEnabled = C<WebSearchSetEnabledParams, WebSearchTopicState>("websearch.setEnabled", Role.Owner);

    public static readonly MethodDef<WebSearchReorderParams, WebSearchTopicState> Reorder = C<WebSearchReorderParams, WebSearchTopicState>("websearch.reorder", Role.Owner);

    public static readonly MethodDef<WebSearchCredentialSetParams, WebSearchCredentialInfoDto> SetCredential = C<WebSearchCredentialSetParams, WebSearchCredentialInfoDto>("websearch.setCredential", Role.Owner);

    /// <summary>清 Key,<b>顺手把这家关掉</b>:留着「已启用但没 Key」的状态,搜索链会把它滤掉,于是界面上开关是开的、搜索却永远不经过它。</summary>
    public static readonly MethodDef<WebSearchRefParams, WebSearchTopicState> ClearCredential = C<WebSearchRefParams, WebSearchTopicState>("websearch.clearCredential", Role.Owner);

    public static readonly MethodDef<WebSearchRefParams, WebSearchTestResultDto> Test = Q<WebSearchRefParams, WebSearchTestResultDto>("websearch.test", Role.Owner);

    /// <summary>搜索提供方的全量状态(快照和 <see cref="WebSearchEvents.Changed"/> 同一个类型)。member 可订阅:不含任何凭据内容。</summary>
    public static readonly TopicDef<WebSearchTopicState> Topic = new("websearch", PayloadShapes.GetShape<WebSearchTopicState>(), Role.Member);

    public static readonly IReadOnlyList<MethodDef> All = [List, SetEnabled, Reorder, SetCredential, ClearCredential, Test];

    private static MethodDef<TP, TR> Q<TP, TR>(string name, Role role) => MethodTable.Query<TP, TR>(name, role, Capabilities.WebSearch);

    private static MethodDef<TP, TR> C<TP, TR>(string name, Role role) => MethodTable.Command<TP, TR>(name, role, Capabilities.WebSearch);
}

[GenerateShapeFor<WebSearchProviderDto>]
[GenerateShapeFor<WebSearchTopicState>]
[GenerateShapeFor<WebSearchRefParams>]
[GenerateShapeFor<WebSearchSetEnabledParams>]
[GenerateShapeFor<WebSearchReorderParams>]
[GenerateShapeFor<WebSearchCredentialSetParams>]
[GenerateShapeFor<WebSearchCredentialInfoDto>]
[GenerateShapeFor<WebSearchTestResultDto>]
public sealed partial class PayloadShapes;

public static partial class Capabilities
{
    /// <summary>搜索提供方:<c>websearch.*</c> 和 <c>websearch</c> 主题(读取 member 起,写入与测试 owner;<c>web_search</c> 工具的数据源)。</summary>
    public const string WebSearch = "websearch";
}
