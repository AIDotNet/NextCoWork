using Ncw.Agent.Environments;
using Ncw.Agent.Workspace.Git;

namespace Ncw.Agent.Workspace.Tests;

/// <summary>
/// <see cref="GitService"/> 的行为,在真实的临时仓库上跑,逐条覆盖 2.x <c>ipc/git.ts</c> 的各个分支,并移植 <c>git-methods.test.ts</c> 的链路
/// (概览 / 分支 / 历史 → diff → 暂存 → 提交 → 取消暂存;建分支 / 切分支;flag 注入名与不安全路径在碰 git 之前被拒;失败如实回传 stderr)。
/// </summary>
public sealed class GitServiceTests
{
    private static GitRepo InitialRepo()
    {
        var repo = new GitRepo();
        repo.Project.Write("a.txt", "one\n");
        repo.CommitAll("initial");
        return repo;
    }

    private static async Task<GitException> Fails(Func<Task> action) => await Assert.ThrowsAsync<GitException>(action);

    // ───────── 概览 / 分支 / 历史 ─────────

    [Fact]
    public async Task Overview_branches_and_history_read_a_fresh_repository()
    {
        using var repo = InitialRepo();
        var git = repo.Service();

        var overview = await git.OverviewAsync();
        Assert.True(overview.Available);
        Assert.Equal(repo.Root, overview.Root);
        Assert.Equal("main", overview.Status!.Branch);
        Assert.Empty(overview.Status.Files);

        var branches = await git.BranchesAsync();
        Assert.Equal([("main", true)], branches.Select(b => (b.Name, b.Current)));

        var commits = await git.CommitsAsync(5);
        var only = Assert.Single(commits);
        Assert.Equal("initial", only.Subject);
        Assert.Equal(7, only.ShortHash.Length >= 7 ? 7 : only.ShortHash.Length);
        Assert.True(only.Timestamp > 1_600_000_000_000);
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_is_unavailable_rather_than_an_error()
    {
        GitAvailability.RequireGit();
        using var project = new TempProject();
        var git = new GitService(project.Env, project.RootPath);

        var overview = await git.OverviewAsync();

        Assert.False(overview.Available);
        Assert.Equal(GitUnavailableReason.NotRepository, overview.Reason);
        Assert.Empty(await git.CommitsAsync());
        Assert.Empty(await git.BranchesAsync());
        var e = await Fails(() => git.StageAsync(["a.txt"]));
        Assert.Equal((GitErrorKind.NotAvailable, "git.unavailable.notRepository"), (e.Kind, e.Key));
    }

    [Fact]
    public async Task A_project_root_that_no_longer_exists_is_unavailable()
    {
        GitAvailability.RequireGit();
        using var project = new TempProject();
        var git = new GitService(project.Env, project.RootPath.Join("gone"));

        var overview = await git.OverviewAsync();

        Assert.False(overview.Available);
        Assert.Equal(GitUnavailableReason.WorkspaceUnavailable, overview.Reason);
    }

    [Fact]
    public async Task A_missing_git_executable_is_reported_as_git_missing()
    {
        using var project = new TempProject();
        var git = new GitService(new NoGitEnvironment(project.Env), project.RootPath);

        Assert.Equal(GitUnavailableReason.GitMissing, (await git.OverviewAsync()).Reason);
        var e = await Fails(() => git.CommitAsync("x"));
        Assert.Equal("git.unavailable.gitMissing", e.Key);
    }

    [Fact]
    public async Task An_unborn_repository_lists_no_commits_and_overview_says_unborn()
    {
        using var repo = new GitRepo();
        repo.Project.Write("a.txt", "x\n");

        var git = repo.Service();
        var overview = await git.OverviewAsync();

        Assert.True(overview.Status!.Unborn);
        Assert.Empty(await git.CommitsAsync());
        Assert.Single(overview.Status.Files);
        Assert.True(overview.Status.Files[0].Untracked);
    }

    // ───────── diff → 暂存 → 提交 → 取消暂存 ─────────

    [Fact]
    public async Task A_change_walks_through_diff_stage_commit_and_unstage()
    {
        using var repo = InitialRepo();
        var git = repo.Service();

        repo.Project.Write("a.txt", "one\ntwo\n");
        Assert.Contains("+two", (await git.DiffAsync("a.txt", staged: false)).Text);

        await git.StageAsync(["a.txt"]);
        Assert.Contains("+two", (await git.DiffAsync("a.txt", staged: true)).Text);
        var summary = (await git.OverviewAsync()).Status!.Files;
        Assert.True(summary[0].Staged && !summary[0].Unstaged);

        var commit = await git.CommitAsync("add two");
        Assert.Equal("add two", commit.Subject);
        Assert.Equal("add two", repo.Git("log", "-1", "--format=%s").Trim());

        repo.Project.Write("a.txt", "changed\n");
        await git.StageAsync(["a.txt"]);
        await git.UnstageAsync(["a.txt"]);
        Assert.Equal("", repo.Git("diff", "--cached", "--name-only").Trim());
    }

    [Fact]
    public async Task Chinese_and_spaced_paths_and_glob_characters_are_staged_literally()
    {
        using var repo = InitialRepo();
        repo.Project.Write("中文/说明 文件.md", "你好\n");
        repo.Project.Write("a[1].txt", "glob\n");
        repo.Project.Write("a1.txt", "decoy\n");
        var git = repo.Service();

        await git.StageAsync(["中文/说明 文件.md", "a[1].txt"]);

        var staged = repo.Git("diff", "--cached", "--name-only", "-z").Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["a[1].txt", "中文/说明 文件.md"], staged.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("a1.txt", staged); // [1] 没有被当成通配
        var files = (await git.OverviewAsync()).Status!.Files;
        Assert.Contains(files, f => f.Path == "中文/说明 文件.md" && f.Staged);
    }

    [Fact]
    public async Task Deletions_can_be_staged_and_the_diff_shows_them()
    {
        using var repo = InitialRepo();
        File.Delete(repo.Project.Abs("a.txt"));
        var git = repo.Service();

        await git.StageAsync(["a.txt"]);

        Assert.Contains("-one", (await git.DiffAsync("a.txt", staged: true)).Text);
    }

    [Fact]
    public async Task A_project_in_a_subdirectory_resolves_repo_relative_paths_against_the_repo_root()
    {
        using var repo = InitialRepo();
        repo.Project.Write("pkg/sub/a.txt", "x\n");
        repo.CommitAll("pkg");
        repo.Project.Write("pkg/sub/a.txt", "x\ny\n");
        var git = repo.Service("pkg/sub"); // 在子目录里打开项目是常态:porcelain 给的是仓库根相对路径

        var overview = await git.OverviewAsync();
        Assert.Equal(repo.Root, overview.Root);
        var file = Assert.Single(overview.Status!.Files);
        Assert.Equal("pkg/sub/a.txt", file.Path);

        Assert.Contains("+y", (await git.DiffAsync(file.Path, staged: false)).Text);
        await git.StageAsync([file.Path]);
        Assert.Contains("+y", (await git.DiffAsync(file.Path, staged: true)).Text);
        await git.UnstageAsync([file.Path]);
        Assert.Equal("", repo.Git("diff", "--cached", "--name-only").Trim());
        Assert.NotNull(await git.DiffSidesAsync(file.Path, staged: false));
    }

    [Fact]
    public async Task Unstaging_in_an_unborn_repository_removes_the_index_entry()
    {
        using var repo = new GitRepo();
        repo.Project.Write("a.txt", "x\n");
        var git = repo.Service();
        await git.StageAsync(["a.txt"]);
        Assert.Equal("a.txt", repo.Git("diff", "--cached", "--name-only").Trim());

        await git.UnstageAsync(["a.txt"]);

        Assert.Equal("", repo.Git("diff", "--cached", "--name-only").Trim());
        Assert.Empty(await git.CommitsAsync());
    }

    [Fact]
    public async Task The_first_commit_in_an_unborn_repository_works()
    {
        using var repo = new GitRepo();
        repo.Project.Write("a.txt", "x\n");
        var git = repo.Service();
        await git.StageAsync(["a.txt"]);

        var commit = await git.CommitAsync("first");

        Assert.Equal("first", commit.Subject);
        Assert.Single(await git.CommitsAsync());
    }

    // ───────── 未跟踪 / 二进制 / 截断 ─────────

    [Fact]
    public async Task An_untracked_file_gets_a_hand_built_unified_diff()
    {
        using var repo = InitialRepo();
        repo.Project.Write("new.txt", "l1\nl2\n");

        var diff = await repo.Service().DiffAsync("new.txt", staged: false);

        Assert.Equal("--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1,2 @@\n+l1\n+l2", diff.Text);
        Assert.False(diff.Binary);
        Assert.False(diff.Truncated);
    }

    [Fact]
    public async Task An_empty_untracked_file_has_a_header_and_no_lines()
    {
        using var repo = InitialRepo();
        repo.Project.Write("empty.txt", "");

        var diff = await repo.Service().DiffAsync("empty.txt", staged: false);

        Assert.Equal("--- /dev/null\n+++ b/empty.txt\n@@ -0,0 +1,0 @@\n", diff.Text);
    }

    [Fact]
    public async Task An_untracked_binary_file_is_flagged_binary_with_no_text()
    {
        using var repo = InitialRepo();
        File.WriteAllBytes(repo.Project.Abs("b.dat"), [1, 2, 0, 3]);

        var diff = await repo.Service().DiffAsync("b.dat", staged: false);

        Assert.True(diff.Binary);
        Assert.Equal("", diff.Text);
    }

    [Fact]
    public async Task A_tracked_binary_diff_is_detected_from_the_git_output()
    {
        using var repo = InitialRepo();
        File.WriteAllBytes(repo.Project.Abs("b.dat"), [1, 2, 0, 3]);
        repo.CommitAll("bin");
        File.WriteAllBytes(repo.Project.Abs("b.dat"), [9, 9, 0, 3]);

        var diff = await repo.Service().DiffAsync("b.dat", staged: false);

        Assert.True(diff.Binary);
        Assert.Equal("", diff.Text);
    }

    [Fact]
    public async Task A_huge_diff_is_truncated_and_says_so()
    {
        using var repo = InitialRepo();
        repo.Project.Write("a.txt", string.Concat(Enumerable.Range(0, 400).Select(i => $"line {i}\n")));
        var git = repo.Service(options: new GitServiceOptions { MaxDiffChars = 1000 });

        var tracked = await git.DiffAsync("a.txt", staged: false);
        Assert.True(tracked.Truncated);
        Assert.Equal(1000, tracked.Text.Length);

        repo.Project.Write("huge.txt", string.Concat(Enumerable.Range(0, 400).Select(i => $"line {i}\n")));
        var untracked = await git.DiffAsync("huge.txt", staged: false);
        Assert.True(untracked.Truncated);
    }

    [Fact]
    public async Task An_untracked_symlink_is_not_followed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symlinks need privileges on Windows");
        using var repo = InitialRepo();
        File.WriteAllText(Path.Combine(repo.Project.Outside, "secret.txt"), "TOP-SECRET\n");
        File.CreateSymbolicLink(repo.Project.Abs("link.txt"), Path.Combine(repo.Project.Outside, "secret.txt"));

        var diff = await repo.Service().DiffAsync("link.txt", staged: false);

        Assert.DoesNotContain("TOP-SECRET", diff.Text);
        Assert.Contains(Path.Combine(repo.Project.Outside, "secret.txt"), diff.Text); // git 存的是链接目标的文本
    }

    // ───────── 分支 ─────────

    [Fact]
    public async Task Branches_are_created_and_switched()
    {
        using var repo = InitialRepo();
        var git = repo.Service();

        var created = await git.CreateBranchAsync("feature/x", checkout: true);
        Assert.Equal(("feature/x", true), (created.Name, created.Current));
        Assert.Equal("feature/x", repo.Git("branch", "--show-current").Trim());

        await git.CheckoutAsync("main");
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());

        var plain = await git.CreateBranchAsync("topic", checkout: false);
        Assert.False(plain.Current);
        Assert.Equal(["feature/x", "main", "topic"], (await git.BranchesAsync()).Select(b => b.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("--detach")]
    [InlineData("-f")]
    [InlineData("a b")]
    [InlineData("x~1")]
    [InlineData("x^")]
    [InlineData("a:b")]
    [InlineData("x*")]
    [InlineData("")]
    public async Task Option_like_or_malformed_refs_are_rejected_before_git_runs(string name)
    {
        using var repo = InitialRepo();
        var git = repo.Service();

        var e = await Fails(() => git.CheckoutAsync(name));

        Assert.Equal((GitErrorKind.InvalidRequest, "git.invalidBranch"), (e.Kind, e.Key));
        Assert.Equal("main", repo.Git("branch", "--show-current").Trim());
        await Fails(() => git.CreateBranchAsync(name, checkout: false));
    }

    [Fact]
    public async Task A_name_git_itself_refuses_fails_and_leaves_state_untouched()
    {
        using var repo = InitialRepo();

        var e = await Fails(() => repo.Service().CreateBranchAsync("a..b", checkout: false));

        Assert.Equal(GitErrorKind.Failed, e.Kind);
        Assert.Equal("* main", repo.Git("branch", "--list").Trim());
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("")]
    [InlineData("a\0b")]
    public async Task Unsafe_paths_are_rejected_before_git_runs(string path)
    {
        using var repo = InitialRepo();
        var git = repo.Service();

        Assert.Equal("git.invalidPath", (await Fails(() => git.StageAsync([path]))).Key);
        Assert.Equal("git.invalidPath", (await Fails(() => git.UnstageAsync([path]))).Key);
        Assert.Equal("git.invalidPath", (await Fails(() => git.DiffAsync(path, false))).Key);
        Assert.Equal("git.invalidPath", (await Fails(() => git.StageAsync([]))).Key);
    }

    // ───────── 提交 / 失败 ─────────

    [Fact]
    public async Task A_blank_commit_message_is_rejected_and_a_failing_command_returns_the_git_stderr()
    {
        using var repo = InitialRepo();
        var git = repo.Service();

        var blank = await Fails(() => git.CommitAsync("   "));
        Assert.Equal((GitErrorKind.InvalidRequest, "git.emptyMessage"), (blank.Kind, blank.Key));

        var missing = await Fails(() => git.CheckoutAsync("no-such-branch"));
        Assert.Equal((GitErrorKind.Failed, "git.failed"), (missing.Kind, missing.Key));
        Assert.Contains("no-such-branch", missing.Detail);

        var nothing = await Fails(() => git.CommitAsync("nothing to commit"));
        Assert.Equal(GitErrorKind.Failed, nothing.Kind);
        Assert.Contains("nothing to commit", nothing.Detail);
    }

    [Fact]
    public async Task A_held_index_lock_is_reported_as_busy()
    {
        using var repo = InitialRepo();
        repo.Project.Write("a.txt", "two\n");
        File.WriteAllText(Path.Combine(repo.Root, ".git", "index.lock"), "");

        var e = await Fails(() => repo.Service().StageAsync(["a.txt"]));

        Assert.Equal(GitErrorKind.Busy, e.Kind);
    }

    // ───────── pull / push ─────────

    [Fact]
    public async Task Push_without_an_upstream_sets_one_on_origin()
    {
        using var repo = InitialRepo();
        var bare = Path.Combine(repo.Project.Base, "origin.git");
        repo.GitIn(repo.Project.Base, "init", "-q", "--bare", "-b", "main", bare);
        repo.Git("remote", "add", "origin", bare);

        await repo.Service().PushAsync();

        Assert.Equal("origin/main", repo.Git("rev-parse", "--abbrev-ref", "@{u}").Trim());
        var overview = await repo.Service().OverviewAsync();
        Assert.Equal(("origin/main", 0, 0), (overview.Status!.Upstream, overview.Status.Ahead, overview.Status.Behind));
        // 有上游之后再推也行
        repo.Project.Write("a.txt", "two\n");
        repo.CommitAll("two");
        await repo.Service().PushAsync();
        Assert.Equal(repo.Git("rev-parse", "HEAD"), repo.GitIn(bare, "rev-parse", "main"));
    }

    [Fact]
    public async Task Pushing_a_detached_head_is_refused_with_a_dedicated_key()
    {
        using var repo = InitialRepo();
        repo.Git("checkout", "-q", "--detach");

        var e = await Fails(() => repo.Service().PushAsync());

        Assert.Equal("git.detachedPush", e.Key);
        var overview = await repo.Service().OverviewAsync();
        Assert.True(overview.Status!.Detached);
        Assert.DoesNotContain(await repo.Service().BranchesAsync(), b => b.Name.StartsWith('('));
    }

    [Fact]
    public async Task Pull_fast_forwards_and_a_diverged_history_fails_honestly_instead_of_merging()
    {
        using var repo = InitialRepo();
        var bare = Path.Combine(repo.Project.Base, "origin.git");
        repo.GitIn(repo.Project.Base, "init", "-q", "--bare", "-b", "main", bare);
        repo.Git("remote", "add", "origin", bare);
        repo.Git("push", "-q", "-u", "origin", "main");

        // 另一份克隆推进了 origin
        var other = Path.Combine(repo.Project.Base, "other");
        repo.GitIn(repo.Project.Base, "clone", "-q", bare, other);
        File.WriteAllText(Path.Combine(other, "b.txt"), "b\n");
        repo.GitIn(other, "add", "-A");
        repo.GitIn(other, "commit", "-q", "-m", "remote work");
        repo.GitIn(other, "push", "-q");

        await repo.Service().PullAsync();
        Assert.Equal("remote work", repo.Git("log", "-1", "--format=%s").Trim());

        // 本地、远端各自再提交一个:快进不了
        repo.Project.Write("local.txt", "l\n");
        repo.CommitAll("local work");
        File.WriteAllText(Path.Combine(other, "c.txt"), "c\n");
        repo.GitIn(other, "add", "-A");
        repo.GitIn(other, "commit", "-q", "-m", "more remote work");
        repo.GitIn(other, "push", "-q");

        var e = await Fails(() => repo.Service().PullAsync());

        Assert.Equal(GitErrorKind.Failed, e.Kind);
        Assert.False(string.IsNullOrWhiteSpace(e.Detail));
        Assert.Equal("local work", repo.Git("log", "-1", "--format=%s").Trim()); // 没有悄悄合并
    }

    [Fact]
    public async Task A_command_that_exceeds_its_timeout_is_reported_as_a_timeout()
    {
        using var repo = InitialRepo();
        var options = new GitServiceOptions { Timeouts = new GitTimeouts { Read = TimeSpan.FromMilliseconds(1) } };

        var e = await Fails(() => repo.Service(options: options).OverviewAsync());

        Assert.Equal((GitErrorKind.Unavailable, "git.timeout"), (e.Kind, e.Key));
    }

    // ───────── 生成提交信息(取数据的部分) ─────────

    [Fact]
    public async Task Generate_reads_the_staged_diff_file_list_and_recent_subjects_and_does_not_commit()
    {
        using var repo = InitialRepo();
        repo.Project.Write("b.txt", "bee\n");
        var git = repo.Service();
        await git.StageAsync(["b.txt"]);
        var model = new FakeModel("feat: add b");

        var message = await git.GenerateCommitMessageAsync(model);

        Assert.Equal("feat: add b", message);
        Assert.Contains("+bee", model.User);
        Assert.Contains("Staged files (1):\nb.txt", model.User);
        Assert.Contains("Recent commit titles in this repository:\ninitial", model.User);
        Assert.Equal("initial", repo.Git("log", "-1", "--format=%s").Trim()); // 草稿填进输入框,不直接提交
    }

    [Fact]
    public async Task Generate_with_nothing_staged_or_no_model_fails_with_the_matching_key()
    {
        using var repo = InitialRepo();
        var git = repo.Service();

        Assert.Equal("git.nothingStaged", (await Fails(() => git.GenerateCommitMessageAsync(new FakeModel("x")))).Key);

        repo.Project.Write("b.txt", "bee\n");
        await git.StageAsync(["b.txt"]);
        Assert.Equal("git.aiNoModel", (await Fails(() => git.GenerateCommitMessageAsync(null))).Key);
    }

    [Fact]
    public async Task Generate_truncates_a_huge_diff_and_tells_the_model_but_keeps_the_full_file_list()
    {
        using var repo = new GitRepo();
        repo.Project.Write("big.txt", string.Concat(Enumerable.Range(0, 3000).Select(i => $"line number {i}\n")));
        repo.Project.Write("small.txt", "s\n");
        var git = repo.Service();
        await git.StageAsync(["big.txt", "small.txt"]);
        var model = new FakeModel("chore: add files");

        await git.GenerateCommitMessageAsync(model);

        Assert.Contains($"(truncated to the first {CommitMessageGenerator.DiffLimit} characters)", model.User);
        Assert.Contains("small.txt", model.User); // 文件清单是完整的
    }

    private sealed class NoGitEnvironment(IExecutionEnvironment inner) : IExecutionEnvironment
    {
        public EnvironmentId Id => inner.Id;

        public EnvironmentKind Kind => inner.Kind;

        public string DisplayName => inner.DisplayName;

        public int Generation => inner.Generation;

        public EnvironmentState State => inner.State;

        public event EventHandler<EnvironmentStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public EnvironmentFacts Facts => inner.Facts;

        public PathGuard Paths => inner.Paths;

        public IEnvironmentFs Fs => inner.Fs;

        public ValueTask EnsureReadyAsync(CancellationToken ct = default) => inner.EnsureReadyAsync(ct);

        public Task<EnvironmentFacts> ProbeToolsAsync(CancellationToken ct = default) => inner.ProbeToolsAsync(ct);

        public Task<IEnvProcess> OpenProcessAsync(CommandSpec spec, CancellationToken ct = default) =>
            throw new EnvironmentException(EnvironmentErrorCode.ProcessStartFailed, "git: not found");

        public Task<ITerminal> OpenTerminalAsync(TerminalSpec spec, CancellationToken ct = default) => inner.OpenTerminalAsync(spec, ct);

        public Task<Stream> OpenTcpAsync(string host, int port, CancellationToken ct = default) => inner.OpenTcpAsync(host, port, ct);

        public Task<IPortForward> OpenLocalForwardAsync(string host, int port, CancellationToken ct = default) => inner.OpenLocalForwardAsync(host, port, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeModel(string answer, bool complete = true) : ICommitMessageModel
{
    public string System { get; private set; } = "";

    public string User { get; private set; } = "";

    public Func<CancellationToken, Task>? Before { get; init; }

    public async Task<CommitMessageCompletion> CompleteAsync(string system, string user, CancellationToken ct)
    {
        System = system;
        User = user;
        if (Before is not null)
        {
            await Before(ct);
        }

        return new CommitMessageCompletion(answer, complete);
    }
}
