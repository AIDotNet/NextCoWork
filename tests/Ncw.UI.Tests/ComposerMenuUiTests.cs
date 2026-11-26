using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop.Conversation;

namespace Ncw.UI.Tests;

/// <summary>输入框菜单与模型选择器(issue #204):流式刷新不重建菜单、隐藏停用供应商模型、按供应商折叠。</summary>
public class ComposerMenuUiTests
{
    private static readonly SessionRef Session = new("local", "s-1");

    private sealed class StubCommands : IConversationCommands
    {
        public Task<bool> SetModelAsync(SessionRef session, string alias, string? providerId, string? defaultForWorkspace = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SetPermissionModeAsync(SessionRef session, string mode, string? activeRunId = null, string? defaultForWorkspace = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<SendResult> SendAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(new SendResult(SendOutcome.Started));
        public Task<bool> EnqueueAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SteerAsync(SessionRef session, string text, SendOptionsDto options, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> AbortAsync(RunRef run, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> PromoteQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RemoveQueuedAsync(SessionRef session, string itemId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<InteractionResult> RespondAsync(string deviceId, InteractionDto interaction, InteractionResponseDto response, CancellationToken ct = default) => Task.FromResult(new InteractionResult(InteractionOutcome.Answered));
        public Task<SessionRef?> CreateSessionAsync(ProjectRef project, string? model = null, CancellationToken ct = default) => Task.FromResult<SessionRef?>(null);
    }

    private static ModelOptionDto Option(string providerId, string providerName, string alias, bool available = true) =>
        new(providerId, providerName, alias, alias, alias, 200_000, 32_000, true, true, true, available, "effort", ["low", "high"], providerId);

    private static SessionDto Meta(string model = "m1", string? provider = "p1") =>
        new(Session.SessionId, "ws", null, "user", "t", null, model, provider, "code", "auto", false, false, 1, 2, "ask");

    private static void Dispatch()
    {
        for (var i = 0; i < 2; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaFact]
    public void Streaming_updates_do_not_rebuild_menus()
    {
        var vm = new ComposerViewModel(new StubCommands());
        var options = new[] { Option("p1", "OpenAI", "m1"), Option("p2", "Anthropic", "m2") };
        vm.Update(Session, Meta("m1", "p1"), null, "run-1", SessionActivity.Running, DeviceConnection.Connected, "owner", options);

        var view = new ComposerView { DataContext = vm };
        var window = new Window { Width = 900, Height = 400, Content = view };
        window.Show();
        Dispatch();

        var menuBuildsBefore = view.MenuRebuildCount;
        var modelBuildsBefore = view.ModelPickerRebuildCount;

        // 模拟运行期间频繁推送状态更新与输入草稿变化:菜单不应重建
        for (var i = 0; i < 20; i++)
        {
            vm.Update(Session, Meta("m1", "p1"), null, "run-1", SessionActivity.Running, DeviceConnection.Connected, "owner", options);
            vm.Draft = $"msg {i}";
        }

        Dispatch();
        Assert.Equal(menuBuildsBefore, view.MenuRebuildCount);
        Assert.Equal(modelBuildsBefore, view.ModelPickerRebuildCount);

        window.Close();
        Dispatch();
    }

    [AvaloniaFact]
    public void Model_picker_hides_disabled_providers_and_toggles_provider_collapse()
    {
        var vm = new ComposerViewModel(new StubCommands());
        var options = new[]
        {
            Option("p1", "Provider 1", "m1"),
            Option("p1", "Provider 1", "m2"),
            Option("p1", "Provider 1", "m3"),
            Option("p1", "Provider 1", "m4"),
            Option("p2", "Provider 2", "m5"),
            Option("p2", "Provider 2", "m6"),
            Option("p2", "Provider 2", "m7"),
            Option("p2", "Provider 2", "m8"),
            Option("p3", "Provider 3", "m9"),
            Option("p3", "Provider 3", "m10"),
            Option("p3", "Provider 3", "m11"),
            Option("p4", "Provider 4", "m12"),
            Option("p4", "Provider 4", "m13"),
            Option("disabled-p", "Disabled Provider", "d1", available: false),
        };
        vm.Update(Session, Meta("m1", "p1"), null, null, SessionActivity.Idle, DeviceConnection.Connected, "owner", options);

        // 停用供应商的模型不出现在可见列表里
        var visible = ComposerView.VisibleModels(vm);
        Assert.DoesNotContain(visible, m => m.ProviderId == "disabled-p");

        var view = new ComposerView { DataContext = vm };
        var window = new Window { Width = 900, Height = 400, Content = view };
        window.Show();
        Dispatch();

        // 供应商多且模型多时:当前选中模型所在供应商展开,其他默认折叠
        Assert.False(view.IsProviderCollapsed("p1"));
        Assert.True(view.IsProviderCollapsed("p2"));

        // 点击 p2 组头后展开,再点一次折叠
        var panel = view.FindControl<StackPanel>("ModelListPanel")!;
        var p2Header = panel.GetVisualDescendants().OfType<PlainButton>().Single(b => Equals(b.Tag, "p2"));
        p2Header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(view.IsProviderCollapsed("p2"));

        p2Header.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(view.IsProviderCollapsed("p2"));

        window.Close();
        Dispatch();
    }
}
