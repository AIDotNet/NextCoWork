using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Inspector;

public class UnifiedDiffTests
{
    private const string Sample = """
        diff --git a/a.cs b/a.cs
        index 111..222 100644
        --- a/a.cs
        +++ b/a.cs
        @@ -3,4 +3,5 @@ class A
         keep1
        -old
        +new1
        +new2
         keep2
        \ No newline at end of file
        @@ -20,2 +21,2 @@
        -x
        +y
         z
        """;

    [Fact]
    public void Parse_reads_hunks_with_line_numbers_and_skips_the_file_header()
    {
        var rows = UnifiedDiff.Parse(Sample);

        Assert.Equal(DiffLineKind.Fold, rows[0].Kind);
        Assert.Equal("@@ -3,4 +3,5 @@ class A", rows[0].Text);
        Assert.Equal((3, 3, DiffLineKind.Context, "keep1"), (rows[1].OldLine, rows[1].NewLine, rows[1].Kind, rows[1].Text));
        Assert.Equal((4, (int?)null, DiffLineKind.Removed), (rows[2].OldLine, rows[2].NewLine, rows[2].Kind));
        Assert.Equal(((int?)null, 4, DiffLineKind.Added), (rows[3].OldLine, rows[3].NewLine, rows[3].Kind));
        Assert.Equal(((int?)null, 5), (rows[4].OldLine, rows[4].NewLine));
        Assert.Equal((5, 6), (rows[5].OldLine, rows[5].NewLine));
        Assert.DoesNotContain(rows, r => r.Text.StartsWith("No newline", StringComparison.Ordinal));
        // 第二个 hunk 的行号从它自己的头里来
        var second = rows.Skip(6).First(r => r.Kind != DiffLineKind.Fold);
        Assert.Equal(20, second.OldLine);
        Assert.Equal((3, 2), UnifiedDiff.Count(rows)); // (新增, 删除)
    }

    [Fact]
    public void Split_pairs_runs_of_removals_and_additions_and_pads_the_shorter_side()
    {
        var rows = UnifiedDiff.Parse(Sample);

        var split = UnifiedDiff.Split(rows);

        Assert.True(split[0].IsHunk);
        Assert.Equal("keep1", split[1].LeftText);
        // -old / +new1 配对,+new2 的左边留空
        Assert.Equal(("old", "new1"), (split[2].LeftText, split[2].RightText));
        Assert.True(split[3].LeftEmpty);
        Assert.Equal("new2", split[3].RightText);
        Assert.True(split[2].LeftRemoved && split[2].RightAdded);
    }

    [Fact]
    public void FromSides_folds_unchanged_lines_into_hunks_and_counts_like_the_core_line_diff()
    {
        var before = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i}")) + "\n";
        var after = before.Replace("line 5\n", "line five\nextra\n", StringComparison.Ordinal).Replace("line 35\n", "", StringComparison.Ordinal);

        var rows = UnifiedDiff.FromSides(before, after);
        var (added, removed) = UnifiedDiff.Count(rows);

        Assert.Equal(LineDiff.Count(before, after), (added, removed)); // 和 Agent 封包变更集时用的是同一个口径
        Assert.Equal(2, rows.Count(r => r.Kind == DiffLineKind.Fold)); // 两处改动之间隔得远:两个 hunk
        Assert.Contains(rows, r => r is { Kind: DiffLineKind.Added, Text: "extra" });
        Assert.Contains(rows, r => r is { Kind: DiffLineKind.Removed, Text: "line 35" });
        Assert.DoesNotContain(rows, r => r.Text == "line 20"); // 远离改动的行被折叠掉了
    }

    [Fact]
    public void FromSides_handles_new_and_deleted_files_and_identical_content()
    {
        Assert.Equal(3, UnifiedDiff.FromSides("", "a\nb\nc\n").Count(r => r.Kind == DiffLineKind.Added));
        Assert.Equal(2, UnifiedDiff.FromSides("a\nb\n", "").Count(r => r.Kind == DiffLineKind.Removed));
        Assert.Empty(UnifiedDiff.FromSides("same\n", "same\n"));
        // CRLF 不算差异
        Assert.Empty(UnifiedDiff.FromSides("a\r\nb\r\n", "a\nb\n"));
    }

    [Fact]
    public void DiffViewModel_reports_binary_oversize_and_empty_without_rows()
    {
        Assert.True(DiffViewModel.FromUnified("a.png", "", binary: true, truncated: false).IsBinary);
        Assert.True(DiffViewModel.FromSides("big.txt", "", "", oversize: true).IsOversize);
        Assert.True(DiffViewModel.FromSides("same.txt", "x\n", "x\n", oversize: false).IsEmpty);
        var vm = DiffViewModel.FromSides("a.txt", "a\n", "b\n", oversize: false);
        Assert.True(vm.HasStats);
        Assert.Equal((1, 1), (vm.Added, vm.Removed));
        Assert.Equal(DiffMode.Unified, vm.Mode);
        vm.SetModeCommand.Execute(DiffMode.Split);
        Assert.True(vm.IsSplit);
    }
}

public class GitGroupTests
{
    // 真实的 porcelain v2:暂存后又改过的文件(MM)、只暂存的(A.)、只改了工作区(.M)、未跟踪、冲突(UU)、重命名
    private const string Porcelain =
        "# branch.oid abc\0# branch.head main\0# branch.upstream origin/main\0# branch.ab +2 -1\0"
        + "1 MM N... 100644 100644 100644 aaa bbb src/Both.cs\0"
        + "1 A. N... 000000 100644 100644 000 ccc src/NewStaged.cs\0"
        + "1 .M N... 100644 100644 100644 ddd ddd src/WorkOnly.cs\0"
        + "1 .D N... 100644 100644 000000 eee eee old.txt\0"
        + "u UU N... 100644 100644 100644 100644 fff ggg hhh src/Conflict.cs\0"
        + "2 R. N... 100644 100644 100644 iii jjj R100 src/Renamed.cs\0src/Before.cs\0"
        + "? notes.txt\0";

    private static GitChangeGroups Groups()
    {
        var snapshot = GitStatus.Parse(Porcelain);
        return GitChangeGroups.Of(snapshot.Files);
    }

    [Fact]
    public void Groups_come_from_the_core_git_status_flags()
    {
        var groups = Groups();

        Assert.Equal(["src/Conflict.cs"], groups.Conflicted.Select(i => i.Path));
        // 暂存区:MM 的文件在这里(看 X 位),A. 和 R. 也在
        Assert.Equal(["src/Both.cs", "src/NewStaged.cs", "src/Renamed.cs"], groups.Staged.Select(i => i.Path));
        // 未暂存:MM 的文件也在这里(看 Y 位),还有 .M 和 .D
        Assert.Equal(["old.txt", "src/Both.cs", "src/WorkOnly.cs"], groups.Unstaged.Select(i => i.Path));
        Assert.Equal(["notes.txt"], groups.Untracked.Select(i => i.Path));
    }

    [Fact]
    public void Each_group_shows_its_own_letter_and_a_file_can_be_in_both_staged_and_unstaged()
    {
        var groups = Groups();

        Assert.Equal("A", groups.Staged.Single(i => i.Path == "src/NewStaged.cs").Letter);
        Assert.True(groups.Staged.Single(i => i.Path == "src/NewStaged.cs").IsAdded);
        Assert.Equal("R", groups.Staged.Single(i => i.Path == "src/Renamed.cs").Letter);
        Assert.Equal("src/Before.cs", groups.Staged.Single(i => i.Path == "src/Renamed.cs").RenamedFrom);
        Assert.Equal("D", groups.Unstaged.Single(i => i.Path == "old.txt").Letter);
        Assert.True(groups.Unstaged.Single(i => i.Path == "old.txt").IsDeleted);
        Assert.Equal("M", groups.Unstaged.Single(i => i.Path == "src/Both.cs").Letter);
        Assert.Equal("U", groups.Untracked.Single().Letter);
        Assert.Equal("!", groups.Conflicted.Single().Letter);
        Assert.True(groups.Staged.Single(i => i.Path == "src/Both.cs").CanUnstage);
        Assert.True(groups.Unstaged.Single(i => i.Path == "src/Both.cs").CanStage);
    }

    [Fact]
    public void The_summary_counts_and_the_groups_agree()
    {
        var snapshot = GitStatus.Parse(Porcelain);
        var summary = GitStatusSummary.Of(snapshot.Files);
        var groups = GitChangeGroups.Of(snapshot.Files);

        Assert.Equal((summary.Staged, summary.Unstaged, summary.Untracked, summary.Conflicted), (groups.Staged.Length, groups.Unstaged.Length, groups.Untracked.Length, groups.Conflicted.Length));
        Assert.Equal(7, summary.Total);
    }

    [Fact]
    public void Tree_markers_prefer_the_worktree_side_and_skip_nothing()
    {
        var dto = new GitOverviewDto(true, null, "/r", "main", Files: [.. GitStatus.Parse(Porcelain).Files.Select(f => new GitFileChangeDto(f.Path, f.Index, f.Worktree, f.Staged, f.Unstaged, f.Untracked, f.Conflicted, f.RenamedFrom))]);

        var markers = GitChangeGroups.TreeMarkers(dto);

        Assert.Equal(GitChange.Untracked, markers["notes.txt"]);
        Assert.Equal(GitChange.Deleted, markers["old.txt"]);
        Assert.Equal(GitChange.Added, markers["src/NewStaged.cs"]);
        Assert.Equal(GitChange.Modified, markers["src/Both.cs"]);
        Assert.Equal(GitChange.Renamed, markers["src/Renamed.cs"]);
        Assert.Equal(7, markers.Count);
        Assert.Empty(GitChangeGroups.TreeMarkers(new GitOverviewDto(false, GitUnavailableReasons.NotRepository)));
    }
}

public class GitPanelTests
{
    private static readonly ProjectRef Project = new("local", "p1");

    private sealed class Dialogs(string? answer) : IDialogService
    {
        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) => Task.FromResult(answer);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;
    }

    private static (GitPanelViewModel Panel, FakeGit Git, ClientStores Stores) Create(string? prompt = null)
    {
        var stores = new ClientStores();
        var git = new FakeGit(stores)
        {
            Overview = new GitOverviewDto(true, null, "/repo", "feature/x", false, false, "origin/feature/x", 2, 1,
            [
                new GitFileChangeDto("a.cs", "M", ".", true, false, false, false),
                new GitFileChangeDto("b.cs", ".", "M", false, true, false, false),
                new GitFileChangeDto("c.txt", "?", "?", false, false, true, false),
            ]),
        };
        var panel = new GitPanelViewModel(stores, git, () => new Dialogs(prompt), () => DefaultInspectorText.Instance);
        panel.Bind(Project);
        return (panel, git, stores);
    }

    [Fact]
    public async Task The_overview_comes_from_the_store_mirror_and_is_unknown_before_the_first_fetch()
    {
        var (panel, _, _) = Create();
        Assert.True(panel.IsUnknown);
        Assert.False(panel.CanOperate);

        await panel.RefreshCommand.ExecuteAsync(null);

        Assert.False(panel.IsUnknown);
        Assert.Equal(("feature/x", 2, 1), (panel.Branch, panel.Ahead, panel.Behind));
        Assert.Equal(["a.cs"], panel.Staged.Select(i => i.Path));
        Assert.Equal(["b.cs"], panel.Unstaged.Select(i => i.Path));
        Assert.Equal(["c.txt"], panel.Untracked.Select(i => i.Path));
        Assert.Equal(3, panel.Summary.Total);
        Assert.Equal(["main", "dev"], panel.Branches.Select(b => b.Name)); // 刷新同时取分支列表
    }

    [Fact]
    public async Task Disconnect_marks_the_mirror_unknown_and_disables_operations_without_forgetting_the_old_values()
    {
        var (panel, _, stores) = Create();
        await panel.RefreshCommand.ExecuteAsync(null);
        Assert.True(panel.CanOperate);

        stores.Git.Update(m => GitReducer.MarkDeviceUnknown(m, "local"));
        panel.Derive();

        Assert.True(panel.IsUnknown);
        Assert.False(panel.CanOperate); // 规则 7:断线时不把旧值当现状,按钮停用
        Assert.Equal("feature/x", panel.Branch);
    }

    [Fact]
    public async Task Stage_and_unstage_call_the_device_then_the_mirror_updates_the_groups()
    {
        var (panel, git, _) = Create();
        await panel.RefreshCommand.ExecuteAsync(null);

        git.Overview = git.Overview with { Files = [new("a.cs", "M", ".", true, false, false, false), new("b.cs", "M", ".", true, false, false, false), new("c.txt", "?", "?", false, false, true, false)] };
        await panel.StageCommand.ExecuteAsync(panel.Unstaged.Single());

        Assert.Contains("stage:b.cs", git.Calls);
        Assert.Equal(["a.cs", "b.cs"], panel.Staged.Select(i => i.Path));
        Assert.Empty(panel.Unstaged);

        await panel.UnstageAllCommand.ExecuteAsync(null);
        Assert.Contains("unstage:a.cs,b.cs", git.Calls);
    }

    [Fact]
    public async Task Stage_all_covers_unstaged_and_untracked_files()
    {
        var (panel, git, _) = Create();
        await panel.RefreshCommand.ExecuteAsync(null);

        await panel.StageAllCommand.ExecuteAsync(null);

        Assert.Contains("stage:b.cs,c.txt", git.Calls);
    }

    [Fact]
    public async Task Commit_needs_staged_files_and_a_message_and_clears_the_draft_on_success()
    {
        var (panel, git, _) = Create();
        await panel.RefreshCommand.ExecuteAsync(null);
        Assert.False(panel.CanCommit); // 有暂存的,但没写信息

        panel.CommitMessage = "  fix: thing  ";
        Assert.True(panel.CanCommit);
        await panel.CommitCommand.ExecuteAsync(null);

        Assert.Contains("commit:fix: thing", git.Calls); // 去掉首尾空白
        Assert.Equal("", panel.CommitMessage);
        Assert.Equal("deadbeef", panel.LastCommitShortHash);
        Assert.Null(panel.Failure);
    }

    [Fact]
    public async Task A_failed_commit_keeps_the_message_and_exposes_the_structured_failure()
    {
        var (panel, git, _) = Create();
        git.CommitFailureKey = GitErrorKeys.NothingStaged;
        await panel.RefreshCommand.ExecuteAsync(null);
        panel.CommitMessage = "msg";

        await panel.CommitCommand.ExecuteAsync(null);

        Assert.Equal("msg", panel.CommitMessage);
        Assert.Equal(GitErrorKeys.NothingStaged, panel.Failure?.Key);
        Assert.Equal("fatal: boom", panel.Failure?.Detail);
        Assert.False(panel.IsBusy);
    }

    [Fact]
    public async Task Generate_message_fills_the_draft_without_committing()
    {
        var (panel, git, _) = Create();
        await panel.RefreshCommand.ExecuteAsync(null);

        await panel.GenerateMessageCommand.ExecuteAsync(null);

        Assert.Equal("feat: generated", panel.CommitMessage);
        Assert.DoesNotContain(git.Calls, c => c.StartsWith("commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Opening_a_diff_asks_for_the_staged_or_worktree_side_by_group_and_parses_it()
    {
        var (panel, git, _) = Create();
        git.Diff = new GitDiffDto("", false, false, false, "@@ -1,1 +1,2 @@\n a\n+b\n");
        await panel.RefreshCommand.ExecuteAsync(null);

        await panel.OpenDiffCommand.ExecuteAsync(panel.Staged.Single());
        Assert.Contains("diff:a.cs:staged", git.Calls);
        Assert.Equal((1, 0), (panel.Diff!.Added, panel.Diff.Removed));
        Assert.True(panel.HasDiff);
        Assert.False(panel.ShowsChanges);

        await panel.OpenDiffCommand.ExecuteAsync(panel.Unstaged.Single());
        Assert.Contains("diff:b.cs:worktree", git.Calls);

        panel.CloseDiffCommand.Execute(null);
        Assert.True(panel.ShowsChanges);
    }

    [Fact]
    public async Task Checkout_create_branch_pull_push_and_history_go_through_the_device()
    {
        var (panel, git, _) = Create("topic");
        await panel.RefreshCommand.ExecuteAsync(null);

        await panel.CheckoutCommand.ExecuteAsync(panel.Branches.Single(b => b.Name == "dev"));
        await panel.CheckoutCommand.ExecuteAsync(panel.Branches.Single(b => b.Current)); // 当前分支:不发请求
        await panel.CreateBranchCommand.ExecuteAsync(null);
        await panel.PullCommand.ExecuteAsync(null);
        await panel.PushCommand.ExecuteAsync(null);
        await panel.ToggleHistoryCommand.ExecuteAsync(null);

        Assert.Equal(["checkout:dev", "createBranch:topic", "pull", "push"], git.Calls.Where(c => c is not "refresh").ToList());
        Assert.True(panel.ShowHistory);
        Assert.Equal("init", Assert.Single(panel.Commits).Subject);
    }

    [Fact]
    public async Task Commit_drafts_are_kept_per_project()
    {
        var (panel, _, _) = Create();
        panel.CommitMessage = "draft A";

        panel.Bind(new ProjectRef("local", "p2"));
        Assert.Equal("", panel.CommitMessage);
        panel.CommitMessage = "draft B";

        panel.Bind(Project);
        Assert.Equal("draft A", panel.CommitMessage);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task An_unavailable_repository_shows_its_reason_and_no_groups()
    {
        var (panel, git, _) = Create();
        git.Overview = new GitOverviewDto(false, GitUnavailableReasons.NotRepository);

        await panel.RefreshCommand.ExecuteAsync(null);

        Assert.False(panel.IsAvailable);
        Assert.False(panel.IsUnknown);
        Assert.Equal(GitUnavailableReasons.NotRepository, panel.UnavailableReason);
        Assert.Empty(panel.Staged);
    }
}

public class ReviewPanelTests
{
    private static ReviewChangeSetDto Set(string runId, string state, long updated, params string[] files) => new(runId, "s1", "p1", state, files.Length, 10, 2,
        [.. files.Select(f => new ReviewFileEntryDto(f, ReviewChangeKinds.Modified, 5, 1, false, true))], updated);

    private static (ReviewPanelViewModel Panel, FakeReview Review, ClientStores Stores) Create(params ReviewChangeSetDto[] sets)
    {
        var stores = new ClientStores();
        var review = new FakeReview { Sets = sets };
        var panel = new ReviewPanelViewModel(stores, review);
        panel.Bind("local", "s1");
        return (panel, review, stores);
    }

    private static void Mirror(ClientStores stores, params ReviewChangeSetDto[] sets) =>
        stores.Review.Update(m => ReviewReducer.ApplyMany(m, "local", sets));

    [Fact]
    public async Task Rounds_come_from_the_mirror_newest_first_and_the_latest_is_selected_by_default()
    {
        var (panel, review, stores) = Create(Set("run-1", ReviewStates.Applied, 100, "a.cs"), Set("run-2", ReviewStates.Applied, 200, "b.cs", "c.cs"));

        await panel.LoadCommand.ExecuteAsync(null); // 取列表;镜像由 Action 更新(这里假实现不写 Store,所以手动合)
        Mirror(stores, review.Sets);
        panel.Derive();

        Assert.Equal(["run-2", "run-1"], panel.Runs.Select(r => r.RunId));
        Assert.Equal("run-2", panel.Selected!.RunId);
        Assert.Equal(["b.cs", "c.cs"], panel.Files.Select(f => f.Path));

        panel.SelectRunCommand.Execute(panel.Runs[1]);
        Assert.Equal(["a.cs"], panel.Files.Select(f => f.Path));
    }

    [Fact]
    public void Other_sessions_and_devices_are_not_mixed_in()
    {
        var (panel, _, stores) = Create();
        Mirror(stores, Set("mine", ReviewStates.Applied, 1, "a.cs"), Set("theirs", ReviewStates.Applied, 2, "z.cs") with { SessionId = "other" });
        stores.Review.Update(m => ReviewReducer.Apply(m, "remote", "r-x", Set("r-x", ReviewStates.Applied, 3, "q.cs")));

        panel.Derive();

        Assert.Equal(["mine"], panel.Runs.Select(r => r.RunId));
    }

    [Fact]
    public async Task Opening_a_file_loads_before_and_after_and_builds_the_diff()
    {
        var (panel, review, stores) = Create();
        Mirror(stores, Set("run-1", ReviewStates.Applied, 1, "a.cs"));
        review.Diffs["a.cs"] = new ReviewFileDiffDto("a.cs", ReviewChangeKinds.Modified, "x\ny\n", "x\nz\n", false);
        panel.Derive();

        await panel.OpenFileCommand.ExecuteAsync(panel.Files.Single());

        Assert.True(panel.HasDiff);
        Assert.Equal((1, 1), (panel.Diff!.Added, panel.Diff.Removed));
        panel.CloseDiffCommand.Execute(null);
        Assert.True(panel.ShowsList);
    }

    [Fact]
    public async Task Undo_without_conflicts_goes_straight_through_and_reports_per_file_results()
    {
        var (panel, review, stores) = Create();
        Mirror(stores, Set("run-1", ReviewStates.Applied, 1, "a.cs", "b.cs"));
        review.NextMutation = new ReviewMutationDto("run-1", ReviewStates.Reverted,
            [new("a.cs", ReviewFileStatuses.Ok), new("b.cs", ReviewFileStatuses.Skipped, "x")]);
        panel.Derive();
        Assert.True(panel.CanUndo);

        await panel.UndoCommand.ExecuteAsync(null);

        Assert.Equal(["precheck", "undo:False"], review.Calls);
        Assert.Equal(new ReviewOutcome(true, 1, 0, 1, 0), panel.LastOutcome);
        Assert.False(panel.LastOutcome!.IsAllOk);
    }

    [Fact]
    public async Task Undo_with_conflicts_waits_for_confirmation_then_forces()
    {
        var (panel, review, stores) = Create();
        Mirror(stores, Set("run-1", ReviewStates.Applied, 1, "a.cs"));
        review.Conflicts = ["a.cs"];
        panel.Derive();

        await panel.UndoCommand.ExecuteAsync(null);

        Assert.Equal(["precheck"], review.Calls); // 预检发现磁盘上又被改过:还没撤销
        Assert.Equal(["a.cs"], panel.PendingConflicts);
        Assert.True(panel.HasPendingConflicts);

        await panel.ForceUndoCommand.ExecuteAsync(null);

        Assert.Equal(["precheck", "undo:True"], review.Calls);
        Assert.False(panel.HasPendingConflicts);
    }

    [Fact]
    public async Task Cancelling_the_conflict_prompt_does_nothing_and_redo_only_exists_for_an_undone_round()
    {
        var (panel, review, stores) = Create();
        Mirror(stores, Set("run-1", ReviewStates.Reverted, 1, "a.cs"));
        review.Conflicts = ["a.cs"];
        panel.Derive();
        Assert.False(panel.CanUndo);
        Assert.True(panel.CanRedo);

        await panel.RedoCommand.ExecuteAsync(null);
        Assert.Equal(["redo"], review.Calls);

        panel.CancelUndoCommand.Execute(null);
        Assert.False(panel.HasPendingConflicts);
    }

    [Fact]
    public void A_disconnected_device_makes_the_state_unknown_and_disables_undo()
    {
        var (panel, _, stores) = Create();
        Mirror(stores, Set("run-1", ReviewStates.Applied, 1, "a.cs"));
        panel.Derive();
        Assert.True(panel.CanUndo);

        stores.Review.Update(m => ReviewReducer.MarkDeviceUnknown(m, "local"));
        panel.Derive();

        Assert.True(panel.IsUnknown);
        Assert.False(panel.CanUndo);
        Assert.Equal(["run-1"], panel.Runs.Select(r => r.RunId)); // 旧值还在,只是不当现状
    }
}
