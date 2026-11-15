using Ncw.Agent.Host.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 角色(docs/design/09 §4、§8.4):viewer 只读、member 可对话审批、owner 全部。控制端经真实的局域网承载(TLS 1.3 + 配对)连上,
/// 角色由设备上的信任记录决定(<c>trust.setRole</c>)。路由统一检查,所以被拒绝的是整个方法,不是方法里的某一步。
/// </summary>
public sealed class ConversationRoleTests : IAsyncLifetime
{
    private readonly ConversationRig _rig = new();
    private DeviceClient _owner = null!;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        var status = await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true));
        _port = status.Port!.Value;
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

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

    private static async Task<string> CodeOf<T>(Func<Task<T>> call)
    {
        var e = await Assert.ThrowsAsync<ProtocolException>(call);
        return e.Code;
    }

    [Fact]
    public async Task A_viewer_can_read_and_watch_but_every_method_that_changes_anything_is_forbidden()
    {
        var project = await _rig.AddProjectAsync(_owner);
        var session = await _rig.CreateSessionAsync(_owner, project);
        _rig.Llm.Then(Up.Say("a reply"));
        var start = await _rig.StartAsync(_owner, session.Id, "hello");
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();

        await using var viewer = await ConnectAsync("viewer-1", "viewer");

        // 读:会话、详情、队列、工具、挂起的交互、上下文估算都行
        Assert.Single((await viewer.CallAsync(ConversationMethods.SessionsList, new SessionsListParams(project.WorkspaceId))).Sessions);
        Assert.Equal(2, (await viewer.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(session.Id))).Messages.Length);
        Assert.Empty((await viewer.CallAsync(ConversationMethods.QueueList, new SessionRefParams(session.Id))).Items);
        Assert.NotEmpty((await viewer.CallAsync(ConversationMethods.ToolsList, new ToolsListParams())).Tools);
        Assert.Empty((await viewer.CallAsync(ConversationMethods.InteractionsList, Unit.Value)).Pending);
        Assert.True((await viewer.CallAsync(ConversationMethods.ContextPreview, new ContextPreviewParams(session.Id))).Used > 0);

        // 看:device / interactions / session / run 主题都能订阅(重放 Run 的日志也行)
        var device = await Watch<DeviceTopicState>.OpenAsync(viewer, DeviceMethods.DeviceTopic);
        var interactions = await Watch<InteractionsDto>.OpenAsync(viewer, ConversationMethods.InteractionsTopic);
        var topic = await Watch<SessionTopicState>.OpenAsync(viewer, ConversationMethods.SessionTopic(session.Id));
        var run = await RunWatch.OpenAsync(viewer, start.RunId);
        await device.WaitAsync(() => device.HasSnapshot && interactions.HasSnapshot && topic.HasSnapshot, "snapshots");
        Assert.Equal("done", run.Snapshot!.State);

        // 改:全部 forbidden
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ConversationMethods.SessionsRename, new SessionsRenameParams(session.Id, "x"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ConversationMethods.SessionsDeleteTurn, new SessionsDeleteTurnParams(session.Id, "m"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => _rig.StartAsync(viewer, session.Id, "hi")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ConversationMethods.RunsAbort, new RunsAbortParams(start.RunId))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ConversationMethods.InteractionsRespond,
            new InteractionsRespondParams("i", 1, new ToolPermissionResponseDto(PermissionChoices.AllowOnce)))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ConversationMethods.QueueEnqueue,
            new QueueEnqueueParams(session.Id, null, [new InputPartDto(InputPartKinds.Text, "x")], new SendOptionsDto("sonnet")))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ConversationMethods.ContextCompact, new ContextCompactParams(session.Id))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => viewer.CallAsync(ConversationMethods.SessionsDelete, new SessionRefParams(session.Id))));

        // 什么都没变
        Assert.Single(_rig.Llm.Requests);
        Assert.Equal(session.Title, (await _owner.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(session.Id))).Session.Title);
    }

    [Fact]
    public async Task A_member_can_talk_and_approve_but_not_use_full_access_delete_sessions_or_change_a_running_runs_mode()
    {
        var project = await _rig.AddProjectAsync(_owner);
        await using var member = await ConnectAsync("member-1", "member");
        var session = await _rig.CreateSessionAsync(member, project);
        var target = Path.Combine(project.Path, "member.txt");

        // 完全访问档位只有 owner 能用(09 §4)
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => _rig.StartAsync(member, session.Id, "hi", permission: "full")));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ConversationMethods.QueueEnqueue,
            new QueueEnqueueParams(session.Id, null, [new InputPartDto(InputPartKinds.Text, "x")], new SendOptionsDto("sonnet", PermissionMode: "full")))));
        Assert.Empty(_rig.Llm.Requests);

        // 对话 + 审批:member 可以
        _rig.Llm.Then(Up.Calls(("c1", "Write", ConversationRig.WriteJson(target, "by member")))).Then(Up.Say("done"));
        var interactions = await Watch<InteractionsDto>.OpenAsync(member, ConversationMethods.InteractionsTopic);
        var start = await _rig.StartAsync(member, session.Id, "write it");
        var watch = await RunWatch.OpenAsync(member, start.RunId);
        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending approval");
        var pending = interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending[0];

        // 运行中改档位 / 删会话:owner 才行
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ConversationMethods.RunsSetPermissionMode, new RunsSetPermissionModeParams(start.RunId, "full"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => member.CallAsync(ConversationMethods.SessionsDelete, new SessionRefParams(session.Id))));

        var answered = await member.CallAsync(ConversationMethods.InteractionsRespond,
            new InteractionsRespondParams(pending.Id, pending.Version, new ToolPermissionResponseDto(PermissionChoices.AllowOnce)));
        Assert.Equal("member-1", answered.Resolution.ResolvedBy);
        await watch.SettledAsync();
        Assert.Equal("by member", File.ReadAllText(target));

        // owner:可以改运行中 Run 的档位、可以删会话
        _rig.Llm.Then(Up.Calls(("c2", "Write", ConversationRig.WriteJson(Path.Combine(project.Path, "second.txt"))))).Then(Up.Say("second done"));
        var second = await _rig.StartAsync(_owner, session.Id, "again");
        var secondWatch = await RunWatch.OpenAsync(_owner, second.RunId);
        await Watch<Unit>.UntilAsync(() => secondWatch.Of<InteractionRequestEventDto>().Any(), "second approval");
        await _owner.CallAsync(ConversationMethods.RunsSetPermissionMode, new RunsSetPermissionModeParams(second.RunId, "full"));
        var again = (await _owner.CallAsync(ConversationMethods.InteractionsList, Unit.Value)).Pending.Single();
        await _owner.CallAsync(ConversationMethods.InteractionsRespond, new InteractionsRespondParams(again.Id, again.Version, new ToolPermissionResponseDto(PermissionChoices.AllowOnce)));
        await secondWatch.SettledAsync();

        await _owner.CallAsync(ConversationMethods.SessionsDelete, new SessionRefParams(session.Id));
        Assert.Equal(ErrorCodes.NotFound, await CodeOf(() => member.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(session.Id))));
    }

    [Fact]
    public async Task An_unpaired_controller_cannot_use_conversation_methods_or_subscribe()
    {
        var identity = PeerIdentity.Create("stranger");
        var stream = (await SecureChannel.ConnectAsync(await Ncw.Protocol.Transport.LanTransport.ConnectAsync("127.0.0.1", _port, default), identity, null, default)).Stream;
        await using var stranger = await DeviceClient.ConnectAsync(stream, new ClientHello("stranger", "3.0.0"));
        Assert.Equal("unpaired", stranger.Welcome.Role);

        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => stranger.CallAsync(ConversationMethods.SessionsList, new SessionsListParams("w"))));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => Watch<InteractionsDto>.OpenAsync(stranger, ConversationMethods.InteractionsTopic)));
        Assert.Equal(ErrorCodes.Forbidden, await CodeOf(() => RunWatch.OpenAsync(stranger, "some-run")));
    }
}
