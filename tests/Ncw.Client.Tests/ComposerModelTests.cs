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

        public Task<bool> SetModelAsync(SessionRef session, string alias, string? providerId, string? defaultForWorkspace = null, CancellationToken ct = default)
        {
            ModelWrites.Add((session, alias, providerId, defaultForWorkspace));
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

    private static SessionDto Meta(SessionRef session, string model = "", string? provider = null) =>
        new(session.SessionId, "ws", null, "user", "t", null, model, provider, "code", "auto", false, false, 1, 2);

    private static void Show(ComposerViewModel composer, SessionRef session, SessionDto meta, string role = "owner") =>
        composer.Update(session, meta, null, null, SessionActivity.Idle, DeviceConnection.Connected, role, Options);

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
}
