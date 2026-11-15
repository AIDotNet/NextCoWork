using Ncw.Agent.Cloud.Tests.Support;
using Ncw.Agent.Host;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Orchestration;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Cloud.Tests;

/// <summary>
/// 云端 Agent 的端到端测试(docs/design/02 A6、05 M6):假编排服务 + 假 KMS + 进程内中继 + 真实的 <c>--mode cloud</c> AgentHost + 控制端经中继。
/// 覆盖:启动配置 → 身份注册 → 信任下发 → 经中继连接并对话 → 空闲休眠 → 唤醒恢复 → 吊销立即断开 → 配额 → 指令重放 / 错误签名被拒 → 用量上报 → 编排服务不可用时降级。
/// </summary>
public sealed class CloudAgentTests : IAsyncLifetime
{
    private readonly CloudRig _rig = new();
    private readonly PeerIdentity _phone = PeerIdentity.Create("phone");

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private FakeOrchestrator Orch => _rig.Orchestrator;

    private async Task<(DeviceClient Client, string Token)> StartAndConnectAsync(CloudConfig? config = null)
    {
        await _rig.StartAgentAsync(config);
        var (token, seq) = Orch.Grant(_phone);
        Assert.Equal(AckStatuses.Applied, (await _rig.GrantAppliedAsync(seq)).Status);
        try
        {
            return (await _rig.ConnectAsync(_phone, token), token);
        }
        catch (RelayException e)
        {
            // 失败消息里带上中继给的错误码和它当时的状态:「中继拒绝了连接」本身分不出是路由令牌还没同步到中继,还是别的原因
            throw new InvalidOperationException(
                $"The relay refused the controller right after the trust was acknowledged: code={e.Code}, tokensAtRelay={_rig.Relay.TokenCount(_rig.Agent.DeviceId)}, deviceOnline={_rig.Relay.IsOnline(_rig.Agent.DeviceId)}", e);
        }
    }

    private async Task<string> WaitForAssistantAsync(DeviceClient client, string session, string contains, int count = 1)
    {
        string[] texts = [];
        await CloudRig.WaitUntil(async () =>
        {
            texts = await _rig.AssistantTextsAsync(client, session);
            return texts.Count(t => t.Contains(contains, StringComparison.Ordinal)) >= count;
        }, $"assistant text containing '{contains}'");
        return texts.Last(t => t.Contains(contains, StringComparison.Ordinal));
    }

    private async Task<CloudStatus> StatusAsync(DeviceClient client) => await client.CallAsync(CloudMethods.GetStatus, Unit.Value);

    private static async Task<bool> CallsStillWork(DeviceClient client)
    {
        try
        {
            await client.CallAsync(DeviceMethods.Info, Unit.Value);
            return true;
        }
        catch (Exception e) when (e is ProtocolException or IOException)
        {
            return false;
        }
    }

    // ---- 启动、身份、信任、对话 -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_agent_registers_its_identity_with_the_orchestrator_and_serves_a_controller_only_through_the_relay()
    {
        var host = await _rig.StartAgentAsync();

        // 身份注册:设备的「持有私钥证明」验证通过,编排服务回了签名的身份绑定,Agent 验过签并记住了账户
        var hello = Assert.Single(Orch.Hellos);
        Assert.Equal(host.DeviceId, hello.DeviceId);
        Assert.False(hello.Resumed);
        Assert.Equal(Convert.ToHexStringLower(host.Fingerprint), Orch.Fingerprint);
        Assert.Null(host.Remote.Port);           // 云端没有局域网监听
        Assert.Null(host.Remote.PairingCode);    // 也没有配对码
        Assert.True(Relay_IsOnline(host));

        var (token, seq) = Orch.Grant(_phone);
        Assert.Equal(AckStatuses.Applied, (await _rig.GrantAppliedAsync(seq)).Status);
        var client = await _rig.ConnectAsync(_phone, token);

        Assert.Equal("owner", client.Welcome.Role);
        Assert.Contains(Capabilities.Cloud, client.Welcome.Features);
        Assert.Equal("cloud", (await client.CallAsync(DeviceMethods.Info, Unit.Value)).Kind);
        var status = await StatusAsync(client);
        Assert.Equal("acct-1", status.AccountId);
        Assert.Equal(CloudStates.Awake, status.State);
        Assert.False(status.ResumedFromSleep);

        // 配对码 / 二维码流程在云端不存在(设备身份由账户签发,信任记录由编排服务下发)
        var pair = await CloudRig.ErrorOf(() => client.CallAsync(DeviceMethods.PairBegin, new PairBeginParams("evil phone")));
        Assert.Equal(ErrorCodes.Unsupported, pair.Code);
        Assert.Equal(CloudErrorKeys.PairingUnsupported, CloudRig.ErrorKey(pair));
    }

    private bool Relay_IsOnline(AgentHost host) => _rig.Relay.IsOnline(host.DeviceId);

    [Fact]
    public async Task A_controller_converses_with_the_cloud_agent_through_the_relay_against_a_fake_upstream()
    {
        var (client, _) = await StartAndConnectAsync();
        var project = await CloudRig.NewProjectAsync(client, "demo", _rig.Workspaces);
        var session = await _rig.NewSessionAsync(client, project);
        _rig.Llm.Then(FakeLlm.Say("Hello from the cloud agent."));

        var started = await _rig.StartRunAsync(client, session, "hi");

        Assert.Equal(StartOutcomes.Started, started.Outcome);
        await WaitForAssistantAsync(client, session, "Hello from the cloud agent.");
        var status = await StatusAsync(client);
        Assert.Equal(1, status.Usage.RunsToday);
        Assert.Equal(1, status.Usage.RunsThisMonth);
        Assert.Null(status.ExceededKey);
    }

    [Fact]
    public async Task The_master_key_is_wrapped_by_the_kms_and_no_secret_lands_on_disk_in_the_clear()
    {
        var (client, _) = await StartAndConnectAsync();

        await client.CallAsync(GitCredentialsMethods.Set, new GitCredentialSetParams("GitHub.com", "ghp_VERY_SECRET_TOKEN"), idem: "gc-1");
        var list = await client.CallAsync(GitCredentialsMethods.List, Unit.Value);

        var entry = Assert.Single(list.Credentials);
        Assert.Equal(("github.com", "x-access-token"), (entry.Host, entry.Username));
        Assert.False(File.Exists(Path.Combine(_rig.DataRoot, "credential.key")));          // 没有明文主密钥
        Assert.True(File.Exists(Path.Combine(_rig.DataRoot, "credential.key.kms")));       // 只有 KMS 包裹后的
        Assert.Equal(1, _rig.Kms.Wraps);
        var needle = System.Text.Encoding.UTF8.GetBytes("ghp_VERY_SECRET_TOKEN");
        foreach (var file in Directory.EnumerateFiles(_rig.Root, "*", SearchOption.AllDirectories))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes);
            Assert.False(bytes.AsSpan().IndexOf(needle) >= 0, $"the token must not appear in {Path.GetFileName(file)}");
        }

        // 令牌也永远不会经协议回到控制端:清单里只有主机和用户名
        Assert.DoesNotContain("ghp_", string.Join("|", list.Credentials.Select(c => c.Host + c.Username)));
    }

    // ---- 克隆(Git 接力)与出网策略 -------------------------------------------------------------------------------

    [Fact]
    public async Task Cloning_validates_the_address_confines_the_target_and_consults_the_egress_policy()
    {
        var config = _rig.Config(egress: new CloudEgressFile(["github.com"], ["gitRemote"]));
        var (client, _) = await StartAndConnectAsync(config);

        var badUrl = await CloudRig.ErrorOf(() => client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams("repo", null, CloneUrl: "file:///etc"), idem: "c1"));
        Assert.Equal(ErrorCodes.InvalidRequest, badUrl.Code);
        Assert.Equal("projects.clone.url", CloudRig.ErrorKey(badUrl));

        var denied = await CloudRig.ErrorOf(() => client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams("repo", null, CloneUrl: "https://evil.example.com/a/b.git"), idem: "c2"));
        Assert.Equal(ErrorCodes.Forbidden, denied.Code);
        Assert.Equal(CloudErrorKeys.EgressDenied, CloudRig.ErrorKey(denied));
        Assert.Equal("evil.example.com", CloudRig.ErrorParams(denied)["host"]);

        var outside = await CloudRig.ErrorOf(() => client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(Path.Combine(_rig.Root, "elsewhere"), null, CloneUrl: "https://github.com/a/b.git"), idem: "c3"));
        Assert.Equal("projects.clone.target", CloudRig.ErrorKey(outside)); // 不能克隆到工作区根之外(例如数据根)
        Assert.False(Directory.Exists(Path.Combine(_rig.Root, "elsewhere")));

        var ssh = await CloudRig.ErrorOf(() => client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams("repo", null, EnvironmentId: "some-ssh-env", CloneUrl: "https://github.com/a/b.git"), idem: "c4"));
        Assert.Equal("projects.clone.environment", CloudRig.ErrorKey(ssh));
    }

    [Fact]
    public async Task A_clone_that_fails_in_git_is_a_structured_error_and_leaves_no_project_or_directory()
    {
        var (client, _) = await StartAndConnectAsync();

        var error = await CloudRig.ErrorOf(() => client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams("never", null, CloneUrl: "https://127.0.0.1:1/nope.git"), idem: "c5"));

        Assert.Equal(ErrorCodes.Conflict, error.Code);
        Assert.Equal(GitErrorKeys.Failed, FrameCodec.DecodePayload(error.Details!, PayloadShapes.GetShape<GitErrorDto>()).Key);
        Assert.False(Directory.Exists(Path.Combine(_rig.Workspaces, "never")));
        Assert.Empty((await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects);
    }

    // ---- 休眠与唤醒 ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_idle_agent_checkpoints_leaves_the_relay_notifies_the_orchestrator_and_exits_gracefully()
    {
        var (client, _) = await StartAndConnectAsync();
        var project = await CloudRig.NewProjectAsync(client, "demo", _rig.Workspaces);
        var session = await _rig.NewSessionAsync(client, project);
        _rig.Llm.Then(FakeLlm.Say("done"));
        await _rig.StartRunAsync(client, session, "work");
        await WaitForAssistantAsync(client, session, "done");
        var deviceId = _rig.Agent.DeviceId;

        // 「通知」到达的瞬间:设备端的中继注册必须已经停掉(中继服务器随后才察觉连接断了,所以服务器一侧只能最终为离线)、WAL 已经检查点
        bool? registeredAtNotice = null;
        long? walAtNotice = null;
        Orch.OnNotice = notice =>
        {
            if (notice.State == LifecycleStates.Sleeping)
            {
                registeredAtNotice = _rig.Agent.Remote.RelayStatus is not null;
                var wal = Path.Combine(_rig.DataRoot, "nextcowork.db-wal");
                walAtNotice = File.Exists(wal) ? new FileInfo(wal).Length : 0;
            }
        };

        // 控制端还连着:再久也不休眠
        _rig.Clock.Advance(TimeSpan.FromMinutes(40));
        await Task.Delay(200);
        Assert.False(_rig.Run.IsCompleted);
        Assert.DoesNotContain(Orch.Notices, n => n.State == LifecycleStates.Sleeping);

        // 控制端断开:空闲不满 N 分钟不休眠,满了才休眠
        await client.DisposeAsync();
        await CloudRig.WaitUntil(() => _rig.Agent.Remote.ConnectedSessions == 0, "controller disconnected");
        _rig.Clock.Advance(TimeSpan.FromMinutes(14));
        await Task.Delay(200);
        Assert.False(_rig.Run.IsCompleted);
        _rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await _rig.WaitForExitAsync();

        Assert.Equal("sleep", _rig.Agent.CloudExit!.Reason);
        var notice = Assert.Single(Orch.Notices, n => n.State == LifecycleStates.Sleeping);
        Assert.Equal("idle", notice.Reason);
        Assert.Equal(new SleepSummary(0, 0, 0), notice.Summary);
        Assert.False(registeredAtNotice, "the device must have deregistered from the relay before the orchestrator is told it may stop the sandbox");
        Assert.Equal(0, walAtNotice);
        await CloudRig.WaitUntil(() => !_rig.Relay.IsOnline(deviceId), "the relay notices the device is gone");

        // 休眠快照在数据库里(唤醒时读它)
        using var db = AgentDatabase.Open(_rig.DataRoot);
        Assert.Contains("\"reason\":\"idle\"", new KvStore(db).Get("v3.cloud.sleep"));
    }

    [Fact]
    public async Task A_woken_agent_serves_the_first_controller_with_the_old_credentials_and_nothing_is_lost()
    {
        var (client, token) = await StartAndConnectAsync();
        var project = await CloudRig.NewProjectAsync(client, "demo", _rig.Workspaces);
        var session = await _rig.NewSessionAsync(client, project);
        _rig.Llm.Then(FakeLlm.Say("remember this answer"));
        await _rig.StartRunAsync(client, session, "question");
        await WaitForAssistantAsync(client, session, "remember this answer");
        var deviceId = _rig.Agent.DeviceId;
        var fingerprint = _rig.Agent.Fingerprint;
        await client.DisposeAsync();
        Orch.Notices.Clear();
        Orch.RequestSleep(SleepModes.WhenIdle);
        await _rig.WaitForExitAsync();

        // 唤醒:同一个数据根、同一个 KMS、同一个编排服务;控制端不需要重新注册(信任记录和路由令牌哈希都在数据库里)
        await _rig.StartAgentAsync();
        var woken = await _rig.ConnectAsync(_phone, token);

        Assert.Equal(deviceId, _rig.Agent.DeviceId);
        Assert.Equal(fingerprint, _rig.Agent.Fingerprint);
        Assert.True(Orch.Hellos[^1].Resumed);
        var status = await StatusAsync(woken);
        Assert.True(status.ResumedFromSleep);
        Assert.NotNull(status.WokeAt);
        Assert.Contains("remember this answer", (await _rig.AssistantTextsAsync(woken, session)).Single());
        Assert.True(_rig.Kms.Unwraps >= 1); // 唤醒时向 KMS 解开了主密钥
        Assert.Contains(Orch.Notices, n => n.State == LifecycleStates.Awake && n.Reason == "wake");
    }

    [Fact]
    public async Task A_forced_sleep_interrupts_the_run_keeps_the_queued_message_and_the_wake_up_sends_it_while_the_schedule_survives()
    {
        var (client, token) = await StartAndConnectAsync();
        var project = await CloudRig.NewProjectAsync(client, "demo", _rig.Workspaces);
        var session = await _rig.NewSessionAsync(client, project);
        var never = new TaskCompletionSource();
        _rig.Llm.ThenGated(never.Task, "first run never finishes").Then(FakeLlm.Say("answer after wake-up"));
        var task = await client.CallAsync(
            SchedulesMethods.Create,
            new ScheduledTaskInputDto("nightly", "do the nightly thing", project, "sonnet", new ScheduleRuleDto("daily", Time: "03:00"), Timezone: "UTC"), idem: "sched-1");

        var started = await _rig.StartRunAsync(client, session, "long job");
        await CloudRig.WaitUntil(() => _rig.Llm.Requests == 1, "the first run reached the upstream");
        var queued = await client.CallAsync(
            ConversationMethods.QueueEnqueue,
            new QueueEnqueueParams(session, "queued-msg-1", [new InputPartDto(InputPartKinds.Text, "and then this")], new SendOptionsDto("sonnet", PermissionMode: "auto")), idem: "q-1");
        Assert.True(queued.Accepted);

        var seq = Orch.RequestSleep(SleepModes.Now, "maintenance");
        await _rig.WaitForExitAsync();

        Assert.Equal(AckStatuses.Applied, (await _rig.AckedAsync(seq)).Status);
        var notice = Assert.Single(Orch.Notices, n => n.State == LifecycleStates.Sleeping);
        Assert.Equal(new SleepSummary(InterruptedRuns: 1, KilledShells: 0, QueuedMessages: 1), notice.Summary);

        // 唤醒:排队的消息接着发给上游,被打断的 Run 列在状态里(不自动重跑),定时任务还在
        await _rig.StartAgentAsync();
        var woken = await _rig.ConnectAsync(_phone, token);
        await WaitForAssistantAsync(woken, session, "answer after wake-up");

        var status = await StatusAsync(woken);
        var interrupted = Assert.Single(status.InterruptedRuns);
        Assert.Equal((started.RunId, session), (interrupted.RunId, interrupted.SessionId));
        var tasks = (await woken.CallAsync(SchedulesMethods.ListTasks, new ScheduleListParams())).Tasks;
        var restored = Assert.Single(tasks);
        Assert.Equal((task.Id, true), (restored.Id, restored.Enabled));
        Assert.True(restored.NextRunAt > 0);

        await woken.CallAsync(CloudMethods.ClearInterrupted, new CloudClearInterruptedParams([interrupted.RunId]), idem: "clr-1");
        Assert.Empty((await StatusAsync(woken)).InterruptedRuns);
    }

    [Fact]
    public async Task A_whenIdle_sleep_request_is_refused_as_busy_while_a_run_or_a_controller_is_active_and_does_not_consume_its_sequence_number()
    {
        var (client, _) = await StartAndConnectAsync();

        var seq = Orch.RequestSleep(SleepModes.WhenIdle);
        var busy = await _rig.AckedAsync(seq);

        Assert.Equal(AckStatuses.Busy, busy.Status); // 控制端连着
        Assert.False(_rig.Run.IsCompleted);

        await client.DisposeAsync(); // 控制端走了:同一条指令重发,现在可以了
        await CloudRig.WaitUntil(() => _rig.Agent.Remote.ConnectedSessions == 0, "controller disconnected");
        await _rig.WaitForExitAsync();
        Assert.Equal("sleep", _rig.Agent.CloudExit!.Reason);
        Assert.Equal(AckStatuses.Applied, Orch.Acks.Last(a => a.Seq == seq).Status);
    }

    [Fact]
    public async Task An_upgrade_request_waits_for_no_runs_then_persists_and_exits_with_the_upgrade_reason()
    {
        var (client, _) = await StartAndConnectAsync(); // 控制端连着也可以升级:只有运行中的 Run 才挡升级

        var seq = Orch.Issue(CommandTypes.UpgradeRequest, b => b with { Upgrade = new UpgradeRequest("3.0.1", UpgradeModes.WhenIdle) }).Body.Seq;
        await _rig.WaitForExitAsync();

        Assert.Equal("upgrade", _rig.Agent.CloudExit!.Reason);
        Assert.Equal(AckStatuses.Applied, (await _rig.AckedAsync(seq)).Status);
        Assert.Contains(Orch.Notices, n => n.State == LifecycleStates.Upgrading && n.Reason == "upgrade:3.0.1");
        Assert.False(await CallsStillWork(client));
    }

    // ---- 吊销 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_revocation_from_the_orchestrator_disconnects_the_controller_at_once_and_invalidates_its_relay_token()
    {
        var (client, token) = await StartAndConnectAsync();
        Assert.True(await CallsStillWork(client));
        Assert.Equal(1, _rig.Relay.TokenCount(_rig.Agent.DeviceId));

        var seq = Orch.Revoke(_phone);

        Assert.Equal(AckStatuses.Applied, (await _rig.AckedAsync(seq)).Status);
        await CloudRig.WaitUntil(() => client.Completion.IsCompleted, "the live session is dropped");
        Assert.False(await CallsStillWork(client));
        await CloudRig.WaitUntil(() => _rig.Relay.TokenCount(_rig.Agent.DeviceId) == 0, "the route token hash is withdrawn from the relay");
        var reconnect = await Assert.ThrowsAnyAsync<Exception>(() => _rig.ConnectAsync(_phone, token));
        Assert.True(reconnect is RelayException { Code: RelayErrors.Unauthorized }, reconnect.ToString());
    }

    [Fact]
    public async Task A_revocation_that_arrives_while_the_agent_is_asleep_takes_effect_before_the_device_is_reachable_again()
    {
        var (client, token) = await StartAndConnectAsync();
        var deviceId = _rig.Agent.DeviceId;
        var fingerprint = _rig.Agent.Fingerprint;
        await client.DisposeAsync();
        Orch.RequestSleep(SleepModes.Now);
        await _rig.WaitForExitAsync();
        Orch.Revoke(_phone); // 休眠期间到达:没有 Agent 在轮询,指令留在编排服务里

        // 攻击者(被吊销的控制端)在设备一上线就拼命连。唤醒流程必须先处理积压的指令、再向中继注册。
        var attempts = 0;
        var successes = 0;
        using var stop = new CancellationTokenSource();
        var hammer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (_rig.Relay.IsOnline(deviceId))
                {
                    attempts++;
                    try
                    {
                        var connection = await RemoteDevices.ConnectAsync(deviceId, new DeviceAddresses([], new RelayRoute(_rig.Relay.WebSocketUrl, token)), _phone, fingerprint);
                        await connection.Stream.DisposeAsync();
                        successes++;
                    }
                    catch (Exception e) when (e is RelayException or SecureChannelException or IOException or ProtocolException)
                    {
                    }
                }

                await Task.Delay(2);
            }
        });

        await _rig.StartAgentAsync();
        await Task.Delay(300);
        await stop.CancelAsync();
        await hammer;

        Assert.True(attempts > 0, "the relay must have seen the device online");
        Assert.Equal(0, successes);
        Assert.Empty(_rig.Agent.Remote.State().Controllers);
        Assert.Equal(0, _rig.Relay.TokenCount(deviceId));
    }

    // ---- 配额 -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_over_the_daily_quota_is_rejected_with_quota_exceeded_and_the_window_resets_the_next_utc_day()
    {
        var (client, _) = await StartAndConnectAsync();
        var project = await CloudRig.NewProjectAsync(client, "demo", _rig.Workspaces);
        var session = await _rig.NewSessionAsync(client, project);
        _rig.Llm.Then(FakeLlm.Say("first")).Then(FakeLlm.Say("after the reset"));
        var seq = Orch.SetQuota(new QuotaLimits(DailyRuns: 1));
        Assert.Equal(AckStatuses.Applied, (await _rig.AckedAsync(seq)).Status);

        await _rig.StartRunAsync(client, session, "one");
        await WaitForAssistantAsync(client, session, "first");
        var rejected = await CloudRig.ErrorOf(() => _rig.StartRunAsync(client, session, "two"));
        var queueRejected = await CloudRig.ErrorOf(() => client.CallAsync(
            ConversationMethods.QueueEnqueue, new QueueEnqueueParams(session, "q-over", [new InputPartDto(InputPartKinds.Text, "x")], new SendOptionsDto("sonnet")), idem: "q-over"));

        Assert.Equal(ErrorCodes.RateLimited, rejected.Code);
        Assert.False(rejected.Retryable);
        Assert.Equal(CloudErrorKeys.QuotaExceeded, CloudRig.ErrorKey(rejected));
        var parameters = CloudRig.ErrorParams(rejected);
        Assert.Equal(("dailyRuns", "1", "1"), (parameters["window"], parameters["limit"], parameters["used"]));
        Assert.Equal(CloudErrorKeys.QuotaExceeded, CloudRig.ErrorKey(queueRejected));
        Assert.Equal(1, _rig.Llm.Requests); // 被拒绝的 Run 没有碰上游
        var status = await StatusAsync(client);
        Assert.Equal((CloudErrorKeys.QuotaExceeded, "dailyRuns"), (status.ExceededKey, status.ExceededWindow));

        _rig.Clock.Advance(TimeSpan.FromHours(25)); // 过了 UTC 午夜
        await _rig.StartRunAsync(client, session, "three");
        await WaitForAssistantAsync(client, session, "after the reset");
    }

    [Fact]
    public async Task The_concurrent_run_limit_and_a_frozen_account_reject_new_runs_and_the_limits_survive_a_sleep()
    {
        var (client, token) = await StartAndConnectAsync();
        var project = await CloudRig.NewProjectAsync(client, "demo", _rig.Workspaces);
        var a = await _rig.NewSessionAsync(client, project);
        var b = await _rig.NewSessionAsync(client, project);
        var hold = new TaskCompletionSource();
        _rig.Llm.ThenGated(hold.Task, "slow finished");
        Assert.Equal(AckStatuses.Applied, (await _rig.AckedAsync(Orch.SetQuota(new QuotaLimits(MaxConcurrentRuns: 1)))).Status);

        await _rig.StartRunAsync(client, a, "slow");
        await CloudRig.WaitUntil(() => _rig.Llm.Requests == 1, "slow run started");
        var second = await CloudRig.ErrorOf(() => _rig.StartRunAsync(client, b, "parallel"));
        Assert.Equal("concurrentRuns", CloudRig.ErrorParams(second)["window"]);
        hold.SetResult();
        await WaitForAssistantAsync(client, a, "slow finished");

        var frozen = Orch.SetQuota(new QuotaLimits(Frozen: true));
        await _rig.AckedAsync(frozen);
        var refused = await CloudRig.ErrorOf(() => _rig.StartRunAsync(client, b, "while frozen"));
        Assert.Equal("frozen", CloudRig.ErrorParams(refused)["window"]);

        // 限额存在数据库里:休眠唤醒之后仍然有效
        await client.DisposeAsync();
        Orch.RequestSleep(SleepModes.Now);
        await _rig.WaitForExitAsync();
        await _rig.StartAgentAsync();
        var woken = await _rig.ConnectAsync(_phone, token);
        Assert.True((await StatusAsync(woken)).Quota.Frozen);
        Assert.Equal("frozen", CloudRig.ErrorParams(await CloudRig.ErrorOf(() => _rig.StartRunAsync(woken, b, "still frozen")))["window"]);
    }

    // ---- 指令的验签与防重放 --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_replayed_trust_command_does_not_resurrect_a_revoked_controller()
    {
        await _rig.StartAgentAsync();
        var (token, grantSeq) = Orch.Grant(_phone);
        await _rig.GrantAppliedAsync(grantSeq);
        var original = Orch.Issued[^1];
        var client = await _rig.ConnectAsync(_phone, token);
        await _rig.AckedAsync(Orch.Revoke(_phone));
        await CloudRig.WaitUntil(() => client.Completion.IsCompleted, "revoked");

        Orch.Replay(original); // 攻击者重放当初合法的「下发信任」
        await CloudRig.WaitUntil(() => Orch.Acks.Count(a => a.Seq == grantSeq) >= 2, "the replay is answered");

        Assert.Equal(AckStatuses.Duplicate, Orch.Acks.Last(a => a.Seq == grantSeq).Status);
        Assert.Empty(_rig.Agent.Remote.State().Controllers);
        await Assert.ThrowsAnyAsync<Exception>(() => _rig.ConnectAsync(_phone, token));
    }

    [Fact]
    public async Task Forged_misaddressed_expired_and_tampered_commands_are_rejected_and_do_not_burn_sequence_numbers()
    {
        await _rig.StartAgentAsync();
        var deviceId = _rig.Agent.DeviceId;
        var attackerEntry = new TrustEntry(_phone.FingerprintHex, "attacker", "owner", RelayProtocol.HashToken("attacker-token"));
        var seq = Orch.NextSeq;

        // 1. 别的密钥签名(同一个 kid)
        var imposter = new Issuer(_rig.Issuer.Kid);
        Orch.Inject(imposter.Signer.Sign(imposter.Body(CommandTypes.TrustPut, deviceId, seq + 1000, _rig.Clock.GetUtcNow()) with { Trust = attackerEntry }));
        // 2. 合法签名,但发给别的设备
        Orch.Inject(_rig.Issuer.Signer.Sign(_rig.Issuer.Body(CommandTypes.TrustPut, "dev_zzzzzzzzzzzzzzzzzzzzzzzzzz", seq + 1, _rig.Clock.GetUtcNow()) with { Trust = attackerEntry }));
        // 3. 合法签名,但已经过期
        Orch.Inject(_rig.Issuer.Signer.Sign(_rig.Issuer.Body(CommandTypes.TrustPut, deviceId, seq + 2, _rig.Clock.GetUtcNow() - TimeSpan.FromHours(2), TimeSpan.FromMinutes(10)) with { Trust = attackerEntry }));
        // 4. 合法签名之后被篡改(把配额改成无限)
        var legit = _rig.Issuer.Signer.Sign(_rig.Issuer.Body(CommandTypes.QuotaSet, deviceId, seq + 3, _rig.Clock.GetUtcNow()) with { Quota = new QuotaLimits(DailyRuns: 0) });
        var bytes = Convert.FromBase64String(legit.Payload);
        var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("\"dailyRuns\":0", "\"dailyRuns\":9", StringComparison.Ordinal);
        Orch.Inject(legit with { Payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)) });

        // 被拒绝的伪造指令(含高序号)没有消耗序号:紧接着的合法指令(序号比伪造的低)仍然被接受。它和上面四条在同一批里处理。
        var (token, grantSeq) = Orch.Grant(_phone);
        Assert.Equal(AckStatuses.Applied, (await _rig.GrantAppliedAsync(grantSeq)).Status);

        // 验签通过但内容不被接受的(发给别人的、过期的)有带原因的回执;签名都不对的(伪造的、篡改的)不回执——没有可信的东西可以回
        Assert.Equal(RejectCodes.WrongDevice, Orch.AckFor(seq + 1)!.Code);
        Assert.Equal(RejectCodes.Expired, Orch.AckFor(seq + 2)!.Code);
        Assert.All(new[] { seq + 1, seq + 2 }, s => Assert.Equal(AckStatuses.Rejected, Orch.AckFor(s)!.Status));
        Assert.Null(Orch.AckFor(seq + 3));
        Assert.Null(Orch.AckFor(seq + 1000));
        var only = Assert.Single(_rig.Agent.Remote.State().Controllers);
        Assert.Equal(_phone.FingerprintHex, only.Fingerprint); // 只有合法下发的那一个,没有 attacker
        Assert.True(await CallsStillWork(await _rig.ConnectAsync(_phone, token)));
        Assert.Equal(0, (await StatusAsync(await _rig.ConnectAsync(_phone, token))).Quota.DailyRuns ?? 0); // 篡改的配额(dailyRuns:9)没有生效
    }

    // ---- 用量上报 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Usage_is_reported_in_idempotent_batches_retried_after_a_failure_and_flushed_once_more_before_sleeping()
    {
        var (client, _) = await StartAndConnectAsync();
        var project = await CloudRig.NewProjectAsync(client, "demo", _rig.Workspaces);
        var session = await _rig.NewSessionAsync(client, project);
        _rig.Llm.Then(FakeLlm.Say("one")).Then(FakeLlm.Say("two"));
        await _rig.StartRunAsync(client, session, "first");
        await WaitForAssistantAsync(client, session, "one");

        Orch.FailUsage = 1;
        _rig.Clock.Advance(TimeSpan.FromSeconds(61)); // 第一次上报失败(503)
        await CloudRig.WaitUntil(() => Orch.UsageAttempts == 1, "the first attempt");
        Assert.Empty(Orch.UsageBatches);
        _rig.Clock.Advance(TimeSpan.FromSeconds(60)); // 重试:同一个 batchId(先补发旧的,再发新增量)
        await CloudRig.WaitUntil(() => Orch.UsageBatches.Count >= 1, "the retried batch arrives");

        var first = Orch.UsageBatches[0];
        Assert.Equal($"{_rig.Agent.DeviceId}:1", first.BatchId);
        Assert.Equal(1, first.Runs);
        Assert.True(first.ComputeSeconds > 0);

        await _rig.StartRunAsync(client, session, "second");
        await WaitForAssistantAsync(client, session, "two");
        await client.DisposeAsync();
        Orch.RequestSleep(SleepModes.Now);
        await _rig.WaitForExitAsync();

        // 休眠前的最后一次上报在「休眠通知」之前;所有批次的序号连续、batchId 不重复、Run 数加起来恰好是 2(没有丢、没有重复)
        var batches = Orch.UsageBatches;
        Assert.Equal(Enumerable.Range(1, batches.Count).Select(i => (long)i), batches.Select(b => b.Seq));
        Assert.Equal(batches.Count, batches.Select(b => b.BatchId).Distinct().Count());
        Assert.Equal(2, batches.Sum(b => b.Runs));
        Assert.True(batches[^1].Runs == 1 || batches.Count(b => b.Runs == 1) == 2);
    }

    // ---- 降级与身份 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task With_the_orchestrator_down_the_agent_still_serves_known_controllers_and_catches_up_when_it_returns()
    {
        var (client, token) = await StartAndConnectAsync();
        await client.DisposeAsync();
        Orch.RequestSleep(SleepModes.Now);
        await _rig.WaitForExitAsync();

        Orch.Offline = true;
        await _rig.StartAgentAsync(firstHelloDeadline: TimeSpan.FromMilliseconds(300));
        var degraded = await _rig.ConnectAsync(_phone, token); // 带着数据库里的信任记录照常服务
        Assert.True(await CallsStillWork(degraded));

        Orch.Offline = false;
        var seq = Orch.Revoke(_phone); // 编排服务回来之后,积压的指令照常生效
        await CloudRig.WaitUntil(() => degraded.Completion.IsCompleted, "the revocation is applied after the orchestrator returns");
        Assert.Equal(AckStatuses.Applied, (await _rig.AckedAsync(seq)).Status);
    }

    [Fact]
    public async Task A_data_root_that_belongs_to_another_device_refuses_to_start()
    {
        var host = await _rig.StartAgentAsync();
        Orch.Notices.Clear();
        Orch.RequestSleep(SleepModes.Now);
        await _rig.WaitForExitAsync();

        var error = await Assert.ThrowsAsync<CloudStartupException>(() => _rig.StartAgentAsync(_rig.Config(expectedDeviceId: "dev_zzzzzzzzzzzzzzzzzzzzzzzzzz")));

        Assert.Equal((CloudStartupCodes.IdentityMismatch, CloudExitCodes.Identity), (error.Code, error.ExitCode));
        Assert.NotEqual("dev_zzzzzzzzzzzzzzzzzzzzzzzzzz", host.DeviceId);
    }

    [Fact]
    public async Task An_identity_binding_that_does_not_match_the_device_stops_the_agent_with_a_structured_failure()
    {
        Orch.CorruptBinding = true;

        var error = await Assert.ThrowsAsync<CloudStartupException>(() => _rig.StartAgentAsync(waitForRelay: false));

        Assert.Equal((CloudStartupCodes.BindingMismatch, CloudExitCodes.Identity), (error.Code, error.ExitCode));
    }

    [Fact]
    public async Task Account_token_rotation_arrives_as_a_signed_command_and_the_local_channel_does_not_exist()
    {
        var host = await _rig.StartAgentAsync();
        var grant = new AccountTokenGrant("rotated-token-2", DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds(), "acct-1", null);

        var seq = Orch.Issue(CommandTypes.AccountToken, b => b with { Account = grant }).Body.Seq;

        Assert.Equal(AckStatuses.Applied, (await _rig.AckedAsync(seq)).Status);
        Assert.NotNull(host.DeviceId);
        // 云端没有本机通道:数据根里没有 socket / 本机令牌文件
        Assert.False(File.Exists(Path.Combine(_rig.DataRoot, "run", "agent.sock")));
        Assert.Null(host.Remote.Port);
    }
}
