using System.Text;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Host;
using Ncw.Agent.Terminal;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Integration.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

namespace Ncw.Integration.Tests;

/// <summary>终端的控制端链路:真实 Agent(进程内,真 PTY)+ DeviceLink + TerminalActions + Store。接收器收快照与数据、断线后按游标续传(不丢不重)、状态单调、设备断开标未知。</summary>
public sealed class TerminalLinkTests : IAsyncLifetime
{
    private const string Token = "terminal-link-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwt-{Guid.NewGuid():N}"[..16]);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ncw-term-{Guid.NewGuid():N}");
    private readonly ClientStores _stores = new();
    private readonly LockedDispatcher _ui = new();
    private readonly CancellationTokenSource _agentCts = new();
    private Task _agent = Task.CompletedTask;
    private CuttableStream? _current;
    private DeviceLink _link = null!;
    private TerminalActions _actions = null!;
    private string _deviceId = "";
    private string _projectId = "";

    private string Endpoint => LocalTransport.DefaultEndpoint(_root);

    public async ValueTask InitializeAsync()
    {
        if (!LocalTerminals.IsSupported)
        {
            return;
        }

        Directory.CreateDirectory(_workspace);
        var host = new AgentHost(new AgentOptions(AgentMode.Local, _root, Endpoint, Token, null));
        _agent = host.RunAsync(_agentCts.Token);
        _link = new DeviceLink(_stores, _ui, "local", DeviceKind.Local, "本机",
            async ct =>
            {
                var stream = new CuttableStream(await LocalTransport.ConnectAsync(Endpoint, ct));
                _current = stream;
                return stream;
            },
            new ClientHello("terminal-link", "3.0.0", LocalToken: Token));
        var devices = new DeviceRegistry { Local = _link };
        _actions = new TerminalActions(devices, _stores);
        _link.Start();
        await WaitUntil(() => Read(() => _stores.Workspace.State.Devices.Values.Any(d => d.Connection == DeviceConnection.Connected && d.Data?.State == DataStates.Ready)), "connected");
        var project = await _link.AddProjectAsync(_workspace);
        _deviceId = project.DeviceId;
        _projectId = project.WorkspaceId;
    }

    public async ValueTask DisposeAsync()
    {
        if (_link is not null)
        {
            await _link.DisposeAsync();
        }

        await _agentCts.CancelAsync();
        try { await _agent; } catch (OperationCanceledException) { }
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _workspace })
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }
    }

    private T Read<T>(Func<T> read)
    {
        lock (_ui.Gate)
        {
            return read();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 30_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                Assert.Fail($"Timed out waiting for: {what}");
            }

            await Task.Delay(15);
        }
    }

    /// <summary>显示端的接收器:快照重置屏幕,数据追加(和终端控件的做法一致),记下收到了几次快照。</summary>
    private sealed class RecordingSink : ITerminalSink
    {
        private readonly object _gate = new();
        private readonly StringBuilder _screen = new();
        private int _snapshots;
        private (int? Code, string Reason)? _exit;
        private bool _gone;

        public string Screen { get { lock (_gate) { return _screen.ToString(); } } }

        public int Snapshots { get { lock (_gate) { return _snapshots; } } }

        public (int? Code, string Reason)? Exit { get { lock (_gate) { return _exit; } } }

        public bool Gone { get { lock (_gate) { return _gone; } } }

        public void OnSnapshot(byte[] data, bool truncated)
        {
            lock (_gate)
            {
                _snapshots++;
                _screen.Clear().Append(Encoding.UTF8.GetString(data));
            }
        }

        public void OnData(byte[] data)
        {
            lock (_gate)
            {
                _screen.Append(Encoding.UTF8.GetString(data));
            }
        }

        public void OnExit(int? exitCode, int? signal, string reason)
        {
            lock (_gate)
            {
                _exit = (exitCode, reason);
            }
        }

        public void OnGone()
        {
            lock (_gate)
            {
                _gone = true;
            }
        }
    }

    private static byte[] Keys(string text) => Encoding.UTF8.GetBytes(text);

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task Output_flows_to_the_sink_resumes_by_cursor_after_a_cut_and_the_store_stays_monotonic()
    {
        Assert.SkipUnless(LocalTerminals.IsSupported, "no local PTY on this machine");
        var created = await _actions.CreateAsync(_deviceId, _projectId, 100, 30);
        Assert.True(created.IsOk);
        var terminal = created.Value!;
        await WaitUntil(() => Read(() => _stores.Terminals.State.Of(_deviceId, terminal.Id) is { Known: true, Exited: false }), "terminal mirrored");

        var sink = new RecordingSink();
        Assert.True((await _actions.AttachAsync(_deviceId, terminal.Id, sink)).IsOk);
        await WaitUntil(() => sink.Snapshots == 1, "first snapshot");
        Assert.True((await _actions.WriteAsync(_deviceId, terminal.Id, Keys("echo ncw$((10+1))x\n"))).IsOk);
        await WaitUntil(() => sink.Screen.Contains("ncw11x\r", StringComparison.Ordinal), "first output");

        // 掐断连接:镜像里的终端状态是「未知」(不判成结束,也不继续当现状)
        _current!.Cut();
        await WaitUntil(() => Read(() => _stores.Terminals.State.Of(_deviceId, terminal.Id) is { Known: false, Exited: false }), "unknown after the cut");

        // 重连后(DeviceLink 自己重连):元数据补回来,输出按游标续上——不丢、不重、没有第二个快照
        await WaitUntil(() => _link.IsConnected, "reconnected", 30_000);
        await WaitUntil(() => Read(() => _stores.Terminals.State.Of(_deviceId, terminal.Id) is { Known: true }), "known again after the reconnect");
        Assert.True((await _actions.WriteAsync(_deviceId, terminal.Id, Keys("echo ncw$((10+2))x\n"))).IsOk);
        await WaitUntil(() => sink.Screen.Contains("ncw12x\r", StringComparison.Ordinal), "output after the reconnect");
        Assert.Equal(1, CountOf(sink.Screen, "ncw11x\r"));
        Assert.Equal(1, CountOf(sink.Screen, "ncw12x\r"));
        Assert.Equal(1, sink.Snapshots);

        // 退出:接收器收到终态,Store 里是已退出;之后迟到的元数据改不回运行中
        Assert.True((await _actions.KillAsync(_deviceId, terminal.Id)).IsOk);
        await WaitUntil(() => sink.Exit is not null, "exit");
        Assert.Equal("killed", sink.Exit!.Value.Reason);
        await WaitUntil(() => Read(() => _stores.Terminals.State.Of(_deviceId, terminal.Id) is { Exited: true }), "mirrored exit");
        var listed = await _actions.ListAsync(_deviceId, _projectId);
        Assert.Equal("exited", listed.Value!.Single().Status);
        lock (_ui.Gate)
        {
            _stores.Terminals.Update(m => TerminalReducer.ApplyInfo(m, _deviceId, terminal, 1)); // 创建时那份「运行中」迟到了
            Assert.True(_stores.Terminals.State.Of(_deviceId, terminal.Id)!.Exited);
        }

        // 页签关闭:设备上移除,Store 里去掉
        Assert.True((await _actions.CloseAsync(_deviceId, terminal.Id)).IsOk);
        await WaitUntil(() => Read(() => _stores.Terminals.State.Of(_deviceId, terminal.Id) is null), "forgotten after close");
    }

    [Fact]
    public async Task Attaching_a_terminal_that_does_not_exist_reports_gone_and_errors_carry_stable_keys()
    {
        Assert.SkipUnless(LocalTerminals.IsSupported, "no local PTY on this machine");
        var sink = new RecordingSink();
        await _actions.AttachAsync(_deviceId, "nope", sink);
        await WaitUntil(() => sink.Gone, "gone");

        var missing = await _actions.GetBufferAsync(_deviceId, "nope");
        Assert.Equal(ActionOutcome.NotFound, missing.Outcome);
        Assert.Equal(TerminalErrorKeys.NotFound, missing.ErrorKey);
        Assert.Equal(ActionOutcome.NotFound, (await _actions.WriteAsync(_deviceId, "nope", Keys("x"))).Outcome);
        Assert.Equal(ActionOutcome.Unavailable, (await _actions.ListAsync("no-such-device", _projectId)).Outcome);
    }
}
