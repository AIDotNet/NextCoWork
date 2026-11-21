using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Subagents;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Subagents.Tests;

/// <summary>
/// 子代理定义扫描。移植自 2.x 的 <c>agent-load.test.ts</c>(<c>AgentRegistry</c> 那组在 <see cref="AgentRegistryTests"/>)。
/// <b>用真临时目录,不打桩文件系统</b>——这个文件里最重要的两条(软链逃逸、空工具表作废)都是安全判断,而路径守卫挡住第一条靠的就是真的 realpath。
/// 加载器的契约是<b>永不抛</b>,所以几乎每条用例都同时断言两件事:「这条没进来」和「用户能知道它为什么没进来」(诊断 key)。
/// ★ 还有一条贯穿全文件的隐式断言:<b>内建的那条永远在</b>——「至少有一个子代理可派」如果取决于用户机器上有没有 agents 目录,
/// 那么 <c>Task</c> 工具就会在半数机器上变成一个必然失败的工具。
/// </summary>
public sealed class AgentLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncw-agents-{Guid.NewGuid():N}");
    private readonly LocalEnvironment _env = new("dev");
    private readonly string _global;
    private readonly string _project;

    public AgentLoaderTests()
    {
        _global = Path.Combine(_root, "global", "agents");
        _project = Path.Combine(_root, "project", ".next-cowork", "agents");
        Directory.CreateDirectory(_global);
        Directory.CreateDirectory(_project);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static void Put(string dir, string file, string content) => File.WriteAllText(Path.Combine(dir, file), content);

    /// <summary>一份最小的定义。★ 刻意不写 name:——名字由文件名给,和 CC 一致。</summary>
    private static string Md(string? description, string body = "You own this.") =>
        $"---\n{(description is null ? "" : $"description: {description}\n")}---\n{body}\n";

    private Task<AgentScanResult> Scan() => AgentLoader.ScanAsync(new AgentScanInput(_env, LocalEnvironment.PathOf(_global), _env, LocalEnvironment.PathOf(_project)));

    private static List<string> Loaded(AgentScanResult result) => [.. result.Agents.Where(a => a.Source.Kind != AgentSourceKind.Builtin).Select(a => a.Name)];

    /// <summary>诊断里有没有这件事——断言「用户能知道原因」(稳定 key),不是断言逐字文案。</summary>
    private static bool Said(AgentScanResult result, string key) => result.Diagnostics.Any(d => d.Key == key);

    private static string[] BuiltinNames => [.. BuiltinAgents.All.Select(a => a.Name)];

    // ───────────────────────── 基本形状 ─────────────────────────

    [Fact]
    public async Task Reads_the_name_description_and_role_prompt_and_marks_the_source()
    {
        Put(_global, "researcher.md", "---\ndescription: read-only research\n---\nYou only look.\n");

        var agent = (await Scan()).Agents.Single(a => a.Name == "researcher");

        Assert.Equal("read-only research", agent.Description);
        Assert.Equal("You only look.", agent.Prompt);
        Assert.Equal(AgentSourceKind.Global, agent.Source.Kind);
        Assert.Contains("researcher.md", agent.Source.Path);
        // 四个可选字段都没写:省略,不是占位值
        Assert.Null(agent.Tools);
        Assert.Null(agent.Model);
        Assert.Null(agent.PermissionMode);
        Assert.Null(agent.Thinking);
    }

    [Fact]
    public async Task Thinking_is_case_insensitive_and_a_valid_level_lands_on_the_definition()
    {
        Put(_global, "deep.md", "---\ndescription: think harder\nthinking: HIGH\n---\nSlowly.\n");

        Assert.Equal(ThinkingLevel.High, (await Scan()).Agents.Single(a => a.Name == "deep").Thinking);
    }

    [Fact]
    public async Task An_unrecognised_thinking_value_still_loads_the_agent_mentions_it_in_a_diagnostic_and_leaves_the_field_empty()
    {
        Put(_global, "deep.md", "---\ndescription: think harder\nthinking: on\n---\nSlowly.\n");

        var result = await Scan();

        var agent = result.Agents.Single(a => a.Name == "deep");
        Assert.Null(agent.Thinking);
        Assert.Contains(result.Diagnostics, d => d.Key == AgentDiagnosticKeys.ThinkingInvalid && d.Params!["value"] == "on");
    }

    [Fact]
    public async Task Invariant_with_no_agent_files_at_all_the_builtin_ones_are_still_there()
    {
        var result = await Scan();

        Assert.Equal(BuiltinNames, result.Agents.Select(a => a.Name));
        Assert.All(result.Agents, a => Assert.Equal(AgentSourceKind.Builtin, a.Source.Kind));
    }

    [Fact]
    public async Task Missing_directories_are_not_an_error_having_no_agents_directory_is_the_norm()
    {
        var result = await AgentLoader.ScanAsync(new AgentScanInput(_env, LocalEnvironment.PathOf(Path.Combine(_root, "does-not-exist")), null, null));

        Assert.Equal(BuiltinNames, result.Agents.Select(a => a.Name));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task On_a_name_clash_the_project_wins()
    {
        Put(_global, "reviewer.md", Md("the global one"));
        Put(_project, "reviewer.md", Md("the project one"));

        var result = await Scan();

        var agent = result.Agents.Single(a => a.Name == "reviewer");
        Assert.Equal("the project one", agent.Description);
        Assert.Equal(AgentSourceKind.Project, agent.Source.Kind);
        Assert.Equal(["reviewer"], Loaded(result));
    }

    [Fact]
    public async Task Invariant_a_user_file_can_override_a_builtin_agent_of_the_same_name()
    {
        Put(_project, "general-purpose.md", Md("my own general agent"));

        var result = await Scan();

        // 覆盖的是那一条,其余内建照旧:总数不变。
        Assert.Equal(BuiltinAgents.All.Count, result.Agents.Count);
        var agent = result.Agents.Single(a => a.Name == BuiltinAgents.GeneralPurpose.Name);
        Assert.Equal("my own general agent", agent.Description);
        Assert.Equal(AgentSourceKind.Project, agent.Source.Kind);
    }

    [Fact]
    public async Task Invariant_when_the_override_is_broken_what_remains_is_the_working_builtin()
    {
        Put(_project, "general-purpose.md", Md(null)); // 缺 description → 作废

        var result = await Scan();

        Assert.Equal(BuiltinAgents.All.Count, result.Agents.Count);
        Assert.Equal(AgentSourceKind.Builtin, result.Agents.Single(a => a.Name == BuiltinAgents.GeneralPurpose.Name).Source.Kind);
        Assert.True(Said(result, AgentDiagnosticKeys.DescriptionMissing));
    }

    [Fact]
    public async Task The_name_in_the_frontmatter_wins_over_the_file_name_and_says_so()
    {
        Put(_global, "old.md", "---\nname: new-name\ndescription: renamed\n---\nbody\n");

        var result = await Scan();

        Assert.Equal(["new-name"], Loaded(result));
        Assert.True(Said(result, AgentDiagnosticKeys.NameMismatch));
    }

    // ───────────────────────── 这些不进表,而且要说明原因 ─────────────────────────

    [Fact]
    public async Task Directories_are_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_global, "somedir.md"));

        Assert.Empty(Loaded(await Scan()));
    }

    [Fact]
    public async Task Non_markdown_files_are_skipped_silently_a_readme_is_not_an_error()
    {
        Put(_global, "README.txt", Md("must not be read"));

        var result = await Scan();

        Assert.Empty(Loaded(result));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task An_invalid_file_name_is_skipped_with_an_explanation()
    {
        Put(_global, "Bad Name.md", Md("upper case and a space"));

        var result = await Scan();

        Assert.Empty(Loaded(result));
        Assert.True(Said(result, AgentDiagnosticKeys.FileNameInvalid));
    }

    [Fact]
    public async Task An_invalid_name_in_the_frontmatter_drops_the_whole_agent()
    {
        Put(_global, "ok.md", "---\nname: NOT OK\ndescription: has one\n---\nbody\n");

        var result = await Scan();

        Assert.Empty(Loaded(result));
        Assert.True(Said(result, AgentDiagnosticKeys.NameInvalid));
    }

    [Fact]
    public async Task Invariant_a_missing_description_drops_the_whole_agent_and_never_borrows_the_name()
    {
        Put(_global, "nodesc.md", Md(null));

        var result = await Scan();

        Assert.Empty(Loaded(result));
        Assert.True(Said(result, AgentDiagnosticKeys.DescriptionMissing));
    }

    [Fact]
    public async Task An_empty_body_drops_the_whole_agent_a_role_without_a_prompt_means_nothing()
    {
        Put(_global, "empty.md", "---\ndescription: has one\n---\n   \n");

        var result = await Scan();

        Assert.Empty(Loaded(result));
        Assert.True(Said(result, AgentDiagnosticKeys.PromptEmpty));
    }

    [Fact]
    public async Task Invariant_a_symlink_out_of_the_agents_directory_is_refused()
    {
        var outside = Path.Combine(_root, "outside.md");
        File.WriteAllText(outside, Md("I live outside"));
        File.CreateSymbolicLink(Path.Combine(_global, "evil.md"), outside);

        var result = await Scan();

        Assert.Empty(Loaded(result));
        Assert.True(Said(result, AgentDiagnosticKeys.OutsideRoot));
    }

    // ───────────────────────── tools:——三种结局各不相同 ─────────────────────────

    [Fact]
    public async Task No_tools_line_means_null_and_inherits_every_tool_of_the_parent()
    {
        Put(_global, "a.md", Md("no tools line"));

        Assert.Null((await Scan()).Agents.Single(x => x.Name == "a").Tools);
    }

    [Fact]
    public async Task Invariant_a_Claude_Code_agent_file_pasted_as_is_works()
    {
        Put(_global, "cc.md", "---\ndescription: copied over\ntools: Read, Grep, Glob\n---\nbody\n");

        Assert.Equal(["Read", "Grep", "Glob"], (await Scan()).Agents.Single(x => x.Name == "cc").Tools);
    }

    [Fact]
    public async Task Lower_case_yaml_arrays_and_MultiEdit_are_all_understood_and_deduplicated()
    {
        Put(_global, "b.md", "---\ndescription: d\ntools: [read, multiedit, edit, ls]\n---\nbody\n");

        // multiedit 和 edit 都归一化成 Edit,去重之后只剩一个
        Assert.Equal(["Read", "Edit", "LS"], (await Scan()).Agents.Single(x => x.Name == "b").Tools);
    }

    [Fact]
    public async Task A_partly_recognised_list_uses_the_part_that_is_recognised_and_reports_the_rest_never_silently()
    {
        Put(_global, "c.md", "---\ndescription: d\ntools: Read, Telepathy\n---\nbody\n");

        var result = await Scan();

        Assert.Equal(["Read"], result.Agents.Single(x => x.Name == "c").Tools);
        Assert.Contains(result.Diagnostics, d => d.Key == AgentDiagnosticKeys.ToolsUnknown && d.Message.Contains("Telepathy"));
    }

    [Fact]
    public async Task Invariant_a_tools_line_of_which_nothing_is_recognised_drops_the_agent_an_empty_tool_list_makes_an_answer_up()
    {
        Put(_global, "d.md", "---\ndescription: d\ntools: Telepathy, Clairvoyance\n---\nbody\n");

        var result = await Scan();

        Assert.Empty(Loaded(result));
        var none = Assert.Single(result.Diagnostics, d => d.Key == AgentDiagnosticKeys.ToolsNone);
        // 诊断里要告诉用户怎么改,不能只说「不行」
        Assert.Contains("Delete the tools line", none.Message);
    }

    // ───────────────────────── permissionMode:——唯一一个认不出就作废的可选字段 ─────────────────────────

    [Fact]
    public async Task A_recognised_permission_mode_lands_on_the_definition()
    {
        Put(_global, "ro.md", "---\ndescription: d\npermissionMode: ask\n---\nbody\n");

        Assert.Equal(PermissionMode.Ask, (await Scan()).Agents.Single(x => x.Name == "ro").PermissionMode);
    }

    [Fact]
    public async Task The_permission_dash_mode_spelling_is_understood_too()
    {
        Put(_global, "ro2.md", "---\ndescription: d\npermission-mode: FULL\n---\nbody\n");

        Assert.Equal(PermissionMode.Full, (await Scan()).Agents.Single(x => x.Name == "ro2").PermissionMode);
    }

    [Fact]
    public async Task Invariant_an_unrecognised_permission_mode_drops_the_whole_agent_instead_of_ignoring_the_field()
    {
        Put(_global, "bad.md", "---\ndescription: d\npermissionMode: readonly\n---\nbody\n");

        var result = await Scan();

        Assert.Empty(Loaded(result));
        // 理由要说清楚:忽略它等于按父代理的档位跑,和作者本意正好相反
        Assert.Contains(result.Diagnostics, d => d.Key == AgentDiagnosticKeys.PermissionModeInvalid && d.Message.Contains("opposite of what the author meant"));
    }

    // ───────────────────────── 其余字段 ─────────────────────────

    [Fact]
    public async Task A_provider_id_without_a_model_is_dropped_and_a_bad_color_is_just_ignored()
    {
        Put(_global, "m.md", "---\ndescription: d\nmodelProviderId: p1\ncolor: chartreuse\n---\nbody\n");
        Put(_global, "n.md", "---\ndescription: d\nmodel: claude-sonnet-4\nmodelProviderId: p1\ncolor: BLUE\n---\nbody\n");

        var result = await Scan();

        var alone = result.Agents.Single(x => x.Name == "m");
        Assert.Null(alone.ModelProviderId);
        Assert.Null(alone.Color); // 装饰字段读错:不作废
        var both = result.Agents.Single(x => x.Name == "n");
        Assert.Equal(("claude-sonnet-4", "p1", "blue"), (both.Model, both.ModelProviderId, both.Color));
    }

    // ───────────────────────── 不可信输入 ─────────────────────────

    [Fact]
    public async Task Control_characters_in_the_description_are_sanitised_it_goes_verbatim_into_the_Task_tool_definition()
    {
        Put(_global, "x.md", "---\ndescription: clean\u0007ed\n---\nbody\n");

        var description = (await Scan()).Agents.Single(a => a.Name == "x").Description;

        Assert.DoesNotContain('\u0007', description);
        Assert.Contains("clean", description);
    }

    [Fact]
    public async Task Control_characters_in_the_role_prompt_are_sanitised_too_it_goes_into_the_system_prompt()
    {
        Put(_global, "y.md", "---\ndescription: d\n---\nYou do\u0007 it this way\n");

        Assert.DoesNotContain('\u0007', (await Scan()).Agents.Single(a => a.Name == "y").Prompt);
    }

    [Fact]
    public async Task An_oversized_role_prompt_is_truncated_not_stuffed_whole_into_the_system_prompt()
    {
        Put(_global, "big.md", $"---\ndescription: d\n---\n{new string('啊', 40_000)}\n");

        Assert.True((await Scan()).Agents.Single(a => a.Name == "big").Prompt.Length <= AgentLimits.PromptMax);
    }

    [Fact]
    public async Task Invariant_one_bad_file_does_not_fail_the_whole_scan_the_good_one_still_comes_in()
    {
        Put(_global, "good.md", Md("a good one"));
        Put(_global, "bad.md", Md(null));

        var result = await Scan();

        Assert.Equal(["good"], Loaded(result));
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public async Task A_description_over_the_limit_is_clamped_with_a_marker()
    {
        Put(_global, "long.md", $"---\ndescription: {new string('a', 900)}\n---\nbody\n");

        var description = (await Scan()).Agents.Single(a => a.Name == "long").Description;

        Assert.Equal(AgentLimits.DescriptionMax, description.Length);
        Assert.EndsWith("...", description);
    }

    [Fact]
    public async Task Frontmatter_notes_become_diagnostics_with_the_frontmatter_key()
    {
        Put(_global, "odd.md", "---\ndescription: d\nanchors: *x\n---\nbody\n");

        var result = await Scan();

        Assert.Contains(result.Diagnostics, d => d.Key == AgentDiagnosticKeys.Frontmatter);
        Assert.Equal(["odd"], Loaded(result)); // 不作废
    }

    // ───────────────────────── 环境 ─────────────────────────

    [Fact]
    public async Task A_disconnected_environment_is_not_the_same_as_no_agents_it_propagates_instead_of_being_swallowed()
    {
        var env = new DisconnectedEnvironment(_env);

        await Assert.ThrowsAsync<EnvironmentException>(() =>
            AgentLoader.ScanAsync(new AgentScanInput(null, null, env, LocalEnvironment.PathOf(_project))));
    }

    // ───────────────────────── Claude Code 兼容扩展字段 ─────────────────────────

    [Fact]
    public async Task Parses_disallowedTools_maxTurns_effort_background_and_isolation_worktree()
    {
        Put(_global, "cc-agent.md", """
            ---
            description: Isolated reviewer
            disallowedTools: Write, Edit
            maxTurns: 7
            effort: max
            background: true
            isolation: worktree
            ---
            Review carefully.
            """);

        var result = await Scan();
        var agent = result.Agents.Single(a => a.Name == "cc-agent");

        Assert.Equal(["Write", "Edit"], agent.DisallowedTools);
        Assert.Equal(7, agent.MaxTurns);
        Assert.Equal(ThinkingLevel.Max, agent.Thinking);
        Assert.True(agent.DefaultBackground);
        Assert.Equal(AgentIsolation.Worktree, agent.Isolation);
    }

    [Fact]
    public async Task Invalid_maxTurns_or_isolation_records_a_diagnostic_and_ignores_only_that_field()
    {
        Put(_global, "bad-meta.md", """
            ---
            description: Meta check
            maxTurns: 9999
            isolation: container
            ---
            Body text.
            """);

        var result = await Scan();
        var agent = result.Agents.Single(a => a.Name == "bad-meta");

        Assert.Null(agent.MaxTurns);
        Assert.Equal(AgentIsolation.None, agent.Isolation);
        Assert.Contains(result.Diagnostics, d => d.Key == AgentDiagnosticKeys.MaxTurnsInvalid);
        Assert.Contains(result.Diagnostics, d => d.Key == AgentDiagnosticKeys.IsolationInvalid);
    }
}
