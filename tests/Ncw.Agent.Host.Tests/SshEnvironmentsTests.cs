using System.Diagnostics;
using System.Text;
using Ncw.Agent.Environments.Ssh;
using Ncw.Agent.Environments.Tests;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Environments;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// SSH 多设备执行(06、09 §8.4 的 <c>environments.*</c>)的端到端测试:真实 Agent(进程内)+ <see cref="DeviceClient"/> + 用户态 sshd。没有 sshd 时跳过。
/// 配置(创建 / 更新 / 删除 / 默认)→ 首次连接触发主机指纹交互 → 凭据交互 → 项目绑定到 SSH 环境 → 文件 / git / 终端 / Read·Edit·Bash 工具在它上面跑 →
/// 断线重连 → 指纹变化被拒 → 跳板机 → 角色限制 → 空闲回收 → 凭据不出现在任何线上载荷。
/// </summary>
public sealed class SshEnvironmentsTests(SshdFixture fixture) : IClassFixture<SshdFixture>
{
    private const string Passphrase = SshdInstance.Passphrase;

    private static EnvironmentHostOptions HostOptions(SshdInstance sshd, TimeSpan? idle = null) => new()
    {
        Ssh = new SshEnvironmentOptions
        {
            Reconnect = new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(400), Jitter = 0 },
            KillGrace = TimeSpan.FromMilliseconds(500),
            ReadyWait = TimeSpan.FromSeconds(15),
        },
        // 指向临时 ~/.ssh:绝不读用户真实的 ssh 配置;也不去找开发机上真实的 ssh-agent
        ConfigFiles = new PhysicalSshConfigFiles(sshd.Directory),
        DiscoverSshAgent = false,
        IdleTimeout = idle ?? TimeSpan.FromMinutes(10),
    };

    /// <summary>真实 Agent + 用户态 sshd + 一个线上字节被记下来的 owner 控制端。</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _remoteRoot = Path.Combine(Path.GetTempPath(), $"ncw-ssh-proj-{Guid.NewGuid():N}");
        private int _port;
        private bool _disposed;

        private Rig(SshdInstance sshd, TimeSpan? idle)
        {
            Sshd = sshd;
            Conversation = new ConversationRig(llm => new AgentServices
            {
                Diagnostics = Log,
                Models = new FakeModels(llm),
                Http = new NoNetwork(),
                CatalogDelayer = new NeverDelay(),
                Delayer = new NoDelay(),
                Loop = new AgentLoopOptions { ResumeDelays = [] },
                SshEnvironments = HostOptions(sshd, idle),
            });
        }

        public SshdInstance Sshd { get; }

        /// <summary>设备的诊断日志(<c>diagnostics.logs</c> 的来源):凭据不许出现在里面。</summary>
        public HostDiagnostics Log { get; } = new();

        public ConversationRig Conversation { get; }

        public DeviceClient Owner { get; private set; } = null!;

        public TapStream Tap { get; private set; } = null!;

        /// <summary>远程主机上的一个空目录(sshd 和 Agent 在同一台机器上,所以它也是本地路径)。</summary>
        public string RemoteDir => _remoteRoot;

        public static async Task<Rig> StartAsync(SshdInstance sshd, TimeSpan? idle = null)
        {
            var rig = new Rig(sshd, idle);
            await rig.Conversation.InitializeAsync();
            Directory.CreateDirectory(rig._remoteRoot);
            (rig.Owner, rig.Tap) = await rig.Conversation.ConnectTappedAsync("owner");
            await rig.WaitForDataAsync();
            return rig;
        }

        private async Task WaitForDataAsync()
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                try
                {
                    await Owner.CallAsync(EnvironmentsMethods.List, Unit.Value);
                    return;
                }
                catch (ProtocolException e) when (e.Retryable)
                {
                    await Task.Delay(50);
                }
            }

            throw new TimeoutException("the device data never became ready");
        }

        public async Task<(DeviceClient Client, Func<Task<DeviceClient>> Reconnect)> ConnectRemoteAsync(string name, string role)
        {
            if (_port == 0)
            {
                _port = (await Owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
            }

            var identity = PeerIdentity.Create(name);
            var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, Conversation.Host.Remote.PairingCode!, name);
            await Owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));

            async Task<DeviceClient> Connect()
            {
                var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
                var client = await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
                Assert.Equal(role, client.Welcome.Role);
                return client;
            }

            return (await Connect(), Connect);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await Conversation.DisposeAsync();
            try { Directory.Delete(_remoteRoot, recursive: true); } catch (IOException) { }
        }
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static EnvironmentInput Input(SshdInstance sshd, string? identity = null, string auth = "key", string name = "build-box", string? proxyJump = null) =>
        new(name, new SshTargetInput("manual", "127.0.0.1", sshd.Port, sshd.Username, identity ?? sshd.ClientKey, ProxyJump: proxyJump), auth);

    private static Task<EnvironmentDto> CreateAsync(DeviceClient client, EnvironmentInput input, EnvironmentSecretsInput? secrets = null) =>
        client.CallAsync(EnvironmentsMethods.Create, new EnvironmentCreateParams(input, secrets));

    private static async Task<T> NextAsync<T>(DeviceClient client, Func<T, bool>? where = null, int timeoutMs = 20_000) where T : InteractionDto
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var pending = (await client.CallAsync(ConversationMethods.InteractionsList, Unit.Value)).Pending;
            if (pending.OfType<T>().FirstOrDefault(where ?? (_ => true)) is { } hit)
            {
                return hit;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for a pending {typeof(T).Name}; pending: {string.Join(", ", pending.Select(p => p.GetType().Name))}");
            }

            await Task.Delay(20);
        }
    }

    private static Task<InteractionsRespondResult> Respond(DeviceClient client, InteractionDto interaction, InteractionResponseDto response) =>
        client.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(interaction.Id, interaction.Version, response));

    private static async Task<int> PendingCountAsync(DeviceClient client) =>
        (await client.CallAsync(ConversationMethods.InteractionsList, Unit.Value)).Pending.Length;

    /// <summary>连接并信任指纹(用无口令的私钥,只有指纹确认这一道交互)。</summary>
    private static async Task<EnvironmentDto> ConnectTrustedAsync(DeviceClient client, string id)
    {
        var connect = client.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(id));
        var hostKey = await NextAsync<SshHostKeyInteractionDto>(client, h => h.Env.Environment == id);
        await Respond(client, hostKey, new SshHostKeyResponseDto(true));
        return await connect;
    }

    private static async Task<EnvironmentDto> WaitForEnvironmentAsync(DeviceClient client, string id, Func<EnvironmentDto, bool> where, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var dto = await client.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(id));
            if (where(dto))
            {
                return dto;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}; the environment is {dto.State}");
            }

            await Task.Delay(30);
        }
    }

    private static async Task<ProjectDto> AddRemoteProjectAsync(DeviceClient client, string environmentId, string path, string? name = null) =>
        await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(path, name, environmentId));

    private static async Task<(string Code, string? Key)> FailureOf<T>(Func<Task<T>> call)
    {
        var e = await Assert.ThrowsAsync<ProtocolException>(call);
        return (e.Code, e.Details is { Length: > 0 } bytes ? FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProviderErrorDto>()).Key : null);
    }

    /// <summary>设备发给控制端的每个字节里都不许出现这些秘密(整段线上字节扫描,不靠某个 DTO 的字段)。</summary>
    private static void AssertNoSecretsOnTheWire(TapStream tap, params string[] secrets)
    {
        var wire = tap.Transcript;
        foreach (var secret in secrets)
        {
            Assert.False(IndexOf(wire, Encoding.UTF8.GetBytes(secret)) >= 0, $"the secret '{secret}' appeared in the bytes the device sent to a controller");
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    // ───────────────────────── 配置 ─────────────────────────

    [Fact]
    public async Task Owners_create_update_default_and_remove_environments_and_the_state_is_a_monotonic_topic()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var watch = await Watch<EnvironmentsTopicState>.OpenAsync(rig.Owner, EnvironmentsMethods.Topic);
        await watch.WaitAsync(() => watch.HasSnapshot, "snapshot");
        var initial = watch.Snapshot;
        Assert.Equal("local", initial.DefaultId);
        var local = Assert.Single(initial.Environments);
        Assert.Equal((EnvironmentKindNames.Local, EnvironmentStateNames.Ready, "local"), (local.Kind, local.State, local.Id));
        Assert.NotNull(local.Facts);

        var created = await CreateAsync(rig.Owner, Input(sshd), new EnvironmentSecretsInput(Password: "pw-123"));
        Assert.Equal((EnvironmentKindNames.Ssh, EnvironmentStateNames.Idle, 0, false), (created.Kind, created.State, created.Generation, created.IsDefault));
        Assert.Equal(("manual", "127.0.0.1", sshd.Port, true), (created.Target!.Kind, created.Target.Host, created.Target.Port, created.Target.HasIdentityFile));
        Assert.True(created.HasSavedPassword);
        Assert.Equal(1, created.Revision);

        var list = await rig.Owner.CallAsync(EnvironmentsMethods.List, Unit.Value);
        Assert.Equal(["local", created.Id], list.Environments.Select(e => e.Id));

        // 主题:新环境以事件到达,Rev 严格递增(首帧与事件同一个类型)
        await watch.WaitAsync(() => watch.Decoded<EnvironmentsTopicState>(EnvironmentsEvents.Changed).Any(s => s.Environments.Length == 2), "created event");
        var revs = watch.Decoded<EnvironmentsTopicState>(EnvironmentsEvents.Changed).Select(s => s.Rev).Prepend(watch.Snapshot.Rev).ToList();
        Assert.Equal(revs.Order(), revs);
        Assert.Equal(revs.Distinct().Count(), revs.Count);

        // 更新:乐观锁;路径 null = 保持;改名不需要确认
        var renamed = await rig.Owner.CallAsync(EnvironmentsMethods.Update, new EnvironmentUpdateParams(created.Id, 1, Input(sshd, name: "renamed") with { Target = Input(sshd).Target with { IdentityFile = null } }));
        Assert.Equal(("renamed", 2L, true, true), (renamed.Name, renamed.Revision, renamed.Target!.HasIdentityFile, renamed.HasSavedPassword));
        Assert.Equal(ErrorCodes.Conflict, (await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Update, new EnvironmentUpdateParams(created.Id, 1, Input(sshd))))).Code);
        // 清除凭据 / 路径用空串
        var cleared = await rig.Owner.CallAsync(EnvironmentsMethods.Update, new EnvironmentUpdateParams(created.Id, 2, Input(sshd) with { Target = Input(sshd).Target with { IdentityFile = "" } }, new EnvironmentSecretsInput(Password: "")));
        Assert.False(cleared.HasSavedPassword);
        Assert.False(cleared.Target!.HasIdentityFile);

        // 默认环境
        await rig.Owner.CallAsync(EnvironmentsMethods.SetDefault, new EnvironmentIdParams(created.Id));
        Assert.Equal(created.Id, (await rig.Owner.CallAsync(EnvironmentsMethods.List, Unit.Value)).DefaultId);
        Assert.Equal(ErrorCodes.NotFound, (await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.SetDefault, new EnvironmentIdParams("nope")))).Code);

        // 删除:local 不能删,不存在 not_found;删掉默认环境后默认回到 local
        Assert.Equal(EnvironmentErrorKeys.LocalImmutable, (await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Remove, new EnvironmentIdParams("local")))).Key);
        await rig.Owner.CallAsync(EnvironmentsMethods.Remove, new EnvironmentIdParams(created.Id));
        var after = await rig.Owner.CallAsync(EnvironmentsMethods.List, Unit.Value);
        Assert.Equal(("local", 1), (after.DefaultId, after.Environments.Length));
        Assert.Equal(ErrorCodes.NotFound, (await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(created.Id)))).Code);

        AssertNoSecretsOnTheWire(rig.Tap, "pw-123");
    }

    [Fact]
    public async Task Invalid_profiles_are_rejected_with_a_structured_key()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        EnvironmentInput[] bad =
        [
            Input(sshd, name: " "),
            Input(sshd) with { Target = new SshTargetInput("manual", "-oProxyCommand=evil", 22, "dev") },
            Input(sshd) with { Target = new SshTargetInput("manual", "host with space", 22, "dev") },
            Input(sshd) with { Target = new SshTargetInput("manual", "host", 70000, "dev") },
            Input(sshd) with { Target = new SshTargetInput("manual", "host", 22, "") },
            Input(sshd) with { Target = new SshTargetInput("bogus", "host") },
            Input(sshd) with { Target = new SshTargetInput("config", "prod", 22) }, // 别名的端口来自 ssh_config
            Input(sshd) with { AuthMethod = "telepathy" },
            Input(sshd) with { Platform = "plan9" },
            Input(sshd) with { Proxy = new SshProxyInput("ftp", "h", 1) },
            Input(sshd) with { Target = new SshTargetInput("manual", "host", 22, "dev", ProxyJump: "user@") },
        ];
        foreach (var input in bad)
        {
            var (code, key) = await FailureOf(() => CreateAsync(rig.Owner, input));
            Assert.Equal((ErrorCodes.InvalidRequest, EnvironmentErrorKeys.InvalidProfile), (code, key));
        }

        var secret = await FailureOf(() => CreateAsync(rig.Owner, Input(sshd), new EnvironmentSecretsInput(Password: "a\0b")));
        Assert.Equal(ErrorCodes.InvalidRequest, secret.Code);
        Assert.Single((await rig.Owner.CallAsync(EnvironmentsMethods.List, Unit.Value)).Environments); // 一个都没存下
    }

    // ───────────────────────── 主机指纹 ─────────────────────────

    [Fact]
    public async Task A_first_connection_asks_to_confirm_the_host_key_and_pins_it()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var env = await CreateAsync(rig.Owner, Input(sshd));
        var watch = await Watch<EnvironmentsTopicState>.OpenAsync(rig.Owner, EnvironmentsMethods.Topic);

        var connect = rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        var hostKey = await NextAsync<SshHostKeyInteractionDto>(rig.Owner);
        // 带执行环境标识(设备 + 环境)、主机名按 known_hosts 写法、指纹是 ssh-keygen -l 同款
        Assert.Equal((rig.Conversation.Host.DeviceId, env.Id, "build-box", "ssh"), (hostKey.Env.DeviceId, hostKey.Env.Environment, hostKey.Env.Name, hostKey.Env.Type));
        Assert.Equal(($"[127.0.0.1]:{sshd.Port}", "ssh-ed25519"), (hostKey.HostName, hostKey.Algorithm));
        Assert.DoesNotContain("=", hostKey.Fingerprint);
        Assert.StartsWith("env:", hostKey.RunId); // 设备级作用域,不属于任何会话
        Assert.Equal(EnvironmentStateNames.Connecting, (await rig.Owner.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(env.Id))).State);
        Assert.False(connect.IsCompleted);

        await Respond(rig.Owner, hostKey, new SshHostKeyResponseDto(true));
        var ready = await connect;
        Assert.Equal((EnvironmentStateNames.Ready, 1), (ready.State, ready.Generation));
        Assert.Equal(sshd.Username, ready.Facts!.Username);
        Assert.NotNull(ready.Facts.Hostname);
        Assert.Equal(0, await PendingCountAsync(rig.Owner));
        // 延迟在连接成功之后测一次,随状态发布
        var withLatency = await WaitForEnvironmentAsync(rig.Owner, env.Id, e => e.LatencyMs is not null, "a latency measurement");
        Assert.InRange(withLatency.LatencyMs!.Value, 0, 5000);

        // 已知主机里有它,指纹和交互里给的一致
        var known = Assert.Single((await rig.Owner.CallAsync(EnvironmentsMethods.KnownHosts, Unit.Value)).Hosts);
        Assert.Equal((hostKey.HostName, hostKey.Algorithm, hostKey.Fingerprint), (known.HostName, known.Algorithm, known.Fingerprint));

        // 状态经主题推送:connecting → ready
        await watch.WaitAsync(() => watch.Decoded<EnvironmentsTopicState>(EnvironmentsEvents.Changed).Any(s => s.Environments.Any(e => e.State == EnvironmentStateNames.Ready)), "ready event");
        var states = watch.Decoded<EnvironmentsTopicState>(EnvironmentsEvents.Changed).SelectMany(s => s.Environments).Where(e => e.Id == env.Id).Select(e => e.State).Distinct().ToList();
        Assert.Contains(EnvironmentStateNames.Connecting, states);
        Assert.Contains(EnvironmentStateNames.Ready, states);

        // 断开再连:已钉扎,不再询问
        var idle = await rig.Owner.CallAsync(EnvironmentsMethods.Disconnect, new EnvironmentIdParams(env.Id));
        Assert.Equal(EnvironmentStateNames.Idle, idle.State);
        var again = await rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        Assert.Equal(EnvironmentStateNames.Ready, again.State);
        Assert.Equal(0, await PendingCountAsync(rig.Owner));
    }

    [Fact]
    public async Task A_rejected_host_key_fails_the_connect_as_a_result_before_any_credential_is_sent()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var env = await CreateAsync(rig.Owner, Input(sshd));
        var accepted = sshd.Log.Split("Accepted publickey").Length;

        var connect = rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        await Respond(rig.Owner, await NextAsync<SshHostKeyInteractionDto>(rig.Owner), new SshHostKeyResponseDto(false));
        var failed = await connect;

        Assert.Equal(EnvironmentStateNames.Failed, failed.State);
        Assert.Equal(EnvironmentErrorKeys.HostKeyRejected, failed.Error!.Key);
        Assert.Null(failed.Error.HostKey);
        Assert.Empty((await rig.Owner.CallAsync(EnvironmentsMethods.KnownHosts, Unit.Value)).Hosts);
        Assert.Equal(accepted, sshd.Log.Split("Accepted publickey").Length); // 握手在指纹这一步就中止了
    }

    [Fact]
    public async Task A_changed_host_key_is_refused_with_the_mismatch_and_never_prompts_or_overwrites_the_pin()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var env = await CreateAsync(rig.Owner, Input(sshd));
        await ConnectTrustedAsync(rig.Owner, env.Id);
        var pinned = Assert.Single((await rig.Owner.CallAsync(EnvironmentsMethods.KnownHosts, Unit.Value)).Hosts);
        await rig.Owner.CallAsync(EnvironmentsMethods.Disconnect, new EnvironmentIdParams(env.Id));

        await sshd.RotateHostKeyAsync(); // 服务器换了主机密钥(重装,或被中间人顶替)
        var failed = await rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        Assert.Equal(EnvironmentStateNames.Failed, failed.State);
        Assert.Equal(EnvironmentErrorKeys.HostKeyChanged, failed.Error!.Key);
        // 展示不一致:出示的指纹 vs 钉扎的指纹
        var mismatch = failed.Error.HostKey!;
        Assert.Equal((pinned.HostName, "ssh-ed25519"), (mismatch.HostName, mismatch.Algorithm));
        Assert.NotEqual(pinned.Fingerprint, mismatch.Presented);
        Assert.Equal(pinned.Fingerprint, Assert.Single(mismatch.Pinned).Fingerprint);
        // 不弹确认、不覆盖旧指纹
        Assert.Equal(0, await PendingCountAsync(rig.Owner));
        Assert.Equal(pinned.Fingerprint, Assert.Single((await rig.Owner.CallAsync(EnvironmentsMethods.KnownHosts, Unit.Value)).Hosts).Fingerprint);

        // 再连一次(Failed 之后是用户主动重试)还是同样被拒;状态里也带着不一致
        var status = await rig.Owner.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(env.Id));
        Assert.Equal(EnvironmentErrorKeys.HostKeyChanged, status.Error!.Key);
        Assert.Equal(0, await PendingCountAsync(rig.Owner));

        // 「处理入口」:用户明确选择信任新指纹 = 先忘记旧钉扎,再连会重新确认
        await rig.Owner.CallAsync(EnvironmentsMethods.ForgetHostKey, new ForgetHostKeyParams(pinned.HostName));
        var reconnect = rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        var prompt = await NextAsync<SshHostKeyInteractionDto>(rig.Owner);
        Assert.Equal(mismatch.Presented, prompt.Fingerprint);
        await Respond(rig.Owner, prompt, new SshHostKeyResponseDto(true));
        Assert.Equal(EnvironmentStateNames.Ready, (await reconnect).State);
    }

    // ───────────────────────── 凭据 ─────────────────────────

    [Fact]
    public async Task A_key_passphrase_prompt_is_a_rejected_then_remembered_interaction_and_never_goes_back_on_the_wire()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var env = await CreateAsync(rig.Owner, Input(sshd, identity: sshd.EncryptedKey));

        var connect = rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        await Respond(rig.Owner, await NextAsync<SshHostKeyInteractionDto>(rig.Owner), new SshHostKeyResponseDto(true));

        // 第一问:口令。给一个错的
        var first = await NextAsync<SshCredentialInteractionDto>(rig.Owner);
        Assert.Equal(("keyPassphrase", "client_encrypted", sshd.Username, false, true), (first.PromptKind, first.KeyName, first.UserName, first.PreviousRejected, first.CanRemember));
        Assert.Equal((rig.Conversation.Host.DeviceId, env.Id, "ssh"), (first.Env.DeviceId, first.Env.Environment, first.Env.Type));
        await Respond(rig.Owner, first, new SshCredentialResponseDto(["wrong passphrase"]));

        // 被拒绝了才再问,界面要说「上一次不对」;这次给对的,并记住
        var second = await NextAsync<SshCredentialInteractionDto>(rig.Owner, c => c.Id != first.Id);
        Assert.True(second.PreviousRejected);
        await Respond(rig.Owner, second, new SshCredentialResponseDto([Passphrase], Remember: true));
        var ready = await connect;
        Assert.Equal(EnvironmentStateNames.Ready, ready.State);

        // 记住之后:断开再连,不再问口令(用 NCK1 存着的)
        await rig.Owner.CallAsync(EnvironmentsMethods.Disconnect, new EnvironmentIdParams(env.Id));
        var again = await rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        Assert.Equal(EnvironmentStateNames.Ready, again.State);
        Assert.Equal(0, await PendingCountAsync(rig.Owner));

        // 凭据只写不读:线上(设备 → 控制端)永远没有口令、也没有私钥路径;已保存的口令存在 NCK1 里(库里没有明文)
        AssertNoSecretsOnTheWire(rig.Tap, Passphrase, "wrong passphrase", sshd.Directory, sshd.EncryptedKey);
        var logs = string.Join('\n', rig.Log.Lines);
        Assert.DoesNotContain(Passphrase, logs, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong passphrase", logs, StringComparison.Ordinal);
        var db = Path.Combine(rig.Conversation.DataRoot, "nextcowork.db");
        Assert.DoesNotContain(Passphrase, await ReadAllLossy(db), StringComparison.Ordinal);
    }

    private static async Task<string> ReadAllLossy(string dbPath)
    {
        // 数据库文件在用:以共享方式读出字节
        await using var stream = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return Encoding.Latin1.GetString(buffer.ToArray());
    }

    [Fact]
    public async Task An_unremembered_passphrase_is_asked_again_after_the_connection_is_dropped()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var env = await CreateAsync(rig.Owner, Input(sshd, identity: sshd.EncryptedKey));
        var connect = rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        await Respond(rig.Owner, await NextAsync<SshHostKeyInteractionDto>(rig.Owner), new SshHostKeyResponseDto(true));
        await Respond(rig.Owner, await NextAsync<SshCredentialInteractionDto>(rig.Owner), new SshCredentialResponseDto([Passphrase], Remember: false));
        Assert.Equal(EnvironmentStateNames.Ready, (await connect).State);

        await rig.Owner.CallAsync(EnvironmentsMethods.Disconnect, new EnvironmentIdParams(env.Id)); // 回收实例,内存里的凭据缓存随之清掉
        var again = rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        var prompt = await NextAsync<SshCredentialInteractionDto>(rig.Owner);
        Assert.False(prompt.PreviousRejected);
        await Respond(rig.Owner, prompt, new SshCredentialResponseDto([Passphrase]));
        Assert.Equal(EnvironmentStateNames.Ready, (await again).State);
    }

    [Fact]
    public async Task Cancelling_a_credential_prompt_ends_the_attempt_as_cancelled_and_remembering_a_draft_is_refused()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var env = await CreateAsync(rig.Owner, Input(sshd, identity: sshd.EncryptedKey));
        var connect = rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        await Respond(rig.Owner, await NextAsync<SshHostKeyInteractionDto>(rig.Owner), new SshHostKeyResponseDto(true));
        var prompt = await NextAsync<SshCredentialInteractionDto>(rig.Owner);

        // 空口令不被接受,也不消耗这次交互(invalid_request)
        Assert.Equal(ErrorCodes.InvalidRequest, (await FailureOf(() => Respond(rig.Owner, prompt, new SshCredentialResponseDto([""])))).Code);
        Assert.Equal(1, await PendingCountAsync(rig.Owner));
        await Respond(rig.Owner, prompt, new SshCredentialResponseDto(null));

        var failed = await connect;
        Assert.Equal((EnvironmentStateNames.Failed, EnvironmentErrorKeys.Cancelled), (failed.State, failed.Error!.Key));
    }

    [Fact]
    public async Task The_first_controller_to_answer_wins_and_the_other_gets_a_conflict_that_says_who()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var other = await rig.Conversation.ConnectAsync("phone");
        var env = await CreateAsync(rig.Owner, Input(sshd));

        var connect = rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        var seenByOwner = await NextAsync<SshHostKeyInteractionDto>(rig.Owner);
        var seenByPhone = await NextAsync<SshHostKeyInteractionDto>(other); // 任何已连接的控制端都看得到
        Assert.Equal(seenByOwner.Id, seenByPhone.Id);

        await Respond(other, seenByPhone, new SshHostKeyResponseDto(true));
        var late = await Assert.ThrowsAsync<ProtocolException>(() => Respond(rig.Owner, seenByOwner, new SshHostKeyResponseDto(true)));
        Assert.Equal(ErrorCodes.Conflict, late.Code);
        var details = FrameCodec.DecodePayload(late.Details!, PayloadShapes.GetShape<InteractionConflictDto>());
        Assert.Equal(ConflictReasons.AlreadyResolved, details.Reason);
        Assert.Equal("phone", details.Resolution!.ResolvedBy);
        Assert.Equal(EnvironmentStateNames.Ready, (await connect).State);
    }

    [Fact]
    public async Task A_cancelled_connect_request_clears_its_prompt_and_leaves_the_environment_idle()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var env = await CreateAsync(rig.Owner, Input(sshd));
        var asker = await rig.Conversation.ConnectAsync("asker");

        using var cts = new CancellationTokenSource();
        var connect = asker.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id), cts.Token);
        var prompt = await NextAsync<SshHostKeyInteractionDto>(rig.Owner);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => connect);

        // 断线 / 取消清理:挂起的交互以中止收尾(后到的回答是 conflict「已中止」),环境回到 idle,不是 failed
        await Watch<Unit>.UntilAsync(() => PendingCountAsync(rig.Owner).GetAwaiter().GetResult() == 0, "the prompt to be cleared");
        var late = await Assert.ThrowsAsync<ProtocolException>(() => Respond(rig.Owner, prompt, new SshHostKeyResponseDto(true)));
        Assert.Equal(ErrorCodes.Conflict, late.Code);
        Assert.Equal("aborted", FrameCodec.DecodePayload(late.Details!, PayloadShapes.GetShape<InteractionConflictDto>()).Resolution!.Status);
        Assert.Equal(EnvironmentStateNames.Idle, (await rig.Owner.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(env.Id))).State);
        Assert.Empty((await rig.Owner.CallAsync(EnvironmentsMethods.KnownHosts, Unit.Value)).Hosts);
    }

    [Fact]
    public async Task A_dropped_controller_clears_the_prompt_its_request_was_waiting_on()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var env = await CreateAsync(rig.Owner, Input(sshd));
        var asker = await rig.Conversation.ConnectAsync("asker");

        var connect = asker.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        await NextAsync<SshHostKeyInteractionDto>(rig.Owner);
        await asker.DisposeAsync(); // 控制端掉线

        await Watch<Unit>.UntilAsync(() => PendingCountAsync(rig.Owner).GetAwaiter().GetResult() == 0, "the prompt to be cleared after the controller dropped");
        await Assert.ThrowsAnyAsync<Exception>(() => connect);
        await WaitForEnvironmentAsync(rig.Owner, env.Id, e => e.State == EnvironmentStateNames.Idle, "the environment to be idle again");
    }

    // ───────────────────────── 试连接、浏览、导入 ─────────────────────────

    [Fact]
    public async Task Testing_a_draft_connects_once_reports_a_structured_result_and_leaves_nothing_behind_but_the_pinned_host_key()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);

        var test = rig.Owner.CallAsync(EnvironmentsMethods.Test, new EnvironmentTestParams(Input(sshd)));
        var prompt = await NextAsync<SshHostKeyInteractionDto>(rig.Owner);
        Assert.StartsWith("test-", prompt.Env.Environment); // 草稿没有保存,用临时的环境标识
        await Respond(rig.Owner, prompt, new SshHostKeyResponseDto(true));
        var ok = await test;
        Assert.True(ok.Ok);
        Assert.Equal(sshd.Username, ok.Facts!.Username);
        Assert.NotNull(ok.LatencyMs);
        Assert.Single((await rig.Owner.CallAsync(EnvironmentsMethods.List, Unit.Value)).Environments); // 没有保存任何环境
        // 指纹确认后钉扎,和正式连接是同一个指纹库:接着正式创建、连接不再问
        var env = await CreateAsync(rig.Owner, Input(sshd));
        Assert.Equal(EnvironmentStateNames.Ready, (await rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id))).State);

        // 失败是结果:私钥不对 → authentication
        var otherKey = Path.Combine(rig.RemoteDir, "other_key");
        using (var keygen = Process.Start(new ProcessStartInfo(SshdInstance.KeygenPath!, ["-q", "-t", "ed25519", "-N", "", "-f", otherKey]) { UseShellExecute = false })!)
        {
            await keygen.WaitForExitAsync();
        }

        var wrongKey = await rig.Owner.CallAsync(EnvironmentsMethods.Test, new EnvironmentTestParams(Input(sshd, identity: otherKey)));
        Assert.Equal((false, EnvironmentErrorKeys.Authentication), (wrongKey.Ok, wrongKey.Error!.Key));
        // 草稿里现填的口令用来解私钥,不问人;已存环境的试连接可以沿用它已保存的
        var withPassphrase = await rig.Owner.CallAsync(EnvironmentsMethods.Test, new EnvironmentTestParams(Input(sshd, identity: sshd.EncryptedKey), null, new EnvironmentSecretsInput(KeyPassphrase: Passphrase)));
        Assert.True(withPassphrase.Ok);
        Assert.Equal(ErrorCodes.NotFound, (await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Test, new EnvironmentTestParams(Input(sshd), "no-such-env")))).Code);
        AssertNoSecretsOnTheWire(rig.Tap, Passphrase, sshd.Directory);
    }

    [Fact]
    public async Task Browsing_lists_remote_directories_with_breadcrumbs_and_reports_bad_paths()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        Directory.CreateDirectory(Path.Combine(rig.RemoteDir, "beta"));
        Directory.CreateDirectory(Path.Combine(rig.RemoteDir, "Alpha"));
        await File.WriteAllTextAsync(Path.Combine(rig.RemoteDir, "file.txt"), "x");
        var env = await CreateAsync(rig.Owner, Input(sshd));

        // 浏览会先连接(指纹交互照常出现)
        var browse = rig.Owner.CallAsync(EnvironmentsMethods.Browse, new EnvironmentBrowseParams(env.Id, rig.RemoteDir));
        await Respond(rig.Owner, await NextAsync<SshHostKeyInteractionDto>(rig.Owner), new SshHostKeyResponseDto(true));
        var listing = await browse;
        Assert.Equal(["Alpha", "beta"], listing.Entries.Select(e => e.Name)); // 只有目录,按名字不分大小写排序
        Assert.EndsWith("/beta", listing.Entries[1].Path);
        Assert.Equal("/", listing.Roots.Single());
        Assert.Equal("/", listing.Breadcrumbs[0].Path);
        Assert.Equal(listing.Path, listing.Breadcrumbs[^1].Path);
        Assert.Equal(Path.GetDirectoryName(listing.Path), listing.Parent);
        Assert.False(listing.Truncated);

        var home = await rig.Owner.CallAsync(EnvironmentsMethods.Browse, new EnvironmentBrowseParams(env.Id));
        Assert.Equal(listing.Facts.Username, home.Facts.Username);
        Assert.Equal(home.Path, home.Breadcrumbs[^1].Path);

        Assert.Equal((ErrorCodes.NotFound, EnvironmentErrorKeys.NotFoundPath), await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Browse, new EnvironmentBrowseParams(env.Id, rig.RemoteDir + "/nope"))));
        Assert.Equal(ErrorCodes.InvalidRequest, (await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Browse, new EnvironmentBrowseParams(env.Id, rig.RemoteDir + "/file.txt")))).Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Browse, new EnvironmentBrowseParams(env.Id, "relative/path")))).Code);
        // 本机环境不能浏览:设备的文件系统结构不是 member 该看的
        Assert.Equal(EnvironmentErrorKeys.NotRemote, (await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Browse, new EnvironmentBrowseParams("local")))).Key);
    }

    [Fact]
    public async Task Importing_ssh_config_lists_aliases_and_an_alias_environment_connects_through_the_config()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        Directory.CreateDirectory(Path.Combine(sshd.Directory, ".ssh"));
        var config = Path.Combine(sshd.Directory, ".ssh", "config");
        await File.WriteAllTextAsync(config, $"""
            Host alias-box
              HostName 127.0.0.1
              Port {sshd.Port}
              User {sshd.Username}
              IdentityFile {sshd.ClientKey}
            Host proxied
              HostName 10.9.9.9
              ProxyCommand ssh -W %h:%p bastion
            Host *
              ServerAliveInterval 30
            """);
        try
        {
            var imported = await rig.Owner.CallAsync(EnvironmentsMethods.ImportSshConfig, new ImportSshConfigParams());
            Assert.Equal(["alias-box", "proxied"], imported.Candidates.Select(c => c.Alias));
            var alias = imported.Candidates[0];
            Assert.Equal(("127.0.0.1", sshd.Port, sshd.Username, true, true), (alias.HostName, alias.Port, alias.Username, alias.HasIdentityFile, alias.Importable));
            Assert.False(imported.Candidates[1].Importable); // ProxyCommand 不支持
            Assert.Contains("environments.import.proxyCommand", imported.Candidates[1].Warnings);

            // 用别名建环境(端口 / 用户 / 私钥全来自 ssh_config)并连上
            var env = await CreateAsync(rig.Owner, new EnvironmentInput("via-config", new SshTargetInput("config", "alias-box")));
            Assert.Equal("config", env.Target!.Kind);
            Assert.Equal(EnvironmentStateNames.Ready, (await ConnectTrustedAsync(rig.Owner, env.Id)).State);
            AssertNoSecretsOnTheWire(rig.Tap, sshd.ClientKey, config);
        }
        finally
        {
            File.Delete(config);
        }
    }

    // ───────────────────────── 项目绑定 ─────────────────────────

    [Fact]
    public async Task A_project_can_be_bound_to_an_ssh_environment_and_lists_its_environment()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var env = await CreateAsync(rig.Owner, Input(sshd));

        // 添加 SSH 项目会先连接环境:首次指纹以交互出现
        var add = AddRemoteProjectAsync(rig.Owner, env.Id, rig.RemoteDir);
        await Respond(rig.Owner, await NextAsync<SshHostKeyInteractionDto>(rig.Owner), new SshHostKeyResponseDto(true));
        var project = await add;
        Assert.Equal(("ssh", env.Id, "build-box"), (project.Environment, project.EnvironmentId, project.EnvironmentName));
        Assert.EndsWith(Path.GetFileName(rig.RemoteDir), project.Path);
        Assert.StartsWith("build-box: ", project.Name);

        var list = await rig.Owner.CallAsync(DeviceMethods.ProjectsList, Unit.Value);
        var listed = Assert.Single(list.Projects);
        Assert.Equal((project.WorkspaceId, "ssh", env.Id), (listed.WorkspaceId, listed.Environment, listed.EnvironmentId));
        // 同一个环境里同一个目录只登记一次;名字可以自己给
        Assert.Equal(project.WorkspaceId, (await AddRemoteProjectAsync(rig.Owner, env.Id, rig.RemoteDir)).WorkspaceId);
        // 本机项目照旧,没有环境 id
        var local = await rig.Conversation.AddProjectAsync(rig.Owner);
        Assert.Equal(("local", null), (local.Environment, local.EnvironmentId));

        // 不存在的环境 / 不是目录 / 相对路径 / 本机环境 id 当本机项目
        Assert.Equal(ErrorCodes.NotFound, (await FailureOf(() => AddRemoteProjectAsync(rig.Owner, "no-such-env", rig.RemoteDir))).Code);
        await File.WriteAllTextAsync(Path.Combine(rig.RemoteDir, "f.txt"), "x");
        Assert.Equal(ErrorCodes.InvalidRequest, (await FailureOf(() => AddRemoteProjectAsync(rig.Owner, env.Id, Path.Combine(rig.RemoteDir, "f.txt")))).Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await FailureOf(() => AddRemoteProjectAsync(rig.Owner, env.Id, "relative"))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await FailureOf(() => AddRemoteProjectAsync(rig.Owner, env.Id, rig.RemoteDir + "/missing"))).Code);

        // 项目在用时:环境不能删,改了目标要确认
        Assert.Equal(EnvironmentErrorKeys.InUse, (await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Remove, new EnvironmentIdParams(env.Id)))).Key);
        var moved = Input(sshd) with { Target = new SshTargetInput("manual", "localhost", sshd.Port, sshd.Username) };
        var refused = await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Update, new EnvironmentUpdateParams(env.Id, 1, moved)));
        Assert.Equal((ErrorCodes.Conflict, EnvironmentErrorKeys.TargetChangeNeedsConfirm), refused);
        // 不碰目标的修改(改名)不需要确认
        Assert.Equal("renamed", (await rig.Owner.CallAsync(EnvironmentsMethods.Update, new EnvironmentUpdateParams(env.Id, 1, Input(sshd, name: "renamed")))).Name);
        // 删除项目之后环境才能删
        await rig.Owner.CallAsync(DeviceMethods.ProjectsRemove, new ProjectsRemoveParams(project.WorkspaceId));
        await rig.Owner.CallAsync(EnvironmentsMethods.Remove, new EnvironmentIdParams(env.Id));
    }

    // ───────────────────────── 文件 / git / 终端 ─────────────────────────

    private static async Task Git(string dir, params string[] args)
    {
        using var process = Process.Start(new ProcessStartInfo("git", args) { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {await process.StandardError.ReadToEndAsync()}");
    }

    private static async Task<ProjectDto> ConnectedProjectAsync(Rig rig, string name = "build-box", string? identity = null)
    {
        var env = await CreateAsync(rig.Owner, Input(rig.Sshd, identity, name: name));
        await ConnectTrustedAsync(rig.Owner, env.Id);
        return await AddRemoteProjectAsync(rig.Owner, env.Id, rig.RemoteDir);
    }

    [Fact]
    public async Task Files_and_git_work_on_the_ssh_project_through_the_environment()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        await File.WriteAllTextAsync(Path.Combine(rig.RemoteDir, "hello.txt"), "hello from the remote\n");
        Directory.CreateDirectory(Path.Combine(rig.RemoteDir, "src"));
        await File.WriteAllTextAsync(Path.Combine(rig.RemoteDir, "src", "main.cs"), "class A;\n");
        await Git(rig.RemoteDir, "init", "-q", "-b", "main");
        await Git(rig.RemoteDir, "-c", "user.email=t@t", "-c", "user.name=t", "add", ".");
        await Git(rig.RemoteDir, "-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "-m", "init");
        var project = await ConnectedProjectAsync(rig);

        var listing = await rig.Owner.CallAsync(FilesMethods.List, new FilesListParams(project.WorkspaceId));
        Assert.Contains(listing.Entries, e => e.Name == "hello.txt");
        Assert.Contains(listing.Entries, e => e is { Name: "src", Kind: "dir" });
        var read = await rig.Owner.CallAsync(FilesMethods.Read, new FilesReadParams(project.WorkspaceId, "hello.txt"));
        Assert.Equal("hello from the remote\n", read.Content);
        // 写:乐观锁 + 原子替换(经 SFTP)
        var written = await rig.Owner.CallAsync(FilesMethods.Write, new FilesWriteParams(project.WorkspaceId, "hello.txt", read.Revision, "changed on the remote\n"));
        Assert.Equal("changed on the remote\n", await File.ReadAllTextAsync(Path.Combine(rig.RemoteDir, "hello.txt")));
        Assert.NotEqual(read.Revision, written.Revision);
        // 响应里没有设备 / 远程上的绝对路径:路径是项目相对的
        Assert.DoesNotContain(rig.RemoteDir, written.Path, StringComparison.Ordinal);

        var overview = await rig.Owner.CallAsync(GitMethods.Overview, new GitWorkspaceParams(project.WorkspaceId));
        Assert.True(overview.Available, overview.Reason);
        Assert.Equal("main", overview.Branch);
        Assert.Contains(overview.Files!, f => f.Path == "hello.txt");
        var commits = await rig.Owner.CallAsync(GitMethods.Commits, new GitCommitsParams(project.WorkspaceId, 5));
        Assert.Equal("init", Assert.Single(commits.Commits).Subject);
    }

    [Fact]
    public async Task Terminals_on_the_ssh_project_need_a_per_terminal_approval_that_names_the_environment()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var project = await ConnectedProjectAsync(rig);

        var prepared = await rig.Owner.CallAsync(TerminalMethods.Prepare, new TerminalsPrepareParams(project.WorkspaceId, 100, 30));
        Assert.Null(prepared.Terminal);
        var intent = prepared.Intent!;
        Assert.Equal(("ssh", "build-box", project.EnvironmentId), (intent.Environment.Kind, intent.Environment.Name, intent.Environment.Environment));
        Assert.Equal(rig.Conversation.Host.DeviceId, intent.Environment.DeviceId);

        var approval = (await rig.Owner.CallAsync(TerminalMethods.Approve, new TerminalsApproveParams(intent.Id, true))).Approval!;
        var terminal = await rig.Owner.CallAsync(TerminalMethods.Create, new TerminalsCreateParams(project.WorkspaceId, 100, 30, intent.TerminalId, null, approval));
        Assert.Equal(("ssh", project.EnvironmentId, "running"), (terminal.Environment.Kind, terminal.Environment.Environment, terminal.Status));

        var topic = await Watch<TerminalSnapshotDto>.OpenAsync(rig.Owner, TerminalMethods.Topic(terminal.Id));
        await rig.Owner.CallAsync(TerminalMethods.Write, new TerminalsWriteParams(terminal.Id, Encoding.UTF8.GetBytes("echo marker-$((6*7))-$SSH_CONNECTION | cut -c1-12\n")));
        await topic.WaitAsync(() => Text(topic).Contains("marker-42-12", StringComparison.Ordinal), "the remote shell's output", 30_000);

        // 终端在用,环境就不能被断开
        Assert.Equal(EnvironmentErrorKeys.Busy, (await FailureOf(() => rig.Owner.CallAsync(EnvironmentsMethods.Disconnect, new EnvironmentIdParams(project.EnvironmentId!)))).Key);
        await rig.Owner.CallAsync(TerminalMethods.Close, new TerminalRefParams(terminal.Id));
    }

    private static string Text(Watch<TerminalSnapshotDto> watch) =>
        (watch.HasSnapshot ? Encoding.UTF8.GetString(watch.Snapshot.Data) : "") + string.Concat(watch.Decoded<TerminalDataDto>(TerminalEvents.Data).Select(d => Encoding.UTF8.GetString(d.Data)));

    // ───────────────────────── 工具 + 审批 ─────────────────────────

    [Fact]
    public async Task Read_Edit_and_Bash_run_on_the_ssh_environment_and_every_approval_and_tool_card_carries_it()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var target = Path.Combine(rig.RemoteDir, "notes.txt");
        await File.WriteAllTextAsync(target, "one\ntwo\n");
        var project = await ConnectedProjectAsync(rig);
        var session = await rig.Conversation.CreateSessionAsync(rig.Owner, project);
        var path = Path.Combine(project.Path, "notes.txt");
        string Json(params (string, string)[] pairs) => new System.Text.Json.Nodes.JsonObject(pairs.Select(p => KeyValuePair.Create(p.Item1, (System.Text.Json.Nodes.JsonNode?)p.Item2))).ToJsonString();
        rig.Conversation.Llm
            .Then(Up.Calls(("c1", "Read", Json(("file_path", path)))))
            .Then(Up.Calls(("c2", "Edit", Json(("file_path", path), ("old_string", "two"), ("new_string", "2")))))
            .Then(Up.Calls(("c3", "Bash", Json(("command", "echo ssh=$SSH_CONNECTION | cut -c1-8; cat notes.txt")))))
            .Then(Up.Say("all done"));

        var start = await rig.Conversation.StartAsync(rig.Owner, session.Id, "work on the remote");
        var run = await RunWatch.OpenAsync(rig.Owner, start.RunId);

        // Read 是只读工具,直接放行;Edit 和 Bash 要审批 —— 审批卡片带「设备 + 环境」标识
        var edit = await NextAsync<ToolPermissionInteractionDto>(rig.Owner, p => p.ToolName == "Edit");
        Assert.Equal((rig.Conversation.Host.DeviceId, project.EnvironmentId, "build-box", "ssh"), (edit.Env.DeviceId, edit.Env.Environment, edit.Env.Name, edit.Env.Type));
        await Respond(rig.Owner, edit, new ToolPermissionResponseDto(PermissionChoices.AllowOnce));
        var bash = await NextAsync<ToolPermissionInteractionDto>(rig.Owner, p => p.ToolName == "Bash");
        Assert.Equal(("ssh", "build-box", project.EnvironmentId), (bash.Env.Type, bash.Env.Name, bash.Env.Environment));
        await Respond(rig.Owner, bash, new ToolPermissionResponseDto(PermissionChoices.AllowOnce));
        await run.SettledAsync(60_000);

        Assert.Equal("done", Assert.Single(run.Of<RunEndEventDto>()).Status);
        Assert.Equal("one\n2\n", await File.ReadAllTextAsync(target)); // Edit 真的改了远程文件
        // 工具卡片:每个 tool_start 都带环境标识(设备 + 环境 id;名字和种类由控制端按 environments 镜像里的 (设备, 环境) 取,tool_start 里只有 id)
        var starts = run.Of<ToolStartEventDto>().ToList();
        Assert.Equal(["Read", "Edit", "Bash"], starts.Select(s => s.ToolName));
        Assert.All(starts, s => Assert.Equal((rig.Conversation.Host.DeviceId, project.EnvironmentId), (s.Environment!.DeviceId, s.Environment.Environment)));
        var results = run.Of<ToolEndEventDto>().ToList();
        Assert.All(results, r => Assert.False(r.IsError, r.Content));
        Assert.Contains("one", results[0].Content);
        // Bash 经 SSH exec:SSH_CONNECTION 只在 ssh 会话里有;cwd 是项目根(remote 目录),所以 cat 读到了刚改的文件
        Assert.Contains("ssh=127.", results[2].Content);
        Assert.Contains("one\n2\n", results[2].Content);
        AssertNoSecretsOnTheWire(rig.Tap, sshd.ClientKey);
    }

    [Fact]
    public async Task A_run_on_an_idle_ssh_project_connects_lazily_and_asks_for_the_host_key_through_an_interaction()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd, idle: TimeSpan.FromMilliseconds(400));
        var project = await ConnectedProjectAsync(rig);
        // 空闲 400ms 后连接被回收(2.x 的 60 秒,测试里缩短)
        await WaitForEnvironmentAsync(rig.Owner, project.EnvironmentId!, e => e.State == EnvironmentStateNames.Idle, "the idle connection to be recycled");

        // 忘掉指纹:下一次使用(runs.start)必须重新确认。Run 在这里等用户回答。
        await rig.Owner.CallAsync(EnvironmentsMethods.ForgetHostKey, new ForgetHostKeyParams($"[127.0.0.1]:{sshd.Port}"));
        var session = await rig.Conversation.CreateSessionAsync(rig.Owner, project);
        rig.Conversation.Llm.Then(Up.Say("hello from the remote"));
        var start = rig.Conversation.StartAsync(rig.Owner, session.Id, "hi");
        var prompt = await NextAsync<SshHostKeyInteractionDto>(rig.Owner);
        Assert.Equal(project.EnvironmentId, prompt.Env.Environment);
        await Respond(rig.Owner, prompt, new SshHostKeyResponseDto(true));
        var started = await start;
        await (await RunWatch.OpenAsync(rig.Owner, started.RunId)).SettledAsync(60_000);
        Assert.Equal(EnvironmentStateNames.Ready, (await rig.Owner.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(project.EnvironmentId!))).State);
    }

    // ───────────────────────── 扩展域:Skills / 命令 / Hooks ─────────────────────────

    private static byte[] SkillZip(string name)
    {
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry($"{name}/SKILL.md").Open(), new UTF8Encoding(false));
            writer.Write($"---\nname: {name}\ndescription: {name} description\n---\nbody of {name}\n");
        }

        return memory.ToArray();
    }

    [Fact]
    public async Task Skills_commands_and_hooks_read_write_and_run_through_the_ssh_environment()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The hook script needs a POSIX shell.");
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var project = await ConnectedProjectAsync(rig);
        await rig.Owner.CallAsync(EnvironmentsMethods.Disconnect, new EnvironmentIdParams(project.EnvironmentId!)); // 路由前置闸门要把它重新连上

        // Skills:项目层安装 = 经 SSH 环境解压写到远程的 .next-cowork/skills(原子改名),list 经环境重新扫
        var install = await rig.Owner.CallAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(SkillZip("remote-skill"), "remote-skill.zip", SkillScopes.Project, project.WorkspaceId));
        Assert.Equal("remote-skill", install.Installed.Name);
        Assert.True(File.Exists(Path.Combine(rig.RemoteDir, ".next-cowork", "skills", "remote-skill", "SKILL.md")));
        Assert.Contains((await rig.Owner.CallAsync(SkillsMethods.List, new SkillsScopeParams(project.WorkspaceId))).Skills, k => k.Name == "remote-skill");

        // 命令:远程 .next-cowork/commands/<name>.md
        Directory.CreateDirectory(Path.Combine(rig.RemoteDir, ".next-cowork", "commands"));
        await File.WriteAllTextAsync(Path.Combine(rig.RemoteDir, ".next-cowork", "commands", "ship.md"), "---\ndescription: ship it\n---\nShip $ARGUMENTS\n");
        Assert.Contains((await rig.Owner.CallAsync(CommandsMethods.List, new CommandsScopeParams(project.WorkspaceId))).Commands, c => c.Name == "ship");

        // Hooks:配置存在远程的 settings.local.json;hooks.test 真跑命令——在远程主机上(SSH_CONNECTION 只在 ssh 会话里有)
        var hook = new HookUpsertDto(null, "PreToolUse", "command", "Bash", true, 10_000, null, "echo \"ssh=$SSH_CONNECTION\" | cut -c1-8", null, null, null, null);
        var saved = await rig.Owner.CallAsync(HooksMethods.Save, new HooksSaveParams("project", project.WorkspaceId, hook));
        Assert.True(File.Exists(Path.Combine(rig.RemoteDir, ".next-cowork", "settings.local.json")));
        Assert.Contains((await rig.Owner.CallAsync(HooksMethods.List, new HooksListParams(project.WorkspaceId))).Hooks, h => h.Id == saved.Hook.Id);
        var report = await rig.Owner.CallAsync(HooksMethods.Test, new HooksTestParams("project", project.WorkspaceId, hook with { Id = saved.Hook.Id }));
        Assert.Equal(0, report.ExitCode);
        Assert.StartsWith("ssh=127.", report.Stdout);
    }

    // ───────────────────────── 断线重连 ─────────────────────────

    [Fact]
    public async Task A_lost_connection_reconnects_with_a_new_generation_and_the_tools_keep_working()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var project = await ConnectedProjectAsync(rig);
        var before = await rig.Owner.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(project.EnvironmentId!));
        Assert.Equal(1, before.Generation);
        var watch = await Watch<EnvironmentsTopicState>.OpenAsync(rig.Owner, EnvironmentsMethods.Topic);

        sshd.Stop(); // 服务器崩溃 / 网络断开
        await WaitForEnvironmentAsync(rig.Owner, project.EnvironmentId!, e => e.State == EnvironmentStateNames.Reconnecting, "the environment to notice the loss");
        // 重连期间,项目的文件操作最多等 ReadyWait;服务器回来之后成功
        await sshd.RestartAsync();
        var after = await WaitForEnvironmentAsync(rig.Owner, project.EnvironmentId!, e => e.State == EnvironmentStateNames.Ready, "the reconnect", 30_000);
        Assert.Equal(2, after.Generation);
        Assert.Equal(0, await PendingCountAsync(rig.Owner)); // 重连不再打扰用户(指纹已钉扎,私钥无口令)

        var seen = watch.Decoded<EnvironmentsTopicState>(EnvironmentsEvents.Changed).SelectMany(s => s.Environments).Where(e => e.Id == project.EnvironmentId).Select(e => e.State).ToList();
        Assert.Contains(EnvironmentStateNames.Reconnecting, seen);
        Assert.Equal(EnvironmentStateNames.Ready, seen[^1]);
        Assert.Equal("local", (await rig.Owner.CallAsync(EnvironmentsMethods.List, Unit.Value)).Environments[0].Id);

        await File.WriteAllTextAsync(Path.Combine(rig.RemoteDir, "after.txt"), "back");
        Assert.Equal("back", (await rig.Owner.CallAsync(FilesMethods.Read, new FilesReadParams(project.WorkspaceId, "after.txt"))).Content);
    }

    // ───────────────────────── 跳板机 ─────────────────────────

    [Fact]
    public async Task A_jump_host_asks_for_each_hops_host_key_and_the_target_is_reached_through_it()
    {
        var target = fixture.Require();
        using var jump = await SshdInstance.StartAsync();
        jump.AuthorizeKey(target.ClientKey + ".pub"); // 目标机的客户端私钥要被跳板机接受(手填目标的跳板机沿用目标的私钥)
        await using var rig = await Rig.StartAsync(target);

        var env = await CreateAsync(rig.Owner, Input(target, proxyJump: $"{jump.Username}@127.0.0.1:{jump.Port}"));
        var connect = rig.Owner.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        // 每一跳都按它真实的主机名校验指纹:跳板机一问、目标机一问
        var jumpPrompt = await NextAsync<SshHostKeyInteractionDto>(rig.Owner, h => h.HostName == $"[127.0.0.1]:{jump.Port}");
        await Respond(rig.Owner, jumpPrompt, new SshHostKeyResponseDto(true));
        var targetPrompt = await NextAsync<SshHostKeyInteractionDto>(rig.Owner, h => h.HostName == $"[127.0.0.1]:{target.Port}");
        Assert.NotEqual(jumpPrompt.Fingerprint, targetPrompt.Fingerprint);
        await Respond(rig.Owner, targetPrompt, new SshHostKeyResponseDto(true));
        Assert.Equal(EnvironmentStateNames.Ready, (await connect).State);
        Assert.Equal(2, (await rig.Owner.CallAsync(EnvironmentsMethods.KnownHosts, Unit.Value)).Hosts.Length);

        // 目标机的工具经跳板机的 direct-tcpip 通道跑
        await File.WriteAllTextAsync(Path.Combine(rig.RemoteDir, "via-jump.txt"), "through the tunnel");
        var project = await AddRemoteProjectAsync(rig.Owner, env.Id, rig.RemoteDir);
        Assert.Equal("through the tunnel", (await rig.Owner.CallAsync(FilesMethods.Read, new FilesReadParams(project.WorkspaceId, "via-jump.txt"))).Content);
        Assert.Contains("Accepted publickey", jump.Log); // 跳板机真的接受过这条连接
    }

    // ───────────────────────── 角色 ─────────────────────────

    [Fact]
    public async Task Viewers_cannot_even_list_environments_members_read_and_connect_but_only_owners_configure()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd);
        var project = await ConnectedProjectAsync(rig);
        var env = await rig.Owner.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(project.EnvironmentId!));
        await rig.Owner.CallAsync(EnvironmentsMethods.Disconnect, new EnvironmentIdParams(env.Id));

        await using var viewer = (await rig.ConnectRemoteAsync("viewer-1", "viewer")).Client;
        await using var member = (await rig.ConnectRemoteAsync("member-1", "member")).Client;

        // viewer:环境清单有内网主机名和用户名,属于设备配置,整个领域都不给(方法和主题)
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => viewer.CallAsync(EnvironmentsMethods.List, Unit.Value))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => viewer.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(env.Id)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => viewer.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id)))).Code);
        await Assert.ThrowsAnyAsync<ProtocolException>(() => Watch<EnvironmentsTopicState>.OpenAsync(viewer, EnvironmentsMethods.Topic));
        // viewer 也不能替用户回答连接提问
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => viewer.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams("i", 1, new SshHostKeyResponseDto(true))))).Code);
        // viewer 对没连着的 SSH 项目:读不了,但绝不会因此让设备去连内网主机或弹出确认框(unavailable + 稳定 key)
        var notConnected = await FailureOf(() => viewer.CallAsync(FilesMethods.List, new FilesListParams(project.WorkspaceId)));
        Assert.Equal((ErrorCodes.Unavailable, EnvironmentErrorKeys.NotConnected), notConnected);
        Assert.Equal(EnvironmentStateNames.Idle, (await rig.Owner.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(env.Id))).State);
        Assert.Equal(0, await PendingCountAsync(rig.Owner));

        // member:读清单、订阅主题、连接、回答连接提问、在 SSH 项目上用文件;不能改配置
        var memberWatch = await Watch<EnvironmentsTopicState>.OpenAsync(member, EnvironmentsMethods.Topic);
        await memberWatch.WaitAsync(() => memberWatch.HasSnapshot, "the member's snapshot");
        Assert.Equal(2, (await member.CallAsync(EnvironmentsMethods.List, Unit.Value)).Environments.Length);
        Assert.Equal(EnvironmentStateNames.Ready, (await member.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id))).State); // 指纹已钉扎:不用问
        await File.WriteAllTextAsync(Path.Combine(rig.RemoteDir, "m.txt"), "member");
        Assert.Equal("member", (await member.CallAsync(FilesMethods.Read, new FilesReadParams(project.WorkspaceId, "m.txt"))).Content);
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => CreateAsync(member, Input(sshd)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => member.CallAsync(EnvironmentsMethods.Update, new EnvironmentUpdateParams(env.Id, 1, Input(sshd))))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => member.CallAsync(EnvironmentsMethods.Remove, new EnvironmentIdParams(env.Id)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => member.CallAsync(EnvironmentsMethods.SetDefault, new EnvironmentIdParams(env.Id)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => member.CallAsync(EnvironmentsMethods.Test, new EnvironmentTestParams(Input(sshd))))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => member.CallAsync(EnvironmentsMethods.ImportSshConfig, new ImportSshConfigParams()))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => member.CallAsync(EnvironmentsMethods.KnownHosts, Unit.Value))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await FailureOf(() => member.CallAsync(EnvironmentsMethods.ForgetHostKey, new ForgetHostKeyParams("h")))).Code);
        // 回答连接提问的 member:viewer 看不到的提问,member 看得到、答得了
        await rig.Owner.CallAsync(EnvironmentsMethods.Disconnect, new EnvironmentIdParams(env.Id));
        await rig.Owner.CallAsync(EnvironmentsMethods.ForgetHostKey, new ForgetHostKeyParams($"[127.0.0.1]:{sshd.Port}"));
        var memberConnect = member.CallAsync(EnvironmentsMethods.Connect, new EnvironmentIdParams(env.Id));
        await Respond(member, await NextAsync<SshHostKeyInteractionDto>(member), new SshHostKeyResponseDto(true));
        Assert.Equal(EnvironmentStateNames.Ready, (await memberConnect).State);
    }

    // ───────────────────────── 空闲回收 ─────────────────────────

    [Fact]
    public async Task An_idle_connection_is_recycled_but_one_with_a_live_terminal_is_not_and_the_next_use_reconnects_lazily()
    {
        var sshd = fixture.Require();
        await using var rig = await Rig.StartAsync(sshd, idle: TimeSpan.FromMilliseconds(500));
        var project = await ConnectedProjectAsync(rig);
        var id = project.EnvironmentId!;

        // 有活终端:不回收(终端不是租约持有者,靠忙碌探针)
        var intent = (await rig.Owner.CallAsync(TerminalMethods.Prepare, new TerminalsPrepareParams(project.WorkspaceId, 80, 24))).Intent!;
        var approval = (await rig.Owner.CallAsync(TerminalMethods.Approve, new TerminalsApproveParams(intent.Id, true))).Approval!;
        var terminal = await rig.Owner.CallAsync(TerminalMethods.Create, new TerminalsCreateParams(project.WorkspaceId, 80, 24, intent.TerminalId, null, approval));
        await Task.Delay(1800);
        Assert.Equal(EnvironmentStateNames.Ready, (await rig.Owner.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(id))).State);

        // 终端关了:没人用了,空闲回收;配置和凭据保留
        await rig.Owner.CallAsync(TerminalMethods.Close, new TerminalRefParams(terminal.Id));
        var idle = await WaitForEnvironmentAsync(rig.Owner, id, e => e.State == EnvironmentStateNames.Idle, "the idle connection to be recycled");
        Assert.Equal(0, idle.Generation);
        Assert.Equal("ssh", (await rig.Owner.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single().Environment);

        // 下一次使用惰性重连(已钉扎、无口令:不打扰用户)
        await File.WriteAllTextAsync(Path.Combine(rig.RemoteDir, "lazy.txt"), "lazy");
        Assert.Equal("lazy", (await rig.Owner.CallAsync(FilesMethods.Read, new FilesReadParams(project.WorkspaceId, "lazy.txt"))).Content);
        Assert.Equal(EnvironmentStateNames.Ready, (await rig.Owner.CallAsync(EnvironmentsMethods.Status, new EnvironmentIdParams(id))).State);
        Assert.Equal(0, await PendingCountAsync(rig.Owner));
    }
}
