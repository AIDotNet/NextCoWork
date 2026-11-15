using System.Net;
using System.Text;
using Ncw.Agent.Skills.Install;

namespace Ncw.Agent.Skills.Tests;

/// <summary>市场目录的 HTTP 抓取(假 <see cref="HttpMessageHandler"/>,不碰网络):信封、图标解析、授权的状态码映射、下载封顶与进度。</summary>
public sealed class HttpSkillMarketClientTests
{
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

    private static (HttpSkillMarketClient Client, Handler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond, string? token = null)
    {
        var handler = new Handler(respond);
        return (new HttpSkillMarketClient(new HttpClient(handler), () => token), handler);
    }

    [Fact]
    public async Task List_builds_the_query_unwraps_the_data_envelope_and_resolves_icons_and_authors()
    {
        var (client, handler) = Make(_ => Json("""
            {"data":{"items":[
              {"slug":"pdf","name":"pdf","displayName":"PDF","description":"d","category":"docs","iconUrl":"/static/pdf.png","author":{"name":"Acme"},"version":"1.0.0","downloadCount":7},
              {"slug":"x","name":"x","iconUrl":"javascript:alert(1)","author":"Bob"},
              {"name":"no-slug"}
            ]}}
            """));

        var items = await client.ListAsync(new string('q', 150), "docs", CancellationToken.None);

        Assert.Equal(2, items.Count); // 没有 slug 的丢掉
        Assert.Equal(("PDF", "https://nextco.work/static/pdf.png", "Acme", 7), (items[0].DisplayName, items[0].IconUrl, items[0].Author, items[0].DownloadCount));
        Assert.Null(items[1].IconUrl); // 只认 http / https
        Assert.Equal("Bob", items[1].Author);
        var uri = handler.Requests[0].RequestUri!;
        Assert.Equal("https://nextco.work/api/skills", uri.GetLeftPart(UriPartial.Path));
        Assert.Contains("category=docs", uri.Query);
        Assert.Contains("q=" + new string('q', 100) + "&", uri.Query + "&"); // 查询词截到 100
        Assert.DoesNotContain(new string('q', 101), uri.Query);
        Assert.Null(handler.Requests[0].Headers.Authorization); // 浏览不带令牌
    }

    [Fact]
    public async Task A_payload_without_the_envelope_is_read_as_is_and_categories_and_detail_work()
    {
        var (client, _) = Make(r => r.RequestUri!.AbsolutePath.EndsWith("categories", StringComparison.Ordinal)
            ? Json("""{"categories":["a","b"]}""")
            : Json("""{"skill":{"slug":"pdf","name":"pdf"},"versions":[{"version":"1.0.0","sha256":"abc","fileSize":123,"changelog":"first"}]}"""));

        Assert.Equal(["a", "b"], await client.CategoriesAsync(CancellationToken.None));
        var detail = await client.DetailAsync("pdf", CancellationToken.None);
        Assert.Equal("pdf", detail.Item.Slug);
        Assert.Equal(("1.0.0", "abc", 123L, "first"), (detail.Versions[0].Version, detail.Versions[0].Sha256, detail.Versions[0].FileSize, detail.Versions[0].Changelog));
    }

    [Fact]
    public async Task A_non_success_status_or_garbage_is_a_network_failure_with_a_stable_key()
    {
        var (bad, _) = Make(_ => Json("{}", HttpStatusCode.InternalServerError));
        Assert.Equal(SkillErrorKeys.NetworkFailed, (await Assert.ThrowsAsync<SkillException>(() => bad.CategoriesAsync(CancellationToken.None))).Key);

        var (garbage, _) = Make(_ => Json("not json"));
        Assert.Equal(SkillErrorKeys.NetworkFailed, (await Assert.ThrowsAsync<SkillException>(() => garbage.CategoriesAsync(CancellationToken.None))).Key);

        var throwing = new HttpSkillMarketClient(new HttpClient(new ThrowingHandler()));
        Assert.Equal(SkillErrorKeys.NetworkFailed, (await Assert.ThrowsAsync<SkillException>(() => throwing.CategoriesAsync(CancellationToken.None))).Key);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new HttpRequestException("down");
    }

    [Fact]
    public async Task Invariant_a_grant_needs_a_token_and_the_token_is_sent_only_there()
    {
        var (noToken, handler) = Make(_ => Json("""{"data":{"version":"1.0.0","sha256":"%SHA%"}}"""));
        Assert.Equal(SkillErrorKeys.AuthRequired, (await Assert.ThrowsAsync<SkillException>(() => noToken.GrantAsync("pdf", "1.0.0", CancellationToken.None))).Key);
        Assert.Empty(handler.Requests);

        var sha = new string('a', 64);
        var (client, withToken) = Make(_ => Json("{\"data\":{\"version\":\"1.0.0\",\"sha256\":\"" + sha + "\"}}"), token: "tok-123");
        var grant = await client.GrantAsync("pdf", "1.0.0", CancellationToken.None);

        Assert.Equal(("1.0.0", sha), (grant.Version, grant.Sha256));
        Assert.Equal("Bearer tok-123", withToken.Requests[0].Headers.Authorization!.ToString());
        Assert.Equal(HttpMethod.Post, withToken.Requests[0].Method);
        Assert.EndsWith("client/skills/pdf/install", withToken.Requests[0].RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, SkillErrorKeys.ScopeRequired)]
    [InlineData(HttpStatusCode.Unauthorized, SkillErrorKeys.AuthRequired)]
    [InlineData(HttpStatusCode.NotFound, SkillErrorKeys.VersionUnavailable)]
    [InlineData(HttpStatusCode.BadGateway, SkillErrorKeys.NetworkFailed)]
    public async Task Grant_status_codes_map_to_stable_keys(HttpStatusCode status, string key)
    {
        var (client, _) = Make(_ => Json("{}", status), token: "t");

        Assert.Equal(key, (await Assert.ThrowsAsync<SkillException>(() => client.GrantAsync("pdf", "1.0.0", CancellationToken.None))).Key);
    }

    [Theory]
    [InlineData("""{"data":{"version":"2.0.0","sha256":"%S%"}}""")]       // 版本对不上
    [InlineData("""{"data":{"version":"1.0.0","sha256":"short"}}""")]      // 摘要不是 64 位十六进制
    [InlineData("""{"data":{"version":"1.0.0"}}""")]                       // 没有摘要
    public async Task A_grant_without_a_usable_matching_digest_is_a_digest_mismatch(string body)
    {
        var (client, _) = Make(_ => Json(body.Replace("%S%", new string('a', 64), StringComparison.Ordinal)), token: "t");

        Assert.Equal(SkillErrorKeys.DigestMismatch, (await Assert.ThrowsAsync<SkillException>(() => client.GrantAsync("pdf", "1.0.0", CancellationToken.None))).Key);
    }

    [Fact]
    public async Task Download_returns_the_bytes_and_reports_a_final_progress_frame()
    {
        var payload = new byte[300 * 1024];
        new Random(1).NextBytes(payload);
        var (client, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var frames = new List<SkillDownloadProgress>();

        var bytes = await client.DownloadAsync("pdf", "1.0.0", SkillLimits.MaxZipBytes, frames.Add, CancellationToken.None);

        Assert.Equal(payload, bytes);
        Assert.Equal(payload.Length, frames[^1].Received); // 节流会吞掉最后一帧,所以要补
        Assert.Equal(payload.Length, frames[^1].Total);
        Assert.True(frames.Count < 50);
    }

    [Fact]
    public async Task Invariant_download_is_capped_both_by_the_declared_length_and_while_streaming()
    {
        var (declared, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2048]) });
        Assert.Equal(SkillErrorKeys.PackageTooLarge, (await Assert.ThrowsAsync<SkillException>(() => declared.DownloadAsync("pdf", "1.0.0", 1024, null, CancellationToken.None))).Key);

        var (streaming, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(new byte[4096]) });
        Assert.Equal(SkillErrorKeys.PackageTooLarge, (await Assert.ThrowsAsync<SkillException>(() => streaming.DownloadAsync("pdf", "1.0.0", 1024, null, CancellationToken.None))).Key);
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task A_missing_version_download_is_version_unavailable()
    {
        var (client, _) = Make(_ => Json("{}", HttpStatusCode.NotFound));

        Assert.Equal(SkillErrorKeys.VersionUnavailable, (await Assert.ThrowsAsync<SkillException>(() => client.DownloadAsync("pdf", "9", 1024, null, CancellationToken.None))).Key);
    }
}
