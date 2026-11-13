using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;

namespace Ncw.UI.Tests;

/// <summary>设置页(docs/design/07):设备、连接与远程。数据只来自 Store。</summary>
public class SettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class Inline : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private static (Window Window, ShellViewModel Vm, ClientStores Stores) Render()
    {
        var stores = MockWorkspace.Create(Now);
        var devices = new DeviceRegistry
        {
            Local = new DeviceLink(stores, new Inline(), "local", DeviceKind.Local, "local",
                _ => throw new InvalidOperationException(), new ClientHello("tests", "3.0.0")),
            Remote = new RemoteDeviceManager(stores, new Inline(), new EphemeralControllerIdentity(), new MemoryDeviceBook(), "tests"),
        };
        var vm = new ShellViewModel(stores, devices: devices);
        var window = new Window { Width = 1200, Height = 760, Content = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) } };
        window.Show();
        Dispatch();
        return (window, vm, stores);
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Snapshot(Window window, string name)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(dir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static SettingsOverlay Overlay(Window window) => window.GetVisualDescendants().OfType<SettingsOverlay>().Single();

    [AvaloniaFact]
    public void Settings_button_opens_and_escape_closes()
    {
        var (window, vm, _) = Render();
        Assert.False(Overlay(window).IsVisible);

        var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SettingsButton");
        button.Command!.Execute(null);
        Dispatch();
        Assert.True(Overlay(window).IsVisible);

        vm.Settings.CloseCommand.Execute(null);
        Dispatch();
        Assert.False(Overlay(window).IsVisible);
    }

    [AvaloniaFact]
    public void Devices_page_lists_paired_devices_and_the_add_form()
    {
        var (window, vm, stores) = Render();
        stores.Paired.Update(_ => ImmutableArray.Create(
            new SavedDevice("studio", "192.168.1.40:7810", "ab", "192.168.1.40", 7810, "member", "t", 0)));
        vm.Settings.OpenCommand.Execute(null);
        vm.Settings.Devices.Address = "192.168.1.23";
        Dispatch();

        Assert.True(vm.Settings.Devices.CanAdd);
        var item = Assert.Single(vm.Settings.Devices.Items);
        Assert.Equal("工作室 Mac mini", item.Name); // Store 里设备自己报告的名字优先
        Assert.Equal(DeviceConnection.Offline, item.Connection);
        Snapshot(window, "settings-devices");
    }

    [AvaloniaFact]
    public void Remote_page_shows_switch_addresses_code_and_controllers()
    {
        var (window, vm, stores) = Render();
        var state = new RemoteState(
            new RemoteStatus(true, 7810, 7810, true, ["192.168.1.23", "fd12::5"], "482915", null),
            [new TrustedControllerDto("9f3ac2e1aa", "张三的 iPhone", "member", 0, 0, true)]);
        stores.Workspace.Update(s => s with { Devices = s.Devices.SetItem("local", s.Devices["local"] with { Remote = state }) });
        vm.Settings.OpenCommand.Execute(null);
        vm.Settings.ShowCommand.Execute(SettingsSection.Remote);
        Dispatch();

        var remote = vm.Settings.Remote;
        Assert.True(remote.IsAvailable);
        Assert.True(remote.Enabled);
        Assert.Equal("482 915", remote.PairingCode);
        Assert.Equal(["192.168.1.23:7810", "[fd12::5]:7810"], remote.Addresses);
        Assert.Equal("9F3A C2E1", Assert.Single(remote.Controllers).ShortFingerprint);
        Snapshot(window, "settings-remote");
    }

    [AvaloniaFact]
    public void Remote_page_says_unavailable_when_the_local_agent_is_not_connected()
    {
        var (window, vm, _) = Render();
        vm.Settings.OpenCommand.Execute(null);
        vm.Settings.ShowCommand.Execute(SettingsSection.Remote);
        Dispatch();

        Assert.False(vm.Settings.Remote.IsAvailable);
        Snapshot(window, "settings-remote-unavailable");
    }
}
