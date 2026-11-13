using Ncw.Agent.Host;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

namespace Ncw.Integration.Tests;

public sealed class DeviceLinkTests : IAsyncLifetime
{
    private const string Token = "integration-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwi-{Guid.NewGuid():N}"[..16]);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ncw-proj-{Guid.NewGuid():N}");
    private readonly ClientStores _stores = new();
    private readonly LockedDispatcher _ui = new();
    private CancellationTokenSource _agentCts = new();
    private Task _agent = Task.CompletedTask;
    private DeviceLink _link = null!;

    private string Endpoint => LocalTransport.DefaultEndpoint(_root);

    /// <summary>集成测试里没有 UI 线程:用一把锁把 Store 的写入串行化。</summary>
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

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        StartAgent();
        _link = new DeviceLink(_stores, _ui, "local", DeviceKind.Local, "本机",
            ct => LocalTransport.ConnectAsync(Endpoint, ct), new ClientHello("integration", "3.0.0", LocalToken: Token));
        return ValueTask.CompletedTask;
    }

    private void StartAgent()
    {
        _agentCts = new CancellationTokenSource();
        var host = new AgentHost(new AgentOptions(AgentMode.Local, _root, Endpoint, Token, null));
        _agent = host.RunAsync(_agentCts.Token);
    }

    private async Task StopAgent()
    {
        await _agentCts.CancelAsync();
        try { await _agent; } catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _link.DisposeAsync();
        await StopAgent();
        foreach (var dir in new[] { _root, _workspace })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private async Task WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            lock (_ui.Gate)
            {
                if (condition()) return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for: {what}");
    }

    private DeviceInfo? Device => _stores.Workspace.State.Devices.GetValueOrDefault(_link.DeviceId);

    [Fact]
    public async Task Syncs_device_then_added_project_appears_in_stores_and_rail()
    {
        _link.Start();
        await WaitUntil(() => Device?.Connection == DeviceConnection.Connected, "connected");
        Assert.StartsWith("dev_", _link.DeviceId);
        Assert.DoesNotContain("local", _stores.Workspace.State.Devices.Keys); // 占位 id 被替换

        var project = await _link.AddProjectAsync(_workspace);

        await WaitUntil(() => _stores.Workspace.State.Projects.ContainsKey(project), "project in store");
        lock (_ui.Gate)
        {
            Assert.Equal(Path.GetFileName(_workspace), _stores.Workspace.State.Projects[project].Name);
            Assert.Contains(project, _stores.Layout.State.Rail.Projects());
            Assert.Equal(project, _stores.Selection.State.Project); // 第一个项目自动选中
        }
    }

    [Fact]
    public async Task Agent_restart_marks_device_disconnected_then_reconnects_and_resyncs()
    {
        _link.Start();
        await WaitUntil(() => Device?.Connection == DeviceConnection.Connected, "connected");
        var project = await _link.AddProjectAsync(_workspace);

        await StopAgent();
        await WaitUntil(() => Device?.Connection is DeviceConnection.Connecting or DeviceConnection.Offline, "disconnected");
        lock (_ui.Gate)
        {
            // 断开时项目还在(不是被删除),只是状态未知
            Assert.True(_stores.Workspace.State.Projects.ContainsKey(project));
        }

        StartAgent();
        await WaitUntil(() => Device?.Connection == DeviceConnection.Connected, "reconnected");
        lock (_ui.Gate)
        {
            Assert.True(_stores.Workspace.State.Projects.ContainsKey(project));
        }
    }

    [Fact]
    public async Task Wrong_token_stops_retrying_and_shows_offline()
    {
        await using var bad = new DeviceLink(_stores, _ui, "bad", DeviceKind.Local, "本机",
            ct => LocalTransport.ConnectAsync(Endpoint, ct), new ClientHello("integration", "3.0.0", LocalToken: "wrong"));
        bad.Start();

        await WaitUntil(() => _stores.Workspace.State.Devices.GetValueOrDefault("bad")?.Connection == DeviceConnection.Offline, "offline");
    }
}
