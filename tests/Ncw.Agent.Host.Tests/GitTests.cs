using System.Diagnostics;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Workspace.Git;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// <c>git.*</c> 的端到端:真实 Agent + <see cref="DeviceClient"/> + 项目目录里真实的 git 仓库。
/// 读(概览、分支、提交、diff)viewer 起;写(暂存 / 提交 / 切分支 / 拉取 / 推送)和生成提交信息 member 起;
/// 非法路径 / 分支名在碰 git 之前被拒,失败带稳定 key 和 git 的 stderr(<see cref="GitErrorDto"/>);幂等重放不会提交两次;
/// 生成提交信息用注入的假模型(不碰网络),没配模型是 <c>git.aiNoModel</c>。
/// </summary>
public sealed class GitTests : IAsyncLifetime
{
    private readonly FakeCommitModel _model = new("feat: add things");
    private readonly ConversationRig _rig;
    private DeviceClient _owner = null!;
    private ProjectDto _project = null!;
    private int _port;

    public GitTests()
    {
        _rig = new ConversationRig(llm => new AgentServices
        {
            Models = new FakeModels(llm),
            Http = new NoNetwork(),
            CatalogDelayer = new NeverDelay(),
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
            CommitMessages = _ => _model,
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _project = await _rig.AddProjectAsync(_owner);
        var status = await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true));
        _port = status.Port!.Value;
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private string Abs(string relative) => Path.Combine(_project.Path, relative);

    private string Git(params string[] args)
    {
        Assert.SkipUnless(GitOnPath.Present, "git is not available on this machine");
        var info = new ProcessStartInfo("git") { WorkingDirectory = _project.Path, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["GIT_AUTHOR_NAME"] = "Test";
        info.Environment["GIT_AUTHOR_EMAIL"] = "t@example.com";
        info.Environment["GIT_COMMITTER_NAME"] = "Test";
        info.Environment["GIT_COMMITTER_EMAIL"] = "t@example.com";
        info.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_rig.DataRoot, "no-global");
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr}");
        return stdout;
    }

    private void InitRepoWithCommit()
    {
        Git("init", "-q", "-b", "main");
        // 仓库级身份:被测的产品代码自己跑 git commit,拿不到上面给测试命令设的 GIT_AUTHOR_*;CI 机器上又没有全局身份(Linux CI 首跑时 commit 失败)
        Git("config", "user.name", "Test");
        Git("config", "user.email", "t@example.com");
        File.WriteAllText(Abs("a.txt"), "one\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "initial");
    }

    private async Task<DeviceClient> ConnectAsync(string name, string role)
    {
        var identity = PeerIdentity.Create(name);
        var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, _rig.Host.Remote.PairingCode!, name);
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));
        var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
        var client = await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
        Assert.Equal(role, client.Welcome.Role);
        return client;
    }

    private static async Task<ProtocolException> Fails<T>(Func<Task<T>> call) => await Assert.ThrowsAsync<ProtocolException>(call);

    private static GitErrorDto DetailsOf(ProtocolException e) => FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<GitErrorDto>());

    private static string Idem() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task The_method_table_is_registered_and_the_device_advertises_the_git_capability()
    {
        Assert.Contains(Capabilities.Git, _owner.Welcome.Features);
        foreach (var method in GitMethods.All)
        {
            Assert.False(string.IsNullOrEmpty(method.Name));
        }

        // 方法没注册的话这一句会是 unsupported
        var overview = await _owner.CallAsync(GitMethods.Overview, new GitWorkspaceParams(_project.WorkspaceId));
        Assert.False(overview.Available);
    }

    [Fact]
    public async Task The_service_keys_and_the_protocol_keys_are_the_same_strings()
    {
        Assert.Equal(
            [GitKeys.UnavailablePrefix, GitKeys.InvalidPath, GitKeys.InvalidBranch, GitKeys.EmptyMessage, GitKeys.NothingStaged, GitKeys.DetachedPush, GitKeys.Failed, GitKeys.Timeout,
                GitKeys.AiNoModel, GitKeys.AiFailed, GitKeys.AiTimeout, GitKeys.AiTruncated, GitKeys.AiUnparsable],
            [GitErrorKeys.UnavailablePrefix, GitErrorKeys.InvalidPath, GitErrorKeys.InvalidBranch, GitErrorKeys.EmptyMessage, GitErrorKeys.NothingStaged, GitErrorKeys.DetachedPush, GitErrorKeys.Failed, GitErrorKeys.Timeout,
                GitErrorKeys.AiNoModel, GitErrorKeys.AiFailed, GitErrorKeys.AiTimeout, GitErrorKeys.AiTruncated, GitErrorKeys.AiUnparsable]);
        Assert.Equal(GitUnavailableReasons.NotRepository, GitKeys.Unavailable(GitUnavailableReason.NotRepository)[GitKeys.UnavailablePrefix.Length..]);
        Assert.Equal(GitUnavailableReasons.GitMissing, GitKeys.Unavailable(GitUnavailableReason.GitMissing)[GitKeys.UnavailablePrefix.Length..]);
        Assert.Equal(GitUnavailableReasons.WorkspaceUnavailable, GitKeys.Unavailable(GitUnavailableReason.WorkspaceUnavailable)[GitKeys.UnavailablePrefix.Length..]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_change_walks_through_overview_diff_stage_commit_and_history()
    {
        InitRepoWithCommit();
        var w = _project.WorkspaceId;

        var overview = await _owner.CallAsync(GitMethods.Overview, new GitWorkspaceParams(w));
        Assert.True(overview.Available);
        Assert.Equal("main", overview.Branch);
        Assert.Empty(overview.Files!);
        Assert.Equal(["main"], (await _owner.CallAsync(GitMethods.Branches, new GitWorkspaceParams(w))).Branches.Select(b => b.Name));
        Assert.Equal("initial", Assert.Single((await _owner.CallAsync(GitMethods.Commits, new GitCommitsParams(w, 5))).Commits).Subject);

        File.WriteAllText(Abs("a.txt"), "one\ntwo\n");
        File.WriteAllText(Abs("中文 name.txt"), "你好\n");
        var changed = (await _owner.CallAsync(GitMethods.Overview, new GitWorkspaceParams(w))).Files!;
        Assert.Contains(changed, f => f is { Path: "a.txt", Unstaged: true, Staged: false });
        Assert.Contains(changed, f => f is { Path: "中文 name.txt", Untracked: true });

        Assert.Contains("+two", (await _owner.CallAsync(GitMethods.Diff, new GitDiffParams(w, "a.txt", false))).Text);
        Assert.Contains("+你好", (await _owner.CallAsync(GitMethods.Diff, new GitDiffParams(w, "中文 name.txt", false))).Text);
        var sides = await _owner.CallAsync(GitMethods.DiffSides, new GitDiffParams(w, "a.txt", false));
        Assert.Equal((true, "one\n", "one\ntwo\n"), (sides.Comparable, sides.Original, sides.Modified));

        await _owner.CallAsync(GitMethods.Stage, new GitPathsParams(w, ["a.txt", "中文 name.txt"]), idem: Idem());
        Assert.All((await _owner.CallAsync(GitMethods.Overview, new GitWorkspaceParams(w))).Files!, f => Assert.True(f.Staged && !f.Unstaged));

        var commit = await _owner.CallAsync(GitMethods.Commit, new GitCommitParams(w, "add two"), idem: Idem());
        Assert.Equal("add two", commit.Subject);
        Assert.Equal("add two", Git("log", "-1", "--format=%s").Trim());
        Assert.Equal(["add two", "initial"], (await _owner.CallAsync(GitMethods.Commits, new GitCommitsParams(w, 5))).Commits.Select(c => c.Subject));

        File.WriteAllText(Abs("a.txt"), "changed\n");
        await _owner.CallAsync(GitMethods.Stage, new GitPathsParams(w, ["a.txt"]), idem: Idem());
        await _owner.CallAsync(GitMethods.Unstage, new GitPathsParams(w, ["a.txt"]), idem: Idem());
        Assert.Equal("", Git("diff", "--cached", "--name-only").Trim());
    }

    [Fact]
    public async Task Branches_are_created_and_switched_and_a_replayed_commit_does_not_commit_twice()
    {
        InitRepoWithCommit();
        var w = _project.WorkspaceId;

        var created = await _owner.CallAsync(GitMethods.CreateBranch, new GitCreateBranchParams(w, "feature/x", true), idem: Idem());
        Assert.Equal(("feature/x", true), (created.Name, created.Current));
        await _owner.CallAsync(GitMethods.Checkout, new GitCheckoutParams(w, "main"), idem: Idem());
        Assert.Equal("main", Git("branch", "--show-current").Trim());

        File.WriteAllText(Abs("b.txt"), "b\n");
        await _owner.CallAsync(GitMethods.Stage, new GitPathsParams(w, ["b.txt"]), idem: Idem());
        var key = Idem();
        await _owner.CallAsync(GitMethods.Commit, new GitCommitParams(w, "add b"), idem: key);
        await _owner.CallAsync(GitMethods.Commit, new GitCommitParams(w, "add b"), idem: key); // 续连后重发:同一个键只执行一次
        Assert.Equal("2", Git("rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public async Task A_viewer_can_read_but_everything_that_writes_or_costs_money_is_forbidden()
    {
        InitRepoWithCommit();
        File.WriteAllText(Abs("a.txt"), "one\ntwo\n");
        var w = _project.WorkspaceId;
        await using var viewer = await ConnectAsync("viewer-1", "viewer");

        Assert.True((await viewer.CallAsync(GitMethods.Overview, new GitWorkspaceParams(w))).Available);
        Assert.NotEmpty((await viewer.CallAsync(GitMethods.Branches, new GitWorkspaceParams(w))).Branches);
        Assert.NotEmpty((await viewer.CallAsync(GitMethods.Commits, new GitCommitsParams(w))).Commits);
        Assert.Contains("+two", (await viewer.CallAsync(GitMethods.Diff, new GitDiffParams(w, "a.txt", false))).Text);
        Assert.True((await viewer.CallAsync(GitMethods.DiffSides, new GitDiffParams(w, "a.txt", false))).Comparable);

        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(GitMethods.Stage, new GitPathsParams(w, ["a.txt"]), idem: Idem()))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(GitMethods.Unstage, new GitPathsParams(w, ["a.txt"]), idem: Idem()))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(GitMethods.Commit, new GitCommitParams(w, "x"), idem: Idem()))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(GitMethods.Checkout, new GitCheckoutParams(w, "main"), idem: Idem()))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(GitMethods.CreateBranch, new GitCreateBranchParams(w, "x", false), idem: Idem()))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(GitMethods.Pull, new GitWorkspaceParams(w), idem: Idem()))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(GitMethods.Push, new GitWorkspaceParams(w), idem: Idem()))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Fails(() => viewer.CallAsync(GitMethods.GenerateMessage, new GitWorkspaceParams(w)))).Code);

        Assert.Equal("", Git("diff", "--cached", "--name-only").Trim()); // 什么都没变
        Assert.Equal(0, _model.Calls);
    }

    [Fact]
    public async Task A_member_can_write_and_bad_input_is_refused_before_git_runs_with_a_stable_key()
    {
        InitRepoWithCommit();
        var w = _project.WorkspaceId;
        await using var member = await ConnectAsync("member-1", "member");

        File.WriteAllText(Abs("m.txt"), "m\n");
        await member.CallAsync(GitMethods.Stage, new GitPathsParams(w, ["m.txt"]), idem: Idem());
        Assert.Equal("m.txt", Git("diff", "--cached", "--name-only").Trim());

        foreach (var path in new[] { "/etc/passwd", "../x", "a/../../x" })
        {
            var e = await Fails(() => member.CallAsync(GitMethods.Stage, new GitPathsParams(w, [path]), idem: Idem()));
            Assert.Equal((ErrorCodes.InvalidRequest, "git.invalidPath"), (e.Code, DetailsOf(e).Key));
        }

        foreach (var branch in new[] { "--detach", "-f", "a b", "x~1" })
        {
            var e = await Fails(() => member.CallAsync(GitMethods.Checkout, new GitCheckoutParams(w, branch), idem: Idem()));
            Assert.Equal((ErrorCodes.InvalidRequest, "git.invalidBranch"), (e.Code, DetailsOf(e).Key));
        }

        var blank = await Fails(() => member.CallAsync(GitMethods.Commit, new GitCommitParams(w, "  "), idem: Idem()));
        Assert.Equal((ErrorCodes.InvalidRequest, "git.emptyMessage"), (blank.Code, DetailsOf(blank).Key));

        var tooMany = await Fails(() => member.CallAsync(GitMethods.Commits, new GitCommitsParams(w, 501)));
        Assert.Equal(ErrorCodes.InvalidRequest, tooMany.Code);

        // git 自己拒绝的:conflict + key + stderr 在 details 里(消息本身不带 stderr)
        var missing = await Fails(() => member.CallAsync(GitMethods.Checkout, new GitCheckoutParams(w, "no-such-branch"), idem: Idem()));
        Assert.Equal(ErrorCodes.Conflict, missing.Code);
        Assert.DoesNotContain("no-such-branch", missing.Message);
        Assert.Equal("git.failed", DetailsOf(missing).Key);
        Assert.Contains("no-such-branch", DetailsOf(missing).Detail);
        Assert.Equal("main", Git("branch", "--show-current").Trim());

        // 未知项目
        Assert.Equal(ErrorCodes.NotFound, (await Fails(() => member.CallAsync(GitMethods.Overview, new GitWorkspaceParams("nope")))).Code);
    }

    [Fact]
    public async Task A_project_that_is_not_a_repository_reads_as_unavailable_and_writes_fail_with_not_found()
    {
        Assert.SkipUnless(GitOnPath.Present, "git is not available on this machine");
        var w = _project.WorkspaceId;

        var overview = await _owner.CallAsync(GitMethods.Overview, new GitWorkspaceParams(w));
        Assert.Equal((false, GitUnavailableReasons.NotRepository), (overview.Available, overview.Reason));
        Assert.Empty((await _owner.CallAsync(GitMethods.Commits, new GitCommitsParams(w))).Commits);
        Assert.Empty((await _owner.CallAsync(GitMethods.Branches, new GitWorkspaceParams(w))).Branches);

        var e = await Fails(() => _owner.CallAsync(GitMethods.Stage, new GitPathsParams(w, ["a.txt"]), idem: Idem()));
        Assert.Equal((ErrorCodes.NotFound, "git.unavailable.notRepository"), (e.Code, DetailsOf(e).Key));
    }

    [Fact]
    public async Task Generating_a_message_reads_the_staged_diff_with_the_injected_model_and_never_commits()
    {
        InitRepoWithCommit();
        var w = _project.WorkspaceId;

        var nothing = await Fails(() => _owner.CallAsync(GitMethods.GenerateMessage, new GitWorkspaceParams(w)));
        Assert.Equal((ErrorCodes.InvalidRequest, "git.nothingStaged"), (nothing.Code, DetailsOf(nothing).Key));
        Assert.Equal(0, _model.Calls);

        File.WriteAllText(Abs("b.txt"), "bee\n");
        await _owner.CallAsync(GitMethods.Stage, new GitPathsParams(w, ["b.txt"]), idem: Idem());
        await using var member = await ConnectAsync("member-2", "member");

        var generated = await member.CallAsync(GitMethods.GenerateMessage, new GitWorkspaceParams(w));

        Assert.Equal("feat: add things", generated.Message);
        Assert.Contains("+bee", _model.LastUser);
        Assert.Contains("initial", _model.LastUser); // 最近提交标题当样本
        Assert.Equal("initial", Git("log", "-1", "--format=%s").Trim());

        _model.Fail = new CommitMessageModelException(GitKeys.AiFailed, "502 from upstream");
        var failed = await Fails(() => member.CallAsync(GitMethods.GenerateMessage, new GitWorkspaceParams(w)));
        Assert.Equal((ErrorCodes.Unavailable, true, "git.aiFailed"), (failed.Code, failed.Retryable, DetailsOf(failed).Key));
    }

    [Fact]
    public async Task Without_an_injected_model_and_no_default_model_in_settings_generating_says_ai_no_model()
    {
        await using var plain = new ConversationRig();
        await plain.InitializeAsync();
        var client = await plain.ConnectAsync("c");
        var project = await plain.AddProjectAsync(client);
        Assert.SkipUnless(GitOnPath.Present, "git is not available on this machine");
        RunGit(project.Path, "init", "-q", "-b", "main");
        RunGit(project.Path, "config", "user.name", "Test");
        RunGit(project.Path, "config", "user.email", "t@example.com");
        File.WriteAllText(Path.Combine(project.Path, "a.txt"), "x\n");
        RunGit(project.Path, "add", "-A");

        var e = await Fails(() => client.CallAsync(GitMethods.GenerateMessage, new GitWorkspaceParams(project.WorkspaceId)));

        Assert.Equal((ErrorCodes.InvalidRequest, "git.aiNoModel"), (e.Code, DetailsOf(e).Key));
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    private sealed class FakeCommitModel(string answer) : ICommitMessageModel
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string LastUser { get; private set; } = "";

        public Exception? Fail { get; set; }

        public Task<CommitMessageCompletion> CompleteAsync(string system, string user, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            LastUser = user;
            return Fail is { } error ? Task.FromException<CommitMessageCompletion>(error) : Task.FromResult(new CommitMessageCompletion(answer, true));
        }
    }

    private static class GitOnPath
    {
        public static readonly bool Present = Probe();

        private static bool Probe()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                process!.WaitForExit(10_000);
                return process.ExitCode == 0;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return false;
            }
        }
    }
}
