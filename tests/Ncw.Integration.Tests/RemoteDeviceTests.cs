using Microsoft.Data.Sqlite;
using Ncw.Agent.Host;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

namespace Ncw.Integration.Tests;

/// <summary>
/// 控制端添加局域网设备的完整链路:本机 Agent(A)+ 另一台设备上的 Agent(B,打开远程开关)。
/// 控制端经 Actions / RemoteDeviceManager 配对 B,B 的项目和本机项目出现在同一个 Store 里。
/// </summary>
public sealed class RemoteDeviceTests : IAsyncLifetime
{
    private const string Token = "integration-token";

    private readonly string _rootA = Path.Combine(Path.GetTempPath(), $"ncwa-{Guid.NewGuid():N}"[..16]);
    private readonly string _rootB = Path.Combine(Path.GetTempPath(), $"ncwb-{Guid.NewGuid():N}"[..16]);
    private readonly string _projectB = Path.Combine(Path.GetTempPath(), $"ncw-studio-{Guid.NewGuid():N}");
    private readonly ClientStores _stores = new();
    private readonly LockedDispatcher _ui = new();
    private readonly MemoryDeviceBook _book = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _agents = [];
    private AgentHost _b = null!;
    private DeviceClient _bLocal = null!;
    private DeviceRegistry _devices = null!;

    private sealed class LockedDispatcher : IUiDispatcher
    {
        public readonly object Gate = new();

        public void Post(Action action)
        {
            lock (Gate)
            {
                action();
            }
        }
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_projectB);
        var a = new AgentHost(new AgentOptions(AgentMode.Local, _rootA, LocalTransport.DefaultEndpoint(_rootA), Token, null));
        _agents.Add(a.RunAsync(_cts.Token));
        _b = new AgentHost(new AgentOptions(AgentMode.Local, _rootB, LocalTransport.DefaultEndpoint(_rootB), Token, null), pairingFailureDelay: TimeSpan.Zero);
        _agents.Add(_b.RunAsync(_cts.Token));

        // B 的主人在 B 上打开远程开关、添加一个项目。
        _bLocal = await ConnectLocal(_rootB);
        await _bLocal.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true));
        await _bLocal.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(_projectB, "studio-project"));

        // 控制端(本机 A 上的桌面端)。
        var local = new DeviceLink(_stores, _ui, "local", DeviceKind.Local, "本机",
            ct => LocalTransport.ConnectAsync(LocalTransport.DefaultEndpoint(_rootA), ct), new ClientHello("desktop", "3.0.0", LocalToken: Token));
        var remote = new RemoteDeviceManager(_stores, _ui, new EphemeralControllerIdentity(), _book, "Desktop A");
        _devices = new DeviceRegistry { Local = local, Remote = remote };
        local.Start();
        remote.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _bLocal.DisposeAsync();
        await _devices.Local!.DisposeAsync();
        await _devices.Remote!.DisposeAsync();
        await _cts.CancelAsync();
        foreach (var agent in _agents)
        {
            try { await agent; } catch (OperationCanceledException) { }
        }

        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _rootA, _rootB, _projectB })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<DeviceClient> ConnectLocal(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = await LocalTransport.ConnectAsync(LocalTransport.DefaultEndpoint(root), CancellationToken.None);
                return await DeviceClient.ConnectAsync(stream, new ClientHello("owner", "3.0.0", LocalToken: Token));
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException && attempt < 60)
            {
                await Task.Delay(50);
            }
        }
    }

    private async Task WaitUntil(Func<WorkspaceState, bool> condition, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (_ui.Gate)
            {
                if (condition(_stores.Workspace.State)) return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(what);
    }

    private string Address => $"127.0.0.1:{_b.Remote.Port}";

    [Fact]
    public async Task Pairing_brings_the_remote_device_and_its_projects_into_the_store()
    {
        var actions = new DeviceActions(_devices);

        Assert.Equal(PairOutcome.Paired, await actions.PairAsync(Address, _b.Remote.PairingCode!));

        await WaitUntil(s => s.Devices.TryGetValue(_b.DeviceId, out var d) && d.Connection == DeviceConnection.Connected, "remote connected");
        await WaitUntil(s => s.Projects.Keys.Any(p => p.DeviceId == _b.DeviceId), "remote projects");
        lock (_ui.Gate)
        {
            var device = _stores.Workspace.State.Devices[_b.DeviceId];
            Assert.Equal(DeviceKind.Remote, device.Kind);
            Assert.Equal(Environment.MachineName, device.Name); // 设备自己报告的名字
            Assert.Null(device.Remote); // remote 主题只给本机通道
            Assert.Contains(_stores.Workspace.State.Projects.Values, p => p.Ref.DeviceId == _b.DeviceId && p.Name == "studio-project");
            Assert.Equal(_b.DeviceId, Assert.Single(_book.Saved).DeviceId);
            Assert.Equal(Convert.ToHexStringLower(_b.Fingerprint), _book.Saved[0].Fingerprint);
        }
    }

    [Fact]
    public async Task Local_device_mirrors_its_remote_state_for_the_settings_page()
    {
        await WaitUntil(s => s.Devices.Values.Any(d => d.Kind == DeviceKind.Local && d.Remote is not null), "local remote state");
        lock (_ui.Gate)
        {
            var settings = new RemoteSettingsViewModel(_stores, _devices, new DeviceActions(_devices));
            Assert.True(settings.IsAvailable);
            Assert.False(settings.IsEnabled); // A 的远程开关默认关
            Assert.Null(settings.PairingCode);
        }
    }

    [Fact]
    public async Task Settings_page_turns_remote_on_and_changes_the_port()
    {
        await WaitUntil(s => s.Devices.Values.Any(d => d.Kind == DeviceKind.Local && d.Remote is not null), "local remote state");
        RemoteSettingsViewModel settings;
        lock (_ui.Gate)
        {
            settings = new RemoteSettingsViewModel(_stores, _devices, new DeviceActions(_devices)) { PortDebounce = TimeSpan.FromMilliseconds(50) };
            settings.Enabled = true;
        }

        await WaitUntil(s => s.Devices.Values.Single(d => d.Kind == DeviceKind.Local).Remote!.Status.PairingCode is not null, "remote on");
        int target;
        lock (_ui.Gate)
        {
            target = settings.PreferredPort + 101;
            settings.Port = target;
            settings.Port = target; // 重复设置同一个值不会多发
        }

        await WaitUntil(s => s.Devices.Values.Single(d => d.Kind == DeviceKind.Local).Remote!.Status.PreferredPort == target, "port changed");
        lock (_ui.Gate)
        {
            Assert.Equal(target, (int)settings.Port);
        }
    }

    [Fact]
    public async Task Settings_page_sets_validates_and_clears_the_relay_address()
    {
        await WaitUntil(s => s.Devices.Values.Any(d => d.Kind == DeviceKind.Local && d.Remote is not null), "local remote state");
        RemoteSettingsViewModel settings;
        lock (_ui.Gate)
        {
            settings = new RemoteSettingsViewModel(_stores, _devices, new DeviceActions(_devices));
            settings.Enabled = true;
        }

        await WaitUntil(s => s.Devices.Values.Single(d => d.Kind == DeviceKind.Local).Remote!.Status.PairingCode is not null, "remote on");
        Assert.Null(settings.RelayUrl); // 默认没有中继

        // 格式不对:设备拒绝,界面标出来,设置不变。
        settings.RelayDraft = "http://not-a-relay";
        Assert.True(settings.CanApplyRelay);
        await settings.ApplyRelayCommand.ExecuteAsync(null);
        Assert.True(settings.RelayInvalid);
        Assert.Null(settings.RelayUrl);

        // 合法地址(没有中继在听):设备记下来并如实报告「连不上」,而不是假装在线。
        settings.RelayDraft = "ws://127.0.0.1:9"; // discard 端口,连接会被拒绝
        Assert.False(settings.RelayInvalid);
        await settings.ApplyRelayCommand.ExecuteAsync(null);
        await WaitUntil(s => s.Devices.Values.Single(d => d.Kind == DeviceKind.Local).Remote!.Status is { RelayUrl: "ws://127.0.0.1:9", RelayState: "failed" or "connecting" }, "relay failure reported");
        lock (_ui.Gate)
        {
            Assert.Equal("ws://127.0.0.1:9", settings.RelayUrl);
            Assert.False(settings.CanApplyRelay);
        }

        // 留空 = 关闭中继。
        settings.RelayDraft = "";
        await settings.ApplyRelayCommand.ExecuteAsync(null);
        await WaitUntil(s => s.Devices.Values.Single(d => d.Kind == DeviceKind.Local).Remote!.Status.RelayUrl is null, "relay cleared");
        lock (_ui.Gate)
        {
            Assert.Null(settings.RelayState);
        }
    }

    [Fact]
    public async Task Wrong_code_and_bad_address_are_reported_as_outcomes()
    {
        var actions = new DeviceActions(_devices);
        var wrong = _b.Remote.PairingCode == "000000" ? "111111" : "000000";

        Assert.Equal(PairOutcome.WrongCode, await actions.PairAsync(Address, wrong));
        Assert.Equal(PairOutcome.InvalidAddress, await actions.PairAsync("", "123456"));
        Assert.Equal(PairOutcome.InvalidCode, await actions.PairAsync(Address, "12"));
        Assert.Empty(_book.Saved);
    }

    [Fact]
    public async Task Revoked_device_is_marked_and_not_retried()
    {
        var actions = new DeviceActions(_devices);
        Assert.Equal(PairOutcome.Paired, await actions.PairAsync(Address, _b.Remote.PairingCode!));
        await WaitUntil(s => s.Devices.TryGetValue(_b.DeviceId, out var d) && d.Connection == DeviceConnection.Connected, "connected");

        var controllers = await WaitForControllers();
        await _bLocal.CallAsync(DeviceMethods.TrustRevoke, new TrustRevokeParams(Assert.Single(controllers).Fingerprint));

        await WaitUntil(s => s.Devices.TryGetValue(_b.DeviceId, out var d) && d.Connection == DeviceConnection.Revoked, "revoked");
    }

    [Fact]
    public async Task Removing_a_device_forgets_it_and_its_projects()
    {
        var actions = new DeviceActions(_devices);
        Assert.Equal(PairOutcome.Paired, await actions.PairAsync(Address, _b.Remote.PairingCode!));
        await WaitUntil(s => s.Projects.Keys.Any(p => p.DeviceId == _b.DeviceId), "remote projects");

        await actions.RemoveAsync(_b.DeviceId);

        await WaitUntil(s => !s.Devices.ContainsKey(_b.DeviceId) && s.Projects.Keys.All(p => p.DeviceId != _b.DeviceId), "forgotten");
        Assert.Empty(_book.Saved);
    }

    private async Task<TrustedControllerDto[]> WaitForControllers()
    {
        var done = new TaskCompletionSource<TrustedControllerDto[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sub = await _bLocal.SubscribeAsync(DeviceMethods.RemoteTopic,
            (state, _) => { if (state.Controllers.Length > 0) done.TrySetResult(state.Controllers); },
            evt =>
            {
                var state = Ncw.Protocol.Wire.FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<RemoteState>());
                if (state.Controllers.Length > 0) done.TrySetResult(state.Controllers);
            });
        return await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
