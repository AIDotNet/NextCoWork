using Ncw.Agent.Environments;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>
/// <c>isolation: worktree</c> 的 git worktree 生命周期。全部经 <see cref="IExecutionEnvironment"/> 执行 <c>git</c>,
/// 不直接碰 <c>System.IO</c> / <c>Process</c>(本机和 SSH 执行环境走同一条路径)。
/// 结束时有未提交改动或新提交就保留并返回 <see cref="WorktreeInfo"/>;干净则自动 <c>worktree remove</c> + 删除临时分支。
/// </summary>
internal sealed class SubagentWorktree
{
    private readonly IExecutionEnvironment _environment;
    private readonly EnvPath _repoRoot;
    private readonly EnvPath _worktreePath;
    private readonly string _branch;
    private readonly string _baseCommit;

    private SubagentWorktree(IExecutionEnvironment environment, EnvPath repoRoot, EnvPath worktreePath, string branch, string baseCommit)
    {
        _environment = environment;
        _repoRoot = repoRoot;
        _worktreePath = worktreePath;
        _branch = branch;
        _baseCommit = baseCommit;
    }

    public EnvPath Path => _worktreePath;

    public string Branch => _branch;

    public WorktreeInfo Info => new(_worktreePath.Value, _branch);

    public static async Task<(SubagentWorktree? Worktree, string? Error)> TryCreateAsync(
        IExecutionEnvironment environment, EnvPath projectRoot, string childRunId, CancellationToken ct)
    {
        var top = await environment.RunAsync(
            CommandSpec.Exec("git", "rev-parse", "--show-toplevel") with { WorkingDirectory = projectRoot }, ct: ct).ConfigureAwait(false);
        var topOut = top.Stdout.ToText();
        if (top.ExitCode != 0 || string.IsNullOrWhiteSpace(topOut))
        {
            return (null, "isolation=\"worktree\" requires the project to be inside a git repository.");
        }

        var repoRoot = EnvPath.Parse(topOut.Trim(), projectRoot.Style);
        var head = await environment.RunAsync(
            CommandSpec.Exec("git", "rev-parse", "--verify", "HEAD") with { WorkingDirectory = repoRoot }, ct: ct).ConfigureAwait(false);
        var headOut = head.Stdout.ToText();
        if (head.ExitCode != 0 || string.IsNullOrWhiteSpace(headOut))
        {
            return (null, "isolation=\"worktree\" requires at least one commit in the git repository (HEAD does not resolve).");
        }

        var baseCommit = headOut.Trim();
        var slug = SanitizeSlug(childRunId);
        var branch = $"ncw/subagent/{slug}";
        var worktreePath = repoRoot.Join(".next-cowork", "worktrees", slug);

        var add = await environment.RunAsync(
            CommandSpec.Exec("git", "worktree", "add", "-b", branch, worktreePath.Value, "HEAD") with { WorkingDirectory = repoRoot }, ct: ct).ConfigureAwait(false);
        if (add.ExitCode != 0)
        {
            var err = add.Stderr.ToText().Trim();
            var reason = string.IsNullOrEmpty(err) ? add.Stdout.ToText().Trim() : err;
            return (null, $"Failed to create git worktree at {worktreePath.Value}: {reason}");
        }

        return (new SubagentWorktree(environment, repoRoot, worktreePath, branch, baseCommit), null);
    }

    /// <summary>结束清理:有未提交改动或新提交就保留返回信息;干净就删掉 worktree 与临时分支。</summary>
    public async Task<WorktreeInfo?> FinalizeAsync(CancellationToken ct = default)
    {
        var status = await _environment.RunAsync(
            CommandSpec.Exec("git", "status", "--porcelain") with { WorkingDirectory = _worktreePath }, ct: ct).ConfigureAwait(false);
        if (status.ExitCode != 0 || !string.IsNullOrWhiteSpace(status.Stdout.ToText()))
        {
            return Info;
        }

        var commits = await _environment.RunAsync(
            CommandSpec.Exec("git", "rev-list", "--count", $"{_baseCommit}..HEAD") with { WorkingDirectory = _worktreePath }, ct: ct).ConfigureAwait(false);
        if (commits.ExitCode != 0
            || !int.TryParse(commits.Stdout.ToText().Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var count)
            || count > 0)
        {
            return Info;
        }

        await RemoveAsync(ct).ConfigureAwait(false);
        return null;
    }

    public async Task RemoveAsync(CancellationToken ct = default)
    {
        await _environment.RunAsync(
            CommandSpec.Exec("git", "worktree", "remove", "--force", _worktreePath.Value) with { WorkingDirectory = _repoRoot }, ct: ct).ConfigureAwait(false);
        await _environment.RunAsync(
            CommandSpec.Exec("git", "branch", "-D", _branch) with { WorkingDirectory = _repoRoot }, ct: ct).ConfigureAwait(false);
    }

    private static string SanitizeSlug(string value)
    {
        var chars = new char[value.Length];
        var len = 0;
        var prevDash = false;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
            {
                chars[len++] = c;
                prevDash = false;
            }
            else if (!prevDash && len > 0)
            {
                chars[len++] = '-';
                prevDash = true;
            }
        }

        while (len > 0 && chars[len - 1] == '-')
        {
            len--;
        }

        var slug = len == 0 ? "wt" : new string(chars, 0, len);
        return slug.Length <= 48 ? slug : slug[^48..];
    }
}
