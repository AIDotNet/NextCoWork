using System.Diagnostics;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Integration.Tests.Support;
using Ncw.Protocol.Methods;

using static Ncw.Integration.Tests.Support.OrchestrationRig;

namespace Ncw.Integration.Tests;

/// <summary>
/// Git 面板在控制端的端到端:真实 Agent + 真实 <c>DeviceLink</c> / Store / <see cref="GitActions"/> + 项目目录里真实的 git 仓库。
/// 概览 → 暂存 → 概览(Store 里的分组变了)→ 提交 → 提交列表;失败是结构化的(稳定 key + git 的诊断);设备断开时概览是未知而不是「干净」(规则 7),重连后追平。
/// </summary>
public sealed class GitLinkTests : IAsyncLifetime
{
    private readonly OrchestrationRig _rig = new();

    public ValueTask InitializeAsync() => _rig.InitializeAsync();

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private static ProjectGit State(Controller c, ProjectRef project)
    {
        lock (c.Ui.Gate)
        {
            return c.Stores.Git.State.Of(project);
        }
    }

    private static string Git(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["GIT_AUTHOR_NAME"] = "Test";
        info.Environment["GIT_AUTHOR_EMAIL"] = "t@example.com";
        info.Environment["GIT_COMMITTER_NAME"] = "Test";
        info.Environment["GIT_COMMITTER_EMAIL"] = "t@example.com";
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {stderr}");
        return stdout;
    }

    private async Task<(Controller C, GitActions Git, ProjectRef Project, string Path)> StartAsync()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            probe!.WaitForExit(10_000);
            Assert.SkipUnless(probe.ExitCode == 0, "git is not available on this machine");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("git is not available on this machine");
        }

        var c = await _rig.StartControllerAsync("ctrl-a");
        var project = await c.Link.AddProjectAsync(_rig.Workspace);
        var path = (await c.Link.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single().Path;
        return (c, new GitActions(c.Devices, c.Stores), project, path);
    }

    [Fact]
    public async Task An_overview_a_stage_and_a_commit_move_the_store_and_the_history()
    {
        var (c, git, project, path) = await StartAsync();

        // 还不是仓库:开不出来不是失败
        var before = await git.RefreshAsync(project);
        Assert.True(before.IsOk);
        await WaitUntil(() => State(c, project) is { Known: true, Overview.Available: false }, "the unavailable overview");
        Assert.Equal(GitUnavailableReasons.NotRepository, State(c, project).Overview!.Reason);

        Git(path, "init", "-q", "-b", "main");
        Git(path, "config", "user.name", "Test"); // 仓库级身份:产品代码自己 commit,CI 上没有全局身份
        Git(path, "config", "user.email", "t@example.com");
        File.WriteAllText(Path.Combine(path, "a.txt"), "one\n");
        Assert.True((await git.RefreshAsync(project)).IsOk);
        await WaitUntil(() => State(c, project).Available, "the repository to be available");
        Assert.Equal(new GitStatusSummary(1, 0, 0, 1, 0), State(c, project).Summary);
        Assert.Equal("main", State(c, project).Overview!.Branch);
        Assert.True(State(c, project).Overview!.Unborn);

        // 暂存:Actions 自己在成功之后刷新 Store
        Assert.True((await git.StageAsync(project, ["a.txt"])).IsOk);
        await WaitUntil(() => State(c, project).Summary == new GitStatusSummary(1, 1, 0, 0, 0), "a.txt to become staged");

        var commit = await git.CommitAsync(project, "first commit");
        Assert.True(commit.IsOk);
        Assert.Equal("first commit", commit.Value!.Subject);
        await WaitUntil(() => State(c, project) is { Available: true, Summary.Clean: true, Overview.Unborn: false }, "the clean overview after the commit");

        var log = await git.ListCommitsAsync(project, 5);
        Assert.Equal(["first commit"], log.Value!.Commits.Select(x => x.Subject));

        // 分支 + diff
        var branch = await git.CreateBranchAsync(project, "feature/x", checkout: true);
        Assert.Equal(("feature/x", true), (branch.Value!.Name, branch.Value.Current));
        await WaitUntil(() => State(c, project).Overview!.Branch == "feature/x", "the store to show the new branch");
        File.WriteAllText(Path.Combine(path, "a.txt"), "one\ntwo\n");
        Assert.Contains("+two", (await git.GetDiffAsync(project, "a.txt", staged: false)).Value!.Text);
        Assert.Equal(("one\n", "one\ntwo\n"), ((await git.GetDiffSidesAsync(project, "a.txt", staged: false)).Value is { } s ? (s.Original, s.Modified) : default));
    }

    [Fact]
    public async Task Failures_are_structured_with_a_stable_key_and_the_gits_own_diagnostic()
    {
        var (_, git, project, path) = await StartAsync();
        Git(path, "init", "-q", "-b", "main");
        Git(path, "config", "user.name", "Test"); // 仓库级身份:产品代码自己 commit,CI 上没有全局身份
        Git(path, "config", "user.email", "t@example.com");
        File.WriteAllText(Path.Combine(path, "a.txt"), "one\n");
        Git(path, "add", "-A");
        Git(path, "commit", "-q", "-m", "initial");

        var unsafePath = await git.StageAsync(project, ["../escape.txt"]);
        Assert.Equal((ActionOutcome.Invalid, "git.invalidPath"), (unsafePath.Outcome, unsafePath.ErrorKey));

        var badBranch = await git.CheckoutAsync(project, "--detach");
        Assert.Equal((ActionOutcome.Invalid, "git.invalidBranch"), (badBranch.Outcome, badBranch.ErrorKey));

        var missing = await git.CheckoutAsync(project, "no-such-branch");
        Assert.Equal((ActionOutcome.Conflict, "git.failed"), (missing.Outcome, missing.ErrorKey));

        var blank = await git.CommitAsync(project, "   ");
        Assert.Equal((ActionOutcome.Invalid, "git.emptyMessage"), (blank.Outcome, blank.ErrorKey));

        // 没有配置默认模型:不是含糊的「生成失败」,而是专门的 key
        File.WriteAllText(Path.Combine(path, "b.txt"), "b\n");
        Assert.True((await git.StageAsync(project, ["b.txt"])).IsOk);
        var generated = await git.GenerateMessageAsync(project);
        Assert.Equal((ActionOutcome.Invalid, "git.aiNoModel"), (generated.Outcome, generated.ErrorKey));
    }

    [Fact]
    public async Task A_dropped_connection_makes_the_overview_unknown_not_clean_and_a_reconnect_catches_up()
    {
        var (c, git, project, path) = await StartAsync();
        Git(path, "init", "-q", "-b", "main");
        Git(path, "config", "user.name", "Test"); // 仓库级身份:产品代码自己 commit,CI 上没有全局身份
        Git(path, "config", "user.email", "t@example.com");
        File.WriteAllText(Path.Combine(path, "a.txt"), "one\n");
        Assert.True((await git.RefreshAsync(project)).IsOk);
        await WaitUntil(() => State(c, project) is { Known: true, Summary.Untracked: 1 }, "the overview");

        c.Current!.Cut();

        await WaitUntil(() => State(c, project) is { Known: false }, "the overview to become unknown");
        Assert.False(State(c, project).Available);
        Assert.Equal(1, State(c, project).Summary.Untracked); // 旧值还在,但不被当成现状

        await WaitUntil(() => c.Workspace.Devices.Values.Any(d => d.Connection == DeviceConnection.Connected), "the link to reconnect");
        File.WriteAllText(Path.Combine(path, "b.txt"), "two\n");
        Assert.True((await git.RefreshAsync(project)).IsOk);
        await WaitUntil(() => State(c, project) is { Known: true, Summary.Untracked: 2 }, "the refreshed overview");
    }

    [Fact]
    public async Task Without_a_connection_every_action_is_unavailable_and_the_store_is_untouched()
    {
        var c = await _rig.StartControllerAsync("ctrl-a");
        var git = new GitActions(new DeviceRegistry(), c.Stores);

        var result = await git.RefreshAsync(new ProjectRef("nowhere", "w"));

        Assert.Equal(ActionOutcome.Unavailable, result.Outcome);
        Assert.Empty(c.Stores.Git.State.Projects);
    }
}
