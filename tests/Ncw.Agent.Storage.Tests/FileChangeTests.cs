using Ncw.Agent.Storage;

namespace Ncw.Agent.Storage.Tests;

/// <summary>
/// 改动集落盘的对账。移植自 2.x 的 <c>db/__tests__/file-changes.test.ts</c>:合并语义(首个 before / 最后一个 after)、超限降级、撤销态翻转、删会话时的级联清理、空改动不落盘;
/// 真文件库、每个用例独立目录(重开库才测得出「落盘之后」)。
/// </summary>
public sealed class FileChangeTests : SessionTestBase
{
    private FileChangeRepository Changes => new(Db);

    public FileChangeTests()
    {
        Workspace("ws");
        NewSession("sess");
    }

    private static FileChangeInput Modify(string path, string before, string after) => new(path, $"/ws/{path}", before, after, "modified", true);

    [Fact]
    public void Aggregates_additions_deletions_and_file_count_and_sorts_files_by_path()
    {
        Changes.Save("run", "run", "sess", "ws",
            [Modify("b.ts", "x", "x\ny"), new FileChangeInput("a.ts", "/ws/a.ts", null, "new\nfile", "created", true)], At);

        var set = Changes.Get("run")!;
        Assert.Equal(2, set.FileCount);
        Assert.Equal(3, set.Additions); // b.ts +1, a.ts +2
        Assert.Equal(0, set.Deletions);
        Assert.Equal(["a.ts", "b.ts"], set.Files.Select(f => f.Path));
        Assert.Equal("created", set.Files[0].ChangeKind);
        Assert.Equal(2, set.Files[0].Additions);
        Assert.Equal("applied", set.State);
    }

    [Fact]
    public void Stacking_parent_and_child_keeps_the_earliest_before_and_advances_to_the_latest_after()
    {
        Changes.Save("run", "child", "sess", "ws", [Modify("a.ts", "v0", "v1")], At);
        Changes.Save("run", "run", "sess", "ws", [Modify("a.ts", "v1", "v2")], At + 1);

        var diff = Changes.GetDiff("run", "a.ts")!;
        Assert.Equal("v0", diff.Before);
        Assert.Equal("v2", diff.After);
        Assert.Equal(1, Changes.Get("run")!.FileCount);
        // 真正写它的是父 Run(后一次叠加)
        Assert.Equal("run", Text("SELECT source_run_id FROM file_snapshots WHERE run_id = 'run' AND file_path = 'a.ts'"));
    }

    [Fact]
    public void Created_is_not_flipped_by_a_later_modification_and_the_hash_follows_the_text()
    {
        Changes.Save("run", "child", "sess", "ws", [new FileChangeInput("n.ts", "/ws/n.ts", null, "one", "created", true)], At);
        Changes.Save("run", "run", "sess", "ws", [Modify("n.ts", "one", "two")], At + 1);

        var row = Changes.ListSnapshots("run").Single();
        Assert.Equal("created", row.ChangeKind);
        Assert.Null(row.Before);
        Assert.Null(row.BeforeHash);
        Assert.Equal(FileChangeRepository.Sha256("two"), row.AfterHash);
        Assert.Equal("", Changes.GetDiff("run", "n.ts")!.Before);
    }

    [Fact]
    public void A_file_over_one_mib_is_oversize_with_no_content_and_no_diff()
    {
        var huge = new string('a', 1024 * 1024 + 1);
        Changes.Save("run", "run", "sess", "ws", [Modify("big.ts", "small", huge)], At);

        Assert.True(Changes.Get("run")!.Files[0].Oversize);
        var diff = Changes.GetDiff("run", "big.ts")!;
        Assert.True(diff.Oversize);
        Assert.Equal("", diff.Before);
        Assert.Equal("", diff.After);
        // 撤销要用的完整行仍在,但内容为 null(内容没入库);hash 仍记
        var snapshot = Changes.ListSnapshots("run")[0];
        Assert.True(snapshot.Oversize);
        Assert.Null(snapshot.Before);
        Assert.Null(snapshot.After);
        Assert.Equal(FileChangeRepository.Sha256(huge), snapshot.AfterHash);
        Assert.Equal(0, Changes.Get("run")!.Additions);
    }

    [Fact]
    public void Once_oversize_a_later_small_save_stays_oversize()
    {
        Changes.Save("run", "child", "sess", "ws", [Modify("big.ts", "small", new string('a', 1024 * 1024 + 1))], At);
        Changes.Save("run", "run", "sess", "ws", [Modify("big.ts", "x", "y")], At + 1);

        Assert.True(Changes.ListSnapshots("run")[0].Oversize);
    }

    [Fact]
    public void The_undone_state_flips_between_applied_and_reverted_and_survives_a_restart()
    {
        Changes.Save("run", "run", "sess", "ws", [Modify("a.ts", "x", "y")], At);
        Assert.Equal("applied", Changes.GetState("run"));

        Changes.SetState("run", "reverted", At + 5);
        Restart();

        var set = Changes.Get("run")!;
        Assert.Equal("reverted", set.State);
        Assert.Equal(At + 5, set.UpdatedAt);
        Assert.Equal(At, set.CreatedAt);
    }

    [Fact]
    public void Deleting_the_session_cascades_to_the_change_set_and_its_snapshots()
    {
        Changes.Save("run", "run", "sess", "ws", [Modify("a.ts", "x", "y")], At);

        Repo.Delete("sess");

        Assert.Null(Changes.Get("run"));
        Assert.Empty(Changes.ListSnapshots("run"));
    }

    [Fact]
    public void An_empty_change_list_is_not_stored()
    {
        Changes.Save("run", "run", "sess", "ws", [], At);

        Assert.Null(Changes.Get("run"));
    }

    [Fact]
    public void Lists_a_sessions_change_sets_newest_first_and_a_missing_one_is_null()
    {
        Changes.Save("r1", "r1", "sess", "ws", [Modify("a.ts", "x", "y")], At);
        Changes.Save("r2", "r2", "sess", "ws", [Modify("b.ts", "x", "y")], At + 10);

        Assert.Equal(["r2", "r1"], Changes.ListBySession("sess").Select(s => s.RunId));
        Assert.Empty(Changes.ListBySession("other"));
        Assert.Null(Changes.GetDiff("r1", "nope.ts"));
        Assert.Null(Changes.GetState("nope"));
    }
}
