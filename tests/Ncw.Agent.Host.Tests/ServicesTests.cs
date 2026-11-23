using Ncw.Agent.Host.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 设置 / 定时任务经协议的端到端(docs/design/07 §3、08/05 §8、09 §8.4):真实 Agent + 真实 DeviceClient + 假上游 + 手动推进的假时钟。
/// 覆盖:设置写入后<b>下一个 Run</b>就生效(请求里的最大输出和系统提示词)并经主题广播、坏补丁整个不生效且带稳定 key、项目设置、
/// 定时任务到点经注册表以 <c>RunOrigin.Scheduled</c> 发起 Run(会话 <c>origin = scheduled</c>、档位 <c>full</c>)并落运行记录、<c>schedules</c> 主题的全量权威、
/// 角色与承载(定时任务的写入只有 owner,代理密码只有本机通道,viewer 读不了设置)。
/// </summary>
public sealed class ServicesTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private readonly ManualClock _clock = new(Origin);
    private readonly ConversationRig _rig;
    private DeviceClient _owner = null!;
    private ProjectDto _project = null!;

    public ServicesTests()
    {
        _rig = new ConversationRig(clock: _clock);
    }

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _project = await _rig.AddProjectAsync(_owner); // 等数据就绪
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private async Task RunOnceAsync(string text = "hi")
    {
        var session = await _rig.CreateSessionAsync(_owner, _project);
        var start = await _rig.StartAsync(_owner, session.Id, text);
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();
    }

    private static async Task<ProtocolException> Failure<T>(Func<Task<T>> call) => await Assert.ThrowsAsync<ProtocolException>(call);

    // ───────────────────────── 设置:写入后下一个 Run 生效 ─────────────────────────

    [Fact]
    public async Task A_settings_write_takes_effect_on_the_next_run_and_is_broadcast_with_a_monotonic_revision()
    {
        var topic = await Watch<DeviceSettingsState>.OpenAsync(_owner, SettingsMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "settings snapshot");
        var first = topic.Snapshot;
        Assert.Equal(("ask", 32_000, true), (first.Settings.DefaultPermissionMode, first.Settings.MaxOutputTokens, first.Settings.AutoCompact)); // 出厂值

        var updated = await _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(
            MaxOutputTokens: 8000, Personalization: new PersonalizationPatchDto(Name: "Ada", Instructions: "ALWAYS ANSWER IN HAIKU"))));
        Assert.True(updated.Rev > first.Rev);
        Assert.Equal((8000, "Ada"), (updated.Settings.MaxOutputTokens, updated.Settings.Personalization.Name));
        await topic.WaitAsync(() => topic.Decoded<DeviceSettingsState>(SettingsEvents.Changed).Any(s => s.Settings.MaxOutputTokens == 8000), "settings.changed");

        _rig.Llm.Then(Up.Say("ok"));
        await RunOnceAsync();
        var request = Assert.Single(_rig.Llm.Requests);
        Assert.Equal(8000, request.Options.MaxOutputTokens);                       // 最大输出 Token 对这个 Run 生效
        Assert.Contains("ALWAYS ANSWER IN HAIKU", request.System);                 // 个性化进了系统提示词

        // 再改回去:下一个 Run 又是新的值(没有任何缓存挡在中间)
        await _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(MaxOutputTokens: 4096, Personalization: new PersonalizationPatchDto(Instructions: ""))));
        _rig.Llm.Then(Up.Say("ok again"));
        await RunOnceAsync("again");
        Assert.Equal(4096, _rig.Llm.Requests[1].Options.MaxOutputTokens);
        Assert.DoesNotContain("ALWAYS ANSWER IN HAIKU", _rig.Llm.Requests[1].System);
    }

    [Fact]
    public async Task Media_settings_round_trip_through_the_update_result_query_and_subscription()
    {
        Assert.Contains(Capabilities.SettingsMedia, _owner.Welcome.Features);
        var topic = await Watch<DeviceSettingsState>.OpenAsync(_owner, SettingsMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "settings snapshot");
        Assert.Null(topic.Snapshot.Settings.Video);
        Assert.Null(topic.Snapshot.Settings.Audio);
        var video = new ModelPickDto("video", "video-provider");
        var audio = new ModelPickDto("audio", "audio-provider");

        var updated = await _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(Video: video, Audio: audio)));
        await topic.WaitAsync(() => topic.Decoded<DeviceSettingsState>(SettingsEvents.Changed).Contains(updated), "settings.changed");
        var read = await _owner.CallAsync(SettingsMethods.Get, Unit.Value);

        Assert.Equal((video, audio), (updated.Settings.Video, updated.Settings.Audio));
        Assert.Equal(updated, read);
        var cleared = await _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(Video: ModelPickDto.None)));
        Assert.Null(cleared.Settings.Video);
        Assert.Equal(audio, cleared.Settings.Audio);
    }

    [Fact]
    public async Task A_bad_patch_is_invalid_request_with_a_stable_key_and_nothing_is_written()
    {
        var before = await _owner.CallAsync(SettingsMethods.Get, Unit.Value);

        var error = await Failure(() => _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(PromptSuggestions: false, MaxOutputTokens: 100))));

        Assert.Equal(ErrorCodes.InvalidRequest, error.Code);
        var details = FrameCodec.DecodePayload(error.Details!, PayloadShapes.GetShape<ProviderErrorDto>());
        Assert.Equal(("settings.invalid.range", "maxOutputTokens"), (details.Key, details.Params["field"]));
        var after = await _owner.CallAsync(SettingsMethods.Get, Unit.Value);
        Assert.Equal(before, after); // 同一补丁里合法的 PromptSuggestions = false 也没生效,版本号也没动
    }

    [Fact]
    public async Task Project_settings_round_trip_and_an_unknown_project_is_not_found()
    {
        var read = await _owner.CallAsync(SettingsMethods.ProjectGet, new ProjectSettingsRefParams(_project.WorkspaceId));
        Assert.Equal(("ask", "code", true, false), (read.PermissionMode, read.DefaultMode, read.WebSearch, read.MaxContext));

        var updated = await _owner.CallAsync(SettingsMethods.ProjectUpdate, new ProjectSettingsPatchDto(_project.WorkspaceId, PermissionMode: "auto", MaxContext: true, DefaultModel: new ModelPickDto("sonnet", "p1")));
        Assert.Equal(("auto", true, "sonnet", "p1"), (updated.PermissionMode, updated.MaxContext, updated.DefaultModel.Model, updated.DefaultModel.ProviderId));
        Assert.Equal(updated, await _owner.CallAsync(SettingsMethods.ProjectGet, new ProjectSettingsRefParams(_project.WorkspaceId)));

        Assert.Equal(ErrorCodes.NotFound, (await Failure(() => _owner.CallAsync(SettingsMethods.ProjectGet, new ProjectSettingsRefParams("nope")))).Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await Failure(() => _owner.CallAsync(SettingsMethods.ProjectUpdate, new ProjectSettingsPatchDto(_project.WorkspaceId, PermissionMode: "yolo")))).Code);
    }

    [Fact]
    public async Task A_session_created_without_a_model_takes_the_project_default_then_the_device_default()
    {
        // 都没配:留空(控制端在候选里兜底)
        var bare = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(_project.WorkspaceId));
        Assert.Equal(("", null), (bare.Model, bare.ModelProviderId));

        // 设备默认模型
        await _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(DefaultModel: new ModelPickDto("device-model", "pd"))));
        var fromDevice = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(_project.WorkspaceId));
        Assert.Equal(("device-model", "pd"), (fromDevice.Model, fromDevice.ModelProviderId));

        // 项目默认模型压过设备的;别名和供应商整对取
        await _owner.CallAsync(SettingsMethods.ProjectUpdate, new ProjectSettingsPatchDto(_project.WorkspaceId, DefaultModel: new ModelPickDto("project-model", "pp")));
        var fromProject = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(_project.WorkspaceId));
        Assert.Equal(("project-model", "pp"), (fromProject.Model, fromProject.ModelProviderId));

        // 显式指定的压过一切
        var explicitModel = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(_project.WorkspaceId, Model: "chosen"));
        Assert.Equal(("chosen", null), (explicitModel.Model, explicitModel.ModelProviderId));
    }

    [Fact]
    public async Task A_session_created_without_a_permission_mode_takes_the_project_default_then_the_device_default_and_can_be_changed()
    {
        var bare = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(_project.WorkspaceId));
        Assert.Equal("ask", bare.PermissionMode);

        // 设备默认权限档位
        await _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(DefaultPermissionMode: "full")));
        var fromDevice = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(_project.WorkspaceId));
        Assert.Equal("full", fromDevice.PermissionMode);

        // 项目默认权限档位压过设备的
        await _owner.CallAsync(SettingsMethods.ProjectUpdate, new ProjectSettingsPatchDto(_project.WorkspaceId, PermissionMode: "auto"));
        var fromProjectAuto = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(_project.WorkspaceId));
        Assert.Equal("auto", fromProjectAuto.PermissionMode);

        // 会话上直接改权限档位
        var updated = await _owner.CallAsync(ConversationMethods.SessionsSetPermissionMode, new SessionsSetPermissionModeParams(fromProjectAuto.Id, "full"));
        Assert.Equal("full", updated.PermissionMode);
        Assert.Equal("full", (await _owner.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(fromProjectAuto.Id))).Session.PermissionMode);
    }

    [Fact]
    public async Task The_proxy_password_is_stored_encrypted_and_only_a_has_flag_ever_leaves_the_device()
    {
        const string secret = "proxy-SECRET-pass-123";
        var (owner, tap) = await _rig.ConnectTappedAsync("tapped-owner");
        var topic = await Watch<DeviceSettingsState>.OpenAsync(owner, SettingsMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "snapshot");
        Assert.False(topic.Snapshot.Settings.Proxy.HasPassword);

        var state = await owner.CallAsync(SettingsMethods.ProxySetPassword, new ProxyPasswordParams(secret));

        Assert.True(state.Settings.Proxy.HasPassword);
        await topic.WaitAsync(() => topic.Decoded<DeviceSettingsState>(SettingsEvents.Changed).Any(s => s.Settings.Proxy.HasPassword), "settings.changed with the flag");
        Assert.DoesNotContain(secret, System.Text.Encoding.Latin1.GetString(tap.Transcript)); // 密码不在设备发来的任何字节里
        // 凭据表里是 NCK1 密文,不是明文
        var blob = ReadCredentialBlob("proxy:password");
        Assert.StartsWith("NCK1", System.Text.Encoding.ASCII.GetString(blob, 0, 4));
        Assert.DoesNotContain(secret, System.Text.Encoding.Latin1.GetString(blob));

        var cleared = await owner.CallAsync(SettingsMethods.ProxySetPassword, new ProxyPasswordParams(""));
        Assert.False(cleared.Settings.Proxy.HasPassword);
    }

    private byte[] ReadCredentialBlob(string reference)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_rig.DataRoot, "nextcowork.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT blob FROM credentials WHERE ref = $r";
        command.Parameters.AddWithValue("$r", reference);
        return (byte[])command.ExecuteScalar()!;
    }

    // ───────────────────────── 定时任务:到点触发 Run ─────────────────────────

    private ScheduledTaskInputDto DailyAt(string time, string name = "Nightly", string prompt = "summarize the repo") =>
        new(name, prompt, _project.WorkspaceId, "sonnet", new ScheduleRuleDto("daily", Time: time), Timezone: "UTC");

    [Fact]
    public async Task A_due_task_starts_a_scheduled_run_through_the_registry_and_the_outcome_lands_in_the_run_record_and_the_topic()
    {
        var topic = await Watch<SchedulesTopicState>.OpenAsync(_owner, SchedulesMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "schedules snapshot");
        Assert.Empty(topic.Snapshot.Tasks);

        var task = await _owner.CallAsync(SchedulesMethods.Create, DailyAt("11:00"));
        Assert.Equal(Origin.AddHours(1).ToUnixTimeMilliseconds(), task.NextRunAt);
        await topic.WaitAsync(() => topic.Decoded<SchedulesTopicState>(SchedulesEvents.Changed).Any(s => s.Tasks.Length == 1), "schedules.changed after create");

        _rig.Llm.Then(Up.Say("the repo is fine"));
        _clock.Advance(TimeSpan.FromHours(1)); // 到点

        var runs = await UntilAsync(async () => (await _owner.CallAsync(SchedulesMethods.ListRuns, new ScheduledRunsQuery(task.Id))).Runs, rs => rs.Length == 1 && rs[0].Status == "success", "the scheduled run to succeed");
        var run = Assert.Single(runs);
        Assert.Equal(("scheduled", "the repo is fine", null), (run.Trigger, run.Summary, run.Error));
        Assert.Equal(Origin.AddHours(1).ToUnixTimeMilliseconds(), run.ScheduledAt);

        // 会话是 origin = scheduled 的真会话,Run 经注册表跑完并落了库;上游请求只有一次
        var session = await _owner.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(run.SessionId));
        Assert.Equal(("scheduled", "Nightly", "sonnet"), (session.Session.Origin, session.Session.Title, session.Session.Model));
        Assert.Equal(["summarize the repo", "the repo is fine"], session.Messages.Select(m => TextOf(m.PartsJson)));
        Assert.Single(_rig.Llm.Requests);

        // 主题是全量权威:最终状态里这条运行是终态,任务的下一次触发推到了次日
        await topic.WaitAsync(() => topic.Decoded<SchedulesTopicState>(SchedulesEvents.Changed).Any(s => s.Runs.Any(r => r.Status == "success")), "final schedules.changed");
        var last = topic.Decoded<SchedulesTopicState>(SchedulesEvents.Changed)[^1];
        Assert.DoesNotContain(last.Runs, r => r.Status is "queued" or "running");
        Assert.Equal(Origin.AddHours(25).ToUnixTimeMilliseconds(), last.Tasks.Single().NextRunAt);
        Assert.True(topic.Decoded<SchedulesTopicState>(SchedulesEvents.Changed).Select(s => s.Rev).SequenceEqual(topic.Decoded<SchedulesTopicState>(SchedulesEvents.Changed).Select(s => s.Rev).Order()));
    }

    [Fact]
    public async Task Run_now_returns_the_queued_record_immediately_and_the_run_finishes_in_the_background()
    {
        var task = await _owner.CallAsync(SchedulesMethods.Create, DailyAt("23:00"));
        _rig.Llm.Then(Up.Say("manual done"));

        var queued = await _owner.CallAsync(SchedulesMethods.RunNow, new ScheduleRefParams(task.Id));
        Assert.Equal(("manual", task.Id), (queued.Trigger, queued.TaskId));

        var finished = await UntilAsync(() => _owner.CallAsync(SchedulesMethods.GetRun, new ScheduleRefParams(queued.Id)), r => r.Status == "success", "the manual run");
        Assert.Equal("manual done", finished.Summary);
        Assert.NotNull(finished.StartedAt);

        // 删记录:已结束的可以删,删两次也不报错的是任务(幂等),记录删不存在的 = 不报错
        await _owner.CallAsync(SchedulesMethods.DeleteRun, new ScheduleRefParams(queued.Id));
        Assert.Equal(ErrorCodes.NotFound, (await Failure(() => _owner.CallAsync(SchedulesMethods.GetRun, new ScheduleRefParams(queued.Id)))).Code);
        await _owner.CallAsync(SchedulesMethods.Delete, new ScheduleRefParams(task.Id));
        await _owner.CallAsync(SchedulesMethods.Delete, new ScheduleRefParams(task.Id), idem: "second-delete");
        Assert.Empty((await _owner.CallAsync(SchedulesMethods.ListTasks, new ScheduleListParams())).Tasks);
    }

    [Fact]
    public async Task A_model_that_is_not_configured_fails_the_run_with_a_stable_key_instead_of_starting_a_doomed_run()
    {
        var task = await _owner.CallAsync(SchedulesMethods.Create, DailyAt("23:00") with { Model = "no-such-model" });

        var queued = await _owner.CallAsync(SchedulesMethods.RunNow, new ScheduleRefParams(task.Id));
        var finished = await UntilAsync(() => _owner.CallAsync(SchedulesMethods.GetRun, new ScheduleRefParams(queued.Id)), r => r.Status == "error", "the failed run");

        Assert.Equal("scheduled.modelUnavailable", finished.Error);
        Assert.Empty(_rig.Llm.Requests);
    }

    [Fact]
    public async Task Invalid_schedules_are_rejected_with_stable_keys_and_nothing_is_created()
    {
        async Task<string?> KeyOf(ScheduledTaskInputDto input)
        {
            var e = await Failure(() => _owner.CallAsync(SchedulesMethods.Create, input));
            Assert.Equal(ErrorCodes.InvalidRequest, e.Code);
            return FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key;
        }

        Assert.Equal("schedules.invalid.rule", await KeyOf(DailyAt("9am")));
        Assert.Equal("schedules.invalid.timezone", await KeyOf(DailyAt("09:00") with { Timezone = "Mars/Olympus" }));
        Assert.Equal("schedules.invalid.neverFires", await KeyOf(DailyAt("09:00") with { Schedule = new ScheduleRuleDto("once", At: "2000-01-01T09:00") }));
        Assert.Equal(ErrorCodes.NotFound, (await Failure(() => _owner.CallAsync(SchedulesMethods.Create, DailyAt("09:00") with { WorkspaceId = "nope" }))).Code);
        Assert.Empty((await _owner.CallAsync(SchedulesMethods.ListTasks, new ScheduleListParams())).Tasks);
    }

    [Fact]
    public async Task Disabling_a_task_stops_it_firing_and_enabling_it_again_schedules_the_next_occurrence()
    {
        var task = await _owner.CallAsync(SchedulesMethods.Create, DailyAt("11:00"));
        var off = await _owner.CallAsync(SchedulesMethods.SetEnabled, new ScheduleSetEnabledParams(task.Id, false));
        Assert.Equal((false, null), (off.Enabled, off.NextRunAt));

        _clock.Advance(TimeSpan.FromHours(2));
        await Task.Delay(100);
        Assert.Empty((await _owner.CallAsync(SchedulesMethods.ListRuns, new ScheduledRunsQuery())).Runs);

        var on = await _owner.CallAsync(SchedulesMethods.SetEnabled, new ScheduleSetEnabledParams(task.Id, true));
        Assert.Equal(Origin.AddDays(1).AddHours(1).ToUnixTimeMilliseconds(), on.NextRunAt);
    }

    // ───────────────────────── 角色与承载 ─────────────────────────

    [Fact]
    public async Task Settings_are_member_and_up_schedule_writes_are_owner_only_usage_is_viewer_and_the_proxy_password_is_local_only()
    {
        var status = await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true));
        async Task<DeviceClient> Connect(string name, string role)
        {
            var identity = PeerIdentity.Create(name);
            var paired = await RemoteDevices.PairAsync("127.0.0.1", status.Port!.Value, identity, _rig.Host.Remote.PairingCode!, name);
            await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));
            var stream = await RemoteDevices.ConnectAsync("127.0.0.1", status.Port!.Value, identity, paired.DeviceFingerprint);
            return await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
        }

        var task = await _owner.CallAsync(SchedulesMethods.Create, DailyAt("11:00"));
        await using var viewer = await Connect("viewer-s", "viewer");
        await using var member = await Connect("member-s", "member");
        await using var remoteOwner = await Connect("owner-s", "owner");

        // viewer:能读定时任务和用量,读不了设置(member 起),什么都不能写
        Assert.Single((await viewer.CallAsync(SchedulesMethods.ListTasks, new ScheduleListParams())).Tasks);
        Assert.NotNull(await viewer.CallAsync(UsageMethods.GetSummary, new UsageWindowDto(null, 1)));
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => viewer.CallAsync(SettingsMethods.Get, Unit.Value))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => Watch<DeviceSettingsState>.OpenAsync(viewer, SettingsMethods.Topic))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => viewer.CallAsync(SchedulesMethods.RunNow, new ScheduleRefParams(task.Id)))).Code);

        // member:能读设置,不能改设置,不能动定时任务(无人值守的 full 档位只有 owner 能交出去)
        Assert.NotNull(await member.CallAsync(SettingsMethods.Get, Unit.Value));
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => member.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(PromptSuggestions: false))))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => member.CallAsync(SettingsMethods.ProjectUpdate, new ProjectSettingsPatchDto(_project.WorkspaceId, WebSearch: false)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => member.CallAsync(SchedulesMethods.Create, DailyAt("12:00")))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => member.CallAsync(SchedulesMethods.Delete, new ScheduleRefParams(task.Id)))).Code);

        // 远程 owner:能改设置和定时任务,但代理密码只有本机通道
        Assert.False((await remoteOwner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(PromptSuggestions: false)))).Settings.PromptSuggestions);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => remoteOwner.CallAsync(SettingsMethods.ProxySetPassword, new ProxyPasswordParams("x")))).Code);
        Assert.NotNull(await remoteOwner.CallAsync(SchedulesMethods.SetEnabled, new ScheduleSetEnabledParams(task.Id, false)));
    }

    // ───────────────────────── 助手 ─────────────────────────

    private static string TextOf(string partsJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(partsJson);
        return string.Concat(doc.RootElement.EnumerateArray().Where(p => p.TryGetProperty("text", out _)).Select(p => p.GetProperty("text").GetString()));
    }

    private static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> done, string what, int timeoutMs = 20_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var value = await read();
            if (done(value))
            {
                return value;
            }

            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(20);
        }
    }
}
