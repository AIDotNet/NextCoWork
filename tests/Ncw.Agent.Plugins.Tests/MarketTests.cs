using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Ncw.Agent.Plugins.Tests;

/// <summary>
/// 市场(目录 / 授权 / 下载 / 更新):假市场客户端驱动 <see cref="PluginManager"/>(四步安装流程、无令牌 = <c>plugins.authRequired</c>、权威摘要、更新检查与全部更新),
/// 假 <see cref="HttpMessageHandler"/> 驱动 <see cref="HttpPluginMarketClient"/>(信封、状态码映射、下载封顶)。不碰网络。
/// </summary>
public sealed class MarketTests : IAsyncDisposable
{
    private sealed class FakeMarket : IPluginMarketClient
    {
        public Dictionary<string, (PluginMarketItem Item, byte[] Zip)> Plugins { get; } = new(StringComparer.Ordinal);

        public bool SignedIn { get; set; } = true;

        public string? SabotageDigest { get; set; }

        public int ListCalls;
        public List<string> Installs { get; } = [];

        public Task<PluginMarketPage> ListAsync(string? query, string? category, int page, int pageSize, string clientVersion, CancellationToken ct)
        {
            Interlocked.Increment(ref ListCalls);
            var all = Plugins.Values.Select(p => p.Item).Where(i => query is null || i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult(new PluginMarketPage([.. all.Skip((page - 1) * pageSize).Take(pageSize)], all.Count > page * pageSize, page));
        }

        public Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["tools", "docs"]);

        public Task<PluginMarketDetail> DetailAsync(string slug, CancellationToken ct) =>
            Plugins.TryGetValue(slug, out var p)
                ? Task.FromResult(new PluginMarketDetail(p.Item, [new PluginMarketVersion(p.Item.Version!, "notes", null, p.Zip.Length)]))
                : throw new PluginException(PluginErrorKeys.MarketFailed, "404");

        public Task<PluginInstallGrant> GrantAsync(string slug, string version, CancellationToken ct)
        {
            if (!SignedIn)
            {
                throw new PluginException(PluginErrorKeys.AuthRequired, "sign in");
            }

            return Task.FromResult(new PluginInstallGrant(version, SabotageDigest ?? Convert.ToHexStringLower(SHA256.HashData(Plugins[slug].Zip))));
        }

        public Task<byte[]> DownloadAsync(string slug, string version, int maxBytes, Action<PluginDownloadProgress>? progress, CancellationToken ct)
        {
            Installs.Add($"{slug}@{version}");
            progress?.Invoke(new PluginDownloadProgress(Plugins[slug].Zip.Length, Plugins[slug].Zip.Length));
            return Task.FromResult(Plugins[slug].Zip);
        }
    }

    private readonly FakeMarket _market = new();
    private readonly PluginFx _fx;

    public MarketTests()
    {
        _fx = new PluginFx(b => b.Market = _market);
    }

    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    private static PluginMarketItem Item(string id, string slug, string version, params string[] permissions) =>
        new(id, slug, id.Split('.')[0], slug, "d", "tools", null, 1, version, "^0.3.0", permissions, [], "Acme");

    private void Publish(string id, string slug, string version, byte[]? zip = null, params string[] permissions) =>
        _market.Plugins[slug] = (Item(id, slug, version, permissions), zip ?? PluginFx.MinimalZip(id.Split('.')[1], "export function activate() {}", null, id.Split('.')[0], version));

    [Fact]
    public async Task Browsing_lists_pages_categories_and_detail_without_an_account()
    {
        _market.SignedIn = false;
        Publish("acme.alpha", "alpha", "1.0.0", null, "net");
        Publish("acme.beta", "beta", "1.0.0");

        var page = await _fx.Manager.MarketListAsync("alp", null, 1);
        var categories = await _fx.Manager.MarketCategoriesAsync();
        var detail = await _fx.Manager.MarketDetailAsync("alpha");

        Assert.Equal(["alpha"], page.Items.Select(i => i.Slug));
        Assert.Equal(["net"], page.Items[0].Permissions); // 必选能力在列表里就能看到
        Assert.Equal(["tools", "docs"], categories);
        Assert.Equal(("1.0.0", 1), (detail.Item.Version, detail.Versions.Count));
    }

    [Fact]
    public async Task Installing_without_an_account_token_says_auth_required_and_installs_nothing()
    {
        _market.SignedIn = false;
        Publish("acme.alpha", "alpha", "1.0.0");

        var e = await Assert.ThrowsAsync<PluginException>(() => _fx.Manager.InstallMarketAsync("alpha", null));

        Assert.Equal(PluginErrorKeys.AuthRequired, e.Key);
        Assert.Empty(_fx.Manager.Snapshot().Plugins);
        Assert.Empty(_market.Installs); // 授权这一步就挡住了,没有下载
    }

    [Fact]
    public async Task A_market_install_downloads_verifies_the_authoritative_digest_and_remembers_the_slug()
    {
        Publish("acme.alpha", "alpha", "1.0.0");
        var progress = new List<string>();

        var installed = await _fx.Manager.InstallMarketAsync("alpha", null, new SyncProgress(p => progress.Add(p.Phase)));

        Assert.Equal(("acme.alpha", "alpha", "1.0.0"), (installed.Id, installed.Slug, installed.Manifest.Version));
        Assert.Equal(["alpha@1.0.0"], _market.Installs);
        Assert.Contains("done", progress);
        Assert.Equal("preparing", progress[0]);
    }

    [Fact]
    public async Task A_package_whose_digest_differs_from_the_markets_vouched_digest_is_refused()
    {
        Publish("acme.alpha", "alpha", "1.0.0");
        _market.SabotageDigest = new string('a', 64);
        var phases = new List<string>();

        var e = await Assert.ThrowsAsync<PluginException>(() => _fx.Manager.InstallMarketAsync("alpha", null, new SyncProgress(p => phases.Add(p.Phase + ":" + p.ErrorKey))));

        Assert.Equal(PluginErrorKeys.DigestMismatch, e.Key);
        Assert.Empty(_fx.Manager.Snapshot().Plugins);
        Assert.Contains("failed:plugins.digestMismatch", phases); // 终态一定推出去
    }

    [Fact]
    public async Task Two_installs_of_the_same_slug_at_once_run_as_one()
    {
        Publish("acme.alpha", "alpha", "1.0.0");

        var a = _fx.Manager.InstallMarketAsync("alpha", null);
        var b = _fx.Manager.InstallMarketAsync("alpha", null);
        await Task.WhenAll(a, b);

        Assert.Single(_market.Installs);
    }

    [Fact]
    public async Task Update_checks_compare_the_whole_market_against_what_is_installed_and_list_exactly_which_required_permissions_are_new()
    {
        Publish("acme.alpha", "alpha", "1.0.0");
        Publish("acme.beta", "beta", "1.0.0");
        await _fx.Manager.InstallMarketAsync("alpha", null);
        await _fx.Manager.GrantAsync("acme.alpha", []);
        await _fx.Manager.InstallZipAsync(_market.Plugins["beta"].Zip); // 本地装的:不是从市场来的
        Publish("acme.alpha", "alpha", "1.1.0", null, "net");
        Publish("acme.beta", "beta", "1.2.0");

        var updates = await _fx.Manager.CheckUpdatesAsync(force: true);

        var alpha = updates.Single(u => u.PluginId == "acme.alpha");
        Assert.Equal(("1.0.0", "1.1.0", true), (alpha.CurrentVersion, alpha.LatestVersion, alpha.FromMarket));
        Assert.Equal(["net"], alpha.EscalatedPermissions); // 逐条列出,不是「权限有变化」
        var beta = updates.Single(u => u.PluginId == "acme.beta");
        Assert.False(beta.FromMarket); // 本地来源:徽标有、批量更新不动
    }

    [Fact]
    public async Task Update_checks_are_cached_for_ten_minutes_but_computed_against_the_current_state()
    {
        Publish("acme.alpha", "alpha", "1.0.0");
        await _fx.Manager.InstallMarketAsync("alpha", null);
        Publish("acme.alpha", "alpha", "1.1.0");
        var first = await _fx.Manager.CheckUpdatesAsync();
        var calls = _market.ListCalls;

        var second = await _fx.Manager.CheckUpdatesAsync();
        Assert.Equal(calls, _market.ListCalls); // 缓存命中
        Assert.Single(second);

        await _fx.Manager.InstallMarketAsync("alpha", "1.1.0");
        Assert.Empty(await _fx.Manager.CheckUpdatesAsync()); // 更新完,同一份缓存立刻算出「没有更新了」
        Assert.Single(first);
    }

    [Fact]
    public async Task Update_all_updates_only_market_sourced_plugins_and_a_failure_does_not_stop_the_rest()
    {
        Publish("acme.alpha", "alpha", "1.0.0");
        Publish("acme.beta", "beta", "1.0.0");
        Publish("acme.gamma", "gamma", "1.0.0");
        foreach (var slug in new[] { "alpha", "beta", "gamma" })
        {
            await _fx.Manager.InstallMarketAsync(slug, null);
        }

        await _fx.Manager.InstallZipAsync(PluginFx.MinimalZip("local", "export function activate() {}")); // 本地的
        Publish("acme.alpha", "alpha", "1.1.0");
        Publish("acme.beta", "beta", "1.1.0");
        _market.Plugins["gamma"] = (Item("acme.gamma", "gamma", "1.1.0"), Encoding.UTF8.GetBytes("not a zip"));

        var result = await _fx.Manager.UpdateAllAsync();

        Assert.Equal(["acme.alpha", "acme.beta"], result.Updated.Order());
        var failure = Assert.Single(result.Failed);
        Assert.Equal(("acme.gamma", PluginErrorKeys.ZipInvalid), (failure.PluginId, failure.ErrorKey)); // 摘要对得上(假市场按内容算),但内容不是 zip:装不上
    }

    [Fact]
    public async Task Without_a_market_client_the_market_methods_report_market_failed()
    {
        await using var bare = new PluginFx();

        var e = await Assert.ThrowsAsync<PluginException>(() => bare.Manager.MarketCategoriesAsync());

        Assert.Equal(PluginErrorKeys.MarketFailed, e.Key);
    }

    private sealed class SyncProgress(Action<PluginInstallProgress> handler) : IProgress<PluginInstallProgress>
    {
        public void Report(PluginInstallProgress value) => handler(value);
    }

    // ───────────────────────── HTTP 实现 ─────────────────────────

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (HttpPluginMarketClient Client, Handler Handler) Http(Func<HttpRequestMessage, HttpResponseMessage> respond, string? token = null)
    {
        var handler = new Handler(respond);
        return (new HttpPluginMarketClient(new HttpClient(handler), () => token), handler);
    }

    [Fact]
    public async Task The_http_client_builds_the_plugin_query_unwraps_the_envelope_and_normalises_old_servers()
    {
        var (client, handler) = Http(_ => Json("""
            {"data":{"items":[
              {"pluginId":"acme.pdf","slug":"pdf","publisher":"acme","name":"pdf","displayName":"PDF","iconUrl":"/i/pdf.png","author":{"name":"Acme"},"version":"1.0.0","engines":"^0.3.0","permissions":["net"],"skills":["a",7,null]},
              {"slug":"old","name":"old","iconUrl":"javascript:x"},
              {"name":"no-slug"}
            ],"hasNext":true}}
            """));

        var page = await client.ListAsync("pd", "docs", 2, 12, "3.0.0", CancellationToken.None);

        Assert.True(page.HasNext);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(("acme.pdf", "https://nextco.work/i/pdf.png", "Acme"), (page.Items[0].PluginId, page.Items[0].IconUrl, page.Items[0].Author));
        Assert.Equal(["net"], page.Items[0].Permissions);
        Assert.Equal(["a", "7"], page.Items[0].Skills); // 逐条转字符串,null 丢掉
        Assert.Empty(page.Items[1].Permissions); // 老服务端没有这个字段:空数组,不是 null
        Assert.Null(page.Items[1].IconUrl);
        var uri = handler.Requests[0].RequestUri!;
        Assert.Equal("https://nextco.work/api/plugins", uri.GetLeftPart(UriPartial.Path));
        Assert.Contains("client=3.0.0", uri.Query, StringComparison.Ordinal); // 带上本机版本做灰度 / 过滤
        Assert.Contains("page=2", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_http_grant_maps_status_codes_to_stable_keys_and_validates_the_digest()
    {
        PluginException? Catch(Func<Task> work) => Record.ExceptionAsync(work).GetAwaiter().GetResult() as PluginException;

        Assert.Equal(PluginErrorKeys.AuthRequired, Catch(() => Http(_ => Json("{}"), token: null).Client.GrantAsync("p", "1.0.0", default))!.Key);
        Assert.Equal(PluginErrorKeys.AuthRequired, Catch(() => Http(_ => Json("{}", HttpStatusCode.Unauthorized), "t").Client.GrantAsync("p", "1.0.0", default))!.Key);
        Assert.Equal(PluginErrorKeys.ScopeRequired, Catch(() => Http(_ => Json("{}", HttpStatusCode.Forbidden), "t").Client.GrantAsync("p", "1.0.0", default))!.Key);
        Assert.Equal(PluginErrorKeys.VersionUnavailable, Catch(() => Http(_ => Json("{}", HttpStatusCode.NotFound), "t").Client.GrantAsync("p", "1.0.0", default))!.Key);
        Assert.Equal(PluginErrorKeys.MarketFailed, Catch(() => Http(_ => Json("{}", HttpStatusCode.BadGateway), "t").Client.GrantAsync("p", "1.0.0", default))!.Key);
        Assert.Equal(PluginErrorKeys.DigestMismatch, Catch(() => Http(_ => Json("""{"data":{"version":"1.0.1","sha256":"abc"}}"""), "t").Client.GrantAsync("p", "1.0.0", default))!.Key);

        var (client, handler) = Http(_ => Json("{\"data\":{\"version\":\"1.0.0\",\"sha256\":\"" + new string('a', 64) + "\"}}"), "secret-token");
        var grant = await client.GrantAsync("p", "1.0.0", default);
        Assert.Equal(new string('a', 64), grant.Sha256);
        Assert.Equal("Bearer secret-token", handler.Requests[0].Headers.Authorization!.ToString());
        Assert.Equal("https://nextco.work/api/client/plugins/p/install", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task The_http_download_stops_at_the_cap_while_reading_not_after()
    {
        var (client, _) = Http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2 * 1024 * 1024]) });

        var e = await Assert.ThrowsAsync<PluginException>(() => client.DownloadAsync("p", "1.0.0", 1024 * 1024, null, default));

        Assert.Equal(PluginErrorKeys.PackageTooLarge, e.Key);
        var (ok, _) = Http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) });
        var progress = new List<long>();
        Assert.Equal(100, (await ok.DownloadAsync("p", "1.0.0", 1024, p => progress.Add(p.Received), default)).Length);
        Assert.Equal(100, progress[^1]); // 最后一帧一定报出去
    }
}
