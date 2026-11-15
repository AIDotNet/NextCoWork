using Ncw.Agent.Environments;
using Ncw.Agent.Workspace.Review;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Workspace;

/// <summary>
/// <c>review.*</c> 的方法实现(方法表在 <see cref="ReviewMethods"/>)。角色、承载、幂等键由路由统一检查,这里只做业务:校验参数 → 调 <see cref="ReviewService"/> → 映射成 DTO。
/// 没有这个变更集 / 文件不是错误(结果里是 null、空列表,撤销 / 重做是「什么都没动」),项目没了(<c>not_found</c>)或环境断开(<c>unavailable</c>,可重试)才是错误。
/// 撤销 / 重做的文件读写在项目的执行环境里做,路径只来自库里的记录,经路径守卫限定在项目根内。
/// </summary>
internal static class ReviewHandlers
{
    public static void Register(MethodRouter router, Func<WorkspaceToolsRuntime> tools)
    {
        router.Handle(ReviewMethods.ChangeSet, (p, _, _) =>
            ValueTask.FromResult(new ReviewChangeSetResult(tools().Review.ChangeSet(Require(p.RunId)) is { } set ? ToDto(set) : null)));
        router.Handle(ReviewMethods.List, (p, _, _) =>
            ValueTask.FromResult(new ReviewListResult([.. tools().Review.List(Require(p.SessionId)).Select(ToDto)])));
        router.Handle(ReviewMethods.FileDiff, (p, _, _) =>
            ValueTask.FromResult(new ReviewFileDiffResult(tools().Review.FileDiff(Require(p.RunId), p.Path) is { } d
                ? new ReviewFileDiffDto(d.Path, KindName(d.Kind), d.Before, d.After, d.Oversize)
                : null)));
        router.Handle(ReviewMethods.PrecheckUndo, async (p, ctx, ct) => await GuardAsync(async () =>
        {
            var runtime = tools();
            return runtime.Review.ChangeSet(Require(p.RunId)) is not { } set
                ? new ReviewPrecheckResult([])
                : new ReviewPrecheckResult([.. await WithProject(runtime, set, ctx.Role, ct, (env, root) => runtime.Review.PrecheckUndoAsync(p.RunId, env, root, ct)).ConfigureAwait(false)]);
        }).ConfigureAwait(false));
        router.Handle(ReviewMethods.Undo, async (p, ctx, ct) => await GuardAsync(async () =>
        {
            var runtime = tools();
            return runtime.Review.ChangeSet(Require(p.RunId)) is not { } set
                ? new ReviewMutationDto(p.RunId, ReviewStates.Applied, [])
                : ToDto(await WithProject(runtime, set, ctx.Role, ct, (env, root) => runtime.Review.UndoAsync(p.RunId, env, root, p.Force, ct)).ConfigureAwait(false));
        }).ConfigureAwait(false));
        router.Handle(ReviewMethods.Redo, async (p, ctx, ct) => await GuardAsync(async () =>
        {
            var runtime = tools();
            return runtime.Review.ChangeSet(Require(p.RunId)) is not { } set
                ? new ReviewMutationDto(p.RunId, ReviewStates.Applied, [])
                : ToDto(await WithProject(runtime, set, ctx.Role, ct, (env, root) => runtime.Review.RedoAsync(p.RunId, env, root, ct)).ConfigureAwait(false));
        }).ConfigureAwait(false));
    }

    private static string Require(string id) =>
        string.IsNullOrWhiteSpace(id) ? throw new ProtocolException(ErrorCodes.InvalidRequest, "An id is required") : id;

    private static async Task<T> WithProject<T>(WorkspaceToolsRuntime runtime, ReviewChangeSet set, Ncw.Protocol.Methods.Role role, CancellationToken ct, Func<IExecutionEnvironment, EnvPath, Task<T>> work)
    {
        // SSH 项目:先确保环境已连接(member 起才会发起连接,viewer 只能用已连着的)。review.* 按 Run id 进来,拿不到项目 id,所以不在路由前置闸门里。
        if (runtime.Conversation.Workspaces.Get(set.WorkspaceId) is { } record)
        {
            await runtime.Conversation.Environments.EnsureProjectReadyAsync(record, role >= Ncw.Protocol.Methods.Role.Member ? Ncw.Agent.Host.Environments.ConnectMode.Interactive : Ncw.Agent.Host.Environments.ConnectMode.None, ct).ConfigureAwait(false);
        }

        var project = runtime.Project(set.WorkspaceId);
        await project.Environment.EnsureReadyAsync().ConfigureAwait(false);
        return await work(project.Environment, project.Root).ConfigureAwait(false);
    }

    /// <summary>环境层的失败映射成协议错误:连接断开 / 过期是可重试的 <c>unavailable</c>,项目根没了 <c>not_found</c>,其余 <c>internal</c>(路由收成不带细节的内部错误)。</summary>
    private static async ValueTask<T> GuardAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.StaleGeneration or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.ResultUnknown)
        {
            throw new ProtocolException(ErrorCodes.Unavailable, "The project environment is not connected", retryable: true);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.EnvironmentNotFound)
        {
            throw new ProtocolException(ErrorCodes.NotFound, "The project folder is not available");
        }
    }

    private static ReviewChangeSetDto ToDto(ReviewChangeSet s) => new(
        s.RunId, s.SessionId, s.WorkspaceId, s.State == ReviewState.Reverted ? ReviewStates.Reverted : ReviewStates.Applied, s.FileCount, s.Additions, s.Deletions,
        [.. s.Files.Select(static f => new ReviewFileEntryDto(f.Path, KindName(f.Kind), f.Additions, f.Deletions, f.Oversize, f.InWorkspace))], s.UpdatedAt);

    private static ReviewMutationDto ToDto(ReviewMutation m) => new(
        m.RunId, m.State == ReviewState.Reverted ? ReviewStates.Reverted : ReviewStates.Applied,
        [.. m.Files.Select(static f => new ReviewFileResultDto(f.Path, StatusName(f.Status), f.Skip switch
        {
            ReviewSkipReason.Oversize => "oversize",
            ReviewSkipReason.OutsideWorkspace => "outsideWorkspace",
            _ => f.Error,
        }))]);

    private static string KindName(ReviewChangeKind kind) => kind switch
    {
        ReviewChangeKind.Created => ReviewChangeKinds.Created,
        ReviewChangeKind.Deleted => ReviewChangeKinds.Deleted,
        _ => ReviewChangeKinds.Modified,
    };

    private static string StatusName(ReviewFileStatus status) => status switch
    {
        ReviewFileStatus.Ok => ReviewFileStatuses.Ok,
        ReviewFileStatus.Conflict => ReviewFileStatuses.Conflict,
        ReviewFileStatus.Skipped => ReviewFileStatuses.Skipped,
        _ => ReviewFileStatuses.Error,
    };
}
