using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Mobile;

/// <summary>推送登记:令牌经 push.register 登记到每台已连接且声明了推送能力的设备;设备重连、令牌刷新后重新登记。</summary>
public class PushViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakePush(PushPermission permission = PushPermission.Granted, string? token = "tok-1") : IPushRegistration
    {
        public bool IsSupported => true;

        public bool IsConfigured { get; set; } = true;

        public PushPlatform Platform => PushPlatform.Apns;

        public PushPermission Permission { get; set; } = permission;

        public string? Token { get; set; } = token;

        public int Requests { get; private set; }

        public event Action<string>? TokenRefreshed;

        public Task<PushPermission> GetPermissionAsync(CancellationToken ct = default) => Task.FromResult(Permission);

        public Task<PushPermission> RequestPermissionAsync(CancellationToken ct = default)
        {
            Requests++;
            Permission = PushPermission.Granted;
            return Task.FromResult(Permission);
        }

        public Task<string?> GetTokenAsync(CancellationToken ct = default) => Task.FromResult(Token);

        public void Refresh(string token) => TokenRefreshed?.Invoke(token);
    }

    private sealed class FakeCommands : IPushCommands
    {
        public HashSet<string> Supporting { get; } = ["local", "cloud"];

        public bool Fail { get; set; }

        public List<(string Device, PushPlatform Platform, string Token)> Registered { get; } = [];

        public bool Supports(string deviceId) => Supporting.Contains(deviceId);

        public Task<ActionResult<PushRegisterResult>> RegisterAsync(string deviceId, PushPlatform platform, string token, string? appVersion = null, CancellationToken ct = default)
        {
            Registered.Add((deviceId, platform, token));
            return Task.FromResult(Fail ? new ActionResult<PushRegisterResult>(ActionOutcome.Forbidden) : new ActionResult<PushRegisterResult>(ActionOutcome.Ok, new PushRegisterResult("apns", 1)));
        }
    }

    private static (PushViewModel Vm, ClientStores Stores, FakeCommands Commands) Make(IPushRegistration? push)
    {
        var stores = MockWorkspace.Create(Now); // 本机连着,云端休眠,工作室离线
        var commands = new FakeCommands();
        return (new PushViewModel(stores, commands, () => push, "3.0.0"), stores, commands);
    }

    [Fact]
    public async Task A_platform_without_push_configuration_is_not_an_error_and_never_asks_or_registers()
    {
        var push = new FakePush { IsConfigured = false };
        var (vm, _, commands) = Make(push);

        await vm.RefreshAsync();
        await vm.EnableCommand.ExecuteAsync(null);

        Assert.Equal(PushStatus.NotConfigured, vm.Status);
        Assert.False(vm.CanEnable);
        Assert.False(vm.IsSupported);
        Assert.Equal(0, push.Requests); // 没配置就不弹系统权限对话框
        Assert.Empty(commands.Registered);
    }

    [Fact]
    public async Task Without_host_support_push_is_unsupported_and_nothing_is_registered()
    {
        var (vm, _, commands) = Make(null);

        await vm.RefreshAsync();

        Assert.Equal(PushStatus.Unsupported, vm.Status);
        Assert.Empty(commands.Registered);
        Assert.False(vm.CanEnable);
    }

    [Fact]
    public async Task A_token_is_registered_only_on_connected_devices_that_declare_the_push_capability()
    {
        var (vm, _, commands) = Make(new FakePush());

        await vm.RefreshAsync();

        Assert.Equal([("local", PushPlatform.Apns, "tok-1")], commands.Registered); // 云端休眠、工作室离线,都不登记
        Assert.Equal(PushStatus.Registered, vm.Status);
        Assert.Equal(1, vm.RegisteredDevices);
    }

    [Fact]
    public async Task A_device_that_connects_later_gets_the_token_once_and_a_reconnect_registers_it_again()
    {
        var (vm, stores, commands) = Make(new FakePush());
        await vm.RefreshAsync();

        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("cloud", w.Devices["cloud"] with { Connection = DeviceConnection.Connected }) });
        stores.Workspace.Update(w => w); // 无关变化不重复登记
        Assert.Equal(["local", "cloud"], commands.Registered.Select(r => r.Device));

        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("cloud", w.Devices["cloud"] with { Connection = DeviceConnection.Offline }) });
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("cloud", w.Devices["cloud"] with { Connection = DeviceConnection.Connected }) });

        Assert.Equal(["local", "cloud", "cloud"], commands.Registered.Select(r => r.Device));
    }

    [Fact]
    public async Task A_refreshed_token_replaces_the_old_registration_on_every_device()
    {
        var push = new FakePush();
        var (vm, _, commands) = Make(push);
        await vm.RefreshAsync();

        push.Refresh("tok-2");

        Assert.Equal(["tok-1", "tok-2"], commands.Registered.Select(r => r.Token));
    }

    [Fact]
    public async Task Not_yet_asked_and_denied_permissions_show_their_states_and_enabling_asks_once_then_registers()
    {
        var push = new FakePush(PushPermission.NotDetermined, token: "tok-9");
        var (vm, _, commands) = Make(push);

        await vm.RefreshAsync();
        Assert.Equal(PushStatus.NotDetermined, vm.Status);
        Assert.True(vm.CanEnable);
        Assert.Empty(commands.Registered);

        await vm.EnableCommand.ExecuteAsync(null);

        Assert.Equal(1, push.Requests);
        Assert.Equal(PushStatus.Registered, vm.Status);
        Assert.Equal("tok-9", commands.Registered.Single().Token);

        var denied = new FakePush(PushPermission.Denied);
        var (vm2, _, commands2) = Make(denied);
        await vm2.RefreshAsync();
        Assert.Equal(PushStatus.Denied, vm2.Status);
        Assert.Empty(commands2.Registered);
    }

    [Fact]
    public async Task A_device_that_refuses_the_registration_leaves_a_failed_status_to_retry_on_reconnect()
    {
        var (vm, _, commands) = Make(new FakePush());
        commands.Fail = true;

        await vm.RefreshAsync();

        Assert.Equal(PushStatus.Failed, vm.Status);
        Assert.True(vm.CanEnable);
        Assert.Equal(0, vm.RegisteredDevices);
    }
}
