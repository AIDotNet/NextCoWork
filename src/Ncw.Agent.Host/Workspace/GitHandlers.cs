using Ncw.Agent.Workspace.Git;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Workspace;

/// <summary>
/// <c>git.*</c> 的方法实现(方法表在 <see cref="GitMethods"/>)。角色、承载、幂等键由路由统一检查,这里只做业务:项目 → (环境, 根) → <see cref="GitService"/> → DTO。
/// 失败一律是 <see cref="ProtocolException"/>:<see cref="GitException"/> 按粗粒度错误码映射(<c>invalid_request</c> / <c>not_found</c> / <c>conflict</c> / <c>busy</c> / <c>unavailable</c> / <c>unsupported</c>),
/// 稳定 key 和 git 的 stderr 放进 <c>details</c>(<see cref="GitErrorDto"/>),消息本身不带 stderr;其它异常由路由收成 <c>internal</c>。设备数据没就绪时 <paramref name="tools"/> 抛可重试的 <c>unavailable</c>。
/// </summary>
internal static class GitHandlers
{
    private const int MaxCommits = 500;

    private const int DefaultCommits = 50;

    public static void Register(MethodRouter router, Func<WorkspaceToolsRuntime> tools)
    {
        router.Handle(GitMethods.Overview, async (p, _, ct) => await Guard(async () => ToDto(await tools().GitOf(p.WorkspaceId).OverviewAsync(ct).ConfigureAwait(false))).ConfigureAwait(false));

        router.Handle(GitMethods.Branches, async (p, _, ct) => await Guard(async () =>
            new GitBranchesResult([.. (await tools().GitOf(p.WorkspaceId).BranchesAsync(ct).ConfigureAwait(false)).Select(static b => new GitBranchDto(b.Name, b.Current, b.Upstream))])).ConfigureAwait(false));

        router.Handle(GitMethods.Commits, async (p, _, ct) => await Guard(async () =>
        {
            // 2.x 在 schema 里拒绝超过 500 的 limit(不静默改写);不给(≤ 0)用缺省
            if (p.Limit > MaxCommits)
            {
                throw new ProtocolException(ErrorCodes.InvalidRequest, $"Limit must be at most {MaxCommits}.");
            }

            var commits = await tools().GitOf(p.WorkspaceId).CommitsAsync(p.Limit <= 0 ? DefaultCommits : p.Limit, ct).ConfigureAwait(false);
            return new GitCommitsResult([.. commits.Select(ToDto)]);
        }).ConfigureAwait(false));

        router.Handle(GitMethods.Diff, async (p, _, ct) => await Guard(async () =>
        {
            var diff = await tools().GitOf(p.WorkspaceId).DiffAsync(p.Path, p.Staged, ct).ConfigureAwait(false);
            return new GitDiffDto(diff.Path, diff.Staged, diff.Binary, diff.Truncated, diff.Text);
        }).ConfigureAwait(false));

        router.Handle(GitMethods.DiffSides, async (p, _, ct) => await Guard(async () =>
        {
            var sides = await tools().GitOf(p.WorkspaceId).DiffSidesAsync(p.Path, p.Staged, ct).ConfigureAwait(false);
            return sides is null ? new GitDiffSidesDto(false) : new GitDiffSidesDto(true, sides.Original, sides.Modified);
        }).ConfigureAwait(false));

        router.Handle(GitMethods.Stage, async (p, _, ct) => await Guard(async () =>
        {
            await tools().GitOf(p.WorkspaceId).StageAsync(p.Paths, ct).ConfigureAwait(false);
            return Unit.Value;
        }).ConfigureAwait(false));

        router.Handle(GitMethods.Unstage, async (p, _, ct) => await Guard(async () =>
        {
            await tools().GitOf(p.WorkspaceId).UnstageAsync(p.Paths, ct).ConfigureAwait(false);
            return Unit.Value;
        }).ConfigureAwait(false));

        router.Handle(GitMethods.Commit, async (p, _, ct) => await Guard(async () => ToDto(await tools().GitOf(p.WorkspaceId).CommitAsync(p.Message, ct).ConfigureAwait(false))).ConfigureAwait(false));

        router.Handle(GitMethods.Checkout, async (p, _, ct) => await Guard(async () =>
        {
            await tools().GitOf(p.WorkspaceId).CheckoutAsync(p.Branch, ct).ConfigureAwait(false);
            return Unit.Value;
        }).ConfigureAwait(false));

        router.Handle(GitMethods.CreateBranch, async (p, _, ct) => await Guard(async () =>
        {
            var branch = await tools().GitOf(p.WorkspaceId).CreateBranchAsync(p.Name, p.Checkout, ct).ConfigureAwait(false);
            return new GitBranchDto(branch.Name, branch.Current, branch.Upstream);
        }).ConfigureAwait(false));

        router.Handle(GitMethods.Pull, async (p, _, ct) => await Guard(async () =>
        {
            await tools().GitOf(p.WorkspaceId).PullAsync(ct).ConfigureAwait(false);
            return Unit.Value;
        }).ConfigureAwait(false));

        router.Handle(GitMethods.Push, async (p, _, ct) => await Guard(async () =>
        {
            await tools().GitOf(p.WorkspaceId).PushAsync(ct).ConfigureAwait(false);
            return Unit.Value;
        }).ConfigureAwait(false));

        router.Handle(GitMethods.GenerateMessage, async (p, _, ct) => await Guard(async () =>
        {
            var t = tools();
            var message = await t.GitOf(p.WorkspaceId).GenerateCommitMessageAsync(t.CommitMessageModelFor(p.WorkspaceId), ct: ct).ConfigureAwait(false);
            return new GitGeneratedMessageDto(message);
        }).ConfigureAwait(false));
    }

    private static async ValueTask<T> Guard<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (GitException e)
        {
            throw Map(e);
        }
    }

    /// <summary>粗粒度错误码 + 稳定 key(+ git 的 stderr)。消息本身只有 key:stderr 里常有绝对路径、远端 URL,只放进 <c>details</c>。</summary>
    internal static ProtocolException Map(GitException e)
    {
        var details = FrameCodec.EncodePayload(new GitErrorDto(e.Key, e.Detail), PayloadShapes.GetShape<GitErrorDto>());
        return e.Kind switch
        {
            GitErrorKind.InvalidRequest => new ProtocolException(ErrorCodes.InvalidRequest, e.Key, details: details),
            GitErrorKind.NotAvailable => new ProtocolException(ErrorCodes.NotFound, e.Key, details: details),
            GitErrorKind.Busy => new ProtocolException(ErrorCodes.Busy, e.Key, retryable: true, details: details),
            GitErrorKind.Unavailable => new ProtocolException(ErrorCodes.Unavailable, e.Key, retryable: true, details: details),
            GitErrorKind.Unsupported => new ProtocolException(ErrorCodes.Unsupported, e.Key, details: details),
            _ => new ProtocolException(ErrorCodes.Conflict, e.Key, details: details),
        };
    }

    private static GitOverviewDto ToDto(GitOverview overview)
    {
        if (!overview.Available || overview.Status is not { } s)
        {
            return new GitOverviewDto(false, overview.Reason is { } r ? ReasonName(r) : null);
        }

        return new GitOverviewDto(
            true, null, overview.Root, s.Branch, s.Detached, s.Unborn, s.Upstream, s.Ahead, s.Behind,
            [.. s.Files.Select(static f => new GitFileChangeDto(f.Path, f.Index, f.Worktree, f.Staged, f.Unstaged, f.Untracked, f.Conflicted, f.RenamedFrom))], s.FilesTruncated);
    }

    private static string ReasonName(GitUnavailableReason reason) => reason switch
    {
        GitUnavailableReason.WorkspaceUnavailable => GitUnavailableReasons.WorkspaceUnavailable,
        GitUnavailableReason.GitMissing => GitUnavailableReasons.GitMissing,
        _ => GitUnavailableReasons.NotRepository,
    };

    private static GitCommitDto ToDto(GitCommit c) => new(c.Hash, c.ShortHash, c.Subject, c.Author, c.Timestamp);
}
