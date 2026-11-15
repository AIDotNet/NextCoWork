using System.Net;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Tools;
using Ncw.Agent.WebTools.Fetch;
using Ncw.Agent.WebTools.Net;

namespace Ncw.Agent.WebTools.Tests;

/// <summary>
/// <c>WebFetch</c>(移植自 2.x <c>web.test.ts</c> 的全部用例;2.x 里「本机 / 内网地址被放行」那两条在 3.0 反过来——被拒,见设计 03 §13)。
/// 另补 3.0 的:连接时校验的 SSRF 错误翻译、重定向到内网被拒、字节上限(流式,读到上限就停)、超时、字符集、Details 带环境标识。
/// </summary>
public sealed class WebFetchToolTests
{
    private readonly ToolHarness _h = new();

    private static string Args(string url, string prompt = "the answer") => new System.Text.Json.Nodes.JsonObject { ["url"] = url, ["prompt"] = prompt }.ToJsonString();

    private static WebFetchTool Tool(FakeHttp http, WebFetchLimits? limits = null) => new(http, limits);

    private static FakeHttp Page(string body = "<p>hello</p>", string type = "text/html") => new(_ => FakeHttp.Ok(body, type));

    // ───────────────────────── 标记 ─────────────────────────

    [Fact]
    public void Traits_declare_network_and_read_only_and_the_prompt_pieces_exist()
    {
        var tool = Tool(Page());
        Assert.Equal("WebFetch", tool.Info.InternalId);
        Assert.True(tool.Info.Traits.NeedsNetwork); // 它是联网开关要拦的工具
        Assert.True(tool.Info.Traits.ReadOnly); // 联网不靠 ReadOnly 管,靠权限闸那张表的第 1 行
        Assert.False(tool.Info.Traits.Destructive);
        Assert.False(string.IsNullOrWhiteSpace(tool.Info.PromptSnippet));
        Assert.NotEmpty(tool.Info.PromptGuidelines!);
        Assert.Contains("THE PAGE ITSELF", tool.Info.Description);
    }

    // ───────────────────────── 地址闸门 ─────────────────────────

    [Fact]
    public async Task A_malformed_url_is_refused_without_a_request()
    {
        var http = Page();
        var result = await _h.Run(Tool(http), Args("not a url"));
        Assert.True(result.IsError);
        Assert.Contains("Malformed URL", ToolHarness.TextOf(result));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("http://localhost:3000/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://192.168.1.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/iam/security-credentials/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://100.100.100.200/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://0x7f.1/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    [InlineData("http://router/")]
    [InlineData("http://nas.local/")]
    [InlineData("https://127.0.0.1:8443/")]
    public async Task Loopback_private_link_local_and_metadata_addresses_are_refused_in_every_spelling_and_no_request_is_made(string url)
    {
        var http = Page();
        var result = await _h.Run(Tool(http), Args(url));
        Assert.True(result.IsError, url);
        Assert.Contains("Refusing to reach", ToolHarness.TextOf(result));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task File_is_refused_and_points_to_Read()
    {
        var http = Page();
        var result = await _h.Run(Tool(http), Args("file:///etc/passwd"));
        Assert.True(result.IsError);
        Assert.Contains("Read", ToolHarness.TextOf(result));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Credentials_in_the_url_are_refused()
    {
        var http = Page();
        var result = await _h.Run(Tool(http), Args("https://alice:hunter2@example.com/"));
        Assert.True(result.IsError);
        Assert.Contains("username or password", ToolHarness.TextOf(result));
        Assert.DoesNotContain("hunter2", ToolHarness.TextOf(result));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Http_is_upgraded_to_https_and_a_non_default_port_is_kept()
    {
        var http = Page();
        await _h.Run(Tool(http), Args("http://example.com/a?b=1"));
        await _h.Run(Tool(http), Args("http://example.com:8080/x"));
        Assert.Equal(["https://example.com/a?b=1", "https://example.com:8080/x"], http.Requests.Select(static r => r.Url.ToString()));
    }

    [Fact]
    public async Task A_connect_time_ssrf_refusal_becomes_a_clear_tool_error()
    {
        // 公网域名在连接时被解析到内网(GuardedHttp 抛 SsrfBlockedException)
        var http = new FakeHttp((_, _) => throw new SsrfBlockedException("evil.example", IPAddress.Loopback, "Refusing to reach \"evil.example\": it resolves to a loopback or private-network address (127.0.0.1), and network tools may only reach the public internet."));
        var result = await _h.Run(Tool(http), Args("https://evil.example/"));
        Assert.True(result.IsError);
        Assert.Contains("resolves to a loopback", ToolHarness.TextOf(result));
        Assert.Contains("ask the user", ToolHarness.TextOf(result));
    }

    // ───────────────────────── 重定向 ─────────────────────────

    [Fact]
    public async Task A_same_host_redirect_is_followed()
    {
        var http = new FakeHttp(r => r.RequestUri!.AbsolutePath == "/old" ? FakeHttp.Redirect("/new") : FakeHttp.Ok("<p>arrived</p>"));
        var result = await _h.Run(Tool(http), Args("https://example.com/old"));
        Assert.False(result.IsError);
        Assert.Contains("arrived", ToolHarness.TextOf(result));
        Assert.Equal(["https://example.com/old", "https://example.com/new"], http.Requests.Select(static r => r.Url.ToString()));
        Assert.Contains("https://example.com/new", ToolHarness.TextOf(result)); // 说明的是最终地址
    }

    [Theory]
    [InlineData("http://127.0.0.1/admin")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://localhost/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://2130706433/")]
    [InlineData("http://10.0.0.1/")]
    public async Task A_redirect_into_the_internal_network_is_refused_hop_by_hop(string target)
    {
        var http = new FakeHttp(r => r.RequestUri!.Host == "example.com" ? FakeHttp.Redirect(target) : throw new InvalidOperationException("must not follow"));
        var result = await _h.Run(Tool(http), Args("https://example.com/start"));
        Assert.True(result.IsError);
        Assert.Contains("The redirect target is not allowed", ToolHarness.TextOf(result));
        Assert.Single(http.Requests); // 只发了第一跳
    }

    [Fact]
    public async Task A_redirect_target_with_credentials_is_refused()
    {
        var http = new FakeHttp(_ => FakeHttp.Redirect("https://user:pass@example.com/x"));
        var result = await _h.Run(Tool(http), Args("https://example.com/start"));
        Assert.True(result.IsError);
        Assert.Contains("username or password", ToolHarness.TextOf(result));
    }

    [Fact]
    public async Task A_redirect_to_a_file_or_other_protocol_is_refused()
    {
        var http = new FakeHttp(_ => FakeHttp.Redirect("file:///etc/passwd"));
        var result = await _h.Run(Tool(http), Args("https://example.com/start"));
        Assert.True(result.IsError);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_cross_host_redirect_stops_and_hands_the_new_address_back_to_the_model()
    {
        var http = new FakeHttp(_ => FakeHttp.Redirect("https://other.example.org/landing"));
        var result = await _h.Run(Tool(http), Args("https://example.com/start"));
        Assert.False(result.IsError);
        var text = ToolHarness.TextOf(result);
        Assert.Contains("https://other.example.org/landing", text);
        Assert.Contains("Nothing was fetched", text);
        Assert.Single(http.Requests);
        Assert.Equal("https://other.example.org/landing", result.Details!.Value.GetProperty("redirectTo").GetString());
    }

    [Fact]
    public async Task The_redirect_chain_is_followed_at_most_the_configured_number_of_hops()
    {
        var http = new FakeHttp(r => FakeHttp.Redirect($"/hop{http_count(r)}"));
        var limits = new WebFetchLimits { MaxRedirects = 3 };
        var result = await _h.Run(Tool(http, limits), Args("https://example.com/hop0"));
        Assert.True(result.IsError);
        Assert.Contains("HTTP 302", ToolHarness.TextOf(result));
        Assert.Equal(4, http.Requests.Count); // 第一次 + 3 跳

        static int http_count(HttpRequestMessage r) => int.Parse(r.RequestUri!.AbsolutePath.AsSpan(4)) + 1;
    }

    [Fact]
    public async Task A_3xx_without_a_location_is_handled_as_the_current_response()
    {
        var http = new FakeHttp(_ => FakeHttp.Ok("not modified", "text/plain", HttpStatusCode.MultipleChoices));
        var result = await _h.Run(Tool(http), Args("https://example.com/"));
        Assert.True(result.IsError);
        Assert.Contains("HTTP 300", ToolHarness.TextOf(result));
    }

    // ───────────────────────── content-type ─────────────────────────

    [Theory]
    [InlineData("text/html; charset=utf-8", "<p>x</p>")]
    [InlineData("text/plain", "x")]
    [InlineData("application/json", """{"x":1}""")]
    [InlineData("application/xml", "<a>x</a>")]
    [InlineData("application/vnd.api+json", """{"x":1}""")]
    public async Task Textual_types_are_accepted(string type, string body)
    {
        var result = await _h.Run(Tool(Page(body, type)), Args("https://example.com/"));
        Assert.False(result.IsError);
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/png")]
    [InlineData("application/zip")]
    [InlineData("application/octet-stream")]
    [InlineData("video/mp4")]
    public async Task Binary_types_are_refused_with_advice(string type)
    {
        var result = await _h.Run(Tool(Page("x", type)), Args("https://example.com/f"));
        Assert.True(result.IsError);
        Assert.Contains(type, ToolHarness.TextOf(result));
        Assert.Contains("Read", ToolHarness.TextOf(result));
    }

    [Fact]
    public async Task A_missing_content_type_is_treated_as_unknown_and_refused()
    {
        var http = new FakeHttp(_ => FakeHttp.Bytes("hello"u8.ToArray(), null));
        var result = await _h.Run(Tool(http), Args("https://example.com/"));
        Assert.True(result.IsError);
        Assert.Contains("an unknown type", ToolHarness.TextOf(result));
    }

    // ───────────────────────── 正文 ─────────────────────────

    [Fact]
    public async Task The_prompt_is_echoed_above_the_text()
    {
        var result = await _h.Run(Tool(Page("<p>body text</p>")), Args("https://example.com/", "find the price"));
        var text = ToolHarness.TextOf(result);
        Assert.StartsWith("You are looking for: find the price", text);
        Assert.True(text.IndexOf("find the price", StringComparison.Ordinal) < text.IndexOf("body text", StringComparison.Ordinal));
        Assert.Contains("Content of https://example.com/", text);
    }

    [Fact]
    public async Task Html_becomes_text_with_tags_stripped_and_entities_restored()
    {
        var html = "<html><head><style>p{}</style></head><body><h1>Title &amp; more</h1><script>evil()</script><p>First</p><p>Second &lt;b&gt;</p><ul><li>a</li><li>b</li></ul></body></html>";
        var text = ToolHarness.TextOf(await _h.Run(Tool(Page(html)), Args("https://example.com/")));
        Assert.Contains("Title & more", text);
        Assert.Contains("Second <b>", text);
        Assert.Contains("- a\n- b", text);
        Assert.DoesNotContain("evil()", text);
        Assert.DoesNotContain("p{}", text);
    }

    [Fact]
    public async Task Json_is_not_washed_as_html()
    {
        var text = ToolHarness.TextOf(await _h.Run(Tool(Page("""{"a":"<b>x</b>","n":1}""", "application/json")), Args("https://example.com/api")));
        Assert.Contains("""{"a":"<b>x</b>","n":1}""", text);
    }

    [Fact]
    public async Task Text_longer_than_the_limit_is_truncated_and_says_so()
    {
        var limits = new WebFetchLimits { MaxTextChars = 1000 };
        var result = await _h.Run(Tool(Page(new string('x', 5000), "text/plain"), limits), Args("https://example.com/"));
        var text = ToolHarness.TextOf(result);
        Assert.Contains("text was too long and has been truncated", text);
        Assert.True(text.Length < 1500);
        Assert.True(result.Truncated);
        Assert.True(result.Details!.Value.GetProperty("truncatedText").GetBoolean());
    }

    [Fact]
    public async Task An_empty_page_gives_a_hint_instead_of_blank_space()
    {
        var result = await _h.Run(Tool(Page("<html><body><script>render()</script></body></html>")), Args("https://example.com/spa"));
        Assert.False(result.IsError);
        Assert.Contains("no readable text", ToolHarness.TextOf(result));
        Assert.Contains("JavaScript", ToolHarness.TextOf(result));
    }

    [Fact]
    public async Task Control_characters_in_the_fetched_content_are_scrubbed()
    {
        var text = ToolHarness.TextOf(await _h.Run(Tool(Page("clean\u0007\u001b[31m‮text​", "text/plain")), Args("https://example.com/")));
        Assert.Contains("clean[31mtext", text);
        Assert.DoesNotContain('\u0007', text);
        Assert.DoesNotContain('‮', text);
        Assert.DoesNotContain('​', text);
    }

    [Fact]
    public async Task The_charset_in_the_header_is_honoured_for_gbk_pages()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var gbk = System.Text.Encoding.GetEncoding("gbk").GetBytes("<p>你好,世界</p>");
        var http = new FakeHttp(_ => FakeHttp.Bytes(gbk, "text/html; charset=gbk"));
        var text = ToolHarness.TextOf(await _h.Run(Tool(http), Args("https://example.com/cn")));
        Assert.Contains("你好,世界", text);
    }

    [Fact]
    public async Task An_unknown_charset_falls_back_to_utf8_instead_of_failing()
    {
        var http = new FakeHttp(_ => FakeHttp.Bytes("<p>ok é</p>"u8.ToArray(), "text/html; charset=x-no-such-charset"));
        var text = ToolHarness.TextOf(await _h.Run(Tool(http), Args("https://example.com/")));
        Assert.Contains("ok é", text);
    }

    // ───────────────────────── 大小与时间 ─────────────────────────

    [Fact]
    public async Task The_byte_limit_stops_reading_instead_of_downloading_everything()
    {
        long produced = 0;
        var http = new FakeHttp(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream(n => produced += n)) };
            response.Content.Headers.ContentType = new("text/plain");
            return response;
        });
        var limits = new WebFetchLimits { MaxBytes = 64 * 1024 };
        var result = await _h.Run(Tool(http, limits), Args("https://example.com/huge"));
        Assert.False(result.IsError);
        Assert.Contains("only the start was read", ToolHarness.TextOf(result));
        Assert.True(result.Details!.Value.GetProperty("truncatedBytes").GetBoolean());
        Assert.True(produced <= limits.MaxBytes + 64 * 1024, $"produced {produced} bytes"); // 读到上限就停(允许多读一个缓冲块)
        Assert.Equal(limits.MaxBytes, result.Details!.Value.GetProperty("bytes").GetInt32());
    }

    [Fact]
    public async Task A_response_of_exactly_the_limit_is_not_flagged_truncated()
    {
        var limits = new WebFetchLimits { MaxBytes = 1000 };
        var result = await _h.Run(Tool(Page(new string('a', 1000), "text/plain"), limits), Args("https://example.com/"));
        Assert.DoesNotContain("only the start was read", ToolHarness.TextOf(result));
        Assert.False(result.Details!.Value.GetProperty("truncatedBytes").GetBoolean());
    }

    [Fact]
    public async Task A_server_that_never_answers_times_out_with_a_clear_message()
    {
        var http = new FakeHttp(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return FakeHttp.Ok("never");
        });
        var limits = new WebFetchLimits { Timeout = TimeSpan.FromMilliseconds(150) };
        var result = await _h.Run(Tool(http, limits), Args("https://example.com/slow"));
        Assert.True(result.IsError);
        Assert.Contains("timed out", ToolHarness.TextOf(result));
    }

    [Fact]
    public async Task A_body_that_stalls_after_the_headers_also_times_out()
    {
        var http = new FakeHttp(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) };
            response.Content.Headers.ContentType = new("text/plain");
            return response;
        });
        var limits = new WebFetchLimits { Timeout = TimeSpan.FromMilliseconds(150) };
        var result = await _h.Run(Tool(http, limits), Args("https://example.com/stall"));
        Assert.True(result.IsError);
        Assert.Contains("timed out", ToolHarness.TextOf(result));
    }

    [Fact]
    public async Task The_user_stopping_the_run_rethrows_the_cancellation_instead_of_a_plain_failure()
    {
        using var cts = new CancellationTokenSource();
        var http = new FakeHttp(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeHttp.Ok("never");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Run(Tool(http), Args("https://example.com/"), cts.Token));
    }

    // ───────────────────────── HTTP 错误 ─────────────────────────

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task Login_walls_say_retrying_will_not_help(int status)
    {
        var http = new FakeHttp(_ => FakeHttp.Ok("denied", "text/html", (HttpStatusCode)status));
        var text = ToolHarness.TextOf(await _h.Run(Tool(http), Args("https://example.com/private")));
        Assert.Contains($"HTTP {status}", text);
        Assert.Contains("Retrying will not help", text);
    }

    [Fact]
    public async Task Not_found_suggests_another_source_and_server_errors_carry_the_status()
    {
        var notFound = ToolHarness.TextOf(await _h.Run(Tool(new FakeHttp(_ => FakeHttp.Ok("", "text/html", HttpStatusCode.NotFound))), Args("https://example.com/missing")));
        Assert.Contains("HTTP 404", notFound);
        Assert.Contains("find another source", notFound);

        var failing = await _h.Run(Tool(new FakeHttp(_ => FakeHttp.Ok("", "text/html", HttpStatusCode.InternalServerError))), Args("https://example.com/boom"));
        Assert.True(failing.IsError);
        Assert.Contains("HTTP 500", ToolHarness.TextOf(failing));
        Assert.Equal(500, failing.Details!.Value.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_network_error_gets_a_human_message_and_says_retrying_the_same_url_will_not_help()
    {
        var http = new FakeHttp((_, _) => throw new HttpRequestException("name resolution failed"));
        var result = await _h.Run(Tool(http), Args("https://example.com/"));
        Assert.True(result.IsError);
        var text = ToolHarness.TextOf(result);
        Assert.Contains("name resolution failed", text);
        Assert.Contains("retrying the same URL will not help", text);
    }

    // ───────────────────────── Details / 请求头 ─────────────────────────

    [Fact]
    public async Task Details_carry_the_environment_the_urls_and_never_the_body()
    {
        var result = await _h.Run(Tool(Page("<p>SECRET-BODY</p>")), Args("http://example.com/p"));
        var details = result.Details!.Value;
        Assert.Equal("webFetch", details.GetProperty("kind").GetString());
        Assert.Equal("dev-1", details.GetProperty("environment").GetProperty("deviceId").GetString());
        Assert.Equal("local", details.GetProperty("environment").GetProperty("environment").GetString());
        Assert.Equal("https://example.com/p", details.GetProperty("finalUrl").GetString());
        Assert.Equal(200, details.GetProperty("status").GetInt32());
        Assert.DoesNotContain("SECRET-BODY", details.GetRawText());
    }

    [Fact]
    public async Task Errors_also_carry_the_environment_tag()
    {
        var result = await _h.Run(Tool(Page()), Args("http://127.0.0.1/"));
        Assert.True(result.IsError);
        Assert.Equal("dev-1", result.Details!.Value.GetProperty("environment").GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task The_request_is_a_get_with_an_accept_and_a_user_agent_and_no_credentials()
    {
        var http = Page();
        await _h.Run(Tool(http), Args("https://example.com/"));
        var (method, _, _, headers) = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Get, method);
        Assert.Contains("text/html", headers["accept"]);
        Assert.Contains("NextCoWork", headers["user-agent"]);
        Assert.DoesNotContain("authorization", headers.Keys);
        Assert.DoesNotContain("cookie", headers.Keys);
    }

    [Theory]
    [InlineData("""{"url":"","prompt":"x"}""")]
    [InlineData("""{"url":"https://example.com"}""")]
    [InlineData("""{"prompt":"x"}""")]
    [InlineData("{}")]
    public async Task Missing_or_empty_arguments_are_stopped_by_the_schema_before_any_request(string json)
    {
        var http = Page();
        var result = await _h.Run(Tool(http), json);
        Assert.True(result.IsError);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task A_tool_context_without_an_execution_environment_is_an_error_not_a_crash()
    {
        var tool = Tool(Page());
        var bare = new ToolContext(new RunId("r"), "c", "s", 0, null, new NoProgress(), new FileMutationQueue());
        var input = JsonDocument.Parse(Args("https://example.com/")).RootElement.Clone();
        var result = await tool.ExecuteAsync(new ToolInvocation("c", "WebFetch", tool, input), bare, default);
        Assert.True(result.IsError);
    }

    private sealed class NoProgress : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value)
        {
        }
    }

    // ───────────────────────── 流 ─────────────────────────

    private sealed class EndlessStream(Action<int> produced) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Fill(buffer.Span));

        private int Fill(Span<byte> span)
        {
            span.Fill((byte)'a');
            produced(span.Length);
            return span.Length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
