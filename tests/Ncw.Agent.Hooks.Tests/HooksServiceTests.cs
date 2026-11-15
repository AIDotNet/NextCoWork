using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Hooks.Tests;

/// <summary>
/// 配置(列表 / 诊断 / 保存 / 删除 / 启停 / 试运行)和事件选择(顺序、上限、matcher、启停)。
/// 移植自 2.x 的 <c>hooks-methods.test.ts</c>(列表 + 启停不改内容、未知钩子 / 工作区如实报告)与 <c>hooks.ts</c> 里的 <c>hooksFor</c> / <c>runHookEvent</c> 规则;
/// 真文件、真 <see cref="LocalEnvironment"/>。
/// </summary>
public sealed class HooksServiceTests : IAsyncLifetime
{
    private readonly HookFixture _fx = new();
    private HooksService _service = null!;
    private readonly RecordingLog _log = new();

    private sealed class RecordingLog : ILoopDiagnostics
    {
        public List<string> Lines { get; } = [];

        public void Warn(string message, Exception? error = null)
        {
            lock (Lines)
            {
                Lines.Add(message);
            }
        }
    }

    public ValueTask InitializeAsync()
    {
        _service = _fx.NewService(log: _log);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _service.DisposeAsync();
        await _fx.DisposeAsync();
    }

    private static HookSaveRequest Save(HookScope scope, HookDefinition hook, IReadOnlyList<HookEnvEntry>? env = null) =>
        new(scope, scope == HookScope.Project ? HookFixture.Workspace : null, hook, env);

    private static HookDefinition NewHook(string command, string hookEvent = HookEvents.PreToolUse, string id = "") =>
        new() { Id = id, Event = hookEvent, Command = command, TimeoutMs = 10_000 };

    // ───────────────────────── 列表 / 启停(hooks-methods.test.ts) ─────────────────────────

    [Fact]
    public async Task Listing_shows_the_project_hooks_and_toggling_one_does_not_change_its_content()
    {
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"PreToolUse":[{"id":"HOOK1","matcher":"Bash","command":"echo guard","timeout":5}]}}""");

        var listed = await _service.ListAsync(HookFixture.Workspace);
        var hook = Assert.Single(listed);
        Assert.Equal(HookScope.Project, hook.Scope);
        Assert.Equal("echo guard", hook.Hook.Command);
        Assert.Equal(_fx.ProjectFile, hook.SourcePath);

        await _service.SetEnabledAsync(HookScope.Project, HookFixture.Workspace, "HOOK1", false);
        Assert.False((await _service.ListAsync(HookFixture.Workspace)).Single().Hook.Enabled);
        var onDisk = _fx.Read(_fx.ProjectFile);
        Assert.Contains("\"echo guard\"", onDisk);
        Assert.Contains("\"Bash\"", onDisk);

        await _service.SetEnabledAsync(HookScope.Project, HookFixture.Workspace, "HOOK1", true);
        Assert.True((await _service.ListAsync(HookFixture.Workspace)).Single().Hook.Enabled);
        Assert.DoesNotContain("\"enabled\"", _fx.Read(_fx.ProjectFile));
    }

    [Fact]
    public async Task Unknown_hooks_and_projects_are_reported_honestly()
    {
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"PreToolUse":[{"id":"HOOK1","command":"x"}]}}""");

        var missing = await Assert.ThrowsAsync<HookException>(() => _service.SetEnabledAsync(HookScope.Project, HookFixture.Workspace, "NOPE", false));
        Assert.Equal(HookErrorKeys.NotFound, missing.Key);
        var globalMissing = await Assert.ThrowsAsync<HookException>(() => _service.SetEnabledAsync(HookScope.Global, null, "HOOK1", false));
        Assert.Equal(HookErrorKeys.NotFound, globalMissing.Key); // 它在项目层,不在全局层
        var noProject = await Assert.ThrowsAsync<HookException>(() => _service.ListAsync("nope"));
        Assert.Equal(HookErrorKeys.NoWorkspace, noProject.Key);
        var projectScopeWithoutProject = await Assert.ThrowsAsync<HookException>(() => _service.SetEnabledAsync(HookScope.Project, null, "HOOK1", false));
        Assert.Equal(HookErrorKeys.NoWorkspace, projectScopeWithoutProject.Key);
    }

    [Fact]
    public async Task Hand_written_entries_without_an_id_get_one_when_the_list_is_read_and_it_is_written_back_once()
    {
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"command":"notify"}]}}""");

        var first = (await _service.ListAsync(HookFixture.Workspace)).Single();
        var second = (await _service.ListAsync(HookFixture.Workspace)).Single();

        Assert.NotEmpty(first.Hook.Id);
        Assert.Equal(first.Hook.Id, second.Hook.Id);
        Assert.Contains(first.Hook.Id, _fx.Read(_fx.ProjectFile));
    }

    [Fact]
    public async Task The_global_layer_comes_first_then_the_project_layer_and_identical_commands_are_not_deduplicated()
    {
        _fx.WriteFile(_fx.GlobalFile, """{"hooks":{"Stop":[{"id":"x","command":"notify"}]}}""");
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"id":"x","command":"notify"}]}}""");

        var listed = await _service.ListAsync(HookFixture.Workspace);

        Assert.Equal([HookScope.Global, HookScope.Project], listed.Select(i => i.Scope));
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task Without_a_project_only_the_global_layer_is_listed()
    {
        _fx.WriteFile(_fx.GlobalFile, """{"hooks":{"Stop":[{"id":"g","command":"x"}]}}""");
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"id":"p","command":"y"}]}}""");

        Assert.Equal(["g"], (await _service.ListAsync(null)).Select(i => i.Hook.Id));
    }

    // ───────────────────────── 保存 ─────────────────────────

    [Fact]
    public async Task Saving_mints_an_id_writes_the_file_and_keeps_the_permission_rules_in_it()
    {
        _fx.WriteFile(_fx.ProjectFile, """{"version":1,"permissions":{"allow":["Read"],"ask":[],"deny":[]}}""");

        var saved = await _service.SaveAsync(Save(HookScope.Project, NewHook("echo guard") with { Matcher = "Bash" }));

        Assert.NotEmpty(saved.Hook.Id);
        var text = _fx.Read(_fx.ProjectFile);
        Assert.Contains("\"allow\"", text);
        Assert.Contains("echo guard", text);
        Assert.Equal(saved.Hook.Id, (await _service.ListAsync(HookFixture.Workspace)).Single().Hook.Id);
    }

    [Fact]
    public async Task Saving_the_same_id_again_replaces_it_and_moving_it_to_another_event_leaves_one_copy()
    {
        var first = await _service.SaveAsync(Save(HookScope.Global, NewHook("v1")));
        await _service.SaveAsync(Save(HookScope.Global, NewHook("v2", HookEvents.PostToolUse, first.Hook.Id)));

        var hook = Assert.Single(await _service.ListAsync(null));
        Assert.Equal("v2", hook.Hook.Command);
        Assert.Equal(HookEvents.PostToolUse, hook.Hook.Event);
    }

    [Fact]
    public async Task An_invalid_draft_is_refused_with_a_stable_key_and_nothing_is_written()
    {
        var e = await Assert.ThrowsAsync<HookException>(() => _service.SaveAsync(Save(HookScope.Global, NewHook("  "))));

        Assert.Equal(HookErrorKeys.Invalid, e.Key);
        Assert.Equal(HookErrorKeys.EmptyCommand, e.Params["reason"]);
        Assert.False(File.Exists(_fx.GlobalFile));
    }

    [Fact]
    public async Task A_prompt_draft_drops_a_stale_command_and_a_command_draft_drops_a_stale_prompt()
    {
        var prompt = await _service.SaveAsync(Save(HookScope.Global, NewHook("echo stale", HookEvents.Stop) with { Kind = HookKind.Prompt, Prompt = "Done?" }));
        var command = await _service.SaveAsync(Save(HookScope.Global, NewHook("echo ok", HookEvents.PostToolUse) with { Prompt = "left over" }));

        Assert.Null(prompt.Hook.Command);
        Assert.Null(command.Hook.Prompt);
        Assert.DoesNotContain("echo stale", _fx.Read(_fx.GlobalFile));
    }

    [Fact]
    public async Task A_provider_without_a_model_is_not_saved()
    {
        var saved = await _service.SaveAsync(Save(HookScope.Global, NewHook("x", HookEvents.Stop) with { Kind = HookKind.Prompt, Prompt = "Done?", ModelProviderId = "p1" }));

        Assert.Null(saved.Hook.ModelProviderId);
    }

    [Fact]
    public async Task At_most_fifty_hooks_per_event_per_file_the_fifty_first_is_refused()
    {
        for (var i = 0; i < HookLimits.MaxPerEvent; i++)
        {
            await _service.SaveAsync(Save(HookScope.Global, NewHook($"echo {i}")));
        }

        var e = await Assert.ThrowsAsync<HookException>(() => _service.SaveAsync(Save(HookScope.Global, NewHook("one too many"))));
        Assert.Equal(HookErrorKeys.Full, e.Key);
        // 更新已有的不算新增
        var existing = (await _service.ListAsync(null)).First().Hook;
        await _service.SaveAsync(Save(HookScope.Global, existing with { Command = "changed" }));
    }

    [Fact]
    public async Task A_settings_file_that_cannot_be_parsed_is_never_overwritten()
    {
        _fx.WriteFile(_fx.ProjectFile, "{ this is not json");

        var e = await Assert.ThrowsAsync<HookException>(() => _service.SaveAsync(Save(HookScope.Project, NewHook("x"))));
        var e2 = await Assert.ThrowsAsync<HookException>(() => _service.DeleteAsync(HookScope.Project, HookFixture.Workspace, "x"));

        Assert.Equal(HookErrorKeys.FileUnreadable, e.Key);
        Assert.Equal(HookErrorKeys.FileUnreadable, e2.Key);
        Assert.Equal("{ this is not json", _fx.Read(_fx.ProjectFile));
    }

    [Fact]
    public async Task Delete_removes_only_the_target_and_an_unknown_id_is_not_found()
    {
        var a = await _service.SaveAsync(Save(HookScope.Global, NewHook("a")));
        var b = await _service.SaveAsync(Save(HookScope.Global, NewHook("b")));

        await _service.DeleteAsync(HookScope.Global, null, a.Hook.Id);

        Assert.Equal([b.Hook.Id], (await _service.ListAsync(null)).Select(i => i.Hook.Id));
        var e = await Assert.ThrowsAsync<HookException>(() => _service.DeleteAsync(HookScope.Global, null, "nope"));
        Assert.Equal(HookErrorKeys.NotFound, e.Key);
    }

    [Fact]
    public async Task Concurrent_saves_to_the_same_file_do_not_lose_updates()
    {
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => _service.SaveAsync(Save(HookScope.Global, NewHook($"echo {i}")))));

        Assert.Equal(12, (await _service.ListAsync(null)).Count);
    }

    [Fact]
    public async Task Changes_raise_the_changed_event()
    {
        var raised = 0;
        _service.Changed += () => Interlocked.Increment(ref raised);

        var saved = await _service.SaveAsync(Save(HookScope.Global, NewHook("x")));
        await _service.SetEnabledAsync(HookScope.Global, null, saved.Hook.Id, false);
        await _service.DeleteAsync(HookScope.Global, null, saved.Hook.Id);

        Assert.Equal(3, raised);
    }

    // ───────────────────────── 环境变量:值单向写入 ─────────────────────────

    [Fact]
    public async Task An_edit_that_sends_no_value_keeps_the_stored_one_because_values_never_travel_back_to_the_controller()
    {
        var saved = await _service.SaveAsync(Save(HookScope.Global, NewHook("echo x"), [new HookEnvEntry("TOKEN", "s3cret-value"), new HookEnvEntry("MODE", "fast")]));

        await _service.SaveAsync(Save(HookScope.Global, saved.Hook with { Command = "echo y" }, [new HookEnvEntry("TOKEN", null), new HookEnvEntry("EXTRA", "1")]));

        var env = (await _service.ListAsync(null)).Single().Hook.Env;
        Assert.Equal("s3cret-value", env["TOKEN"]);
        Assert.Equal("1", env["EXTRA"]);
        Assert.False(env.ContainsKey("MODE")); // 非 null 的清单 = 恰好这些变量
    }

    [Fact]
    public async Task Leaving_the_env_list_out_keeps_the_variables_as_they_were()
    {
        var saved = await _service.SaveAsync(Save(HookScope.Global, NewHook("echo x"), [new HookEnvEntry("TOKEN", "s3cret-value")]));

        await _service.SaveAsync(Save(HookScope.Global, saved.Hook with { Description = "now with a description" }));

        Assert.Equal("s3cret-value", (await _service.ListAsync(null)).Single().Hook.Env["TOKEN"]);
    }

    [Fact]
    public async Task A_variable_without_a_value_and_without_a_stored_one_is_refused()
    {
        var e = await Assert.ThrowsAsync<HookException>(() => _service.SaveAsync(Save(HookScope.Global, NewHook("x"), [new HookEnvEntry("TOKEN", null)])));

        Assert.Equal(HookErrorKeys.BadEnv, e.Key);
    }

    [Fact]
    public async Task A_reserved_or_malformed_variable_name_is_refused_at_save()
    {
        var reserved = await Assert.ThrowsAsync<HookException>(() => _service.SaveAsync(Save(HookScope.Global, NewHook("x"), [new HookEnvEntry("NCW_RUN_ID", "v")])));

        Assert.Equal(HookErrorKeys.Invalid, reserved.Key);
        Assert.Equal(HookErrorKeys.BadEnv, reserved.Params["reason"]);
    }

    // ───────────────────────── 事件选择 ─────────────────────────

    private HookEventRequest Request(string hookEvent, string? tool = null, string? json = null, string session = "s1") => new(hookEvent, _fx.Env, _fx.RootPath, session, "r1", HookFixture.Workspace)
    {
        ToolInternalId = tool,
        ToolName = tool,
        ToolInput = json is null ? null : System.Text.Json.JsonDocument.Parse(json).RootElement.Clone(),
    };

    private string Marker(string name) => Path.Combine(_fx.Base, name);

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Hooks_run_global_then_project_then_runtime_in_that_order_and_the_first_block_short_circuits()
    {
        var order = Marker("order.txt");
        _fx.WriteFile(_fx.GlobalFile, """{"hooks":{"Stop":[{"id":"g","command":"echo g >> '@@order@@'"}]}}""".Replace("@@order@@", order));
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"id":"p","command":"echo p >> '@@order@@'; echo stop-here >&2; exit 2"},{"id":"p2","command":"echo p2 >> '@@order@@'"}]}}""".Replace("@@order@@", order));
        _service.Runtime.Register("s1", HookFixture.Command($"echo r >> '{order}'", HookEvents.Stop, "runtime"));

        var reports = await _service.RunEventAsync(Request(HookEvents.Stop), CancellationToken.None);

        Assert.Equal(["g", "p"], reports.Select(r => r.HookId));
        Assert.Equal(["g", "p"], File.ReadAllLines(order));
        Assert.True(reports[1].Blocks);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task An_env_value_echoed_in_a_blocking_reason_is_scrubbed_before_it_can_reach_the_model_or_the_log()
    {
        await _service.SaveAsync(Save(HookScope.Project, new HookDefinition
        {
            Id = "", Event = HookEvents.PreToolUse, Command = "echo \"blocked: $TOKEN\" >&2; exit 2", TimeoutMs = 10_000,
        }, [new HookEnvEntry("TOKEN", "s3cret-value")]));

        var reports = await _service.RunEventAsync(Request(HookEvents.PreToolUse, "Bash", """{"command":"ls"}"""), CancellationToken.None);

        var report = Assert.Single(reports);
        Assert.True(report.Blocks);
        Assert.Contains("blocked: ***", report.Reason);
        Assert.DoesNotContain("s3cret-value", report.Reason + report.Stderr + report.Stdout);
        Assert.DoesNotContain("s3cret-value", string.Join('\n', _log.Lines));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Runtime_hooks_run_after_the_file_hooks_and_belong_to_one_session()
    {
        var order = Marker("runtime.txt");
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"id":"p","command":"echo p >> '@@order@@'"}]}}""".Replace("@@order@@", order));
        _service.Runtime.Register("s1", HookFixture.Command($"echo r >> '{order}'", HookEvents.Stop, "rt"));

        await _service.RunEventAsync(Request(HookEvents.Stop, session: "s1"), CancellationToken.None);
        await _service.RunEventAsync(Request(HookEvents.Stop, session: "other"), CancellationToken.None);

        Assert.Equal(["p", "r", "p"], File.ReadAllLines(order));
    }

    [Fact]
    public void The_runtime_table_upserts_by_id_so_re_registering_never_piles_up_and_can_be_removed_and_cleared()
    {
        var table = new RuntimeHookTable();
        table.Register("s", HookFixture.Command("a", HookEvents.Stop, "one"));
        table.Register("s", HookFixture.Command("b", HookEvents.Stop, "one"));
        table.Register("s", HookFixture.Command("c", HookEvents.Stop, "two"));

        Assert.Equal(["b", "c"], table.For("s").Select(h => h.Command));
        table.Remove("s", "one");
        Assert.Equal(["c"], table.For("s").Select(h => h.Command));
        table.Clear("s");
        Assert.Empty(table.For("s"));
        Assert.Empty(table.For("never"));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Disabled_hooks_and_other_events_are_not_run()
    {
        var mark = Marker("mark.txt");
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"id":"off","enabled":false,"command":"echo off >> '@@mark@@'"},{"id":"on","command":"echo on >> '@@mark@@'"}],"PostToolUse":[{"id":"other","command":"echo other >> '@@mark@@'"}]}}""".Replace("@@mark@@", mark));

        await _service.RunEventAsync(Request(HookEvents.Stop), CancellationToken.None);

        Assert.Equal(["on"], File.ReadAllLines(mark));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_matcher_uses_permission_rule_syntax_and_only_applies_to_tool_events()
    {
        var mark = Marker("match.txt");
        _fx.WriteFile(_fx.ProjectFile, """
            {"hooks":{
              "PreToolUse":[
                {"id":"bash-any","matcher":"Bash","command":"echo bash-any >> '@@mark@@'"},
                {"id":"write-only","matcher":"Write","command":"echo write-only >> '@@mark@@'"},
                {"id":"bash-git","matcher":"Bash(git status:*)","command":"echo bash-git >> '@@mark@@'"},
                {"id":"every","command":"echo every >> '@@mark@@'"}],
              "Stop":[{"id":"stop-with-matcher","matcher":"Write","command":"echo stop >> '@@mark@@'"}]}}
            """.Replace("@@mark@@", mark));

        await _service.RunEventAsync(Request(HookEvents.PreToolUse, "Bash", """{"command":"git status"}"""), CancellationToken.None);
        await _service.RunEventAsync(Request(HookEvents.Stop), CancellationToken.None);

        // Stop 手里没有工具,写了 matcher 也无从匹配:忽略它,不让这条钩子静默失效
        Assert.Equal(["bash-any", "bash-git", "every", "stop"], File.ReadAllLines(mark));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task The_cap_of_fifty_applies_to_the_merged_total_across_layers()
    {
        var mark = Marker("cap.txt");
        var global = string.Join(',', Enumerable.Range(0, 40).Select(i => """{"id":"g@@i@@","command":"echo x >> '@@mark@@'"}""".Replace("@@i@@", i.ToString()).Replace("@@mark@@", mark)));
        var project = string.Join(',', Enumerable.Range(0, 40).Select(i => """{"id":"p@@i@@","command":"echo x >> '@@mark@@'"}""".Replace("@@i@@", i.ToString()).Replace("@@mark@@", mark)));
        _fx.WriteFile(_fx.GlobalFile, "{\"hooks\":{\"Stop\":[" + global + "]}}");
        _fx.WriteFile(_fx.ProjectFile, "{\"hooks\":{\"Stop\":[" + project + "]}}");

        var reports = await _service.RunEventAsync(Request(HookEvents.Stop), CancellationToken.None);

        Assert.Equal(50, reports.Count);
        Assert.Equal(50, File.ReadAllLines(mark).Length);
    }

    [Fact]
    public async Task With_no_hooks_configured_nothing_runs_and_an_unreadable_layer_is_skipped_with_a_diagnostic_line()
    {
        Assert.Empty(await _service.RunEventAsync(Request(HookEvents.Stop), CancellationToken.None));

        _fx.WriteFile(_fx.ProjectFile, "{ broken");
        Assert.Empty(await _service.RunEventAsync(Request(HookEvents.Stop), CancellationToken.None)); // 手改坏了 = 什么都没配,不拦运行
    }

    // ───────────────────────── 诊断 ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_failing_hook_does_not_stop_anything_but_lands_in_the_diagnostics_with_stable_keys()
    {
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"id":"bad","command":"echo oops >&2; exit 3"},{"id":"slow","command":"sleep 5","timeout":0.3}]}}""");

        var reports = await _service.RunEventAsync(Request(HookEvents.Stop), CancellationToken.None);

        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.False(r.Blocks));
        var diagnostics = await _service.DiagnosticsAsync(HookFixture.Workspace);
        Assert.Contains(diagnostics, d => d.Key == HookDiagnosticKeys.Command && d.Params!["code"] == "3" && d.Params["detail"].Contains("oops", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Key == HookDiagnosticKeys.Timeout);
        Assert.Contains(_log.Lines, l => l.Contains("[hook]", StringComparison.Ordinal));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_block_with_exit_two_is_not_a_failure_so_it_is_not_in_the_diagnostics()
    {
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"id":"gate","command":"echo no >&2; exit 2"}]}}""");

        await _service.RunEventAsync(Request(HookEvents.Stop), CancellationToken.None);

        Assert.Empty(await _service.DiagnosticsAsync(HookFixture.Workspace));
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Environment_variable_values_are_scrubbed_from_the_diagnostics_even_if_the_script_echoes_them()
    {
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"id":"leaky","command":"echo \"token is $TOKEN\" >&2; exit 5","env":{"TOKEN":"sk-very-secret-123"}}]}}""");

        await _service.RunEventAsync(Request(HookEvents.Stop), CancellationToken.None);

        var diagnostics = await _service.DiagnosticsAsync(HookFixture.Workspace);
        var json = string.Join('|', diagnostics.Select(d => d.Message + string.Join(',', d.Params?.Values ?? [])));
        Assert.DoesNotContain("sk-very-secret-123", json);
        Assert.Contains("token is ***", json);
    }

    [Fact]
    public async Task The_diagnostics_report_unreadable_files_and_the_count_of_entries_that_were_dropped()
    {
        _fx.WriteFile(_fx.GlobalFile, "{ not json");
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"id":"ok","command":"x"},{"id":"bad","command":""},{"id":"bad2","type":"webhook"}]}}""");

        var diagnostics = await _service.DiagnosticsAsync(HookFixture.Workspace);

        Assert.Contains(diagnostics, d => d.Key == HookDiagnosticKeys.Unreadable && d.Path == _fx.GlobalFile);
        var invalid = Assert.Single(diagnostics, d => d.Key == HookDiagnosticKeys.Invalid);
        Assert.Equal("2", invalid.Params!["count"]);
        Assert.Equal(_fx.ProjectFile, invalid.Path);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Only_the_most_recent_twenty_failures_are_kept()
    {
        _fx.WriteFile(_fx.ProjectFile, """{"hooks":{"Stop":[{"id":"bad","command":"exit 9"}]}}""");

        for (var i = 0; i < 25; i++)
        {
            await _service.RunEventAsync(Request(HookEvents.Stop), CancellationToken.None);
        }

        Assert.Equal(20, (await _service.DiagnosticsAsync(HookFixture.Workspace)).Count(d => d.Key == HookDiagnosticKeys.Command));
    }

    // ───────────────────────── 试运行 ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Test_runs_the_draft_not_what_is_on_disk_with_the_sample_payload()
    {
        var report = await _service.TestAsync(new HookTestRequest(HookScope.Project, HookFixture.Workspace, NewHook("cat", HookEvents.PreToolUse)));

        Assert.Equal(0, report.ExitCode);
        Assert.Contains("\"command\":\"echo hello\"", report.Stdout);
        Assert.Contains("\"sessionId\":\"test\"", report.Stdout);
        Assert.False(File.Exists(_fx.ProjectFile)); // 试运行不写盘
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Test_without_a_project_runs_on_the_device_itself()
    {
        var report = await _service.TestAsync(new HookTestRequest(HookScope.Global, null, NewHook("echo ran", HookEvents.Stop)));

        Assert.Equal("ran", report.AdditionalContext);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Test_reports_the_block_the_timeout_and_the_duration_and_scrubs_env_values_from_the_output()
    {
        var blocked = await _service.TestAsync(new HookTestRequest(HookScope.Project, HookFixture.Workspace, NewHook("echo no >&2; exit 2")));
        var slow = await _service.TestAsync(new HookTestRequest(HookScope.Project, HookFixture.Workspace, NewHook("sleep 5") with { TimeoutMs = 300 }));
        var leaky = await _service.TestAsync(new HookTestRequest(HookScope.Project, HookFixture.Workspace, NewHook("echo $TOKEN; echo $TOKEN >&2"), [new HookEnvEntry("TOKEN", "sk-very-secret-123")]));

        Assert.True(blocked.Blocks);
        Assert.Equal("no", blocked.Reason);
        Assert.Equal(HookOutcome.Timeout, slow.Outcome);
        Assert.True(slow.DurationMs >= 250);
        Assert.DoesNotContain("sk-very-secret-123", leaky.Stdout + leaky.Stderr + leaky.AdditionalContext);
    }

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task Test_can_reuse_the_stored_values_of_an_existing_hook_by_sending_no_value()
    {
        var saved = await _service.SaveAsync(Save(HookScope.Global, NewHook("echo x"), [new HookEnvEntry("TOKEN", "abcd1234")]));

        var report = await _service.TestAsync(new HookTestRequest(HookScope.Global, null, NewHook("test \"$TOKEN\" = abcd1234 && echo same"), [new HookEnvEntry("TOKEN", null)], saved.Hook.Id));

        Assert.Equal("same", report.AdditionalContext);
    }

    [Fact]
    public async Task Test_of_a_prompt_hook_only_previews_it_never_calls_a_model()
    {
        var evaluator = new CountingEvaluator();
        await using var service = _fx.NewService(evaluator);

        var report = await service.TestAsync(new HookTestRequest(HookScope.Global, null,
            NewHook("", HookEvents.Stop) with { Kind = HookKind.Prompt, Command = null, Prompt = "Check: $ARGUMENTS", TimeoutMs = 30_000 }));

        Assert.Equal(0, evaluator.Calls);
        Assert.StartsWith("Check: {", report.Preview);
        Assert.Contains("\"event\":\"Stop\"", report.Preview);
    }

    private sealed class CountingEvaluator : IHookEvaluator
    {
        public int Calls;

        public ValueTask<EvaluationVerdict> EvaluateAsync(EvaluationRequest request, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(new EvaluationVerdict(EvaluationKind.Met));
        }
    }

    [Fact]
    public async Task Test_refuses_an_invalid_draft()
    {
        var e = await Assert.ThrowsAsync<HookException>(() => _service.TestAsync(new HookTestRequest(HookScope.Global, null, NewHook(""))));

        Assert.Equal(HookErrorKeys.Invalid, e.Key);
    }

    // ───────────────────────── 不等待的事件 ─────────────────────────

    [Fact(Skip = "POSIX only", SkipUnless = nameof(Platform.IsPosix), SkipType = typeof(Platform))]
    public async Task A_fire_and_forget_event_can_be_drained_and_is_cancelled_with_the_service()
    {
        var mark = Marker("fire.txt");
        _fx.WriteFile(_fx.GlobalFile, """{"hooks":{"Notification":[{"id":"n","command":"echo hi > '@@mark@@'"}]}}""".Replace("@@mark@@", mark));

        _service.Fire(token => _service.RunEventAsync(Request(HookEvents.Notification), token));
        await _service.DrainAsync();

        Assert.Equal("hi", File.ReadAllText(mark).Trim());
    }
}
