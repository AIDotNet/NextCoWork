using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Tests;

/// <summary>
/// 输入框的内置本地动作:<c>/goal</c>、<c>/compact</c>、<c>/model</c>、<c>/clear</c>。它们在提交时被拦下,<b>从不</b>作为文本发给模型(2.x <c>Composer.tsx</c> 的 <c>localActions</c>)。
/// </summary>
public class ComposerSlashTests
{
    private static readonly SessionRef Session = new("local", "s-1");

    private sealed class Commands : IConversationCommands
    {
        public List<string> Sent { get; } = [];

        public List<(string? Instructions, string Model)> Compacts { get; } = [];

        public CompactOutcome CompactResult { get; set; } = CompactOutcome.Done;

        public List<string> Enqueued { get; } = [];

        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default)
        {
            Sent.Add(text);
            return Task.FromResult(new SendResult(SendOutcome.Started));
        }

        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default)
        {
            Enqueued.Add(text);
            return Task.FromResult(true);
        }

        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default) =>
            Task.FromResult(new InteractionResult(InteractionOutcome.Answered));

        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);

        public Task<CompactOutcome> CompactAsync(SessionRef session, string? instructions, SendOptionsDto options, CancellationToken ct = default)
        {
            Compacts.Add((instructions, options.Model));
            return Task.FromResult(CompactResult);
        }
    }

    private sealed class Goals : IGoalCommands
    {
        public List<string> Set { get; } = [];

        public int Clears { get; private set; }

        public ActionOutcome Outcome { get; set; } = ActionOutcome.Ok;

        public Task<ActionResult<GoalSetResultDto>> SetAsync(SessionRef session, string condition, SendOptionsDto? options = null, CancellationToken ct = default)
        {
            Set.Add(condition);
            return Task.FromResult(new ActionResult<GoalSetResultDto>(Outcome, Outcome == ActionOutcome.Ok ? new GoalSetResultDto(GoalOutcomes.Set) : null));
        }

        public Task<ActionResult<Unit>> ClearAsync(SessionRef session, CancellationToken ct = default)
        {
            Clears++;
            return Task.FromResult(new ActionResult<Unit>(Outcome, Outcome == ActionOutcome.Ok ? Unit.Value : null));
        }
    }

    private sealed class Lookups : IComposerLookups
    {
        public Task<IReadOnlyList<FileSuggestionItem>?> SuggestFilesAsync(SessionRef session, string workspaceId, string query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FileSuggestionItem>?>([]);

        // 磁盘上有一条和内置同名的 compact:内置的本地动作说了算,菜单里不重复列
        public Task<IReadOnlyList<CommandDto>?> ListCommandsAsync(SessionRef session, string workspaceId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<CommandDto>?>([
                new CommandDto("review", "Review the current diff", "Review: $ARGUMENTS", "project", "file", "[path]"),
                new CommandDto("compact", "My own compact prompt", "Summarize", "project", "file", null),
            ]);

        public Task<string?> ExpandAsync(SessionRef session, string workspaceId, string draft, CancellationToken ct) => Task.FromResult<string?>(draft);
    }

    private static ModelOptionDto[] Models() =>
    [
        new("anthropic", "Anthropic", "opus", "Claude Opus", "opus", 200_000, 32_000, true, true, true, true, "effort", ["low", "high"], "anthropic"),
        new("anthropic", "Anthropic", "sonnet", "Claude Sonnet", "sonnet", 200_000, 32_000, true, true, true, true, "effort", ["low", "high"], "anthropic"),
        new("deepseek", "DeepSeek", "flash", "DeepSeek Flash", "flash", 128_000, 8_000, false, false, false, true, null, [], "openai"),
    ];

    private static (ComposerViewModel Composer, Commands Commands, Goals Goals) Create(SessionActivity activity = SessionActivity.Idle)
    {
        var commands = new Commands();
        var goals = new Goals();
        var composer = new ComposerViewModel(commands, new Lookups(), suggestDelay: TimeSpan.Zero, goals: goals);
        var meta = new SessionDto("s-1", "ws", null, "user", "t", null, "opus", "anthropic", "code", "auto", false, false, 1, 2);
        composer.Update(Session, meta, null, activity == SessionActivity.Running ? "run-1" : null, activity, DeviceConnection.Connected, "owner", Models());
        return (composer, commands, goals);
    }

    // ───────────────────────── 解析 ─────────────────────────

    [Theory]
    [InlineData("/goal tests pass", SlashAction.Goal, "tests pass")]
    [InlineData("/COMPACT", SlashAction.Compact, "")]
    [InlineData("  /model   sonnet  ", SlashAction.Model, "sonnet")]
    [InlineData("/clear", SlashAction.Clear, "")]
    [InlineData("/compact keep the API decisions\nand the open bugs", SlashAction.Compact, "keep the API decisions\nand the open bugs")]
    public void A_builtin_name_followed_by_whitespace_or_the_end_is_a_local_action(string text, SlashAction action, string args) =>
        Assert.Equal(new SlashInvocation(action, args), ComposerSlash.Parse(text));

    [Theory]
    [InlineData("/goals")]
    [InlineData("/review src")]
    [InlineData("please /compact")]
    [InlineData("")]
    public void Anything_else_is_not_a_local_action(string text) => Assert.Null(ComposerSlash.Parse(text));

    [Fact]
    public void Model_names_match_exactly_first_and_then_by_a_unique_substring()
    {
        var choices = Models().Select(ModelChoice.Of).ToList();
        Assert.Equal("sonnet", ComposerSlash.MatchModel(choices, "Claude Sonnet").Match?.Alias);
        Assert.Equal("opus", ComposerSlash.MatchModel(choices, "OPUS").Match?.Alias);
        Assert.Equal("flash", ComposerSlash.MatchModel(choices, "deepseek/flash").Match?.Alias);
        Assert.Equal("flash", ComposerSlash.MatchModel(choices, "fla").Match?.Alias);
        Assert.Equal((null, 2), ComposerSlash.MatchModel(choices, "claude"));   // 歧义:不替用户猜
        Assert.Equal((null, 0), ComposerSlash.MatchModel(choices, "gemini"));
    }

    // ───────────────────────── 菜单 ─────────────────────────

    private static async Task OpenMenu(ComposerViewModel composer, string draft)
    {
        composer.Draft = draft;
        for (var i = 0; i < 50 && !composer.SlashCommands.Any(c => c.Command == "/review"); i++)
        {
            await Task.Delay(10); // 命令清单是异步取的
        }
    }

    [Fact]
    public async Task The_menu_lists_the_builtins_first_and_hides_a_disk_command_with_the_same_name()
    {
        var (composer, _, _) = Create();
        await OpenMenu(composer, "/");

        Assert.Equal(["/goal", "/compact", "/model", "/clear", "/review"], composer.SlashCommands.Select(c => c.Command));
        Assert.All(composer.SlashCommands, c => Assert.True(c.IsAvailable));
        Assert.Equal(SlashAction.Compact, composer.SlashCommands[1].Action);
        Assert.Null(composer.SlashCommands[4].Action);
        Assert.True(composer.SlashCommands[0].IsActive);
        Assert.Contains("goal", composer.CommandNames); // 草稿开头的 /goal 画成 token

        composer.Draft = "/c";
        Assert.Equal(["/compact", "/clear"], composer.SlashCommands.Select(c => c.Command));
    }

    [Fact]
    public async Task Arrow_keys_move_the_highlight_and_tab_or_enter_complete_it_but_enter_on_a_full_name_submits()
    {
        var (composer, _, _) = Create();
        await OpenMenu(composer, "/c");

        composer.MoveSlash(1);
        Assert.Equal([false, true], composer.SlashCommands.Select(c => c.IsActive));
        Assert.True(composer.AcceptSlash(enter: false));
        Assert.Equal("/clear ", composer.Draft);

        await OpenMenu(composer, "/compact");
        Assert.False(composer.AcceptSlash(enter: true)); // 打全了:Enter 是要执行它
        Assert.True(composer.AcceptSlash(enter: false));
        Assert.Equal("/compact ", composer.Draft);
    }

    // ───────────────────────── /goal ─────────────────────────

    [Fact]
    public async Task Goal_sets_the_condition_on_the_device_and_never_sends_text_to_the_model()
    {
        var (composer, commands, goals) = Create();
        composer.Draft = "/goal all tests pass";

        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(["all tests pass"], goals.Set);
        Assert.Empty(commands.Sent);
        Assert.Equal("", composer.Draft);
        Assert.Null(composer.Notice);
    }

    [Fact]
    public async Task Goal_without_a_condition_explains_itself_and_a_clear_word_clears_the_goal()
    {
        var (composer, _, goals) = Create();
        composer.Draft = "/goal";
        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(SlashNoticeKind.GoalUsage, composer.Notice?.Kind);
        Assert.True(composer.NoticeIsInfo);
        Assert.Equal("/goal ", composer.Draft); // 等着写条件
        Assert.Empty(goals.Set);

        composer.Draft = "/goal stop";
        Assert.Null(composer.Notice); // 改草稿就收起提示
        await composer.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(1, goals.Clears);
        Assert.Equal("", composer.Draft);
    }

    [Fact]
    public async Task A_goal_the_device_refuses_gives_the_draft_back()
    {
        var (composer, _, goals) = Create();
        goals.Outcome = ActionOutcome.Unavailable;
        composer.Draft = "/goal ship it";

        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("/goal ship it", composer.Draft);
        Assert.Equal(SendOutcome.Unavailable, composer.LastFailure);
    }

    [Fact]
    public async Task A_condition_over_the_limit_is_refused_before_it_reaches_the_device()
    {
        var (composer, _, goals) = Create();
        composer.Draft = "/goal " + new string('x', GoalConditions.Max + 1);

        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Empty(goals.Set);
        Assert.Equal(new SlashNotice(SlashNoticeKind.GoalTooLong, GoalConditions.Max.ToString(System.Globalization.CultureInfo.InvariantCulture)), composer.Notice);
        Assert.StartsWith("/goal ", composer.Draft);
    }

    [Fact]
    public async Task Goal_while_a_run_is_going_is_set_now_instead_of_being_queued()
    {
        var (composer, commands, goals) = Create(SessionActivity.Running);
        composer.Draft = "/goal docs updated";
        Assert.Equal(ComposerPrimary.Queue, composer.State.Primary);

        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(["docs updated"], goals.Set);
        Assert.Empty(commands.Enqueued);
    }

    // ───────────────────────── /compact ─────────────────────────

    [Fact]
    public async Task Compact_runs_on_the_device_with_the_focus_text_and_the_current_model()
    {
        var (composer, commands, _) = Create();
        composer.Draft = "/compact keep the protocol decisions";

        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Equal([("keep the protocol decisions", "opus")], commands.Compacts);
        Assert.Empty(commands.Sent);
        Assert.Equal("", composer.Draft);
        Assert.False(composer.IsCompacting);

        composer.Draft = "/compact";
        await composer.SubmitCommand.ExecuteAsync(null);
        Assert.Null(commands.Compacts[^1].Instructions);
    }

    [Theory]
    [InlineData(CompactOutcome.Busy, SlashNoticeKind.CompactBusy, "/compact")]
    [InlineData(CompactOutcome.Failed, SlashNoticeKind.CompactFailed, "/compact")]
    [InlineData(CompactOutcome.NothingToCompact, SlashNoticeKind.CompactNothing, "")]
    public async Task A_compaction_the_device_cannot_do_says_why(CompactOutcome outcome, SlashNoticeKind notice, string draftAfter)
    {
        var (composer, commands, _) = Create();
        commands.CompactResult = outcome;
        composer.Draft = "/compact";

        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(notice, composer.Notice?.Kind);
        Assert.Equal(draftAfter, composer.Draft);
    }

    [Fact]
    public async Task Compact_while_a_run_is_going_is_refused_without_asking_the_device()
    {
        var (composer, commands, _) = Create(SessionActivity.Running);
        composer.Draft = "/compact";

        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Empty(commands.Compacts);
        Assert.Empty(commands.Enqueued);
        Assert.Equal(SlashNoticeKind.CompactBusy, composer.Notice?.Kind);
        Assert.Equal("/compact", composer.Draft);
    }

    // ───────────────────────── /model ─────────────────────────

    [Fact]
    public async Task Model_with_a_name_switches_and_without_one_opens_the_menu()
    {
        var (composer, commands, _) = Create();
        composer.Draft = "/model sonnet";
        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("sonnet", composer.SelectedModel?.Alias);
        Assert.Equal(new SlashNotice(SlashNoticeKind.ModelSwitched, "Claude Sonnet"), composer.Notice);
        Assert.Equal("", composer.Draft);
        Assert.Empty(commands.Sent);

        var opened = 0;
        composer.ModelMenuRequested += () => opened++;
        composer.Draft = "/model";
        await composer.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(1, opened);
        Assert.Equal("", composer.Draft);
    }

    [Theory]
    [InlineData("/model gemini", SlashNoticeKind.ModelNotFound, "gemini")]
    [InlineData("/model claude", SlashNoticeKind.ModelAmbiguous, "claude")]
    [InlineData("/model flash", SlashNoticeKind.ModelUnavailable, "DeepSeek Flash")]
    public async Task A_model_name_that_does_not_pick_one_usable_model_keeps_the_draft(string draft, SlashNoticeKind kind, string arg)
    {
        var (composer, _, _) = Create();
        composer.Draft = draft;

        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(new SlashNotice(kind, arg), composer.Notice);
        Assert.Equal("opus", composer.SelectedModel?.Alias);
        Assert.Equal(draft, composer.Draft);
    }

    // ───────────────────────── /clear ─────────────────────────

    [Fact]
    public async Task Clear_asks_for_a_new_session_and_sends_nothing()
    {
        var (composer, commands, _) = Create();
        var requested = 0;
        composer.NewSessionRequested += () => requested++;
        composer.Draft = "/clear";

        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(1, requested);
        Assert.Empty(commands.Sent);
        Assert.Equal("", composer.Draft);
    }

    [Fact]
    public async Task A_disk_command_that_is_not_a_builtin_still_expands_and_sends()
    {
        var (composer, commands, _) = Create();
        composer.Draft = "/review src/a.cs";

        await composer.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(["/review src/a.cs"], commands.Sent); // 假的展开原样返回:走的是发送这条路
    }
}
