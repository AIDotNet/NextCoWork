using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>真实的本机承载(Unix socket / 命名管道)上跑完整会话。</summary>
public sealed class SessionTests : IAsyncLifetime
{
    private const string Token = "local-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncw-{Guid.NewGuid():N}"[..16]);
    private readonly CancellationTokenSource _cts = new();
    private readonly MethodRouter _router = new();
    private readonly TopicHub _topics = new();
    private string _endpoint = "";
    private Task _server = Task.CompletedTask;
    private int _addCalls;
    private readonly List<ProjectDto> _projects = [];

    public ValueTask InitializeAsync()
    {
        _endpoint = LocalTransport.DefaultEndpoint(_root);
        _router.Handle(DeviceMethods.ProjectsList, (_, _, _) => ValueTask.FromResult(new ProjectsListResult([.. _projects], _projects.Count)));
        _router.Handle(DeviceMethods.ProjectsAdd, (p, _, _) =>
        {
            Interlocked.Increment(ref _addCalls);
            var project = new ProjectDto($"ws{_projects.Count + 1}", p.Name ?? Path.GetFileName(p.Path), p.Path, "local", null, 0);
            _projects.Add(project);
            _topics.Publish("device", DeviceEvents.ProjectsChanged, new ProjectsListResult([.. _projects], _projects.Count), PayloadShapes.GetShape<ProjectsListResult>());
            return ValueTask.FromResult(project);
        });
        _router.Handle(DeviceMethods.ProjectsRemove, async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // 永不结束,用来测取消
            return Unit.Value;
        });
        _topics.Register(DeviceMethods.DeviceTopic, () => new DeviceTopicState([], _projects.Count));

        var options = new ServerOptions("dev-1", "3.0.0-test", Transports.Local,
            hello => hello.LocalToken == Token ? Role.Owner : hello.LocalToken == "viewer" ? Role.Viewer : null);
        _server = Task.Run(async () =>
        {
            await foreach (var stream in LocalTransport.ListenAsync(_endpoint, _cts.Token))
            {
                _ = new ServerSession(new FrameStream(stream), _router, _topics, options).RunAsync(_cts.Token);
            }
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _server; } catch (OperationCanceledException) { }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<DeviceClient> Connect(string? token = Token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = await LocalTransport.ConnectAsync(_endpoint, _cts.Token);
                return await DeviceClient.ConnectAsync(stream, new ClientHello("test-client", "3.0.0", LocalToken: token), _cts.Token);
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException && attempt < 50)
            {
                await Task.Delay(50); // 等监听起来
            }
        }
    }

    [Fact]
    public async Task Handshake_query_and_command()
    {
        await using var client = await Connect();

        Assert.Equal("dev-1", client.Welcome.DeviceId);
        Assert.Equal("owner", client.Welcome.Role);

        var added = await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams("/tmp/demo", null));
        var list = await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value);

        Assert.Equal("demo", added.Name);
        Assert.Equal([added], list.Projects);
    }

    [Fact]
    public async Task Wrong_token_is_rejected()
    {
        var e = await Assert.ThrowsAsync<ProtocolException>(() => Connect("nope"));

        Assert.Equal(ErrorCodes.Unauthorized, e.Code);
    }

    [Fact]
    public async Task Same_idempotency_key_runs_the_command_once()
    {
        await using var client = await Connect();

        var first = await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams("/tmp/a", null), idem: "k1");
        var again = await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams("/tmp/a", null), idem: "k1");

        Assert.Equal(first, again);
        Assert.Equal(1, _addCalls);
    }

    [Fact]
    public async Task Viewer_cannot_call_member_commands()
    {
        await using var client = await Connect("viewer");

        var e = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams("/x", null)));

        Assert.Equal(ErrorCodes.Forbidden, e.Code);
        Assert.Equal(0, _addCalls);
    }

    [Fact]
    public async Task Subscribe_gets_snapshot_then_ordered_events()
    {
        await using var client = await Connect();
        var log = new List<string>();
        var gate = new TaskCompletionSource();

        await using var sub = await client.SubscribeAsync(DeviceMethods.DeviceTopic,
            (state, seq) => { lock (log) log.Add($"snapshot:{seq}:{state.ProjectsRevision}"); },
            evt => { lock (log) { log.Add($"event:{evt.Seq}:{evt.Type}"); if (log.Count == 3) gate.TrySetResult(); } });

        await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams("/tmp/1", null));
        await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams("/tmp/2", null));
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["snapshot:0:0", "event:1:projects.changed", "event:2:projects.changed"], log);
    }

    [Fact]
    public async Task Cancelling_a_call_cancels_it_on_the_device()
    {
        await using var client = await Connect();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CallAsync(DeviceMethods.ProjectsRemove, new ProjectsRemoveParams("ws1"), cts.Token));

        // 连接仍然可用
        Assert.NotNull(await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value));
    }

    [Fact]
    public async Task Device_shutdown_fails_pending_calls_as_retryable_and_completes()
    {
        var client = await Connect();
        var pending = client.CallAsync(DeviceMethods.ProjectsRemove, new ProjectsRemoveParams("ws1"));

        await _cts.CancelAsync();

        var e = await Assert.ThrowsAsync<ProtocolException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(e.Retryable);
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
    }
}
