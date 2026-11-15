using Ncw.Agent.Environments;

namespace Ncw.Agent.Skills.Tests;

/// <summary>
/// Skill 扫描的测试,移植自 2.x <c>kernel/__tests__/skill-load.test.ts</c>。<b>用真临时目录,不打桩文件系统</b>:这个文件里最重要的一条是软链逃逸(<c>skills/evil -> /</c>),
/// 而路径守卫挡住它靠的是 realpath——桩掉文件系统,那条用例测的就只剩我们自己写的假对象怎么编造 realpath 了。
/// 加载器的契约是<b>永不抛</b>:每一种坏输入都要变成一条诊断,所以几乎每条用例都同时断言两件事:「这条没进来」和「用户能知道它为什么没进来」(断言诊断的稳定 key,不断言逐字文案)。
/// </summary>
public sealed class SkillLoaderTests : IDisposable
{
    private readonly Fx _fx = new();

    public void Dispose() => _fx.Dispose();

    private static string Md(string? description, string body = "照着这个做。") => Fx.Md(description, body);

    private void Global(string dir, string content) => Fx.Put(_fx.GlobalSkills, dir, content);

    private void Project(string dir, string content) => Fx.Put(_fx.ProjectSkills, dir, content);

    // ───────────────────────── 基本形状 ─────────────────────────

    [Fact]
    public async Task Client_asset_packages_are_unavailable_in_a_remote_project_while_text_snapshots_and_project_overrides_stay()
    {
        Global("text-only", Md("Text snapshot"));
        Global("client-package", Md("Client assets"));
        Directory.CreateDirectory(Path.Combine(_fx.GlobalSkills, "client-package", "scripts"));
        Global("overridden", Md("Client assets"));
        Directory.CreateDirectory(Path.Combine(_fx.GlobalSkills, "overridden", "scripts"));
        Project("overridden", Md("Server version"));

        var result = await SkillLoader.ScanAsync(_fx.Input(projectEnvironment: new SshLikeEnvironment(_fx.Env)));

        Assert.False(result.Skills.Single(s => s.Name == "text-only").ClientAssetsOnly);
        Assert.True(result.Skills.Single(s => s.Name == "client-package").ClientAssetsOnly);
        var overridden = result.Skills.Single(s => s.Name == "overridden");
        Assert.Equal(SkillScope.Project, overridden.Scope);
        Assert.False(overridden.ClientAssetsOnly);
    }

    [Fact]
    public async Task A_local_project_never_marks_anything_unavailable()
    {
        Global("client-package", Md("Client assets"));
        Directory.CreateDirectory(Path.Combine(_fx.GlobalSkills, "client-package", "scripts"));

        Assert.All((await _fx.ScanAsync()).Skills, s => Assert.False(s.ClientAssetsOnly));
    }

    [Fact]
    public async Task Reads_name_description_body_and_the_scope()
    {
        Global("commit", "---\ndescription: 写提交信息\n---\n按 Conventional Commits 写。\n");

        var r = await _fx.ScanAsync();

        var s = Assert.Single(r.Skills);
        Assert.Equal(("commit", "commit", "写提交信息", "按 Conventional Commits 写。", SkillScope.Global), (s.Name, s.Id, s.Description, s.Body, s.Scope));
        Assert.Empty(r.Diagnostics);
    }

    [Fact]
    public async Task A_project_skill_has_project_scope_and_its_source_points_at_the_real_file()
    {
        Project("commit", Md("写提交信息"));

        var s = Assert.Single((await _fx.ScanAsync()).Skills);

        Assert.Equal(SkillScope.Project, s.Scope);
        Assert.Contains("SKILL.md", s.Source.Path);
        Assert.Equal(SkillSourceKind.Folder, s.Source.Kind);
    }

    [Fact]
    public async Task Category_falls_back_to_uncategorised_and_otherwise_uses_the_given_one()
    {
        Global("a", "---\ndescription: d\ncategory: 开发工具\n---\n正文\n");
        Global("b", Md("d"));

        var byName = (await _fx.ScanAsync()).Skills.ToDictionary(s => s.Name);

        Assert.Equal("开发工具", byName["a"].Category);
        Assert.Equal(SkillLoader.Uncategorized, byName["b"].Category);
    }

    [Fact]
    public async Task A_missing_directory_is_normal_not_an_error()
    {
        Directory.Delete(_fx.GlobalSkills, true);
        Directory.Delete(_fx.ProjectSkills, true);

        var r = await _fx.ScanAsync();

        Assert.Empty(r.Skills);
        Assert.Empty(r.Diagnostics);
    }

    [Fact]
    public async Task A_null_root_skips_the_whole_layer_which_is_the_shape_without_a_workspace()
    {
        Global("commit", Md("d"));

        var r = await SkillLoader.ScanAsync(_fx.Input(project: false));

        Assert.Equal(["commit"], r.Skills.Select(s => s.Name));
    }

    [Fact]
    public async Task Loose_files_in_the_skills_directory_are_ignored()
    {
        File.WriteAllText(Path.Combine(_fx.GlobalSkills, "README.md"), "# 说明");
        Global("commit", Md("d"));

        Assert.Equal(["commit"], (await _fx.ScanAsync()).Skills.Select(s => s.Name));
    }

    // ───────────────────────── 同名时项目胜出 ─────────────────────────

    [Fact]
    public async Task Invariant_on_a_name_clash_only_the_project_skill_remains_with_the_project_body()
    {
        // ★ 钉的是「全局先进、项目后进覆盖」的顺序。反过来写,项目里那条「这个仓库要用我们自己的提交规范」的 Skill 就永远压不过全局那条,而且症状是静默的。
        Global("commit", "---\ndescription: 全局的\n---\n全局正文\n");
        Project("commit", "---\ndescription: 项目的\n---\n项目正文\n");

        var s = Assert.Single((await _fx.ScanAsync()).Skills);

        Assert.Equal(("项目的", "项目正文", SkillScope.Project), (s.Description, s.Body, s.Scope));
    }

    [Fact]
    public async Task Differently_named_layers_merge_instead_of_choosing_one()
    {
        Global("commit", Md("d"));
        Project("review", Md("d"));

        Assert.Equal(["commit", "review"], (await _fx.ScanAsync()).Skills.Select(s => s.Name).Order());
    }

    // ───────────────────────── 无效的那些 ─────────────────────────

    [Fact]
    public async Task Invariant_a_missing_description_voids_the_skill_and_says_why()
    {
        // ★ 渐进披露之后描述是模型唯一的判断依据:凑一个的话,模型要么永远不调它,要么见什么都调它——两种都比「这条没装上」更糟,因为用户在界面上看得见它,会认为它在工作。
        Global("commit", Md(null));

        var r = await _fx.ScanAsync();

        Assert.Empty(r.Skills);
        Assert.True(Fx.Said(r, SkillDiagnosticKeys.DescriptionMissing));
    }

    [Fact]
    public async Task An_empty_body_voids_the_skill_too()
    {
        Global("commit", "---\ndescription: d\n---\n\n   \n");

        var r = await _fx.ScanAsync();

        Assert.Empty(r.Skills);
        Assert.True(Fx.Said(r, SkillDiagnosticKeys.BodyEmpty));
    }

    [Fact]
    public async Task A_directory_without_skill_md_is_reported_not_silently_skipped()
    {
        Directory.CreateDirectory(Path.Combine(_fx.GlobalSkills, "commit"));

        var r = await _fx.ScanAsync();

        Assert.Empty(r.Skills);
        Assert.True(Fx.Said(r, SkillDiagnosticKeys.SkillMdMissing));
    }

    [Fact]
    public async Task Directory_names_that_are_not_valid_are_skipped_capitals_dots_and_spaces()
    {
        Global("Commit", Md("d"));
        Global("a b", Md("d"));
        Global("x.y", Md("d"));

        var r = await _fx.ScanAsync();

        Assert.Empty(r.Skills);
        Assert.Equal(3, r.Diagnostics.Count(d => d.Key == SkillDiagnosticKeys.DirectoryNameInvalid));
    }

    [Fact]
    public async Task A_skill_md_without_any_frontmatter_is_void_because_there_is_no_description()
    {
        Global("commit", "# 只是一篇普通的 markdown\n\n没有 frontmatter。\n");

        var r = await _fx.ScanAsync();

        Assert.Empty(r.Skills);
        Assert.True(Fx.Said(r, SkillDiagnosticKeys.DescriptionMissing));
    }

    // ───────────────────────── name: 与目录名 ─────────────────────────

    [Fact]
    public async Task The_frontmatter_name_wins_over_the_directory_name_but_a_mismatch_is_reported()
    {
        Global("old-name", "---\nname: new-name\ndescription: d\n---\n正文\n");

        var r = await _fx.ScanAsync();

        var s = Assert.Single(r.Skills);
        Assert.Equal(("new-name", "new-name"), (s.Name, s.Id));
        Assert.True(Fx.Said(r, SkillDiagnosticKeys.NameMismatch));
    }

    [Fact]
    public async Task A_matching_name_produces_no_noise()
    {
        Global("commit", "---\nname: commit\ndescription: d\n---\n正文\n");

        Assert.Empty((await _fx.ScanAsync()).Diagnostics);
    }

    [Fact]
    public async Task Invariant_a_valid_directory_with_an_invalid_name_is_void_not_falling_back_to_the_directory_name()
    {
        // 目录名那道闸在扫描时过,name 那道闸在解析之后过。少了后面这道,一个带换行的 name 会直接进系统提示词的目录里。
        Global("commit", "---\nname: 不合法的名字\ndescription: d\n---\n正文\n");

        var r = await _fx.ScanAsync();

        Assert.Empty(r.Skills);
        Assert.True(Fx.Said(r, SkillDiagnosticKeys.NameInvalid));
    }

    // ───────────────────────── 路径逃逸 ─────────────────────────

    [Fact]
    public async Task Invariant_a_symlinked_directory_pointing_outside_the_skills_root_is_rejected_without_hurting_its_neighbours()
    {
        // ★ 最重要的一条:skills/evil -> / 之后,「扫描 skills 目录」就变成了「扫描整个磁盘」,而 .next-cowork/ 是跟着仓库 clone 下来的,一个软链进 git 是零成本的事。
        var outside = Path.Combine(_fx.Root, "elsewhere", "hidden-target");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "SKILL.md"), Md("我在外面"));
        Directory.CreateSymbolicLink(Path.Combine(_fx.GlobalSkills, "evil"), outside);
        Global("good", Md("我在里面"));

        var r = await _fx.ScanAsync();

        Assert.Equal(["good"], r.Skills.Select(s => s.Name));
        var diagnostic = Assert.Single(r.Diagnostics, d => d.Key == SkillDiagnosticKeys.OutsideRoot);
        // 诊断不回显软链的目标:那是逃逸想探的东西
        Assert.DoesNotContain("hidden-target", diagnostic.Message);
        Assert.DoesNotContain("hidden-target", string.Join(' ', diagnostic.Params?.Values ?? []));
    }

    [Fact]
    public async Task A_symlink_to_another_directory_inside_the_skills_root_is_allowed_the_fence_is_about_going_out()
    {
        Global("real", Md("d"));
        Directory.CreateSymbolicLink(Path.Combine(_fx.GlobalSkills, "alias"), Path.Combine(_fx.GlobalSkills, "real"));

        Assert.Equal(["alias", "real"], (await _fx.ScanAsync()).Skills.Select(s => s.Name).Order());
    }

    [Fact]
    public async Task A_skill_md_that_is_a_symlink_out_of_its_directory_is_not_read()
    {
        var secret = Path.Combine(_fx.Root, "secret.md");
        File.WriteAllText(secret, Md("secret"));
        Directory.CreateDirectory(Path.Combine(_fx.GlobalSkills, "leak"));
        File.CreateSymbolicLink(Path.Combine(_fx.GlobalSkills, "leak", "SKILL.md"), secret);

        var r = await _fx.ScanAsync();

        Assert.Empty(r.Skills);
        Assert.NotEmpty(r.Diagnostics);
    }

    // ───────────────────────── 不可信输入的消毒与封顶 ─────────────────────────

    [Fact]
    public async Task Invariant_control_characters_in_the_description_are_stripped_because_it_enters_the_system_prompt_verbatim()
    {
        var esc = (char)27;
        Global("commit", $"---\ndescription: 写{esc}[31m提交信息\n---\n正文\n");

        var s = Assert.Single((await _fx.ScanAsync()).Skills);

        Assert.DoesNotContain(esc, s.Description);
        Assert.Contains("提交信息", s.Description);
    }

    [Fact]
    public async Task Control_characters_in_the_body_are_stripped_too()
    {
        Global("commit", $"---\ndescription: d\n---\n正{(char)0}文\n");

        Assert.Equal("正文", (await _fx.ScanAsync()).Skills[0].Body);
    }

    [Fact]
    public async Task The_description_is_capped_at_1KB_because_it_is_the_only_part_that_enters_the_prompt()
    {
        Global("commit", $"---\ndescription: {new string('x', 5000)}\n---\n正文\n");

        Assert.True((await _fx.ScanAsync()).Skills[0].Description.Length <= SkillLimits.DescriptionMax);
    }

    [Fact]
    public async Task The_body_is_capped_at_the_body_limit()
    {
        Global("commit", $"---\ndescription: d\n---\n{new string('正', SkillLimits.BodyMax)}\n");

        Assert.True((await _fx.ScanAsync()).Skills[0].Body.Length <= SkillLimits.BodyMax);
    }

    [Fact]
    public async Task Invariant_an_oversized_skill_md_is_read_only_up_to_a_byte_limit_not_pulled_into_memory_whole()
    {
        Global("commit", $"---\ndescription: d\n---\n{new string('A', 2_000_000)}\n");

        var s = Assert.Single((await _fx.ScanAsync()).Skills);

        Assert.Equal("d", s.Description);
        Assert.True(s.Body.Length <= SkillLimits.FileMaxBytes);
    }

    [Fact]
    public async Task Unsupported_frontmatter_syntax_becomes_a_diagnostic_instead_of_failing_the_whole_skill()
    {
        Global("commit", "---\ndescription: d\nnested:\n  a:\n    b: 1\n---\n正文\n");

        var r = await _fx.ScanAsync();

        Assert.Single(r.Skills);
        Assert.True(Fx.Said(r, SkillDiagnosticKeys.Frontmatter));
    }

    // ───────────────────────── allowed-tools ─────────────────────────

    [Fact]
    public async Task Allowed_tools_accepts_the_claude_code_form_a_comma_separated_bare_scalar()
    {
        Global("commit", "---\ndescription: d\nallowed-tools: Read, Grep, Glob\n---\n正文\n");

        Assert.Equal(["Read", "Grep", "Glob"], (await _fx.ScanAsync()).Skills[0].AllowedTools);
    }

    [Fact]
    public async Task Allowed_tools_also_accepts_a_yaml_array_and_the_camel_case_variant()
    {
        Global("a", "---\ndescription: d\nallowed-tools: [Read, Grep]\n---\n正文\n");
        Global("b", "---\ndescription: d\nallowedTools: Read\n---\n正文\n");

        var byName = (await _fx.ScanAsync()).Skills.ToDictionary(s => s.Name);

        Assert.Equal(["Read", "Grep"], byName["a"].AllowedTools);
        Assert.Equal(["Read"], byName["b"].AllowedTools);
    }

    [Fact]
    public async Task Unset_allowed_tools_is_null_not_an_empty_list_which_downstream_would_read_as_no_tools_at_all()
    {
        Global("commit", Md("d"));

        Assert.Null((await _fx.ScanAsync()).Skills[0].AllowedTools);
    }

    // ───────────────────────── 永不抛 ─────────────────────────

    [Fact]
    public async Task Invariant_a_file_system_that_throws_at_everything_yields_diagnostics_not_an_exception()
    {
        // 一次扫描因为某台机器上一个坏文件而整体抛异常的话,用户看到的是「所有 Skill 都不见了」,而真正坏掉的只有一条。
        var angry = new AngryEnvironment(_fx.Env, EnvironmentErrorCode.PermissionDenied);

        var r = await SkillLoader.ScanAsync(new SkillScanInput(angry, _fx.P(_fx.GlobalSkills), null, null));

        Assert.Empty(r.Skills);
        Assert.Contains(r.Diagnostics, d => d.Key == SkillDiagnosticKeys.DirectoryUnreadable);
    }

    [Fact]
    public async Task A_connection_loss_is_not_no_skills_it_propagates()
    {
        Global("commit", Md("d"));
        var dead = new DisconnectedEnvironment(_fx.Env);

        await Assert.ThrowsAsync<EnvironmentException>(() => SkillLoader.ScanAsync(_fx.Input(projectEnvironment: dead)));
    }

    [Fact]
    public async Task Other_skills_in_the_same_layer_still_load_when_one_skill_md_cannot_be_read()
    {
        Global("good", Md("d"));
        Directory.CreateDirectory(Path.Combine(_fx.GlobalSkills, "bad"));
        File.CreateSymbolicLink(Path.Combine(_fx.GlobalSkills, "bad", "SKILL.md"), Path.Combine(_fx.GlobalSkills, "bad", "nowhere"));

        Assert.Equal(["good"], (await _fx.ScanAsync()).Skills.Select(s => s.Name));
    }

    [Fact]
    public async Task More_than_two_hundred_skills_stops_with_a_diagnostic()
    {
        for (var i = 0; i < SkillLimits.MaxSkills + 5; i++)
        {
            Global($"s{i:D3}", Md("d"));
        }

        var r = await _fx.ScanAsync();

        Assert.Equal(SkillLimits.MaxSkills, r.Skills.Count);
        Assert.True(Fx.Said(r, SkillDiagnosticKeys.TooMany));
    }

    [Fact]
    public async Task A_zip_install_leaves_package_metadata_that_the_scan_reads_back_as_the_source()
    {
        Global("demo", Md("d"));
        File.WriteAllText(Path.Combine(_fx.GlobalSkills, "demo", SkillLoader.PackageMetadataFile), """{"sourceKind":"zip","sha256":"abc","version":"1.2.3"}""");
        Global("other", Md("d"));
        File.WriteAllText(Path.Combine(_fx.GlobalSkills, "other", SkillLoader.PackageMetadataFile), """{"sourceKind":"git","sha256":"abc"}""");

        var byName = (await _fx.ScanAsync()).Skills.ToDictionary(s => s.Name);

        Assert.Equal((SkillSourceKind.Zip, "1.2.3", "abc"), (byName["demo"].Source.Kind, byName["demo"].Source.Version, byName["demo"].Source.Sha256));
        Assert.Equal(SkillSourceKind.Folder, byName["other"].Source.Kind);
    }
}

/// <summary>每个文件系统操作都抛指定错误码的环境(测「永不抛」)。</summary>
internal sealed class AngryEnvironment(IExecutionEnvironment inner, EnvironmentErrorCode code) : IExecutionEnvironment
{
    private sealed class AngryFs(IEnvironmentFs inner, EnvironmentErrorCode code) : IEnvironmentFs
    {
        private EnvironmentException Boom() => new(code);

        public ValueTask<FsEntryInfo> StatAsync(EnvPath path, CancellationToken ct = default) => throw Boom();

        public ValueTask<FsEntryInfo> LStatAsync(EnvPath path, CancellationToken ct = default) =>
            code == EnvironmentErrorCode.PermissionDenied ? ValueTask.FromResult(new FsEntryInfo(FsEntryKind.Directory, 0, DateTimeOffset.UnixEpoch)) : throw Boom();

        public ValueTask<EnvPath> RealPathAsync(EnvPath path, CancellationToken ct = default) => throw Boom();

        public ValueTask<IReadOnlyList<FsDirEntry>> ListAsync(EnvPath path, CancellationToken ct = default) => throw Boom();

        public ValueTask<Stream> OpenReadAsync(EnvPath path, CancellationToken ct = default) => throw Boom();

        public ValueTask<Stream> OpenWriteAsync(EnvPath path, FsWriteOptions options, CancellationToken ct = default) => throw Boom();

        public ValueTask MkdirAsync(EnvPath path, bool recursive, CancellationToken ct = default) => throw Boom();

        public ValueTask RenameAsync(EnvPath source, EnvPath destination, bool replace, CancellationToken ct = default) => throw Boom();

        public ValueTask DeleteFileAsync(EnvPath path, CancellationToken ct = default) => throw Boom();

        public ValueTask DeleteDirectoryAsync(EnvPath path, bool recursive, CancellationToken ct = default) => throw Boom();

        public ValueTask<long> CopyFileAsync(EnvPath source, EnvPath destination, long maxBytes, CancellationToken ct = default) => throw Boom();

        public IFsWatcher Watch(EnvPath directory, FsWatchOptions options) => inner.Watch(directory, options);
    }

    public EnvironmentId Id => inner.Id;

    public EnvironmentKind Kind => inner.Kind;

    public string DisplayName => inner.DisplayName;

    public int Generation => inner.Generation;

    public EnvironmentState State => inner.State;

    public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged
    {
        add => inner.StatusChanged += value;
        remove => inner.StatusChanged -= value;
    }

    public EnvironmentFacts Facts => inner.Facts;

    public PathGuard Paths => new(Fs, inner.Facts.PathStyle);

    public IEnvironmentFs Fs { get; } = new AngryFs(inner.Fs, code);

    public ValueTask EnsureReadyAsync(CancellationToken ct = default) => inner.EnsureReadyAsync(ct);

    public Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default) => inner.ProbeToolsAsync(ct);

    public Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default) => inner.OpenProcessAsync(spec, ct);

    public Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default) => inner.OpenTerminalAsync(spec, ct);

    public Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default) => inner.OpenTcpAsync(host, port, ct);

    public Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) => inner.OpenLocalForwardAsync(host, port, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
