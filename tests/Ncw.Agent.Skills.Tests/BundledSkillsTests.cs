using Ncw.Agent.Skills.Install;

namespace Ncw.Agent.Skills.Tests;

/// <summary>
/// 内置 Skill 的铺设,移植自 2.x <c>kernel/skill/__tests__/bundled.test.ts</c>。<c>resources/skills</c> 在本分支还没有(随资源打包),所以用测试里造的源目录;
/// 「随应用发布的 repo-init 单文件」「skills-creator 可被发现」那几条需要真资源,等资源进来后补(见 parity)。
/// </summary>
public sealed class BundledSkillsTests : IDisposable
{
    private readonly Fx _fx = new();

    public void Dispose() => _fx.Dispose();

    private string Source => Path.Combine(_fx.Root, "bundled");

    private string Target => Path.Combine(_fx.Root, "global-target", "skills");

    private void Bundle(string name, string description = "bundled", string body = "bundled body", params (string Path, string Content)[] extra)
    {
        Fx.Put(Source, name, $"---\ndescription: {description}\n---\n{body}\n");
        foreach (var (path, content) in extra)
        {
            var full = Path.Combine(Source, name, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private Task<IReadOnlyList<SkillDiagnostic>> InstallAsync() => BundledSkills.InstallAsync(_fx.Env, _fx.P(Source), _fx.Env, _fx.P(Target));

    [Fact]
    public async Task Installs_a_discoverable_global_skill_with_its_resources_and_the_sentinel()
    {
        Bundle("skills-creator", extra: ("references/guide.md", "guide"));

        Assert.Empty(await InstallAsync());
        Assert.Empty(await InstallAsync()); // 重复启动也干净

        var scan = await SkillLoader.ScanAsync(new SkillScanInput(_fx.Env, _fx.P(Target), null, null));
        var skill = Assert.Single(scan.Skills);
        Assert.Equal(("skills-creator", SkillScope.Global), (skill.Name, skill.Scope));
        Assert.Empty(scan.Diagnostics);
        Assert.Equal("guide", File.ReadAllText(Path.Combine(Target, "skills-creator", "references", "guide.md")));
        Assert.True(File.Exists(Path.Combine(Target, "skills-creator", SkillLoader.BundledMarker)));
    }

    [Fact]
    public async Task A_single_file_bundled_skill_stays_usable_in_a_remote_session_but_one_with_references_does_not()
    {
        // 哨兵和包清单一样是安装器自己写的元数据,不是正文会引用的资产:不放过它的话,每一条内置 Skill 在 SSH 会话里都会被拒掉。
        Bundle("repo-init");
        Bundle("plugin-builder", extra: ("references/a.md", "a"));
        await InstallAsync();

        var scan = await SkillLoader.ScanAsync(new SkillScanInput(_fx.Env, _fx.P(Target), new SshLikeEnvironment(_fx.Env), _fx.P(_fx.ProjectSkills)));
        var byName = scan.Skills.ToDictionary(s => s.Name);

        Assert.False(byName["repo-init"].ClientAssetsOnly);
        Assert.True(byName["plugin-builder"].ClientAssetsOnly);
    }

    [Fact]
    public async Task Invariant_a_previously_bundled_package_is_overwritten_on_the_next_startup_refreshing_the_built_in_content()
    {
        Bundle("repo-init", body: "v1");
        await InstallAsync();
        Bundle("repo-init", body: "v2");

        Assert.Empty(await InstallAsync());

        Assert.Contains("v2", File.ReadAllText(Path.Combine(Target, "repo-init", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(Target, "repo-init", SkillLoader.BundledMarker)));
        Assert.Equal(["repo-init"], Directory.GetDirectories(Target).Select(Path.GetFileName)); // 暂存 / 备份都清掉了
    }

    [Fact]
    public async Task Invariant_a_users_same_named_skill_without_the_sentinel_is_never_overwritten()
    {
        Fx.Put(Target, "repo-init", "---\ndescription: mine\n---\nuser body\n");
        Bundle("repo-init", body: "bundled");

        Assert.Empty(await InstallAsync());

        Assert.Contains("user body", File.ReadAllText(Path.Combine(Target, "repo-init", "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(Target, "repo-init", SkillLoader.BundledMarker)));
    }

    [Fact]
    public async Task An_incomplete_bundled_package_is_reported_without_creating_it()
    {
        Directory.CreateDirectory(Path.Combine(Source, "broken"));
        File.WriteAllText(Path.Combine(Source, "broken", "readme.md"), "x");

        var diagnostics = await InstallAsync();

        Assert.Contains(diagnostics, d => d.Key == SkillDiagnosticKeys.BundledInvalid);
        Assert.False(Directory.Exists(Path.Combine(Target, "broken")));
    }

    [Fact]
    public async Task A_missing_source_directory_is_a_diagnostic_so_startup_can_continue()
    {
        var diagnostics = await BundledSkills.InstallAsync(_fx.Env, _fx.P(Path.Combine(_fx.Root, "nowhere")), _fx.Env, _fx.P(Target));

        Assert.Single(diagnostics);
    }

    [Fact]
    public async Task Symbolic_links_in_the_bundled_source_are_not_copied()
    {
        Bundle("repo-init");
        File.WriteAllText(Path.Combine(_fx.Root, "outside.txt"), "secret");
        File.CreateSymbolicLink(Path.Combine(Source, "repo-init", "leak.txt"), Path.Combine(_fx.Root, "outside.txt"));

        Assert.Empty(await InstallAsync());

        Assert.False(File.Exists(Path.Combine(Target, "repo-init", "leak.txt")));
    }
}
