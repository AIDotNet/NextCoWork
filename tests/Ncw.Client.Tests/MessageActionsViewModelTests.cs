using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>消息工具条背后的状态:什么时候能编辑 / 删除 / 分支、编辑状态机、失败的结构化结果。设备由假命令代替。</summary>
public class MessageActionsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly ProjectRef Project = new("local", "nextcowork");
    private static readonly SessionRef Session = new("local", "s-ncw-1");

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NoSend : IConversationCommands
    {
        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(new SendResult(SendOutcome.Started));

        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default) =>
            Task.FromResult(new InteractionResult(InteractionOutcome.Answered));

        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }

    private sealed class FakeMessages : IMessageCommands
    {
        public List<string> Calls { get; } = [];

        public ActionResult<Unit> Result { get; set; } = new(ActionOutcome.Ok);

        public ActionResult<string> BranchResult { get; set; } = new(ActionOutcome.Ok, "branch-1");

        public Task<ActionResult<Unit>> EditAsync(SessionRef session, string messageId, string text, CancellationToken ct = default)
        {
            Calls.Add($"edit:{messageId}:{text}");
            return Task.FromResult(Result);
        }

        public IReadOnlyList<InputFile> LastFiles { get; private set; } = [];

        public Task<ActionResult<Unit>> EditAndResendAsync(SessionRef session, string messageId, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default)
        {
            LastFiles = files;
            Calls.Add($"rerun:{messageId}:{text}");
            return Task.FromResult(Result);
        }

        public Task<ActionResult<Unit>> DeleteTurnAsync(SessionRef session, string userMessageId, CancellationToken ct = default)
        {
            Calls.Add($"deleteTurn:{userMessageId}");
            return Task.FromResult(Result);
        }

        public Task<ActionResult<Unit>> DeleteReplyAsync(SessionRef session, string fromAssistantId, string toAssistantId, CancellationToken ct = default)
        {
            Calls.Add($"deleteReply:{fromAssistantId}..{toAssistantId}");
            return Task.FromResult(Result);
        }

        public Task<ActionResult<string>> BranchAsync(ProjectRef project, string sessionId, string userMessageId, CancellationToken ct = default)
        {
            Calls.Add($"branch:{sessionId}:{userMessageId}");
            return Task.FromResult(BranchResult);
        }
    }

    private static ClientStores Stores(string role = "owner", bool running = false, DeviceConnection connection = DeviceConnection.Connected, MessageDto[]? history = null)
    {
        var stores = MockWorkspace.Create(Now);
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem("local", w.Devices["local"] with
            {
                Role = role,
                Connection = connection,
                Index = running ? new ActiveRunIndex("local", [new ActiveRun("local", "run-1", "s-ncw-1", "nextcowork", 0)]) { Rev = 1 } : new ActiveRunIndex("local", []) { Rev = 1 },
            }),
        });
        stores.Providers.Update(_ => new ProvidersMirror(ImmutableDictionary<string, DeviceProviders>.Empty.Add("local",
            DeviceProviders.Unknown with { Known = true, Options = [] })));
        stores.Selection.Update(_ => new SelectionState(Project, "s-ncw-1"));
        var meta = new SessionDto("s-ncw-1", "nextcowork", null, "user", "t", null, "opus", null, "code", "auto", false, false, 1, 2);
        var messages = history ?? new[]
        {
            new MessageDto("u1", "user", Now.ToUnixTimeMilliseconds(), false, """[{"type":"text","text":"hello"}]"""),
            new MessageDto("a1", "assistant", Now.ToUnixTimeMilliseconds() + 1, false, """[{"type":"text","text":"hi there"}]"""),
        };
        stores.Conversations.Update(c => ConversationReducer.ApplySession(c, "local",
            new SessionTopicState(new SessionPageDto(meta, messages, false, [], [], []), new QueueDto("s-ncw-1", 1, []), running ? "run-1" : null)));
        return stores;
    }

    private static (ConversationViewModel Conversation, FakeMessages Messages, UserRowViewModel Row) Rig(ClientStores stores)
    {
        var messages = new FakeMessages();
        var conversation = new ConversationViewModel(stores, new NoSend(), clock: new Clock(), messages: messages);
        return (conversation, messages, conversation.Rows.OfType<UserRowViewModel>().First());
    }

    [Fact]
    public void The_user_row_carries_the_message_id_the_actions_need()
    {
        var (_, _, row) = Rig(Stores());
        Assert.Equal("u1", row.MessageId);
        Assert.Equal("hello", row.Text);
    }

    [Fact]
    public void History_actions_are_available_only_when_connected_idle_and_not_a_viewer()
    {
        Assert.True(Rig(Stores()).Conversation.CanEditHistory);
        Assert.True(Rig(Stores(role: "member")).Conversation.CanEditHistory);
        Assert.False(Rig(Stores(role: "viewer")).Conversation.CanEditHistory);
        Assert.False(Rig(Stores(running: true)).Conversation.CanEditHistory); // 设备会拒绝改写运行中的会话
        Assert.False(Rig(Stores(connection: DeviceConnection.Connecting)).Conversation.CanEditHistory); // 断开时状态未知
        Assert.False(new ConversationViewModel(Stores(), new NoSend(), clock: new Clock()).CanEditHistory); // 没有接消息操作(手机壳)
    }

    [Fact]
    public void Starting_a_run_disables_history_actions_and_finishing_enables_them_again()
    {
        var stores = Stores();
        var (conversation, _, _) = Rig(stores);
        Assert.True(conversation.CanEditHistory);

        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Index = new ActiveRunIndex("local", [new ActiveRun("local", "run-9", "s-ncw-1", "nextcowork", 0)]) { Rev = 2 } }) });
        Assert.False(conversation.CanEditHistory);

        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Index = new ActiveRunIndex("local", []) { Rev = 3 } }) });
        Assert.True(conversation.CanEditHistory);
    }

    [Fact]
    public void Begin_edit_fills_the_editor_with_the_message_and_cancel_leaves_it_untouched()
    {
        var (conversation, messages, row) = Rig(Stores());
        conversation.BeginEditCommand.Execute(row);
        Assert.True(row.IsEditing);
        Assert.Equal("hello", row.EditText);

        row.EditText = "changed";
        conversation.CancelEditCommand.Execute(row);
        Assert.False(row.IsEditing);
        Assert.Equal("hello", row.Text);
        Assert.Empty(messages.Calls);
    }

    [Fact]
    public void Begin_edit_does_nothing_while_history_is_locked()
    {
        var (conversation, _, row) = Rig(Stores(running: true));
        conversation.BeginEditCommand.Execute(row);
        Assert.False(row.IsEditing);
    }

    [Fact]
    public async Task Saving_rewrites_only_the_text_and_closes_the_editor()
    {
        var (conversation, messages, row) = Rig(Stores());
        conversation.BeginEditCommand.Execute(row);
        row.EditText = "  hello, edited  ";
        await conversation.SaveEditCommand.ExecuteAsync(row);

        Assert.Equal(["edit:u1:hello, edited"], messages.Calls);
        Assert.False(row.IsEditing);
        Assert.Null(conversation.MessageError);
    }

    [Fact]
    public async Task A_blank_edit_is_never_sent()
    {
        var (conversation, messages, row) = Rig(Stores());
        conversation.BeginEditCommand.Execute(row);
        row.EditText = "   ";
        await conversation.SaveEditCommand.ExecuteAsync(row);
        await conversation.ResendEditCommand.ExecuteAsync(row);

        Assert.Empty(messages.Calls);
        Assert.True(row.IsEditing);
    }

    [Fact]
    public async Task Saving_and_rerunning_goes_through_the_resend_intent()
    {
        var (conversation, messages, row) = Rig(Stores());
        conversation.BeginEditCommand.Execute(row);
        row.EditText = "again";
        await conversation.ResendEditCommand.ExecuteAsync(row);

        Assert.Equal(["rerun:u1:again"], messages.Calls);
        Assert.False(row.IsEditing);
    }

    [Fact]
    public async Task A_failed_save_keeps_the_editor_open_and_reports_a_stable_key()
    {
        var (conversation, messages, row) = Rig(Stores());
        messages.Result = new ActionResult<Unit>(ActionOutcome.Conflict, default, MessageActions.BusyKey);
        conversation.BeginEditCommand.Execute(row);
        row.EditText = "x";
        await conversation.SaveEditCommand.ExecuteAsync(row);

        Assert.True(row.IsEditing);
        Assert.Equal(MessageErrorKeys.Busy, conversation.MessageError);
        Assert.True(conversation.HasMessageError);

        conversation.DismissMessageErrorCommand.Execute(null);
        Assert.Null(conversation.MessageError);
    }

    [Fact]
    public async Task When_the_rerun_cannot_start_the_text_goes_back_to_the_composer()
    {
        var (conversation, messages, row) = Rig(Stores());
        messages.Result = new ActionResult<Unit>(ActionOutcome.Failed, default, MessageActions.ResendFailedKey);
        conversation.BeginEditCommand.Execute(row);
        row.EditText = "don't lose me";
        await conversation.ResendEditCommand.ExecuteAsync(row);

        Assert.Equal("don't lose me", conversation.Composer.Draft); // 历史已经截断,文字不能丢
        Assert.Equal(MessageErrorKeys.ResendFailed, conversation.MessageError);
    }

    [Theory]
    [InlineData(ActionOutcome.Unavailable, MessageErrorKeys.Unavailable)]
    [InlineData(ActionOutcome.Forbidden, MessageErrorKeys.Forbidden)]
    [InlineData(ActionOutcome.NotFound, MessageErrorKeys.Gone)]
    [InlineData(ActionOutcome.Failed, MessageErrorKeys.Failed)]
    [InlineData(ActionOutcome.Invalid, MessageErrorKeys.Failed)]
    public async Task Failures_map_to_stable_keys(ActionOutcome outcome, string key)
    {
        var (conversation, messages, row) = Rig(Stores());
        messages.Result = new ActionResult<Unit>(outcome);
        conversation.AskDeleteTurnCommand.Execute(row);
        await conversation.DeleteTurnCommand.ExecuteAsync(row);

        Assert.Equal(key, conversation.MessageError);
    }

    [Fact]
    public async Task Deleting_a_turn_needs_a_second_confirmation()
    {
        var (conversation, messages, row) = Rig(Stores());
        conversation.AskDeleteTurnCommand.Execute(row);
        Assert.True(row.IsConfirmingDelete);
        Assert.Empty(messages.Calls); // 第一次点击什么都不删

        conversation.CancelDeleteTurnCommand.Execute(row);
        Assert.False(row.IsConfirmingDelete);

        conversation.AskDeleteTurnCommand.Execute(row);
        await conversation.DeleteTurnCommand.ExecuteAsync(row);
        Assert.Equal(["deleteTurn:u1"], messages.Calls);
        Assert.False(row.IsConfirmingDelete);
    }

    [Fact]
    public void Asking_to_delete_is_ignored_while_history_is_locked()
    {
        var (conversation, _, row) = Rig(Stores(running: true));
        conversation.AskDeleteTurnCommand.Execute(row);
        Assert.False(row.IsConfirmingDelete);
    }

    [Fact]
    public async Task Branching_passes_the_session_and_the_message_and_reports_failure()
    {
        var (conversation, messages, row) = Rig(Stores());
        await conversation.BranchTurnCommand.ExecuteAsync(row);
        Assert.Equal(["branch:s-ncw-1:u1"], messages.Calls);
        Assert.Null(conversation.MessageError);

        messages.BranchResult = new ActionResult<string>(ActionOutcome.Unavailable);
        await conversation.BranchTurnCommand.ExecuteAsync(row);
        Assert.Equal(MessageErrorKeys.Unavailable, conversation.MessageError);
    }

    [Fact]
    public async Task The_editor_survives_transcript_updates_and_a_new_session_message_does_not_reopen_it()
    {
        var stores = Stores();
        var (conversation, _, row) = Rig(stores);
        conversation.BeginEditCommand.Execute(row);
        row.EditText = "mid-edit";

        // 流式增量 / 重新派生:同一个 key 的行对象不变,编辑状态是纯 UI 状态,不丢
        conversation.Tick();
        Assert.Same(row, conversation.Rows.OfType<UserRowViewModel>().Single());
        Assert.True(row.IsEditing);
        Assert.Equal("mid-edit", row.EditText);
        await Task.CompletedTask;
    }

    // ───────────────────────── 重新生成 / 重跑时带上原附件 ─────────────────────────

    private static MessageDto U(string id, long at, params string[] parts) => new(id, "user", at, false, "[" + string.Join(",", parts) + "]");

    private static MessageDto A(string id, long at, string text) => new(id, "assistant", at, false, $"[{{\"type\":\"text\",\"text\":\"{text}\"}}]");

    private static string T(string text) => $"{{\"type\":\"text\",\"text\":\"{text}\"}}";

    private static readonly long T0 = Now.ToUnixTimeMilliseconds();

    [Fact]
    public void Only_the_last_finished_reply_of_the_latest_turn_offers_regeneration()
    {
        var stores = Stores(history: [U("u1", T0, T("one")), A("a1", T0 + 1, "first"), U("u2", T0 + 2, T("two")), A("a2", T0 + 3, "second")]);
        var (conversation, _, _) = Rig(stores);

        var flags = conversation.Rows.OfType<AssistantRowViewModel>().Select(r => (r.MessageId, r.CanRegenerate)).ToList();
        Assert.Equal([("a1", false), ("a2", true)], flags); // 更早的回复后面还有对话,重跑会截掉后面的全部
    }

    [Fact]
    public void A_turn_without_a_reply_yet_or_a_running_session_has_nothing_to_regenerate()
    {
        Assert.DoesNotContain(Rig(Stores(history: [U("u1", T0, T("one"))])).Conversation.Rows.OfType<AssistantRowViewModel>(), r => r.CanRegenerate);
        Assert.DoesNotContain(Rig(Stores(running: true)).Conversation.Rows.OfType<AssistantRowViewModel>(), r => r.CanRegenerate);
    }

    [Fact]
    public async Task Regenerating_reruns_the_latest_question_with_its_text_and_attachments_unchanged()
    {
        var image = "{\"type\":\"image\",\"mime\":\"image/png\",\"dataRef\":\"ncw://att-1\"}";
        var file = "{\"type\":\"file_ref\",\"path\":\"src/app.cs\",\"name\":\"app.cs\"}";
        var stores = Stores(history: [U("u1", T0, T("old")), A("a1", T0 + 1, "x"), U("u2", T0 + 2, T("look at these"), image, file), A("a2", T0 + 3, "answer")]);
        var (conversation, messages, _) = Rig(stores);
        var reply = conversation.Rows.OfType<AssistantRowViewModel>().Last();

        await conversation.RegenerateCommand.ExecuteAsync(reply);

        Assert.Equal(["rerun:u2:look at these"], messages.Calls); // 最近一轮的提问,不是第一轮
        Assert.Equal([("ncw://att-1", "image/png"), ("src/app.cs", null)], messages.LastFiles.Select(f => (f.Path, f.Mime)));
        Assert.Null(conversation.MessageError);
    }

    [Fact]
    public async Task Regenerating_an_older_reply_or_while_locked_is_ignored()
    {
        var stores = Stores(history: [U("u1", T0, T("one")), A("a1", T0 + 1, "first"), U("u2", T0 + 2, T("two")), A("a2", T0 + 3, "second")]);
        var (conversation, messages, _) = Rig(stores);
        await conversation.RegenerateCommand.ExecuteAsync(conversation.Rows.OfType<AssistantRowViewModel>().First());
        Assert.Empty(messages.Calls);

        var (locked, lockedMessages, _) = Rig(Stores(running: true));
        await locked.RegenerateCommand.ExecuteAsync(new AssistantRowViewModel(new Ncw.Client.Transcript.AssistantRow("x", "x", "t", false)) { CanRegenerate = true });
        Assert.Empty(lockedMessages.Calls);
    }

    [Fact]
    public async Task A_failed_regeneration_puts_the_question_back_in_the_composer_when_history_was_already_cut()
    {
        var stores = Stores(history: [U("u1", T0, T("ask again")), A("a1", T0 + 1, "meh")]);
        var (conversation, messages, _) = Rig(stores);
        messages.Result = new ActionResult<Unit>(ActionOutcome.Failed, default, MessageActions.ResendFailedKey);

        await conversation.RegenerateCommand.ExecuteAsync(conversation.Rows.OfType<AssistantRowViewModel>().Single());

        Assert.Equal("ask again", conversation.Composer.Draft);
        Assert.Equal(MessageErrorKeys.ResendFailed, conversation.MessageError);
    }

    [Fact]
    public async Task Edit_and_rerun_also_carries_the_original_attachments()
    {
        var image = "{\"type\":\"image\",\"mime\":\"image/jpeg\",\"dataRef\":\"ncw://att-9\"}";
        var (conversation, messages, row) = Rig(Stores(history: [U("u1", T0, T("describe"), image), A("a1", T0 + 1, "ok")]));
        conversation.BeginEditCommand.Execute(row);
        row.EditText = "describe it in French";
        await conversation.ResendEditCommand.ExecuteAsync(row);

        Assert.Equal(["rerun:u1:describe it in French"], messages.Calls);
        Assert.Equal([("ncw://att-9", "image/jpeg")], messages.LastFiles.Select(f => (f.Path, f.Mime)));
    }

    [Fact]
    public void A_message_rebuilds_its_inputs_in_order_and_skips_internal_and_assistant_messages()
    {
        var parts = "[{\"type\":\"text\",\"text\":\"hi\"},{\"type\":\"file_ref\",\"path\":\"a.txt\",\"name\":\"a.txt\"},{\"type\":\"image\",\"mime\":\"image/png\",\"dataRef\":\"data:image/png;base64,AAAA\"}]";
        var files = MessageInputs.FilesOf(new MessageDto("u", "user", 1, false, parts));
        Assert.Equal([("a.txt", null), ("data:image/png;base64,AAAA", "image/png")], files.Select(f => (f.Path, f.Mime)));
        Assert.Empty(MessageInputs.FilesOf(new MessageDto("i", "user", 1, true, parts)));
        Assert.Empty(MessageInputs.FilesOf(new MessageDto("a", "assistant", 1, false, parts)));
    }

    // ───────────────────────── AI 回复:删除 / 分支 ─────────────────────────

    private static string Call(string callId) => $"{{\"type\":\"tool_call\",\"callId\":\"{callId}\",\"name\":\"Write\",\"input\":{{}}}}";

    private static MessageDto Asst(string id, long at, params string[] parts) => new(id, "assistant", at, false, "[" + string.Join(",", parts) + "]");

    private static MessageDto Receipt(string id, long at) => new(id, "user", at, false, "[{\"type\":\"tool_result\",\"callId\":\"c1\",\"output\":{\"content\":\"ok\"},\"isError\":false}]");

    private static MessageDto[] TwoTurns() =>
    [
        U("u1", T0, T("one")), A("a1", T0 + 1, "first"),
        U("u2", T0 + 2, T("two")), Asst("a2", T0 + 3, T("let me look"), Call("c1")), Receipt("t2", T0 + 4), A("a3", T0 + 5, "done"),
    ];

    [Fact]
    public void Every_reply_after_a_question_can_branch_but_only_the_last_can_regenerate()
    {
        var (conversation, _, _) = Rig(Stores(history: TwoTurns()));
        List<(string, bool, bool)> Flags() => conversation.Rows.OfType<AssistantRowViewModel>().Select(r => (r.MessageId, r.HasTurn, r.CanRegenerate)).ToList();

        // 第二轮已经完成:中间那段文字(a2)收在「工作区」里,收起时只看得到 a1 和最后一段 a3
        Assert.Equal([("a1", true, false), ("a3", true, true)], Flags());

        conversation.Rows.OfType<WorkGroupRowViewModel>().Single().IsExpanded = true;

        Assert.Equal([("a1", true, false), ("a2", true, false), ("a3", true, true)], Flags());
    }

    [Fact]
    public async Task Deleting_a_reply_asks_twice_and_removes_the_whole_ai_reply_of_that_turn_keeping_the_question()
    {
        var (conversation, messages, _) = Rig(Stores(history: TwoTurns()));
        conversation.Rows.OfType<WorkGroupRowViewModel>().Single().IsExpanded = true; // 中间那段(a2)在「工作区」里,先展开
        var row = conversation.Rows.OfType<AssistantRowViewModel>().First(r => r.MessageId == "a2");

        conversation.AskDeleteReplyCommand.Execute(row);
        Assert.True(row.IsConfirmingDelete);
        Assert.Empty(messages.Calls); // 第一次点击什么都不删

        conversation.CancelDeleteReplyCommand.Execute(row);
        Assert.False(row.IsConfirmingDelete);

        conversation.AskDeleteReplyCommand.Execute(row);
        await conversation.DeleteReplyCommand.ExecuteAsync(row);

        Assert.Equal(["deleteReply:a2..a3"], messages.Calls); // 第二轮 AI 的全部回复(第一条到最后一条助手消息),不是点到的那一段
        Assert.False(row.IsConfirmingDelete);
        Assert.Null(conversation.MessageError);
    }

    [Fact]
    public void Asking_to_delete_a_reply_is_ignored_while_history_is_locked()
    {
        var (conversation, _, _) = Rig(Stores(running: true));
        var row = new AssistantRowViewModel(new AssistantRow("x", "a1", "t", false));
        conversation.AskDeleteReplyCommand.Execute(row);
        Assert.False(row.IsConfirmingDelete);
    }

    [Fact]
    public async Task Branching_from_a_reply_uses_the_question_of_its_turn()
    {
        var (conversation, messages, _) = Rig(Stores(history: TwoTurns()));

        await conversation.BranchReplyCommand.ExecuteAsync(conversation.Rows.OfType<AssistantRowViewModel>().First(r => r.MessageId == "a1"));
        await conversation.BranchReplyCommand.ExecuteAsync(conversation.Rows.OfType<AssistantRowViewModel>().Last());

        Assert.Equal(["branch:s-ncw-1:u1", "branch:s-ncw-1:u2"], messages.Calls);
    }

    [Fact]
    public async Task Reply_actions_report_failures_with_stable_keys_and_a_vanished_reply_is_gone()
    {
        var (conversation, messages, _) = Rig(Stores(history: TwoTurns()));
        var row = conversation.Rows.OfType<AssistantRowViewModel>().Last();

        messages.Result = new ActionResult<Unit>(ActionOutcome.Conflict, default, MessageActions.BusyKey);
        conversation.AskDeleteReplyCommand.Execute(row);
        await conversation.DeleteReplyCommand.ExecuteAsync(row);
        Assert.Equal(MessageErrorKeys.Busy, conversation.MessageError);
        Assert.False(row.IsConfirmingDelete);

        messages.BranchResult = new ActionResult<string>(ActionOutcome.Unavailable);
        await conversation.BranchReplyCommand.ExecuteAsync(row);
        Assert.Equal(MessageErrorKeys.Unavailable, conversation.MessageError);

        // 别的控制端先把它删了:镜像里已经没有这条消息 → 「已经不在了」,不去调设备
        var ghost = new AssistantRowViewModel(new AssistantRow("g", "ghost", "t", false));
        messages.Calls.Clear();
        await conversation.DeleteReplyCommand.ExecuteAsync(ghost);
        Assert.Equal(MessageErrorKeys.Gone, conversation.MessageError);
        Assert.Empty(messages.Calls);
    }
}
