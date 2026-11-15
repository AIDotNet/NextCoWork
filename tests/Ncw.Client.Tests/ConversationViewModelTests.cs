using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.Transcript;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>会话列表过滤 / 排序、输入框状态机(发送 / 运行中 / 排队 / 中止)、转录行的稳定性、交互卡片的回答与冲突提示。</summary>
public class ConversationViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly ProjectRef Project = new("local", "nextcowork");
    private static readonly SessionRef Session = new("local", "s-ncw-1");

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>记录调用的假命令:单测里不起设备连接。</summary>
    private sealed class FakeCommands : IConversationCommands
    {
        public List<string> Calls { get; } = [];
        public SendOptionsDto? LastOptions { get; private set; }
        public InteractionResponseDto? LastResponse { get; private set; }
        public SendResult SendResult { get; set; } = new(SendOutcome.Started);
        public bool QueueAccepted { get; set; } = true;
        public InteractionResult RespondResult { get; set; } = new(InteractionOutcome.Answered);

        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default)
        {
            Calls.Add($"send:{text}");
            LastOptions = options;
            return Task.FromResult(SendResult);
        }

        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default)
        {
            Calls.Add($"enqueue:{text}");
            LastOptions = options;
            return Task.FromResult(QueueAccepted);
        }

        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default)
        {
            Calls.Add($"steer:{text}");
            return Task.FromResult(QueueAccepted);
        }

        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default)
        {
            Calls.Add($"abort:{run.RunId}");
            return Task.FromResult(true);
        }

        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default)
        {
            Calls.Add($"promote:{itemId}");
            return Task.FromResult(true);
        }

        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default)
        {
            Calls.Add($"remove:{itemId}");
            return Task.FromResult(true);
        }

        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default)
        {
            Calls.Add($"respond:{interaction.Id}");
            LastResponse = response;
            return Task.FromResult(RespondResult);
        }

        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }

    private static ModelOptionDto[] Options() =>
    [
        new("anthropic", "Anthropic", "opus", "Opus", "opus", 200_000, 32_000, true, true, true, true, "effort", ["low", "high"], "anthropic"),
        new("anthropic", "Anthropic", "haiku", "Haiku", "haiku", 200_000, 16_000, true, true, false, true, null, null, "anthropic"),
    ];

    private static ClientStores Stores(string role = "owner", bool running = false)
    {
        var stores = MockWorkspace.Create(Now);
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem("local", w.Devices["local"] with
            {
                Role = role,
                Index = running ? new ActiveRunIndex("local", [new ActiveRun("local", "run-1", "s-ncw-1", "nextcowork", 0)]) { Rev = 1 } : new ActiveRunIndex("local", []) { Rev = 1 },
            }),
        });
        stores.Providers.Update(_ => new ProvidersMirror(ImmutableDictionary<string, DeviceProviders>.Empty.Add("local",
            DeviceProviders.Unknown with { Known = true, Options = [.. Options()] })));
        stores.Selection.Update(_ => new SelectionState(Project, "s-ncw-1"));
        var meta = new SessionDto("s-ncw-1", "nextcowork", null, "user", "t", null, "opus", null, "code", "auto", false, false, 1, 2);
        stores.Conversations.Update(c => ConversationReducer.ApplySession(c, "local",
            new SessionTopicState(new SessionPageDto(meta, [], false, [], [], []), new QueueDto("s-ncw-1", 1, []), running ? "run-1" : null)));
        return stores;
    }

    private static ConversationViewModel Conversation(ClientStores stores, FakeCommands commands) => new(stores, commands, clock: new Clock());

    // ───────────────────────── 会话列表 ─────────────────────────

    private static SessionSummary S(string id, string title, int minutesAgo, bool archived = false, bool favorited = false) =>
        new(id, Project, title, Now.AddMinutes(-minutesAgo), archived, favorited);

    [Fact]
    public void Session_list_puts_favorites_first_then_newest_and_hides_archived_until_asked()
    {
        var sessions = new[] { S("a", "old", 60), S("b", "new", 1), S("c", "fav", 600, favorited: true), S("d", "gone", 2, archived: true) };

        Assert.Equal(["c", "b", "a"], SessionListQuery.Apply(sessions, null, showArchived: false).Select(s => s.Id));
        Assert.Equal(["c", "b", "a", "d"], SessionListQuery.Apply(sessions, "", showArchived: true).Select(s => s.Id)); // 归档的排最后
    }

    [Fact]
    public void Session_list_search_is_case_insensitive_and_ties_are_stable()
    {
        var sessions = new[] { S("2", "Fix CI", 5), S("1", "fix login", 5), S("3", "docs", 1) };

        Assert.Equal(["1", "2"], SessionListQuery.Apply(sessions, "  FIX ", false).Select(s => s.Id));
    }

    [Fact]
    public void Session_activity_comes_from_the_authoritative_index_and_is_unknown_when_the_device_drops()
    {
        var index = new ActiveRunIndex("local", [new ActiveRun("local", "r", "s1", "w", 0)]);

        Assert.Equal(SessionActivity.Running, SessionActivityView.Of("s1", index, DeviceConnection.Connected));
        Assert.Equal(SessionActivity.Idle, SessionActivityView.Of("s2", index, DeviceConnection.Connected));
        Assert.Equal(SessionActivity.Unknown, SessionActivityView.Of("s1", index, DeviceConnection.Connecting));
        Assert.Equal(SessionActivity.Idle, SessionActivityView.Of("s2", index, DeviceConnection.Offline));
    }

    [Fact]
    public void Project_panel_shows_running_from_active_runs_and_unknown_after_disconnect()
    {
        var stores = Stores(running: true);
        var panel = new ProjectPanelViewModel(stores, new LayoutActions(stores));

        Assert.True(panel.Sessions.Single(s => s.Id == "s-ncw-1").IsRunning);

        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Connecting }) });
        var item = panel.Sessions.Single(s => s.Id == "s-ncw-1");
        Assert.False(item.IsRunning);
        Assert.True(item.IsUnknown); // 既不判成结束,也不继续算运行中
        Assert.True(panel.IsDeviceUnknown);
    }

    [Fact]
    public void Session_list_reducer_replaces_one_project_ignores_older_meta_and_removes()
    {
        var state = WorkspaceState.Empty with { Sessions = [S("keep", "other project", 1) with { Project = new ProjectRef("local", "other") }] };
        state = SessionListReducer.ApplyList(state, Project, [new SessionListItemDto("a", "A", Now.ToUnixTimeMilliseconds(), false, true, false)]);

        Assert.Equal(["keep", "a"], state.Sessions.Select(s => s.Id));
        Assert.True(state.Sessions.Single(s => s.Id == "a").Favorited);

        var older = new SessionDto("a", "nextcowork", null, "user", "renamed", null, "m", null, "code", "auto", true, false, 1, Now.AddHours(-1).ToUnixTimeMilliseconds());
        Assert.Same(state, SessionListReducer.ApplyMeta(state, "local", older)); // 更旧的元数据不倒退
        var newer = older with { UpdatedAt = Now.AddHours(1).ToUnixTimeMilliseconds() };
        Assert.Equal("renamed", SessionListReducer.ApplyMeta(state, "local", newer).Sessions.Single(s => s.Id == "a").Title);
        Assert.DoesNotContain(SessionListReducer.Remove(state, "local", "a").Sessions, s => s.Id == "a");
    }

    // ───────────────────────── 输入框状态机 ─────────────────────────

    [Theory]
    [InlineData("hello", SessionActivity.Idle, DeviceConnection.Connected, "owner", ComposerPrimary.Send)]
    [InlineData("", SessionActivity.Idle, DeviceConnection.Connected, "owner", ComposerPrimary.Disabled)]
    [InlineData("  ", SessionActivity.Idle, DeviceConnection.Connected, "owner", ComposerPrimary.Disabled)]
    [InlineData("", SessionActivity.Running, DeviceConnection.Connected, "member", ComposerPrimary.Stop)]
    [InlineData("more", SessionActivity.Running, DeviceConnection.Connected, "member", ComposerPrimary.Queue)]
    [InlineData("hello", SessionActivity.Unknown, DeviceConnection.Connecting, "owner", ComposerPrimary.Disabled)]
    [InlineData("hello", SessionActivity.Idle, DeviceConnection.Offline, "owner", ComposerPrimary.Disabled)]
    [InlineData("hello", SessionActivity.Idle, DeviceConnection.Connected, "viewer", ComposerPrimary.Disabled)]
    public void Composer_primary_action_follows_draft_activity_connection_and_role(string draft, SessionActivity activity, DeviceConnection connection, string role, ComposerPrimary expected)
    {
        Assert.Equal(expected, ComposerLogic.Of(draft, true, activity, connection, role, hasModel: true).Primary);
    }

    [Fact]
    public void Composer_running_with_text_can_queue_steer_and_stop_but_not_send()
    {
        var state = ComposerLogic.Of("next", true, SessionActivity.Running, DeviceConnection.Connected, "member", true);

        Assert.Equal((false, true, true, true), (state.CanSend, state.CanQueue, state.CanSteer, state.CanStop));
        Assert.False(ComposerLogic.Of("x", true, SessionActivity.Idle, DeviceConnection.Connected, "member", hasModel: false).CanSend);
        Assert.Equal(ComposerBlock.NoModel, ComposerLogic.Of("x", true, SessionActivity.Idle, DeviceConnection.Connected, "member", false).Block);
        Assert.Equal(ComposerBlock.NoSession, ComposerLogic.Of("x", false, SessionActivity.Idle, DeviceConnection.Connected, "owner", true).Block);
    }

    [Fact]
    public void Full_access_is_owner_only_and_thinking_levels_follow_the_model()
    {
        Assert.Equal(["ask", "auto", "full"], ComposerLogic.PermissionModesFor("owner"));
        Assert.Equal(["ask", "auto"], ComposerLogic.PermissionModesFor("member"));
        Assert.Equal("ask", ComposerLogic.ClampPermission("full", "member"));

        Assert.Equal(["auto", "off", "low", "high"], ComposerLogic.ThinkingLevels(Options()[0]));
        Assert.Equal(["off"], ComposerLogic.ThinkingLevels(Options()[1]));
        Assert.Equal("off", ComposerLogic.ClampThinking("high", Options()[1]));
    }

    [Fact]
    public async Task Sending_passes_the_chosen_options_clears_the_draft_and_keeps_it_when_the_device_refuses()
    {
        var commands = new FakeCommands();
        var convo = Conversation(Stores(), commands);
        var composer = convo.Composer;
        composer.SetPermissionCommand.Execute("auto");
        composer.SetThinkingCommand.Execute("high");
        composer.Draft = "  go  ";

        await composer.SendCommand.ExecuteAsync(null);

        Assert.Equal(["send:go"], commands.Calls);
        Assert.Equal(("opus", "high", "auto"), (commands.LastOptions!.Model, commands.LastOptions.Thinking, commands.LastOptions.PermissionMode));
        Assert.Equal("", composer.Draft);

        commands.SendResult = new SendResult(SendOutcome.Unavailable);
        composer.Draft = "keep me";
        await composer.SendCommand.ExecuteAsync(null);
        Assert.Equal("keep me", composer.Draft); // 失败不丢用户写的字
        Assert.Equal(SendOutcome.Unavailable, composer.LastFailure);
    }

    [Fact]
    public async Task While_running_text_is_queued_or_steered_and_empty_submit_stops_the_run()
    {
        var commands = new FakeCommands();
        var convo = Conversation(Stores(running: true), commands);
        var composer = convo.Composer;

        Assert.Equal(ComposerPrimary.Stop, composer.State.Primary);
        await composer.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(["abort:run-1"], commands.Calls);

        composer.Draft = "also fix the readme";
        Assert.Equal(ComposerPrimary.Queue, composer.State.Primary);
        await composer.SendCommand.ExecuteAsync(null); // 运行中不能「发送」:不会开第二个 Run
        await composer.SubmitCommand.ExecuteAsync(null);
        composer.Draft = "right now";
        await composer.SteerCommand.ExecuteAsync(null);

        Assert.Equal(["abort:run-1", "enqueue:also fix the readme", "steer:right now"], commands.Calls);
    }

    [Fact]
    public async Task Queued_items_can_be_promoted_and_removed()
    {
        var commands = new FakeCommands();
        var stores = Stores(running: true);
        var convo = Conversation(stores, commands);
        stores.Conversations.Update(c => ConversationReducer.ApplyQueue(c, "local",
            new QueueDto("s-ncw-1", 5, [new QueueItemDto("q1", [new InputPartDto(InputPartKinds.Text, "later")], QueueStates.Pending, 0, 1, null, new SendOptionsDto("opus"))])));

        var item = Assert.Single(convo.Composer.Queue);
        Assert.Equal("later", item.Text);
        await convo.Composer.PromoteCommand.ExecuteAsync(item);
        await convo.Composer.RemoveQueuedCommand.ExecuteAsync(item);
        Assert.Equal(["promote:q1", "remove:q1"], commands.Calls);
    }

    [Fact]
    public void Composer_is_blocked_while_the_device_is_unknown_and_the_model_picker_lists_candidates()
    {
        var stores = Stores(running: true);
        var convo = Conversation(stores, new FakeCommands());
        Assert.Equal(["Opus", "Haiku"], convo.Composer.Models.Select(m => m.DisplayName));
        Assert.Equal("200K", convo.Composer.Models[0].WindowText);

        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Connecting, Role = null }) });

        Assert.True(convo.IsDeviceUnknown);
        Assert.Equal(ComposerBlock.DeviceUnavailable, convo.Composer.State.Block);
        Assert.False(convo.Composer.State.CanStop); // 状态未知:既不能发送也不能停止
        Assert.False(convo.NeedsTick);
    }

    // ───────────────────────── 转录 ─────────────────────────

    private static MessageDto Assistant(string id, params string[] parts) => new(id, "assistant", 1, false, "[" + string.Join(",", parts) + "]");

    [Fact]
    public void Rows_keep_their_identity_and_ui_state_when_the_store_updates()
    {
        var stores = Stores(running: true);
        var convo = Conversation(stores, new FakeCommands());
        var run = new RunRef("local", "run-1");
        stores.Conversations.Update(c => c with { Runs = c.Runs.SetItem(run, RunView.New(run, "s-ncw-1", 1) with { Phase = RunPhase.Running, Thinking = ImmutableDictionary<string, string>.Empty.Add("m1", "hmm"), Streaming = ImmutableDictionary<string, string>.Empty.Add("m1", "Hel") }) });

        var thinking = Assert.IsType<ThinkingRowViewModel>(convo.Rows[0]);
        thinking.IsExpanded = true;
        var text = Assert.IsType<AssistantRowViewModel>(convo.Rows[1]);
        Assert.Equal("Hel", text.Text);

        // 更多增量:同一个行对象原地更新
        stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, "local", "run-1", 1, new MessageDeltaEventDto("m1", 0, "text", "lo")));
        Assert.Same(text, convo.Rows[1]);
        Assert.Equal("Hello", text.Text);
        Assert.True(thinking.IsExpanded);

        // 落库:流式的行和落库后的行是同一个 key(同一个对象),不闪
        stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, "local", "run-1", 2,
            new MessageEndEventDto(Assistant("m1", "{\"type\":\"thinking\",\"text\":\"hmm\",\"durationMs\":2000}", "{\"type\":\"text\",\"text\":\"Hello world\"}"))));
        Assert.Same(thinking, convo.Rows[0]);
        Assert.Same(text, convo.Rows[1]);
        Assert.Equal("Hello world", text.Text);
        Assert.False(text.IsStreaming);
        Assert.Equal(2, thinking.Seconds);
    }

    [Fact]
    public void Tool_rows_show_status_summary_and_the_execution_environment()
    {
        var stores = Stores();
        var msg = Assistant("m1",
            "{\"type\":\"tool_call\",\"callId\":\"c1\",\"name\":\"Edit\",\"input\":{\"file_path\":\"/a/b.cs\",\"old_string\":\"x\\ny\",\"new_string\":\"z\"}}",
            "{\"type\":\"tool_call\",\"callId\":\"c2\",\"name\":\"Bash\",\"input\":{\"command\":\"dotnet test\\nsecond line\"}}",
            "{\"type\":\"tool_call\",\"callId\":\"c3\",\"name\":\"Bash\",\"input\":{\"command\":\"false\"}}");
        var results = new MessageDto("m2", "user", 2, true,
            "[{\"type\":\"tool_result\",\"callId\":\"c1\",\"output\":{\"content\":\"ok\"},\"isError\":false},{\"type\":\"tool_result\",\"callId\":\"c2\",\"output\":{\"content\":\"line1\\nline2\\nline3\\nline4\\nline5\\nline6\\nline7\\nline8\"},\"isError\":false},{\"type\":\"tool_result\",\"callId\":\"c3\",\"output\":{\"content\":\"exit 1\"},\"isError\":true}]");
        stores.Conversations.Update(c => ConversationReducer.ApplyMessage(ConversationReducer.ApplyMessage(c, Session, msg), Session, results));
        var env = new EnvRefDto("local", "dev", "dev", "ssh");

        var rows = TranscriptBuilder.Build(stores.Conversations.State, Session, env, Now).OfType<ToolRow>().ToDictionary(r => r.CallId);

        Assert.Equal((ToolKind.Edit, "/a/b.cs", 1, 2), (rows["c1"].Kind, rows["c1"].Target, rows["c1"].Added, rows["c1"].Removed));
        Assert.Equal(ToolRowStatus.Done, rows["c2"].Status);
        Assert.Equal("dotnet test", rows["c2"].Target); // 命令只取第一行
        Assert.Equal(TranscriptBuilder.PreviewLineLimit, rows["c2"].Preview.Split('\n').Length);
        Assert.Equal(2, rows["c2"].MoreLines);
        Assert.Equal(ToolRowStatus.Failed, rows["c3"].Status);
        Assert.All(rows.Values, r => Assert.Equal(env, r.Env)); // 落库的调用用项目环境兜底:设备 + 环境
    }

    [Fact]
    public void A_waiting_tool_takes_its_environment_from_the_approval_and_the_card_lists_scopes()
    {
        var stores = Stores(running: true);
        stores.Conversations.Update(c => ConversationReducer.ApplyMessage(c, Session,
            Assistant("m1", "{\"type\":\"tool_call\",\"callId\":\"c1\",\"name\":\"Write\",\"input\":{\"file_path\":\"/x\",\"content\":\"a\"}}")));
        var approval = new ToolPermissionInteractionDto
        {
            Id = "i1", RunId = "run-1", CallId = "c1", ToolName = "Write", InternalId = "write", InputJson = "{\"file_path\":\"/x\"}",
            Env = new EnvRefDto("local", "dev", "dev", "ssh"), AllowedScopes = ["session", "project"], Version = 3,
        };
        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, "local", new InteractionsDto(1, [approval])));

        var rows = TranscriptBuilder.Build(stores.Conversations.State, Session, null, Now);

        var tool = rows.OfType<ToolRow>().Single();
        Assert.Equal(ToolRowStatus.WaitingApproval, tool.Status);
        Assert.Equal("dev", tool.Env?.Environment);
        Assert.IsType<InteractionRow>(rows[^1]);
    }

    // ───────────────────────── 交互 ─────────────────────────

    private static (ConversationViewModel Convo, FakeCommands Commands, InteractionRowViewModel Card) WithApproval()
    {
        var stores = Stores(running: true);
        var commands = new FakeCommands();
        var convo = Conversation(stores, commands);
        var approval = new ToolPermissionInteractionDto
        {
            Id = "i1", RunId = "run-1", CallId = "c1", ToolName = "Bash", InternalId = "bash", InputJson = "{\"command\":\"rm -rf out\"}",
            Env = new EnvRefDto("local", "local", "local", "local"), AllowedScopes = ["session", "project", "global"], Risk = "dangerous", Version = 7,
        };
        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, "local", new InteractionsDto(1, [approval])));
        return (convo, commands, Assert.IsType<InteractionRowViewModel>(convo.Rows.Single(r => r is InteractionRowViewModel)));
    }

    [Fact]
    public async Task Approval_card_answers_allow_once_always_with_scope_and_deny()
    {
        var (_, commands, card) = WithApproval();
        Assert.Equal("本机 · local", card.EnvLabel);
        Assert.True(card.IsDangerous);
        Assert.Equal("rm -rf out", card.Target);

        await card.AllowOnceCommand.ExecuteAsync(null);
        Assert.Equal(PermissionChoices.AllowOnce, Assert.IsType<ToolPermissionResponseDto>(commands.LastResponse).Choice);

        card.SelectedScope = "project";
        await card.AlwaysAllowCommand.ExecuteAsync(null);
        var always = Assert.IsType<ToolPermissionResponseDto>(commands.LastResponse);
        Assert.Equal((PermissionChoices.AllowAlways, "project"), (always.Choice, always.Scope));

        await card.DenyCommand.ExecuteAsync(null);
        Assert.Equal(PermissionChoices.Deny, Assert.IsType<ToolPermissionResponseDto>(commands.LastResponse).Choice);
    }

    [Fact]
    public async Task Answering_an_interaction_another_device_already_handled_shows_who_and_when()
    {
        var (convo, commands, card) = WithApproval();
        var at = Now.AddMinutes(-2).ToUnixTimeMilliseconds();
        commands.RespondResult = new InteractionResult(InteractionOutcome.AlreadyHandled, new ResolutionDto("i1", "run-1", "tool_permission", 7, "answered", "phone-9f3a", at, 2));

        await card.AllowOnceCommand.ExecuteAsync(null);

        var notice = Assert.Single(convo.Notices);
        Assert.Equal(("phone-9f3a", false), (notice.ResolvedBy, notice.Aborted));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(at), notice.At);
        Assert.Null(card.Problem);
    }

    [Fact]
    public void Stale_and_invalid_answers_keep_the_card_and_report_the_problem()
    {
        var (_, _, card) = WithApproval();

        card.ApplyOutcome(new InteractionResult(InteractionOutcome.Stale, null, 9));
        Assert.Equal(InteractionOutcome.Stale, card.Problem);
        card.ApplyOutcome(new InteractionResult(InteractionOutcome.Answered));
        Assert.Null(card.Problem);
    }

    [Fact]
    public async Task Ask_user_card_collects_selected_options_and_freeform_text()
    {
        var stores = Stores(running: true);
        var commands = new FakeCommands();
        var convo = Conversation(stores, commands);
        var ask = new AskUserInteractionDto
        {
            Id = "i2", RunId = "run-1", Version = 4,
            Questions = [new AskUserQuestionDto("H", "Which?", [new AskUserOptionDto("a"), new AskUserOptionDto("b")], MultiSelect: true, AllowFreeform: true)],
        };
        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, "local", new InteractionsDto(1, [ask])));
        var card = convo.Rows.OfType<InteractionRowViewModel>().Single();

        var question = card.Questions[0];
        question.Toggle(question.Options[1]);
        question.Freeform = "  other  ";
        await card.SubmitAnswersCommand.ExecuteAsync(null);

        Assert.Equal(["b", "other"], Assert.Single(Assert.IsType<AskUserResponseDto>(commands.LastResponse).Answers!).Values);
        await card.DismissAskCommand.ExecuteAsync(null);
        Assert.Null(Assert.IsType<AskUserResponseDto>(commands.LastResponse).Answers); // 关掉框不是回答
    }

    // ───────────────────────── 设备断开 ─────────────────────────

    [Fact]
    public void Subagent_cards_freeze_when_the_device_disconnects()
    {
        var stores = Stores(running: true);
        var convo = Conversation(stores, new FakeCommands());
        stores.Conversations.Update(c => ConversationReducer.ApplyMessage(c, Session,
            Assistant("m1", "{\"type\":\"tool_call\",\"callId\":\"k1\",\"name\":\"Task\",\"input\":{\"subagent_type\":\"x\",\"description\":\"d\"}}")));
        var record = new SubagentRecord("s-ncw-1", "k1", "x", "d", SubagentStatus.Running, Now.AddMinutes(-1), null, 3, "Grep") { ChildRunId = "child" };
        var entry = new SubagentEntry(new SubagentRef("local", "s-ncw-1", "k1"), record, SubagentTelemetry.Empty, 1);
        stores.Conversations.Update(c => c with { Subagents = c.Subagents.SetItem(entry.Ref, entry) });
        Assert.True(convo.NeedsTick);
        Assert.True(Assert.IsType<SubagentRowViewModel>(convo.Rows.Single(r => r is SubagentRowViewModel)).IsActive);

        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = DeviceConnection.Offline }) });
        stores.Conversations.Update(c => ConversationReducer.MarkDeviceUnknown(c, "local"));

        var card = convo.Rows.OfType<SubagentRowViewModel>().Single();
        Assert.True(card.IsUnknown);
        Assert.False(card.IsActive);
        Assert.False(card.CanStop);
        Assert.False(convo.NeedsTick);
        Assert.Contains(convo.Rows, r => r is StatusRowViewModel { IsUnknown: true });
    }
}
