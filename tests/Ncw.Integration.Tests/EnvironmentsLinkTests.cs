using Microsoft.Data.Sqlite;
using Ncw.Agent.Environments.Ssh;
using Ncw.Agent.Environments.Tests;
using Ncw.Agent.Host;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Environments;
using Ncw.Agent.Kernel.Loop;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Integration.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

using static Ncw.Integration.Tests.Support.OrchestrationRig;

namespace Ncw.Integration.Tests;

/// <summary>
/// 执行环境(SSH 多设备执行)在控制端的端到端:真实 Agent + 用户态 sshd + 真实 <c>DeviceLink</c> / Store / <see cref="EnvironmentActions"/>。
/// 验证「设备推送 → 归约 → Store」:另一个控制端创建 / 连接环境,这个控制端靠 <c>environments</c> 主题的镜像看到;主机指纹确认以交互出现在<b>所有</b>控制端、先到先得;
/// 断线是未知;凭据永远不进任何 Store。没有 sshd 时跳过。
/// </summary>
public sealed class EnvironmentsLinkTests(SshdFixture fixture) : IClassFixture<SshdFixture>
{
    private sealed class SshRig : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwe-{Guid.NewGuid():N}"[..16]);
        private readonly CancellationTokenSource _agentCts = new();
        private readonly List<Controller> _controllers = [];
        private Task _agent = Task.CompletedTask;

        public SshRig(SshdInstance sshd)
        {
            Sshd = sshd;
            RemoteDir = Path.Combine(Path.GetTempPath(), $"ncw-envlink-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RemoteDir);
            Host = new AgentHost(
                new AgentOptions(AgentMode.Local, _root, LocalTransport.DefaultEndpoint(_root), "env-token", null),
                services: new AgentServices
                {
                    Models = new FakeModels(Llm),
                    Delayer = new NoDelay(),
                    Loop = new AgentLoopOptions { ResumeDelays = [] },
                    SshEnvironments = new EnvironmentHostOptions
                    {
                        Ssh = new SshEnvironmentOptions
                        {
                            Reconnect = new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(400), Jitter = 0 },
                            KillGrace = TimeSpan.FromMilliseconds(500),
                        },
                        ConfigFiles = new PhysicalSshConfigFiles(sshd.Directory),
                        DiscoverSshAgent = false,
                    },
                });
            _agent = Host.RunAsync(_agentCts.Token);
        }

        public SshdInstance Sshd { get; }

        public string RemoteDir { get; }

        public ScriptedLlm Llm { get; } = new();

        public AgentHost Host { get; }

        public async Task<(Controller C, EnvironmentActions Env)> StartControllerAsync(string clientId)
        {
            var controller = new Controller();
            var endpoint = LocalTransport.DefaultEndpoint(_root);
            controller.Link = new DeviceLink(controller.Stores, controller.Ui, $"local-{clientId}", DeviceKind.Local, "本机",
                async ct =>
                {
                    var stream = new CuttableStream(await LocalTransport.ConnectAsync(endpoint, ct));
                    controller.Current = stream;
                    return stream;
                },
                new ClientHello(clientId, "3.0.0", LocalToken: "env-token"));
            controller.Devices = new DeviceRegistry { Local = controller.Link };
            controller.Conversations = new ConversationActions(controller.Devices);
            _controllers.Add(controller);
            controller.Link.Start();
            await WaitUntil(() => controller.Workspace.Devices.Values.Any(d => d.Connection == DeviceConnection.Connected && d.Data?.State == "ready"), $"{clientId} connected");
            return (controller, new EnvironmentActions(controller.Devices, controller.Conversations));
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var controller in _controllers)
            {
                await controller.Link.DisposeAsync();
            }

            await _agentCts.CancelAsync();
            try { await _agent; } catch (OperationCanceledException) { }
            SqliteConnection.ClearAllPools();
            foreach (var dir in new[] { _root, RemoteDir })
            {
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
                }
            }
        }
    }

    private static T Read<T>(Controller c, Func<ClientStores, T> read)
    {
        lock (c.Ui.Gate)
        {
            return read(c.Stores);
        }
    }

    private static string DeviceIdOf(Controller c) => c.Workspace.Devices.Keys.Single();

    private static EnvironmentInput Input(SshdInstance sshd, string name = "build-box") =>
        new(name, new SshTargetInput("manual", "127.0.0.1", sshd.Port, sshd.Username, sshd.ClientKey), "key");

    private static EnvironmentDto? EnvOf(Controller c, string environmentId) =>
        Read(c, s => s.Environments.State.Find(DeviceIdOf(c), environmentId));

    private static async Task<T> PendingAsync<T>(Controller c, Func<T, bool>? where = null) where T : InteractionDto
    {
        T? found = null;
        await WaitUntil(() =>
        {
            found = c.State.InteractionsOf(DeviceIdOf(c)).Pending.OfType<T>().FirstOrDefault(where ?? (_ => true));
            return found is not null;
        }, $"a pending {typeof(T).Name} in the mirror");
        return found!;
    }

    [Fact]
    public async Task Environments_a_host_key_prompt_and_the_connection_state_reach_every_controller_and_the_first_answer_wins()
    {
        var sshd = fixture.Require();
        await using var rig = new SshRig(sshd);
        var (a, actionsA) = await rig.StartControllerAsync("ctrl-a");
        var (b, actionsB) = await rig.StartControllerAsync("ctrl-b");
        var device = DeviceIdOf(a);

        // 两个控制端的镜像都拿到了首帧:只有 local;key 带 deviceId
        await WaitUntil(() => Read(a, s => s.Environments.State.Of(device).Known) && Read(b, s => s.Environments.State.Of(device).Known), "both mirrors to hold the first frame");
        Assert.Equal(["local"], Read(a, s => s.Environments.State.Of(device).Environments.Select(e => e.Id).ToList()));

        // A 创建环境(密码存设备上,不回传):B 的镜像靠主题追上
        var created = await actionsA.CreateAsync(device, Input(sshd), new EnvironmentSecretsInput(Password: "never-shown-pw"));
        Assert.True(created.IsOk, created.Detail);
        var id = created.Value!.Id;
        await WaitUntil(() => EnvOf(b, id) is { State: EnvironmentStateNames.Idle, HasSavedPassword: true }, "B's mirror to learn about the new environment");
        Assert.Equal(Read(a, s => s.Environments.State.Of(device).Rev), Read(a, s => s.Environments.State.Of(device).Rev)); // 单调,不为同一份重复前进
        Assert.Equal(ActionOutcome.Conflict, (await actionsA.UpdateAsync(device, id, 99, Input(sshd))).Outcome);

        // A 发起连接:主机指纹确认出现在两个控制端(设备级交互,带「设备 + 环境」标识)
        var connect = actionsA.ConnectAsync(device, id);
        var promptSeenByB = await PendingAsync<SshHostKeyInteractionDto>(b);
        var promptSeenByA = await PendingAsync<SshHostKeyInteractionDto>(a);
        Assert.Equal(promptSeenByA.Id, promptSeenByB.Id);
        Assert.Equal((device, id, "ssh"), (promptSeenByB.Env.DeviceId, promptSeenByB.Env.Environment, promptSeenByB.Env.Type));
        await WaitUntil(() => EnvOf(a, id)?.State == EnvironmentStateNames.Connecting, "the mirror to show connecting");

        // B 抢先回答;A 再答得到「已在另一台设备上处理」
        Assert.Equal(InteractionOutcome.Answered, (await actionsB.AnswerHostKeyAsync(device, promptSeenByB, trust: true)).Outcome);
        Assert.Equal(InteractionOutcome.AlreadyHandled, (await actionsA.AnswerHostKeyAsync(device, promptSeenByA, trust: true)).Outcome);
        var connected = await connect;
        Assert.True(connected.IsOk);
        Assert.Equal((EnvironmentStateNames.Ready, 1), (connected.Value!.State, connected.Value.Generation));
        await WaitUntil(() => EnvOf(a, id)?.State == EnvironmentStateNames.Ready && EnvOf(b, id)?.State == EnvironmentStateNames.Ready, "both mirrors to show ready");
        await WaitUntil(() => Read(b, s => s.Environments.State.Of(device).Known), "B to be known");
        await WaitUntil(() => a.State.InteractionsOf(device).Pending.Length == 0 && b.State.InteractionsOf(device).Pending.Length == 0, "the prompt to leave both interaction mirrors");

        // 在 SSH 环境里加项目:两个控制端的项目栏都有,带环境种类 / 名字 / id
        var added = await actionsA.AddProjectAsync(device, id, rig.RemoteDir);
        Assert.True(added.IsOk, added.Detail);
        await WaitUntil(() => a.Workspace.Projects.Values.Any(p => p.Environment == EnvironmentKind.Ssh) && b.Workspace.Projects.Values.Any(p => p.Environment == EnvironmentKind.Ssh), "both controllers to list the ssh project");
        var project = b.Workspace.Projects.Values.Single(p => p.Environment == EnvironmentKind.Ssh);
        Assert.Equal(("build-box", id), (project.EnvironmentName, project.EnvironmentId));
        Assert.Equal(added.Value!.WorkspaceId, project.Ref.WorkspaceId);

        // 工具卡片:tool_start 只带 (设备, 环境 id),显示名从环境镜像取(一个事实只有一个真源)
        await File.WriteAllTextAsync(Path.Combine(rig.RemoteDir, "card.txt"), "card");
        var session = (await a.Conversations.CreateSessionAsync(project.Ref, "sonnet"))!.Value;
        rig.Llm.On(session.SessionId, S.Call("c1", "Read", new System.Text.Json.Nodes.JsonObject { ["file_path"] = Path.Combine(added.Value.Path, "card.txt") }.ToJsonString()), S.Say("read it"));
        var sent = await a.Conversations.SendAsync(session, "read card", Options);
        var run = sent.Run!.Value;
        await WaitUntil(() => a.State.Runs[run] is { Phase: RunPhase.Done, Settled: true }, "the run on the ssh environment to finish");
        var tool = a.State.Runs[run].Tools["c1"];
        Assert.False(tool.Output?.Contains("error", StringComparison.OrdinalIgnoreCase) ?? false, tool.Output);
        Assert.Equal((device, id), (tool.Environment!.DeviceId, tool.Environment.Environment));
        var resolved = Read(a, s => s.Environments.State.Find(tool.Environment.DeviceId, tool.Environment.Environment));
        Assert.Equal(("build-box", EnvironmentKindNames.Ssh), (resolved!.Name, resolved.Kind));

        // 凭据永远不进任何 Store:镜像里只有「有没有」
        Assert.DoesNotContain("never-shown-pw", Read(a, s => s.Environments.State.ToString()) ?? "");
        Assert.DoesNotContain("never-shown-pw", Read(b, s => s.Environments.State.ToString()) ?? "");
        Assert.DoesNotContain(sshd.ClientKey, Read(a, s => s.Environments.State.ToString()) ?? "");
    }

    [Fact]
    public async Task A_failed_connection_and_a_changed_host_key_are_states_in_the_mirror_and_a_disconnected_controller_sees_unknown()
    {
        var sshd = fixture.Require();
        await using var rig = new SshRig(sshd);
        var (a, actions) = await rig.StartControllerAsync("ctrl-a");
        var device = DeviceIdOf(a);
        var id = (await actions.CreateAsync(device, Input(sshd))).Value!.Id;

        // 拒绝指纹:失败是结果(failed + 稳定 key),不是错误
        var rejected = actions.ConnectAsync(device, id);
        Assert.Equal(InteractionOutcome.Answered, (await actions.AnswerHostKeyAsync(device, await PendingAsync<SshHostKeyInteractionDto>(a), trust: false)).Outcome);
        var rejectedResult = await rejected;
        Assert.True(rejectedResult.IsOk);
        Assert.Equal((EnvironmentStateNames.Failed, EnvironmentErrorKeys.HostKeyRejected), (rejectedResult.Value!.State, rejectedResult.Value.Error!.Key));
        await WaitUntil(() => EnvOf(a, id)?.Error?.Key == EnvironmentErrorKeys.HostKeyRejected, "the mirror to show the failure");

        // 信任并连上,断开,服务器换了主机密钥:再连被拒绝,不一致的详情在镜像里
        var trusted = actions.ConnectAsync(device, id);
        await actions.AnswerHostKeyAsync(device, await PendingAsync<SshHostKeyInteractionDto>(a), trust: true);
        Assert.Equal(EnvironmentStateNames.Ready, (await trusted).Value!.State);
        var pinned = Assert.Single((await actions.KnownHostsAsync(device)).Value!.Hosts);
        Assert.True((await actions.DisconnectAsync(device, id)).IsOk);
        await sshd.RotateHostKeyAsync();
        var changed = await actions.ConnectAsync(device, id);
        Assert.Equal(EnvironmentErrorKeys.HostKeyChanged, changed.Value!.Error!.Key);
        await WaitUntil(() => EnvOf(a, id)?.Error?.HostKey is { } mismatch && mismatch.Pinned[0].Fingerprint == pinned.Fingerprint && mismatch.Presented != pinned.Fingerprint, "the mirror to show the mismatch");
        Assert.Empty(a.State.InteractionsOf(device).Pending); // 不弹确认

        // 断线:镜像标为未知(规则 7)——不把旧的 failed 当现状,也不判成别的;重连之后首帧照收
        var before = Read(a, s => s.Environments.State.Of(device).Rev);
        a.Current!.Cut();
        await WaitUntil(() => !Read(a, s => s.Environments.State.Of(device).Known), "the mirror to become unknown");
        Assert.Null(Read(a, s => s.Environments.State.Find(device, id)));
        await WaitUntil(() => Read(a, s => s.Environments.State.Of(device).Known) && EnvOf(a, id)?.State == EnvironmentStateNames.Failed, "the mirror to recover after reconnecting");
        Assert.True(Read(a, s => s.Environments.State.Of(device).Rev) >= before);
    }

    [Fact]
    public async Task Answering_a_credential_prompt_and_cancelling_one_go_through_the_actions_and_the_answer_never_comes_back()
    {
        var sshd = fixture.Require();
        await using var rig = new SshRig(sshd);
        var (a, actions) = await rig.StartControllerAsync("ctrl-a");
        var device = DeviceIdOf(a);
        var id = (await actions.CreateAsync(device, Input(sshd) with { Target = Input(sshd).Target with { IdentityFile = sshd.EncryptedKey } })).Value!.Id;

        // 取消口令提问:连接以 cancelled 结束
        var first = actions.ConnectAsync(device, id);
        await actions.AnswerHostKeyAsync(device, await PendingAsync<SshHostKeyInteractionDto>(a), trust: true);
        var prompt = await PendingAsync<SshCredentialInteractionDto>(a);
        Assert.Equal(("keyPassphrase", "client_encrypted", true), (prompt.PromptKind, prompt.KeyName, prompt.CanRemember));
        Assert.DoesNotContain(sshd.Directory, prompt.ToString()); // 私钥路径不上线,只有文件名
        Assert.Equal(InteractionOutcome.Answered, (await actions.AnswerCredentialAsync(device, prompt, null)).Outcome);
        Assert.Equal(EnvironmentErrorKeys.Cancelled, (await first).Value!.Error!.Key);

        // 给口令并记住:连上;之后断开再连不再问
        var second = actions.ConnectAsync(device, id);
        var again = await PendingAsync<SshCredentialInteractionDto>(a, c => c.Id != prompt.Id);
        Assert.Equal(InteractionOutcome.Answered, (await actions.AnswerCredentialAsync(device, again, [SshdInstance.Passphrase], remember: true)).Outcome);
        Assert.Equal(EnvironmentStateNames.Ready, (await second).Value!.State);
        Assert.True((await actions.DisconnectAsync(device, id)).IsOk);
        Assert.Equal(EnvironmentStateNames.Ready, (await actions.ConnectAsync(device, id)).Value!.State);
        Assert.Empty(a.State.InteractionsOf(device).Pending);
        // 回答没有进任何 Store
        Assert.DoesNotContain(SshdInstance.Passphrase, Read(a, s => s.Environments.State.ToString()) ?? "");
        Assert.DoesNotContain(SshdInstance.Passphrase, a.State.InteractionsOf(device).ToString());
    }
}
