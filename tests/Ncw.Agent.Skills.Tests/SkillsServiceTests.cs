using Ncw.Agent.Environments;
using Ncw.Agent.Skills.Install;

namespace Ncw.Agent.Skills.Tests;

/// <summary>
/// Skills 服务:清单(每次重扫、两个开关合成一份)、全局启停、工作区选装(「空清单 = 全都要」的物化、客户端资产闸门,移植自 2.x <c>ipc/__tests__/skill-gate.test.ts</c>)、
/// 卸载围栏、zip / 市场安装的编排。市场是假的 <see cref="ISkillMarketClient"/>,不碰网络。
/// </summary>
public sealed class SkillsServiceTests : IDisposable
{
    private readonly Fx _fx = new();
    private readonly MemorySkillStateStore _state = new();

    public void Dispose() => _fx.Dispose();

    private SkillsService Service(ISkillMarketClient? market = null, IExecutionEnvironment? projectEnvironment = null, ISkillUsage? usage = null) =>
        new(new TestLocations(_fx, projectEnvironment), _state, usage, market);

    private void Global(string name, string description = "d") => Fx.Put(_fx.GlobalSkills, name, Fx.Md(description));

    private void Project(string name, string description = "d") => Fx.Put(_fx.ProjectSkills, name, Fx.Md(description));

    private static byte[] ZipOf(string name, string body = "body") => Fx.Zip(($"{name}/SKILL.md", $"---\nname: {name}\ndescription: d\n---\n{body}\n"));

    // ───────────────────────── 清单 ─────────────────────────

    [Fact]
    public async Task Invariant_the_list_rescans_every_time_so_a_directory_dropped_in_a_moment_ago_shows_up()
    {
        var service = Service();
        Assert.Empty(await service.ListAsync("ws"));

        Global("fresh");

        Assert.Equal(["fresh"], (await service.ListAsync("ws")).Select(i => i.Name));
    }

    [Fact]
    public async Task An_empty_selection_shows_every_skill_as_active_and_no_workspace_means_the_question_is_not_asked()
    {
        Global("a");
        Global("b");
        var service = Service();

        Assert.All(await service.ListAsync("ws"), i => Assert.True(i.ActiveInWorkspace && i.GlobalEnabled));
        Assert.All(await service.ListAsync(null), i => Assert.True(i.ActiveInWorkspace));
    }

    [Fact]
    public async Task The_list_reports_source_scope_directory_and_usage()
    {
        Global("a");
        Project("b");
        var usage = new FixedUsage(new Dictionary<string, SkillUsage> { ["a"] = new(3, 1_700_000_000_000) });
        var items = (await Service(usage: usage).ListAsync("ws")).ToDictionary(i => i.Name);

        Assert.Equal((SkillScope.Global, 3, 1_700_000_000_000), (items["a"].Scope, items["a"].UsageCount, items["a"].LastUsedAt));
        Assert.Equal(SkillScope.Project, items["b"].Scope);
        Assert.EndsWith(Path.Combine("skills", "b"), items["b"].SourcePath);
    }

    private sealed class FixedUsage(IReadOnlyDictionary<string, SkillUsage> map) : ISkillUsage
    {
        public void Record(string skillId, string? workspaceId)
        {
        }

        public IReadOnlyDictionary<string, SkillUsage> Usage(string? workspaceId) => map;
    }

    [Fact]
    public async Task Diagnostics_are_returned_with_stable_keys()
    {
        Directory.CreateDirectory(Path.Combine(_fx.GlobalSkills, "empty"));

        var diagnostics = await Service().DiagnosticsAsync("ws");

        Assert.Contains(diagnostics, d => d.Key == SkillDiagnosticKeys.SkillMdMissing);
    }

    // ───────────────────────── 全局开关 ─────────────────────────

    [Fact]
    public async Task The_global_switch_stores_the_disabled_ones_and_changes_the_run_snapshot()
    {
        Global("a");
        Global("b");
        var service = Service();
        var changes = 0;
        service.Changed += () => changes++;

        service.SetGlobalEnabled("a", false);

        Assert.Equal(1, changes);
        Assert.Equal(["a"], _state.DisabledGlobally());
        var items = (await service.ListAsync("ws")).ToDictionary(i => i.Name);
        Assert.False(items["a"].GlobalEnabled);
        Assert.Equal(["b"], (await service.SnapshotAsync("ws")).Active.Select(s => s.Name));

        service.SetGlobalEnabled("a", true);
        Assert.Equal(["a", "b"], (await service.SnapshotAsync("ws")).Active.Select(s => s.Name).Order());
    }

    // ───────────────────────── 工作区选装(skill-gate.test.ts) ─────────────────────────

    [Fact]
    public async Task Refuses_to_enable_a_skill_whose_device_assets_are_unavailable_and_writes_nothing()
    {
        // 闸门必须在这里,不在界面:界面有三个入口能打开这个开关,历史上只有第一个做了判断——另外两个能把一条永远不会生效的 id 写进 activeSkillIds,界面显示「已启用」,模型那边却被静默排除。
        Global("needs-device");
        Directory.CreateDirectory(Path.Combine(_fx.GlobalSkills, "needs-device", "scripts"));
        _state.SetSelection("ws", [], true);
        var service = Service(projectEnvironment: new SshLikeEnvironment(_fx.Env));

        var e = await Assert.ThrowsAsync<SkillException>(() => service.SetWorkspaceActiveAsync("needs-device", "ws", true));

        Assert.Equal(SkillErrorKeys.ClientAssetsUnavailable, e.Key);
        Assert.Empty(_state.Selection("ws")!.Ids);
        Assert.True(_state.Selection("ws")!.Explicit);
    }

    [Fact]
    public async Task Still_allows_turning_such_a_skill_off_so_one_wrongly_enabled_in_the_past_can_always_be_removed()
    {
        Global("needs-device");
        Directory.CreateDirectory(Path.Combine(_fx.GlobalSkills, "needs-device", "scripts"));
        _state.SetSelection("ws", ["needs-device"], true);

        await Service(projectEnvironment: new SshLikeEnvironment(_fx.Env)).SetWorkspaceActiveAsync("needs-device", "ws", false);

        Assert.Empty(_state.Selection("ws")!.Ids);
        Assert.True(_state.Selection("ws")!.Explicit);
    }

    [Fact]
    public async Task Enabling_an_available_skill_adds_it_and_switches_the_workspace_to_explicit_selection()
    {
        Global("plain");

        await Service().SetWorkspaceActiveAsync("plain", "ws", true);

        Assert.Equal(["plain"], _state.Selection("ws")!.Ids);
        Assert.True(_state.Selection("ws")!.Explicit);
    }

    [Fact]
    public async Task Invariant_turning_one_off_under_an_implicit_all_materialises_the_whole_list_first()
    {
        // 空清单时把某一条关掉,不能只是「从空清单里删掉它」(空操作):得先把当前所有 Skill 的 id 铺开,再去掉这一条。
        Global("a");
        Global("b");
        Global("c");
        var service = Service();

        await service.SetWorkspaceActiveAsync("b", "ws", false);

        Assert.Equal(["a", "c"], _state.Selection("ws")!.Ids);
        Assert.Equal(["a", "c"], (await service.SnapshotAsync("ws")).Active.Select(s => s.Name));
        Assert.False((await service.ListAsync("ws")).Single(i => i.Name == "b").ActiveInWorkspace);
    }

    [Fact]
    public async Task Enabling_an_unknown_skill_is_not_found_and_an_unknown_workspace_is_reported()
    {
        var service = Service();
        Assert.Equal(SkillErrorKeys.NotFound, (await Assert.ThrowsAsync<SkillException>(() => service.SetWorkspaceActiveAsync("ghost", "ws", true))).Key);

        var noWorkspace = new SkillsService(new TestLocations(_fx), new NoWorkspaceState());
        Assert.Equal(SkillErrorKeys.WorkspaceNotFound, (await Assert.ThrowsAsync<SkillException>(() => noWorkspace.SetWorkspaceActiveAsync("a", "missing", true))).Key);
    }

    private sealed class NoWorkspaceState : ISkillStateStore
    {
        public IReadOnlySet<string> DisabledGlobally() => new HashSet<string>();

        public void SetGlobalEnabled(string skillId, bool enabled)
        {
        }

        public WorkspaceSkillSelection? Selection(string workspaceId) => null;

        public void SetSelection(string workspaceId, IReadOnlyList<string> ids, bool explicitSelection)
        {
        }
    }

    // ───────────────────────── 安装 ─────────────────────────

    [Fact]
    public async Task Installing_a_zip_returns_the_listed_item_and_raises_changed_and_reports_progress()
    {
        var service = Service();
        var changes = 0;
        service.Changed += () => changes++;
        var progress = new SyncProgress<SkillInstallProgress>();

        var item = await service.InstallZipAsync(ZipOf("demo"), SkillInstallScope.Global, null, "demo-v1.0.0.zip", progress);

        Assert.Equal(("demo", SkillSourceKind.Zip, SkillScope.Global, "1.0.0"), (item.Name, item.SourceKind, item.Scope, item.Version));
        Assert.Equal(1, changes);
        await Task.Delay(50);
        Assert.Equal(["installing", "done"], progress.Items.Select(p => p.Phase));
    }

    [Fact]
    public async Task A_project_install_writes_into_the_project_skills_directory()
    {
        var item = await Service().InstallZipAsync(ZipOf("demo"), SkillInstallScope.Project, "ws");

        Assert.Equal(SkillScope.Project, item.Scope);
        Assert.True(File.Exists(Path.Combine(_fx.ProjectSkills, "demo", "SKILL.md")));
    }

    [Fact]
    public async Task A_project_install_without_a_workspace_is_refused()
    {
        var e = await Assert.ThrowsAsync<SkillException>(() => Service().InstallZipAsync(ZipOf("demo"), SkillInstallScope.Project, null));

        Assert.Equal(SkillErrorKeys.WorkspaceRequired, e.Key);
    }

    [Fact]
    public async Task A_failed_install_reports_a_terminal_failed_progress_with_the_key()
    {
        var progress = new SyncProgress<SkillInstallProgress>();

        await Assert.ThrowsAsync<SkillException>(() => Service().InstallZipAsync(Fx.Zip(("x/readme.md", "x")), SkillInstallScope.Global, null, null, progress));

        Assert.Equal(("failed", SkillErrorKeys.ZipInvalid), (progress.Items[^1].Phase, progress.Items[^1].ErrorKey));
    }

    // ───────────────────────── 市场 ─────────────────────────

    private sealed class FakeMarket : ISkillMarketClient
    {
        public byte[] Package { get; set; } = [];

        public string? GrantSha { get; set; }

        public Exception? GrantFails { get; set; }

        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<SkillMarketItem>> ListAsync(string? query, string? category, CancellationToken ct)
        {
            Calls.Add($"list:{query}:{category}");
            return Task.FromResult<IReadOnlyList<SkillMarketItem>>([new SkillMarketItem("demo", "demo", "Demo", "d", "dev", null, null, "1.0.0", null, null, 5, null)]);
        }

        public Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["dev", "docs"]);

        public Task<SkillMarketDetail> DetailAsync(string slug, CancellationToken ct) =>
            Task.FromResult(new SkillMarketDetail(
                new SkillMarketItem(slug, slug, slug, "d", "dev", null, null, "1.0.0", null, null, null, null),
                [new SkillMarketVersion("1.0.0", null, null, Package.Length)]));

        public Task<SkillInstallGrant> GrantAsync(string slug, string version, CancellationToken ct)
        {
            Calls.Add($"grant:{slug}:{version}");
            return GrantFails is { } e ? Task.FromException<SkillInstallGrant>(e) : Task.FromResult(new SkillInstallGrant(version, GrantSha ?? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Package))));
        }

        public Task<byte[]> DownloadAsync(string slug, string version, int maxBytes, Action<SkillDownloadProgress>? progress, CancellationToken ct)
        {
            Calls.Add($"download:{slug}:{version}");
            progress?.Invoke(new SkillDownloadProgress(Package.Length, Package.Length));
            return Task.FromResult(Package);
        }
    }

    [Fact]
    public async Task A_market_install_asks_for_a_grant_downloads_verifies_the_digest_and_installs()
    {
        var market = new FakeMarket { Package = ZipOf("demo") };
        var progress = new SyncProgress<SkillInstallProgress>();

        var item = await Service(market).InstallMarketAsync("demo", null, SkillInstallScope.Global, null, progress);

        Assert.Equal("demo", item.Name);
        Assert.Equal(["grant:demo:1.0.0", "download:demo:1.0.0"], market.Calls);
        Assert.Equal(["preparing", "downloading", "installing", "done"], progress.Items.Select(p => p.Phase));
        Assert.Equal(market.Package.Length, progress.Items.Single(p => p.Phase == "downloading").Received);
    }

    [Fact]
    public async Task A_package_that_does_not_match_the_granted_digest_installs_nothing()
    {
        var market = new FakeMarket { Package = ZipOf("demo"), GrantSha = new string('a', 64) };

        var e = await Assert.ThrowsAsync<SkillException>(() => Service(market).InstallMarketAsync("demo", "1.0.0", SkillInstallScope.Global, null));

        Assert.Equal(SkillErrorKeys.DigestMismatch, e.Key);
        Assert.False(Directory.Exists(Path.Combine(_fx.GlobalSkills, "demo")));
    }

    [Fact]
    public async Task Without_an_account_token_the_grant_fails_with_auth_required_and_nothing_is_downloaded()
    {
        var market = new FakeMarket { Package = ZipOf("demo"), GrantFails = new SkillException(SkillErrorKeys.AuthRequired, "sign in") };

        var e = await Assert.ThrowsAsync<SkillException>(() => Service(market).InstallMarketAsync("demo", null, SkillInstallScope.Global, null));

        Assert.Equal(SkillErrorKeys.AuthRequired, e.Key);
        Assert.DoesNotContain("download:demo:1.0.0", market.Calls);
    }

    [Fact]
    public async Task Market_browsing_passes_through_and_without_a_client_it_is_a_network_failure_not_a_crash()
    {
        var market = new FakeMarket();
        var service = Service(market);

        Assert.Single(await service.MarketListAsync("pd", "dev"));
        Assert.Equal(["dev", "docs"], await service.MarketCategoriesAsync());
        Assert.Equal("1.0.0", (await service.MarketDetailAsync("demo")).Item.Version);
        Assert.Equal(["list:pd:dev"], market.Calls);

        var e = await Assert.ThrowsAsync<SkillException>(() => Service().MarketListAsync(null, null));
        Assert.Equal(SkillErrorKeys.NetworkFailed, e.Key);
    }

    // ───────────────────────── 卸载 ─────────────────────────

    [Fact]
    public async Task Uninstall_removes_only_the_skill_directory_inside_the_skills_root()
    {
        Global("a");
        Global("b");
        var service = Service();
        var changes = 0;
        service.Changed += () => changes++;

        await service.UninstallAsync("a", null, null);

        Assert.False(Directory.Exists(Path.Combine(_fx.GlobalSkills, "a")));
        Assert.True(Directory.Exists(Path.Combine(_fx.GlobalSkills, "b")));
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Uninstalling_a_project_skill_defaults_to_the_project_scope()
    {
        Project("proj");

        await Service().UninstallAsync("proj", null, "ws");

        Assert.False(Directory.Exists(Path.Combine(_fx.ProjectSkills, "proj")));
    }

    [Fact]
    public async Task Invariant_a_scope_that_does_not_match_where_the_skill_really_lives_is_refused_and_nothing_is_deleted()
    {
        Project("proj");

        var e = await Assert.ThrowsAsync<SkillException>(() => Service().UninstallAsync("proj", SkillInstallScope.Global, "ws"));

        Assert.Equal(SkillErrorKeys.InvalidLocation, e.Key);
        Assert.True(Directory.Exists(Path.Combine(_fx.ProjectSkills, "proj")));
    }

    [Fact]
    public async Task Invariant_a_skill_that_is_a_symlink_to_a_directory_outside_the_root_is_never_deleted_through_the_link()
    {
        // 逃逸的软链本来就进不了清单(围栏);直接伪造一个指向根外的 Skill 目录来卸载同样要被拒。
        var outside = Path.Combine(_fx.Root, "keep-me");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "SKILL.md"), Fx.Md("x"));
        Directory.CreateSymbolicLink(Path.Combine(_fx.GlobalSkills, "evil"), outside);

        await Assert.ThrowsAsync<SkillException>(() => Service().UninstallAsync("evil", null, null));

        Assert.True(File.Exists(Path.Combine(outside, "SKILL.md")));
    }

    [Fact]
    public async Task A_plugin_skill_has_no_uninstall_and_an_unknown_one_is_not_found()
    {
        var dir = Path.Combine(_fx.Root, "plugins", "acme.p", "skills", "pdf");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), Fx.Md("d"));
        var service = new SkillsService(new TestLocations(_fx, plugins: [new PluginSkillRoot("acme.p", _fx.Env, _fx.P(dir))]), _state);

        Assert.Equal(SkillErrorKeys.InvalidLocation, (await Assert.ThrowsAsync<SkillException>(() => service.UninstallAsync("pdf", null, null))).Key);
        Assert.Equal(SkillErrorKeys.NotFound, (await Assert.ThrowsAsync<SkillException>(() => service.UninstallAsync("ghost", null, null))).Key);
        Assert.True(Directory.Exists(dir));
    }
}
