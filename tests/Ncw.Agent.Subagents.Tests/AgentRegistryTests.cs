using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Subagents;

namespace Ncw.Agent.Subagents.Tests;

/// <summary>
/// 子代理注册表、目录服务(启停 / 列表 / 诊断)和内建定义。移植自 2.x 的 <c>agent-load.test.ts</c> 的 <c>AgentRegistry</c> 组,外加 <c>ipc/agents.ts</c> 的列表 / 启停语义。
/// </summary>
public sealed class AgentRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncw-agentreg-{Guid.NewGuid():N}");
    private readonly LocalEnvironment _env = new("dev");
    private readonly string _global;

    public AgentRegistryTests()
    {
        _global = Path.Combine(_root, "global", "agents");
        Directory.CreateDirectory(_global);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Md(string description) => $"---\ndescription: {description}\n---\nbody\n";

    private Task<AgentScanResult> Scan() => AgentLoader.ScanAsync(new AgentScanInput(_env, EnvPath.Posix(_global), null, null));

    private sealed class Source(LocalEnvironment env, string global) : IAgentScanSource
    {
        public ValueTask<AgentScanInput> InputForAsync(string workspaceId, CancellationToken ct) =>
            ValueTask.FromResult(new AgentScanInput(env, EnvPath.Posix(global), null, null));
    }

    private static string[] BuiltinNames => [.. BuiltinAgents.All.Select(a => a.Name)];

    [Fact]
    public void Invariant_a_fresh_registry_is_not_empty_the_first_Task_description_is_built_before_the_first_scan() =>
        Assert.Equal(BuiltinNames, new AgentRegistry().Names());

    [Fact]
    public async Task Builtin_agents_come_first_in_their_written_order_the_rest_by_name_so_the_tool_definition_does_not_jitter()
    {
        File.WriteAllText(Path.Combine(_global, "zeta.md"), Md("z"));
        File.WriteAllText(Path.Combine(_global, "alpha.md"), Md("a"));
        var registry = new AgentRegistry();

        var result = await Scan();
        registry.Replace(result.Agents, result.Diagnostics);

        Assert.Equal([.. BuiltinNames, "alpha", "zeta"], registry.Names());
    }

    [Fact]
    public async Task Invariant_general_purpose_is_the_first_line_alphabetical_order_would_push_it_behind_the_code_agents()
    {
        var registry = new AgentRegistry();
        var result = await Scan();
        registry.Replace(result.Agents, result.Diagnostics);

        Assert.Equal(BuiltinAgents.GeneralPurpose.Name, registry.Names()[0]);
    }

    [Fact]
    public void Invariant_every_tool_a_builtin_agent_names_is_recognised_verbatim_by_the_tool_name_table()
    {
        // 认不出的那一个会被加载器丢掉;全认不出时整条内建作废——而它是写死在代码里的,用户连删都删不掉,界面上也没有诊断(诊断只对文件)。
        foreach (var agent in BuiltinAgents.All)
        {
            foreach (var tool in agent.Tools ?? [])
            {
                Assert.True(tool == AgentToolNames.Normalize(tool), $"{agent.Name}: {tool}");
            }
        }
    }

    [Fact]
    public void Invariant_every_tool_the_form_offers_is_recognised_verbatim_by_the_tool_name_table()
    {
        // 漂了的话表单会勾出一个加载器不认识的名字,而加载器在「一个都认不出」时是把整条子代理作废的。
        foreach (var tool in AgentToolChoices.All)
        {
            Assert.Equal(tool, AgentToolNames.Normalize(tool));
        }
    }

    [Fact]
    public async Task An_overridden_builtin_no_longer_takes_a_builtin_line()
    {
        // 覆盖之后 Source.Kind 是 Global:那已经是用户自己的子代理了。
        File.WriteAllText(Path.Combine(_global, "code-analyst.md"), Md("my own analyst"));
        var registry = new AgentRegistry();

        var result = await Scan();
        registry.Replace(result.Agents, result.Diagnostics);

        Assert.Equal(BuiltinAgents.GeneralPurpose.Name, registry.Names()[0]);
        Assert.Equal("code-analyst", registry.Names()[^1]);
        Assert.Equal("my own analyst", registry.Get("code-analyst")!.Description);
    }

    [Fact]
    public async Task Get_looks_up_by_name_and_never_falls_back_to_general_purpose()
    {
        var registry = new AgentRegistry();
        var result = await Scan();
        registry.Replace(result.Agents, result.Diagnostics);

        Assert.Equal("general-purpose", registry.Get("general-purpose")!.Name);
        Assert.Null(registry.Get("reviewr"));
    }

    [Fact]
    public async Task Diagnostics_are_swapped_along_with_the_list_and_the_version_moves()
    {
        File.WriteAllText(Path.Combine(_global, "bad.md"), "---\n---\nbody\n");
        var registry = new AgentRegistry();
        var before = registry.Version;

        var result = await Scan();
        registry.Replace(result.Agents, result.Diagnostics);

        Assert.NotEmpty(registry.Diagnostics());
        Assert.True(registry.Version > before);
    }

    [Fact]
    public void The_tool_name_table_is_case_insensitive_maps_CC_names_and_never_confuses_WebSearch_with_WebFetch()
    {
        Assert.Equal("Edit", AgentToolNames.Normalize("MultiEdit"));
        Assert.Equal("Edit", AgentToolNames.Normalize("NotebookEdit"));
        Assert.Equal("Read", AgentToolNames.Normalize(" read "));
        Assert.Equal("web_search", AgentToolNames.Normalize("WebSearch"));
        Assert.Equal("WebFetch", AgentToolNames.Normalize("WEBFETCH"));
        Assert.Equal("BashOutput", AgentToolNames.Normalize("BashOutput"));
        Assert.Null(AgentToolNames.Normalize("Telepathy"));
        Assert.All(AgentToolNames.KnownAliases, alias => Assert.Equal(alias, alias.ToLowerInvariant()));
    }

    [Fact]
    public void A_list_deduplicates_and_reports_unknown_names_untouched()
    {
        var (tools, unknown) = AgentToolNames.NormalizeList(["Read", "multiedit", "Edit", " ", "Telepathy "]);

        Assert.Equal(["Read", "Edit"], tools);
        Assert.Equal(["Telepathy"], unknown);
    }

    // ───────────────────────── 目录服务 ─────────────────────────

    [Fact]
    public async Task Disabled_agents_are_filtered_out_of_the_registry_but_still_listed_with_a_flag()
    {
        File.WriteAllText(Path.Combine(_global, "mine.md"), Md("mine"));
        var switches = new MemoryAgentSwitchStore();
        var catalog = new AgentCatalog(new Source(_env, _global), switches);
        catalog.SetEnabled("mine", false);
        catalog.SetEnabled("code-reviewer", false); // 内建也在被过滤之列

        var enabled = await catalog.RefreshAsync("ws");
        var listed = await catalog.ListAsync("ws");

        // 不滤的话「禁用」只改了界面上一个开关的样子,模型照样能通过 Task 派遣它
        Assert.DoesNotContain(enabled, a => a.Name is "mine" or "code-reviewer");
        Assert.Contains(enabled, a => a.Name == "general-purpose");
        Assert.False(listed.Single(a => a.Name == "mine").Enabled);
        Assert.False(listed.Single(a => a.Name == "code-reviewer").Enabled);
        Assert.True(listed.Single(a => a.Name == "general-purpose").Enabled);
        Assert.Equal("", listed.Single(a => a.Name == "general-purpose").Source); // 内建没有来源路径
    }

    [Fact]
    public async Task Listing_and_diagnostics_are_read_only_they_never_replace_a_registry_that_runs_are_reading()
    {
        File.WriteAllText(Path.Combine(_global, "mine.md"), Md("mine"));
        var catalog = new AgentCatalog(new Source(_env, _global), new MemoryAgentSwitchStore());
        var registry = catalog.Registry("ws");
        var before = registry.Version;

        await catalog.ListAsync("ws");
        await catalog.DiagnosticsAsync("ws");

        Assert.Equal(before, registry.Version);
        Assert.Equal(BuiltinNames, registry.Names());
    }

    [Fact]
    public async Task Each_workspace_has_its_own_registry_one_workspaces_agents_do_not_show_up_in_anothers()
    {
        var other = Path.Combine(_root, "other", "agents");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(_global, "only-a.md"), Md("a"));
        File.WriteAllText(Path.Combine(other, "only-b.md"), Md("b"));
        var catalog = new AgentCatalog(new PerWorkspace(_env, new Dictionary<string, string> { ["a"] = _global, ["b"] = other }), new MemoryAgentSwitchStore());

        await catalog.RefreshAsync("a");
        await catalog.RefreshAsync("b");

        Assert.Contains("only-a", catalog.Registry("a").Names());
        Assert.DoesNotContain("only-b", catalog.Registry("a").Names());
        Assert.Contains("only-b", catalog.Registry("b").Names());
        catalog.Drop("a");
        Assert.Equal(BuiltinNames, catalog.Registry("a").Names()); // 新建的桶出厂就带内建
    }

    private sealed class PerWorkspace(LocalEnvironment env, Dictionary<string, string> roots) : IAgentScanSource
    {
        public ValueTask<AgentScanInput> InputForAsync(string workspaceId, CancellationToken ct) =>
            ValueTask.FromResult(new AgentScanInput(env, EnvPath.Posix(roots[workspaceId]), null, null));
    }

    [Fact]
    public void The_builtin_definitions_are_what_the_design_says_four_verbs_the_read_only_ones_name_their_tools()
    {
        Assert.Equal(["general-purpose", "code-analyst", "code-reviewer", "code-editor"], BuiltinNames);
        Assert.Null(BuiltinAgents.GeneralPurpose.Tools); // 不写 tools:继承全部,免得在这里复制一份内置工具表
        Assert.Equal(["Read", "Grep", "Glob", "LS"], BuiltinAgents.CodeAnalyst.Tools);
        Assert.Contains("Bash", BuiltinAgents.CodeReviewer.Tools!);
        Assert.Null(BuiltinAgents.CodeEditor.Tools);
        Assert.All(BuiltinAgents.All, a => Assert.Null(a.PermissionMode)); // 真正的收窄由用户的文件写
        Assert.All(BuiltinAgents.All, a => Assert.Contains("ONLY THE TEXT OF YOUR FINAL MESSAGE IS SENT BACK", a.Prompt));
        Assert.All(BuiltinAgents.All, a => Assert.True(AgentLimits.IsValidName(a.Name)));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("code-analyst", true)]
    [InlineData("0abc", true)]
    [InlineData("", false)]
    [InlineData("-a", false)]
    [InlineData("A", false)]
    [InlineData("a b", false)]
    [InlineData("../x", false)]
    [InlineData("a\n", false)]
    public void Agent_names_follow_the_2x_rule(string name, bool valid) => Assert.Equal(valid, AgentLimits.IsValidName(name));

    [Fact]
    public void An_agent_name_is_at_most_64_characters() => Assert.Equal((true, false), (AgentLimits.IsValidName(new string('a', 64)), AgentLimits.IsValidName(new string('a', 65))));
}
