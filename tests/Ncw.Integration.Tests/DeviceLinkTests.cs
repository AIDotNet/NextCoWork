using Ncw.Agent.Host;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
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

    private sealed class ConfirmingDialogs(bool confirm) : IDialogService
    {
        public List<string> Errors { get; } = [];

        public int Confirms { get; private set; }

        public Task ShowErrorAsync(string title, string message)
        {
            Errors.Add(message);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        {
            Confirms++;
            return Task.FromResult(confirm);
        }

        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task Removing_a_project_unregisters_it_on_the_device_so_it_does_not_come_back_after_a_restart()
    {
        _link.Start();
        await WaitUntil(() => Device?.Connection == DeviceConnection.Connected, "connected");
        var project = await _link.AddProjectAsync(_workspace);
        await WaitUntil(() => _stores.Workspace.State.Projects.ContainsKey(project), "project in store");
        _stores.Layout.Update(s => s with { Appearance = s.Appearance.SetItem(project, new ProjectAppearance("🐙", "#0F766E")) });
        var devices = new DeviceRegistry { Local = _link };

        // 取消确认:什么都不动
        var cancel = new ConfirmingDialogs(confirm: false);
        await new ProjectActions(_stores, devices, () => cancel, () => DefaultClientText.Instance).RemoveAsync(project);
        Assert.Equal(1, cancel.Confirms);
        Assert.Contains(project, _stores.Layout.State.Rail.Projects());

        var dialogs = new ConfirmingDialogs(confirm: true);
        await new ProjectActions(_stores, devices, () => dialogs, () => DefaultClientText.Instance).RemoveAsync(project);

        Assert.Empty(dialogs.Errors);
        await WaitUntil(() => !_stores.Workspace.State.Projects.ContainsKey(project), "device broadcast drops the project");
        lock (_ui.Gate)
        {
            Assert.DoesNotContain(project, _stores.Layout.State.Rail.Projects());
            Assert.False(_stores.Layout.State.Appearance.ContainsKey(project));
            Assert.NotEqual(project, _stores.Selection.State.Project);
        }

        Assert.True(Directory.Exists(_workspace)); // 文件夹不动

        // 以前的 bug:只改本地布局,重连后设备推送项目列表又把它追加回来
        await StopAgent();
        await WaitUntil(() => Device?.Connection is DeviceConnection.Connecting or DeviceConnection.Offline, "disconnected");
        StartAgent();
        await WaitUntil(() => Device?.Connection == DeviceConnection.Connected, "reconnected");
        await Task.Delay(300);
        lock (_ui.Gate)
        {
            Assert.False(_stores.Workspace.State.Projects.ContainsKey(project));
            Assert.DoesNotContain(project, _stores.Layout.State.Rail.Projects());
        }
    }

    [Fact]
    public async Task A_stale_project_is_dropped_from_the_sidebar_without_asking_and_an_offline_device_refuses()
    {
        var stale = new ProjectRef("gone-device", "ws_gone");
        _stores.Layout.Update(s => s with { Rail = Ncw.Core.Layout.RailLayoutOps.Reconcile(s.Rail, [stale]) });
        var dialogs = new ConfirmingDialogs(confirm: true);
        var actions = new ProjectActions(_stores, new DeviceRegistry(), () => dialogs, () => DefaultClientText.Instance);

        await actions.RemoveAsync(stale);

        Assert.Equal(0, dialogs.Confirms); // 设备上已经没有的失效项目:直接拿掉
        Assert.DoesNotContain(stale, _stores.Layout.State.Rail.Projects());

        // 设备上有、但设备没连上:不能移除,说明原因,布局不动
        var offline = new ProjectRef("offline-device", "ws_1");
        _stores.Workspace.Update(s => s with { Projects = s.Projects.SetItem(offline, new ProjectInfo(offline, "proj", "/p", EnvironmentKind.Local, null, null, true, null)) });
        _stores.Layout.Update(s => s with { Rail = Ncw.Core.Layout.RailLayoutOps.Reconcile(s.Rail, [offline]) });
        await actions.RemoveAsync(offline);
        Assert.Equal([DefaultClientText.Instance.ProjectRemoveOffline], dialogs.Errors);
        Assert.Contains(offline, _stores.Layout.State.Rail.Projects());
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
