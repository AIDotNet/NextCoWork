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
        }
    }

    public TempProject Project { get; }

    public string Root => Project.Root;

    public GitService Service(string? subdirectory = null, GitServiceOptions? options = null) =>
        new(Project.Env, subdirectory is null ? Project.RootPath : Project.RootPath.Join(subdirectory), options);

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
