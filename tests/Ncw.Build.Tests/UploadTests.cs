using System.Net;
using System.Text;
using System.Text.Json;
using Ncw.Build.Core;
using Ncw.Build.Release;

namespace Ncw.Build.Tests;

/// <summary>
/// upload:本地先做客户端会做的检查(验签、通道、平台、文件与清单一致),再按后端 /api/client/updates/v3 的协议上传
/// (HEAD 已有就跳过 → PUT 按 sha256 → POST publish 清单原始字节 + 分离签名)。后端侧的同一套约定由 Codex/CoWork 的 AuthTests 覆盖。
/// </summary>
[Collection(nameof(UploadTests))] // HandlerFactory 是静态的:这一组不和自己并行
public sealed class UploadTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly MemoryBuildLog _log = new();
    private readonly FakeBackend _backend = new();
    private readonly (string Private, string Public) _key = Ed25519.Generate();

    public UploadTests() => UploadCommand.HandlerFactory = () => _backend;

    public void Dispose()
    {
        UploadCommand.HandlerFactory = null;
        _dir.Dispose();
    }

    private string Feed => _dir.Sub("feed");

    /// <summary>一个 osx-arm64 / stable 的 Velopack 输出目录 + 用 <see cref="_key"/> 签名的清单。</summary>
    private async Task<BuildContext> PreparedAsync(string channel = "stable", Dictionary<string, string>? env = null, bool sign = true)
    {
        _dir.Write("feed/releases.stable.json", """{"Assets":[]}""");
        _dir.WriteBytes("feed/NextCoWork-3.0.1-stable-full.nupkg", Encoding.UTF8.GetBytes(new string('f', 5000)));
        _dir.Write("feed/NextCoWork-stable-Setup.pkg", "setup");
        var signing = new Dictionary<string, string>();
        if (sign)
        {
            signing[Ed25519.KeyEnv] = _key.Private;
            signing[Ed25519.KeyIdEnv] = "k1";
        }

        var maker = TestKit.Context(_dir, new FakeRunner(), new MemoryBuildLog(), TestKit.MacArm, signing);
        Assert.Equal(0, await Program.RunAsync(["manifest", "--scan", Feed, "--rid", "osx-arm64", "--channel", channel, "--version", "3.0.1", "--out", Feed], maker, default));
        var all = new Dictionary<string, string>
        {
            [UploadCommand.TokenEnv] = "secret-token",
            ["NCW_RELEASE_PUBLIC_KEYS"] = $"old:{Ed25519.Generate().PublicKey},k1:{_key.Public}",
        };
        foreach (var (k, v) in env ?? [])
        {
            all[k] = v;
        }

        return TestKit.Context(_dir, new FakeRunner(), _log, TestKit.MacArm, all);
    }

    private static Task<int> Upload(BuildContext ctx, params string[] extra) =>
        Program.RunAsync(["upload", "--rid", "osx-arm64", "--dir", ctx.Repo.Root + "/../feed", "--server", "https://feed.example", .. extra], ctx, default);

    [Fact]
    public async Task Uploads_every_file_by_sha256_then_publishes_the_exact_manifest_bytes()
    {
        var ctx = await PreparedAsync();
        Assert.Equal(0, await Upload(ctx));

        Assert.Equal(3, _backend.Blobs.Count);
        Assert.All(_backend.Requests, r => Assert.Equal("secret-token", r.Token));
        var publish = Assert.Single(_backend.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal("https://feed.example/api/client/updates/v3/stable/osx-arm64/publish", publish.Uri);
        using var body = JsonDocument.Parse(publish.Body);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Feed, "release-manifest.json")), Convert.FromBase64String(body.RootElement.GetProperty("manifest").GetString()!));
        Assert.Equal(File.ReadAllText(Path.Combine(Feed, "release-manifest.json.sig")).Trim(), body.RootElement.GetProperty("signature").GetString());
        Assert.False(body.RootElement.GetProperty("allowOlder").GetBoolean());
        Assert.DoesNotContain(_log.Lines, l => l.Contains("secret-token")); // 令牌不进日志
    }

    [Fact]
    public async Task A_rerun_skips_files_the_backend_already_has()
    {
        var ctx = await PreparedAsync();
        Assert.Equal(0, await Upload(ctx));
        _backend.Requests.Clear();
        Assert.Equal(0, await Upload(ctx, "--allow-older"));
        Assert.DoesNotContain(_backend.Requests, r => r.Method == HttpMethod.Put);
        Assert.Contains(_log.Lines, l => l.Contains("already there"));
        using var body = JsonDocument.Parse(Assert.Single(_backend.Requests, r => r.Method == HttpMethod.Post).Body);
        Assert.True(body.RootElement.GetProperty("allowOlder").GetBoolean());
    }

    [Fact]
    public async Task Dry_run_needs_no_token_and_sends_nothing()
    {
        var ctx = await PreparedAsync(env: new() { [UploadCommand.TokenEnv] = "" });
        Assert.Equal(0, await Upload(ctx, "--dry-run"));
        Assert.Empty(_backend.Requests);
        Assert.Equal(1, await Upload(ctx));
        Assert.Contains(_log.Lines, l => l.Contains(UploadCommand.TokenEnv));
    }

    [Fact]
    public async Task An_unsigned_manifest_is_never_uploaded()
    {
        var ctx = await PreparedAsync(sign: false);
        Assert.Equal(1, await Upload(ctx));
        Assert.Contains(_log.Lines, l => l.Contains("not signed"));
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task A_signature_from_a_key_clients_do_not_trust_is_refused_before_upload()
    {
        var ctx = await PreparedAsync(env: new() { ["NCW_RELEASE_PUBLIC_KEYS"] = $"k1:{Ed25519.Generate().PublicKey}" });
        Assert.Equal(1, await Upload(ctx));
        Assert.Contains(_log.Lines, l => l.Contains("Clients would reject"));
        Assert.Empty(_backend.Requests);

        var unknownKid = await PreparedAsync(env: new() { ["NCW_RELEASE_PUBLIC_KEYS"] = $"other:{_key.Public}" });
        Assert.Equal(1, await Upload(unknownKid));
        Assert.Contains(_log.Lines, l => l.Contains("'k1', which is not in NCW_RELEASE_PUBLIC_KEYS"));
    }

    [Fact]
    public async Task A_file_changed_after_signing_or_a_channel_mismatch_is_refused()
    {
        var ctx = await PreparedAsync();
        File.WriteAllText(Path.Combine(Feed, "NextCoWork-stable-Setup.pkg"), "rebuilt");
        Assert.Equal(1, await Upload(ctx));
        Assert.Contains(_log.Lines, l => l.Contains("NextCoWork-stable-Setup.pkg does not match the manifest"));

        var beta = await PreparedAsync(channel: "beta");
        Assert.Equal(1, await Upload(beta));
        Assert.Contains(_log.Lines, l => l.Contains("is for channel 'beta', not 'stable'"));
        Assert.Empty(_backend.Requests);
    }

    /// <summary>2.x <c>collectArtifacts reports missing targets</c> 的对应:3.x 的文件清单来自签过名的清单,缺一个就整次不传(没有「部分上传」)。</summary>
    [Fact]
    public async Task A_file_listed_in_the_manifest_but_missing_from_the_folder_stops_the_upload()
    {
        var ctx = await PreparedAsync();
        File.Delete(Path.Combine(Feed, "NextCoWork-3.0.1-stable-full.nupkg"));
        Assert.Equal(1, await Upload(ctx));
        Assert.Contains(_log.Lines, l => l.Contains("Missing file: NextCoWork-3.0.1-stable-full.nupkg"));
        Assert.Empty(_backend.Requests);
    }

    [Fact]
    public async Task Backend_errors_surface_their_message_and_a_bad_token_is_named()
    {
        var ctx = await PreparedAsync();
        _backend.PublishFailure = (HttpStatusCode.Conflict, """{"code":409,"message":"stable/osx-arm64 is at 3.0.2; refusing to publish the older 3.0.1","data":null}""");
        Assert.Equal(1, await Upload(ctx));
        Assert.Contains(_log.Lines, l => l.Contains("HTTP 409 stable/osx-arm64 is at 3.0.2"));

        _backend.Token = "rotated";
        Assert.Equal(1, await Upload(ctx));
        Assert.Contains(_log.Lines, l => l.Contains("rejected the upload token"));
    }

    [Theory]
    [InlineData("https://nextco.work", true)]
    [InlineData("http://127.0.0.1:5040", true)]
    [InlineData("http://localhost:5040", true)]
    [InlineData("http://nextco.work", false)]
    [InlineData("ftp://nextco.work", false)]
    public void Only_https_or_loopback_http_servers_are_accepted(string server, bool ok)
    {
        if (ok)
        {
            Assert.EndsWith("/", UploadCommand.ServerUri(server).AbsoluteUri);
        }
        else
        {
            Assert.Throws<BuildException>(() => UploadCommand.ServerUri(server));
        }
    }

    /// <summary>后端 v3 接口的最小替身:HEAD / PUT blobs、POST publish,检查上传令牌和 sha256。</summary>
    private sealed class FakeBackend : HttpMessageHandler
    {
        public string Token { get; set; } = "secret-token";

        public Dictionary<string, byte[]> Blobs { get; } = [];

        public List<(HttpMethod Method, string Uri, string? Token, string Body)> Requests { get; } = [];

        public (HttpStatusCode Status, string Body)? PublishFailure { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = request.Headers.TryGetValues("X-Client-Upload-Token", out var values) ? values.Single() : null;
            var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Add((request.Method, request.RequestUri!.AbsoluteUri, token, Encoding.UTF8.GetString(bytes)));
            if (token != Token)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"code":401,"message":"上传凭证无效"}""") };
            }

            var path = request.RequestUri.AbsolutePath;
            if (path.Contains("/blobs/", StringComparison.Ordinal))
            {
                var sha = path[(path.LastIndexOf('/') + 1)..];
                if (request.Method == HttpMethod.Head)
                {
                    var head = new HttpResponseMessage(Blobs.ContainsKey(sha) ? HttpStatusCode.OK : HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
                    head.Content.Headers.ContentLength = Blobs.TryGetValue(sha, out var b) ? b.Length : 0;
                    return head;
                }

                Assert.Equal(sha, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
                Blobs[sha] = bytes;
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
            }

            if (PublishFailure is { } failure)
            {
                return new HttpResponseMessage(failure.Status) { Content = new StringContent(failure.Body) };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"code":200,"message":"success","data":{}}""") };
        }
    }
}
