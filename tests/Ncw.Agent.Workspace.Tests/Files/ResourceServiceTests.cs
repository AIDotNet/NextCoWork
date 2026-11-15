using Ncw.Agent.Environments;
using Ncw.Agent.Workspace.Files;

namespace Ncw.Agent.Workspace.Tests.Files;

/// <summary>
/// markdown 资源的单文件读写(移植自 2.x <c>markdown-resource.test.ts</c>)。几乎每条都在回答同一个问题:<b>用户的文件会不会在保存一次之后变形</b>
/// —— 所以用例的形状基本是「写进去 → 读回来 → 和原来比」。
/// </summary>
public sealed class ResourceServiceTests
{
    private sealed class Rig : IDisposable
    {
        public Rig() => Location = new ResourceLocation(Project.Env, Project.RootPath, ".next-cowork/commands");

        public TempProject Project { get; } = new();

        public ResourceLocation Location { get; }

        public ResourceService Service { get; } = new(new ResourceOptions { IsKnownTool = t => t is "Read" or "Grep" or "Bash" });

        public string FileOf(string name) => Project.Abs($".next-cowork/commands/{name}.md");

        public void Dispose() => Project.Dispose();

        public ResourceLocation For(string dir) => Location with { Directory = $".next-cowork/{dir}" };
    }

    private static FrontmatterItem[] Fm(params (string Key, string Value)[] pairs) => [.. pairs.Select(p => new FrontmatterItem(p.Key, p.Value))];

    [Fact]
    public async Task A_missing_resource_is_not_found()
    {
        using var rig = new Rig();

        await FilesRig.Fails(() => rig.Service.GetAsync(rig.Location, ResourceKind.Command, "deploy"), FilesErrorCode.NotFound);
    }

    [Fact]
    public async Task Reads_frontmatter_body_and_revision()
    {
        using var rig = new Rig();
        rig.Project.Write(".next-cowork/commands/deploy.md", "---\ndescription: 部署\nargument-hint: <env>\n---\n跑部署脚本\n");

        var got = await rig.Service.GetAsync(rig.Location, ResourceKind.Command, "deploy");

        Assert.Equal([new FrontmatterItem("description", "部署"), new FrontmatterItem("argument-hint", "<env>")], got.Frontmatter);
        Assert.Equal("跑部署脚本\n", got.Body);
        Assert.Matches(@"^\d+:\d+$", got.Revision);
    }

    [Fact]
    public async Task Keys_the_app_does_not_know_are_read_too()
    {
        using var rig = new Rig();
        rig.Project.Write(".next-cowork/commands/x.md", "---\ndescription: x\ncolor: blue\n---\n正文");

        Assert.Contains(new FrontmatterItem("color", "blue"), (await rig.Service.GetAsync(rig.Location, ResourceKind.Command, "x")).Frontmatter);
    }

    [Fact]
    public async Task Syntax_the_parser_cannot_read_is_reported_in_skipped()
    {
        using var rig = new Rig();
        rig.Project.Write(".next-cowork/commands/x.md", "---\ndescription: x\nnested:\n  a:\n    b: 1\n---\n正文");

        Assert.NotEmpty((await rig.Service.GetAsync(rig.Location, ResourceKind.Command, "x")).Skipped);
    }

    // ── 并发保护 ──

    [Fact]
    public async Task Creating_over_an_existing_file_is_a_conflict()
    {
        using var rig = new Rig();
        rig.Project.Write(".next-cowork/commands/deploy.md", "旧内容");

        await FilesRig.Fails(() => rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "deploy", [], "新内容", null), FilesErrorCode.Conflict);

        Assert.Equal("旧内容", rig.Project.Read(".next-cowork/commands/deploy.md"));
    }

    [Fact]
    public async Task A_stale_revision_is_refused()
    {
        using var rig = new Rig();
        var created = await rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "deploy", [], "v1", null);
        // 别人先存了一次
        await rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "deploy", [], "v2", created.Revision);

        await FilesRig.Fails(() => rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "deploy", [], "v3", created.Revision), FilesErrorCode.Conflict);

        Assert.Equal("v2", rig.Project.Read(".next-cowork/commands/deploy.md"));
    }

    [Fact]
    public async Task Editing_a_deleted_file_is_a_conflict()
    {
        using var rig = new Rig();

        await FilesRig.Fails(() => rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "gone", [], "x", "123:4"), FilesErrorCode.Conflict);
    }

    [Fact]
    public async Task A_matching_revision_writes_and_returns_a_new_one()
    {
        using var rig = new Rig();
        var v1 = await rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "deploy", [], "v1", null);
        File.SetLastWriteTimeUtc(rig.FileOf("deploy"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // 让两次的修改时间不同

        var current = await rig.Service.GetAsync(rig.Location, ResourceKind.Command, "deploy");
        var v2 = await rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "deploy", [], "v2 longer", current.Revision);

        Assert.NotEqual(current.Revision, v2.Revision);
        Assert.Equal("v2 longer", v2.Body);
        Assert.NotNull(v1);
    }

    // ── 往返 ──

    [Fact]
    public async Task A_command_round_trips_exactly()
    {
        using var rig = new Rig();
        var fm = Fm(("description", "部署到指定环境"), ("argument-hint", "<env>"));

        var got = await rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "deploy", fm, "把 $ARGUMENTS 部署上去\n", null);

        Assert.Equal(fm.OrderBy(f => f.Key), got.Frontmatter.OrderBy(f => f.Key));
        Assert.Equal("把 $ARGUMENTS 部署上去\n", got.Body);
    }

    [Fact]
    public async Task List_fields_round_trip_for_agents()
    {
        using var rig = new Rig();
        FrontmatterItem[] fm = [new("name", "reviewer"), new("description", "审代码"), new("tools", Items: ["Read", "Grep"]), new("model", "sonnet")];

        var got = await rig.Service.SaveAsync(rig.For("agents"), ResourceKind.Agent, "reviewer", fm, "你是一个审查者", null);

        Assert.Equal(["Read", "Grep"], got.Frontmatter.Single(f => f.Key == "tools").Items);
        Assert.Equal("sonnet", got.Frontmatter.Single(f => f.Key == "model").Value);
    }

    [Fact]
    public async Task Unknown_keys_survive_a_save()
    {
        using var rig = new Rig();
        var fm = Fm(("description", "x"), ("color", "blue"), ("custom-thing", "y"));

        var got = await rig.Service.SaveAsync(rig.For("agents"), ResourceKind.Agent, "a", fm, "body", null);

        Assert.Equal(["color", "custom-thing", "description"], got.Frontmatter.Select(f => f.Key).Order());
    }

    [Fact]
    public async Task Key_order_is_decided_by_the_kind_and_stable()
    {
        using var rig = new Rig();
        await rig.Service.SaveAsync(rig.For("agents"), ResourceKind.Agent, "a", [new("model", "m"), new("description", "d"), new("name", "n"), new("tools", Items: ["T"])], "", null);

        var keys = File.ReadAllLines(rig.Project.Abs(".next-cowork/agents/a.md")).Where(l => l.Length > 0 && char.IsAsciiLetterLower(l[0])).Select(l => l[..l.IndexOf(':')]);

        Assert.Equal(["name", "description", "tools", "model"], keys);
    }

    [Fact]
    public async Task A_command_without_frontmatter_writes_no_front_block_and_the_body_is_clamped()
    {
        using var rig = new Rig();

        await rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "plain", [], "只有正文", null);
        Assert.Equal("只有正文", rig.Project.Read(".next-cowork/commands/plain.md"));

        var huge = new string('x', 70 * 1024);
        var big = await rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "huge", [], huge, null);
        Assert.True(big.Body.Length <= ResourceService.BodyMax);
    }

    // ── 名字与边界 ──

    [Theory]
    [InlineData(ResourceKind.Command, "deploy", true)]
    [InlineData(ResourceKind.Command, "Deploy_2", true)]
    [InlineData(ResourceKind.Agent, "code-reviewer", true)]
    [InlineData(ResourceKind.Agent, "Code", false)]
    [InlineData(ResourceKind.Agent, "a_b", false)]
    [InlineData(ResourceKind.Mode, "-x", false)]
    [InlineData(ResourceKind.Command, "", false)]
    [InlineData(ResourceKind.Command, "../x", false)]
    [InlineData(ResourceKind.Command, "a/b", false)]
    [InlineData(ResourceKind.Command, "a.b", false)]
    public void Name_rules_per_kind(ResourceKind kind, string name, bool valid)
    {
        Assert.Equal(valid, ResourceService.IsValidName(kind, name));
    }

    [Fact]
    public async Task Invalid_names_never_reach_the_file_system()
    {
        using var rig = new Rig();

        await FilesRig.Fails(() => rig.Service.GetAsync(rig.Location, ResourceKind.Command, "../x"), FilesErrorCode.InvalidName);
        await FilesRig.Fails(() => rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "a/b", [], "x", null), FilesErrorCode.InvalidName);
        await FilesRig.Fails(() => rig.Service.DeleteAsync(rig.Location, ResourceKind.Command, ".."), FilesErrorCode.InvalidName);
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        using var rig = new Rig();
        await rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "deploy", [], "x", null);

        await rig.Service.DeleteAsync(rig.Location, ResourceKind.Command, "deploy");
        await rig.Service.DeleteAsync(rig.Location, ResourceKind.Command, "deploy");

        Assert.False(File.Exists(rig.FileOf("deploy")));
    }

    [Fact]
    public async Task A_resource_file_that_is_a_link_out_of_the_directory_cannot_be_read_or_deleted()
    {
        // commands/x.md -> 目录外的文件:名字合法,只有解析真实路径才看得出来
        Assert.SkipWhen(FilesRig.SymlinksUnavailable, "symbolic links need privileges on Windows");
        using var rig = new Rig();
        var secret = Path.Combine(rig.Project.Outside, "secret.md");
        File.WriteAllText(secret, "secret");
        Directory.CreateDirectory(rig.Project.Abs(".next-cowork/commands"));
        File.CreateSymbolicLink(rig.FileOf("x"), secret);

        await FilesRig.Fails(() => rig.Service.DeleteAsync(rig.Location, ResourceKind.Command, "x"), FilesErrorCode.Permission);
        await FilesRig.Fails(() => rig.Service.GetAsync(rig.Location, ResourceKind.Command, "x"), FilesErrorCode.Permission);

        Assert.True(File.Exists(secret));
    }

    [Fact]
    public async Task A_resources_directory_that_is_a_link_out_of_the_project_is_refused()
    {
        Assert.SkipWhen(FilesRig.SymlinksUnavailable, "symbolic links need privileges on Windows");
        using var rig = new Rig();
        Directory.CreateDirectory(rig.Project.Abs(".next-cowork"));
        Directory.CreateSymbolicLink(rig.Project.Abs(".next-cowork/commands"), rig.Project.Outside);

        await FilesRig.Fails(() => rig.Service.SaveAsync(rig.Location, ResourceKind.Command, "x", [], "body", null), FilesErrorCode.Permission);

        Assert.Empty(Directory.GetFileSystemEntries(rig.Project.Outside));
    }

    // ── 模式 ──

    [Fact]
    public async Task Builtin_modes_are_read_only()
    {
        using var rig = new Rig();
        var fm = Fm(("name", "Code"), ("description", "d"));

        await FilesRig.Fails(() => rig.Service.SaveAsync(rig.For("modes"), ResourceKind.Mode, "code", fm, "prompt", null), FilesErrorCode.BuiltinReadOnly);
        await FilesRig.Fails(() => rig.Service.DeleteAsync(rig.For("modes"), ResourceKind.Mode, "plan"), FilesErrorCode.BuiltinReadOnly);
    }

    [Fact]
    public async Task A_mode_needs_a_name_a_description_and_a_prompt()
    {
        using var rig = new Rig();
        var modes = rig.For("modes");

        var noName = await FilesRig.Fails(() => rig.Service.SaveAsync(modes, ResourceKind.Mode, "m", Fm(("description", "d")), "p", null), FilesErrorCode.InvalidResource);
        await FilesRig.Fails(() => rig.Service.SaveAsync(modes, ResourceKind.Mode, "m", Fm(("name", "M")), "p", null), FilesErrorCode.InvalidResource);
        await FilesRig.Fails(() => rig.Service.SaveAsync(modes, ResourceKind.Mode, "m", Fm(("name", "M"), ("description", "d")), "   ", null), FilesErrorCode.InvalidResource);

        Assert.Contains("name", noName.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mode_tools_must_be_known_non_empty_and_cover_the_required_ones()
    {
        using var rig = new Rig();
        var modes = rig.For("modes");
        FrontmatterItem[] Mode(params FrontmatterItem[] extra) => [new FrontmatterItem("name", "M"), new FrontmatterItem("description", "d"), .. extra];

        await FilesRig.Fails(() => rig.Service.SaveAsync(modes, ResourceKind.Mode, "m", Mode(new FrontmatterItem("tools", Items: [])), "p", null), FilesErrorCode.InvalidResource);
        await FilesRig.Fails(() => rig.Service.SaveAsync(modes, ResourceKind.Mode, "m", Mode(new FrontmatterItem("requiredTools", Items: ["Read"])), "p", null), FilesErrorCode.InvalidResource);
        await FilesRig.Fails(() => rig.Service.SaveAsync(modes, ResourceKind.Mode, "m", Mode(new FrontmatterItem("tools", Items: ["Read"]), new FrontmatterItem("requiredTools", Items: ["Grep"])), "p", null), FilesErrorCode.InvalidResource);
        await FilesRig.Fails(() => rig.Service.SaveAsync(modes, ResourceKind.Mode, "m", Mode(new FrontmatterItem("tools", Items: ["Nope"])), "p", null), FilesErrorCode.InvalidResource);

        var ok = await rig.Service.SaveAsync(modes, ResourceKind.Mode, "m", Mode(new FrontmatterItem("tools", "Read, Grep"), new FrontmatterItem("requiredTools", Items: ["Read"])), "p", null);
        Assert.Equal("m", ok.Name);
    }
}
