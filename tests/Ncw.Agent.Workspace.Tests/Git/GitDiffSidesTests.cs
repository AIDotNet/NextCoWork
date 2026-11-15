using Ncw.Agent.Workspace.Git;

namespace Ncw.Agent.Workspace.Tests;

/// <summary>
/// <see cref="GitService.DiffSidesAsync"/> —— 对比视图的两侧全文,在真实的临时仓库上跑(移植自 2.x 的 <c>git-diff-sides.test.ts</c>)。
/// 比较基准必须和 <c>git diff</c> 一致(工作区:index → 工作区;暂存区:HEAD → index),而「这一侧不存在」(新文件、删除、空仓库)必须是空串,不是失败;
/// 没法按全文比的(二进制、冲突)必须是 null,让界面退回 unified 文本。
/// </summary>
public sealed class GitDiffSidesTests
{
    private static async Task<(string Original, string Modified)?> Sides(GitRepo repo, string path, bool staged)
    {
        var sides = await repo.Service().DiffSidesAsync(path, staged);
        return sides is null ? null : (sides.Original, sides.Modified);
    }

    [Fact]
    public async Task Unstaged_compares_index_to_worktree_and_staged_compares_head_to_index()
    {
        using var repo = new GitRepo();
        repo.Project.Write("a.txt", "one\n");
        repo.CommitAll("init");
        repo.Project.Write("a.txt", "two\n");
        repo.Git("add", "a.txt");
        repo.Project.Write("a.txt", "three\n");

        Assert.Equal(("one\n", "two\n"), await Sides(repo, "a.txt", true));
        Assert.Equal(("two\n", "three\n"), await Sides(repo, "a.txt", false));
    }

    [Fact]
    public async Task A_missing_side_is_empty_untracked_unborn_head_deleted()
    {
        using var repo = new GitRepo();
        repo.Project.Write("new.txt", "hello\n");
        Assert.Equal(("", "hello\n"), await Sides(repo, "new.txt", false));

        // 空仓库里暂存的新文件:HEAD 不存在
        repo.Git("add", "new.txt");
        Assert.Equal(("", "hello\n"), await Sides(repo, "new.txt", true));

        repo.Git("commit", "-q", "-m", "init");
        File.Delete(repo.Project.Abs("new.txt"));
        Assert.Equal(("hello\n", ""), await Sides(repo, "new.txt", false));

        repo.Git("rm", "-q", "--cached", "new.txt");
        Assert.Equal(("hello\n", ""), await Sides(repo, "new.txt", true));
    }

    [Fact]
    public async Task Binary_content_is_not_comparable()
    {
        using var repo = new GitRepo();
        File.WriteAllBytes(repo.Project.Abs("bin.dat"), [1, 0, 2, 3]);
        repo.Git("add", "bin.dat");

        Assert.Null(await Sides(repo, "bin.dat", true));
        Assert.Null(await Sides(repo, "bin.dat", false));
    }

    [Fact]
    public async Task A_conflicted_file_is_not_comparable()
    {
        using var repo = new GitRepo();
        repo.Project.Write("c.txt", "base\n");
        repo.CommitAll("base");
        repo.Git("checkout", "-q", "-b", "other");
        repo.Project.Write("c.txt", "other\n");
        repo.Git("commit", "-q", "-am", "other");
        repo.Git("checkout", "-q", "-");
        repo.Project.Write("c.txt", "main\n");
        repo.Git("commit", "-q", "-am", "main");
        repo.GitTry("merge", "-q", "other"); // 冲突时 merge 退出非零,这正是要的状态

        Assert.Null(await Sides(repo, "c.txt", false));
    }

    [Fact]
    public async Task Paths_escaping_the_repository_are_rejected()
    {
        using var repo = new GitRepo();

        var e = await Assert.ThrowsAsync<GitException>(() => repo.Service().DiffSidesAsync("../outside.txt", false));
        Assert.Equal((GitErrorKind.InvalidRequest, "git.invalidPath"), (e.Kind, e.Key));
    }

    // ───────── 3.0 补充 ─────────

    [Fact]
    public async Task A_content_over_the_limit_falls_back_to_the_unified_text()
    {
        using var repo = new GitRepo();
        repo.Project.Write("big.txt", new string('a', 600));
        repo.CommitAll("init");
        repo.Project.Write("big.txt", new string('b', 600));

        var small = repo.Service(options: new GitServiceOptions { MaxSideChars = 500 });
        Assert.Null(await small.DiffSidesAsync("big.txt", false));
        Assert.NotNull(await repo.Service().DiffSidesAsync("big.txt", false));
    }

    [Fact]
    public async Task A_symlink_reads_its_target_text_not_the_file_it_points_at()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");
        using var repo = new GitRepo();
        repo.Project.Write("target.txt", "secret\n");
        File.CreateSymbolicLink(repo.Project.Abs("link"), "target.txt");
        repo.CommitAll("init");
        File.Delete(repo.Project.Abs("link"));
        File.CreateSymbolicLink(repo.Project.Abs("link"), "other.txt");

        var sides = await repo.Service().DiffSidesAsync("link", false);

        Assert.NotNull(sides);
        Assert.Equal("target.txt", sides.Original);
        Assert.Equal("other.txt", sides.Modified);
    }
}
