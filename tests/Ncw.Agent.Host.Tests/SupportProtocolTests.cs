using Ncw.Agent.Host.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 附件 / 全局搜索 / 输入框补全 / 小组件经协议的端到端(真实 Agent + 真实 DeviceClient + 假上游):
/// 分块上传 → 发送带图片的消息(草稿在落盘的同一事务里升为已提交)→ 复制会话时图片搬家 → 删会话回收文件;远程控制端按引用分块读;
/// 搜索只返回顶层会话;补全经真实路由(假上游)出灰字、设置里关掉就没有请求;小组件状态读写与提交;角色(viewer 只读)。
/// </summary>
public sealed class SupportProtocolTests : IAsyncLifetime
{
    private static readonly byte[] Png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 1, 2, 3, 4, 5, 6, 7, 8];

    private readonly ConversationRig _rig = new();
    private DeviceClient _owner = null!;
    private ProjectDto _project = null!;

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _project = await _rig.AddProjectAsync(_owner);
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static async Task<ProtocolException> Failure<T>(Func<Task<T>> call) => await Assert.ThrowsAsync<ProtocolException>(call);

    private static string KeyOf(ProtocolException e) => FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key;

    private async Task<AttachmentDto> UploadAsync(DeviceClient client, string sessionId, string name, byte[] bytes, int chunk = 10)
    {
        var begin = await client.CallAsync(AttachmentsMethods.UploadBegin, new AttachmentsUploadBeginParams(sessionId, name, "image/png", bytes.Length));
        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            var progress = await client.CallAsync(AttachmentsMethods.UploadChunk, new AttachmentsUploadChunkParams(begin.UploadId, offset, bytes[offset..Math.Min(bytes.Length, offset + chunk)]));
            Assert.Equal(Math.Min(bytes.Length, offset + chunk), progress.Received);
        }

        return await client.CallAsync(AttachmentsMethods.UploadCommit, new AttachmentsUploadRefParams(begin.UploadId));
    }

    private async Task<string> SendWithImageAsync(string sessionId, AttachmentDto image, string reply)
    {
        _rig.Llm.Then(Up.Say(reply));
        var start = await _owner.CallAsync(ConversationMethods.RunsStart, new RunsStartParams(
            Guid.NewGuid().ToString("N"), sessionId, Guid.NewGuid().ToString("N"),
            [new InputPartDto(InputPartKinds.Text, "what is in this picture"), new InputPartDto(InputPartKinds.Image, Mime: "image/png", DataRef: image.Url)],
            new SendOptionsDto("sonnet", PermissionMode: "ask")));
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();
        return start.RunId;
    }

    // ───────────────────────── 附件 ─────────────────────────

    [Fact]
    public async Task An_image_uploaded_in_chunks_is_a_draft_until_the_message_that_references_it_is_committed_and_then_it_is_listed_no_more()
    {
        var session = await _rig.CreateSessionAsync(_owner, _project);
        var image = await UploadAsync(_owner, session.Id, "screenshot.png", Png);

        Assert.StartsWith($"ncw://attachments/sessions/{session.Id}/", image.Url);
        Assert.DoesNotContain(_rig.DataRoot, image.Url); // 绝对路径不出设备
        var drafts = (await _owner.CallAsync(AttachmentsMethods.ListBySession, new AttachmentsListParams(session.Id))).Attachments;
        Assert.Equal(["screenshot.png"], drafts.Select(a => a.DisplayName));

        await SendWithImageAsync(session.Id, image, "a cat");

        Assert.Empty((await _owner.CallAsync(AttachmentsMethods.ListBySession, new AttachmentsListParams(session.Id))).Attachments); // 提交之后不再是草稿
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_rig.DataRoot, "nextcowork.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, session_id FROM attachments WHERE id = $id";
        command.Parameters.AddWithValue("$id", image.Id);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(("committed", session.Id), (reader.GetString(0), reader.GetString(1)));
    }

    [Fact]
    public async Task Removing_a_draft_works_but_a_committed_attachment_is_a_conflict_with_a_stable_key()
    {
        var session = await _rig.CreateSessionAsync(_owner, _project);
        var draft = await UploadAsync(_owner, session.Id, "a.png", Png);
        await _owner.CallAsync(AttachmentsMethods.Remove, new AttachmentRefParams(draft.Id));
        Assert.Empty((await _owner.CallAsync(AttachmentsMethods.ListBySession, new AttachmentsListParams(session.Id))).Attachments);

        var kept = await UploadAsync(_owner, session.Id, "b.png", [..Png, 9]);
        await SendWithImageAsync(session.Id, kept, "ok");
        var conflict = await Failure(() => _owner.CallAsync(AttachmentsMethods.Remove, new AttachmentRefParams(kept.Id)));
        Assert.Equal((ErrorCodes.Conflict, "attachments.error.committed"), (conflict.Code, KeyOf(conflict)));
    }

    [Fact]
    public async Task Upload_failures_carry_stable_keys_and_parameters_for_the_controller_to_translate()
    {
        var session = await _rig.CreateSessionAsync(_owner, _project);
        var tooBig = await Failure(() => _owner.CallAsync(AttachmentsMethods.UploadBegin, new AttachmentsUploadBeginParams(session.Id, "huge.bin", "application/zip", 64L * 1024 * 1024)));
        Assert.Equal((ErrorCodes.TooLarge, "attachments.error.tooLarge"), (tooBig.Code, KeyOf(tooBig)));
        Assert.Equal("32", FrameCodec.DecodePayload(tooBig.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Params["limit"]);
        Assert.Equal(ErrorCodes.InvalidRequest, (await Failure(() => _owner.CallAsync(AttachmentsMethods.UploadBegin, new AttachmentsUploadBeginParams("../etc", "a.png", "image/png", 5)))).Code);
        Assert.Equal(ErrorCodes.NotFound, (await Failure(() => _owner.CallAsync(AttachmentsMethods.UploadCommit, new AttachmentsUploadRefParams("0123456789abcdef")))).Code);
        var begin = await _owner.CallAsync(AttachmentsMethods.UploadBegin, new AttachmentsUploadBeginParams(session.Id, "a.png", "image/png", 10));
        Assert.Equal(ErrorCodes.InvalidRequest, (await Failure(() => _owner.CallAsync(AttachmentsMethods.UploadChunk, new AttachmentsUploadChunkParams(begin.UploadId, 5, [1])))).Code); // 乱序
        await _owner.CallAsync(AttachmentsMethods.UploadAbort, new AttachmentsUploadRefParams(begin.UploadId));
    }

    [Fact]
    public async Task Duplicating_a_session_moves_its_images_and_deleting_the_source_leaves_the_copys_images_readable()
    {
        var session = await _rig.CreateSessionAsync(_owner, _project);
        var image = await UploadAsync(_owner, session.Id, "pic.png", Png);
        await SendWithImageAsync(session.Id, image, "a cat");

        var copy = await _owner.CallAsync(ConversationMethods.SessionsDuplicate, new SessionsDuplicateParams(session.Id));
        var copied = (await _owner.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(copy.Id))).Messages[0];
        Assert.DoesNotContain($"/sessions/{session.Id}/", copied.PartsJson);
        Assert.Contains($"/sessions/{copy.Id}/", copied.PartsJson);

        await _owner.CallAsync(ConversationMethods.SessionsDelete, new SessionRefParams(session.Id));
        Assert.False(Directory.Exists(Path.Combine(_rig.DataRoot, "attachments", "sessions", session.Id)) && Directory.GetFiles(Path.Combine(_rig.DataRoot, "attachments", "sessions", session.Id)).Length > 0); // 回收了
        var url = System.Text.RegularExpressions.Regex.Match(copied.PartsJson, @"ncw://attachments/sessions/[^""]+").Value;
        Assert.Equal(Png, (await _owner.CallAsync(AttachmentsMethods.Read, new AttachmentsReadParams(url, 0, 1000))).Data); // 副本还在
    }

    [Fact]
    public async Task A_remote_viewer_can_read_an_attachment_by_reference_in_chunks_but_not_upload_or_remove()
    {
        var session = await _rig.CreateSessionAsync(_owner, _project);
        var image = await UploadAsync(_owner, session.Id, "pic.png", Png);
        var port = (await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
        var identity = PeerIdentity.Create("viewer-a");
        var paired = await RemoteDevices.PairAsync("127.0.0.1", port, identity, _rig.Host.Remote.PairingCode!, "viewer-a");
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, "viewer"));
        await using var viewer = await DeviceClient.ConnectAsync(await RemoteDevices.ConnectAsync("127.0.0.1", port, identity, paired.DeviceFingerprint), new ClientHello("viewer-a", "3.0.0"));

        var bytes = new List<byte>();
        while (true)
        {
            var chunk = await viewer.CallAsync(AttachmentsMethods.Read, new AttachmentsReadParams(image.Url, bytes.Count, 7));
            bytes.AddRange(chunk.Data);
            if (chunk.Eof)
            {
                break;
            }
        }

        Assert.Equal(Png, bytes);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => viewer.CallAsync(AttachmentsMethods.UploadBegin, new AttachmentsUploadBeginParams(session.Id, "a.png", "image/png", 5)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => viewer.CallAsync(AttachmentsMethods.Remove, new AttachmentRefParams(image.Id)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => viewer.CallAsync(AttachmentsMethods.ListBySession, new AttachmentsListParams(session.Id)))).Code);
    }

    // ───────────────────────── 搜索 ─────────────────────────

    [Fact]
    public async Task Search_finds_messages_in_top_level_sessions_scoped_to_a_project_and_a_blank_query_is_empty()
    {
        var session = await _rig.CreateSessionAsync(_owner, _project);
        _rig.Llm.Then(Up.Say("the quokka is a small marsupial"));
        var start = await _rig.StartAsync(_owner, session.Id, "quokka facts please");
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();

        var hits = (await _owner.CallAsync(SearchMethods.Conversations, new SearchConversationsParams("quokka"))).Hits;
        Assert.Equal(2, hits.Length);
        Assert.All(hits, h => Assert.Equal((session.Id, _project.WorkspaceId), (h.SessionId, h.WorkspaceId)));
        Assert.Contains("<mark>quokka", hits[0].Snippet + hits[1].Snippet);
        Assert.Empty((await _owner.CallAsync(SearchMethods.Conversations, new SearchConversationsParams("quokka", WorkspaceId: "no-such-project"))).Hits);
        Assert.Empty((await _owner.CallAsync(SearchMethods.Conversations, new SearchConversationsParams("   "))).Hits);
        Assert.Empty((await _owner.CallAsync(SearchMethods.Conversations, new SearchConversationsParams("quokka AND OR \"unbalanced"))).Hits); // 运算符不会破坏语法,只是搜不到
    }

    // ───────────────────────── 补全 ─────────────────────────

    [Fact]
    public async Task Suggest_next_goes_through_the_router_to_the_fake_upstream_and_the_settings_switch_turns_it_off()
    {
        var session = await _rig.CreateSessionAsync(_owner, _project);
        _rig.Llm.Then(Up.Say("已修改完,测试还没跑。"));
        var start = await _rig.StartAsync(_owner, session.Id, "修一下,然后跑测试");
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();
        Assert.Single(_rig.Llm.Requests);

        _rig.Llm.Then(Up.Say("<suggestion>跑一下测试</suggestion>"));
        var reply = await _owner.CallAsync(ComposerMethods.SuggestNext, new ComposerSuggestParams("req-0000000a", _project.WorkspaceId, session.Id, 1, "sonnet"));
        Assert.Equal(new ComposerReplyDto("req-0000000a", "跑一下测试"), reply);
        Assert.Equal(2, _rig.Llm.Requests.Count);
        var request = _rig.Llm.Requests[1];
        Assert.Contains("<suggestion>", request.System);
        Assert.Contains("修一下,然后跑测试", ((Ncw.Agent.Llm.TextPart)request.Messages[0].Parts[0]).Text); // 带着用户最近的话,不带工具

        await _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(PromptSuggestions: false)));
        _rig.Llm.Then(Up.Say("<suggestion>不该出现</suggestion>"));
        Assert.Equal("", (await _owner.CallAsync(ComposerMethods.SuggestNext, new ComposerSuggestParams("req-0000000b", _project.WorkspaceId, session.Id, 2, "sonnet"))).Text);
        Assert.Equal(2, _rig.Llm.Requests.Count); // 开关关着:不花钱

        // 历史指令:零请求
        var history = await _owner.CallAsync(ComposerMethods.History, new ComposerHistoryParams(_project.WorkspaceId));
        Assert.Contains("修一下,然后跑测试", history.Prompts);
        await _owner.CallAsync(ComposerMethods.Cancel, new ComposerCancelParams("req-0000000a")); // 不存在的取消不报错
    }

    [Fact]
    public async Task Inline_completion_is_off_by_default_and_a_draft_with_a_secret_never_reaches_the_model()
    {
        var context = new ComposerContextDto("Proj", null, ["a.ts"], []);
        var off = await _owner.CallAsync(ComposerMethods.Complete, new ComposerCompleteParams("req-0000000c", _project.WorkspaceId, null, "帮我给 X 补", context));
        Assert.Equal("", off.Text);
        Assert.Empty(_rig.Llm.Requests);

        await _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(InlineCompletion: true, InlineCompletionModel: new ModelPickDto("sonnet"))));
        _rig.Llm.Then(Up.Say("<completion>上单元测试</completion>"));
        var on = await _owner.CallAsync(ComposerMethods.Complete, new ComposerCompleteParams("req-0000000d", _project.WorkspaceId, null, "帮我给 X 补", context));
        Assert.Equal("上单元测试", on.Text);
        Assert.Single(_rig.Llm.Requests);

        var secret = await _owner.CallAsync(ComposerMethods.Complete, new ComposerCompleteParams("req-0000000e", _project.WorkspaceId, null, "export TOKEN=abcd1234abcd", context));
        Assert.Equal("", secret.Text);
        Assert.Single(_rig.Llm.Requests); // 带密钥的草稿没有发给模型
    }

    // ───────────────────────── 小组件 ─────────────────────────

    [Fact]
    public async Task Widget_state_round_trips_over_the_protocol_a_submit_with_nothing_waiting_is_not_found_and_actions_are_denied_with_a_stable_key()
    {
        var session = await _rig.CreateSessionAsync(_owner, _project);
        Assert.Null((await _owner.CallAsync(WidgetsMethods.GetState, new WidgetRefParams(session.Id, "call-1"))).State);
        await _owner.CallAsync(WidgetsMethods.SetState, new WidgetSetStateParams(session.Id, "call-1", "{\"count\":2}"));
        Assert.Equal("{\"count\":2}", (await _owner.CallAsync(WidgetsMethods.GetState, new WidgetRefParams(session.Id, "call-1"))).State);
        Assert.Equal(ErrorCodes.TooLarge, (await Failure(() => _owner.CallAsync(WidgetsMethods.SetState, new WidgetSetStateParams(session.Id, "call-1", $"\"{new string('x', 70_000)}\"")))).Code);
        Assert.Equal(ErrorCodes.InvalidRequest, (await Failure(() => _owner.CallAsync(WidgetsMethods.SetState, new WidgetSetStateParams(session.Id, "call-1", "{broken")))).Code);

        var notWaiting = await Failure(() => _owner.CallAsync(WidgetsMethods.Submit, new WidgetSubmitParams("call-1", "\"value\"")));
        Assert.Equal((ErrorCodes.NotFound, "widgets.error.notWaiting"), (notWaiting.Code, KeyOf(notWaiting)));
        var action = await _owner.CallAsync(WidgetsMethods.RunAction, new WidgetActionParams(session.Id, "call-1", "refresh", "{}", false));
        // 这张卡没有声明过任何动作(转录里没有 widget 卡):拒绝,稳定 key;动作的完整流程在 WidgetsTests。
        Assert.Equal(("deny", "widgets.action.undeclared"), (action.Kind, action.Reason));
    }
}
