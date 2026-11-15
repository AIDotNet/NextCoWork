using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Mcp.Tests;

/// <summary>
/// 一个 MCP 工具的行为。移植自 2.x 的 <c>bridge-timeout.test.ts</c>(墙钟上限:不回话时收敛成工具失败而不是永远挂着;超时真的取消远端那次调用;用户按停止时原样抛出;
/// 连取消都不理的服务器同样收住;按时回包的工具不受影响)和 <c>bridge.ts</c> 文件头那张「不采信服务器」的表。
/// 2.x 用假 client + 假定时器;这里用真连接 + 假进程里「不回话」的服务器,超时调小到几百毫秒。
/// </summary>
public sealed class ToolTests
{
    private static McpOptions Timeouts(TimeSpan call, int maxResult = 60_000) => new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ListToolsTimeout = TimeSpan.FromSeconds(5),
        CallTimeout = call,
        ReconnectDelays = [],
        MaxResultChars = maxResult,
        GracefulExit = TimeSpan.FromMilliseconds(20),
    };

    private static async Task<(McpRig Rig, FakeMcpServer Server)> ConnectedAsync(FakeMcpServer server, McpOptions? options = null)
    {
        var rig = new McpRig(options ?? Timeouts(TimeSpan.FromSeconds(5)));
        rig.ServeStdio(() => server);
        await rig.Service.UpsertAsync(McpRig.Stdio());
        return (rig, server);
    }

    private static FakeMcpServer With(params FakeTool[] tools)
    {
        var server = new FakeMcpServer();
        server.Tools.AddRange(tools);
        return server;
    }

    private static FakeTool Silent(string name = "slow_tool") => new(name, (_, _) => Task.FromResult<JsonNode?>(null)) { Description = "a tool that never answers" };

    // ───────────────────────── 墙钟上限 ─────────────────────────

    [Fact]
    public async Task When_the_server_does_not_answer_the_call_ends_as_a_tool_failure_after_the_deadline_instead_of_hanging_forever()
    {
        var (rig, _) = await ConnectedAsync(With(Silent()), Timeouts(TimeSpan.FromMilliseconds(250)));
        await using var _ = rig;
        var tool = rig.Tool("mcp__demo__slow_tool");

        var watch = Stopwatch.StartNew();
        var result = await McpRig.CallAsync(tool);

        Assert.True(watch.ElapsedMilliseconds >= 200, "the gate must not fall early: tools may legitimately be slow");
        Assert.True(result.IsError);
        Assert.Contains("slow_tool", McpRig.TextOf(result));
        Assert.Contains("did not respond", McpRig.TextOf(result));
    }

    [Fact]
    public async Task The_default_deadline_is_sixty_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), new McpOptions().CallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), new McpOptions().ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), new McpOptions().ListToolsTimeout);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_timeout_really_cancels_the_remote_call_the_server_receives_notifications_cancelled()
    {
        var (rig, server) = await ConnectedAsync(With(Silent()), Timeouts(TimeSpan.FromMilliseconds(200)));
        await using var _ = rig;

        await McpRig.CallAsync(rig.Tool("mcp__demo__slow_tool"));

        // 只 race 不 abort 的话,远端那次调用会跑到底:一个正在建 PR 的工具会把 PR 建完
        await ServiceTests.Eventually(() => !server.Cancelled.IsEmpty, "notifications/cancelled");
    }

    [Fact]
    public async Task When_the_user_presses_stop_the_abort_is_rethrown_not_disguised_as_a_tool_failure()
    {
        var (rig, server) = await ConnectedAsync(With(Silent()));
        await using var _ = rig;
        using var stop = new CancellationTokenSource();

        var call = McpRig.CallAsync(rig.Tool("mcp__demo__slow_tool"), ct: stop.Token);
        await Task.Delay(50);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await ServiceTests.Eventually(() => !server.Cancelled.IsEmpty, "the remote call to be cancelled too");
    }

    [Fact]
    public async Task A_server_that_ignores_cancellation_is_still_contained_as_a_tool_failure_not_a_run_abort()
    {
        // 服务器收到 cancelled 通知也不理它:不会有任何响应。客户端这一侧到点就放弃,给出的是工具失败。
        var stubborn = new FakeTool("stubborn", async (_, _) =>
        {
            await Task.Delay(Timeout.Infinite);
            return null;
        });
        var (rig, _) = await ConnectedAsync(With(stubborn), Timeouts(TimeSpan.FromMilliseconds(200)));
        await using var _ = rig;

        var result = await McpRig.CallAsync(rig.Tool("mcp__demo__stubborn"));

        Assert.True(result.IsError);
        Assert.Contains("stubborn", McpRig.TextOf(result));
    }

    [Fact]
    public async Task A_tool_that_answers_in_time_is_unaffected_by_the_gate()
    {
        var (rig, _) = await ConnectedAsync(With(new FakeTool("fine", (_, _) => Task.FromResult<JsonNode?>(FakeTool.Text("all done")))), Timeouts(TimeSpan.FromMilliseconds(500)));
        await using var _ = rig;

        var result = await McpRig.CallAsync(rig.Tool("mcp__demo__fine"));

        Assert.False(result.IsError);
        Assert.Contains("all done", McpRig.TextOf(result));
    }

    // ───────────────────────── 不采信服务器 / 描述 ─────────────────────────

    [Fact]
    public async Task The_description_carries_the_server_prefix_and_is_cleaned_and_clamped_because_it_is_untrusted_text()
    {
        var evil = new FakeTool("evil") { Description = "Do this\u0000\u0007 now" + new string('x', 6000) };
        var plain = new FakeTool("plain") { Description = null };
        var (rig, _) = await ConnectedAsync(With(evil, plain));
        await using var _ = rig;

        var description = rig.Tool("mcp__demo__evil").Info.Description;

        Assert.StartsWith("[MCP:Demo]\nDo this now", description);
        Assert.True(description.Length <= 4096);
        Assert.DoesNotContain('\u0000', description);
        Assert.Contains("no description", rig.Tool("mcp__demo__plain").Info.Description);
    }

    [Fact]
    public async Task The_server_description_in_the_configuration_joins_the_prefix()
    {
        var rig = new McpRig();
        await using var _ = rig;
        rig.ServeStdio(() => With(new FakeTool("t")));

        await rig.Service.UpsertAsync(McpRig.Stdio() with { Description = "internal GitLab" });

        Assert.StartsWith("[MCP:Demo · internal GitLab]", rig.Tool("mcp__demo__t").Info.Description);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("5")]
    public async Task Arguments_that_are_not_an_object_are_refused_before_anything_is_sent(string input)
    {
        var (rig, server) = await ConnectedAsync(With(new FakeTool("t")));
        await using var _ = rig;
        var tool = rig.Tool("mcp__demo__t");

        var check = tool.PrepareArguments(JsonDocument.Parse(input).RootElement);

        Assert.False(check.IsValid);
        var result = await McpRig.CallAsync(tool, input);
        Assert.True(result.IsError);
        Assert.DoesNotContain("tools/call", server.Methods);
    }

    [Fact]
    public async Task Missing_arguments_become_an_empty_object()
    {
        var seen = new List<string>();
        var (rig, _) = await ConnectedAsync(With(new FakeTool("t", (args, _) =>
        {
            seen.Add(args.ToJsonString());
            return Task.FromResult<JsonNode?>(FakeTool.Text("ok"));
        })));
        await using var _ = rig;
        var tool = rig.Tool("mcp__demo__t");

        Assert.True(tool.PrepareArguments(default).IsValid);
        await McpRig.CallAsync(tool, "null");

        Assert.Equal(["{}"], seen);
    }

    [Fact]
    public async Task Arguments_are_forwarded_verbatim_without_our_own_schema_validation()
    {
        // schema 要求 a 是 number,模型传了字符串:校验它的人是服务器,不是我们(否则两边理解不一致时「按 schema 传了却被我们拒了」)
        var seen = new List<string>();
        var tool = new FakeTool("typed", (args, _) =>
        {
            seen.Add(args.ToJsonString());
            return Task.FromResult<JsonNode?>(FakeTool.Text("ok"));
        })
        {
            InputSchema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["a"] = new JsonObject { ["type"] = "number" } }, ["required"] = new JsonArray("a") },
        };
        var (rig, _) = await ConnectedAsync(With(tool));
        await using var _ = rig;

        var result = await McpRig.CallAsync(rig.Tool("mcp__demo__typed"), """{"a":"not a number"}""");

        Assert.False(result.IsError);
        Assert.Equal(["""{"a":"not a number"}"""], seen);
        Assert.Contains("\"required\"", rig.Tool("mcp__demo__typed").Info.InputSchema.GetRawText());
    }

    // ───────────────────────── 输出 ─────────────────────────

    [Fact]
    public async Task Images_audio_and_resources_become_text_notes_instead_of_being_dropped()
    {
        var tool = new FakeTool("media", (_, _) => Task.FromResult<JsonNode?>(JsonNode.Parse("""
            {"content":[{"type":"text","text":"before"},{"type":"image","data":"AAAA","mimeType":"image/png"},{"type":"audio","data":"AAAA","mimeType":"audio/wav"},
            {"type":"resource","resource":{"uri":"file:///a.txt","text":"file body"}},{"type":"resource","resource":{"uri":"file:///b.bin","blob":"AAAA"}},
            {"type":"resource_link","uri":"https://x/y","name":"y"},{"type":"hologram"}]}
            """)));
        var (rig, _) = await ConnectedAsync(With(tool));
        await using var _ = rig;

        var text = McpRig.TextOf(await McpRig.CallAsync(rig.Tool("mcp__demo__media")));

        Assert.Contains("before", text);
        Assert.Contains("an image (image/png)", text);
        Assert.Contains("an audio clip (audio/wav)", text);
        Assert.Contains("[Resource file:///a.txt]\nfile body", text);
        Assert.Contains("file:///b.bin is binary content", text);
        Assert.Contains("Resource link y https://x/y", text);
        Assert.Contains("unknown type (hologram)", text);
        Assert.DoesNotContain("AAAA", text);
    }

    [Fact]
    public async Task An_empty_result_says_so_instead_of_returning_nothing()
    {
        var (rig, _) = await ConnectedAsync(With(
            new FakeTool("empty", (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["content"] = new JsonArray() })),
            new FakeTool("empty_error", (_, _) => Task.FromResult<JsonNode?>(new JsonObject { ["content"] = new JsonArray(), ["isError"] = true }))));
        await using var _ = rig;

        var ok = await McpRig.CallAsync(rig.Tool("mcp__demo__empty"));
        var failed = await McpRig.CallAsync(rig.Tool("mcp__demo__empty_error"));

        Assert.False(ok.IsError);
        Assert.Contains("without returning any content", McpRig.TextOf(ok));
        Assert.True(failed.IsError);
        Assert.Contains("no explanation", McpRig.TextOf(failed));
    }

    [Fact]
    public async Task A_huge_result_is_truncated_with_a_marker_and_flagged_as_truncated()
    {
        var (rig, _) = await ConnectedAsync(
            With(new FakeTool("big", (_, _) => Task.FromResult<JsonNode?>(FakeTool.Text(new string('y', 5000))))),
            Timeouts(TimeSpan.FromSeconds(5), maxResult: 1000));
        await using var _ = rig;

        var result = await McpRig.CallAsync(rig.Tool("mcp__demo__big"));

        Assert.True(result.Truncated);
        Assert.Equal(5000, result.OriginalBytes);
        var text = McpRig.TextOf(result);
        Assert.StartsWith(new string('y', 1000), text);
        Assert.Contains("4000 more characters", text);
    }

    [Fact]
    public async Task A_json_rpc_error_from_the_server_is_a_tool_failure_with_the_server_message()
    {
        var (rig, _) = await ConnectedAsync(With(new FakeTool("t")));
        await using var _ = rig;
        var tool = rig.Tool("mcp__demo__t");
        rig.Servers[0].Tools.Clear(); // 服务器之后「忘了」这个工具

        var result = await McpRig.CallAsync(tool);

        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", McpRig.TextOf(result));
    }

    [Fact]
    public async Task A_call_on_a_closed_connection_fails_with_a_clear_message_and_the_tool_drops_out_of_the_snapshot()
    {
        var (rig, _) = await ConnectedAsync(With(new FakeTool("t")), Timeouts(TimeSpan.FromSeconds(5)));
        await using var _ = rig;
        var tool = rig.Tool("mcp__demo__t");
        rig.Local.Processes[0].ExitNow(1);
        await ServiceTests.Eventually(() => rig.Tools.List().Count == 0, "the tools to be taken offline");

        var result = await McpRig.CallAsync(tool);

        Assert.True(result.IsError);
        Assert.Contains("not connected", McpRig.TextOf(result));
        Assert.False(tool.IsEnabled(McpRig.Context()));
    }

    [Fact]
    public async Task The_details_carry_the_server_the_tool_and_the_execution_environment_for_the_tool_card()
    {
        var (rig, _) = await ConnectedAsync(With(new FakeTool("t")));
        await using var _ = rig;

        var result = await McpRig.CallAsync(rig.Tool("mcp__demo__t"));

        var details = result.Details!.Value;
        Assert.Equal("mcp", details.GetProperty("kind").GetString());
        Assert.Equal("demo", details.GetProperty("server").GetString());
        Assert.Equal("t", details.GetProperty("tool").GetString());
        var env = details.GetProperty("environment");
        Assert.Equal("dev-1", env.GetProperty("deviceId").GetString());
        Assert.Equal("local", env.GetProperty("environment").GetString());
        Assert.Equal("local", env.GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_registry_names_the_tool_with_a_model_safe_external_name_and_the_internal_id_is_what_permission_rules_match()
    {
        var (rig, _) = await ConnectedAsync(With(new FakeTool("search.files"), new FakeTool("run-it")));
        await using var _ = rig;

        var dotted = rig.Tools.Find("mcp__demo__search.files")!;

        Assert.Equal("mcp__demo__search_files", dotted.ExternalName);
        Assert.Equal("mcp__demo__run-it", rig.Tools.Find("mcp__demo__run-it")!.ExternalName);
        Assert.Equal("mcp__demo__search.files", dotted.Tool.Info.InternalId);
    }
}
