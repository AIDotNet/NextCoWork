using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
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
using Ncw.UI.Views.Desktop.Rail;

namespace Ncw.UI.Tests;

/// <summary>界面 + 真实 Agent(进程内,真实本机 socket):空状态 → 点「+」→ 选文件夹 → 项目出现在项目栏和面板。</summary>
public class RealAgentTests
{
    private sealed class ScriptedDialogs(string folder) : IDialogService
    {
        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(folder);

        public Task ShowErrorAsync(string title, string message) => throw new Xunit.Sdk.XunitException($"{title}: {message}");
    }

    private static async Task Pump(Func<bool> until, string what)
    {
        for (var i = 0; i < 300; i++)
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

    private static void Snapshot(Window window, string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    [AvaloniaFact]
    public async Task Empty_state_then_plus_adds_a_real_project()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ncwu-{Guid.NewGuid():N}"[..16]);
        var folder = Path.Combine(Path.GetTempPath(), $"my-app-{Guid.NewGuid():N}"[..14]);
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
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = 1500, Height = 860, Content = shell };
        window.Show();
        vm.Dialogs = new ScriptedDialogs(folder); // 换掉 Arc 的系统文件夹选择框

        await Pump(() => link.IsConnected, "agent connected");
        Assert.False(vm.Panel.HasProject);
        Snapshot(window, "real-agent-empty");

        // 真的点项目栏底部的「+」
        var plus = shell.GetVisualDescendants().OfType<ProjectRail>().Single()
            .GetVisualDescendants().OfType<Ncw.UI.Controls.PlainButton>()
            .Single(b => ReferenceEquals(b.Command, vm.Rail.AddProjectCommand));
        var at = plus.TranslatePoint(new Point(plus.Bounds.Width / 2, plus.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);

        await Pump(() => vm.Panel.HasProject, "project added");
        Assert.Equal(Path.GetFileName(folder), vm.Panel.Name);
        Assert.Equal(folder, vm.Panel.Path);
        var tile = Assert.Single(vm.Rail.Items.OfType<RailProjectViewModel>());
        Assert.True(tile.IsSelected);
        Assert.False(tile.IsOffline);
        Snapshot(window, "real-agent-project");

        await agentCts.CancelAsync();
        try { await agent; } catch (OperationCanceledException) { }
        Directory.Delete(root, recursive: true);
        Directory.Delete(folder, recursive: true);
    }
}
