using Ncw.Agent.Environments;
using Ncw.Agent.Workspace.Review;

namespace Ncw.Agent.Workspace.Tests.Review;

/// <summary>
/// 撤销 / 重做 / 预检。移植自 2.x 的 <c>ipc/review.ts</c> 的行为(2.x 没有直接单测:它依赖 electron 的 store,3.0 的服务层可以直接测)。
/// 真实临时目录 + <see cref="LocalEnvironment"/> + 内存里的改动集存储。
/// </summary>
public sealed class ReviewServiceTests : IDisposable
{
    private readonly TempProject _project = new();
    private readonly FakeChangeStore _store = new();
    private readonly ReviewService _service;
    private readonly ManualTime _time = new();

    public ReviewServiceTests()
    {
        _service = new ReviewService(_store, _time);
    }

    public void Dispose() => _project.Dispose();

    private Task<ReviewMutation> Undo(bool force = false) => _service.UndoAsync(FakeChangeStore.Run, _project.Env, _project.RootPath, force);

    private Task<ReviewMutation> Redo() => _service.RedoAsync(FakeChangeStore.Run, _project.Env, _project.RootPath);

    private Task<IReadOnlyList<string>> Precheck() => _service.PrecheckUndoAsync(FakeChangeStore.Run, _project.Env, _project.RootPath);

    [Fact]
    public async Task Undoing_a_created_file_deletes_it_and_flips_the_state()
    {
        _project.Write("src/new.ts", "hello\n");
        _store.Add("src/new.ts", ReviewChangeKind.Created, null, "hello\n");

        var result = await Undo();

        Assert.Equal(ReviewState.Reverted, result.State);
        Assert.Equal(ReviewFileStatus.Ok, Assert.Single(result.Files).Status);
        Assert.False(_project.Exists("src/new.ts"));
        Assert.Equal(ReviewState.Reverted, _store.State);
        Assert.Equal(_time.GetUtcNow().ToUnixTimeMilliseconds(), _store.UpdatedAt);
    }

    [Fact]
    public async Task Undoing_a_modification_restores_the_before_text()
    {
        _project.Write("a.ts", "v2");
        _store.Add("a.ts", ReviewChangeKind.Modified, "v0", "v2");

        var result = await Undo();

        Assert.Equal(ReviewFileStatus.Ok, result.Files[0].Status);
        Assert.Equal("v0", _project.Read("a.ts"));
    }

    [Fact]
    public async Task A_file_edited_by_hand_afterwards_is_a_conflict_and_left_alone_unless_forced()
    {
        _project.Write("a.ts", "hand edited");
        _store.Add("a.ts", ReviewChangeKind.Modified, "v0", "v2");

        var plain = await Undo();

        Assert.Equal(ReviewFileStatus.Conflict, plain.Files[0].Status);
        Assert.Equal("hand edited", _project.Read("a.ts"));
        // 一个文件都没动:整轮状态不变
        Assert.Equal(ReviewState.Applied, plain.State);
        Assert.Equal(ReviewState.Applied, _store.State);

        var forced = await Undo(force: true);

        Assert.Equal(ReviewFileStatus.Ok, forced.Files[0].Status);
        Assert.Equal("v0", _project.Read("a.ts"));
        Assert.Equal(ReviewState.Reverted, forced.State);
    }

    [Fact]
    public async Task A_file_that_vanished_is_a_conflict_for_undo()
    {
        _store.Add("gone.ts", ReviewChangeKind.Modified, "v0", "v2");

        Assert.Equal(ReviewFileStatus.Conflict, (await Undo()).Files[0].Status);
        Assert.False(_project.Exists("gone.ts"));
    }

    [Fact]
    public async Task Undoing_a_creation_that_is_already_gone_counts_as_done()
    {
        _store.Add("gone.ts", ReviewChangeKind.Created, null, "x");

        var forced = await Undo(force: true);

        Assert.Equal(ReviewFileStatus.Ok, forced.Files[0].Status);
    }

    [Fact]
    public async Task Redo_writes_the_after_text_back_and_recreates_a_deleted_new_file()
    {
        _project.Write("keep.ts", "v2");
        _project.Write("new/deep/n.ts", "created");
        _store.Add("keep.ts", ReviewChangeKind.Modified, "v0", "v2").Add("new/deep/n.ts", ReviewChangeKind.Created, null, "created");
        await Undo();
        Assert.Equal("v0", _project.Read("keep.ts"));
        Assert.False(_project.Exists("new/deep/n.ts"));

        var redo = await Redo();

        Assert.All(redo.Files, f => Assert.Equal(ReviewFileStatus.Ok, f.Status));
        Assert.Equal(ReviewState.Applied, redo.State);
        Assert.Equal(ReviewState.Applied, _store.State);
        Assert.Equal("v2", _project.Read("keep.ts"));
        Assert.Equal("created", _project.Read("new/deep/n.ts"));
    }

    [Fact]
    public async Task Redo_is_forced_even_if_the_file_was_touched()
    {
        _project.Write("a.ts", "something else");
        _store.Add("a.ts", ReviewChangeKind.Modified, "v0", "v2");

        var redo = await Redo();

        Assert.Equal(ReviewFileStatus.Ok, redo.Files[0].Status);
        Assert.Equal("v2", _project.Read("a.ts"));
    }

    [Fact]
    public async Task Oversize_and_outside_workspace_entries_are_skipped_with_their_reason()
    {
        _project.Write("big.ts", "huge");
        File.WriteAllText(Path.Combine(_project.Outside, "o.txt"), "outside");
        _store.Add("big.ts", ReviewChangeKind.Modified, "small", "huge", oversize: true)
            .Add(Path.Combine(_project.Outside, "o.txt"), ReviewChangeKind.Modified, "before", "outside", inWorkspace: false);

        var result = await Undo(force: true);

        Assert.Contains(result.Files, f => f.Path == "big.ts" && f is { Status: ReviewFileStatus.Skipped, Skip: ReviewSkipReason.Oversize });
        Assert.Contains(result.Files, f => f is { Status: ReviewFileStatus.Skipped, Skip: ReviewSkipReason.OutsideWorkspace });
        Assert.Equal("huge", _project.Read("big.ts"));
        Assert.Equal("outside", File.ReadAllText(Path.Combine(_project.Outside, "o.txt")));
        Assert.Equal(ReviewState.Applied, result.State);
    }

    [Fact]
    public async Task Precheck_lists_only_the_files_that_no_longer_match_the_record()
    {
        _project.Write("same.ts", "v2");
        _project.Write("edited.ts", "mine");
        _store.Add("same.ts", ReviewChangeKind.Modified, "v0", "v2")
            .Add("edited.ts", ReviewChangeKind.Modified, "v0", "v2")
            .Add("gone.ts", ReviewChangeKind.Created, null, "x")
            .Add("big.ts", ReviewChangeKind.Modified, "a", "b", oversize: true)
            .Add("/somewhere/else.ts", ReviewChangeKind.Modified, "a", "b", inWorkspace: false);

        Assert.Equal(["edited.ts", "gone.ts"], await Precheck());
    }

    [Fact]
    public async Task Unknown_runs_have_nothing_to_undo_or_precheck()
    {
        var undo = await _service.UndoAsync("nope", _project.Env, _project.RootPath, force: true);

        Assert.Empty(undo.Files);
        Assert.Equal(ReviewState.Applied, undo.State);
        Assert.Empty(await _service.PrecheckUndoAsync("nope", _project.Env, _project.RootPath));
    }

    [Fact]
    public async Task A_recorded_path_that_climbs_out_of_the_project_is_never_touched()
    {
        var victim = Path.Combine(_project.Outside, "victim.txt");
        File.WriteAllText(victim, "keep me");
        _store.Add("../outside/victim.txt", ReviewChangeKind.Created, null, "keep me");

        var result = await Undo(force: true);

        Assert.Equal(ReviewSkipReason.OutsideWorkspace, result.Files[0].Skip);
        Assert.Equal("keep me", File.ReadAllText(victim));
        Assert.Empty(await Precheck());
    }

    [Fact]
    public async Task An_absolute_recorded_path_outside_the_project_is_never_touched()
    {
        var victim = Path.Combine(_project.Outside, "abs.txt");
        File.WriteAllText(victim, "keep me");
        _store.Add(victim, ReviewChangeKind.Created, null, "keep me"); // 被篡改成「项目内」的绝对路径

        var result = await Undo(force: true);

        Assert.Equal(ReviewFileStatus.Skipped, result.Files[0].Status);
        Assert.Equal("keep me", File.ReadAllText(victim));
    }

    [Fact]
    public async Task A_symlink_inside_the_project_that_points_outside_is_never_followed()
    {
        var victim = Path.Combine(_project.Outside, "linked.txt");
        File.WriteAllText(victim, "keep me");
        try
        {
            Directory.CreateSymbolicLink(_project.Abs("link"), _project.Outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symbolic links are not available here");
        }

        _store.Add("link/linked.txt", ReviewChangeKind.Created, null, "keep me");

        var result = await Undo(force: true);

        Assert.Equal(ReviewSkipReason.OutsideWorkspace, result.Files[0].Skip);
        Assert.Equal("keep me", File.ReadAllText(victim));
    }

    [Fact]
    public async Task A_write_back_failure_is_an_error_entry_for_that_file_only()
    {
        _project.Write("ok.ts", "v2");
        Directory.CreateDirectory(_project.Abs("dir.ts")); // 记录里是个文件,现在是个目录
        _store.Add("ok.ts", ReviewChangeKind.Modified, "v0", "v2").Add("dir.ts", ReviewChangeKind.Created, null, "x");

        var plain = await Undo();
        Assert.Equal(ReviewFileStatus.Conflict, plain.Files.Single(f => f.Path == "dir.ts").Status); // 目录读不了 = 和记录不符

        var forced = await Undo(force: true);

        var failed = forced.Files.Single(f => f.Path == "dir.ts");
        Assert.Equal(ReviewFileStatus.Error, failed.Status);
        Assert.StartsWith("environment:", failed.Error);
        Assert.DoesNotContain(_project.Root, failed.Error);
        // 另一个文件照常回写,整轮状态翻转
        Assert.Equal("v0", _project.Read("ok.ts"));
        Assert.Equal(ReviewState.Reverted, forced.State);
    }

    [Fact]
    public async Task A_utf8_bom_on_the_current_file_survives_the_write_back_and_does_not_break_the_hash()
    {
        var path = _project.Abs("bom.ts");
        File.WriteAllBytes(path, [0xEF, 0xBB, 0xBF, .. "v2"u8.ToArray()]);
        _store.Add("bom.ts", ReviewChangeKind.Modified, "v0", "v2");

        var result = await Undo();

        Assert.Equal(ReviewFileStatus.Ok, result.Files[0].Status);
        Assert.Equal<byte[]>([0xEF, 0xBB, 0xBF, .. "v0"u8.ToArray()], File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_file_that_is_not_valid_utf8_is_treated_as_changed()
    {
        File.WriteAllBytes(_project.Abs("bin.dat"), [0xFF, 0xFE, 0x00, 0x80]);
        _store.Add("bin.dat", ReviewChangeKind.Modified, "v0", "v2");

        Assert.Equal(["bin.dat"], await Precheck());
        Assert.Equal(ReviewFileStatus.Conflict, (await Undo()).Files[0].Status);
    }

    [Fact]
    public void Recording_a_run_forwards_to_the_store_and_empty_changes_are_not_stored()
    {
        _service.RecordRun("root", "child", "sess", "ws", 5, [new RecordedChange("a.ts", "/ws/a.ts", null, "x", ReviewChangeKind.Created, true)]);
        _service.RecordRun("root", "root", "sess", "ws", 6, []);

        Assert.Equal(["root/child/sess/ws/1"], _store.Saves);
    }

    [Fact]
    public void Reads_pass_through_to_the_store()
    {
        _store.Add("a.ts", ReviewChangeKind.Modified, "v0", "v2");

        Assert.Equal("v0", _service.FileDiff(FakeChangeStore.Run, "a.ts")!.Before);
        Assert.Null(_service.FileDiff(FakeChangeStore.Run, "zzz"));
        Assert.Equal(FakeChangeStore.Run, _service.ChangeSet(FakeChangeStore.Run)!.RunId);
        Assert.Single(_service.List("sess"));
    }

    private sealed class ManualTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2025, 1, 2, 3, 4, 5, TimeSpan.Zero);
    }
}
