using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;
using Ncw.Agent.Environments.Tests;
using Ncw.Agent.Extensions.Interactions;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Environments;
using Ncw.Agent.Llm;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 环境服务的内部件:已知主机存储(失败即关闭)、凭据 ref、配置校验与 2.x 兼容的 JSON、路由前置闸门的参数扫描,
/// 以及直接对 <see cref="EnvironmentService"/> 的行为:非交互模式、viewer 不发起连接、Failed 不自动重连、租约、持久化。
/// </summary>
public sealed class EnvironmentServiceTests(SshdFixture fixture) : IClassFixture<SshdFixture>
{
    private sealed class Dir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ncwes-{Guid.NewGuid():N}"[..16]);

        public Dir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    // ───────────────────────── 已知主机 ─────────────────────────

    [Fact]
    public async Task The_host_key_store_pins_per_algorithm_replaces_the_same_algorithm_and_forgets_a_host()
    {
        using var dir = new Dir();
        using var db = AgentDatabase.Open(dir.Path);
        var store = new StoredHostKeyStore(new KvStore(db));
        Assert.Empty(await store.GetAsync("h", default));

        await store.PinAsync("h", new PinnedHostKey("ssh-ed25519", "fp1"), default);
        await store.PinAsync("h", new PinnedHostKey("ssh-rsa", "fp2"), default);
        await store.PinAsync("H", new PinnedHostKey("ssh-ed25519", "fp3"), default); // 主机名不分大小写:替换同一个算法
        Assert.Equal([("ssh-ed25519", "fp3"), ("ssh-rsa", "fp2")], (await store.GetAsync("h", default)).Select(p => (p.Algorithm, p.FingerprintSha256)).Order());

        // 重开库,钉扎还在(持久化在 kv 表里)
        var again = new StoredHostKeyStore(new KvStore(db));
        Assert.Equal(2, (await again.GetAsync("h", default)).Count);
        Assert.Equal(["h"], (await again.ListAsync(default)).Select(e => e.HostName).Distinct(StringComparer.OrdinalIgnoreCase));

        await again.PinAsync("other", new PinnedHostKey("ssh-ed25519", "fpx"), default);
        await again.RemoveAsync("h", default);
        Assert.Empty(await again.GetAsync("h", default));
        Assert.Single(await again.GetAsync("other", default));
    }

    [Fact]
    public async Task A_corrupt_known_hosts_value_fails_closed_and_is_never_overwritten()
    {
        using var dir = new Dir();
        using var db = AgentDatabase.Open(dir.Path);
        var kv = new KvStore(db);
        kv.Set(StoredHostKeyStore.Key, "this is not json");
        var store = new StoredHostKeyStore(kv);

        // 读不懂 = 抛异常(连接失败),不当作「没钉扎过」:否则用户会在已被篡改的库上重新确认指纹
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(async () => await store.GetAsync("h", default));
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(async () => await store.PinAsync("h", new PinnedHostKey("ssh-ed25519", "fp"), default));
        Assert.Equal("this is not json", kv.Get(StoredHostKeyStore.Key));
    }

    // ───────────────────────── 凭据 ─────────────────────────

    [Fact]
    public void Saved_credentials_use_the_2x_ref_for_the_password_and_are_removed_with_the_environment_only()
    {
        using var dir = new Dir();
        using var db = AgentDatabase.Open(dir.Path);
        var store = new CredentialStore(db, dir.Path, NoLegacySecrets.Instance);
        var secrets = new ConnectionSecrets(store);

        Assert.Equal("connection:abc:password", ConnectionSecrets.PasswordRef("abc")); // 2.x 的 ref:接管后原样可用
        secrets.Set(ConnectionSecrets.PasswordRef("abc"), "pw");
        secrets.Set(ConnectionSecrets.HopPasswordRef("abc", "[10.0.0.1]:2222"), "jump-pw");
        secrets.Set(ConnectionSecrets.PassphraseRef("abc", "/home/me/.ssh/id"), "phrase");
        secrets.Set(ConnectionSecrets.ProxyPasswordRef("abc"), "proxy-pw");
        secrets.Set(ConnectionSecrets.PasswordRef("abcd"), "another environment whose id merely starts the same");

        Assert.True(secrets.Has(ConnectionSecrets.PasswordRef("abc")));
        Assert.NotEqual(ConnectionSecrets.PassphraseRef("abc", "/a"), ConnectionSecrets.PassphraseRef("abc", "/b"));
        Assert.DoesNotContain("/home/me", ConnectionSecrets.PassphraseRef("abc", "/home/me/.ssh/id")); // 私钥路径不进 ref

        secrets.RemoveAll("abc");
        Assert.False(secrets.Has(ConnectionSecrets.PasswordRef("abc")));
        Assert.False(secrets.Has(ConnectionSecrets.PassphraseRef("abc", "/home/me/.ssh/id")));
        Assert.False(secrets.Has(ConnectionSecrets.ProxyPasswordRef("abc")));
        Assert.True(secrets.Has(ConnectionSecrets.PasswordRef("abcd"))); // 前缀是「connection:abc:」,不误删 abcd
    }

    // ───────────────────────── 配置 ─────────────────────────

    [Fact]
    public void A_profile_round_trips_through_the_2x_json_shape_keeps_unknown_keys_and_never_holds_a_credential()
    {
        var input = new EnvironmentInput("box", new SshTargetInput("manual", "10.0.0.5", 2222, "dev", "/k/id", ProxyJump: "j@gate"), "key", "linux", true, new SshProxyInput("socks5", "127.0.0.1", 1080, "u"));
        var validated = EnvironmentProfiles.Validate(input);
        var json = EnvironmentProfiles.ToJson("id-1", validated, null, 1000, 1);

        Assert.Equal("ssh", json["kind"]!.GetValue<string>());
        Assert.Equal("manual", json["target"]!["kind"]!.GetValue<string>());
        Assert.Equal(2222, json["target"]!["port"]!.GetValue<int>());
        Assert.Equal("key", json["authMethod"]!.GetValue<string>());
        Assert.Equal("linux", json["platform"]!.GetValue<string>());
        Assert.DoesNotContain("password", json.ToJsonString(), StringComparison.OrdinalIgnoreCase); // 逐字段构造:凭据进不了这一行

        // 更新:未知键保留;路径 null = 保持、空串 = 清除、非空 = 替换
        json["futureKey"] = "kept";
        var kept = EnvironmentProfiles.ToJson("id-1", EnvironmentProfiles.Validate(input with { Target = input.Target with { IdentityFile = null } }), json, 2000, 2);
        Assert.Equal("kept", kept["futureKey"]!.GetValue<string>());
        Assert.Equal("/k/id", kept["target"]!["identityFile"]!.GetValue<string>());
        Assert.Equal(1000, kept["createdAt"]!.GetValue<long>());
        var cleared = EnvironmentProfiles.ToJson("id-1", EnvironmentProfiles.Validate(input with { Target = input.Target with { IdentityFile = "" } }), kept, 3000, 3);
        Assert.Null(cleared["target"]!["identityFile"]);
        var replaced = EnvironmentProfiles.ToJson("id-1", EnvironmentProfiles.Validate(input with { Target = input.Target with { IdentityFile = "/other" } }), cleared, 4000, 4);
        Assert.Equal("/other", replaced["target"]!["identityFile"]!.GetValue<string>());

        // 读回:运行时配置
        var view = ConnectionView.From(new ConnectionRecord("id-1", "box", true, 1, 1000, 1000, json))!;
        var profile = EnvironmentProfiles.ToProfile(view, "proxy-pw");
        var manual = Assert.IsType<SshTarget.Manual>(profile.Target);
        Assert.Equal(("10.0.0.5", 2222, "dev", "/k/id", "j@gate"), (manual.Host, manual.Port, manual.Username, manual.IdentityFile, manual.ProxyJump));
        Assert.Equal((SshAuthMethod.Key, RemotePlatform.Linux, SshProxyKind.Socks5, "proxy-pw"), (profile.AuthMethod, profile.Platform, profile.Proxy!.Kind, profile.Proxy.Password));
    }

    [Fact]
    public void A_2x_profile_row_is_read_as_is_old_rows_without_an_auth_method_mean_auto_and_darwin_win32_map_to_the_platforms()
    {
        var legacy = JsonNode.Parse("""
            { "id": "uuid-1", "kind": "ssh", "name": "old box", "target": { "kind": "config", "host": "prod", "configFile": "/c", "identityFile": "/i" },
              "platform": "darwin", "enabled": true, "revision": 4, "createdAt": 5, "updatedAt": 6 }
            """)!.AsObject();
        var view = ConnectionView.From(new ConnectionRecord("uuid-1", "old box", true, 4, 5, 6, legacy))!;
        Assert.Equal((SshAuthMethod.Auto, RemotePlatform.MacOS, "config", "prod", "/c", "/i"), (view.AuthMethod, view.Platform, view.TargetKind, view.Host, view.ConfigFile, view.IdentityFile));
        var alias = Assert.IsType<SshTarget.ConfigAlias>(EnvironmentProfiles.ToProfile(view, null).Target);
        Assert.Equal(("prod", "/c", "/i"), (alias.Host, alias.ConfigFile, alias.IdentityFile));
        Assert.Equal(RemotePlatform.Windows, ConnectionView.From(new ConnectionRecord("x", "x", true, 1, 0, 0,
            JsonNode.Parse("""{ "id": "x", "kind": "ssh", "name": "x", "target": { "kind": "manual", "host": "h", "port": 22, "username": "u" }, "platform": "win32", "enabled": true }""")!.AsObject()))!.Platform);
        // 读不懂的行不让整个列表失败
        Assert.Null(ConnectionView.From(new ConnectionRecord("bad", "bad", true, 1, 0, 0, JsonNode.Parse("""{ "id": "bad", "kind": "ssh", "target": { "kind": "telnet" } }""")!.AsObject())));
    }

    // ───────────────────────── 路由前置闸门 ─────────────────────────

    private static byte[] Encode<T>(T value, PolyType.ITypeShape<T> shape) => FrameCodec.EncodePayload(value, shape);

    [Fact]
    public void The_gate_finds_the_project_id_in_real_encoded_params_and_ignores_everything_else()
    {
        Assert.Equal("w1", ProjectEnvironmentGate.FindProjectId(Encode(new FilesListParams("w1", "src"), PayloadShapes.GetShape<FilesListParams>())));
        Assert.Equal("w2", ProjectEnvironmentGate.FindProjectId(Encode(new GitWorkspaceParams("w2"), PayloadShapes.GetShape<GitWorkspaceParams>())));
        Assert.Equal("p9", ProjectEnvironmentGate.FindProjectId(Encode(new TerminalsPrepareParams("p9", 80, 24, "t", null), PayloadShapes.GetShape<TerminalsPrepareParams>())));
        // 键不在第一位、前面有数组 / 嵌套 map / 二进制 / 浮点这类要跳过的值
        Assert.Equal("w3", ProjectEnvironmentGate.FindProjectId(Encode(new SkillsInstallZipParams(new byte[300], "a.zip", "project", "w3"), PayloadShapes.GetShape<SkillsInstallZipParams>())));
        Assert.Null(ProjectEnvironmentGate.FindProjectId(Encode(new SkillsScopeParams(null), PayloadShapes.GetShape<SkillsScopeParams>()))); // 全局作用域:没有项目
        Assert.Null(ProjectEnvironmentGate.FindProjectId(Encode(Unit.Value, PayloadShapes.GetShape<Unit>())));
        Assert.Null(ProjectEnvironmentGate.FindProjectId(Encode(new EnvironmentIdParams("x"), PayloadShapes.GetShape<EnvironmentIdParams>())));
        // 畸形 / 截断 / 类型不对:不抛、不当作项目 id(交给方法实现报错)
        var good = Encode(new FilesListParams("w1", "src"), PayloadShapes.GetShape<FilesListParams>());
        for (var cut = 0; cut < good.Length - 1; cut++)
        {
            _ = ProjectEnvironmentGate.FindProjectId(good.AsSpan(0, cut)); // 任何截断都不能抛
        }

        Assert.Null(ProjectEnvironmentGate.FindProjectId([]));
        Assert.Null(ProjectEnvironmentGate.FindProjectId([0xC1, 0xFF, 0x00]));
        Assert.Null(ProjectEnvironmentGate.FindProjectId([0x81, 0xAB, .. "WorkspaceId"u8, 0x2A])); // WorkspaceId 是整数,不是字符串
    }

    [Fact]
    public void Every_gated_method_carries_a_project_id_so_a_new_method_cannot_silently_skip_the_connection_gate()
    {
        var gated = FilesMethods.All.Concat(GitMethods.All).Concat(TerminalMethods.All).Concat(SkillsMethods.All).Concat(CommandsMethods.All).Concat(HooksMethods.All)
            .Concat(OrchestrationMethods.All).Append(PluginsMethods.RunCommand)
            .Where(m => ProjectEnvironmentGate.NeedsEnvironment(m.Name)).ToList();
        Assert.NotEmpty(gated);
        foreach (var method in gated)
        {
            var names = ((PolyType.Abstractions.IObjectTypeShape)method.ParamsTypeShape).Properties.Select(p => p.Name).ToList();
            Assert.True(names.Contains("WorkspaceId") || names.Contains("ProjectId"), $"{method.Name} is gated but its params have no WorkspaceId / ProjectId");
        }

        // 反过来:只有终端的 prepare / create 需要环境(list / write / kill 对已有终端不连接)
        Assert.Equal(["terminals.create", "terminals.prepare"], TerminalMethods.All.Where(m => ProjectEnvironmentGate.NeedsEnvironment(m.Name)).Select(m => m.Name).Order());
        Assert.All(FilesMethods.All.Concat(GitMethods.All), m => Assert.True(ProjectEnvironmentGate.NeedsEnvironment(m.Name)));
        Assert.False(ProjectEnvironmentGate.NeedsEnvironment("sessions.list"));
        Assert.False(ProjectEnvironmentGate.NeedsEnvironment("environments.list"));
    }

    // ───────────────────────── EnvironmentService ─────────────────────────

    private sealed class ServiceRig : IDisposable
    {
        private readonly Dir _dir = new();
        private readonly AgentDatabase _db;

        public ServiceRig(SshdInstance sshd, TimeSpan? idle = null)
        {
            _db = AgentDatabase.Open(_dir.Path);
            Sshd = sshd;
            Idle = idle;
            Gate = new InteractionGate(new UlidIdGenerator());
            Topics = new TopicHub();
            // AgentHost 在构造时注册主题(快照函数现取现用),服务发布时主题必须已经存在
            Topics.Register(EnvironmentsMethods.Topic, () => Service.State());
            Service = Create();
        }

        public SshdInstance Sshd { get; }

        public TimeSpan? Idle { get; }

        public InteractionGate Gate { get; }

        public TopicHub Topics { get; }

        public EnvironmentService Service { get; private set; } = null!;

        public EnvironmentService Create() => new(
            _db, "dev-1", _dir.Path, TimeProvider.System,
            new AgentServices
            {
                SshEnvironments = new EnvironmentHostOptions
                {
                    Ssh = new SshEnvironmentOptions { Reconnect = ReconnectPolicy.Never, ReadyWait = TimeSpan.FromSeconds(10) },
                    ConfigFiles = new PhysicalSshConfigFiles(Sshd.Directory),
                    DiscoverSshAgent = false,
                    IdleTimeout = Idle ?? TimeSpan.FromMinutes(10),
                },
            },
            Topics, Gate, new UlidIdGenerator(), new HostDiagnostics(), new WorkspaceRepository(_db));

        /// <summary>重启设备:同一个库、新的服务实例。</summary>
        public async Task RestartAsync()
        {
            await Service.DisposeAsync();
            Service = Create();
        }

        public async Task<string> CreateEnvironmentAsync(string? identity = null) =>
            (await Service.CreateAsync(new EnvironmentCreateParams(
                new EnvironmentInput("box", new SshTargetInput("manual", "127.0.0.1", Sshd.Port, Sshd.Username, identity ?? Sshd.ClientKey), "key")))).Id;

        public async Task TrustHostAsync(string id)
        {
            var connect = Service.ConnectAsync(id, CancellationToken.None);
            var prompt = await WaitForPromptAsync<SshHostKey>();
            Assert.True(Gate.Respond(prompt.Id, prompt.Version, new SshHostKeyResponse(true), "test").IsAccepted);
            Assert.Equal(EnvironmentStateNames.Ready, (await connect).State);
        }

        public async Task<T> WaitForPromptAsync<T>() where T : InteractionRequest
        {
            var deadline = Environment.TickCount64 + 15_000;
            while (Gate.Snapshot().Pending.OfType<T>().FirstOrDefault() is not { } prompt)
            {
                Assert.True(Environment.TickCount64 < deadline, $"no pending {typeof(T).Name}");
                await Task.Delay(10);
            }

            return Gate.Snapshot().Pending.OfType<T>().First();
        }

        public void Dispose()
        {
            Service.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _db.Dispose();
            _dir.Dispose();
        }
    }

    private static async Task<(string Code, string? Key)> FailureOf(Func<Task> call)
    {
        var e = await Assert.ThrowsAsync<ProtocolException>(call);
        return (e.Code, e.Details is { Length: > 0 } bytes ? FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProviderErrorDto>()).Key : null);
    }

    [Fact]
    public async Task Non_interactive_mode_never_asks_a_person_it_connects_only_when_the_host_is_already_pinned()
    {
        var sshd = fixture.Require();
        using var rig = new ServiceRig(sshd);
        var id = await rig.CreateEnvironmentAsync();

        // 还没钉扎:非交互(定时任务)不弹确认、不挂着等回答,直接失败
        using (EnvironmentService.NonInteractiveScope())
        {
            var failure = await FailureOf(async () => await rig.Service.EnsureReadyAsync(id, ConnectMode.NonInteractive, CancellationToken.None));
            Assert.Equal((ErrorCodes.Unavailable, EnvironmentErrorKeys.HostKeyRejected), failure);
        }

        Assert.Empty(rig.Gate.Snapshot().Pending);
        Assert.Equal(EnvironmentStateNames.Failed, rig.Service.Dto(id).State);

        // 用户主动连接并信任之后断开;再来一次非交互:已钉扎 + 无口令的私钥,直接连上
        await rig.TrustHostAsync(id);
        await rig.Service.DisconnectAsync(id);
        using (EnvironmentService.NonInteractiveScope())
        {
            var environment = await rig.Service.EnsureReadyAsync(id, ConnectMode.NonInteractive, CancellationToken.None);
            Assert.Equal(EnvironmentState.Ready, environment.State);
        }

        // 口令私钥 + 没保存的口令:非交互不问,以「已取消」失败
        var encrypted = await rig.CreateEnvironmentAsync(sshd.EncryptedKey);
        using (EnvironmentService.NonInteractiveScope())
        {
            var failure = await FailureOf(async () => await rig.Service.EnsureReadyAsync(encrypted, ConnectMode.NonInteractive, CancellationToken.None));
            Assert.Equal(EnvironmentErrorKeys.Cancelled, failure.Key);
        }

        Assert.Empty(rig.Gate.Snapshot().Pending);
    }

    [Fact]
    public async Task A_viewer_never_starts_a_connection_and_a_failed_environment_is_not_retried_behind_the_users_back()
    {
        var sshd = fixture.Require();
        using var rig = new ServiceRig(sshd);
        var id = await rig.CreateEnvironmentAsync();

        // ConnectMode.None(viewer):Idle 的环境不发起连接
        Assert.Equal((ErrorCodes.Unavailable, EnvironmentErrorKeys.NotConnected), await FailureOf(async () => await rig.Service.EnsureReadyAsync(id, ConnectMode.None, CancellationToken.None)));
        Assert.Empty(rig.Gate.Snapshot().Pending);
        Assert.Equal(EnvironmentStateNames.Idle, rig.Service.Dto(id).State);

        // 用户拒绝指纹 → Failed。之后惰性使用(Interactive)不会自动再连(否则每次文件刷新都弹一次确认);只有用户主动 connect 才重试
        var connect = rig.Service.ConnectAsync(id, CancellationToken.None);
        var prompt = await rig.WaitForPromptAsync<SshHostKey>();
        rig.Gate.Respond(prompt.Id, prompt.Version, new SshHostKeyResponse(false), "test");
        Assert.Equal(EnvironmentStateNames.Failed, (await connect).State);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal((ErrorCodes.Unavailable, EnvironmentErrorKeys.HostKeyRejected), await FailureOf(async () => await rig.Service.EnsureReadyAsync(id, ConnectMode.Interactive, CancellationToken.None)));
        }

        Assert.Empty(rig.Gate.Snapshot().Pending);
        var retry = rig.Service.ConnectAsync(id, CancellationToken.None);
        Assert.NotNull(await rig.WaitForPromptAsync<SshHostKey>()); // 主动重连:重新弹确认
        rig.Gate.AbortAll();
        Assert.Equal(EnvironmentStateNames.Failed, (await retry).State);
    }

    [Fact]
    public async Task A_lease_keeps_an_idle_connection_alive_and_releasing_it_lets_the_environment_be_recycled()
    {
        var sshd = fixture.Require();
        using var rig = new ServiceRig(sshd, idle: TimeSpan.FromMilliseconds(400));
        var id = await rig.CreateEnvironmentAsync();
        await rig.TrustHostAsync(id);

        var lease = rig.Service.Hold(id);
        await Task.Delay(1500);
        Assert.Equal(EnvironmentStateNames.Ready, rig.Service.Dto(id).State); // 租约在手:不回收
        lease.Dispose();
        lease.Dispose(); // 幂等
        var deadline = Environment.TickCount64 + 10_000;
        while (rig.Service.Dto(id).State != EnvironmentStateNames.Idle)
        {
            Assert.True(Environment.TickCount64 < deadline, "the released environment was never recycled");
            await Task.Delay(50);
        }

        // 配置还在,下次使用惰性重连(已钉扎)
        Assert.Equal(EnvironmentState.Ready, (await rig.Service.EnsureReadyAsync(id, ConnectMode.Interactive, CancellationToken.None)).State);
        // 本机和不认识的 id 没有租约这回事
        rig.Service.Hold("local").Dispose();
        rig.Service.Hold(null).Dispose();
    }

    [Fact]
    public async Task Environments_the_default_and_saved_credentials_survive_a_device_restart()
    {
        var sshd = fixture.Require();
        using var rig = new ServiceRig(sshd);
        var created = await rig.Service.CreateAsync(new EnvironmentCreateParams(
            new EnvironmentInput("persistent", new SshTargetInput("manual", "127.0.0.1", sshd.Port, sshd.Username, sshd.ClientKey), "key"), new EnvironmentSecretsInput(Password: "saved-pw")));
        rig.Service.SetDefault(created.Id);
        await rig.TrustHostAsync(created.Id);

        await rig.RestartAsync();

        var state = rig.Service.State();
        Assert.Equal(created.Id, state.DefaultId);
        var restored = Assert.Single(state.Environments, e => e.Kind == EnvironmentKindNames.Ssh);
        Assert.Equal(("persistent", EnvironmentStateNames.Idle, true, 1L), (restored.Name, restored.State, restored.HasSavedPassword, restored.Revision));
        // 钉扎的指纹也在:重启之后连接不再问
        var connected = await rig.Service.ConnectAsync(created.Id, CancellationToken.None);
        Assert.Equal(EnvironmentStateNames.Ready, connected.State);
        Assert.Empty(rig.Gate.Snapshot().Pending);

        // 删除环境连同它的凭据;默认回到 local
        await rig.Service.RemoveAsync(created.Id);
        Assert.Equal(("local", 1), (rig.Service.State().DefaultId, rig.Service.State().Environments.Length));
        await rig.RestartAsync();
        Assert.Single(rig.Service.State().Environments);
        Assert.Equal("local", rig.Service.State().DefaultId);
    }

    [Fact]
    public async Task The_topic_revision_increases_with_every_publication_and_the_first_frame_is_never_newer_than_the_content()
    {
        var sshd = fixture.Require();
        using var rig = new ServiceRig(sshd);
        var revisions = new List<long>();
        Assert.Equal(0, rig.Service.State().Rev);
        var id = await rig.CreateEnvironmentAsync();
        var afterCreate = rig.Service.State();
        Assert.True(afterCreate.Rev >= 1);
        Assert.Equal(2, afterCreate.Environments.Length);

        for (var i = 0; i < 5; i++)
        {
            rig.Service.Publish();
            revisions.Add(rig.Service.State().Rev);
        }

        Assert.Equal(revisions.Order(), revisions);
        Assert.Equal(revisions.Count, revisions.Distinct().Count());
        Assert.Equal(id, rig.Service.State().Environments[1].Id);
    }
}
