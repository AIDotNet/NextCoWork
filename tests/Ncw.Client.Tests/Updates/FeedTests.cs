using System.Net;
using System.Text;
using Ncw.Client.Updates;

namespace Ncw.Client.Tests.Updates;

/// <summary>feed 的位置解析与读取(本地目录 / HTTP),以及「读清单 + 验签」。</summary>
public class FeedTests
{
    [Fact]
    public void The_default_template_points_at_the_backend_agreed_in_the_design_and_is_per_channel_and_platform()
    {
        var feed = FeedLocation.Resolve(FeedLocation.DefaultTemplate, UpdateChannel.Beta, "osx-arm64");
        Assert.Equal("https://nextco.work/api/client/updates/v3/beta/osx-arm64/", feed!.Http!.AbsoluteUri);
        Assert.Null(feed.Directory);
    }

    [Theory]
    [InlineData("https://example.com/feed", "https://example.com/feed/")]
    [InlineData("http://127.0.0.1:8080/x/", "http://127.0.0.1:8080/x/")]
    [InlineData("http://localhost:9/{channel}", "http://localhost:9/stable/")]
    public void Https_and_loopback_http_are_http_feeds(string template, string expected) =>
        Assert.Equal(expected, FeedLocation.Resolve(template, UpdateChannel.Stable, "osx-arm64")!.Http!.AbsoluteUri);

    [Theory]
    [InlineData("http://example.com/feed")]    // 公网明文
    [InlineData("ftp://example.com/feed")]
    [InlineData("")]
    [InlineData("   ")]
    public void Plain_http_to_the_internet_and_other_schemes_are_refused(string template) =>
        Assert.Null(FeedLocation.Resolve(template, UpdateChannel.Stable, "osx-arm64"));

    [Fact]
    public void Absolute_and_relative_paths_and_file_urls_are_local_directories()
    {
        var abs = Path.Combine(Path.GetTempPath(), "ncw-feed", "{channel}", "{rid}");
        Assert.Equal(Path.Combine(Path.GetTempPath(), "ncw-feed", "stable", "win-x64"), FeedLocation.Resolve(abs, UpdateChannel.Stable, "win-x64")!.Directory);
        Assert.Equal(Path.GetFullPath("artifacts/feed"), FeedLocation.Resolve("artifacts/feed", UpdateChannel.Stable, "win-x64")!.Directory);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "x"), FeedLocation.Resolve(new Uri(Path.Combine(Path.GetTempPath(), "x")).AbsoluteUri, UpdateChannel.Stable, "win-x64")!.Directory?.TrimEnd('/'));
    }

    [Fact]
    public async Task Directory_reader_returns_null_for_missing_files_and_refuses_path_escapes_and_oversize()
    {
        var dir = Directory.CreateTempSubdirectory("ncw-feed-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.json"), "hello");
            var reader = new DirectoryFeedReader(dir);
            Assert.Equal("hello", Encoding.UTF8.GetString((await reader.ReadAsync("a.json", 100, default))!));
            Assert.Null(await reader.ReadAsync("missing.json", 100, default));
            var escape = await Assert.ThrowsAsync<UpdateException>(() => reader.ReadAsync("../a.json", 100, default));
            Assert.Equal(UpdateFailureKind.FeedInvalid, escape.Kind);
            var big = await Assert.ThrowsAsync<UpdateException>(() => reader.ReadAsync("a.json", 2, default));
            Assert.Equal(UpdateFailureKind.FeedInvalid, big.Kind);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class LocalHttp : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public LocalHttp(Func<string, (int Status, byte[] Body)> route)
        {
            var port = new Random().Next(20000, 60000);
            for (;; port++)
            {
                try
                {
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/feed/");
                    _listener.Start();
                    break;
                }
                catch (HttpListenerException)
                {
                    _listener.Prefixes.Clear();
                }
            }

            BaseUri = new Uri($"http://127.0.0.1:{port}/feed/");
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    try
                    {
                        var ctx = await _listener.GetContextAsync();
                        var (status, body) = route(ctx.Request.Url!.AbsolutePath["/feed/".Length..]);
                        ctx.Response.StatusCode = status;
                        await ctx.Response.OutputStream.WriteAsync(body);
                        ctx.Response.Close();
                    }
                    catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }
                }
            });
        }

        public Uri BaseUri { get; }

        public void Dispose()
        {
            _listener.Close();
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Http_reader_reads_404_as_missing_5xx_as_network_and_caps_the_body()
    {
        using var server = new LocalHttp(name => name switch
        {
            "ok.txt" => (200, "hello"u8.ToArray()),
            "big.bin" => (200, new byte[10_000]),
            "boom.txt" => (500, []),
            _ => (404, []),
        });
        using var client = new HttpClient();
        var reader = new HttpFeedReader(client, server.BaseUri);

        Assert.Equal("hello", Encoding.UTF8.GetString((await reader.ReadAsync("ok.txt", 100, default))!));
        Assert.Null(await reader.ReadAsync("nope.txt", 100, default));
        Assert.Equal(UpdateFailureKind.Network, (await Assert.ThrowsAsync<UpdateException>(() => reader.ReadAsync("boom.txt", 100, default))).Kind);
        Assert.Equal(UpdateFailureKind.FeedInvalid, (await Assert.ThrowsAsync<UpdateException>(() => reader.ReadAsync("big.bin", 100, default))).Kind);
    }

    [Fact]
    public async Task Http_reader_reports_an_unreachable_server_as_network()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var reader = new HttpFeedReader(client, new Uri("http://127.0.0.1:1/feed/"));
        Assert.Equal(UpdateFailureKind.Network, (await Assert.ThrowsAsync<UpdateException>(() => reader.ReadAsync("release-manifest.json", 100, default))).Kind);
    }

    [Fact]
    public async Task Signed_manifest_over_http_end_to_end_and_a_tampered_copy_is_refused()
    {
        var key = new TestSigningKey("test-1");
        var (bytes, sig) = UpdateKit.Signed(key, UpdateKit.Manifest("3.0.1"));
        var tamper = false;
        using var server = new LocalHttp(name =>
        {
            var body = name switch
            {
                "release-manifest.json" => tamper ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("3.0.1", "3.0.2", StringComparison.Ordinal)) : bytes,
                "release-manifest.json.sig" => Encoding.ASCII.GetBytes(sig),
                _ => null,
            };
            return body is null ? (404, []) : (200, body);
        });
        using var client = new HttpClient();
        var reader = new HttpFeedReader(client, server.BaseUri);

        var manifest = await SignedManifest.ReadAsync(reader, key.Ring, UpdateChannel.Stable, default);
        Assert.Equal("3.0.1", manifest.Version);

        tamper = true;
        var failure = await Assert.ThrowsAsync<UpdateException>(() => SignedManifest.ReadAsync(reader, key.Ring, UpdateChannel.Stable, default));
        Assert.Equal(UpdateFailureKind.SignatureInvalid, failure.Kind);
    }

    [Fact]
    public async Task A_feed_without_a_manifest_or_without_a_signature_is_refused()
    {
        var key = new TestSigningKey("test-1");
        var feed = new MemoryFeed();
        Assert.Equal(UpdateFailureKind.FeedInvalid, (await Assert.ThrowsAsync<UpdateException>(() => SignedManifest.ReadAsync(feed, key.Ring, UpdateChannel.Stable, default))).Kind);

        feed.Files[FeedLocation.ManifestName] = UpdateKit.Bytes(UpdateKit.Manifest("3.0.1"));
        Assert.Equal(UpdateFailureKind.SignatureMissing, (await Assert.ThrowsAsync<UpdateException>(() => SignedManifest.ReadAsync(feed, key.Ring, UpdateChannel.Stable, default))).Kind);
    }
}
