using Ncw.Agent.Environments;

namespace Ncw.Agent.Workspace.Files;

/// <summary>
/// 符号链接策略(移植自 2.x <c>checkedPath</c> 的 <c>LinkPolicy</c>)。三种模式都先在<b>词法上</b>拒掉 <c>..</c> / <c>.</c> / 空段,所以入口一定是项目里的那条路径,<c>..</c> 逃不出去。
/// </summary>
internal enum LinkPolicy
{
    /// <summary>逐段拒符号链接:新建 / 改名 / 移动 / 复制 / 删除走这个,不改链背后的东西。</summary>
    Reject,

    /// <summary>祖先目录可以是链(用户在项目里 <c>ln -s</c> 进多个项目目录联调,2.x #182),最后一段自身是链仍拒:读、保存走这个,编辑不会写穿一条链。</summary>
    FollowDirs,

    /// <summary>任何一段都可以是链:只给列目录、搜索这类不碰内容的入口。</summary>
    Follow,
}

/// <summary>
/// 项目相对路径 → 环境里的绝对路径。<b>所有文件操作共用这一处</b>(路径边界保护):协议里的路径一律是项目相对、<c>/</c> 分隔,
/// 不接受绝对路径、反斜杠、盘符、NUL、<c>..</c> / <c>.</c> / 空段。边界由两道保证:词法规则(上面)+ 逐段 lstat 的链接策略 + 环境的 <see cref="PathGuard"/>。
/// </summary>
internal static class ProjectPaths
{
    public static string[] Validate(string? path, bool allowRoot)
    {
        if (path is null || path.Contains('\0'))
        {
            throw new FilesException(FilesErrorCode.InvalidPath, "bad characters");
        }

        if (path.Length == 0)
        {
            return allowRoot ? [] : throw new FilesException(FilesErrorCode.InvalidPath, "empty path");
        }

        if (path.Contains('\\') || path[0] == '/' || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':'))
        {
            throw new FilesException(FilesErrorCode.InvalidPath, "not a project-relative path");
        }

        var segments = path.Split('/');
        if (segments.Any(static s => s.Length == 0 || s == "." || s == ".."))
        {
            throw new FilesException(FilesErrorCode.InvalidPath, "empty, '.' or '..' segment");
        }

        return segments;
    }

    /// <summary>项目根的 realpath(macOS 的 <c>/var</c> → <c>/private/var</c>)。根读不到是 <see cref="FilesErrorCode.ProjectUnavailable"/>。</summary>
    public static async ValueTask<EnvPath> RealRootAsync(FileProject project, CancellationToken ct)
    {
        try
        {
            var real = await project.Environment.Fs.RealPathAsync(project.Root, ct).ConfigureAwait(false);
            var info = await project.Environment.Fs.StatAsync(real, ct).ConfigureAwait(false);
            return info.IsDirectory ? real : throw new FilesException(FilesErrorCode.ProjectUnavailable, "root is not a directory");
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            throw new FilesException(FilesErrorCode.ProjectUnavailable, "root is gone", e);
        }
    }

    /// <summary>
    /// 解析一条项目相对路径。路径不存在时也返回(尾部原样接上):新建、改名的目标本来就不存在。
    /// 沿途按 <paramref name="links"/> 审计符号链接;没有碰到链时再让 <see cref="PathGuard.ResolveWithinAsync"/> 复核一次不越界。
    /// </summary>
    public static async ValueTask<EnvPath> ResolveAsync(FileProject project, string? path, bool allowRoot, LinkPolicy links, CancellationToken ct)
    {
        try
        {
            var segments = Validate(path, allowRoot);
            var realRoot = await RealRootAsync(project, ct).ConfigureAwait(false);
            if (segments.Length == 0)
            {
                return realRoot;
            }

            var fs = project.Environment.Fs;
            var current = realRoot;
            var sawLink = false;
            for (var i = 0; i < segments.Length; i++)
            {
                current = current.Join(segments[i]);
                FsEntryInfo info;
                try
                {
                    info = await fs.LStatAsync(current, ct).ConfigureAwait(false);
                }
                catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
                {
                    // 这一段不存在:余下的是还没建出来的尾部,没有链可查
                    current = current.Join(segments[(i + 1)..]);
                    break;
                }

                if (!info.IsSymlink)
                {
                    continue;
                }

                sawLink = true;
                if (links == LinkPolicy.Reject || (links == LinkPolicy.FollowDirs && i == segments.Length - 1))
                {
                    throw new FilesException(FilesErrorCode.Symlink, "symbolic link on the path");
                }
            }

            if (!sawLink)
            {
                // 没有链:词法上就在根里。PathGuard 复核 realpath 之后仍然在根里(防止 junction 之类词法看不出来的东西)
                await project.Environment.Paths.ResolveWithinAsync(project.Root, path!, ct).ConfigureAwait(false);
            }

            return current;
        }
        catch (EnvironmentException e)
        {
            throw FilesException.From(e);
        }
    }

    /// <summary><paramref name="absolute"/> 相对 <paramref name="realRoot"/> 的 <c>/</c> 写法。</summary>
    public static string Relative(EnvPath realRoot, EnvPath absolute) => absolute.RelativeTo(realRoot);

    public static string Join(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;
}
