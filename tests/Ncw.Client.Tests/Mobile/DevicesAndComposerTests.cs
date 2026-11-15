using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Mobile;

/// <summary>设备页的行(状态只来自 Store)、输入框带上传完成的附件发送、收件箱点进来的卡片定位。</summary>
public class DevicesAndComposerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_devices_page_lists_every_device_with_state_and_unknown_numbers_when_disconnected()
    {
        var stores = MockWorkspace.Create(Now);
        var nav = new MobileNavigationViewModel();
        var page = new DevicesPageViewModel(stores, new DeviceActions(new Ncw.Client.Devices.DeviceRegistry()), nav);

        Assert.Equal(["local", "cloud", "studio"], page.Items.Select(i => i.DeviceId));
        var local = page.Items[0];
        Assert.True(local.IsConnected);
        Assert.True(local.HasRunning);
        Assert.True(local.HasPending);
        Assert.Equal(2, local.Pending);
        var studio = page.Items[2];
        Assert.True(studio.Unknown);
        Assert.False(studio.HasRunning);
        Assert.False(studio.HasPending);
        Assert.True(studio.CanRemove);
        Assert.False(local.CanRemove);
    }

    [Fact]
    public void A_connection_change_updates_the_row_and_the_add_entries_push_their_pages()
    {
        var stores = MockWorkspace.Create(Now);
        var nav = new MobileNavigationViewModel();
        var page = new DevicesPageViewModel(stores, new DeviceActions(new Ncw.Client.Devices.DeviceRegistry()), nav);

        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("studio", w.Devices["studio"] with { Connection = DeviceConnection.Connected }) });
        Assert.True(page.Items.Single(i => i.DeviceId == "studio").IsConnected);

        page.AddByQrCommand.Execute(null);
        Assert.Equal(MobileTab.Devices, nav.SelectedTab);
        Assert.IsType<QrPairRoute>(nav.Top);
        nav.Pop();
        page.AddManuallyCommand.Execute(null);
        Assert.IsType<ManualAddRoute>(nav.Top);
    }

    /// <summary>写入会话镜像(设备推来的 <c>session/&lt;id&gt;</c> 快照):没有它转录是空的,交互卡片也不出现。</summary>
    private static void SeedSession(ClientStores stores, string sessionId = "s-ncw-1", string? activeRunId = "run-1")
    {
        var meta = new SessionDto(sessionId, "nextcowork", null, "user", "t", "auto", "claude", null, "code", "auto", false, false, 0, 0);
        stores.Conversations.Update(c => ConversationReducer.ApplySession(c, "local", new SessionTopicState(new SessionPageDto(meta, [], false, [], [], []), new QueueDto(sessionId, 1, []), activeRunId)));
    }

    private sealed class Commands : IConversationCommands
    {
        public List<(string Text, IReadOnlyList<InputFile> Files)> Sent { get; } = [];

        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => SendAsync(session, text, [], options, ct);

        public Task<SendResult> SendAsync(SessionRef session, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default)
        {
            Sent.Add((text, files));
            return Task.FromResult(new SendResult(SendOutcome.Started));
        }

        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default) => Task.FromResult(new InteractionResult(InteractionOutcome.Answered));
        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }

    private static (ComposerViewModel Composer, Commands Commands, FakeUploader Uploader) MakeComposer()
    {
        var commands = new Commands();
        var uploader = new FakeUploader();
        var composer = new ComposerViewModel(commands, attachments: new AttachmentTrayViewModel(uploader, () => null));
        var session = new SessionRef("local", "s1");
        var meta = new SessionDto("s1", "ws", null, "user", "t", "auto", "claude", null, "code", "auto", false, false, 0, 0);
        var options = new[] { new ModelOptionDto("p", "P", "claude", "Claude", "claude", 200_000, 8_000, true, true, true, true, null, null, "anthropic") };
        composer.Update(session, meta, null, null, SessionActivity.Idle, DeviceConnection.Connected, "owner", options);
        return (composer, commands, uploader);
    }

    [Fact]
    public async Task A_message_is_sent_with_the_uploaded_images_as_image_parts_and_the_tray_empties()
    {
        var (composer, commands, uploader) = MakeComposer();
        var upload = composer.Attachments.AddAsync(new PickedAttachment("cat.jpg", "image/jpeg", 100, _ => Task.FromResult(new byte[4])));
        uploader.Uploads[0].Done.SetResult(FakeUploader.Ok("a1", "cat.jpg", "image/jpeg", 100));
        await upload;

        composer.Draft = "这是什么?";
        await composer.SubmitCommand.ExecuteAsync(null);

        var sent = Assert.Single(commands.Sent);
        var file = Assert.Single(sent.Files);
        Assert.Equal("ncw://attachments/sessions/s1/a1", file.Path);
        Assert.Equal("image/jpeg", file.Mime);
        Assert.Empty(composer.Attachments.Items);
    }

    [Fact]
    public async Task Sending_waits_for_uploads_in_flight_so_an_attachment_is_never_silently_dropped()
    {
        var (composer, _, uploader) = MakeComposer();
        composer.Draft = "看这张图";
        Assert.True(composer.State.CanSend);

        var upload = composer.Attachments.AddAsync(new PickedAttachment("cat.jpg", "image/jpeg", 100, _ => Task.FromResult(new byte[4])));

        Assert.False(composer.State.CanSend);
        uploader.Uploads[0].Done.SetResult(FakeUploader.Ok("a1", "cat.jpg", "image/jpeg", 100));
        await upload;
        Assert.True(composer.State.CanSend);
    }

    [Fact]
    public void An_uploaded_attachment_turns_into_the_right_input_part()
    {
        var image = InputFile.Attachment("ncw://attachments/sessions/s1/a.png", "a.png", "image/png");
        var pdf = InputFile.Attachment("ncw://attachments/sessions/s1/b.pdf", "b.pdf", "application/pdf");
        var mention = new InputFile("src/a.cs", "a.cs");

        Assert.Equal("a.cs@src/a.cs", "a.cs@" + mention.Path);
        Assert.Equal("hi\n@src/a.cs", InputFile.WithMentions("hi", [mention, image, pdf])); // 上传的附件不写进 @ 引用
        Assert.Equal("hi", InputFile.WithMentions("hi", [image]));
    }

    [Fact]
    public void Focusing_a_row_that_arrives_later_waits_for_it_and_then_highlights_the_card_once()
    {
        var stores = MockWorkspace.Create(Now);
        var conversation = new ConversationViewModel(stores, new Commands());
        stores.Selection.Update(_ => new SelectionState(new ProjectRef("local", "nextcowork"), "s-ncw-1"));
        SeedSession(stores);
        var focused = new List<string>();
        conversation.FocusRequested += row => focused.Add(row.Key);

        conversation.RequestFocus("i:i1"); // 快照还没到,行还不在
        Assert.Empty(focused);

        var interaction = new ToolPermissionInteractionDto
        {
            Id = "i1", RunId = "run-1", CallId = "c1", ToolName = "Bash", InternalId = "bash", InputJson = "{\"command\":\"ls\"}",
            Env = new EnvRefDto("local", "local", "local", "local"), AllowedScopes = ["session"], CreatedAt = 1, Version = 1,
        };
        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, "local", new InteractionsDto(1, [interaction])));
        stores.Conversations.Update(c => c with { Runs = c.Runs.SetItem(new RunRef("local", "run-1"), RunView.New(new RunRef("local", "run-1"), "s-ncw-1", 1) with { Phase = RunPhase.Running }) });

        Assert.Equal(["i:i1"], focused);
        Assert.True(conversation.Rows.OfType<InteractionRowViewModel>().Single().IsHighlighted);

        stores.Conversations.Update(c => c); // 之后的刷新不再重复触发
        Assert.Single(focused);
    }

    [Fact]
    public async Task The_long_press_allow_answers_with_allow_once_and_a_cancelled_press_answers_nothing()
    {
        var stores = MockWorkspace.Create(Now);
        var commands = new RecordingRespond();
        var clock = new ManualClock(Now);
        var conversation = new ConversationViewModel(stores, commands, clock: clock);
        stores.Selection.Update(_ => new SelectionState(new ProjectRef("local", "nextcowork"), "s-ncw-1"));
        SeedSession(stores);
        var interaction = new ToolPermissionInteractionDto
        {
            Id = "i1", RunId = "run-1", CallId = "c1", ToolName = "Bash", InternalId = "bash", InputJson = "{\"command\":\"rm -rf out\"}",
            Env = new EnvRefDto("local", "local", "local", "local"), AllowedScopes = ["session"], Risk = "dangerous", CreatedAt = 1, Version = 7,
        };
        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, "local", new InteractionsDto(1, [interaction])));
        stores.Conversations.Update(c => c with { Runs = c.Runs.SetItem(new RunRef("local", "run-1"), RunView.New(new RunRef("local", "run-1"), "s-ncw-1", 1) with { Phase = RunPhase.Running }) });
        var row = conversation.Rows.OfType<InteractionRowViewModel>().Single();

        // 危险审批:按了普通时长还不够;松手取消,什么也不回答
        row.BeginAllowHold();
        clock.Advance(InteractionRowViewModel.HoldDuration + TimeSpan.FromMilliseconds(100));
        row.TickAllowHold();
        row.EndAllowHold();
        Assert.Empty(commands.Responses);

        // 按满危险时长 → 一次 AllowOnce,带着看到的版本号
        row.BeginAllowHold();
        clock.Advance(InteractionRowViewModel.DangerousHoldDuration);
        row.TickAllowHold();
        await Task.Yield();

        var (id, version, response) = Assert.Single(commands.Responses);
        Assert.Equal(("i1", 7L), (id, version));
        Assert.Equal(PermissionChoices.AllowOnce, Assert.IsType<ToolPermissionResponseDto>(response).Choice);
    }

    [Fact]
    public void A_card_that_changes_version_mid_press_cancels_the_press_so_an_old_version_is_never_approved()
    {
        var stores = MockWorkspace.Create(Now);
        var clock = new ManualClock(Now);
        var conversation = new ConversationViewModel(stores, new RecordingRespond(), clock: clock);
        stores.Selection.Update(_ => new SelectionState(new ProjectRef("local", "nextcowork"), "s-ncw-1"));
        SeedSession(stores);
        ToolPermissionInteractionDto Make(long version) => new()
        {
            Id = "i1", RunId = "run-1", CallId = "c1", ToolName = "Bash", InternalId = "bash", InputJson = "{\"command\":\"ls\"}",
            Env = new EnvRefDto("local", "local", "local", "local"), AllowedScopes = ["session"], CreatedAt = 1, Version = version,
        };
        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, "local", new InteractionsDto(1, [Make(1)])));
        stores.Conversations.Update(c => c with { Runs = c.Runs.SetItem(new RunRef("local", "run-1"), RunView.New(new RunRef("local", "run-1"), "s-ncw-1", 1) with { Phase = RunPhase.Running }) });
        var row = conversation.Rows.OfType<InteractionRowViewModel>().Single();
        row.BeginAllowHold();
        clock.Advance(TimeSpan.FromMilliseconds(300));
        row.TickAllowHold();

        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, "local", new InteractionsDto(2, [Make(2)]))); // 设备给了新版本

        Assert.Equal(HoldPhase.Cancelled, row.AllowHold.Phase);
        Assert.Equal(HoldCancelReason.Interrupted, row.AllowHold.CancelReason);
        Assert.Equal(0, row.HoldProgress);
    }

    private sealed class RecordingRespond : IConversationCommands
    {
        public List<(string Id, long Version, InteractionResponseDto Response)> Responses { get; } = [];

        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(new SendResult(SendOutcome.Started));
        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default)
        {
            Responses.Add((interaction.Id, interaction.Version, response));
            return Task.FromResult(new InteractionResult(InteractionOutcome.Answered));
        }

        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }
}
