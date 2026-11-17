using System.Diagnostics;
using Ncw.Agent.Environments;
using Ncw.Agent.Workspace.Git;

namespace Ncw.Agent.Workspace.Tests;

/// <summary>真实的临时 git 仓库(项目根 = 仓库根)。测试自己用 git 命令摆状态;被测的 <see cref="GitService"/> 只能经 <see cref="IExecutionEnvironment"/>。</summary>
public sealed class GitRepo : IDisposable
{
    public GitRepo(bool init = true)
    {
        GitAvailability.RequireGit();
        Project = new TempProject();
        if (init)
        {
            Git("init", "-q", "-b", "main");
            // 仓库级身份:被测的产品代码自己跑 git commit,拿不到上面给测试命令设的 GIT_AUTHOR_*;CI 机器上又没有全局身份(Linux CI 首跑时 commit 失败)
            Git("config", "user.name", "Test");
            Git("config", "user.email", "t@example.com");
        }
    }

    public TempProject Project { get; }

    public string Root => Project.Root;

    /// <summary>不指定选项时把超时放宽:产品默认「读 10 秒」,满载的 CI(Windows runner)上一条 git rev-parse 超过过。专门测超时的用例自己传选项。</summary>
    public GitService Service(string? subdirectory = null, GitServiceOptions? options = null) =>
        new(Project.Env, subdirectory is null ? Project.RootPath : Project.RootPath.Join(subdirectory), options ?? new GitServiceOptions { Timeouts = PatientTimeouts });

    private static GitTimeouts PatientTimeouts { get; } = new() { Read = TimeSpan.FromSeconds(60), Write = TimeSpan.FromSeconds(120) };

    public string Git(params string[] args) => GitIn(Root, args);

    public string GitIn(string cwd, params string[] args)
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
        info.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(Project.Base, "no-global-gitconfig");
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? stdout : throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
    }

    /// <summary>失败也不抛(冲突的 merge 退出非零,那正是要的状态)。</summary>
    public void GitTry(params string[] args)
    {
        try
        {
            Git(args);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void CommitAll(string message)
    {
        Git("add", "-A");
        Git("commit", "-q", "-m", message);
    }

    public void Dispose() => Project.Dispose();
}
