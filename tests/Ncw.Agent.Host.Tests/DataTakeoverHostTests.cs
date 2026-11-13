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
    private readonly CancellationTokenSource _cts = new();
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
}
