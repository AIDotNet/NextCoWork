using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Agent.Storage.Takeover;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>Agent 启动时的数据准备:全新、接管 2.x、被 2.x 挡住后重试(docs/design/04 §4)。</summary>
public sealed class DataTakeoverHostTests : IAsyncDisposable
{
    private const string Token = "t0ken";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwd-{Guid.NewGuid():N}"[..16]);
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), $"ncw-ws-{Guid.NewGuid():N}");
    private CancellationTokenSource _cts = new();
    private Task _run = Task.CompletedTask;
    private AgentHost? _host;
    private volatile bool _legacyAlive;

    public DataTakeoverHostTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_workspace);
    }

    private AgentOptions Options => new(AgentMode.Local, _root, LocalTransport.DefaultEndpoint(_root), Token, null);

    private sealed class FakeLegacy : ILegacySecretDecryptor
    {
        public string? TryDecrypt(ReadOnlySpan<byte> blob) => blob.StartsWith("v10"u8) ? "sk-old" : null;
    }

    private AgentHost Start()
    {
        _host = new AgentHost(Options, takeover: new TakeoverOptions(IsAlive: _ => _legacyAlive), legacySecrets: () => new FakeLegacy());
        _run = _host.RunAsync(_cts.Token);
        return _host;
    }

    /// <summary>像桌面端那样让 Agent 优雅退出(关库;有回退请求就在关库之后执行),然后在同一个数据根上再起一个。</summary>
    private async Task<AgentHost> Restart()
    {
        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        _host?.Dispose();
        SqliteConnection.ClearAllPools();
        _cts = new CancellationTokenSource();
        return Start();
    }

    private static string? ErrorKey(ProtocolException e) =>
        e.Details is { } details ? FrameCodec.DecodePayload(details, PayloadShapes.GetShape<ProviderErrorDto>()).Key : null;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        _host?.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _root, _workspace })
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>模拟一份 2.x 数据根:有库、有一个工作区,没有所有权标记。</summary>
    private void SeedLegacy()
    {
        using var db = AgentDatabase.Open(_root);
        new WorkspaceRepository(db).AddLocal(_workspace);
    }

    private async Task<DeviceClient> Connect()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = await LocalTransport.ConnectAsync(Options.Endpoint, CancellationToken.None);
                return await DeviceClient.ConnectAsync(stream, new ClientHello("tests", "3.0.0", LocalToken: Token));
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException && attempt < 60)
            {
                await Task.Delay(50);
            }
        }
    }

    private static async Task<DataStatus> WaitFor(DeviceClient client, string state)
    {
        for (var i = 0; i < 200; i++)
        {
            var status = await client.CallAsync(DeviceMethods.DataStatus, Unit.Value);
            if (status.State == state) return status;
            await Task.Delay(25);
        }

        throw new TimeoutException($"data never became {state}");
    }

    [Fact]
    public void Fresh_root_is_ready_before_listening_and_gets_an_ownership_mark()
    {
        var host = Start();

        Assert.Equal(DataStates.Ready, host.Data.State);
        Assert.Null(host.Data.TakenOver);
        Assert.Equal(host.DeviceId, Ownership.Read(Path.Combine(_root, AgentDatabase.FileName))?.DeviceId);
    }

    [Fact]
    public async Task Legacy_root_is_taken_over_in_the_background()
    {
        SeedLegacy();
        Start();
        await using var client = await Connect();

        var ready = await WaitFor(client, DataStates.Ready);

        Assert.Equal(new TakeoverSummary(1, 0, 0), ready.TakenOver);
        var list = await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value);
        Assert.Single(list.Projects);
        Assert.Equal(TakeoverPhase.Committed, DataRootTakeover.ReadJournal(_root)?.Phase);
        Assert.True(Directory.Exists(Path.Combine(_root, DataRootTakeover.BackupRoot)));
    }

    [Fact]
    public async Task Takeover_converts_legacy_credentials_and_reports_the_ones_to_reenter()
    {
        SeedLegacy();
        using (var db = AgentDatabase.Open(_root))
        {
            foreach (var (reference, blob) in new[] { ("provider:a", "v10-legacy"u8.ToArray()), ("provider:b", "v11-keyring"u8.ToArray()) })
            {
                using var command = db.Connection.CreateCommand();
                command.CommandText = "INSERT INTO credentials (ref, blob) VALUES ($r, $b)";
                command.Parameters.AddWithValue("$r", reference);
                command.Parameters.Add("$b", Microsoft.Data.Sqlite.SqliteType.Blob).Value = blob;
                command.ExecuteNonQuery();
            }
        }

        Start();
        await using var client = await Connect();
        var ready = await WaitFor(client, DataStates.Ready);

        Assert.Equal(1, ready.TakenOver?.CredentialsToReenter);
        SqliteConnection.ClearAllPools();
        using var check = new SqliteConnection($"Data Source={Path.Combine(_root, AgentDatabase.FileName)};Mode=ReadOnly;Pooling=False");
        check.Open();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT blob FROM credentials WHERE ref = 'provider:a'";
        var key = CredentialCrypto.LoadMasterKey(_root);
        Assert.Equal("sk-old", CredentialCrypto.Decrypt(key, (byte[])read.ExecuteScalar()!));
    }

    [Fact]
    public async Task Running_2x_blocks_takeover_until_retry()
    {
        if (OperatingSystem.IsWindows()) return; // SingletonLock 符号链接是 Unix 的形态

        SeedLegacy();
        File.CreateSymbolicLink(Path.Combine(_root, "SingletonLock"), "host-4242");
        _legacyAlive = true;
        Start();
        await using var client = await Connect();

        var blocked = await WaitFor(client, DataStates.Blocked);
        Assert.Equal(DataReasons.LegacyAppRunning, blocked.Reason);
        var e = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(DeviceMethods.ProjectsList, Unit.Value));
        Assert.Equal(ErrorCodes.Unavailable, e.Code);
        Assert.True(e.Retryable);
        Assert.False(Directory.Exists(Path.Combine(_root, DataRootTakeover.BackupRoot))); // 被挡住时什么都没做

        _legacyAlive = false; // 用户退出了 2.x
        var retried = await client.CallAsync(DeviceMethods.DataRetry, Unit.Value);

        Assert.Equal(DataStates.Ready, retried.State);
        Assert.Single((await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects);
    }

    [Fact]
    public async Task Data_topic_snapshot_and_events_share_one_type()
    {
        SeedLegacy();
        Start();
        await using var client = await Connect();
        var seen = new List<string>();
        var done = new TaskCompletionSource();
        await using var sub = await client.SubscribeAsync(DeviceMethods.DataTopic,
            (state, _) => { lock (seen) { seen.Add(state.State); if (state.State == DataStates.Ready) done.TrySetResult(); } },
            evt =>
            {
                var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<DataStatus>());
                lock (seen) { seen.Add(state.State); if (state.State == DataStates.Ready) done.TrySetResult(); }
            });

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DataStates.Ready, seen[^1]);
    }

    // ───────────────────────── 设置 · 数据 · 回退到 2.x(docs/design/04 §5) ─────────────────────────

    [Fact]
    public async Task Going_back_to_2x_runs_when_the_agent_closes_the_database_and_3x_then_waits_for_an_explicit_move()
    {
        SeedLegacy();
        Start();
        await using (var client = await Connect())
        {
            await WaitFor(client, DataStates.Ready);
            var info = await client.CallAsync(DeviceMethods.DataRollbackInfo, Unit.Value);
            Assert.True(info.Available);
            Assert.NotNull(info.TakenOverAt);

            var unconfirmed = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(DeviceMethods.DataRequestRollback, new DataRollbackParams(false)));
            Assert.Equal(ErrorCodes.InvalidRequest, unconfirmed.Code);
            Assert.Equal("data.rollback.notAcknowledged", ErrorKey(unconfirmed));

            Assert.True((await client.CallAsync(DeviceMethods.DataRequestRollback, new DataRollbackParams(true))).Pending);
            Assert.Single((await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects); // 只登记,Agent 照常可用
        }

        // 桌面端让 Agent 优雅退出:关库之后执行回退,2.9 看到的是没有所有权标记的 2.x 库
        var host = await Restart();
        Assert.Null(Ownership.Read(Path.Combine(_root, AgentDatabase.FileName)));
        Assert.True(RollbackToLegacy.IsRolledBack(_root));

        await using var again = await Connect();
        var rolledBack = await WaitFor(again, DataStates.RolledBack);
        Assert.Null(rolledBack.Reason);
        await Task.Delay(200);
        Assert.Equal(DataStates.RolledBack, (await again.CallAsync(DeviceMethods.DataStatus, Unit.Value)).State); // 不自动再接管
        Assert.Null(Ownership.Read(Path.Combine(_root, AgentDatabase.FileName)));
        var e = await Assert.ThrowsAsync<ProtocolException>(() => again.CallAsync(DeviceMethods.ProjectsList, Unit.Value));
        Assert.Equal(ErrorCodes.Unavailable, e.Code);

        // 用户点「重新迁移」
        var moved = await again.CallAsync(DeviceMethods.DataRetry, Unit.Value);
        Assert.Equal(DataStates.Ready, moved.State);
        Assert.Equal(host.DeviceId, Ownership.Read(Path.Combine(_root, AgentDatabase.FileName))?.DeviceId);
        Assert.Single((await again.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects);
    }

    [Fact]
    public async Task A_request_left_by_a_killed_agent_runs_at_the_next_start_before_the_database_opens()
    {
        SeedLegacy();
        Start();
        await using (var client = await Connect())
        {
            await WaitFor(client, DataStates.Ready);
        }

        await Restart(); // 正常退出,没有请求:数据还是 3.0 的
        Assert.Equal(DataStates.Ready, _host!.Data.State);
        await _cts.CancelAsync();
        try { await _run; } catch (OperationCanceledException) { }
        _host.Dispose();
        SqliteConnection.ClearAllPools();

        // 模拟:请求登记了,Agent 却在关库之前被强杀(请求文件还在,库还是 3.0 的)
        RollbackToLegacy.Request(_root, 1);
        _cts = new CancellationTokenSource();
        Start();
        await using var after = await Connect();

        await WaitFor(after, DataStates.RolledBack);
        Assert.False(RollbackToLegacy.Inspect(_root).Pending);
        Assert.Null(Ownership.Read(Path.Combine(_root, AgentDatabase.FileName)));
    }

    [Fact]
    public async Task A_fresh_3x_root_has_nothing_to_go_back_to()
    {
        Start();
        await using var client = await Connect();

        Assert.False((await client.CallAsync(DeviceMethods.DataRollbackInfo, Unit.Value)).Available);
        var e = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(DeviceMethods.DataRequestRollback, new DataRollbackParams(true)));
        Assert.Equal("data.rollback.unavailable", ErrorKey(e));
        Assert.False(File.Exists(Path.Combine(_root, RollbackToLegacy.RequestFile)));
    }
}
