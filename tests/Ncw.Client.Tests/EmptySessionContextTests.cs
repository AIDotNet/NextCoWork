using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>空会话的上下文胶囊(docs/design/03 §11.4):项目名、执行环境、分支、未提交改动;git 概览未知时不拿旧值当现状(规则 7)。</summary>
public class EmptySessionContextTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private static readonly ProjectRef Project = new("local", "nextcowork");
    private const string SessionId = "s-empty";

    private static ClientStores EmptySession()
    {
        var stores = MockWorkspace.Create(Now);
        stores.Providers.Update(_ => new ProvidersMirror(ImmutableDictionary<string, DeviceProviders>.Empty.Add("local", DeviceProviders.Unknown with { Known = true, Options = [] })));
        stores.Selection.Update(_ => new SelectionState(Project, SessionId));
        var meta = new SessionDto(SessionId, "nextcowork", null, "user", "t", null, "opus", null, "code", "auto", false, false, 1, 2);
        stores.Conversations.Update(c => ConversationReducer.ApplySession(c, "local",
            new SessionTopicState(new SessionPageDto(meta, [], false, [], [], []), new QueueDto(SessionId, 1, []), null)));
        return stores;
    }

    [Fact]
    public void Shows_the_project_and_environment_and_pulls_git_once()
    {
        var stores = EmptySession();
        var git = new CountingGit(new MockGit(stores));
        var vm = new ConversationViewModel(stores, new NoSend(), git: git);

        Assert.True(vm.IsEmpty);
        Assert.Equal("NextCoWork", vm.EmptyProjectName);
        Assert.Equal("本机", vm.EmptyEnvLabel); // 本机项目:只有设备名
        Assert.False(vm.EmptyEnvIsSsh);

        // 镜像里没有这个项目 → 拉一次;拉到了就显示分支,改动数来自 Core 的唯一汇总
        Assert.Equal(1, git.Refreshes);
        Assert.True(vm.HasEmptyBranch);
        Assert.Equal("v3/avalonia", vm.EmptyBranch);
        Assert.Equal(stores.Git.State.Of(Project).Summary.Total, vm.EmptyChanges);
        Assert.Equal(vm.EmptyChanges > 0, vm.HasEmptyChanges);

        // 之后的刷新不再拉
        stores.Workspace.Update(s => s with { });
        Assert.Equal(1, git.Refreshes);
    }

    [Fact]
    public void Unknown_git_hides_branch_and_changes()
    {
        var stores = EmptySession();
        var vm = new ConversationViewModel(stores, new NoSend(), git: new MockGit(stores));
        stores.Git.Update(m => GitReducer.Apply(m, Project,
            new GitOverviewDto(true, null, "/x", "feature/a", Files: [new GitFileChangeDto("a.cs", ".", "M", false, true, false, false, null)]), long.MaxValue));
        Assert.Equal("feature/a", vm.EmptyBranch);
        Assert.Equal(1, vm.EmptyChanges);

        stores.Git.Update(m => GitReducer.MarkDeviceUnknown(m, "local"));

        Assert.False(vm.HasEmptyBranch);
        Assert.Null(vm.EmptyBranch);
        Assert.False(vm.HasEmptyChanges);
    }

    [Fact]
    public void Remote_environment_carries_its_name_and_detached_head_shows_no_branch()
    {
        var stores = EmptySession();
        stores.Workspace.Update(s => s with
        {
            Projects = s.Projects.SetItem(Project, s.Projects[Project] with { Environment = EnvironmentKind.Ssh, EnvironmentName = "dev-server" }),
        });
        var vm = new ConversationViewModel(stores, new NoSend());
        stores.Git.Update(m => GitReducer.Apply(m, Project, new GitOverviewDto(true, null, "/x", "abc1234", Detached: true), long.MaxValue));

        Assert.Equal("本机 · dev-server", vm.EmptyEnvLabel);
        Assert.True(vm.EmptyEnvIsSsh);
        Assert.False(vm.HasEmptyBranch);
    }

    private sealed class CountingGit(IGitCommands inner) : IGitCommands
    {
        public int Refreshes { get; private set; }

        public Task<ActionResult<GitOverviewDto>> RefreshAsync(ProjectRef project, CancellationToken ct = default)
        {
            Refreshes++;
            return inner.RefreshAsync(project, ct);
        }

        public Task<ActionResult<GitBranchesResult>> ListBranchesAsync(ProjectRef project, CancellationToken ct = default) => inner.ListBranchesAsync(project, ct);

        public Task<ActionResult<GitCommitsResult>> ListCommitsAsync(ProjectRef project, int limit = 50, CancellationToken ct = default) => inner.ListCommitsAsync(project, limit, ct);

        public Task<ActionResult<GitDiffDto>> GetDiffAsync(ProjectRef project, string path, bool staged, CancellationToken ct = default) => inner.GetDiffAsync(project, path, staged, ct);


        public Task<ActionResult<Unit>> StageAsync(ProjectRef project, IReadOnlyList<string> paths, CancellationToken ct = default) => inner.StageAsync(project, paths, ct);

        public Task<ActionResult<Unit>> UnstageAsync(ProjectRef project, IReadOnlyList<string> paths, CancellationToken ct = default) => inner.UnstageAsync(project, paths, ct);

        public Task<ActionResult<GitCommitDto>> CommitAsync(ProjectRef project, string message, CancellationToken ct = default) => inner.CommitAsync(project, message, ct);

        public Task<ActionResult<Unit>> CheckoutAsync(ProjectRef project, string branch, CancellationToken ct = default) => inner.CheckoutAsync(project, branch, ct);

        public Task<ActionResult<GitBranchDto>> CreateBranchAsync(ProjectRef project, string name, bool checkout, CancellationToken ct = default) => inner.CreateBranchAsync(project, name, checkout, ct);

        public Task<ActionResult<Unit>> PullAsync(ProjectRef project, CancellationToken ct = default) => inner.PullAsync(project, ct);

        public Task<ActionResult<Unit>> PushAsync(ProjectRef project, CancellationToken ct = default) => inner.PushAsync(project, ct);

        public Task<ActionResult<GitGeneratedMessageDto>> GenerateMessageAsync(ProjectRef project, CancellationToken ct = default) => inner.GenerateMessageAsync(project, ct);

    }

    private sealed class NoSend : IConversationCommands
    {
        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(new SendResult(SendOutcome.Started));

        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default) =>
            Task.FromResult(new InteractionResult(InteractionOutcome.Answered));

        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }
}
