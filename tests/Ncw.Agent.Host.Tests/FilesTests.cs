using Ncw.Agent.Host.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// <c>files.*</c> 经真实 Agent(进程内)+ 真实控制端:本机 owner、经局域网承载配对的 viewer / member。
/// 角色由路由统一检查(viewer 只读、member 可写、全局资源 owner),路径边界、修订号冲突、分页、错误细分 key 都在线上验证。
/// </summary>
public sealed class FilesTests : IAsyncLifetime
{
    private readonly ConversationRig _rig = new();
    private DeviceClient _owner = null!;
    private ProjectDto _project = null!;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _project = await _rig.AddProjectAsync(_owner);
        var status = await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true));
        _port = status.Port!.Value;
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private string Id => _project.WorkspaceId;

    private string Abs(string relative) => Path.Combine(_rig.WorkspaceDir, relative.Replace('/', Path.DirectorySeparatorChar));

    private void Put(string relative, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Abs(relative))!);
        File.WriteAllText(Abs(relative), content);
    }

    private async Task<DeviceClient> ConnectAsync(string name, string role)
    {
        var identity = PeerIdentity.Create(name);
        var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, _rig.Host.Remote.PairingCode!, name);
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));
        var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
        var client = await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
        Assert.Equal(role, client.Welcome.Role);
        return client;
    }

    private static async Task<ProtocolException> Fails<T>(Func<Task<T>> call)
    {
        return await Assert.ThrowsAsync<ProtocolException>(call);
    }

    private static string KeyOf(ProtocolException e) =>
        FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<FilesErrorDto>()).Key;

    [Fact]
    public async Task The_device_announces_the_files_capability()
    {
        await using var client = await _rig.ConnectAsync("probe");

        Assert.Contains(Capabilities.Files, client.Welcome.Features);
    }

    [Fact]
    public async Task Owner_lists_reads_writes_and_mutates_end_to_end()
    {
        Put("src/app.ts", "const a = 1\n");
        Put("README.md", "# hi");

        var root = await _owner.CallAsync(FilesMethods.List, new FilesListParams(Id));
        Assert.Equal(["src", "README.md"], root.Entries.Select(e => e.Name));
        Assert.Equal("dir", root.Entries[0].Kind);
        Assert.Equal("src/app.ts", (await _owner.CallAsync(FilesMethods.List, new FilesListParams(Id, "src"))).Entries.Single().Path);

        var read = await _owner.CallAsync(FilesMethods.Read, new FilesReadParams(Id, "src/app.ts"));
        Assert.Equal("text", read.Kind);
        Assert.Equal("const a = 1\n", read.Content);

        var written = await _owner.CallAsync(FilesMethods.Write, new FilesWriteParams(Id, "src/app.ts", read.Revision, Content: "const a = 2\n"));
        Assert.Equal("const a = 2\n", File.ReadAllText(Abs("src/app.ts")));
        Assert.Equal((await _owner.CallAsync(FilesMethods.Read, new FilesReadParams(Id, "src/app.ts"))).Revision, written.Revision);

        await _owner.CallAsync(FilesMethods.Mutate, new FilesMutateParams(Id, "docs", FileOperations.CreateDirectory));
        await _owner.CallAsync(FilesMethods.Mutate, new FilesMutateParams(Id, "README.md", FileOperations.Rename, "docs/README.md"));
        Assert.True(File.Exists(Abs("docs/README.md")));
        var deleted = await _owner.CallAsync(FilesMethods.Mutate, new FilesMutateParams(Id, "docs/README.md", FileOperations.Delete));
        Assert.False(File.Exists(Abs("docs/README.md")));
        Assert.StartsWith(".next-cowork/trash/", deleted.RecoveryPath, StringComparison.Ordinal);
        Assert.True(File.Exists(Abs(deleted.RecoveryPath!)));
    }

    [Fact]
    public async Task Pagination_and_sorting_travel_over_the_wire()
    {
        foreach (var n in new[] { "e", "d", "c", "b", "a" })
        {
            Put($"{n}.txt", n);
        }

        var first = await _owner.CallAsync(FilesMethods.List, new FilesListParams(Id, Limit: 2));
        var rest = await _owner.CallAsync(FilesMethods.List, new FilesListParams(Id, Offset: 2, Limit: 10));

        Assert.Equal(["a.txt", "b.txt"], first.Entries.Select(e => e.Name));
        Assert.True(first.Truncated);
        Assert.Equal(5, first.Total);
        Assert.Equal(["c.txt", "d.txt", "e.txt"], rest.Entries.Select(e => e.Name));
        Assert.False(rest.Truncated);
    }

    [Fact]
    public async Task Large_files_are_read_in_chunks_that_fit_a_frame()
    {
        var bytes = new byte[FilesLimits.RangeMax * 2 + 777];
        new Random(3).NextBytes(bytes);
        Directory.CreateDirectory(_rig.WorkspaceDir);
        File.WriteAllBytes(Abs("movie.bin"), bytes);

        var joined = new List<byte>();
        string? revision = null;
        long offset = 0;
        while (true)
        {
            var chunk = await _owner.CallAsync(FilesMethods.ReadRange, new FilesReadRangeParams(Id, "movie.bin", offset, FilesLimits.RangeMax, revision));
            joined.AddRange(chunk.Data);
            offset += chunk.Length;
            revision = chunk.Revision;
            if (chunk.Eof)
            {
                break;
            }
        }

        Assert.Equal(bytes, joined);
    }

    [Fact]
    public async Task Binary_and_chunked_images_are_described_not_inlined()
    {
        File.WriteAllBytes(Abs("doc.pdf"), "%PDF-1.4"u8.ToArray());
        using (var stream = new FileStream(Abs("big.png"), FileMode.Create))
        {
            stream.SetLength(FilesLimits.InlineImageMax + 1L);
        }

        var pdf = await _owner.CallAsync(FilesMethods.Read, new FilesReadParams(Id, "doc.pdf"));
        var big = await _owner.CallAsync(FilesMethods.Read, new FilesReadParams(Id, "big.png"));

        Assert.Equal(("binary", "unsupported"), (pdf.Kind, pdf.Reason));
        Assert.Equal("image", big.Kind);
        Assert.True(big.Chunked);
        Assert.Null(big.Data);
    }

    [Fact]
    public async Task A_stale_revision_is_a_conflict_with_a_stable_key_and_the_file_is_untouched()
    {
        Put("a.txt", "one");
        var read = await _owner.CallAsync(FilesMethods.Read, new FilesReadParams(Id, "a.txt"));
        File.WriteAllText(Abs("a.txt"), "two");

        var e = await Fails(() => _owner.CallAsync(FilesMethods.Write, new FilesWriteParams(Id, "a.txt", read.Revision, Content: "three")));

        Assert.Equal(ErrorCodes.Conflict, e.Code);
        Assert.Equal(FilesErrorKeys.Conflict, KeyOf(e));
        Assert.Equal("two", File.ReadAllText(Abs("a.txt")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    public async Task Paths_that_leave_the_project_are_refused_for_every_method(string path)
    {
        var outside = await Fails(() => _owner.CallAsync(FilesMethods.Read, new FilesReadParams(Id, path)));
        Assert.Equal(ErrorCodes.InvalidRequest, outside.Code);
        Assert.Equal(FilesErrorKeys.InvalidPath, KeyOf(outside));

        Assert.Equal(ErrorCodes.InvalidRequest, (await Fails(() => _owner.CallAsync(FilesMethods.List, new FilesListParams(Id, path)))).Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await Fails(() => _owner.CallAsync(FilesMethods.Write, new FilesWriteParams(Id, path, "r", Content: "x")))).Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await Fails(() => _owner.CallAsync(FilesMethods.Mutate, new FilesMutateParams(Id, path, FileOperations.CreateFile)))).Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await Fails(() => _owner.CallAsync(FilesMethods.Search, new FilesSearchParams(Id, "x", path)))).Code);
    }

    [Fact]
    public async Task A_link_out_of_the_project_cannot_be_used_to_create_files_or_to_delete()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links need privileges on Windows");
        var outside = Path.Combine(Path.GetTempPath(), $"ncw-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            Directory.CreateSymbolicLink(Abs("escape"), outside);

            var create = await Fails(() => _owner.CallAsync(FilesMethods.Mutate, new FilesMutateParams(Id, "escape/new.txt", FileOperations.CreateFile)));
            var delete = await Fails(() => _owner.CallAsync(FilesMethods.Mutate, new FilesMutateParams(Id, "escape/keep.txt", FileOperations.Delete)));

            Assert.Equal(ErrorCodes.Forbidden, create.Code);
            Assert.Equal(FilesErrorKeys.Symlink, KeyOf(create));
            Assert.Equal(ErrorCodes.Forbidden, delete.Code);
            Assert.Equal(["keep.txt"], Directory.GetFileSystemEntries(outside).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Fact]
    public async Task A_viewer_can_read_search_and_list_but_every_method_that_changes_anything_is_forbidden()
    {
        Put("a.txt", "needle");
        var read = await _owner.CallAsync(FilesMethods.Read, new FilesReadParams(Id, "a.txt"));
        await using var viewer = await ConnectAsync("viewer-1", "viewer");

        Assert.Single((await viewer.CallAsync(FilesMethods.List, new FilesListParams(Id))).Entries);
        Assert.Equal("needle", (await viewer.CallAsync(FilesMethods.Read, new FilesReadParams(Id, "a.txt"))).Content);
        Assert.Single((await viewer.CallAsync(FilesMethods.Search, new FilesSearchParams(Id, "needle"))).Matches);
        Assert.Contains((await viewer.CallAsync(FilesMethods.Suggest, new FilesSuggestParams(Id, "a"))).Suggestions, s => s.Path == "a.txt");
        Assert.Single((await viewer.CallAsync(FilesMethods.ReadRange, new FilesReadRangeParams(Id, "a.txt", 3, 1))).Data);

        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(FilesMethods.Write, new FilesWriteParams(Id, "a.txt", read.Revision, Content: "x")))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(FilesMethods.Mutate, new FilesMutateParams(Id, "b.txt", FileOperations.CreateFile)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(FilesMethods.ResourceSave, new FilesResourceSaveParams(ResourceKinds.Command, ResourceScopes.Project, "x", [], "b", Id)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(FilesMethods.ResourceDelete, new FilesResourceRefParams(ResourceKinds.Command, ResourceScopes.Project, "x", Id)))).Code);
        Assert.Equal("needle", File.ReadAllText(Abs("a.txt")));
        Assert.False(File.Exists(Abs("b.txt")));
    }

    [Fact]
    public async Task A_member_can_write_and_change_project_files_and_project_resources_but_not_global_ones()
    {
        Put("a.txt", "one");
        var read = await _owner.CallAsync(FilesMethods.Read, new FilesReadParams(Id, "a.txt"));
        await using var member = await ConnectAsync("member-1", "member");

        await member.CallAsync(FilesMethods.Write, new FilesWriteParams(Id, "a.txt", read.Revision, Content: "two"));
        await member.CallAsync(FilesMethods.Mutate, new FilesMutateParams(Id, "b.txt", FileOperations.CreateFile));
        var project = await member.CallAsync(FilesMethods.ResourceSave, new FilesResourceSaveParams(
            ResourceKinds.Command, ResourceScopes.Project, "deploy", [new FrontmatterEntryDto("description", "部署")], "run it", Id));
        Assert.Equal("two", File.ReadAllText(Abs("a.txt")));
        Assert.True(File.Exists(Abs("b.txt")));
        Assert.Equal("部署", project.Frontmatter.Single().Value);
        Assert.True(File.Exists(Abs(".next-cowork/commands/deploy.md")));

        // 全局作用域是设备配置:member 不行,owner 行
        var save = await Fails(() => member.CallAsync(FilesMethods.ResourceSave, new FilesResourceSaveParams(ResourceKinds.Command, ResourceScopes.Global, "g", [], "body")));
        var delete = await Fails(() => member.CallAsync(FilesMethods.ResourceDelete, new FilesResourceRefParams(ResourceKinds.Command, ResourceScopes.Global, "g")));
        Assert.Equal(ErrorCodes.Forbidden, save.Code);
        Assert.Equal(ErrorCodes.Forbidden, delete.Code);
        Assert.False(File.Exists(Path.Combine(_rig.DataRoot, "commands", "g.md")));

        await _owner.CallAsync(FilesMethods.ResourceSave, new FilesResourceSaveParams(ResourceKinds.Command, ResourceScopes.Global, "g", [], "body"));
        Assert.True(File.Exists(Path.Combine(_rig.DataRoot, "commands", "g.md")));
        // 读取 member 可以
        Assert.Equal("body", (await member.CallAsync(FilesMethods.ResourceGet, new FilesResourceRefParams(ResourceKinds.Command, ResourceScopes.Global, "g"))).Body);
    }

    [Fact]
    public async Task Resources_conflict_on_stale_revision_and_builtin_modes_are_read_only()
    {
        var created = await _owner.CallAsync(FilesMethods.ResourceSave, new FilesResourceSaveParams(ResourceKinds.Command, ResourceScopes.Project, "c", [], "v1", Id));
        var again = await Fails(() => _owner.CallAsync(FilesMethods.ResourceSave, new FilesResourceSaveParams(ResourceKinds.Command, ResourceScopes.Project, "c", [], "dup", Id)));
        Assert.Equal(ErrorCodes.Conflict, again.Code);

        File.SetLastWriteTimeUtc(Abs(".next-cowork/commands/c.md"), new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await _owner.CallAsync(FilesMethods.ResourceSave, new FilesResourceSaveParams(ResourceKinds.Command, ResourceScopes.Project, "c", [], "v2 changed", Id, (await _owner.CallAsync(FilesMethods.ResourceGet, new FilesResourceRefParams(ResourceKinds.Command, ResourceScopes.Project, "c", Id))).Revision));
        var stale = await Fails(() => _owner.CallAsync(FilesMethods.ResourceSave, new FilesResourceSaveParams(ResourceKinds.Command, ResourceScopes.Project, "c", [], "v3", Id, created.Revision)));
        Assert.Equal(ErrorCodes.Conflict, stale.Code);

        var builtin = await Fails(() => _owner.CallAsync(FilesMethods.ResourceSave, new FilesResourceSaveParams(
            ResourceKinds.Mode, ResourceScopes.Project, "plan", [new FrontmatterEntryDto("name", "P"), new FrontmatterEntryDto("description", "d")], "x", Id)));
        Assert.Equal(ErrorCodes.Forbidden, builtin.Code);
        Assert.Equal(FilesErrorKeys.BuiltinReadOnly, KeyOf(builtin));

        var badName = await Fails(() => _owner.CallAsync(FilesMethods.ResourceGet, new FilesResourceRefParams(ResourceKinds.Command, ResourceScopes.Project, "../x", Id)));
        Assert.Equal(ErrorCodes.InvalidRequest, badName.Code);
        Assert.Equal(FilesErrorKeys.InvalidName, KeyOf(badName));

        await _owner.CallAsync(FilesMethods.ResourceDelete, new FilesResourceRefParams(ResourceKinds.Command, ResourceScopes.Project, "c", Id));
        await _owner.CallAsync(FilesMethods.ResourceDelete, new FilesResourceRefParams(ResourceKinds.Command, ResourceScopes.Project, "c", Id)); // 幂等
        var gone = await Fails(() => _owner.CallAsync(FilesMethods.ResourceGet, new FilesResourceRefParams(ResourceKinds.Command, ResourceScopes.Project, "c", Id)));
        Assert.Equal(ErrorCodes.NotFound, gone.Code);
    }

    [Fact]
    public async Task Search_and_suggest_run_on_the_device_and_a_bad_regex_is_an_invalid_pattern()
    {
        Put("src/a.cs", "var x = 12;");
        Put("src/b.md", "var x = 12;");

        var found = await _owner.CallAsync(FilesMethods.Search, new FilesSearchParams(Id, @"= \d+;", Glob: "*.cs", Regex: true));
        Assert.Equal(["src/a.cs"], found.Matches.Select(m => m.Path));
        Assert.Equal("src/b.md", (await _owner.CallAsync(FilesMethods.Suggest, new FilesSuggestParams(Id, "b.md"))).Suggestions.Single().Path);

        var bad = await Fails(() => _owner.CallAsync(FilesMethods.Search, new FilesSearchParams(Id, @"(a)\1", Regex: true)));
        Assert.Equal(ErrorCodes.InvalidRequest, bad.Code);
        Assert.Equal(FilesErrorKeys.InvalidPattern, KeyOf(bad));
    }

    [Fact]
    public async Task An_unknown_project_is_not_found_and_commands_need_an_idempotency_key()
    {
        var e = await Fails(() => _owner.CallAsync(FilesMethods.List, new FilesListParams("no-such-project")));
        Assert.Equal(ErrorCodes.NotFound, e.Code);

        // 同一个幂等键重发:只执行一次(第二次拿到同一个结果,不会因为「已存在」失败)
        var first = await _owner.CallAsync(FilesMethods.Mutate, new FilesMutateParams(Id, "once.txt", FileOperations.CreateFile), idem: "k1");
        var second = await _owner.CallAsync(FilesMethods.Mutate, new FilesMutateParams(Id, "once.txt", FileOperations.CreateFile), idem: "k1");
        Assert.Equal(first, second);
    }
}
