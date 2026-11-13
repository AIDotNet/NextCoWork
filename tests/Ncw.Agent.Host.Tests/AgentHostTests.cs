using Ncw.Agent.Host;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

public sealed class AgentHostTests : IAsyncLifetime
{
    private const string Token = "t0ken";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwa-{Guid.NewGuid():N}"[..16]);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ncw-ws-{Guid.NewGuid():N}");
    private CancellationTokenSource _cts = new();
    private Task _run = Task.CompletedTask;
    private AgentHost _host = null!;

    private AgentOptions Options => new(AgentMode.Local, _root, LocalTransport.DefaultEndpoint(_root), Token, null);

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_workspace);
        Start();
        return ValueTask.CompletedTask;
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        _host = new AgentHost(Options);
        _run = _host.RunAsync(_cts.Token);
    }

    private async Task Stop()
    {
        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await Stop();
        foreach (var dir in new[] { _root, _workspace })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private async Task<DeviceClient> Connect(string token = Token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = await LocalTransport.ConnectAsync(Options.Endpoint, CancellationToken.None);
                return await DeviceClient.ConnectAsync(stream, new ClientHello("tests", "3.0.0", LocalToken: token));
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException && attempt < 60)
            {
                await Task.Delay(50);
            }
        }
    }

    [Fact]
    public async Task Device_info_reports_identity_and_mode()
    {
        await using var client = await Connect();

        var info = await client.CallAsync(DeviceMethods.Info, Unit.Value);

        Assert.Equal(_host.DeviceId, info.DeviceId);
        Assert.Equal("local", info.Kind);
        Assert.Equal(_root, info.DataRoot);
    }

    [Fact]
    public async Task Projects_add_list_remove_and_same_folder_is_registered_once()
    {
        await using var client = await Connect();

        var added = await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(_workspace, null));
        var again = await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(_workspace + Path.DirectorySeparatorChar, "别名"));
        var list = await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value);

        Assert.Equal(added, again);
        Assert.Equal(Path.GetFileName(_workspace), added.Name);
        Assert.Single(list.Projects);

        await client.CallAsync(DeviceMethods.ProjectsRemove, new ProjectsRemoveParams(added.WorkspaceId));
        Assert.Empty((await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects);
    }

    [Fact]
    public async Task Missing_folder_is_not_found()
    {
        await using var client = await Connect();

        var e = await Assert.ThrowsAsync<ProtocolException>(() =>
            client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(Path.Combine(_workspace, "nope"), null)));

        Assert.Equal(ErrorCodes.NotFound, e.Code);
    }

    [Fact]
    public async Task Projects_and_identity_survive_restart()
    {
        string deviceId;
        await using (var client = await Connect())
        {
            await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(_workspace, null));
            deviceId = _host.DeviceId;
        }

        await Stop();
        Start();

        await using var reconnected = await Connect();
        Assert.Equal(deviceId, _host.DeviceId);
        Assert.Single((await reconnected.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects);
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        var e = await Assert.ThrowsAsync<ProtocolException>(() => Connect("wrong"));

        Assert.Equal(ErrorCodes.Unauthorized, e.Code);
    }

    [Fact]
    public async Task Device_topic_pushes_project_changes()
    {
        await using var client = await Connect();
        var received = new TaskCompletionSource<ProjectsListResult>();
        await using var sub = await client.SubscribeAsync(DeviceMethods.DeviceTopic, (_, _) => { },
            evt =>
            {
                if (evt.Type == DeviceEvents.ProjectsChanged)
                {
                    received.TrySetResult(FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<ProjectsListResult>()));
                }
            });

        await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(_workspace, null));

        var list = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(list.Projects);
    }
}
