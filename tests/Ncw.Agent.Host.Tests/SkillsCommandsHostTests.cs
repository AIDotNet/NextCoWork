using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Environments;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Extensions;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Skills;
using Ncw.Agent.Skills.Commands;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// Skills / 命令的 Host 适配层与方法处理器(真 SQLite + 真临时目录 + 方法路由,不起 AgentHost):状态落在哪个 kv 键 / 工作区设置键、角色与幂等键由路由检查、业务失败带稳定 key。
/// 真实 Agent + DeviceClient 的端到端在 ConversationService 接线之后(<c>ExtensionsEndToEndTests</c>)。
/// </summary>
public sealed class SkillsCommandsHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncw-skillhost-{Guid.NewGuid():N}"[..28]);
    private readonly AgentDatabase _db;
    private readonly WorkspaceRepository _workspaces;
    private readonly LocalEnvironment _env = new("dev");
    private readonly ExtensionHostContext _context;
    private readonly MethodRouter _router = new();
    private readonly SkillsService _skills;
    private readonly CommandsService _commands;
    private readonly WorkspaceRecord _project;
    private ulong _id;

    public SkillsCommandsHostTests()
    {
        Directory.CreateDirectory(_root);
        var projectDir = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectDir);
        _db = AgentDatabase.Open(Path.Combine(_root, "data"));
        _workspaces = new WorkspaceRepository(_db);
        _project = _workspaces.AddLocal(projectDir);
        _context = new ExtensionHostContext
        {
            Database = _db,
            Kv = new KvStore(_db),
            Credentials = new CredentialStore(_db, _root, NoLegacySecrets.Instance),
            DataRoot = Path.Combine(_root, "data"),
            DeviceId = "dev",
            Clock = TimeProvider.System,
            Log = NullLoopDiagnostics.Instance,
            Services = new AgentServices(),
            LocalEnvironment = _env,
            Topics = new TopicHub(),
            Project = id => _workspaces.Get(id) is { } record
                ? new ProjectTarget(record, _env, EnvPath.Parse(record.RootPath, _env.Facts.PathStyle))
                : throw new ProtocolException(ErrorCodes.NotFound, $"Unknown project '{id}'."),
        };
        _skills = SkillsHost.CreateSkills(_context, market: new NoMarket());
        _commands = SkillsHost.CreateCommands(_context);
        SkillsHandlers.Register(_router, () => _skills);
        CommandsHandlers.Register(_router, () => _commands);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class NoMarket : Ncw.Agent.Skills.Install.ISkillMarketClient
    {
        public Task<IReadOnlyList<Ncw.Agent.Skills.Install.SkillMarketItem>> ListAsync(string? query, string? category, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Ncw.Agent.Skills.Install.SkillMarketItem>>([new("pdf", "pdf", "PDF", "d", "docs", null, null, "1.0.0", null, null, null, null)]);

        public Task<IReadOnlyList<string>> CategoriesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["docs"]);

        public Task<Ncw.Agent.Skills.Install.SkillMarketDetail> DetailAsync(string slug, CancellationToken ct) => throw new SkillException(SkillErrorKeys.NetworkFailed, "offline");

        public Task<Ncw.Agent.Skills.Install.SkillInstallGrant> GrantAsync(string slug, string version, CancellationToken ct) => throw new SkillException(SkillErrorKeys.AuthRequired, "sign in");

        public Task<byte[]> DownloadAsync(string slug, string version, int maxBytes, Action<Ncw.Agent.Skills.Install.SkillDownloadProgress>? progress, CancellationToken ct) => throw new SkillException(SkillErrorKeys.NetworkFailed, "offline");
    }

    private async Task<(ResponseFrame Response, TR? Result)> InvokeAsync<TP, TR>(MethodDef<TP, TR> def, TP parameters, Role role = Role.Owner, string? idem = "auto")
        where TR : class
    {
        var frame = new RequestFrame(++_id, def.Name, FrameCodec.EncodePayload(parameters, def.ParamsShape), def.Kind == MethodKind.Command ? (idem == "auto" ? $"i{_id}" : idem) : null);
        var response = await _router.InvokeAsync(frame, new CallContext("ctrl", role, Transports.Local), CancellationToken.None);
        return (response, response.Ok ? FrameCodec.DecodePayload(response.Result!, def.ResultShape) : null);
    }

    private async Task<TR> OkAsync<TP, TR>(MethodDef<TP, TR> def, TP parameters, Role role = Role.Owner)
        where TR : class
    {
        var (response, result) = await InvokeAsync(def, parameters, role);
        Assert.True(response.Ok, response.Error?.Message);
        return result!;
    }

    private static string ErrorKey(ResponseFrame response) =>
        FrameCodec.DecodePayload(response.Error!.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key;

    private static byte[] Zip(string name, string body = "body", string description = "d")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = archive.CreateEntry($"{name}/SKILL.md").Open();
            writer.Write(Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: {description}\n---\n{body}\n"));
        }

        return stream.ToArray();
    }

    // ───────────────────────── skills ─────────────────────────

    [Fact]
    public async Task Installing_a_zip_returns_the_item_and_the_whole_list_and_the_skill_lands_in_the_data_root_skills_directory()
    {
        var result = await OkAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("demo"), "demo-v1.2.3.zip", SkillScopes.Global));

        Assert.Equal(("demo", "zip", "global", "1.2.3"), (result.Installed.Name, result.Installed.SourceKind, result.Installed.Scope, result.Installed.Version));
        Assert.Single(result.Skills);
        Assert.True(File.Exists(Path.Combine(_root, "data", "skills", "demo", "SKILL.md")));
        Assert.Equal(["demo"], (await OkAsync(SkillsMethods.List, new SkillsScopeParams(_project.Id), Role.Viewer)).Skills.Select(s => s.Name));
    }

    [Fact]
    public async Task A_project_install_writes_into_the_project_skills_directory_and_overrides_a_global_one_of_the_same_name()
    {
        await OkAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("demo", "global body"), null, SkillScopes.Global));

        var result = await OkAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("demo", "project body"), null, SkillScopes.Project, _project.Id));

        Assert.Equal("project", result.Installed.Scope);
        Assert.True(File.Exists(Path.Combine(_project.RootPath, ".next-cowork", "skills", "demo", "SKILL.md")));
        Assert.Equal("project", Assert.Single(result.Skills).Scope);
    }

    [Fact]
    public async Task The_global_switch_is_stored_in_the_kv_table_as_the_disabled_ones_like_2x()
    {
        await OkAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("demo"), null, SkillScopes.Global));

        var list = await OkAsync(SkillsMethods.SetGlobalEnabled, new SkillsSetGlobalEnabledParams("demo", false, _project.Id));

        Assert.False(list.Skills.Single().GlobalEnabled);
        Assert.Equal("""["demo"]""", _context.Kv.Get("skills.disabled"));
        var back = await OkAsync(SkillsMethods.SetGlobalEnabled, new SkillsSetGlobalEnabledParams("demo", true));
        Assert.True(back.Skills.Single().GlobalEnabled);
        Assert.Equal("[]", _context.Kv.Get("skills.disabled"));
    }

    [Fact]
    public async Task Workspace_selection_is_written_into_the_workspace_settings_and_keeps_the_settings_keys_it_does_not_know()
    {
        _workspaces.UpdateSettings(_project.Id, settings => settings["somethingElse"] = "keep me");
        await OkAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("a"), null, SkillScopes.Global));
        await OkAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("b"), null, SkillScopes.Global));

        var list = await OkAsync(SkillsMethods.SetWorkspaceActive, new SkillsSetWorkspaceActiveParams("b", _project.Id, false), Role.Member);

        Assert.Equal([("a", true), ("b", false)], list.Skills.Select(s => (s.Name, s.ActiveInWorkspace)).OrderBy(x => x.Name));
        var settings = (JsonObject)_workspaces.Get(_project.Id)!.Json["settings"]!;
        Assert.Equal(["a"], settings["activeSkillIds"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("explicit", settings["skillSelectionMode"]!.GetValue<string>());
        Assert.Equal("keep me", settings["somethingElse"]!.GetValue<string>());
    }

    [Fact]
    public async Task Uninstall_removes_the_skill_and_returns_the_remaining_list()
    {
        await OkAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("a"), null, SkillScopes.Global));
        await OkAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("b"), null, SkillScopes.Global));

        var list = await OkAsync(SkillsMethods.Uninstall, new SkillsUninstallParams("a"));

        Assert.Equal(["b"], list.Skills.Select(s => s.Name));
        Assert.False(Directory.Exists(Path.Combine(_root, "data", "skills", "a")));
    }

    [Fact]
    public async Task Invariant_roles_are_enforced_by_the_route_viewer_reads_member_selects_only_owner_changes_the_device()
    {
        var zip = new SkillsInstallZipParams(Zip("demo"), null, SkillScopes.Global);

        Assert.Equal(ErrorCodes.Forbidden, (await InvokeAsync(SkillsMethods.InstallZip, zip, Role.Member)).Response.Error!.Code);
        Assert.Equal(ErrorCodes.Forbidden, (await InvokeAsync(SkillsMethods.InstallZip, zip, Role.Viewer)).Response.Error!.Code);
        Assert.Equal(ErrorCodes.Forbidden, (await InvokeAsync(SkillsMethods.SetGlobalEnabled, new SkillsSetGlobalEnabledParams("x", false), Role.Member)).Response.Error!.Code);
        Assert.Equal(ErrorCodes.Forbidden, (await InvokeAsync(SkillsMethods.Uninstall, new SkillsUninstallParams("x"), Role.Member)).Response.Error!.Code);
        Assert.Equal(ErrorCodes.Forbidden, (await InvokeAsync(SkillsMethods.InstallMarket, new SkillsInstallMarketParams("pdf", null, "global"), Role.Member)).Response.Error!.Code);
        Assert.Equal(ErrorCodes.Forbidden, (await InvokeAsync(SkillsMethods.SetWorkspaceActive, new SkillsSetWorkspaceActiveParams("x", _project.Id, true), Role.Viewer)).Response.Error!.Code);
        Assert.Equal(ErrorCodes.Forbidden, (await InvokeAsync(SkillsMethods.MarketList, new SkillsMarketListParams(), Role.Viewer)).Response.Error!.Code);
        Assert.True((await InvokeAsync(SkillsMethods.List, new SkillsScopeParams(), Role.Viewer)).Response.Ok);
        Assert.True((await InvokeAsync(SkillsMethods.Diagnostics, new SkillsScopeParams(), Role.Viewer)).Response.Ok);
        Assert.True((await InvokeAsync(SkillsMethods.MarketList, new SkillsMarketListParams(), Role.Member)).Response.Ok);
        Assert.False(Directory.Exists(Path.Combine(_root, "data", "skills", "demo")));
    }

    [Fact]
    public async Task A_command_method_without_an_idempotency_key_is_refused()
    {
        var (response, _) = await InvokeAsync(SkillsMethods.SetGlobalEnabled, new SkillsSetGlobalEnabledParams("x", false), Role.Owner, idem: null);

        Assert.Equal(ErrorCodes.InvalidRequest, response.Error!.Code);
    }

    [Fact]
    public async Task Business_failures_carry_stable_keys_in_the_details_and_never_the_exception_text_of_something_unknown()
    {
        var bad = await InvokeAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Encoding.UTF8.GetBytes("not a zip"), null, SkillScopes.Global));
        Assert.Equal((ErrorCodes.InvalidRequest, SkillErrorKeys.ZipInvalid), (bad.Response.Error!.Code, ErrorKey(bad.Response)));

        var empty = await InvokeAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams([], null, SkillScopes.Global));
        Assert.Equal(SkillErrorKeys.PackageTooLarge, ErrorKey(empty.Response));

        var scope = await InvokeAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("demo"), null, "nowhere"));
        Assert.Equal(ErrorCodes.InvalidRequest, scope.Response.Error!.Code);

        var noWorkspace = await InvokeAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("demo"), null, SkillScopes.Project));
        Assert.Equal(SkillErrorKeys.WorkspaceRequired, ErrorKey(noWorkspace.Response));

        var unknownProject = await InvokeAsync(SkillsMethods.List, new SkillsScopeParams("ws_nope"), Role.Viewer);
        Assert.Equal(ErrorCodes.NotFound, unknownProject.Response.Error!.Code);

        var ghost = await InvokeAsync(SkillsMethods.SetWorkspaceActive, new SkillsSetWorkspaceActiveParams("ghost", _project.Id, true), Role.Member);
        Assert.Equal((ErrorCodes.NotFound, SkillErrorKeys.NotFound), (ghost.Response.Error!.Code, ErrorKey(ghost.Response)));

        var market = await InvokeAsync(SkillsMethods.InstallMarket, new SkillsInstallMarketParams("pdf", null, SkillScopes.Global));
        Assert.Equal((true, ErrorCodes.Unavailable), (market.Response.Error!.Retryable, market.Response.Error.Code));
        Assert.Equal(SkillErrorKeys.NetworkFailed, ErrorKey(market.Response));
    }

    [Fact]
    public async Task Market_browsing_goes_through_the_injected_client()
    {
        Assert.Equal(["pdf"], (await OkAsync(SkillsMethods.MarketList, new SkillsMarketListParams("p"), Role.Member)).Items.Select(i => i.Slug));
        Assert.Equal(["docs"], (await OkAsync(SkillsMethods.MarketCategories, Unit.Value, Role.Member)).Categories);
    }

    [Fact]
    public async Task Diagnostics_come_back_with_stable_keys()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "skills", "empty"));

        var result = await OkAsync(SkillsMethods.Diagnostics, new SkillsScopeParams(), Role.Viewer);

        Assert.Contains(result.Diagnostics, d => d.Key == SkillDiagnosticKeys.SkillMdMissing);
    }

    [Fact]
    public async Task Usage_is_recorded_per_workspace_in_the_2x_stats_shape_and_shows_in_the_list()
    {
        await OkAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(Zip("demo"), null, SkillScopes.Global));
        var usage = new HostSkillUsage(_context.Kv, TimeProvider.System);

        usage.Record("demo", _project.Id);
        usage.Record("demo", _project.Id);
        usage.Record("demo", null);

        Assert.Equal(3, usage.Usage(null)["demo"].Count);
        Assert.Equal(2, usage.Usage(_project.Id)["demo"].Count);
        Assert.True(usage.Usage(_project.Id)["demo"].LastUsedAt > 0);
        Assert.Equal(0, usage.Usage("other").GetValueOrDefault("demo")?.Count ?? 0);
        Assert.Equal(3, JsonNode.Parse(_context.Kv.Get("skills.stats")!)!["demo"]!["count"]!.GetValue<int>());
    }

    // ───────────────────────── commands ─────────────────────────

    [Fact]
    public async Task The_command_list_has_the_builtin_init_and_the_project_and_global_files_and_switches_persist_as_the_disabled_names()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data", "commands"));
        File.WriteAllText(Path.Combine(_root, "data", "commands", "review.md"), "---\ndescription: Review\nargument-hint: <file>\n---\nReview $1");
        Directory.CreateDirectory(Path.Combine(_project.RootPath, ".next-cowork", "commands"));
        File.WriteAllText(Path.Combine(_project.RootPath, ".next-cowork", "commands", "ship.md"), "Ship it");

        var list = await OkAsync(CommandsMethods.List, new CommandsScopeParams(_project.Id), Role.Viewer);

        Assert.Equal(["init", "review", "ship"], list.Commands.Select(c => c.Name).Order());
        var review = list.Commands.Single(c => c.Name == "review");
        Assert.Equal(("Review $1", "global", "<file>"), (review.Prompt, review.Scope, review.ArgumentHint));
        Assert.Equal("project", list.Commands.Single(c => c.Name == "ship").Scope);

        var all = await OkAsync(CommandsMethods.SetEnabled, new CommandsSetEnabledParams("review", false, _project.Id));
        Assert.False(all.Commands.Single(c => c.Name == "review").Enabled);
        Assert.Equal("""["review"]""", _context.Kv.Get("commands.disabled"));
        Assert.DoesNotContain((await OkAsync(CommandsMethods.List, new CommandsScopeParams(_project.Id), Role.Viewer)).Commands, c => c.Name == "review");
        Assert.Contains((await OkAsync(CommandsMethods.ListAll, new CommandsScopeParams(_project.Id), Role.Viewer)).Commands, c => c.Name == "review" && !c.Enabled);
    }

    [Fact]
    public async Task Commands_enabling_is_owner_only_the_name_is_validated_and_diagnostics_have_keys()
    {
        Assert.Equal(ErrorCodes.Forbidden, (await InvokeAsync(CommandsMethods.SetEnabled, new CommandsSetEnabledParams("init", false), Role.Member)).Response.Error!.Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await InvokeAsync(CommandsMethods.SetEnabled, new CommandsSetEnabledParams("../evil", false))).Response.Error!.Code);
        Assert.Null(_context.Kv.Get("commands.disabled"));

        Directory.CreateDirectory(Path.Combine(_root, "data", "commands"));
        File.WriteAllText(Path.Combine(_root, "data", "commands", "has space.md"), "x");
        var diagnostics = await OkAsync(CommandsMethods.Diagnostics, new CommandsScopeParams(), Role.Viewer);
        Assert.Contains(diagnostics.Diagnostics, d => d.Key == "commands.fileNameInvalid");
    }
}
