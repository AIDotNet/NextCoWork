using Ncw.Agent.Cloud;
using Ncw.Agent.Environments;
using Ncw.Agent.Host.Services;
using Ncw.Agent.Workspace.Git;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Workspace;

/// <summary>
/// <c>projects.add</c> 带 <c>CloneUrl</c> 时的克隆(02 A6「工作区来源」「Git 接力」):校验地址 → 出网策略 → 解析目标目录 → 经项目环境的 git 克隆(凭据经 <see cref="GitCredentialVault"/> 的环境变量)。
/// 克隆本身在 <see cref="GitCloner"/>;这里只做 Host 的接线和协议错误映射。
/// </summary>
internal static class ProjectClone
{
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);

    /// <summary>克隆并返回目标目录的绝对路径(之后由调用方登记成项目)。</summary>
    public static async Task<string> CloneAsync(
        IExecutionEnvironment environment, GitCredentialVault? vault, IEgressPolicy egress, string? workspacesRoot, ProjectsAddParams p, CancellationToken ct)
    {
        if (!GitCloner.TryParseUrl(p.CloneUrl, out var url))
        {
            throw ServiceMapping.Invalid(GitCloner.InvalidUrl, "Only https:// clone addresses without credentials are accepted");
        }

        var decision = egress.Evaluate(new EgressRequest(EgressKind.GitRemote, url.Host, url.Port));
        if (!decision.Allowed)
        {
            throw ServiceMapping.Error(ErrorCodes.Forbidden, CloudErrorKeys.EgressDenied, "The egress policy does not allow this Git host", false, ("host", url.Host));
        }

        var target = ResolveTarget(p.Path, workspacesRoot, environment.Facts.PathStyle);
        var credentials = vault?.Resolve() ?? [];
        var hostKey = GitCloner.HostKey(url);
        var secrets = credentials.Where(c => c.Host == hostKey).Select(static c => c.Token).ToArray();
        try
        {
            await GitCloner.CloneAsync(
                environment, target, new GitCloneRequest(url, p.CloneBranch, GitCredentialEnvironment.Build(credentials), secrets), CloneTimeout, ct).ConfigureAwait(false);
        }
        catch (GitException e)
        {
            throw GitHandlers.Map(e);
        }

        return target.Value;
    }

    private static EnvPath ResolveTarget(string? path, string? workspacesRoot, PathStyle style)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0'))
        {
            throw ServiceMapping.Invalid(GitCloner.TargetInvalid, "A target directory is required");
        }

        if (workspacesRoot is null)
        {
            return Path.IsPathRooted(path) ? EnvPath.Parse(Path.GetFullPath(path), style) : throw ServiceMapping.Invalid(GitCloner.TargetInvalid, "The target directory must be an absolute path");
        }

        // 云端:相对路径从工作区根解析,目标必须在工作区根里面(不能克隆到数据根或沙箱的其它位置)
        var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(workspacesRoot, path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacesRoot));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw ServiceMapping.Invalid(GitCloner.TargetInvalid, "The target directory must be inside the workspaces root");
        }

        return EnvPath.Parse(full, style);
    }
}
