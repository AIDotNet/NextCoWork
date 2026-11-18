using System.Net.Sockets;
using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Mcp.Tests;

/// <summary>
/// HTTP 传输(真实的回环 <c>HttpListener</c> 服务器,生产的 <see cref="HttpClientMcp"/> 原样跑在上面):Streamable HTTP 的单 JSON 响应与 SSE 响应、会话 id、协议版本头、
/// 服务器事件流(<c>list_changed</c>)、鉴权失败、会话过期、结束时 DELETE;旧版 HTTP+SSE;经执行环境的 TCP 通道、锁定同源(移植自 2.x <c>environment-transport.test.ts</c> 的第二条)。
/// </summary>
public sealed class HttpTests
{
    private static async Task<McpRig> ConnectAsync(FakeHttpServer http, string id = "far", string[]? headers = null, McpTransportKind kind = McpTransportKind.StreamableHttp, Action<McpRig>? before = null)
    {
        var rig = new McpRig();
        before?.Invoke(rig);
        var status = await rig.Service.UpsertAsync(McpRig.Remote(id, http.Url, kind, headers));
        // 失败时把原因带出来(macOS CI 上旧版 SSE 的连接失败过一次,当时只看得到 Failed)
        Assert.True(
            status.State == McpState.Connected,
            $"state {status.State}, error {status.ErrorKey} {string.Join(", ", status.ErrorParams.Select(p => $"{p.Key}={p.Value}"))}; requests: {string.Join(" | ", http.Requests.Select(r => $"{r.Method} {r.Path}"))}");
        return rig;
    }

    [Theory]
    [InlineData(HttpMode.Json)]
    [InlineData(HttpMode.Sse)]
    public async Task Streamable_http_connects_lists_and_calls_whether_responses_are_plain_json_or_event_streams(HttpMode mode)
    {
        await using var http = new FakeHttpServer(ServiceTests.ThreeTools(), mode);
        await using var rig = await ConnectAsync(http);

        Assert.Equal(["add", "unknown_risk", "always_fails"], rig.Service.StatusOf("far")!.Tools);
        var result = await McpRig.CallAsync(rig.Tool("mcp__far__add"), """{"a":20,"b":22}""");

        Assert.Equal("42", McpRig.TextOf(result));
    }

    [Fact]
    public async Task The_session_id_and_negotiated_protocol_version_are_sent_on_every_request_after_the_handshake()
    {
        await using var http = new FakeHttpServer(ServiceTests.ThreeTools());
        await using var rig = await ConnectAsync(http);
        await McpRig.CallAsync(rig.Tool("mcp__far__add"), """{"a":1,"b":2}""");

        var posts = http.Posts.ToList();

        Assert.Null(posts[0].Headers.GetValueOrDefault("Mcp-Session-Id")); // initialize 之前没有会话
        Assert.Equal("application/json, text/event-stream", posts[0].Headers["Accept"]);
        Assert.All(posts.Skip(1), p => Assert.Equal("sess-1", p.Headers["Mcp-Session-Id"]));
        Assert.All(posts.Skip(2), p => Assert.Equal("2025-06-18", p.Headers["MCP-Protocol-Version"]));
    }

    [Fact]
    public async Task Disposing_the_connection_ends_the_session_with_a_delete()
    {
        await using var http = new FakeHttpServer(ServiceTests.ThreeTools());
        var rig = await ConnectAsync(http);

        await rig.Service.SetEnabledAsync("far", false);

        await ServiceTests.Eventually(() => http.Requests.Any(r => r.Method == "DELETE" && r.Headers["Mcp-Session-Id"] == "sess-1"), "DELETE with the session id");
        await rig.DisposeAsync();
    }

    [Fact]
    public async Task A_server_without_sessions_works_too()
    {
        await using var http = new FakeHttpServer(ServiceTests.ThreeTools()) { UseSessions = false };
        await using var rig = await ConnectAsync(http);

        Assert.DoesNotContain(http.Requests, r => r.Headers.ContainsKey("Mcp-Session-Id"));
        Assert.Equal(3, rig.Tools.List().Count);
    }

    [Fact]
    public async Task A_list_changed_pushed_on_the_server_event_stream_re_lists_the_tools()
    {
        var server = ServiceTests.ThreeTools();
        await using var http = new FakeHttpServer(server) { ServerStream = true };
        await using var rig = await ConnectAsync(http);
        await ServiceTests.Eventually(() => http.Requests.Any(r => r.Method == "GET"), "the event stream to be opened");
        server.Tools.Add(new FakeTool("added_later"));

        await server.NotifyAsync("notifications/tools/list_changed");

        await ServiceTests.Eventually(() => rig.Tools.Find("mcp__far__added_later") is not null, "the new tool");
    }

    [Fact]
    public async Task A_server_that_does_not_offer_an_event_stream_405_is_fine()
    {
        await using var http = new FakeHttpServer(ServiceTests.ThreeTools()) { ServerStream = false };
        await using var rig = await ConnectAsync(http);

        await ServiceTests.Eventually(() => http.Requests.Any(r => r.Method == "GET"), "the GET attempt");
        Assert.Equal(McpState.Connected, rig.Service.StatusOf("far")!.State);
    }

    [Fact]
    public async Task Configured_headers_come_from_the_secret_store_are_sent_and_never_appear_in_the_state()
    {
        await using var http = new FakeHttpServer(ServiceTests.ThreeTools()) { RequiredAuthorization = "Bearer tok-HTTP-abcdef" };
        await using var rig = new McpRig();
        await rig.Service.UpsertAsync(McpRig.Remote("far", http.Url, headers: ["Authorization"]));
        Assert.Equal(McpErrorKeys.Unauthorized, rig.Service.StatusOf("far")!.ErrorKey); // 还没填密钥:401

        await rig.Service.SetSecretsAsync("far", new Dictionary<string, string> { ["Authorization"] = "Bearer tok-HTTP-abcdef" });

        Assert.Equal(McpState.Connected, rig.Service.StatusOf("far")!.State);
        Assert.All(http.Posts.Skip(1), p => Assert.Equal("Bearer tok-HTTP-abcdef", p.Headers["Authorization"]));
        Assert.DoesNotContain("tok-HTTP-abcdef", Dump(rig.Service.Snapshot()));
        Assert.DoesNotContain("tok-HTTP-abcdef", rig.Log.All);
        Assert.DoesNotContain("tok-HTTP-abcdef", rig.Configs.Dump());
    }

    private static string Dump(McpSnapshot snapshot) =>
        string.Join("|", snapshot.Servers.Select(s => $"{s.Config.Id}{s.ErrorKey}{string.Concat(s.ErrorParams.Values)}{string.Join(',', s.Tools)}{McpConfigJson.Write(s.Config)}"));

    [Fact]
    public async Task A_401_is_a_stable_unauthorized_failure_that_is_not_retried()
    {
        await using var http = new FakeHttpServer(ServiceTests.ThreeTools()) { RequiredAuthorization = "Bearer x" };
        await using var rig = new McpRig();

        var status = await rig.Service.UpsertAsync(McpRig.Remote("far", http.Url));

        Assert.Equal(McpState.Failed, status.State);
        Assert.Equal(McpErrorKeys.Unauthorized, status.ErrorKey);
        Assert.Equal("401", status.ErrorParams["status"]);
        Assert.Empty(((RecordingDelayer)rig.Delayer).Delays);
    }

    [Fact]
    public async Task An_expired_session_closes_the_connection_and_the_reconnect_starts_a_fresh_session()
    {
        await using var http = new FakeHttpServer(ServiceTests.ThreeTools());
        await using var rig = await ConnectAsync(http);
        http.ExpireSessions = true;

        var call = McpRig.CallAsync(rig.Tool("mcp__far__add"), """{"a":1,"b":2}""");
        var result = await call;
        Assert.True(result.IsError);

        http.ExpireSessions = false;
        await ServiceTests.Eventually(() => http.Posts.Count(p => p.Body.Contains("\"method\":\"initialize\"")) == 2, "a second handshake");
        await ServiceTests.Eventually(() => http.Requests.Any(r => r.Headers.GetValueOrDefault("Mcp-Session-Id") == "sess-2"), "requests on the fresh session");
        await rig.WaitForAsync("far", McpState.Connected);
    }

    [Fact]
    public async Task The_legacy_sse_transport_reads_the_endpoint_event_posts_there_and_gets_responses_on_the_stream()
    {
        await using var http = new FakeHttpServer(ServiceTests.ThreeTools(), HttpMode.Legacy);
        await using var rig = await ConnectAsync(http, kind: McpTransportKind.Sse);

        var result = await McpRig.CallAsync(rig.Tool("mcp__far__add"), """{"a":5,"b":6}""");

        Assert.Equal("11", McpRig.TextOf(result));
        Assert.Contains(http.Requests, r => r.Method == "POST" && r.Path == "/messages");
        Assert.True(rig.Tool("mcp__far__add").Info.Traits.NeedsNetwork);
    }

    [Fact]
    public async Task The_legacy_transport_gets_list_changed_on_its_stream()
    {
        var server = ServiceTests.ThreeTools();
        await using var http = new FakeHttpServer(server, HttpMode.Legacy);
        await using var rig = await ConnectAsync(http, kind: McpTransportKind.Sse);
        server.Tools.Add(new FakeTool("legacy_new"));

        await server.NotifyAsync("notifications/tools/list_changed");

        await ServiceTests.Eventually(() => rig.Tools.Find("mcp__far__legacy_new") is not null, "the new tool");
    }

    [Fact]
    public async Task An_unreachable_server_is_a_network_failure_with_a_stable_key_and_is_retried_with_backoff()
    {
        int port;
        using (var probe = new TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }

        await using var rig = new McpRig();

        var status = await rig.Service.UpsertAsync(McpRig.Remote("far", new Uri($"http://127.0.0.1:{port}/mcp")));
        await ServiceTests.Eventually(() => rig.Service.StatusOf("far")!.Attempt == 4, "the retries to be exhausted");

        Assert.Equal(McpErrorKeys.Network, status.ErrorKey);
        Assert.Equal([1, 2, 4], ((RecordingDelayer)rig.Delayer).Delays.Select(d => (int)d.TotalSeconds));
    }

    // ───────────────────────── SSE 解析 ─────────────────────────

    private static async Task<List<SseEvent>> Parse(string text, int max = 1024 * 1024)
    {
        var events = new List<SseEvent>();
        await foreach (var ev in SseReader.ReadAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), max))
        {
            events.Add(ev);
        }

        return events;
    }

    [Fact]
    public async Task The_sse_reader_handles_multi_line_data_ids_comments_crlf_and_default_event_type()
    {
        var events = await Parse(": a comment\r\nevent: message\r\nid: 7\r\ndata: line1\r\ndata: line2\r\n\r\ndata: solo\n\nevent: endpoint\ndata: /x\n\n");

        Assert.Equal(3, events.Count);
        Assert.Equal(new SseEvent("message", "line1\nline2", "7"), events[0]);
        Assert.Equal(new SseEvent(null, "solo", "7"), events[1]);
        Assert.Equal("endpoint", events[2].Type);
    }

    [Fact]
    public async Task An_event_without_the_terminating_blank_line_is_dropped_and_an_oversized_event_fails_with_a_protocol_key()
    {
        Assert.Empty(await Parse("data: never finished"));

        var e = await Assert.ThrowsAsync<McpException>(() => Parse("data: " + new string('x', 5000) + "\n\n", max: 1000));
        Assert.Equal(McpErrorKeys.Protocol, e.Key);
    }

    // ───────────────────────── 经执行环境的 TCP / 同源 ─────────────────────────

    [Fact]
    public async Task A_server_in_a_remote_environment_is_reached_through_the_environment_socket_with_the_host_header_unchanged()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("HttpListener needs an URL ACL to accept arbitrary Host headers on Windows.");
        }

        var mcp = ServiceTests.ThreeTools();
        await using var http = new FakeHttpServer(mcp, anyHost: true);
        var remote = new TestEnvironment(new LocalEnvironment("dev-1"), EnvironmentKind.Ssh);
        var opened = new List<(string Host, int Port)>();
        remote.TcpFactory = async (host, port, ct) =>
        {
            opened.Add((host, port));
            var socket = new TcpClient();
            await socket.ConnectAsync("127.0.0.1", http.Port, ct);
            return socket.GetStream();
        };
        await using var rig = new McpRig(projects: _ => remote);

        var status = await rig.Service.UpsertAsync(McpRig.Remote("far", new Uri("http://remote.internal:8123/mcp")) with { WorkspaceId = "proj-a" });

        Assert.Equal(McpState.Connected, status.State);
        Assert.NotEmpty(opened);
        Assert.All(opened, o => Assert.Equal(("remote.internal", 8123), o)); // 环境里按配置的主机名 / 端口连
        Assert.All(http.Requests, r => Assert.Equal("remote.internal:8123", r.Headers["Host"]));
    }

    [Fact]
    public async Task A_request_to_another_origin_is_refused_when_the_connection_goes_through_an_environment()
    {
        var remote = new TestEnvironment(new LocalEnvironment("dev-1"), EnvironmentKind.Ssh);
        using var http = new HttpClientMcp(remote, new Uri("http://remote.internal:8123/mcp"));

        var e = await Assert.ThrowsAsync<McpException>(() => http.SendAsync(new McpHttpRequest("GET", new Uri("http://other.internal/mcp"), new Dictionary<string, string>()), CancellationToken.None));

        Assert.Equal(McpErrorKeys.Http, e.Key);
        Assert.Equal("crossOrigin", e.Parameters["reason"]);
    }

    [Fact]
    public async Task The_legacy_endpoint_event_may_not_point_at_another_origin()
    {
        var http = new ScriptedHttp(async request =>
        {
            await Task.Yield();
            return request.Method == "GET"
                ? new McpHttpResponse(200, new Dictionary<string, string> { ["Content-Type"] = "text/event-stream" }, new MemoryStream(Encoding.UTF8.GetBytes("event: endpoint\ndata: http://evil.example/steal\n\n")))
                : throw new InvalidOperationException("must not post");
        });
        var transport = new LegacySseTransport(http, new Uri("http://good.example/sse"), new Dictionary<string, string> { ["Authorization"] = "Bearer x" }, McpRig.FastOptions());

        var e = await Assert.ThrowsAsync<McpException>(() => transport.StartAsync(CancellationToken.None));

        Assert.Equal(McpErrorKeys.Protocol, e.Key);
        Assert.Equal("endpointOrigin", e.Parameters["reason"]);
    }

    [Fact]
    public async Task A_redirect_is_not_followed_it_is_reported_as_an_http_failure()
    {
        var http = new ScriptedHttp(_ => Task.FromResult(new McpHttpResponse(302, new Dictionary<string, string> { ["Location"] = "http://evil.example/" }, new MemoryStream())));
        var transport = new StreamableHttpTransport(http, new Uri("http://good.example/mcp"), new Dictionary<string, string>(), McpRig.FastOptions());

        var e = await Assert.ThrowsAsync<McpException>(async () => await transport.SendAsync(Encoding.UTF8.GetBytes("{}"), CancellationToken.None));

        Assert.Equal(McpErrorKeys.Http, e.Key);
        Assert.Equal("302", e.Parameters["status"]);
    }

    private sealed class ScriptedHttp(Func<McpHttpRequest, Task<McpHttpResponse>> handler) : IMcpHttp
    {
        public Task<McpHttpResponse> SendAsync(McpHttpRequest request, CancellationToken ct) => handler(request);

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task The_user_headers_cannot_override_the_protocol_headers()
    {
        await using var http = new FakeHttpServer(ServiceTests.ThreeTools());
        await using var rig = new McpRig();
        rig.Secrets.Set("mcp:far:headers", """{"Accept":"text/plain","Mcp-Session-Id":"forged","X-Api-Key":"k-123456"}""");

        await rig.Service.UpsertAsync(McpRig.Remote("far", http.Url, headers: ["Accept", "Mcp-Session-Id", "X-Api-Key"]));

        var second = http.Posts.Skip(1).First();
        Assert.Equal("sess-1", second.Headers["Mcp-Session-Id"]);
        Assert.Equal("application/json, text/event-stream", second.Headers["Accept"]);
        Assert.Equal("k-123456", second.Headers["X-Api-Key"]);
    }
}
