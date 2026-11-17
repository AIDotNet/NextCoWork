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
        Assert.Equal(7810, remote.Port);
        Assert.True(remote.LanOnly);
        var menu = window.GetVisualDescendants().OfType<Ncw.UI.Controls.NcwDropdownMenu>().Single(m => m.Name == "RoleMenu");
        Assert.Equal(3, menu.ItemsSource!.Cast<object>().Count());
        Snapshot(window, "settings-remote");
    }

    [AvaloniaFact]
    public void Remote_page_shows_the_relay_address_state_and_lets_the_user_edit_it()
    {
        var (window, vm, stores) = Render();
        var state = new RemoteState(
            new RemoteStatus(true, 7810, 7810, true, ["192.168.1.23"], "482915", null, "wss://relay.example.com", "registered", null), []);
        stores.Workspace.Update(s => s with { Devices = s.Devices.SetItem("local", s.Devices["local"] with { Remote = state }) });
        vm.Settings.OpenCommand.Execute(null);
        vm.Settings.ShowCommand.Execute(SettingsSection.Remote);
        Dispatch();

        var remote = vm.Settings.Remote;
        Assert.Equal("wss://relay.example.com", remote.RelayUrl);
        Assert.Equal("wss://relay.example.com", remote.RelayDraft); // 输入框跟随设备上的值
        Assert.False(remote.CanApplyRelay);
        var input = window.GetVisualDescendants().OfType<Ncw.UI.Controls.NcwInput>().Single(i => i.Name == "RelayInput");
        Assert.Equal("wss://relay.example.com", input.Text);
        var stateText = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RelayStateText");
        Assert.True(stateText.IsVisible);
        Assert.Equal(Ncw.UI.Localization.NcwTexts.Get(Ncw.UI.Localization.NcwTextKind.RemoteRelayRegistered), stateText.Text);

        remote.RelayDraft = "";                       // 清空 = 关闭中继
        Assert.True(remote.CanApplyRelay);
        Snapshot(window, "settings-remote-relay");
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

    [AvaloniaFact]
    public void Models_page_is_a_provider_list_with_the_selected_provider_on_the_right()
    {
        var (window, vm, stores) = Render();
        stores.Workspace.Update(s => s with { Devices = s.Devices.SetItem("local", s.Devices["local"] with { Connection = DeviceConnection.Connected, Role = "owner" }) });
        stores.Providers.Update(_ => new ProvidersMirror(ImmutableDictionary<string, DeviceProviders>.Empty.Add("local", DeviceProviders.Unknown with
        {
            Known = true,
            Providers =
            [
                new ProviderDto("anthropic", "Anthropic", "anthropic", "https://api.anthropic.com", 10, true, "5m", null, false, null),
                new ProviderDto("ollama", "Ollama", "openai-chat", "http://localhost:11434/v1", 20, false, "5m", null, false, null),
            ],
            Aliases = [new ModelAliasDto("sonnet", "anthropic", "claude-sonnet-4-5", 200_000, 64_000, "Claude Sonnet 4.5", null, null, null, true, true, [], null, null)],
        })));
        vm.Settings.OpenCommand.Execute(null);
        vm.Settings.ShowPageCommand.Execute(SettingsPage.Models);
        Dispatch();

        var models = vm.Settings.Models;
        Assert.True(models.IsAvailable);
        Assert.True(vm.Settings.ShowsProviderList);
        Assert.True(Overlay(window).GetVisualDescendants().OfType<Control>().Single(c => c.Name == "CatalogPane").IsEffectivelyVisible);
        Snapshot(window, "settings-models-catalog");

        // 点列表栏里的供应商:详情区换成它(摘要 + 操作 + 模型别名),目录收起
        var row = window.GetVisualDescendants().OfType<Ncw.UI.Controls.PlainButton>().First(b => b.DataContext is ProviderItemViewModel { Id: "anthropic" });
        row.Command!.Execute(null);
        Dispatch();
        Assert.Equal("anthropic", models.SelectedProvider?.Id);
        Assert.Contains("active", row.Classes);
        Assert.False(Overlay(window).GetVisualDescendants().OfType<Control>().Single(c => c.Name == "CatalogPane").IsEffectivelyVisible);
        var detail = Overlay(window).GetVisualDescendants().OfType<Control>().Single(c => c.Name == "ProviderPane");
        Assert.True(detail.IsEffectivelyVisible);
        Assert.Contains(detail.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Claude Sonnet 4.5");
        Snapshot(window, "settings-models-provider");
    }
}
