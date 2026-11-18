using Microsoft.Data.Sqlite;
using Ncw.Agent.Host;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Takeover;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Integration.Tests.Support;

namespace Ncw.Integration.Tests;

/// <summary>
/// 设置 · 数据 · 回退到 2.x 的整条链(docs/design/04 §5):真实 Agent(进程内,接管了一份 2.x 数据)+ 真实 DeviceLink / Store + <see cref="DataActions"/>。
/// 动作的顺序必须是「登记 → 让 Agent 优雅退出(关库后执行回退)→ 退出应用」:用户接着打开 2.9 时,看到的已经是迁移前的 2.x 库。
/// </summary>
public sealed class DataRollbackLinkTests : IAsyncDisposable
{
    private const string Token = "integration-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwrb-{Guid.NewGuid():N}"[..16]);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ncw-rb-{Guid.NewGuid():N}");
    private readonly CancellationTokenSource _agentCts = new();
    private readonly LockedDispatcher _ui = new();
    private readonly ClientStores _stores = new();
    private AgentHost? _host;
    private Task _agent = Task.CompletedTask;
    private DeviceLink? _link;

    private string Endpoint => LocalTransport.DefaultEndpoint(_root);

    public async ValueTask DisposeAsync()
    {
        if (_link is not null)
        {
            await _link.DisposeAsync();
        }

        await StopAgentAsync();
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _workspace })
        {
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }
    }

    /// <summary>桌面端的「让本机 Agent 优雅退出」:取消运行并释放(会话收尾 → 关库 → 执行回退请求)。</summary>
    private async Task StopAgentAsync()
    {
        await _agentCts.CancelAsync();
        try { await _agent; } catch (OperationCanceledException) { }
        _host?.Dispose();
        _host = null;
    }

    [Fact]
    public async Task Going_back_requests_then_stops_the_agent_then_exits_and_2x_finds_its_own_database()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_workspace);
        using (var legacy = AgentDatabase.Open(_root))
        {
            new WorkspaceRepository(legacy).AddLocal(_workspace); // 一份 2.x 数据根:有库、有一个项目,没有所有权标记
        }

        _host = new AgentHost(new AgentOptions(AgentMode.Local, _root, Endpoint, Token, null), takeover: new TakeoverOptions(IsAlive: _ => false));
        _agent = _host.RunAsync(_agentCts.Token);
        _link = new DeviceLink(_stores, _ui, "local-rb", DeviceKind.Local, "本机", async ct => await LocalTransport.ConnectAsync(Endpoint, ct),
            new ClientHello("rollback-test", "3.0.0", LocalToken: Token));
        var devices = new DeviceRegistry { Local = _link };
        _link.Start();
        await OrchestrationRig.WaitUntil(() => Data() == DataStates.Ready, "taken over");

        var steps = new List<string>();
        var host = new StoppingHost(async () =>
        {
            steps.Add("stop");
            Assert.True(RollbackToLegacy.Inspect(_root).Pending); // 先登记,再停
            await StopAgentAsync();
        });
        var actions = new DataActions(devices, () => host, () => () => steps.Add("exit"));

        Assert.True((await actions.GetRollbackInfoAsync()).Value!.Available);
        var result = await actions.RollBackToLegacyAsync();

        Assert.True(result.IsOk);
        Assert.Equal(["stop", "exit"], steps);
        Assert.False(RollbackToLegacy.Inspect(_root).Pending);
        Assert.True(RollbackToLegacy.IsRolledBack(_root));
        SqliteConnection.ClearAllPools();
        Assert.Null(Ownership.Read(Path.Combine(_root, AgentDatabase.FileName))); // 2.9 不会把它当成「被 3.0 接管」而拒绝打开
    }

    private string? Data()
    {
        lock (_ui.Gate)
        {
            return _stores.Workspace.State.Devices.Values.FirstOrDefault(d => d.Connection == DeviceConnection.Connected)?.Data?.State;
        }
    }

    private sealed class StoppingHost(Func<Task> stop) : IStartupHost
    {
        public void Retry()
        {
        }

        public string? Diagnostics() => null;

        public Task StopLocalAgentAsync() => stop();
    }
}
