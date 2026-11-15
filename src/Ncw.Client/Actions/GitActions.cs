using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// Git 面板相关的用户意图(AGENTS.md §3 规则 4:一个意图一个方法)。仓库的真源在设备(命令经项目的执行环境跑,本机和 SSH 项目同一套);
/// 控制端只在 <see cref="ClientStores.Git"/> 里镜像「概览」(分组、分支、ahead / behind):<see cref="RefreshAsync"/> 拉取并写入,
/// <b>每个写操作成功之后自己刷新一次概览</b>,所以界面只需要订阅 Store,不用自己在每个按钮后面再拉。
/// diff、分支列表、提交列表是界面的纯 UI 状态,直接返回给调用方,不进 Store。
/// 失败是结构化的 <see cref="ActionResult{T}"/>:<c>ErrorKey</c> 是稳定的 <c>git.*</c> key(界面翻译),<c>Detail</c> 是 git 的英文诊断(可以原样展示在「详情」里)。
/// 「开不出来」(没有仓库 / 没装 git / 目录没了)不是失败:<see cref="RefreshAsync"/> 成功,概览的 <c>Available</c> = false 带原因。
/// </summary>
public sealed class GitActions(DeviceRegistry devices, ClientStores stores) : IGitCommands
{
    private static long _seq;

    /// <summary>拉取并刷新这个项目的 git 概览(用户打开面板、点刷新、项目切换时调)。</summary>
    public async Task<ActionResult<GitOverviewDto>> RefreshAsync(ProjectRef project, CancellationToken ct = default)
    {
        // 序号在发起时领:结果乱序到达时,Store 只收不更旧的(见 GitReducer.Apply)
        var seq = Interlocked.Increment(ref _seq);
        var result = await Run(project.DeviceId, link => link.CallAsync(GitMethods.Overview, new GitWorkspaceParams(project.WorkspaceId), ct)).ConfigureAwait(false);
        if (result.IsOk && result.Value is { } overview)
        {
            // Store 只在 UI 线程上读写:经这台设备的调度器更新
            devices.Find(project.DeviceId)?.PostToUi(() => stores.Git.Update(m => GitReducer.Apply(m, project, overview, seq)));
        }

        return result;
    }

    public Task<ActionResult<GitBranchesResult>> ListBranchesAsync(ProjectRef project, CancellationToken ct = default) =>
        Run(project.DeviceId, link => link.CallAsync(GitMethods.Branches, new GitWorkspaceParams(project.WorkspaceId), ct));

    public Task<ActionResult<GitCommitsResult>> ListCommitsAsync(ProjectRef project, int limit = 50, CancellationToken ct = default) =>
        Run(project.DeviceId, link => link.CallAsync(GitMethods.Commits, new GitCommitsParams(project.WorkspaceId, limit), ct));

    /// <summary>某个文件的 unified diff。<paramref name="path"/> 是仓库相对路径(取自概览里的文件)。</summary>
    public Task<ActionResult<GitDiffDto>> GetDiffAsync(ProjectRef project, string path, bool staged, CancellationToken ct = default) =>
        Run(project.DeviceId, link => link.CallAsync(GitMethods.Diff, new GitDiffParams(project.WorkspaceId, path, staged), ct));

    /// <summary>对比视图的两侧全文;<c>Comparable</c> = false 时退回 <see cref="GetDiffAsync"/>。</summary>
    public Task<ActionResult<GitDiffSidesDto>> GetDiffSidesAsync(ProjectRef project, string path, bool staged, CancellationToken ct = default) =>
        Run(project.DeviceId, link => link.CallAsync(GitMethods.DiffSides, new GitDiffParams(project.WorkspaceId, path, staged), ct));

    public Task<ActionResult<Unit>> StageAsync(ProjectRef project, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        Mutate(project, GitMethods.Stage, new GitPathsParams(project.WorkspaceId, [.. paths]), "stage", ct);

    public Task<ActionResult<Unit>> UnstageAsync(ProjectRef project, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        Mutate(project, GitMethods.Unstage, new GitPathsParams(project.WorkspaceId, [.. paths]), "unstage", ct);

    /// <summary>提交暂存区。成功返回新提交。</summary>
    public Task<ActionResult<GitCommitDto>> CommitAsync(ProjectRef project, string message, CancellationToken ct = default) =>
        Mutate(project, GitMethods.Commit, new GitCommitParams(project.WorkspaceId, message), "commit", ct);

    public Task<ActionResult<Unit>> CheckoutAsync(ProjectRef project, string branch, CancellationToken ct = default) =>
        Mutate(project, GitMethods.Checkout, new GitCheckoutParams(project.WorkspaceId, branch), "checkout", ct);

    public Task<ActionResult<GitBranchDto>> CreateBranchAsync(ProjectRef project, string name, bool checkout, CancellationToken ct = default) =>
        Mutate(project, GitMethods.CreateBranch, new GitCreateBranchParams(project.WorkspaceId, name, checkout), "createBranch", ct);

    /// <summary><c>git pull --ff-only</c>:快进不了就如实失败(<see cref="ActionOutcome.Conflict"/> + 设备给的 git 诊断)。</summary>
    public Task<ActionResult<Unit>> PullAsync(ProjectRef project, CancellationToken ct = default) =>
        Mutate(project, GitMethods.Pull, new GitWorkspaceParams(project.WorkspaceId), "pull", ct);

    public Task<ActionResult<Unit>> PushAsync(ProjectRef project, CancellationToken ct = default) =>
        Mutate(project, GitMethods.Push, new GitWorkspaceParams(project.WorkspaceId), "push", ct);

    /// <summary>用设置里的默认模型写一条提交信息草稿(填进输入框,不直接提交)。会花模型的钱,member 起。</summary>
    public Task<ActionResult<GitGeneratedMessageDto>> GenerateMessageAsync(ProjectRef project, CancellationToken ct = default) =>
        Run(project.DeviceId, link => link.CallAsync(GitMethods.GenerateMessage, new GitWorkspaceParams(project.WorkspaceId), ct));

    /// <summary>界面不再关心这个项目(页签关了):丢掉它的镜像。</summary>
    public void Forget(ProjectRef project) =>
        devices.Find(project.DeviceId)?.PostToUi(() => stores.Git.Update(m => GitReducer.Remove(m, project)));

    /// <summary>写操作:成功之后刷新概览(分组、ahead / behind、分支名都可能变了),再把写操作的结果交还给调用方。</summary>
    private async Task<ActionResult<TResult>> Mutate<TParams, TResult>(ProjectRef project, MethodDef<TParams, TResult> method, TParams parameters, string what, CancellationToken ct)
    {
        var result = await Run(project.DeviceId, link => link.CallAsync(method, parameters, ct, idem: Idem(what))).ConfigureAwait(false);
        if (result.IsOk)
        {
            await RefreshAsync(project, ct).ConfigureAwait(false);
        }

        return result;
    }

    private static string Idem(string what) => $"git.{what}:{Guid.NewGuid():N}";

    private async Task<ActionResult<T>> Run<T>(string deviceId, Func<DeviceLink, Task<T>> call)
    {
        if (devices.Find(deviceId) is not { IsConnected: true } link)
        {
            return ActionResult<T>.NotConnected();
        }

        try
        {
            return ActionResult<T>.Success(await call(link).ConfigureAwait(false));
        }
        catch (ProtocolException e)
        {
            return ActionResult<T>.From(e);
        }
    }
}
