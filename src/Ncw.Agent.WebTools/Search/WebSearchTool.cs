using System.Text;
using System.Text.Json;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.WebTools.Search;

/// <summary>
/// <c>web_search</c>(2.x <c>web-search.ts</c>)。名字是小写下划线:内置工具一律照搬 Claude Code 的名字,而 CC 那边这个工具就叫 <c>web_search</c>。
/// <list type="bullet">
/// <item><b>它不碰密钥</b>:工具只会说「帮我搜一下」(<see cref="SearchService"/>),一个 Key 也拿不到——哪怕被投毒的提示词说服了也拿不到。</item>
/// <item>返回的是<b>结果列表</b>,不是答案(和 WebFetch 同一个立场):我们没有替模型读完再总结的小模型,假装有会让模型把片段当成结论去引用。</item>
/// <item>失败分三种说,因为模型接下来该做的事完全不同:没配任何服务 → 停下来告诉用户去配置(换查询词没用);配了但全挂 → 把每家的原话给模型(Key 过期、额度用完都在里面);
/// 有服务应答了但没结果 → 换查询词是有意义的。把这几种压成一句「搜索失败」,会看到模型把同一个查询原样重试三遍。</item>
/// </list>
/// 联网由权限闸的联网开关管(<c>PermissionGate.NetworkSwitchTools</c>);不下发 / 拒绝的两处共用同一张名单。
/// </summary>
public sealed class WebSearchTool : BuiltinTool
{
    public const int DefaultCount = 8;
    public const int MaxCount = 20;

    /// <summary>单条摘要截多长(Exa 那类会返回整页正文,十条足以撑爆上下文)。</summary>
    public const int SnippetChars = 500;

    private readonly SearchService _search;

    public WebSearchTool(SearchService search)
        : base(
            "web_search",
            "Searches the web and returns a list of results (title, link, snippet).\n\n"
            + "- IMPORTANT: this returns A LIST OF RESULTS, not an answer summarized for you. To read one, open its link with WebFetch\n"
            + "- Write query as search terms, not as a full sentence — search engines respond better to keywords\n"
            + "- Results come back in the order the search provider gave them. The numbering is not a ranking of trustworthiness; look at where a link comes from before citing it\n"
            + "- Snippets may be truncated. When a snippet is not enough to answer the question, open the page with WebFetch instead of guessing\n"
            + "- The user can configure several search providers in priority order. A failing provider falls through to the next automatically, so you do not need to retry\n"
            + "- It needs at least one search provider configured (with an API key) in Settings; if none is, the result says so — then stop and tell the user instead of retrying",
            S.Build(S.Object(
                ["query"],
                ("query", S.String("The search query. Write it as search terms, the way you would in a search engine, not as a full question", minLength: 1, maxLength: 500)),
                ("count", S.Integer($"How many results to return. Defaults to {DefaultCount}", minimum: 1, maximum: MaxCount)))),
            new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: true),
            "Search the web for current information",
            ["Use web_search to find pages; then use WebFetch to read the ones that matter. Do not cite a snippet as if you had read the page."])
    {
        _search = search;
    }

    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var query = RequiredString(args, "query").Trim();
        var count = Math.Clamp(OptionalInt(args, "count") ?? DefaultCount, 1, MaxCount);
        ctx.Progress.Report(new ToolProgress($"Searching: {query}"));

        var outcome = await _search.RunAsync(query, count, ct).ConfigureAwait(false);
        var rt = ctx.Runtime;

        if (outcome.Results.Count == 0)
        {
            var notes = string.Join("\n", outcome.Failures.Select(f => $"- {f.Message}"));
            string message;
            if (outcome.NothingConfigured)
            {
                message = "No search provider is configured. Ask the user to open Settings > Connections > Search and enable a provider with its API key. Changing the query will not help.";
            }
            else if (outcome.Failures.All(f => f.Message.EndsWith("returned no results.", StringComparison.Ordinal)))
            {
                message = $"The search itself worked, but no provider had results for \"{query}\". Rephrasing the query or using different keywords is worth a try.\n\nDetails:\n{notes}";
            }
            else
            {
                message = $"Every configured search provider failed:\n{notes}";
            }

            return ToolResult.Error(message, Details(rt, "webSearch", w => WriteHead(w, query, null, 0, outcome)));
        }

        // 成功时也把中途的失败附上:用户那个填错了的 Key 只有在这里才有机会被看见
        var failureNote = outcome.Failures.Count == 0
            ? ""
            : $"\n\n(These providers could not be used this time: {string.Join("; ", outcome.Failures.Select(f => f.Message))})";
        var text = $"Search results for \"{query}\" (via {SearchProviders.Find(outcome.Provider!)?.Name ?? outcome.Provider}):\n\n{Render(outcome.Results)}{failureNote}";
        return ToolResult.Text(text, Details(rt, "webSearch", w =>
        {
            WriteHead(w, query, outcome.Provider, outcome.Results.Count, outcome);
            w.WriteStartArray("results");
            foreach (var r in outcome.Results)
            {
                w.WriteStartObject();
                w.WriteString("title", r.Title);
                w.WriteString("url", r.Url);
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }));
    }

    private static void WriteHead(Utf8JsonWriter w, string query, string? provider, int count, SearchOutcome outcome)
    {
        w.WriteString("query", query);
        if (provider is not null)
        {
            w.WriteString("provider", provider);
        }

        w.WriteNumber("resultCount", count);
        w.WriteStartArray("failedProviders");
        foreach (var f in outcome.Failures)
        {
            w.WriteStringValue(f.ProviderId);
        }

        w.WriteEndArray();
    }

    /// <summary>结果排成模型好读的样子。每条都带 url(没有 url 的结果在 harvest 时就丢掉了)。</summary>
    internal static string Render(IReadOnlyList<SearchResult> results)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (i > 0)
            {
                sb.Append("\n\n");
            }

            sb.Append(i + 1).Append(". ").Append(r.Title).Append("\n   ").Append(r.Url);
            if (r.PublishedAt is { } date)
            {
                sb.Append(" · ").Append(date);
            }

            if (r.Snippet.Length > 0)
            {
                sb.Append("\n   ").Append(r.Snippet.Length > SnippetChars ? r.Snippet[..SnippetChars] : r.Snippet);
            }
        }

        return sb.ToString();
    }
}
