using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Inspector;

public class ComposerFileRefTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly ProjectRef Project = new("local", "nextcowork");
    private static readonly SessionRef Session = new("local", "s-ncw-1");

    private sealed class Commands : IConversationCommands
    {
        public List<(string Text, InputFile[] Files)> Sent { get; } = [];

        public List<string> Calls { get; } = [];

        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default)
        {
            Sent.Add((text, []));
            return Task.FromResult(new SendResult(SendOutcome.Started));
        }

        public Task<SendResult> SendAsync(SessionRef session, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default)
        {
            Sent.Add((text, [.. files]));
            return Task.FromResult(new SendResult(SendOutcome.Started));
        }

        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> EnqueueAsync(SessionRef session, string text, IReadOnlyList<InputFile> files, SendOptionsDto options, CancellationToken ct = default)
        {
            Calls.Add($"enqueue:{text}:{string.Join(',', files.Select(f => f.Path))}");
            return Task.FromResult(true);
        }

        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default) =>
            Task.FromResult(new InteractionResult(InteractionOutcome.Answered));

        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }

    private sealed class Lookups : IComposerLookups
    {
        public List<string> Queries { get; } = [];

        public bool SuggestFails { get; set; }

        public CommandDto[]? CommandList { get; set; } =
        [
            new("review", "Review the current diff", "Review: $ARGUMENTS", "project", "file", "[path]"),
            new("ship", "Prepare a release", "Ship it", "global", "file", null),
        ];

        public bool ExpandFails { get; set; }

        public Task<IReadOnlyList<FileSuggestionItem>?> SuggestFilesAsync(SessionRef session, string workspaceId, string query, CancellationToken ct)
        {
            Queries.Add(query);
            if (SuggestFails)
            {
                return Task.FromResult<IReadOnlyList<FileSuggestionItem>?>(null);
            }

            string[] all = ["src/Ncw.UI/InspectorPanel.axaml", "src/Ncw.Client/Inspector.cs", "docs/design/03-ui-layout.md"];
            IReadOnlyList<FileSuggestionItem>? hits = [.. all.Where(p => p.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(p => new FileSuggestionItem(p, p[(p.LastIndexOf('/') + 1)..], p[..p.LastIndexOf('/')]))];
            return Task.FromResult<IReadOnlyList<FileSuggestionItem>?>(hits);
        }

        public Task<IReadOnlyList<CommandDto>?> ListCommandsAsync(SessionRef session, string workspaceId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CommandDto>?>(CommandList);

        public Task<string?> ExpandAsync(SessionRef session, string workspaceId, string draft, CancellationToken ct) =>
            Task.FromResult(ExpandFails ? null : CommandExpansion.Apply(draft, (CommandList ?? []).Select(c => (c.Name, c.Prompt))));
    }

    private static ModelOptionDto[] Options() =>
    [
        new("anthropic", "Anthropic", "opus", "Opus", "opus", 200_000, 32_000, true, true, true, true, "effort", ["low", "high"], "anthropic"),
    ];

    private static (ComposerViewModel Composer, Commands Commands, Lookups Lookups) Create()
    {
        var commands = new Commands();
        var lookups = new Lookups();
        var composer = new ComposerViewModel(commands, lookups, suggestDelay: TimeSpan.Zero);
        var meta = new SessionDto("s-ncw-1", "nextcowork", null, "user", "t", null, "opus", null, "code", "auto", false, false, 1, 2);
        composer.Update(Session, meta, null, null, SessionActivity.Idle, DeviceConnection.Connected, "owner", Options());
        return (composer, commands, lookups);
    }

    [Fact]
    public async Task Typing_at_asks_the_device_for_matching_files_and_the_highlight_moves_with_the_keys()
    {
        var (composer, _, lookups) = Create();

        composer.Draft = "look at @Insp";
        await composer.WhenSuggested;

        Assert.Equal(["Insp"], lookups.Queries);
        Assert.True(composer.ShowsSuggestions);
        Assert.Equal(["InspectorPanel.axaml", "Inspector.cs"], composer.FileSuggestions.Select(f => f.Name));
        Assert.True(composer.FileSuggestions[0].IsActive);

        composer.MoveSuggestionCommand.Execute(1);
        Assert.False(composer.FileSuggestions[0].IsActive);
        Assert.True(composer.FileSuggestions[1].IsActive);
        composer.MoveSuggestionCommand.Execute(1); // 到底了回到第一个
        Assert.True(composer.FileSuggestions[0].IsActive);
    }

    [Fact]
    public async Task Accepting_a_suggestion_turns_it_into_a_chip_and_removes_the_at_query_from_the_draft()
    {
        var (composer, _, _) = Create();
        composer.Draft = "look at @Insp";
        await composer.WhenSuggested;
        composer.MoveSuggestionCommand.Execute(1);

        Assert.True(composer.AcceptSuggestion());

        Assert.Equal("look at ", composer.Draft);
        var chip = Assert.Single(composer.FileRefs);
        Assert.Equal(("src/Ncw.Client/Inspector.cs", "Inspector.cs"), (chip.Path, chip.Name));
        Assert.False(composer.ShowsSuggestions);
        Assert.True(composer.HasFileRefs);
    }

    [Fact]
    public async Task Without_candidates_enter_is_not_swallowed_and_a_failure_is_shown_without_blocking_typing()
    {
        var (composer, _, lookups) = Create();
        lookups.SuggestFails = true;

        composer.Draft = "@zzz";
        await composer.WhenSuggested;

        Assert.True(composer.SuggestFailed);
        Assert.False(composer.AcceptSuggestion());
        Assert.False(composer.HasNoSuggestions);

        lookups.SuggestFails = false;
        composer.Draft = "@nomatchanywhere";
        await composer.WhenSuggested;
        Assert.True(composer.HasNoSuggestions);
    }

    [Fact]
    public async Task Send_carries_the_chips_as_file_parts_clears_them_on_success_and_keeps_them_on_failure()
    {
        var (composer, commands, _) = Create();
        composer.Draft = "@Insp";
        await composer.WhenSuggested;
        composer.AcceptSuggestion();
        composer.Draft = "explain this";

        await composer.SendCommand.ExecuteAsync(null);

        var (text, files) = Assert.Single(commands.Sent);
        Assert.Equal("explain this", text);
        Assert.Equal(["src/Ncw.UI/InspectorPanel.axaml"], files.Select(f => f.Path));
        Assert.Empty(composer.FileRefs); // 发出去了:chip 清掉

        // 已经选过的文件不再出现在候选里
        composer.Draft = "@Insp";
        await composer.WhenSuggested;
        Assert.Equal(2, composer.FileSuggestions.Count);
    }

    [Fact]
    public async Task Chips_are_remembered_per_session_and_can_be_removed()
    {
        var (composer, _, _) = Create();
        composer.Draft = "@design";
        await composer.WhenSuggested;
        composer.AcceptSuggestion();
        Assert.Single(composer.FileRefs);

        composer.RemoveFileCommand.Execute(composer.FileRefs[0]);

        Assert.Empty(composer.FileRefs);
        Assert.False(composer.HasFileRefs);
    }

    [Fact]
    public async Task Slash_lists_the_device_commands_with_descriptions_and_keeps_the_builtin_placeholders()
    {
        var (composer, _, _) = Create();

        composer.Draft = "/";
        await Task.Delay(10); // 命令清单是异步取的
        for (var i = 0; i < 50 && !composer.SlashCommands.Any(c => c.IsAvailable); i++)
        {
            await Task.Delay(10);
        }

        var review = composer.SlashCommands.Single(c => c.Command == "/review");
        Assert.True(review.IsAvailable);
        Assert.Equal("Review the current diff", review.Description);
        Assert.Equal("[path]", review.ArgumentHint);
        Assert.Contains(composer.SlashCommands, c => c is { Command: "/compact", IsAvailable: false }); // 内置命令还没接入:占位
        Assert.True(composer.HasSlashQuery);

        composer.Draft = "/rev";
        Assert.Equal(["/review"], composer.SlashCommands.Select(c => c.Command));
        composer.InsertSlashCommand.Execute(composer.SlashCommands[0]);
        Assert.Equal("/review ", composer.Draft);
    }

    [Fact]
    public async Task Sending_a_command_expands_it_first_and_a_failed_expansion_returns_the_draft_instead_of_sending_raw_text()
    {
        var (composer, commands, lookups) = Create();
        composer.Draft = "/review src/a.cs";

        await composer.SendCommand.ExecuteAsync(null);

        Assert.Equal("Review: src/a.cs", Assert.Single(commands.Sent).Text); // 设备收到的永远是普通文本

        lookups.ExpandFails = true;
        composer.Draft = "/ship";
        await composer.SendCommand.ExecuteAsync(null);

        Assert.Single(commands.Sent);                       // 没发
        Assert.Equal("/ship", composer.Draft);              // 草稿还给用户
        Assert.Equal(SendOutcome.Unavailable, composer.LastFailure);
    }

    [Fact]
    public void Without_lookups_the_composer_behaves_as_before()
    {
        var composer = new ComposerViewModel(new Commands());
        var meta = new SessionDto("s-ncw-1", "nextcowork", null, "user", "t", null, "opus", null, "code", "auto", false, false, 1, 2);
        composer.Update(Session, meta, null, null, SessionActivity.Idle, DeviceConnection.Connected, "owner", Options());

        composer.Draft = "@x";

        Assert.True(composer.HasFileQuery);
        Assert.Empty(composer.FileSuggestions);
    }

    [Fact]
    public async Task The_default_conversation_commands_fall_back_to_at_mentions_in_the_text()
    {
        Assert.Equal("see\n@a.cs @b.cs", InputFile.WithMentions("see", [new InputFile("a.cs", "a.cs"), new InputFile("b.cs", "b.cs")]));
        Assert.Equal("plain", InputFile.WithMentions("plain", []));
        await Task.CompletedTask;
    }
}

public class SubagentInspectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }

    private static (ClientStores Stores, InspectorViewModel Inspector) Create()
    {
        var stores = MockWorkspace.Create(Now);
        var inspector = new InspectorViewModel(stores, new FixedClock(Now), MockInspectorServices.Create(stores));
        return (stores, inspector);
    }

    [Fact]
    public async Task An_old_card_rebuilt_from_a_receipt_has_no_child_session_so_opening_says_so_instead_of_doing_nothing()
    {
        var (_, inspector) = Create();
        var card = inspector.Subagents[0]; // 模拟数据里的卡片没有子会话 id
        Assert.False(card.CanOpen);

        await inspector.OpenSubagentCommand.ExecuteAsync(card);

        Assert.Equal(SubagentNotice.NoChildSession, inspector.Notice);
        Assert.Null(inspector.Transcript);

        inspector.DismissNoticeCommand.Execute(null);
        Assert.False(inspector.HasNotice);
    }

    [Fact]
    public async Task Stopping_a_card_with_no_known_run_says_it_cannot_be_stopped()
    {
        var (_, inspector) = Create();

        await inspector.StopSubagentCommand.ExecuteAsync(inspector.Subagents[0]);

        Assert.Equal(SubagentNotice.CannotStop, inspector.Notice);
    }

    [Fact]
    public async Task A_real_card_whose_device_is_not_connected_reports_unavailable_when_opened_and_when_stopped()
    {
        var (stores, inspector) = Create();
        var sessionRecord = new SubagentRecord("s-ncw-1", "k9", "explorer", "look around", SubagentStatus.Running, Now.AddMinutes(-1), null, 3, "Grep")
        {
            ChildRunId = "child-run",
            ChildSessionId = "child-s",
        };
        var entry = new SubagentEntry(new SubagentRef("local", "s-ncw-1", "k9"), sessionRecord, SubagentTelemetry.Empty, 1);
        stores.Conversations.Update(c => c with { Subagents = c.Subagents.SetItem(entry.Ref, entry) });
        var card = inspector.Subagents.Single(s => s.CallId == "k9");
        Assert.True(card.CanOpen && card.CanStop);

        await inspector.OpenSubagentCommand.ExecuteAsync(card);
        Assert.Equal(SubagentNotice.Unavailable, inspector.Notice); // 本机 Agent 没连上:订阅失败

        await inspector.StopSubagentCommand.ExecuteAsync(card);
        Assert.Equal(SubagentNotice.Unavailable, inspector.Notice);
    }

    [Fact]
    public void A_disconnected_device_freezes_the_card_instead_of_counting_on()
    {
        var (stores, inspector) = Create();
        var record = new SubagentRecord("s-ncw-1", "k9", "explorer", "look", SubagentStatus.Running, Now.AddMinutes(-5), null, 3, "Grep") { LastEventAt = Now.AddMinutes(-4) };
        var entry = new SubagentEntry(new SubagentRef("local", "s-ncw-1", "k9"), record, SubagentTelemetry.Empty, 1, Unknown: true);
        stores.Conversations.Update(c => c with { Subagents = c.Subagents.SetItem(entry.Ref, entry) });

        var card = inspector.Subagents.Single(s => s.CallId == "k9");

        Assert.True(card.IsUnknown);
        Assert.False(card.IsActive);
        Assert.Equal(TimeSpan.FromMinutes(1), card.View.Elapsed); // 计时冻结在最后一次听到动静的那一刻
    }

    [Fact]
    public void The_transcript_view_model_builds_the_child_sessions_rows_from_the_store_with_the_shared_derivation()
    {
        var stores = new ClientStores();
        var child = new SessionRef("local", "child-s");
        var meta = new SessionDto("child-s", "nextcowork", "s-ncw-1", "subagent", "child", null, "opus", null, "code", "auto", false, false, 1, 2);
        var messages = new[]
        {
            new MessageDto("u1", "user", 1, false, "[{\"type\":\"text\",\"text\":\"find the bug\"}]"),
            new MessageDto("a1", "assistant", 2, false, "[{\"type\":\"text\",\"text\":\"Found it.\"},{\"type\":\"tool_call\",\"callId\":\"c1\",\"name\":\"Grep\",\"input\":{\"pattern\":\"x\"}}]"),
        };
        var vm = new SubagentTranscriptViewModel(child, "explorer · look", id => id);
        vm.Refresh(stores.Conversations.State, Now, deviceUnknown: false);
        Assert.True(vm.IsLoading); // 子会话的快照还没到

        stores.Conversations.Update(c => ConversationReducer.ApplySession(c, "local",
            new SessionTopicState(new SessionPageDto(meta, messages, false, [], [], []), new QueueDto("child-s", 1, []), null)));
        vm.Refresh(stores.Conversations.State, Now, deviceUnknown: false);

        Assert.False(vm.IsLoading);
        Assert.IsType<UserRowViewModel>(vm.Rows[0]);
        Assert.IsType<AssistantRowViewModel>(vm.Rows[1]);
        Assert.IsType<ToolRowViewModel>(vm.Rows[2]);
        Assert.Equal("Grep", ((ToolRowViewModel)vm.Rows[2]).Name);

        // 再刷新一遍:行按 key 对齐,同一个对象原地更新
        var first = vm.Rows[1];
        vm.Refresh(stores.Conversations.State, Now, deviceUnknown: false);
        Assert.Same(first, vm.Rows[1]);

        vm.Refresh(stores.Conversations.State, Now, deviceUnknown: true);
        Assert.True(vm.IsUnknown);
    }
}
