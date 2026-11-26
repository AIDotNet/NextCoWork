using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>
/// 输入框的模型选择要记住(会话上记一份、owner 顺带写成项目默认值),换会话 / 重开时跟着设备上记住的走,不再每次落回第一个。
/// </summary>
public class ComposerModelTests
{
    private static readonly SessionRef A = new("local", "s-a");
    private static readonly SessionRef B = new("local", "s-b");

    private sealed class Commands : IConversationCommands
    {
        public List<(SessionRef Session, string Alias, string? Provider, string? Workspace)> ModelWrites { get; } = [];

        public List<(SessionRef Session, string Mode, string? ActiveRunId, string? Workspace)> PermissionWrites { get; } = [];

        public Task<bool> SetModelAsync(SessionRef session, string alias, string? providerId, string? defaultForWorkspace = null, CancellationToken ct = default)
        {
            ModelWrites.Add((session, alias, providerId, defaultForWorkspace));
            return Task.FromResult(true);
        }

        public Task<bool> SetPermissionModeAsync(SessionRef session, string mode, string? activeRunId = null, string? defaultForWorkspace = null, CancellationToken ct = default)
        {
            PermissionWrites.Add((session, mode, activeRunId, defaultForWorkspace));
            return Task.FromResult(true);
        }

        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) =>
            Task.FromResult(new SendResult(SendOutcome.Started));

        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default) =>
            Task.FromResult(new InteractionResult(InteractionOutcome.Answered));

        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }

    private static ModelOptionDto Option(string alias) =>
        new("anthropic", "Anthropic", alias, alias, alias, 200_000, 32_000, true, true, true, true, "effort", ["low", "high"], "anthropic");

    private static readonly ModelOptionDto[] Options = [Option("haiku"), Option("sonnet"), Option("opus")];

    private static SessionDto Meta(SessionRef session, string model = "", string? provider = null, string permission = "ask") =>
        new(session.SessionId, "ws", null, "user", "t", null, model, provider, "code", "auto", false, false, 1, 2, permission);

    private static void Show(ComposerViewModel composer, SessionRef session, SessionDto meta, string role = "owner", string? activeRunId = null) =>
        composer.Update(session, meta, null, activeRunId, activeRunId is null ? SessionActivity.Idle : SessionActivity.Running, DeviceConnection.Connected, role, Options);

    [Fact]
    public async Task Picking_a_model_remembers_it_on_the_session_and_as_the_project_default_for_an_owner()
    {
        var commands = new Commands();
        var composer = new ComposerViewModel(commands);
        Show(composer, A, Meta(A, "haiku", "anthropic"));
        Assert.Equal("haiku", composer.SelectedModel?.Alias);

        composer.PickModelCommand.Execute(composer.Models.Single(m => m.Alias == "opus"));
        await composer.WhenModelSaved;

        Assert.Equal([(A, "opus", "anthropic", "ws")], commands.ModelWrites);
        // 写回还在路上(会话主题还没推回来):不把用户的选择撤回去
        Show(composer, A, Meta(A, "haiku", "anthropic"));
        Assert.Equal("opus", composer.SelectedModel?.Alias);
    }

    [Fact]
    public async Task A_member_remembers_the_pick_on_the_session_only_and_picking_the_same_model_writes_nothing()
    {
        var commands = new Commands();
        var composer = new ComposerViewModel(commands);
        Show(composer, A, Meta(A, "haiku", "anthropic"), role: "member");

        composer.PickModelCommand.Execute(composer.Models.Single(m => m.Alias == "haiku"));
        composer.PickModelCommand.Execute(composer.Models.Single(m => m.Alias == "sonnet"));
        await composer.WhenModelSaved;

        Assert.Equal([(A, "sonnet", "anthropic", null)], commands.ModelWrites);
    }

    [Fact]
    public void Each_session_shows_its_own_remembered_model_and_a_pick_does_not_leak_into_the_next_session()
    {
        var composer = new ComposerViewModel(new Commands());
        Show(composer, A, Meta(A, "haiku", "anthropic"));
        composer.PickModelCommand.Execute(composer.Models.Single(m => m.Alias == "opus"));

        Show(composer, B, Meta(B, "sonnet", "anthropic"));
        Assert.Equal("sonnet", composer.SelectedModel?.Alias);

        // 没记模型的会话:兜底第一个可用的,而不是沿用上一个会话的选择
        Show(composer, A, Meta(A));
        Assert.Equal("haiku", composer.SelectedModel?.Alias);
    }

    [Fact]
    public void The_composer_follows_the_device_when_the_remembered_model_changes()
    {
        var composer = new ComposerViewModel(new Commands());
        // 快照还没到:先兜底
        composer.Update(A, null, null, null, SessionActivity.Idle, DeviceConnection.Connected, "owner", Options);
        Assert.Equal("haiku", composer.SelectedModel?.Alias);

        // 快照到了 / 别的控制端改了:跟设备走
        Show(composer, A, Meta(A, "opus", "anthropic"));
        Assert.Equal("opus", composer.SelectedModel?.Alias);
        Show(composer, A, Meta(A, "sonnet", "anthropic"));
        Assert.Equal("sonnet", composer.SelectedModel?.Alias);
    }

    [Fact]
    public async Task Sending_from_a_session_without_a_model_remembers_the_one_actually_used_on_the_session_only()
    {
        var commands = new Commands();
        var composer = new ComposerViewModel(commands);
        Show(composer, A, Meta(A));

        composer.Draft = "hello";
        await composer.SendCommand.ExecuteAsync(null);
        await composer.WhenModelSaved;

        Assert.Equal([(A, "haiku", "anthropic", null)], commands.ModelWrites);
    }

    [Fact]
    public async Task Picking_a_permission_mode_remembers_it_on_the_session_updates_the_active_run_and_writes_the_project_default_for_an_owner()
    {
        var commands = new Commands();
        var composer = new ComposerViewModel(commands);
        Show(composer, A, Meta(A, "haiku", "anthropic", permission: "ask"), activeRunId: "run-1");
        Assert.Equal("ask", composer.Permission);

        composer.SetPermissionCommand.Execute("full");
        await composer.WhenPermissionSaved;

        Assert.Equal([(A, "full", "run-1", "ws")], commands.PermissionWrites);
        // 写回还在路上(会话主题还没推回来):不把用户的选择撤回去
        Show(composer, A, Meta(A, "haiku", "anthropic", permission: "ask"), activeRunId: "run-1");
        Assert.Equal("full", composer.Permission);
    }

    [Fact]
    public async Task Each_session_shows_its_own_remembered_permission_and_a_member_clamps_full_and_writes_on_the_session_only()
    {
        var commands = new Commands();
        var composer = new ComposerViewModel(commands);
        Show(composer, A, Meta(A, "haiku", "anthropic", permission: "auto"));
        Assert.Equal("auto", composer.Permission);

        Show(composer, B, Meta(B, "sonnet", "anthropic", permission: "full"));
        Assert.Equal("full", composer.Permission);

        // 切到 member:full 被夹紧成 ask(ComposerLogic.ClampPermission);member 切档位只写会话,不写项目默认值也不调 owner 专属的 runs.setPermissionMode
        Show(composer, B, Meta(B, "sonnet", "anthropic", permission: "full"), role: "member", activeRunId: "run-2");
        Assert.Equal("ask", composer.Permission);

        composer.SetPermissionCommand.Execute("auto");
        await composer.WhenPermissionSaved;

        Assert.Equal([(B, "auto", null, null)], commands.PermissionWrites);
    }

    [Fact]
    public void CanUseFull_only_notifies_when_owner_role_actually_changes()
    {
        var composer = new ComposerViewModel(new Commands());
        Show(composer, A, Meta(A, "haiku", "anthropic"), role: "owner");

        var canUseFullEvents = 0;
        composer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ComposerViewModel.CanUseFull))
            {
                canUseFullEvents++;
            }
        };

        // 同角色反复刷新、打字都不应触发 CanUseFull(避免菜单反复重建,issue #204)
        Show(composer, A, Meta(A, "haiku", "anthropic"), role: "owner", activeRunId: "run-1");
        Show(composer, A, Meta(A, "haiku", "anthropic"), role: "owner", activeRunId: "run-1");
        composer.Draft = "streaming update";
        Assert.Equal(0, canUseFullEvents);

        // 角色真正变化时才通知
        Show(composer, A, Meta(A, "haiku", "anthropic"), role: "member", activeRunId: "run-1");
        Assert.Equal(1, canUseFullEvents);
    }
}
