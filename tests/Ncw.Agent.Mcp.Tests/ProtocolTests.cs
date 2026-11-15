using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Mcp.Tests;

/// <summary>
/// 协议层(JSON-RPC 会话 + 握手 + tools / resources / prompts),跑在 stdio 传输 + 内存管道的假进程上:
/// 握手与版本协商、分页、乱序响应、服务器夹带通知、请求取消、超时、服务器的 JSON-RPC 错误、坏行、传输关闭时未决请求一起失败。
/// </summary>
public sealed class ProtocolTests
{
    private static McpOptions Options(Action<McpOptionsBuilder>? tweak = null)
    {
        var b = new McpOptionsBuilder();
        tweak?.Invoke(b);
        return new McpOptions
        {
            ConnectTimeout = b.Connect,
            ListToolsTimeout = b.List,
            CallTimeout = b.Call,
            MaxToolsPerServer = 500,
            MaxListPages = b.Pages,
            MaxMessageBytes = b.MaxMessage,
            StderrTailBytes = 64,
            GracefulExit = TimeSpan.FromMilliseconds(50),
        };
    }

    private sealed class McpOptionsBuilder
    {
        public TimeSpan Connect { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan List { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan Call { get; set; } = TimeSpan.FromSeconds(5);

        public int Pages { get; set; } = 50;

        public int MaxMessage { get; set; } = 1024 * 1024;
    }

    private static async Task<(McpConnection Connection, FakeProcess Process, TestEnvironment Env)> OpenAsync(FakeMcpServer server, McpOptions? options = null)
    {
        var env = new TestEnvironment(new LocalEnvironment("dev-1"));
        env.ProcessFactory = _ => new FakeProcess(env.Id, server);
        options ??= Options();
        var transport = new StdioTransport(env, CommandSpec.Exec("fake-mcp"), options);
        var connection = await McpConnection.OpenAsync(transport, options, "3.0-test", null, CancellationToken.None);
        return (connection, env.Processes[0], env);
    }

    private static FakeMcpServer Server(params FakeTool[] tools)
    {
        var server = new FakeMcpServer();
        server.Tools.AddRange(tools);
        return server;
    }

    private static JsonElement Args(string json = "{}") => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task The_handshake_sends_initialize_then_initialized_and_reports_what_the_server_said()
    {
        var server = Server();
        server.ServerName = "srv";
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;

        Assert.Equal("srv", connection.Hello.Name);
        Assert.Equal("1.2.3", connection.Hello.Version);
        Assert.Equal("2025-06-18", connection.Hello.ProtocolVersion);
        Assert.True(connection.Hello.Tools);
        await ServiceTests.Eventually(() => server.Methods.Contains("notifications/initialized"), "initialized notification");
        Assert.Equal("initialize", server.Methods.First());
        using var init = JsonDocument.Parse(server.InitializeParams!);
        Assert.Equal("2025-06-18", init.RootElement.GetProperty("protocolVersion").GetString());
        Assert.Equal("NextCoWork", init.RootElement.GetProperty("clientInfo").GetProperty("name").GetString());
        // 没有声明任何客户端能力(roots / sampling / elicitation):服务器不该来问我们
        Assert.Empty(init.RootElement.GetProperty("capabilities").EnumerateObject());
    }

    [Theory]
    [InlineData("2025-03-26")]
    [InlineData("2024-11-05")]
    public async Task An_older_protocol_version_the_server_answers_with_is_accepted(string version)
    {
        var server = Server();
        server.ProtocolVersion = version;
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;
        Assert.Equal(version, connection.Hello.ProtocolVersion);
    }

    [Fact]
    public async Task A_protocol_version_we_do_not_speak_fails_the_handshake_with_a_stable_key_and_cleans_up()
    {
        var server = Server();
        server.ProtocolVersion = "2099-01-01";
        var env = new TestEnvironment(new LocalEnvironment("dev-1"));
        env.ProcessFactory = _ => new FakeProcess(env.Id, server);
        var options = Options();

        var e = await Assert.ThrowsAsync<McpException>(() => McpConnection.OpenAsync(new StdioTransport(env, CommandSpec.Exec("x"), options), options, "t", null, CancellationToken.None));

        Assert.Equal(McpErrorKeys.ProtocolVersion, e.Key);
        Assert.Equal("2099-01-01", e.Parameters["version"]);
        await ServiceTests.Eventually(() => env.Processes[0].Killed || env.Processes[0].Exited.IsCompleted, "the process to be released");
    }

    [Fact]
    public async Task Tools_are_listed_across_pages_until_there_is_no_cursor()
    {
        var server = Server([.. Enumerable.Range(1, 7).Select(i => new FakeTool($"t{i}"))]);
        server.PageSize = 3;
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;

        var tools = await connection.ListToolsAsync(CancellationToken.None);

        Assert.Equal(7, tools.Count);
        Assert.Equal(3, server.ListCalls);
        Assert.Equal("t7", tools[^1].Name);
    }

    [Fact]
    public async Task A_server_that_paginates_forever_is_cut_off_at_the_page_limit()
    {
        var server = Server([.. Enumerable.Range(1, 40).Select(i => new FakeTool($"t{i}"))]);
        server.PageSize = 1;
        var (connection, _, _) = await OpenAsync(server, Options(b => b.Pages = 5));
        await using var _ = connection;

        var tools = await connection.ListToolsAsync(CancellationToken.None);

        Assert.Equal(5, tools.Count);
    }

    [Fact]
    public async Task A_server_without_the_tools_capability_lists_no_tools_instead_of_failing()
    {
        var server = Server(new FakeTool("hidden"));
        server.ToolsCapability = false;
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;

        Assert.False(connection.Hello.Tools);
        Assert.Empty(await connection.ListToolsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Responses_may_arrive_out_of_order_and_each_caller_gets_its_own()
    {
        var server = Server(
            new FakeTool("slow", async (_, ct) =>
            {
                await Task.Delay(300, ct);
                return FakeTool.Text("slow-result");
            }),
            new FakeTool("fast"));
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;

        var slow = connection.CallToolAsync("slow", Args(), TimeSpan.FromSeconds(5), CancellationToken.None);
        var fast = connection.CallToolAsync("fast", Args(), TimeSpan.FromSeconds(5), CancellationToken.None);

        var fastResult = await fast;
        Assert.False(slow.IsCompleted); // 后发的先回来
        Assert.Equal("ok", fastResult.Content[0].Text);
        Assert.Equal("slow-result", (await slow).Content[0].Text);
    }

    [Fact]
    public async Task Notifications_between_responses_are_dispatched_and_list_changed_is_surfaced()
    {
        var server = Server();
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;
        var changed = 0;
        connection.ToolsListChanged += () => Interlocked.Increment(ref changed);

        await server.NotifyAsync("notifications/message", new JsonObject { ["level"] = "info", ["data"] = "x" });
        await server.NotifyAsync("notifications/tools/list_changed");

        await ServiceTests.Eventually(() => Volatile.Read(ref changed) == 1, "list_changed");
    }

    [Fact]
    public async Task A_timed_out_call_sends_cancelled_to_the_server_and_throws_a_stable_timeout()
    {
        var server = Server(new FakeTool("hang", (_, _) => Task.FromResult<JsonNode?>(null)));
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;

        var e = await Assert.ThrowsAsync<McpException>(() => connection.CallToolAsync("hang", Args(), TimeSpan.FromMilliseconds(150), CancellationToken.None));

        Assert.Equal(McpErrorKeys.Timeout, e.Key);
        await ServiceTests.Eventually(() => !server.Cancelled.IsEmpty, "notifications/cancelled");
    }

    [Fact]
    public async Task A_caller_cancel_sends_cancelled_and_surfaces_as_an_operation_cancelled()
    {
        var server = Server(new FakeTool("hang", (_, _) => Task.FromResult<JsonNode?>(null)));
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;
        using var stop = new CancellationTokenSource();

        var call = connection.CallToolAsync("hang", Args(), TimeSpan.FromSeconds(30), stop.Token);
        await Task.Delay(50);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await ServiceTests.Eventually(() => !server.Cancelled.IsEmpty, "notifications/cancelled");
    }

    [Fact]
    public async Task A_json_rpc_error_from_the_server_surfaces_with_its_code()
    {
        var server = Server();
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;

        var e = await Assert.ThrowsAsync<McpRpcException>(() => connection.CallToolAsync("no-such-tool", Args(), TimeSpan.FromSeconds(5), CancellationToken.None));

        Assert.Equal(-32602, e.Code);
    }

    [Fact]
    public async Task Server_requests_are_answered_ping_with_an_empty_result_and_anything_else_with_method_not_found()
    {
        var server = Server();
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;

        await server.SendRequestAsync(900, "ping");
        await server.SendRequestAsync(901, "roots/list");

        await ServiceTests.Eventually(() => server.ClientResponses.Count == 2, "answers to the two server requests");
        var answers = server.ClientResponses.Select(l => JsonNode.Parse(l)!.AsObject()).ToDictionary(o => o["id"]!.GetValue<long>());
        Assert.NotNull(answers[900]["result"]);
        Assert.Equal(-32601, answers[901]["error"]!["code"]!.GetValue<long>());
    }

    [Fact]
    public async Task Garbage_lines_and_unmatched_responses_are_ignored_without_hurting_the_connection()
    {
        var server = Server(new FakeTool("fast"));
        var (connection, process, _) = await OpenAsync(server);
        await using var _ = connection;

        await process.WriteRawAsync("this is not json\n");
        await process.WriteRawAsync("{\"jsonrpc\":\"2.0\",\"id\":9999,\"result\":{}}\n");
        await process.WriteRawAsync("[1,2,3]\n");

        Assert.Equal("ok", (await connection.CallToolAsync("fast", Args(), TimeSpan.FromSeconds(5), CancellationToken.None)).Content[0].Text);
    }

    [Fact]
    public async Task When_the_transport_closes_every_pending_request_fails_at_once()
    {
        var server = Server(new FakeTool("hang", (_, _) => Task.FromResult<JsonNode?>(null)));
        var (connection, process, _) = await OpenAsync(server);
        await using var _ = connection;

        var a = connection.CallToolAsync("hang", Args(), TimeSpan.FromSeconds(30), CancellationToken.None);
        var b = connection.CallToolAsync("hang", Args(), TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(50);
        process.ExitNow(3);

        var ea = await Assert.ThrowsAsync<McpException>(() => a);
        var eb = await Assert.ThrowsAsync<McpException>(() => b);
        Assert.Equal(McpErrorKeys.ProcessExited, ea.Key);
        Assert.Equal("3", ea.Parameters["exitCode"]);
        Assert.Equal(McpErrorKeys.ProcessExited, eb.Key);
        Assert.True(connection.IsClosed);
    }

    [Fact]
    public async Task Content_blocks_are_parsed_into_text_media_resources_links_and_unknowns()
    {
        var result = JsonDocument.Parse("""
            {"content":[{"type":"text","text":"hi"},{"type":"image","data":"AAAA","mimeType":"image/png"},{"type":"audio","data":"AAAA","mimeType":"audio/wav"},
            {"type":"resource","resource":{"uri":"file:///a.txt","text":"body","mimeType":"text/plain"}},
            {"type":"resource","resource":{"uri":"file:///b.bin","blob":"AAAA"}},
            {"type":"resource_link","uri":"https://x/y","name":"y"},{"type":"hologram"}],"isError":true}
            """).RootElement;

        var parsed = McpConnection.ParseCallResult(result);

        Assert.True(parsed.IsError);
        Assert.Equal(["text", "image", "audio", "resource", "resource", "resource_link", "hologram"], parsed.Content.Select(b => b.Type));
        Assert.Equal("image/png", parsed.Content[1].MimeType);
        Assert.Equal("body", parsed.Content[3].Text);
        Assert.Null(parsed.Content[4].Text);
    }

    [Fact]
    public async Task Resources_and_prompts_can_be_listed_read_and_fetched()
    {
        var server = Server();
        server.Resources.Add(new JsonObject { ["uri"] = "file:///a.txt", ["name"] = "a", ["mimeType"] = "text/plain" });
        server.Prompts.Add(new JsonObject { ["name"] = "review", ["description"] = "d", ["arguments"] = new JsonArray(new JsonObject { ["name"] = "file" }) });
        var (connection, _, _) = await OpenAsync(server);
        await using var _ = connection;

        var resources = await connection.ListResourcesAsync(CancellationToken.None);
        var prompts = await connection.ListPromptsAsync(CancellationToken.None);

        Assert.Equal("file:///a.txt", Assert.Single(resources).Uri);
        Assert.Equal(["file"], Assert.Single(prompts).Arguments);
        Assert.Equal("resource text", (await connection.ReadResourceAsync("file:///a.txt", CancellationToken.None))[0].Text);
        Assert.Equal("prompt text", (await connection.GetPromptAsync("review", null, CancellationToken.None))[0]);
    }
}
