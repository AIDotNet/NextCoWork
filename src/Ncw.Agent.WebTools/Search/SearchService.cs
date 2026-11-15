using System.Diagnostics;
using Ncw.Agent.WebTools.Net;

namespace Ncw.Agent.WebTools.Search;

/// <summary>搜索配置的来源(Host 接到库的 <c>search_providers</c> 表和 NCK1 凭据库上)。每次调用现读:用户刚在设置页改完,下一次搜索就该按新值来。</summary>
public interface ISearchConfigSource
{
    /// <summary>当前的服务列表(含 hasKey),目录里的全部(见 <see cref="SearchProviders.Merge"/>)。</summary>
    ValueTask<IReadOnlyList<SearchProviderStatus>> StatusesAsync(CancellationToken ct);

    /// <summary><b>明文 Key。只有 <see cref="SearchService"/> 调它</b>,工具那一侧永远拿不到——它只能说「帮我搜一下」,拿回结果。</summary>
    ValueTask<string?> ApiKeyAsync(string id, CancellationToken ct);
}

public sealed record SearchOptions
{
    /// <summary>一家最多等多久。全败的上限是这个数乘以链长,所以不能松。</summary>
    public TimeSpan PerProviderTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>「测试」按钮的硬上限(每家 15 秒之上再兜一层)。</summary>
    public TimeSpan TestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed record SearchFailure(string ProviderId, string Message);

public sealed record SearchOutcome(IReadOnlyList<SearchResult> Results, string? Provider, IReadOnlyList<SearchFailure> Failures, bool NothingConfigured);

/// <param name="Key">稳定 key(控制端翻译):失败时 <c>websearch.test.unavailable</c> / <c>noAdapter</c> / <c>noKey</c> / <c>failed</c>;成功但没结果时 <c>websearch.test.noResults</c>(Key 是好的,只是这次查询没有返回结果)。<paramref name="Detail"/> 是英文原话(失败时服务器说了什么)。</param>
public sealed record SearchTestResult(bool Ok, long? LatencyMs, string? Key, string? Detail);

/// <summary>
/// 搜索:按优先级依次调用,失败切下一家(2.x <c>search/service.ts</c>;与模型路由同构)。
/// <para>
/// 切下一家的三个条件:① 适配器抛异常(网络错、非 2xx、响应不是 JSON)→ 切;② 返回空数组 → <b>也切</b>(「这家好好的但没搜到」在理论上该如实报告,
/// 可实际上零结果最常见的成因是这家的响应字段改了名,切一下的代价是一次多余的请求,不切的代价是「搜索坏了但看起来像没搜到」,后者要难查得多);
/// ③ 没配 Key / 标了 unavailable / 没有适配器 → 根本不进链。
/// </para>
/// <para>
/// ★ 一路上的失败<b>即使最后成功了也带着</b>:用户那个填错了的 Key 只有在这里才有机会被看见——悄悄退到下一家的话,它会一直错下去,而每次搜索都白花一次往返。
/// ★ 超时<b>只中断这一家</b>,不动调用方的取消令牌(那是整个 Run 的中断信号);Run 被中断时立刻往上抛,不接着试下一家。
/// ★ 「测试」只回通不通,<b>不回结果</b>:回结果的话这条协议方法就成了一个绕过工具链、绕过联网开关的搜索入口。
/// </para>
/// 2.x 的「免 Key 内置源」兜底(公共 SearxNG 实例 + 直抓 SERP)<b>本批未移植</b>(见 parity):一家都没配时 <see cref="SearchOutcome.NothingConfigured"/> 为真,工具如实告诉模型去配置。
/// </summary>
public sealed class SearchService(ISearchConfigSource source, IWebHttp http, SearchOptions? options = null)
{
    private readonly SearchOptions _options = options ?? new SearchOptions();

    public async Task<SearchOutcome> RunAsync(string query, int count, CancellationToken ct)
    {
        var chain = SearchProviders.Chain(await source.StatusesAsync(ct).ConfigureAwait(false));
        var failures = new List<SearchFailure>();
        var attempted = false;
        foreach (var status in chain)
        {
            ct.ThrowIfCancellationRequested();
            var id = status.Config.Id;
            if (!SearchAdapters.Table.TryGetValue(id, out var adapter))
            {
                continue;
            }

            var key = await source.ApiKeyAsync(id, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(key))
            {
                // hasKey 说有、现取却没有:库和密钥环不一致,如实记一笔再往下走
                failures.Add(new SearchFailure(id, $"{Label(id)}: the API key could not be read; the stored credential may be damaged."));
                attempted = true;
                continue;
            }

            attempted = true;
            try
            {
                var results = await WithTimeoutAsync(ct, _options.PerProviderTimeout, token => adapter(new AdapterInput(query, count), key, http, token)).ConfigureAwait(false);
                if (results.Count > 0)
                {
                    return new SearchOutcome([.. results.Take(count)], id, failures, false);
                }

                failures.Add(new SearchFailure(id, $"{Label(id)} returned no results."));
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Run 被中断时(ct 已取消)不吞,直接往上抛;其余(含这一家自己的超时)记一笔换下一家
                failures.Add(new SearchFailure(id, $"{Label(id)}: {Describe(e)}"));
            }
        }

        return new SearchOutcome([], null, failures, NothingConfigured: !attempted);
    }

    /// <summary>设置页的「测试」按钮:真发一次最小查询(假的 ping 测不出「Key 过期」和「额度用完」,而那两样正是用户点这个按钮时最想知道的)。</summary>
    public async Task<SearchTestResult> TestAsync(string id, CancellationToken ct)
    {
        if (SearchProviders.Find(id) is not { } meta || meta.UnavailableKey is not null)
        {
            return new SearchTestResult(false, null, "websearch.test.unavailable", null);
        }

        if (!SearchAdapters.Table.TryGetValue(id, out var adapter))
        {
            return new SearchTestResult(false, null, "websearch.test.noAdapter", null);
        }

        var key = await source.ApiKeyAsync(id, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(key))
        {
            return new SearchTestResult(false, null, "websearch.test.noKey", null);
        }

        var clock = Stopwatch.StartNew();
        using var hard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hard.CancelAfter(_options.TestTimeout);
        try
        {
            // 只要 1 条:这是连通性测试,不是搜索,没必要为它花一次完整额度
            var results = await WithTimeoutAsync(hard.Token, _options.PerProviderTimeout, token => adapter(new AdapterInput("hello", 1), key, http, token)).ConfigureAwait(false);
            // 通了但没结果:Key 是好的,值得说清楚,免得用户以为测试失败了(Detail 为 null、ok 为 true;控制端按 latency 展示「连接正常」)
            return new SearchTestResult(true, clock.ElapsedMilliseconds, results.Count == 0 ? "websearch.test.noResults" : null, null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new SearchTestResult(false, null, "websearch.test.failed", Describe(e));
        }
    }

    private static string Label(string id) => SearchProviders.Find(id)?.Name ?? id;

    private static string Describe(Exception e) => e switch
    {
        OperationCanceledException => "timed out",
        SsrfBlockedException blocked => blocked.Message,
        _ => e.Message,
    };

    /// <summary>给一家套上超时。★ 超时只中断这一家,不动 <paramref name="outer"/>(整个 Run 的中断信号)。</summary>
    private static async Task<T> WithTimeoutAsync<T>(CancellationToken outer, TimeSpan timeout, Func<CancellationToken, Task<T>> action)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer);
        linked.CancelAfter(timeout);
        return await action(linked.Token).ConfigureAwait(false);
    }
}
