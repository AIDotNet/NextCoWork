using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Devices;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.UI.Chrome;
using Ncw.UI.Views.Desktop;

namespace Ncw.UI.Tests;

/// <summary>启动时的数据页:按 Store 里本机设备的数据状态盖住窗口(docs/design/04 §4)。</summary>
public class DataGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private sealed class Inline : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private static (Window Window, ShellViewModel Vm, ClientStores Stores) Render()
    {
        var stores = MockWorkspace.Create(Now);
        // 本机设备的连接(不启动):Mock 数据里的本机设备 id 就是 "local"。
        var devices = new DeviceRegistry
        {
            Local = new DeviceLink(stores, new Inline(), "local", DeviceKind.Local, "local",
                _ => throw new InvalidOperationException(), new ClientHello("tests", "3.0.0")),
        };
        var vm = new ShellViewModel(stores, devices: devices);
        var window = new Window { Width = 1200, Height = 760, Content = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) } };
        window.Show();
        Dispatch();
        return (window, vm, stores);
    }

    private static void SetData(ClientStores stores, DataStatus? status) =>
        stores.Workspace.Update(s => s with { Devices = s.Devices.SetItem("local", s.Devices["local"] with { Data = status }) });

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

    private static DataGateOverlay Overlay(Window window) => window.GetVisualDescendants().OfType<DataGateOverlay>().Single();

    private static string TitleOf(Window window) => Overlay(window).FindControl<TextBlock>("Title")!.Text ?? "";

    [AvaloniaFact]
    public void Hidden_when_data_is_unknown_or_ready()
    {
        var (window, _, stores) = Render();
        Assert.False(Overlay(window).IsVisible);

        SetData(stores, new DataStatus(DataStates.Ready, null, null, null));
        Dispatch();
        Assert.False(Overlay(window).IsVisible);
    }

    [AvaloniaFact]
    public void Shows_takeover_progress()
    {
        var (window, vm, stores) = Render();

        SetData(stores, new DataStatus(DataStates.TakingOver, DataSteps.BackingUp, null, null));
        Dispatch();

        Assert.True(Overlay(window).IsVisible);
        Assert.Equal(DataGateMode.TakingOver, vm.DataGate.Mode);
        Assert.False(vm.DataGate.CanRetry);
        Assert.NotEmpty(TitleOf(window));
        Snapshot(window, "data-gate-taking-over");
    }

    [AvaloniaFact]
    public void Blocked_by_running_2x_offers_retry()
    {
        var (window, vm, stores) = Render();

        SetData(stores, new DataStatus(DataStates.Blocked, null, DataReasons.LegacyAppRunning, null));
        Dispatch();

        Assert.Equal(DataGateMode.Blocked, vm.DataGate.Mode);
        Assert.True(vm.DataGate.CanRetry);
        Snapshot(window, "data-gate-blocked");
    }

    [AvaloniaFact]
    public void Completed_takeover_shows_summary_until_continue()
    {
        var (window, vm, stores) = Render();

        SetData(stores, new DataStatus(DataStates.Ready, null, null, new TakeoverSummary(23, 631, 0)));
        Dispatch();
        Assert.Equal(DataGateMode.Completed, vm.DataGate.Mode);
        Snapshot(window, "data-gate-completed");

        vm.DataGate.ContinueCommand.Execute(null);
        Dispatch();
        Assert.False(Overlay(window).IsVisible);
    }

    [AvaloniaFact]
    public void Data_handed_back_to_2x_waits_for_an_explicit_move_again()
    {
        var (window, vm, stores) = Render();

        SetData(stores, new DataStatus(DataStates.RolledBack, null, null, null));
        Dispatch();

        Assert.True(Overlay(window).IsVisible);
        Assert.Equal(DataGateMode.RolledBack, vm.DataGate.Mode);
        Assert.True(vm.DataGate.IsRolledBack);
        Assert.False(vm.DataGate.CanRetry); // 不是「出错了重试」,是「重新迁移」
        Assert.NotEmpty(TitleOf(window));
        Snapshot(window, "data-gate-rolled-back");
    }

    [AvaloniaFact]
    public void Completed_takeover_lists_keys_to_reenter()
    {
        var (window, _, stores) = Render();

        SetData(stores, new DataStatus(DataStates.Ready, null, null, new TakeoverSummary(23, 631, 2)));
        Dispatch();

        Snapshot(window, "data-gate-completed-reenter");
    }

    [AvaloniaFact]
    public void Credentials_step_warns_about_the_keychain()
    {
        var (window, _, stores) = Render();

        SetData(stores, new DataStatus(DataStates.TakingOver, DataSteps.Credentials, null, null));
        Dispatch();

        Snapshot(window, "data-gate-credentials");
    }

    [AvaloniaFact]
    public void Disconnect_hides_the_page_instead_of_showing_stale_progress()
    {
        var (window, _, stores) = Render();
        SetData(stores, new DataStatus(DataStates.TakingOver, DataSteps.Migrating, null, null));
        Dispatch();

        SetData(stores, null);
        Dispatch();

        Assert.False(Overlay(window).IsVisible);
    }
}
