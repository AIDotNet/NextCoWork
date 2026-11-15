using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>
/// <see cref="GitStatus.Parse"/> 的断言,逐条移植自 2.x 的 <c>ipc/__tests__/git-status.test.ts</c> —— 盯的是「暂存区 / 未暂存」这一次分组。
/// 面板上那两个分组完全由 <c>Staged</c> / <c>Unstaged</c> 决定,而这两个布尔值曾经因为把 porcelain v2 的 <c>.</c> 当成「有改动」而双双为真。
/// </summary>
public sealed class GitStatusTests
{
    /// <summary>造一条 porcelain v2 的普通改动记录(<c>1</c> 开头)。</summary>
    private static string Ordinary(string xy, string path) => $"1 {xy} N... 100644 100644 100644 aaaaaaa bbbbbbb {path}";

    private static string Zeroed(params string[] records) => string.Concat(records.Select(static r => r + "\0"));

    [Fact]
    public void Dot_means_no_change_so_a_staged_only_file_is_not_listed_as_unstaged()
    {
        var snapshot = GitStatus.Parse(Zeroed(Ordinary("M.", "src/a.ts"), Ordinary("A.", "src/b.ts")));

        Assert.Equal([("src/a.ts", true, false), ("src/b.ts", true, false)], snapshot.Files.Select(f => (f.Path, f.Staged, f.Unstaged)));
    }

    [Fact]
    public void A_file_staged_and_then_edited_again_stays_in_both_groups()
    {
        var file = GitStatus.Parse(Zeroed(Ordinary("MM", "src/a.ts"))).Files[0];

        Assert.True(file.Staged);
        Assert.True(file.Unstaged);
    }

    [Fact]
    public void A_worktree_only_edit_is_unstaged()
    {
        var file = GitStatus.Parse(Zeroed(Ordinary(".M", "src/a.ts"))).Files[0];

        Assert.False(file.Staged);
        Assert.True(file.Unstaged);
    }

    [Fact]
    public void Branch_upstream_and_ahead_behind_come_from_the_header_records()
    {
        var snapshot = GitStatus.Parse(Zeroed("# branch.oid 5d77615", "# branch.head main", "# branch.upstream origin/main", "# branch.ab +2 -3", Ordinary("M.", "a.ts")));

        Assert.Equal("main", snapshot.Branch);
        Assert.Equal("origin/main", snapshot.Upstream);
        Assert.Equal(2, snapshot.Ahead);
        Assert.Equal(3, snapshot.Behind);
        Assert.False(snapshot.Unborn);
        Assert.False(snapshot.Detached);
    }

    [Fact]
    public void The_rename_source_is_the_next_record_not_part_of_the_same_one()
    {
        var snapshot = GitStatus.Parse(Zeroed("2 R. N... 100644 100644 100644 aaaaaaa bbbbbbb R100 src/new.ts", "src/old.ts", Ordinary("M.", "src/after.ts")));

        Assert.Equal(["src/new.ts", "src/after.ts"], snapshot.Files.Select(f => f.Path));
        Assert.Equal("src/old.ts", snapshot.Files[0].RenamedFrom);
    }

    [Fact]
    public void Untracked_entries_and_AA_and_UU_are_untracked_or_conflicted_and_in_no_group()
    {
        var snapshot = GitStatus.Parse(Zeroed("? src/fresh.ts", "u UU N... 100644 100644 100644 100644 aaa bbb ccc src/conflict.ts", Ordinary("AA", "src/both-added.ts")));

        var (fresh, conflict, bothAdded) = (snapshot.Files[0], snapshot.Files[1], snapshot.Files[2]);
        Assert.Equal([true, false, false], [fresh.Untracked, fresh.Staged, fresh.Unstaged]);
        Assert.True(conflict.Conflicted);
        Assert.True(bothAdded.Conflicted);
        // 冲突的文件不进任何一个分组:面板不给它暂存按钮
        Assert.Equal([false, false], [bothAdded.Staged, bothAdded.Unstaged]);
    }

    [Fact]
    public void Paths_that_contain_spaces_survive()
    {
        var file = GitStatus.Parse(Zeroed(Ordinary("M.", "src/my notes/a b.ts"))).Files[0];

        Assert.Equal("src/my notes/a b.ts", file.Path);
    }

    [Fact]
    public void An_unborn_repository_is_flagged_from_branch_oid()
    {
        Assert.True(GitStatus.Parse(Zeroed("# branch.oid (initial)", "# branch.head main")).Unborn);
    }

    // ───────── 3.0 补充 ─────────

    [Fact]
    public void DD_is_also_a_conflict_and_a_detached_head_has_no_branch_name()
    {
        var snapshot = GitStatus.Parse(Zeroed("# branch.head (detached)", Ordinary("DD", "x.ts")));

        Assert.True(snapshot.Detached);
        Assert.Equal("", snapshot.Branch);
        Assert.True(snapshot.Files[0].Conflicted);
    }

    [Fact]
    public void Chinese_paths_are_read_and_the_V1_space_convention_also_means_no_change()
    {
        var chinese = GitStatus.Parse(Zeroed(Ordinary("M.", "中文/说明 文件.md"))).Files[0];
        Assert.Equal(("中文/说明 文件.md", true, false), (chinese.Path, chinese.Staged, chinese.Unstaged));

        // ' ' 也是「无改动」(防御:v1 风格的 XY 直接交给派生函数)
        var v1 = GitStatus.Change("删除.txt", " ", "D", untracked: false);
        Assert.Equal((false, true), (v1.Staged, v1.Unstaged));
    }

    [Fact]
    public void The_list_is_capped_and_a_rename_record_past_the_cap_still_eats_its_source_record()
    {
        var records = Enumerable.Range(0, GitStatus.MaxFiles).Select(i => Ordinary("M.", $"f{i}.ts")).ToList();
        records.Add("2 R. N... 100644 100644 100644 aaaaaaa bbbbbbb R100 over/new.ts");
        records.Add("over/old.ts");
        records.Add("? tail.ts");

        var snapshot = GitStatus.Parse(Zeroed([.. records]));

        Assert.Equal(GitStatus.MaxFiles, snapshot.Files.Length);
        Assert.True(snapshot.FilesTruncated);
        Assert.DoesNotContain(snapshot.Files, f => f.Path == "over/old.ts");
    }

    [Fact]
    public void The_summary_counts_each_group_once_per_file()
    {
        var snapshot = GitStatus.Parse(Zeroed(Ordinary("M.", "a"), Ordinary("MM", "b"), Ordinary(".M", "c"), "? d", Ordinary("UU", "e")));

        var summary = GitStatusSummary.Of(snapshot.Files);

        Assert.Equal(new GitStatusSummary(5, 2, 2, 1, 1), summary);
        Assert.False(summary.Clean);
        Assert.True(GitStatusSummary.Of([]).Clean);
    }
}
