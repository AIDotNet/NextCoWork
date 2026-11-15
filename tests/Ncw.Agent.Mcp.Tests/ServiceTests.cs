using System.Text.Json.Nodes;
using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm.Routing;

namespace Ncw.Agent.Mcp.Tests;

/// <summary>
/// 连接管理与工具注册。移植自 2.x 的 <c>manager.test.ts</c>(连接与注册、断开与重连、失败路径)并补上 3.0 的:退避重连、断线恢复、list_changed 重列、拒绝原因、状态快照、
/// 密钥不泄漏、项目绑定、测试连接。2.x 用 SDK 的 <c>InMemoryTransport</c> 起真服务器;这里用「假进程 + 测试内的假服务器」(协议层是真的,字节流是内存管道)。
/// </summary>
public sealed class ServiceTests
{
    internal static Task Eventually(Func<bool> condition, string what) => McpRig.UntilAsync(condition, what);

    /// <summary>一台带三个工具的服务器,三个工具各自钉一件事(2.x <c>makeServer</c>)。</summary>
    internal static FakeMcpServer ThreeTools()
    {
        var server = new FakeMcpServer();
        server.Tools.Add(new FakeTool("add", (args, _) =>
            Task.FromResult<JsonNode?>(FakeTool.Text((args["a"]!.GetValue<int>() + args["b"]!.GetValue<int>()).ToString())))
        {
            Description = "adds two numbers",
            Annotations = new JsonObject { ["readOnlyHint"] = true, ["destructiveHint"] = false },
        });
        server.Tools.Add(new FakeTool("unknown_risk") { Description = "declares no hint" });
        server.Tools.Add(new FakeTool("always_fails", (_, _) => Task.FromResult<JsonNode?>(FakeTool.Text("this server says no", isError: true))));
        return server;
    }

    private static McpRig Rig(Func<FakeMcpServer>? make = null, McpOptions? options = null, IDelayer? delayer = null)
    {
        var rig = new McpRig(options, delayer);
        rig.ServeStdio(make ?? ThreeTools);
        return rig;
    }

    // ───────────────────────── 连接与注册 ─────────────────────────

    [Fact]
    public async Task After_connecting_the_tools_are_in_the_registry_as_mcp_id_tool()
    {
        await using var rig = Rig();

        var status = await rig.Service.UpsertAsync(McpRig.Stdio());

        Assert.Equal(McpState.Connected, status.State);
        Assert.Equal(["add", "unknown_risk", "always_fails"], status.Tools);
        Assert.NotNull(rig.Tools.Find("mcp__demo__add"));
        Assert.NotNull(rig.Tools.Find("mcp__demo__always_fails"));
        Assert.Equal("fake-server", status.ServerName);
        Assert.NotNull(status.ConnectedAt);
    }

    [Fact]
    public async Task Hints_the_server_declares_never_lower_the_approval_level()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio());

        var declared = rig.Tool("mcp__demo__add").Info.Traits;
        Assert.False(declared.ReadOnly);
        Assert.True(declared.Destructive);
        foreach (var mode in new[] { PermissionMode.Ask, PermissionMode.Auto })
        {
            Assert.Equal(GateKind.Ask, PermissionGate.Evaluate(new PermissionQuery(mode, declared.ReadOnly, declared.Destructive, WebSearch: true)).Kind);
        }

        var silent = rig.Tool("mcp__demo__unknown_risk").Info.Traits;
        Assert.False(silent.ReadOnly);
        Assert.True(silent.Destructive);
    }

    [Fact]
    public async Task Needs_network_comes_from_our_transport_not_from_the_server()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio());
        Assert.False(rig.Tool("mcp__demo__add").Info.Traits.NeedsNetwork);

        await using var http = new FakeHttpServer(ThreeTools());
        await rig.Service.UpsertAsync(McpRig.Remote("far", http.Url));

        Assert.True(rig.Tool("mcp__far__add").Info.Traits.NeedsNetwork);
    }

    [Fact]
    public async Task A_call_round_trips_with_arguments_and_result()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio());

        var result = await McpRig.CallAsync(rig.Tool("mcp__demo__add"), """{"a":2,"b":40}""");

        Assert.False(result.IsError);
        Assert.Equal("42", McpRig.TextOf(result));
    }

    [Fact]
    public async Task A_server_reported_failure_is_a_tool_failure_not_an_exception()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio());

        var result = await McpRig.CallAsync(rig.Tool("mcp__demo__always_fails"));

        Assert.True(result.IsError);
        Assert.Contains("this server says no", McpRig.TextOf(result));
    }

    [Fact]
    public async Task The_server_is_started_through_the_execution_environment_with_the_configured_command_and_piped_stdin()
    {
        await using var rig = Rig();

        await rig.Service.UpsertAsync(McpRig.Stdio());

        var spec = Assert.Single(rig.Local.Spawned);
        Assert.Equal("fake-mcp", spec.Program);
        Assert.Equal(["--stdio"], spec.Args);
        Assert.Equal(StdinMode.Pipe, spec.Stdin);
    }

    // ───────────────────────── 断开与重连 ─────────────────────────

    [Fact]
    public async Task Disabling_a_server_clears_its_tools_and_keeps_the_configuration()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio());
        Assert.Equal(3, rig.Tools.List().Count);

        var status = await rig.Service.SetEnabledAsync("demo", false);

        Assert.Equal(McpState.Disabled, status.State);
        Assert.Empty(rig.Tools.List());
        Assert.False(rig.Configs.Get("demo")!.Enabled);
        await Eventually(() => rig.Local.Processes[0].Exited.IsCompleted, "the process to be shut down");
    }

    [Fact]
    public async Task Disconnecting_one_server_does_not_touch_another()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio("a"));
        await rig.Service.UpsertAsync(McpRig.Stdio("b"));
        Assert.Equal(6, rig.Tools.List().Count);

        await rig.Service.SetEnabledAsync("a", false);

        Assert.Equal(3, rig.Tools.List().Count);
        Assert.NotNull(rig.Tools.Find("mcp__b__add"));
        Assert.Equal(McpState.Connected, rig.Service.StatusOf("b")!.State);
    }

    [Fact]
    public async Task Reconnecting_replaces_the_tools_without_doubling_and_the_external_name_does_not_change()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio());
        var before = rig.Tools.Find("mcp__demo__add")!.ExternalName;

        await rig.Service.ReconnectAsync("demo");

        Assert.Equal(3, rig.Tools.List().Count);
        Assert.Equal(before, rig.Tools.Find("mcp__demo__add")!.ExternalName);
        Assert.Equal(2, rig.Local.Spawned.Count);
    }

    [Fact]
    public async Task Changing_the_configuration_reconnects_instead_of_keeping_the_old_connection()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio());

        await rig.Service.UpsertAsync(McpRig.Stdio() with { Command = "other-command" });

        Assert.Equal(["fake-mcp", "other-command"], rig.Local.Spawned.Select(s => s.Program));
        await Eventually(() => rig.Local.Processes[0].Exited.IsCompleted, "the first connection to be closed");
    }

    [Fact]
    public async Task Removing_a_server_disconnects_it_and_deletes_the_configuration_and_the_secrets_together()
    {
        await using var rig = Rig();
        var config = McpRig.Stdio() with { EnvNames = ["TOKEN"] };
        await rig.Service.UpsertAsync(config);
        await rig.Service.SetSecretsAsync("demo", new Dictionary<string, string> { ["TOKEN"] = "tok-SECRET-123456" });
        Assert.NotEmpty(rig.Secrets.References);

        Assert.True(await rig.Service.RemoveAsync("demo"));

        Assert.Empty(rig.Tools.List());
        Assert.Null(rig.Configs.Get("demo"));
        Assert.Empty(rig.Secrets.References);
        Assert.Null(rig.Service.StatusOf("demo"));
        Assert.False(await rig.Service.RemoveAsync("demo")); // 幂等:没有这台也不报错
    }

    [Fact]
    public async Task A_removed_server_does_not_leave_its_token_for_a_new_server_with_the_same_id()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio() with { EnvNames = ["TOKEN"] });
        await rig.Service.SetSecretsAsync("demo", new Dictionary<string, string> { ["TOKEN"] = "tok-SECRET-123456" });
        await rig.Service.RemoveAsync("demo");

        await rig.Service.UpsertAsync(McpRig.Stdio() with { EnvNames = ["TOKEN"] });

        Assert.False(rig.Service.SecretsInfo("demo").Entries.Single().IsSet);
        Assert.Null(rig.Local.Spawned[^1].Environment);
    }

    [Fact]
    public async Task Disposing_the_service_leaves_no_tools_and_kills_the_processes()
    {
        var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio("a"));
        await rig.Service.UpsertAsync(McpRig.Stdio("b"));

        await rig.DisposeAsync();

        Assert.Empty(rig.Tools.List());
        Assert.All(rig.Local.Processes, p => Assert.True(p.Exited.IsCompleted));
    }

    // ───────────────────────── 失败路径 ─────────────────────────

    [Fact]
    public async Task A_command_that_cannot_start_ends_in_the_failed_state_with_a_stable_key_and_is_not_retried()
    {
        await using var rig = Rig();
        rig.Local.StartFailure = new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, "spawn fake-mcp ENOENT");

        var status = await rig.Service.UpsertAsync(McpRig.Stdio());

        Assert.Equal(McpState.Failed, status.State);
        Assert.Equal(McpErrorKeys.CommandNotFound, status.ErrorKey);
        Assert.Empty(rig.Tools.List());
        Assert.Empty(((RecordingDelayer)rig.Delayer).Delays); // 命令不存在,重试也没用
    }

    [Fact]
    public async Task An_invalid_id_is_refused_before_anything_is_persisted_or_started()
    {
        await using var rig = Rig();

        var e = await Assert.ThrowsAsync<McpException>(() => rig.Service.UpsertAsync(McpRig.Stdio("../etc/passwd")));

        Assert.Equal(McpErrorKeys.InvalidId, e.Key);
        Assert.Empty(rig.Configs.List());
        Assert.Empty(rig.Local.Spawned);
        Assert.Empty(rig.Tools.List());
    }

    [Fact]
    public async Task Two_overlapping_upserts_of_the_same_id_end_with_exactly_one_live_connection_and_one_tool_set()
    {
        await using var rig = Rig();

        var results = await Task.WhenAll(rig.Service.UpsertAsync(McpRig.Stdio()), rig.Service.UpsertAsync(McpRig.Stdio()));

        Assert.All(results, r => Assert.Equal(McpState.Connected, rig.Service.StatusOf("demo")!.State));
        Assert.Equal(3, rig.Tools.List().Count);
        Assert.Equal(1, rig.Local.Processes.Count(p => !p.Exited.IsCompleted));
    }

    [Fact]
    public async Task A_server_that_never_answers_initialize_times_out_with_a_stable_key_and_the_process_is_released()
    {
        var options = McpRig.FastOptions();
        var rig = new McpRig(new McpOptions { ConnectTimeout = TimeSpan.FromMilliseconds(200), ListToolsTimeout = options.ListToolsTimeout, CallTimeout = options.CallTimeout, ReconnectDelays = [], GracefulExit = TimeSpan.FromMilliseconds(20) });
        rig.Local.ProcessFactory = _ => new FakeProcess(rig.Local.Id, null);
        await using var _ = rig;

        var status = await rig.Service.UpsertAsync(McpRig.Stdio());

        Assert.Equal(McpState.Failed, status.State);
        Assert.Equal(McpErrorKeys.Timeout, status.ErrorKey);
        await Eventually(() => rig.Local.Processes[0].Exited.IsCompleted, "the stuck process to be killed");
    }

    [Fact]
    public async Task A_handshake_that_succeeds_but_cannot_list_tools_leaves_no_connection_behind()
    {
        await using var rig = new McpRig();
        rig.Local.ProcessFactory = _ =>
        {
            var server = new FakeMcpServer { ListError = true };
            server.Tools.Add(new FakeTool("x"));
            return new FakeProcess(rig.Local.Id, server);
        };

        var status = await rig.Service.UpsertAsync(McpRig.Stdio());

        Assert.Equal(McpState.Failed, status.State);
        Assert.Equal(McpErrorKeys.ListTools, status.ErrorKey);
        await Eventually(() => rig.Local.Processes[0].Exited.IsCompleted, "the half-open connection to be closed");
    }

    // ───────────────────────── 重连与恢复 ─────────────────────────

    [Fact]
    public async Task A_crashed_server_is_marked_failed_then_reconnected_after_the_first_backoff_and_its_tools_come_back()
    {
        var delayer = new GatedDelayer();
        await using var rig = Rig(delayer: delayer);
        await rig.Service.UpsertAsync(McpRig.Stdio());
        Assert.Equal(3, rig.Tools.List().Count);

        rig.Local.Processes[0].ExitNow(7);

        await rig.WaitForAsync("demo", McpState.Failed);
        var failed = rig.Service.StatusOf("demo")!;
        Assert.Equal(McpErrorKeys.ProcessExited, failed.ErrorKey);
        Assert.Equal("7", failed.ErrorParams["exitCode"]);
        Assert.Equal(1, failed.Attempt);
        Assert.Empty(rig.Tools.List()); // 下线:断了的连接不再挂着工具
        await Eventually(() => !delayer.Delays.IsEmpty, "the reconnect to be scheduled");

        delayer.Release();

        await rig.WaitForAsync("demo", McpState.Connected);
        Assert.Equal(3, rig.Tools.List().Count);
        Assert.Equal(0, rig.Service.StatusOf("demo")!.Attempt);
        Assert.Equal([TimeSpan.FromSeconds(1)], delayer.Delays);
    }

    [Fact]
    public async Task The_backoff_grows_and_stops_when_exhausted_after_which_a_manual_reconnect_works()
    {
        await using var rig = Rig();
        var healthy = false;
        rig.Local.ProcessFactory = _ =>
        {
            var server = healthy ? ThreeTools() : null;
            var process = new FakeProcess(rig.Local.Id, server);
            if (!healthy)
            {
                process.ExitNow(1);
            }

            return process;
        };

        var status = await rig.Service.UpsertAsync(McpRig.Stdio());
        await Eventually(() => rig.Service.StatusOf("demo")!.Attempt == 4, "the retries to be exhausted");

        Assert.Equal(McpState.Failed, rig.Service.StatusOf("demo")!.State);
        Assert.Equal([1, 2, 4], ((RecordingDelayer)rig.Delayer).Delays.Select(d => (int)d.TotalSeconds));
        Assert.Equal(4, rig.Local.Spawned.Count);
        _ = status;

        healthy = true;
        var again = await rig.Service.ReconnectAsync("demo");

        Assert.Equal(McpState.Connected, again.State);
        Assert.Equal(3, rig.Tools.List().Count);
    }

    [Fact]
    public async Task A_tools_list_changed_notification_re_lists_and_replaces_the_registered_tools()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio());
        var server = rig.Servers[0];
        server.Tools.RemoveAll(t => t.Name == "always_fails");
        server.Tools.Add(new FakeTool("brand_new"));

        await server.NotifyAsync("notifications/tools/list_changed");

        await Eventually(() => rig.Tools.Find("mcp__demo__brand_new") is not null, "the new tool to appear");
        Assert.Null(rig.Tools.Find("mcp__demo__always_fails"));
        Assert.Equal(["add", "unknown_risk", "brand_new"], rig.Service.StatusOf("demo")!.Tools);
    }

    [Fact]
    public async Task Several_list_changed_notifications_in_a_burst_converge_on_the_latest_tool_set()
    {
        await using var rig = Rig();
        await rig.Service.UpsertAsync(McpRig.Stdio());
        var server = rig.Servers[0];

        for (var i = 0; i < 5; i++)
        {
            server.Tools.Add(new FakeTool($"extra{i}"));
            await server.NotifyAsync("notifications/tools/list_changed");
        }

        await Eventually(() => rig.Tools.Find("mcp__demo__extra4") is not null, "the last tool to appear");
        Assert.Equal(8, rig.Service.StatusOf("demo")!.Tools.Count);
    }

    // ───────────────────────── 拒绝原因 / 冲突 ─────────────────────────

    [Fact]
    public async Task Tools_the_server_lists_but_we_refuse_are_reported_with_a_reason_even_when_the_connection_succeeds()
    {
        await using var rig = Rig(() =>
        {
            var server = new FakeMcpServer();
            server.Tools.Add(new FakeTool("good"));
            server.Tools.Add(new FakeTool("../../etc/passwd"));
            server.Tools.Add(new FakeTool("has space"));
            server.Tools.Add(new FakeTool("good")); // 重名
            server.Tools.Add(new FakeTool("huge") { InputSchema = new JsonObject { ["type"] = "object", ["description"] = new string('x', 100_000) } });
            server.Tools.Add(new FakeTool("array_schema") { InputSchema = new JsonObject { ["type"] = "array" } });
            return server;
        });

        var status = await rig.Service.UpsertAsync(McpRig.Stdio());

        Assert.Equal(McpState.Connected, status.State);
        Assert.Equal(["good", "array_schema"], status.Tools);
        Assert.Equal(
            [McpErrorKeys.ToolNameInvalid, McpErrorKeys.ToolNameInvalid, McpErrorKeys.ToolDuplicate, McpErrorKeys.ToolSchemaTooLarge],
            status.Rejected.Select(r => r.ReasonKey));
        // schema 不是 object 的退回 {"type":"object"}:工具仍然可用
        Assert.Equal("object", System.Text.Json.JsonDocument.Parse(rig.Tool("mcp__demo__array_schema").Info.InputSchema.GetRawText()).RootElement.GetProperty("type").GetString());
        Assert.Null(rig.Tools.Find("mcp__demo__../../etc/passwd"));
    }

    [Fact]
    public async Task A_tool_whose_internal_id_another_source_already_owns_is_dropped_and_reported_as_a_conflict()
    {
        await using var rig = Rig();
        rig.Tools.Register(new PlainTool("mcp__demo__add"), "plugin:other");

        var status = await rig.Service.UpsertAsync(McpRig.Stdio());

        Assert.Contains(status.Rejected, r => r.Name == "add" && r.ReasonKey == McpErrorKeys.ToolConflict);
        Assert.DoesNotContain("add", status.Tools);
        Assert.IsType<PlainTool>(rig.Tools.Find("mcp__demo__add")!.Tool);
    }

    [Fact]
    public async Task Long_tool_names_get_a_stable_hashed_external_name_that_survives_reconnects()
    {
        await using var rig = Rig(() =>
        {
            var server = new FakeMcpServer();
            server.Tools.Add(new FakeTool("a_really_long_tool_name_" + new string('z', 90)));
            return server;
        });
        await rig.Service.UpsertAsync(McpRig.Stdio());
        var entry = rig.Tools.List().Single();
        Assert.True(entry.ExternalName.Length <= 64);
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", entry.ExternalName);

        await rig.Service.ReconnectAsync("demo");

        Assert.Equal(entry.ExternalName, rig.Tools.List().Single().ExternalName);
    }

    // ───────────────────────── 状态快照 ─────────────────────────

    [Fact]
    public async Task The_snapshot_is_the_full_state_with_a_strictly_increasing_revision_and_the_events_carry_the_same_type()
    {
        await using var rig = Rig();
        var seen = new System.Collections.Concurrent.ConcurrentQueue<McpSnapshot>();
        rig.Service.Changed += seen.Enqueue;

        await rig.Service.UpsertAsync(McpRig.Stdio("b"));
        await rig.Service.UpsertAsync(McpRig.Stdio("a"));

        var revs = seen.Select(s => s.Rev).ToList();
        Assert.Equal(revs.Order(), revs);
        Assert.Equal(revs.Distinct().Count(), revs.Count);
        var last = rig.Service.Snapshot();
        Assert.Equal(["a", "b"], last.Servers.Select(s => s.Config.Id)); // 稳定顺序
        Assert.Equal(seen.Max(s => s.Rev), last.Rev);
        Assert.Contains(seen, s => s.Servers.Any(x => x.State == McpState.Connecting)); // 过程也看得见
    }

    [Fact]
    public async Task Starting_the_service_connects_enabled_servers_in_the_background_and_skips_disabled_and_corrupt_rows()
    {
        var rig = Rig();
        await using var _ = rig;
        rig.Configs.Put(McpRig.Stdio("on"));
        rig.Configs.Put(McpRig.Stdio("off", enabled: false));

        await rig.Service.StartAsync();

        await rig.WaitForAsync("on", McpState.Connected);
        Assert.Equal(McpState.Disabled, rig.Service.StatusOf("off")!.State);
        Assert.Single(rig.Local.Spawned);
    }

    [Fact]
    public async Task Upserting_from_a_client_dto_keeps_the_unknown_fields_already_stored()
    {
        await using var rig = Rig();
        var json = """{"id":"demo","name":"Demo","enabled":true,"transport":"stdio","command":"fake-mcp","args":[],"envNames":[],"futureField":7}""";
        rig.Configs.Put(McpConfigJson.Parse(json)!);

        await rig.Service.UpsertAsync(McpRig.Stdio() with { Name = "Renamed" });

        Assert.Contains("futureField", rig.Configs.Dump());
        Assert.Equal("Renamed", rig.Configs.Get("demo")!.Name);
    }

    // ───────────────────────── 项目绑定 ─────────────────────────

    [Fact]
    public async Task A_project_bound_server_runs_in_that_projects_environment_at_its_root_and_only_serves_that_project()
    {
        var projectEnv = new TestEnvironment(new LocalEnvironment("dev-1"), EnvironmentKind.Ssh);
        await using var rig = new McpRig(projects: id => id == "proj-a" ? projectEnv : null);
        projectEnv.ProcessFactory = _ => new FakeProcess(projectEnv.Id, ThreeTools());

        await rig.Service.UpsertAsync(McpRig.Stdio() with { WorkspaceId = "proj-a" });

        var spec = Assert.Single(projectEnv.Spawned);
        Assert.Equal(McpRig.ProjectRoot(projectEnv), spec.WorkingDirectory);
        Assert.Empty(rig.Local.Spawned);
        var tool = rig.Tool("mcp__demo__add");
        // 快照过滤:别的项目的会话看不到;细节卡片带执行环境标识(这里是伪装成 SSH 的那个环境)
        Assert.Single(rig.Tools.Snapshot(McpRig.Context("proj-a-session")), b => b.Tool == tool);
        Assert.DoesNotContain(rig.Tools.Snapshot(McpRig.Context("proj-b-session")), b => b.Tool == tool);
        var denied = await McpRig.CallAsync(tool, """{"a":1,"b":2}""", McpRig.Context("proj-b-session"));
        Assert.True(denied.IsError);
        Assert.Contains("different project", McpRig.TextOf(denied));
        var allowed = await McpRig.CallAsync(tool, """{"a":1,"b":2}""", McpRig.Context("proj-a-session"));
        Assert.Equal("ssh", allowed.Details!.Value.GetProperty("environment").GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_project_that_no_longer_exists_fails_with_a_stable_key_and_is_not_retried()
    {
        await using var rig = new McpRig(projects: _ => null);

        var status = await rig.Service.UpsertAsync(McpRig.Stdio() with { WorkspaceId = "gone" });

        Assert.Equal(McpErrorKeys.ProjectMissing, status.ErrorKey);
        Assert.Empty(((RecordingDelayer)rig.Delayer).Delays);
    }

    // ───────────────────────── 资源 / 提示 ─────────────────────────

    [Fact]
    public async Task Resources_and_prompts_are_listed_through_the_live_connection_and_a_disconnected_server_says_so()
    {
        await using var rig = Rig(() =>
        {
            var server = ThreeTools();
            server.Resources.Add(new JsonObject { ["uri"] = "file:///a.txt", ["name"] = "a" });
            server.Prompts.Add(new JsonObject { ["name"] = "review" });
            return server;
        });
        await rig.Service.UpsertAsync(McpRig.Stdio());

        Assert.Equal("file:///a.txt", Assert.Single(await rig.Service.ListResourcesAsync("demo")).Uri);
        Assert.Equal("review", Assert.Single(await rig.Service.ListPromptsAsync("demo")).Name);

        await rig.Service.SetEnabledAsync("demo", false);
        var e = await Assert.ThrowsAsync<McpException>(() => rig.Service.ListResourcesAsync("demo"));
        Assert.Equal(McpErrorKeys.NotConnected, e.Key);
    }

    private sealed class PlainTool(string id) : Ncw.Agent.Kernel.Tools.IAgentTool
    {
        public Ncw.Agent.Kernel.Tools.ToolSpec Info { get; } = new(id, "d", System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(), new Ncw.Agent.Kernel.Tools.ToolTraits(true, false, false));

        public ValueTask<Ncw.Agent.Kernel.Tools.ToolResult> ExecuteAsync(Ncw.Agent.Kernel.Tools.ToolInvocation call, Ncw.Agent.Kernel.Tools.ToolContext ctx, CancellationToken ct) =>
            ValueTask.FromResult(Ncw.Agent.Kernel.Tools.ToolResult.Text("plain"));
    }
}
