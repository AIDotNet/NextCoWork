using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Ncw.Agent.Host;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;

namespace Ncw.UI.Tests;

/// <summary>界面 + 真实 Agent(进程内,真实本机 socket):会话列表走真实的 sessions.list,新建 / 重命名 / 收藏 / 归档 / 删除都经协议,列表跟着设备回来。</summary>
public class RealSessionsTests
{
    private sealed class Dialogs(string folder) : IDialogService
    {
        public string? NextName { get; set; }

        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) => Task.FromResult(NextName);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(folder);

        public Task ShowErrorAsync(string title, string message) => throw new Xunit.Sdk.XunitException($"{title}: {message}");

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => Task.FromResult(true);
    }

    private static async Task Pump(Func<bool> until, string what)
    {
        for (var i = 0; i < 400; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (until())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"Timed out waiting for: {what}");
    }

    [AvaloniaFact]
    public async Task Sessions_are_created_listed_renamed_favorited_archived_and_deleted_through_the_real_agent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ncws-{Guid.NewGuid():N}"[..16]);
        var folder = Path.Combine(Path.GetTempPath(), $"proj-{Guid.NewGuid():N}"[..14]);
        Directory.CreateDirectory(folder);
        var endpoint = LocalTransport.DefaultEndpoint(root);
        using var agentCts = new CancellationTokenSource();
        var agent = new AgentHost(new AgentOptions(AgentMode.Local, root, endpoint, "ui-token", null)).RunAsync(agentCts.Token);

        var stores = new ClientStores();
        var devices = new DeviceRegistry();
        await using var link = new DeviceLink(stores, AvaloniaUiDispatcher.Instance, "local", DeviceKind.Local, "本机",
            ct => LocalTransport.ConnectAsync(endpoint, ct), new ClientHello("ui-test", "3.0.0", LocalToken: "ui-token"));
        devices.Local = link;
        link.Start();

        var vm = new ShellViewModel(stores, devices: devices);
        var dialogs = new Dialogs(folder);
        var window = new Avalonia.Controls.Window { Width = 1500, Height = 860, Content = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) } };
        window.Show();
        vm.Dialogs = dialogs; // 外壳装配时会换成 Arc 的实现;测试里换成脚本化的

        await Pump(() => stores.Workspace.State.Devices.GetValueOrDefault(link.DeviceId) is { Connection: DeviceConnection.Connected, Data.State: Ncw.Protocol.Methods.DataStates.Ready }, $"agent connected and data ready (device: {stores.Workspace.State.Devices.GetValueOrDefault(link.DeviceId)?.Connection}, data: {stores.Workspace.State.Devices.GetValueOrDefault(link.DeviceId)?.Data?.State})");
        await vm.Projects.NewProjectAsync();
        await Pump(() => vm.Panel.HasProject, "project added");
        Assert.Equal("owner", stores.Workspace.State.Devices[link.DeviceId].Role);

        // 新建:选中 + 订阅 + 列表里出现
        await vm.Panel.NewSessionCommand.ExecuteAsync(null);
        await Pump(() => vm.Panel.Sessions.Count == 1, "session listed");
        var item = vm.Panel.Sessions.Single();
        Assert.True(item.IsSelected);
        Assert.True(vm.Conversation.HasSession);
        await Pump(() => vm.Conversation.IsEmpty, "snapshot arrived");
        Assert.False(item.IsRunning);
        Assert.Equal(item.Id, Assert.Single(vm.Workspace.Tabs).SessionId);

        // 重命名
        dialogs.NextName = "我的第一个会话";
        await item.RenameCommand.ExecuteAsync(null);
        await Pump(() => vm.Panel.Sessions.SingleOrDefault()?.Title == "我的第一个会话", "renamed");

        // 收藏
        await vm.Panel.Sessions.Single().ToggleFavoriteCommand.ExecuteAsync(null);
        await Pump(() => vm.Panel.Sessions.SingleOrDefault()?.IsFavorite == true, "favorited");

        // 归档:默认不显示;打开「显示已归档」才出现
        await vm.Panel.Sessions.Single().ToggleArchivedCommand.ExecuteAsync(null);
        await Pump(() => vm.Panel.Sessions.Count == 0, "archived hidden");
        Assert.True(vm.Panel.ShowsNoMatch && vm.Panel.HasAnySession); // 全被归档藏起来了:提示可以打开「显示已归档」
        vm.Panel.ToggleArchivedCommand.Execute(null);
        await Pump(() => vm.Panel.Sessions.Count == 1, "archived shown");
        Assert.True(vm.Panel.Sessions.Single().IsArchived);

        // 删除(二次确认后):从列表消失,选择回到「没有会话」
        await vm.Panel.Sessions.Single().DeleteCommand.ExecuteAsync(null);
        await Pump(() => vm.Panel.Sessions.Count == 0, "deleted");
        Assert.False(vm.Conversation.HasSession);

        await agentCts.CancelAsync();
        try { await agent; } catch (OperationCanceledException) { }
        Directory.Delete(root, recursive: true);
        Directory.Delete(folder, recursive: true);
    }
}
