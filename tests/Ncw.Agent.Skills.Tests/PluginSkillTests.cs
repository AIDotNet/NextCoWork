using Ncw.Agent.Environments;

namespace Ncw.Agent.Skills.Tests;

/// <summary>
/// 插件贡献的 Skill 进目录,移植自 2.x <c>kernel/__tests__/plugin-skill-load.test.ts</c>。插件宿主还没接(Jaunt 宿主随 M4 的 plugins),这里只验证「插件层」的接口位置和语义:
/// 优先级最低、撞名先来的赢并出诊断、坏包不拖垮整次扫描、软链出包的 SKILL.md 读不到、SSH 会话里的资产判定、提供者的容错。
/// </summary>
public sealed class PluginSkillTests : IDisposable
{
    private readonly Fx _fx = new();

    public void Dispose() => _fx.Dispose();

    private string PluginsRoot => Path.Combine(_fx.Root, "plugins");

    private static string Md(string? description, string body = "照着这个做。") => Fx.Md(description, body);

    /// <summary>造一个插件包并返回它对这条 Skill 的贡献声明。目录形状和真实安装后一致:<c>&lt;plugins&gt;/&lt;id&gt;/skills/&lt;name&gt;/SKILL.md</c>。</summary>
    private PluginSkillRoot Contribute(string pluginId, string name, string content)
    {
        var dir = Path.Combine(PluginsRoot, pluginId, "skills", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
        return new PluginSkillRoot(pluginId, _fx.Env, _fx.P(dir));
    }

    [Fact]
    public async Task Reads_the_directory_the_manifest_names_and_records_which_plugin_brought_it()
    {
        var result = await _fx.ScanAsync([Contribute("acme.pdf", "pdf-tools", Md("Work with PDFs"))]);

        var skill = Assert.Single(result.Skills, s => s.Name == "pdf-tools");
        Assert.Equal("Work with PDFs", skill.Description);
        Assert.Equal((SkillScope.Plugin, SkillSourceKind.Plugin), (skill.Scope, skill.Source.Kind));
        Assert.Equal("acme.pdf", skill.Source.PluginId); // 界面据此藏掉卸载按钮、详情页据此反查
    }

    [Fact]
    public async Task Invariant_the_source_path_is_absolute_because_the_skill_tool_hands_it_to_the_model()
    {
        var result = await _fx.ScanAsync([Contribute("acme.pdf", "pdf-tools", Md("Work with PDFs"))]);

        var path = result.Skills.Single(s => s.Name == "pdf-tools").Source.Path;
        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith(Path.Combine("skills", "pdf-tools", "SKILL.md"), path);
    }

    [Fact]
    public async Task Without_plugin_roots_there_are_no_plugin_skills_empty_means_none_not_go_and_look()
    {
        Assert.Empty((await _fx.ScanAsync()).Skills);
    }

    [Fact]
    public async Task Invariant_a_users_own_global_skill_beats_the_plugins_one()
    {
        // 插件提供的是一个合理默认,用户自己写的永远说了算。
        Fx.Put(_fx.GlobalSkills, "commit-style", Md("用户自己的规范"));

        var result = await _fx.ScanAsync([Contribute("acme.git", "commit-style", Md("插件自带的规范"))]);

        var hit = result.Skills.Single(s => s.Name == "commit-style");
        Assert.Equal(("用户自己的规范", SkillScope.Global, null), (hit.Description, hit.Scope, hit.Source.PluginId));
    }

    [Fact]
    public async Task A_project_skill_beats_the_plugins_one_too()
    {
        Fx.Put(_fx.ProjectSkills, "commit-style", Md("这个仓库的规范"));

        var result = await _fx.ScanAsync([Contribute("acme.git", "commit-style", Md("插件自带的规范"))]);

        Assert.Equal(SkillScope.Project, result.Skills.Single(s => s.Name == "commit-style").Scope);
    }

    [Fact]
    public async Task Invariant_when_two_plugins_clash_the_first_wins_and_the_authors_can_tell_what_happened()
    {
        var result = await _fx.ScanAsync([
            Contribute("acme.first", "shared-name", Md("第一个")),
            Contribute("acme.second", "shared-name", Md("第二个")),
        ]);

        var winner = Assert.Single(result.Skills, s => s.Name == "shared-name");
        Assert.Equal("acme.first", winner.Source.PluginId);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Key == SkillDiagnosticKeys.PluginNameClash);
        Assert.Contains("acme.second", diagnostic.Message);
        Assert.Contains("acme.first", diagnostic.Message);
    }

    [Fact]
    public async Task A_contribution_without_skill_md_is_skipped_and_the_others_load()
    {
        var empty = Path.Combine(PluginsRoot, "acme.broken", "skills", "nothing");
        Directory.CreateDirectory(empty);

        var result = await _fx.ScanAsync([new PluginSkillRoot("acme.broken", _fx.Env, _fx.P(empty)), Contribute("acme.good", "works", Md("Fine"))]);

        Assert.Equal(["works"], result.Skills.Select(s => s.Name));
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public async Task A_plugin_skill_without_a_description_is_void_and_says_why()
    {
        var result = await _fx.ScanAsync([Contribute("acme.pdf", "no-desc", Md(null))]);

        Assert.Empty(result.Skills);
        Assert.True(Fx.Said(result, SkillDiagnosticKeys.DescriptionMissing));
    }

    [Fact]
    public async Task Invariant_a_skill_md_that_is_a_symlink_out_of_the_package_is_not_read()
    {
        // zip 安装拒了符号链接,但目录安装(开发时装本地目录)没有这道门:指向 ~/.ssh/id_rsa 的软链会被原样读进正文,而正文是要进模型上下文的。
        var secret = Path.Combine(_fx.Root, "secret.md");
        File.WriteAllText(secret, "---\ndescription: leaked\n---\n私钥内容\n");
        var dir = Path.Combine(PluginsRoot, "acme.evil", "skills", "sneaky");
        Directory.CreateDirectory(dir);
        File.CreateSymbolicLink(Path.Combine(dir, "SKILL.md"), secret);

        var result = await _fx.ScanAsync([new PluginSkillRoot("acme.evil", _fx.Env, _fx.P(dir))]);

        Assert.Empty(result.Skills);
        Assert.Contains(result.Diagnostics, d => d.Key == SkillDiagnosticKeys.PluginSkillOutside);
    }

    [Fact]
    public async Task Invariant_a_plugin_skill_with_assets_is_client_assets_only_in_a_remote_session()
    {
        // 插件包在设备本机,而这一轮的命令跑在服务器上:不管它的话,模型会照着正文去跑一个只存在于设备上的 scripts/check.py。
        var contribution = Contribute("acme.pdf", "with-assets", Md("Has scripts"));
        File.WriteAllText(Path.Combine(contribution.Directory.Value, "check.py"), "print(1)");

        var result = await SkillLoader.ScanAsync(_fx.Input(projectEnvironment: new SshLikeEnvironment(_fx.Env), plugins: [contribution]));

        Assert.True(result.Skills.Single(s => s.Name == "with-assets").ClientAssetsOnly);
    }

    [Fact]
    public async Task A_text_only_plugin_skill_is_still_usable_in_a_remote_session()
    {
        var result = await SkillLoader.ScanAsync(_fx.Input(projectEnvironment: new SshLikeEnvironment(_fx.Env), plugins: [Contribute("acme.pdf", "text-only", Md("Just instructions"))]));

        Assert.False(result.Skills.Single(s => s.Name == "text-only").ClientAssetsOnly);
    }

    private sealed class Provider(Func<IReadOnlyList<PluginSkillRoot>> current) : ISkillPluginSource
    {
        public IReadOnlyList<PluginSkillRoot> Current() => current();
    }

    [Fact]
    public void No_provider_means_none_an_unstarted_plugin_system_is_not_an_error()
    {
        Assert.Empty(((ISkillPluginSource?)null).CurrentOrEmpty());
    }

    [Fact]
    public async Task Invariant_a_provider_that_throws_does_not_stop_every_skill_from_scanning()
    {
        // 插件管理器坏了,用户自己装的 skill 不该跟着一起消失。
        var broken = new Provider(() => throw new InvalidOperationException("manager is gone"));
        Assert.Empty(broken.CurrentOrEmpty());
        Fx.Put(_fx.GlobalSkills, "mine", Md("用户自己的"));

        var result = await _fx.ScanAsync(broken.CurrentOrEmpty());

        Assert.Equal(["mine"], result.Skills.Select(s => s.Name));
    }

    [Fact]
    public void The_provider_is_asked_every_time_so_disabling_a_plugin_takes_effect_next_turn()
    {
        var live = new List<PluginSkillRoot> { new("acme.pdf", _fx.Env, _fx.P(Path.Combine(PluginsRoot, "acme.pdf", "skills", "x"))) };
        var provider = new Provider(() => [.. live]);

        Assert.Single(provider.CurrentOrEmpty());
        live.Clear();
        Assert.Empty(provider.CurrentOrEmpty());
    }
}
