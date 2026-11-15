using Ncw.Client.Actions;
using Ncw.Integration.Tests.Support;
using Ncw.Protocol.Methods;

namespace Ncw.Integration.Tests;

/// <summary>
/// <see cref="FilesActions"/> 经真实 Agent(进程内)+ 真实 <c>DeviceLink</c>:列目录 / 读 / 写 / 变更 / 搜索 / markdown 资源全流程。
/// 文件内容不进 Store:结果直接返回给调用方;失败是结构化的 <see cref="ActionResult{T}"/>(稳定的 <c>files.*</c> key,界面翻译)。
/// </summary>
public sealed class FilesLinkTests : IAsyncLifetime
{
    private readonly OrchestrationRig _rig = new();
    private Controller _c = null!;
    private FilesActions _files = null!;
    private string _device = "";
    private string _project = "";

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _c = await _rig.StartControllerAsync("files-ctrl");
        _files = new FilesActions(_c.Devices);
        var reference = await _c.Link.AddProjectAsync(_rig.Workspace);
        _device = reference.DeviceId;
        _project = reference.WorkspaceId;
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private string Abs(string relative) => Path.Combine(_rig.Workspace, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public async Task List_read_edit_and_save_with_the_revision_the_read_returned()
    {
        Directory.CreateDirectory(Abs("src"));
        File.WriteAllText(Abs("src/app.ts"), "const a = 1\n");

        var listing = await _files.ListAsync(_device, _project);
        Assert.True(listing.IsOk);
        Assert.Equal(["src"], listing.Value!.Entries.Select(e => e.Name));

        var read = await _files.ReadAsync(_device, _project, "src/app.ts");
        Assert.Equal("const a = 1\n", read.Value!.Content);

        var saved = await _files.WriteTextAsync(_device, _project, "src/app.ts", "const a = 2\n", read.Value.Revision);
        Assert.True(saved.IsOk);
        Assert.Equal("const a = 2\n", File.ReadAllText(Abs("src/app.ts")));

        // 用旧修订号再存:冲突,稳定 key,文件不变
        var stale = await _files.WriteTextAsync(_device, _project, "src/app.ts", "const a = 3\n", read.Value.Revision);
        Assert.Equal(ActionOutcome.Conflict, stale.Outcome);
        Assert.Equal(FilesErrorKeys.Conflict, stale.ErrorKey);
        Assert.Equal("const a = 2\n", File.ReadAllText(Abs("src/app.ts")));
    }

    [Fact]
    public async Task Create_rename_move_copy_and_delete_travel_as_one_intent_each()
    {
        Assert.True((await _files.CreateDirectoryAsync(_device, _project, "docs")).IsOk);
        Assert.True((await _files.CreateFileAsync(_device, _project, "docs/a.md")).IsOk);
        File.WriteAllText(Abs("docs/a.md"), "# a");
        Assert.True((await _files.RenameAsync(_device, _project, "docs/a.md", "docs/b.md")).IsOk);
        Assert.True((await _files.CopyAsync(_device, _project, "docs", "docs-copy")).IsOk);
        Assert.True((await _files.MoveAsync(_device, _project, "docs-copy/b.md", "c.md")).IsOk);
        Assert.Equal("# a", File.ReadAllText(Abs("c.md")));

        var exists = await _files.CreateFileAsync(_device, _project, "c.md");
        Assert.Equal(ActionOutcome.Conflict, exists.Outcome);
        Assert.Equal(FilesErrorKeys.Exists, exists.ErrorKey);

        var deleted = await _files.DeleteAsync(_device, _project, "c.md");
        Assert.True(deleted.IsOk);
        Assert.False(File.Exists(Abs("c.md")));
        Assert.True(File.Exists(Abs(deleted.Value!.RecoveryPath!)));
    }

    [Fact]
    public async Task A_path_outside_the_project_is_an_invalid_result_with_a_key_not_an_exception()
    {
        var read = await _files.ReadAsync(_device, _project, "../secret.txt");

        Assert.Equal(ActionOutcome.Invalid, read.Outcome);
        Assert.Equal(FilesErrorKeys.InvalidPath, read.ErrorKey);
        Assert.Equal(ActionOutcome.NotFound, (await _files.ReadAsync(_device, _project, "nope.txt")).Outcome);
        Assert.Equal(ActionOutcome.Unavailable, (await _files.ReadAsync("no-such-device", _project, "a.txt")).Outcome);
    }

    [Fact]
    public async Task Search_suggest_and_chunked_reads_work_through_the_actions()
    {
        Directory.CreateDirectory(Abs("src"));
        File.WriteAllText(Abs("src/a.cs"), "class Needle {}");
        File.WriteAllBytes(Abs("big.bin"), new byte[FilesLimits.RangeMax + 10]);

        var found = await _files.SearchAsync(_device, _project, new FilesSearchParams("", "needle", Glob: "*.cs"));
        Assert.Equal(["src/a.cs"], found.Value!.Matches.Select(m => m.Path));
        Assert.Equal("src/a.cs", (await _files.SuggestAsync(_device, _project, "a.cs")).Value!.Suggestions.Single().Path);

        var first = await _files.ReadRangeAsync(_device, _project, "big.bin", 0, FilesLimits.RangeMax);
        var second = await _files.ReadRangeAsync(_device, _project, "big.bin", FilesLimits.RangeMax, FilesLimits.RangeMax, first.Value!.Revision);
        Assert.False(first.Value.Eof);
        Assert.True(second.Value!.Eof);
        Assert.Equal(10, second.Value.Length);

        var bad = await _files.SearchAsync(_device, _project, new FilesSearchParams("", @"(a)\1", Regex: true));
        Assert.Equal(ActionOutcome.Invalid, bad.Outcome);
        Assert.Equal(FilesErrorKeys.InvalidPattern, bad.ErrorKey);
    }

    [Fact]
    public async Task Markdown_resources_are_created_edited_and_deleted_with_conflict_protection()
    {
        var reference = new FilesResourceRefParams(ResourceKinds.Command, ResourceScopes.Project, "deploy", _project);
        Assert.Equal(ActionOutcome.NotFound, (await _files.GetResourceAsync(_device, reference)).Outcome);

        var created = await _files.SaveResourceAsync(_device, new FilesResourceSaveParams(
            ResourceKinds.Command, ResourceScopes.Project, "deploy", [new FrontmatterEntryDto("description", "部署"), new FrontmatterEntryDto("color", "blue")], "run $ARGUMENTS", _project));
        Assert.True(created.IsOk);
        Assert.Contains(created.Value!.Frontmatter, e => e is { Key: "color", Value: "blue" }); // 不认识的键原样带回

        var again = await _files.SaveResourceAsync(_device, new FilesResourceSaveParams(ResourceKinds.Command, ResourceScopes.Project, "deploy", [], "x", _project));
        Assert.Equal(ActionOutcome.Conflict, again.Outcome);

        Assert.True((await _files.DeleteResourceAsync(_device, reference)).IsOk);
        Assert.False(File.Exists(Abs(".next-cowork/commands/deploy.md")));

        // 本机连接是 owner:全局作用域也能写
        var global = await _files.SaveResourceAsync(_device, new FilesResourceSaveParams(ResourceKinds.Agent, ResourceScopes.Global, "helper", [new FrontmatterEntryDto("description", "d")], "prompt"));
        Assert.True(global.IsOk);
        Assert.True(File.Exists(Path.Combine(_rig.DataRoot, "agents", "helper.md")));
    }
}
