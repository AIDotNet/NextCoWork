namespace Ncw.Agent.Hooks.Tests;

/// <summary>
/// 设置文件 hooks 段 ⇄ 列表。移植自 2.x <c>hook-load.test.ts</c>(归一化 / 列表与合并 / 增删改 / prompt 型钩子四组)。
/// 「按 HOOK_EVENTS 顺序」「两层不去重」在 3.0 里是 <see cref="HookSettingsFile"/> 与 <see cref="HooksService"/> 各自的规则,后者的用例在 <see cref="HooksServiceTests"/>。
/// </summary>
public sealed class HookSettingsFileTests
{
    private static IReadOnlyList<HookDefinition> Read(string json) => HookSettingsFile.Parse(json).Hooks;

    private static string Body(HookDefinition h) => h.Kind == HookKind.Prompt ? h.Prompt! : h.Command!;

    private static HookDefinition Cmd(string id = "h1", string hookEvent = "PreToolUse", string command = "echo hi", bool enabled = true, int timeoutMs = 10_000) =>
        new() { Id = id, Event = hookEvent, Command = command, Enabled = enabled, TimeoutMs = timeoutMs };

    // ───────────────────────── 归一化 ─────────────────────────

    [Fact]
    public void A_missing_enabled_means_on_so_hand_written_entries_need_not_carry_it()
    {
        Assert.True(Read("""{"hooks":{"PreToolUse":[{"id":"a","command":"x"}]}}""")[0].Enabled);
    }

    [Fact]
    public void Timeout_is_seconds_in_the_file_and_milliseconds_in_memory()
    {
        Assert.Equal(30_000, Read("""{"hooks":{"PreToolUse":[{"id":"a","command":"x","timeout":30}]}}""")[0].TimeoutMs);
    }

    [Fact]
    public void A_timeout_over_the_ceiling_is_clamped()
    {
        Assert.Equal(600_000, Read("""{"hooks":{"PreToolUse":[{"id":"a","command":"x","timeout":99999}]}}""")[0].TimeoutMs);
    }

    [Fact]
    public void PreToolUse_has_a_shorter_default_timeout_because_it_sits_on_every_tool_call()
    {
        var pre = Read("""{"hooks":{"PreToolUse":[{"id":"a","command":"x"}]}}""")[0];
        var stop = Read("""{"hooks":{"Stop":[{"id":"b","command":"x"}]}}""")[0];

        Assert.Equal(10_000, pre.TimeoutMs);
        Assert.Equal(60_000, stop.TimeoutMs);
    }

    [Fact]
    public void An_entry_that_cannot_be_read_is_dropped_and_the_rest_carry_on_instead_of_voiding_the_file()
    {
        var hooks = Read("""
            {"hooks":{"PreToolUse":[
              {"id":"a","command":"ok"},
              {"id":"b","command":""},
              {"id":"c","command":"x","matcher":"Bash(((("},
              {"id":"d","command":"also-ok"}
            ]}}
            """);

        Assert.Equal(["ok", "also-ok"], hooks.Select(Body));
    }

    [Fact]
    public void The_raw_count_minus_the_recognised_count_is_what_the_diagnostics_report()
    {
        var parsed = HookSettingsFile.Parse("""{"hooks":{"PreToolUse":[{"id":"a","command":"ok"},{"id":"b","command":""}],"Bogus":[{"command":"x"}]}}""");

        Assert.Equal(3, parsed.RawCount);
        Assert.Single(parsed.Hooks);
    }

    [Fact]
    public void An_unknown_event_name_drops_the_whole_group()
    {
        Assert.Empty(Read("""{"hooks":{"NotAnEvent":[{"id":"a","command":"x"}]}}"""));
    }

    [Fact]
    public void The_claude_code_nested_shape_is_flattened_so_pasting_a_section_works()
    {
        var hooks = Read("""{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"a.sh"},{"type":"command","command":"b.sh"}]}]}}""");

        Assert.Equal(["a.sh", "b.sh"], hooks.Select(Body));
        Assert.All(hooks, h => Assert.Equal("Bash", h.Matcher));
    }

    [Fact]
    public void More_than_the_per_event_limit_is_truncated()
    {
        var many = string.Join(',', Enumerable.Range(0, 80).Select(i => $$"""{"id":"h{{i}}","command":"x"}"""));

        Assert.Equal(50, Read("{\"hooks\":{\"PreToolUse\":[" + many + "]}}").Count);
    }

    [Theory]
    [InlineData("\"nonsense\"")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    public void A_garbage_hooks_section_reads_as_empty_and_never_throws(string section)
    {
        var parsed = HookSettingsFile.Parse($$"""{"hooks":{{section}}}""");

        Assert.Empty(parsed.Hooks);
        Assert.False(parsed.Unreadable);
    }

    [Fact]
    public void A_file_that_is_not_json_is_unreadable_and_a_missing_file_is_just_empty()
    {
        Assert.True(HookSettingsFile.Parse("{ not json").Unreadable);
        Assert.True(HookSettingsFile.Parse("[1]").Unreadable);
        Assert.False(HookSettingsFile.Parse(null).Unreadable);
        Assert.False(HookSettingsFile.Parse("   ").Unreadable);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated_because_people_edit_this_file_by_hand()
    {
        Assert.Single(Read("{ /* mine */ \"hooks\": { \"Stop\": [ { \"id\": \"a\", \"command\": \"x\", }, ], }, }"));
    }

    // ───────────────────────── 列表 ─────────────────────────

    [Fact]
    public void Listing_follows_the_event_order_not_the_order_keys_were_written()
    {
        var hooks = Read("""{"hooks":{"Stop":[{"id":"s","command":"x"}],"UserPromptSubmit":[{"id":"u","command":"y"}]}}""");

        Assert.Equal(["u", "s"], hooks.Select(h => h.Id));
    }

    // ───────────────────────── 增删改 ─────────────────────────

    [Fact]
    public void Upsert_overwrites_by_id()
    {
        var after = HookSettingsFile.Upsert(HookSettingsFile.Upsert([], Cmd(command: "v1")), Cmd(command: "v2"));

        Assert.Equal("v2", Assert.Single(after).Command);
    }

    [Fact]
    public void Changing_the_event_removes_it_from_the_old_event_so_it_never_sits_under_two()
    {
        var before = HookSettingsFile.Upsert([], Cmd(hookEvent: "PreToolUse"));
        var after = HookSettingsFile.Upsert(before, Cmd(hookEvent: "PostToolUse"));

        var hook = Assert.Single(after);
        Assert.Equal("PostToolUse", hook.Event);
    }

    [Fact]
    public void Remove_deletes_only_the_target()
    {
        var hooks = HookSettingsFile.Upsert(HookSettingsFile.Upsert([], Cmd("a")), Cmd("b"));

        Assert.Equal(["b"], HookSettingsFile.Remove(hooks, "a").Select(h => h.Id));
    }

    [Fact]
    public void Enabled_is_dropped_from_the_file_when_on_and_written_false_when_off()
    {
        var on = HookSettingsFile.Render(null, [Cmd(enabled: true)]);
        var off = HookSettingsFile.Render(null, HookSettingsFile.SetEnabled([Cmd()], "h1", false));

        Assert.DoesNotContain("\"enabled\"", on);
        Assert.Contains("\"enabled\": false", off);
    }

    [Fact]
    public void Timeout_is_written_back_in_seconds()
    {
        Assert.Contains("\"timeout\": 45", HookSettingsFile.Render(null, [Cmd(timeoutMs: 45_000)]));
    }

    [Fact]
    public void A_command_hook_does_not_write_type_because_it_is_the_default_and_writing_it_would_reshuffle_old_files()
    {
        Assert.DoesNotContain("\"type\"", HookSettingsFile.Render(null, [Cmd()]));
    }

    [Fact]
    public void Hand_written_entries_without_an_id_get_one_minted_so_toggles_do_not_hit_somebody_elses_row()
    {
        var hooks = Read("""{"hooks":{"PreToolUse":[{"command":"no-id-here"}]}}""");
        Assert.True(HookSettingsFile.HasMissingIds(hooks));

        var n = 0;
        var filled = HookSettingsFile.AssignIds(hooks, () => $"minted-{++n}");

        Assert.Equal("minted-1", filled[0].Id);
        Assert.False(HookSettingsFile.HasMissingIds(filled));
    }

    [Fact]
    public void Entries_that_already_have_an_id_keep_it()
    {
        var hooks = Read("""{"hooks":{"PreToolUse":[{"id":"keep","command":"x"}]}}""");

        Assert.Equal("keep", HookSettingsFile.AssignIds(hooks, () => "new")[0].Id);
    }

    // ───────────────────────── prompt 型 ─────────────────────────

    [Fact]
    public void An_old_file_with_no_type_still_parses_as_a_command_because_the_default_cannot_change()
    {
        Assert.Equal(HookKind.Command, Read("""{"hooks":{"Stop":[{"id":"a","command":"x"}]}}""")[0].Kind);
    }

    [Fact]
    public void A_prompt_hook_reads_in_with_its_own_thirty_second_default_timeout()
    {
        var hook = Read("""{"hooks":{"Stop":[{"id":"a","type":"prompt","prompt":"Is it done?"}]}}""")[0];

        Assert.Equal(HookKind.Prompt, hook.Kind);
        Assert.Equal(30_000, hook.TimeoutMs);
        Assert.Equal("Is it done?", hook.Prompt);
    }

    [Theory]
    [InlineData("""{"id":"a","type":"prompt"}""")]
    [InlineData("""{"id":"a","type":"prompt","prompt":"   "}""")]
    [InlineData("""{"id":"a","type":"command"}""")]
    [InlineData("""{"id":"a","type":"webhook","command":"x"}""")]
    [InlineData("""{"id":"a","type":5,"command":"x"}""")]
    public void An_entry_missing_its_body_or_of_an_unknown_type_is_dropped_never_built_as_a_hook_that_cannot_run(string entry)
    {
        Assert.Empty(Read("{\"hooks\":{\"Stop\":[" + entry + "]}}"));
    }

    [Fact]
    public void A_prompt_hook_round_trips()
    {
        var original = new HookDefinition
        {
            Id = "g1",
            Event = "Stop",
            Kind = HookKind.Prompt,
            Prompt = "Only done when the README version matches",
            Model = "sonnet",
            ModelProviderId = "p1",
            TimeoutMs = 30_000,
        };

        var back = Read(HookSettingsFile.Render(null, [original]))[0];

        Assert.Equal(original, back);
    }

    [Fact]
    public void A_provider_without_a_model_alias_is_dropped_because_it_cannot_form_a_binding()
    {
        var hook = Read("""{"hooks":{"Stop":[{"id":"a","type":"prompt","prompt":"x","modelProviderId":"p1"}]}}""")[0];

        Assert.Null(hook.ModelProviderId);
    }

    // ───────────────────────── env(3.0 新增) ─────────────────────────

    [Fact]
    public void Environment_variables_round_trip_and_bad_ones_make_the_whole_entry_unreadable()
    {
        var hook = Cmd() with { Env = new Dictionary<string, string> { ["TOKEN"] = "s3cret", ["LANG_X"] = "zh" } };
        var back = Read(HookSettingsFile.Render(null, [hook]))[0];
        Assert.Equal("s3cret", back.Env["TOKEN"]);

        // 名字不合法 / 占用保留前缀:带着一半环境变量去跑一条脚本,比不跑更危险
        Assert.Empty(Read("""{"hooks":{"Stop":[{"id":"a","command":"x","env":{"1BAD":"v"}}]}}"""));
        Assert.Empty(Read("""{"hooks":{"Stop":[{"id":"a","command":"x","env":{"NCW_RUN_ID":"v"}}]}}"""));
    }

    // ───────────────────────── 写:保留别的键 ─────────────────────────

    [Fact]
    public void Writing_hooks_keeps_every_other_key_in_the_file_including_the_permission_rules()
    {
        const string Existing = """
            {
              "version": 1,
              "permissions": { "allow": ["Bash(git status:*)"], "deny": [] },
              "future": { "x": 1 },
              "hooks": { "Stop": [ { "id": "old", "command": "x" } ] }
            }
            """;

        var rendered = HookSettingsFile.Render(Existing, HookSettingsFile.Upsert(Read(Existing), Cmd("new", "PreToolUse", "guard")));

        Assert.Contains("\"Bash(git status:*)\"", rendered);
        Assert.Contains("\"future\"", rendered);
        var hooks = Read(rendered);
        Assert.Equal(["new", "old"], hooks.Select(h => h.Id));
    }

    [Fact]
    public void A_non_ascii_command_is_written_as_is_not_as_escapes()
    {
        Assert.Contains("echo 你好", HookSettingsFile.Render(null, [Cmd(command: "echo 你好")]));
    }
}
