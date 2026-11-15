using System.IO.Compression;
using System.Text;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Llm;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 扩展域(Skills / 命令 / Hooks / MCP)的端到端:真实 Agent(进程内)+ 真实 <see cref="DeviceClient"/> + 假上游 + 真实文件 / 真子进程(POSIX shell 脚本)。
/// 覆盖:安装技能 → 模型调 <c>Skill</c> 工具;配置 MCP 服务器 → 工具出现在 <c>tools.list</c> → 被调用并走审批;Hook 拒绝一次工具调用;
/// viewer / member / owner 的角色限制;凭据(MCP 密钥、Hook 环境变量)不出现在任何线上载荷和诊断里。
/// </summary>
public sealed class ExtensionsTests : IAsyncLifetime
{
    private const string McpSecret = "sk-MCP-SECRET-0123456789";
    private const string HookSecret = "HOOK-ENV-SECRET-9876543210";

    private readonly HostDiagnostics _log = new();
    private readonly ConversationRig _rig;
    private DeviceClient _owner = null!;
    private TapStream _tap = null!;
    private int _port;

    public ExtensionsTests()
    {
        _rig = new ConversationRig(llm => new AgentServices
        {
            Models = new FakeModels(llm),
            Http = new NoNetwork(),
            CatalogDelayer = new NeverDelay(),
            McpDelayer = new NeverDelay(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
            Diagnostics = _log,
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        (_owner, _tap) = await _rig.ConnectTappedAsync("owner");
        _port = (await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static byte[] SkillZip(string name, string description, string body)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry($"{name}/SKILL.md");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write($"---\nname: {name}\ndescription: {description}\n---\n{body}\n");
        }

        return memory.ToArray();
    }

    private static string TextOf(IEnumerable<Message> messages) =>
        string.Join('\n', messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Select(r => r.Output.Content));

    private async Task<DeviceClient> ConnectRemoteAsync(string name, string role)
    {
        var identity = PeerIdentity.Create(name);
        var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, _rig.Host.Remote.PairingCode!, name);
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));
        var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
        var client = await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
        Assert.Equal(role, client.Welcome.Role);
        return client;
    }

    private static async Task<string> CodeOf<T>(Func<Task<T>> call)
    {
        var e = await Assert.ThrowsAsync<ProtocolException>(call);
        return e.Code;
    }

    // ───────────────────────── Skills ─────────────────────────

    [Fact]
    public async Task An_installed_skill_is_listed_in_the_prompt_without_its_body_and_the_model_reads_the_body_through_the_Skill_tool()
    {
        var project = await _rig.AddProjectAsync(_owner);
        var install = await _owner.CallAsync(SkillsMethods.InstallZip,
            new SkillsInstallZipParams(SkillZip("demo-skill", "Explains the demo procedure", "BODY-MARKER-7: do the demo steps."), "demo-skill.zip", SkillScopes.Project, project.WorkspaceId),
            idem: Guid.NewGuid().ToString("N"));
        Assert.Equal("demo-skill", install.Installed.Name);
        var listed = await _owner.CallAsync(SkillsMethods.List, new SkillsScopeParams(project.WorkspaceId));
        Assert.Contains(listed.Skills, s => s.Name == "demo-skill" && s.ActiveInWorkspace && s.GlobalEnabled);

        var session = await _rig.CreateSessionAsync(_owner, project);
        _rig.Llm.Then(Up.Calls(("c1", "Skill", """{"name":"demo-skill"}"""))).Then(Up.Say("followed the skill"));
        var start = await _rig.StartAsync(_owner, session.Id, "use the demo skill");
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();

        var first = _rig.Llm.Requests[0];
        Assert.Contains("# Available Skills", first.System);
        Assert.Contains("demo-skill", first.System);
        Assert.Contains("Explains the demo procedure", first.System);
        Assert.DoesNotContain("BODY-MARKER-7", first.System);   // 正文永不进系统提示词
        Assert.Contains("Skill", first.Tools.Select(t => t.ExternalName));
        var second = _rig.Llm.Requests[1];
        Assert.Contains("BODY-MARKER-7", TextOf(second.Messages));
        Assert.Contains("not a grant of permission", TextOf(second.Messages), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_skill_switched_off_globally_or_deselected_for_the_workspace_leaves_the_prompt_and_an_uninstall_removes_it()
    {
        var project = await _rig.AddProjectAsync(_owner);
        foreach (var name in new[] { "alpha-skill", "beta-skill" })
        {
            await _owner.CallAsync(SkillsMethods.InstallZip,
                new SkillsInstallZipParams(SkillZip(name, $"{name} description", $"{name} body"), $"{name}.zip", SkillScopes.Project, project.WorkspaceId), idem: Guid.NewGuid().ToString("N"));
        }

        await _owner.CallAsync(SkillsMethods.SetGlobalEnabled, new SkillsSetGlobalEnabledParams("alpha-skill", false, project.WorkspaceId), idem: Guid.NewGuid().ToString("N"));
        var session = await _rig.CreateSessionAsync(_owner, project);
        _rig.Llm.Then(Up.Say("ok"));
        var start = await _rig.StartAsync(_owner, session.Id, "hi");
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();

        Assert.DoesNotContain("alpha-skill", _rig.Llm.Requests[0].System);
        Assert.Contains("beta-skill", _rig.Llm.Requests[0].System);

        var left = await _owner.CallAsync(SkillsMethods.Uninstall, new SkillsUninstallParams("beta-skill", SkillScopes.Project, project.WorkspaceId), idem: Guid.NewGuid().ToString("N"));
        Assert.DoesNotContain(left.Skills, s => s.Name == "beta-skill");
        Assert.False(Directory.Exists(Path.Combine(project.Path, ".next-cowork", "skills", "beta-skill")));
    }

    [Fact]
    public async Task A_zip_with_a_path_traversal_entry_is_refused_with_a_stable_key_and_installs_nothing()
    {
        var project = await _rig.AddProjectAsync(_owner);
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("evil/SKILL.md").Open())) { w.Write("---\nname: evil\ndescription: d\n---\nbody\n"); }
            using (var w = new StreamWriter(zip.CreateEntry("evil/../../escape.txt").Open())) { w.Write("x"); }
        }

        var e = await Assert.ThrowsAsync<ProtocolException>(() => _owner.CallAsync(SkillsMethods.InstallZip,
            new SkillsInstallZipParams(memory.ToArray(), "evil.zip", SkillScopes.Project, project.WorkspaceId), idem: Guid.NewGuid().ToString("N")));

        Assert.NotNull(e.Details);
        Assert.StartsWith("skills.", FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key);
        Assert.False(File.Exists(Path.Combine(project.Path, ".next-cowork", "escape.txt")));
        Assert.DoesNotContain(
            (await _owner.CallAsync(SkillsMethods.List, new SkillsScopeParams(project.WorkspaceId))).Skills, s => s.Name == "evil");
    }

    [Fact]
    public async Task Slash_commands_are_discovered_from_the_project_directory_and_can_be_switched_off()
    {
        var project = await _rig.AddProjectAsync(_owner);
        var dir = Path.Combine(project.Path, ".next-cowork", "commands");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ship.md"), "---\ndescription: Ship it\n---\nShip $ARGUMENTS now.\n");

        var all = await _owner.CallAsync(CommandsMethods.ListAll, new CommandsScopeParams(project.WorkspaceId));
        Assert.Contains(all.Commands, c => c.Name == "ship" && c.Enabled);
        Assert.Contains(all.Commands, c => c.Name == "init");   // 内置只有 /init

        await _owner.CallAsync(CommandsMethods.SetEnabled, new CommandsSetEnabledParams("ship", false), idem: Guid.NewGuid().ToString("N"));
        Assert.DoesNotContain((await _owner.CallAsync(CommandsMethods.List, new CommandsScopeParams(project.WorkspaceId))).Commands, c => c.Name == "ship");
        Assert.Contains((await _owner.CallAsync(CommandsMethods.ListAll, new CommandsScopeParams(project.WorkspaceId))).Commands, c => c.Name == "ship" && !c.Enabled);
    }

    // ───────────────────────── 工具注册表 ─────────────────────────

    [Fact]
    public async Task Tools_list_names_every_registered_tool_including_the_extension_ones_and_the_skill_tool()
    {
        var names = (await _owner.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools.Select(t => t.Name).ToList();

        foreach (var expected in new[] { "Read", "Write", "Edit", "Bash", "Task", "TodoWrite", "EnterPlanMode", "ExitPlanMode", "ProposeGoal", "Skill" })
        {
            Assert.Contains(expected, names);
        }

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    // ───────────────────────── MCP ─────────────────────────

    private const string ShellServer = """
        while IFS= read -r line; do
          id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9][0-9]*\).*/\1/p')
          case "$line" in
            *'"method":"initialize"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"protocolVersion":"2025-06-18","capabilities":{"tools":{}},"serverInfo":{"name":"sh-server","version":"1"}}}\n' "$id";;
            *'"method":"tools/list"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"tools":[{"name":"echo","description":"echoes the token","inputSchema":{"type":"object"}}]}}\n' "$id";;
            *'"method":"tools/call"'*) printf '{"jsonrpc":"2.0","id":%s,"result":{"content":[{"type":"text","text":"token=%s"}]}}\n' "$id" "$MCP_TEST_TOKEN";;
          esac
        done
        """;

    [Fact]
    public async Task A_configured_mcp_server_shows_up_in_tools_list_and_a_call_goes_through_the_approval_and_never_leaks_the_secret()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The shell-script MCP server needs a POSIX shell.");
        var project = await _rig.AddProjectAsync(_owner);
        var mcp = await Watch<McpTopicState>.OpenAsync(_owner, McpMethods.Topic);
        var config = new McpServerConfigDto("demo", "Demo", McpTransports.Stdio, Command: "sh", Args: ["-c", ShellServer], EnvNames: ["MCP_TEST_TOKEN"]);

        var status = await _owner.CallAsync(McpMethods.Upsert, new McpUpsertParams(config), idem: Guid.NewGuid().ToString("N"));
        Assert.Equal("connected", status.State);
        var info = await _owner.CallAsync(McpMethods.SetSecrets, new McpSetSecretsParams("demo", new Dictionary<string, string> { ["MCP_TEST_TOKEN"] = McpSecret }), idem: Guid.NewGuid().ToString("N"));
        Assert.Equal(["MCP_TEST_TOKEN"], info.Entries.Select(e => e.Name));
        Assert.True(info.Entries[0].IsSet);

        // 工具出现在 tools.list:一律按破坏性处理,不信任服务器
        var tool = (await _owner.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools.Single(t => t.Name == "mcp__demo__echo");
        Assert.True(tool.Destructive);
        Assert.False(tool.ReadOnly);
        Assert.StartsWith("[MCP:", tool.Description);
        await mcp.WaitAsync(() => mcp.Decoded<McpTopicState>(McpEvents.Changed).Any(s => s.Servers.Any(x => x.Config.Id == "demo" && x.State == "connected" && x.Tools.Contains("echo"))), "mcp connected state");

        // 被调用并走审批(ask 档):审批卡片点名这个工具,放行一次之后模型拿到结果(密钥值被抹掉)
        var session = await _rig.CreateSessionAsync(_owner, project);
        _rig.Llm.Then(Up.Calls(("c1", "mcp__demo__echo", "{}"))).Then(Up.Say("done"));
        var interactions = await Watch<InteractionsDto>.OpenAsync(_owner, ConversationMethods.InteractionsTopic);
        var start = await _rig.StartAsync(_owner, session.Id, "call the demo tool");
        var run = await RunWatch.OpenAsync(_owner, start.RunId);
        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending approval");
        var pending = interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending[0];
        var permission = Assert.IsType<ToolPermissionInteractionDto>(pending);
        Assert.Equal("mcp__demo__echo", permission.ToolName);
        await _owner.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(pending.Id, pending.Version, new ToolPermissionResponseDto(PermissionChoices.AllowOnce)));
        await run.SettledAsync();

        Assert.Equal("token=***", TextOf(_rig.Llm.Requests[1].Messages).Trim());

        // 关掉服务器:工具整批消失
        await _owner.CallAsync(McpMethods.SetEnabled, new McpSetEnabledParams("demo", false), idem: Guid.NewGuid().ToString("N"));
        Assert.DoesNotContain((await _owner.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools, t => t.Name == "mcp__demo__echo");

        // 凭据不出现在任何线上载荷(这条连接收到的每个字节,含 mcp 主题的首帧和增量)和诊断里
        Assert.DoesNotContain(McpSecret, Encoding.Latin1.GetString(_tap.Transcript));
        Assert.DoesNotContain(McpSecret, string.Join('\n', _log.Lines));
        // 回读只有键名与「是否已设置」
        var back = await _owner.CallAsync(McpMethods.GetSecretsInfo, new McpIdParams("demo"));
        Assert.True(back.Entries.Single().IsSet);
    }

    // ───────────────────────── Hooks ─────────────────────────

    [Fact]
    public async Task A_PreToolUse_hook_that_exits_2_blocks_a_write_even_in_full_mode_and_its_env_value_never_goes_on_the_wire()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The hook script needs a POSIX shell.");
        var project = await _rig.AddProjectAsync(_owner);
        var saved = await _owner.CallAsync(HooksMethods.Save, new HooksSaveParams("project", project.WorkspaceId,
            new HookUpsertDto(null, "PreToolUse", "command", "Write", true, 10_000, "freeze writes",
                "echo \"frozen by policy $HOOK_TOKEN\" >&2; exit 2", null, null, null, [new HookEnvVarDto("HOOK_TOKEN", HookSecret)])),
            idem: Guid.NewGuid().ToString("N"));
        Assert.Equal(["HOOK_TOKEN"], saved.Hook.EnvNames);   // 只有变量名
        var listed = await _owner.CallAsync(HooksMethods.List, new HooksListParams(project.WorkspaceId));
        Assert.Contains(listed.Hooks, h => h.Id == saved.Hook.Id && h.Event == "PreToolUse" && h.Enabled);

        var target = Path.Combine(project.Path, "blocked.txt");
        var session = await _rig.CreateSessionAsync(_owner, project);
        _rig.Llm.Then(Up.Calls(("c1", "Write", ConversationRig.WriteJson(target, "nope")))).Then(Up.Say("understood"));
        var start = await _rig.StartAsync(_owner, session.Id, "write the file", permission: "full");
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();

        Assert.False(File.Exists(target));   // 钩子的拒绝压过 full 档位
        var result = _rig.Llm.Requests[1].Messages.SelectMany(m => m.Parts).OfType<ToolResultPart>().Single();
        Assert.True(result.IsError);
        Assert.Contains("frozen by policy", result.Output.Content);
        Assert.DoesNotContain(HookSecret, result.Output.Content);   // 钩子输出里的环境变量值被抹掉

        // 停用之后同样的调用放行
        await _owner.CallAsync(HooksMethods.SetEnabled, new HooksSetEnabledParams("project", project.WorkspaceId, saved.Hook.Id, false), idem: Guid.NewGuid().ToString("N"));
        _rig.Llm.Then(Up.Calls(("c2", "Write", ConversationRig.WriteJson(target, "now fine")))).Then(Up.Say("written"));
        var second = await _rig.StartAsync(_owner, session.Id, "again", permission: "full");
        await (await RunWatch.OpenAsync(_owner, second.RunId)).SettledAsync();
        Assert.Equal("now fine", File.ReadAllText(target));

        Assert.DoesNotContain(HookSecret, Encoding.Latin1.GetString(_tap.Transcript));
        Assert.DoesNotContain(HookSecret, string.Join('\n', _log.Lines));
    }

    [Fact]
    public async Task A_deny_rule_beats_a_hook_so_the_hook_script_never_runs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The hook script needs a POSIX shell.");
        var project = await _rig.AddProjectAsync(_owner);
        var ran = Path.Combine(project.Path, "hook-ran.marker");
        Directory.CreateDirectory(Path.Combine(project.Path, ".next-cowork"));
        await _owner.CallAsync(HooksMethods.Save, new HooksSaveParams("project", project.WorkspaceId,
            new HookUpsertDto(null, "PreToolUse", "command", "Write", true, 10_000, null, $"touch '{ran}'", null, null, null, null)), idem: Guid.NewGuid().ToString("N"));
        // deny 规则写进项目设置文件(hooks 段和权限规则同一份文件,两边保留对方的键)
        var settingsPath = Path.Combine(project.Path, ".next-cowork", "settings.local.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        json["permissions"] = new System.Text.Json.Nodes.JsonObject { ["allow"] = new System.Text.Json.Nodes.JsonArray(), ["ask"] = new System.Text.Json.Nodes.JsonArray(), ["deny"] = new System.Text.Json.Nodes.JsonArray("Write") };
        File.WriteAllText(settingsPath, json.ToJsonString());

        var session = await _rig.CreateSessionAsync(_owner, project);
        _rig.Llm.Then(Up.Calls(("c1", "Write", ConversationRig.WriteJson(Path.Combine(project.Path, "x.txt"))))).Then(Up.Say("ok"));
        var start = await _rig.StartAsync(_owner, session.Id, "write", permission: "full");
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();

        Assert.False(File.Exists(ran), "a deny rule must stop the call before any hook runs");
        Assert.Contains("deny rule", TextOf(_rig.Llm.Requests[1].Messages));
    }

    // ───────────────────────── 角色 ─────────────────────────

    [Fact]
    public async Task Roles_viewers_read_skills_and_commands_members_also_see_hooks_and_mcp_and_only_owners_write()
    {
        var project = await _rig.AddProjectAsync(_owner);
        var zip = SkillZip("role-skill", "d", "b");
        await using var viewer = await ConnectRemoteAsync("viewer-1", "viewer");
        await using var member = await ConnectRemoteAsync("member-1", "member");

        // viewer:Skills / 命令只读可以;hooks / mcp 完全不行(命令行和服务器配置属于设备配置)
        Assert.NotNull(await viewer.CallAsync(SkillsMethods.List, new SkillsScopeParams(project.WorkspaceId)));
        Assert.NotNull(await viewer.CallAsync(SkillsMethods.Diagnostics, new SkillsScopeParams(project.WorkspaceId)));
        Assert.NotNull(await viewer.CallAsync(CommandsMethods.List, new CommandsScopeParams(project.WorkspaceId)));
        Assert.NotNull(await viewer.CallAsync(CommandsMethods.ListAll, new CommandsScopeParams(project.WorkspaceId)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(HooksMethods.List, new HooksListParams(project.WorkspaceId))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(McpMethods.List, Unit.Value)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => Watch<McpTopicState>.OpenAsync(viewer, McpMethods.Topic)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(SkillsMethods.SetWorkspaceActive, new SkillsSetWorkspaceActiveParams("x", project.WorkspaceId, true), idem: "i1")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(SkillsMethods.MarketList, new SkillsMarketListParams())));

        // member:hooks / mcp 的读,按工作区选装技能;一切写设备文件系统 / 起进程的都不行
        Assert.NotNull(await member.CallAsync(HooksMethods.List, new HooksListParams(project.WorkspaceId)));
        Assert.NotNull(await member.CallAsync(HooksMethods.Diagnostics, new HooksListParams(project.WorkspaceId)));
        Assert.NotNull(await member.CallAsync(McpMethods.List, Unit.Value));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(zip, "r.zip", SkillScopes.Project, project.WorkspaceId), idem: "i2")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(SkillsMethods.InstallMarket, new SkillsInstallMarketParams("s", null, SkillScopes.Global), idem: "i3")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(SkillsMethods.Uninstall, new SkillsUninstallParams("x"), idem: "i4")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(SkillsMethods.SetGlobalEnabled, new SkillsSetGlobalEnabledParams("x", false), idem: "i5")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(CommandsMethods.SetEnabled, new CommandsSetEnabledParams("init", false), idem: "i6")));
        var hook = new HookUpsertDto(null, "PreToolUse", "command", "Bash", true, 5000, null, "true", null, null, null, null);
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(HooksMethods.Save, new HooksSaveParams("project", project.WorkspaceId, hook), idem: "i7")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(HooksMethods.Test, new HooksTestParams("project", project.WorkspaceId, hook), idem: "i8")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(HooksMethods.Delete, new HooksDeleteParams("project", project.WorkspaceId, "h"), idem: "i9")));
        var server = new McpServerConfigDto("srv", "Srv", McpTransports.Stdio, Command: "sh");
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(McpMethods.Upsert, new McpUpsertParams(server), idem: "i10")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(McpMethods.TestConnection, new McpTestParams("srv", server), idem: "i11")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(McpMethods.SetSecrets, new McpSetSecretsParams("srv", new Dictionary<string, string> { ["K"] = "v" }), idem: "i12")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(McpMethods.GetSecretsInfo, new McpIdParams("srv"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(McpMethods.Remove, new McpIdParams("srv"), idem: "i13")));

        // owner:都行
        await _owner.CallAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(zip, "r.zip", SkillScopes.Project, project.WorkspaceId), idem: "o1");
        Assert.Contains((await member.CallAsync(SkillsMethods.List, new SkillsScopeParams(project.WorkspaceId))).Skills, s => s.Name == "role-skill");
        await member.CallAsync(SkillsMethods.SetWorkspaceActive, new SkillsSetWorkspaceActiveParams("role-skill", project.WorkspaceId, false), idem: "m1");
        Assert.DoesNotContain((await _owner.CallAsync(SkillsMethods.List, new SkillsScopeParams(project.WorkspaceId))).Skills.Where(s => s.Name == "role-skill"), s => s.ActiveInWorkspace);
        Assert.False(Directory.Exists(Path.Combine(project.Path, "escape")));   // 被拒绝的调用什么都没留下
    }

    [Fact]
    public async Task The_extensions_topic_carries_monotonic_revisions_that_other_controllers_use_to_refetch()
    {
        var project = await _rig.AddProjectAsync(_owner);
        await using var other = await _rig.ConnectAsync("ctrl-b");
        var topic = await Watch<ExtensionsTopicState>.OpenAsync(other, ExtensionsMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "extensions snapshot");
        var before = topic.Snapshot;

        await _owner.CallAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(SkillZip("rev-skill", "d", "b"), "r.zip", SkillScopes.Project, project.WorkspaceId), idem: Guid.NewGuid().ToString("N"));
        await topic.WaitAsync(() => topic.Decoded<ExtensionsTopicState>(ExtensionsEvents.Changed).Any(s => s.SkillsRev > before.SkillsRev), "skills revision bump");
        await _owner.CallAsync(CommandsMethods.SetEnabled, new CommandsSetEnabledParams("init", false), idem: Guid.NewGuid().ToString("N"));
        await topic.WaitAsync(() => topic.Decoded<ExtensionsTopicState>(ExtensionsEvents.Changed).Any(s => s.CommandsRev > before.CommandsRev), "commands revision bump");

        var revisions = topic.Decoded<ExtensionsTopicState>(ExtensionsEvents.Changed);
        Assert.Equal(revisions.Select(r => r.SkillsRev).Order(), revisions.Select(r => r.SkillsRev));   // 单调
    }
}
