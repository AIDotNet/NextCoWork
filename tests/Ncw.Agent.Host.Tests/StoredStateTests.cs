using Microsoft.Data.Sqlite;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 对话链路留下的三个持久化缺口(docs/parity.md):「永久」权限规则、挂起交互的落库、设置页的值进 <c>SessionSettings</c>。
/// 经真实 Agent 走协议;存储用另开的只读 / 写连接直接看库(重开才测得出「落盘之后」)。
/// </summary>
public sealed class StoredStateTests : IAsyncLifetime
{
    private readonly ConversationRig _rig = new();
    private DeviceClient _client = null!;
    private ProjectDto _project = null!;

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _client = await _rig.ConnectAsync("ctrl-a");
        _project = await _rig.AddProjectAsync(_client);
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private SqliteConnection OpenDb()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(_rig.DataRoot, AgentDatabase.FileName)};Pooling=False");
        connection.Open();
        return connection;
    }

    private T Scalar<T>(string sql, params (string Name, object Value)[] args)
    {
        using var connection = OpenDb();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var result = command.ExecuteScalar();
        return result is null or DBNull ? default! : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private async Task<(string RunId, Watch<InteractionsDto> Interactions)> StartWriteAsync(string sessionId, string path)
    {
        _rig.Llm.Then(Up.Calls(("c1", "Write", ConversationRig.WriteJson(path, "x")))).Then(Up.Say("done"));
        var interactions = await Watch<InteractionsDto>.OpenAsync(_client, ConversationMethods.InteractionsTopic);
        var start = await _rig.StartAsync(_client, sessionId, "write it");
        return (start.RunId, interactions);
    }

    private static InteractionDto PendingOf(Watch<InteractionsDto> interactions) =>
        interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Last(s => s.Pending.Length == 1).Pending[0];

    // ───────────────────────── 挂起交互落库 ─────────────────────────

    [Fact]
    public async Task A_pending_interaction_is_journaled_and_settles_as_resolved_by_the_answerer_or_cancelled_when_the_run_is_aborted()
    {
        var session = await _rig.CreateSessionAsync(_client, _project);
        var target = Path.Combine(_project.Path, "journal.txt");

        // 回答:pending → resolved(带回答者)
        var (runId, interactions) = await StartWriteAsync(session.Id, target);
        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending");
        var pending = PendingOf(interactions);
        Assert.Equal("pending", Scalar<string>("SELECT status FROM device_interactions WHERE id = $id", ("$id", pending.Id)));
        Assert.Equal("permission", Scalar<string>("SELECT kind FROM device_interactions WHERE id = $id", ("$id", pending.Id)));
        Assert.Equal(runId, Scalar<string>("SELECT run_id FROM device_interactions WHERE id = $id", ("$id", pending.Id)));
        await _client.CallAsync(ConversationMethods.InteractionsRespond,
            new InteractionsRespondParams(pending.Id, pending.Version, new ToolPermissionResponseDto(PermissionChoices.AllowOnce)));
        await (await RunWatch.OpenAsync(_client, runId)).SettledAsync();
        Assert.Equal("resolved", Scalar<string>("SELECT status FROM device_interactions WHERE id = $id", ("$id", pending.Id)));
        Assert.Equal("ctrl-a", Scalar<string>("SELECT resolved_by FROM device_interactions WHERE id = $id", ("$id", pending.Id)));

        // 中止:pending → cancelled(没有回答者)
        var (secondRun, secondWatch) = await StartWriteAsync(session.Id, Path.Combine(_project.Path, "journal-2.txt"));
        await secondWatch.WaitAsync(() => secondWatch.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "second pending");
        var second = PendingOf(secondWatch);
        await _client.CallAsync(ConversationMethods.RunsAbort, new RunsAbortParams(secondRun));
        await (await RunWatch.OpenAsync(_client, secondRun)).SettledAsync();
        Assert.Equal("cancelled", Scalar<string>("SELECT status FROM device_interactions WHERE id = $id", ("$id", second.Id)));
        Assert.Null(Scalar<string?>("SELECT resolved_by FROM device_interactions WHERE id = $id", ("$id", second.Id)));

        // 只记元数据:工具输入(文件路径)不落进 payload
        Assert.DoesNotContain("journal", Scalar<string>("SELECT CAST(payload AS TEXT) FROM device_interactions WHERE id = $id", ("$id", pending.Id)));
        Assert.Equal(0, Scalar<long>("SELECT COUNT(*) FROM device_interactions WHERE status = 'pending' AND data_epoch = $e", ("$e", InteractionJournalStore.Epoch)));
    }

    [Fact]
    public void Pending_rows_left_by_a_previous_process_are_cancelled_at_startup_and_2x_rows_are_left_alone()
    {
        using var root = new TempRoot();
        using var database = AgentDatabase.Open(root.Path);
        var store = new InteractionJournalStore(database, "device-1");
        store.Open("i1", "r1", "permission", 3, 1000);
        store.Open("i2", "r1", "question", 4, 1001);
        store.Settle("i2", answered: true, "ctrl-b", 1002);
        // 2.x 留下的 pending 行(别的数据代际):不是我们的,不碰
        using (var command = database.Connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO device_interactions (device_id, data_epoch, config_scope, id, scope_epoch, run_id, kind, payload, payload_digest, status, created_at, updated_at) "
                + "VALUES ('device-1', 'legacy', 'local', 'old', '0', 'r0', 'permission', x'7b7d', 'd', 'pending', 1, 1)";
            command.ExecuteNonQuery();
        }

        // 「下一次启动」:收尾
        Assert.Equal(1, new InteractionJournalStore(database, "device-1").CancelPending(2000));

        Assert.Equal(("cancelled", null), (store.Get("i1")!.Status, store.Get("i1")!.ResolvedBy));
        Assert.Equal(("resolved", "ctrl-b"), (store.Get("i2")!.Status, store.Get("i2")!.ResolvedBy));
        using var check = database.Connection.CreateCommand();
        check.CommandText = "SELECT status FROM device_interactions WHERE id = 'old'";
        Assert.Equal("pending", check.ExecuteScalar());
    }

    // ───────────────────────── 「永久」权限规则 ─────────────────────────

    [Fact]
    public async Task Allow_always_with_the_global_scope_is_persisted_in_the_database_and_applies_to_a_later_session_without_asking()
    {
        var session = await _rig.CreateSessionAsync(_client, _project);
        var target = Path.Combine(_project.Path, "always.txt");
        var (runId, interactions) = await StartWriteAsync(session.Id, target);
        await interactions.WaitAsync(() => interactions.Decoded<InteractionsDto>(InteractionEvents.Changed).Any(s => s.Pending.Length == 1), "pending");
        var pending = PendingOf(interactions);
        var tool = Assert.IsType<ToolPermissionInteractionDto>(pending);
        Assert.Contains("global", tool.AllowedScopes);

        await _client.CallAsync(ConversationMethods.InteractionsRespond,
            new InteractionsRespondParams(pending.Id, pending.Version, new ToolPermissionResponseDto(PermissionChoices.AllowAlways, Scope: "global")));
        await (await RunWatch.OpenAsync(_client, runId)).SettledAsync();

        // 落在库里(kv 表),不是进程内存
        var json = Scalar<string>("SELECT json FROM kv WHERE key = 'v3.permissions.global'");
        Assert.Contains("allow", json);
        Assert.Contains("always.txt", json);

        // 换一个会话、换一个 Run:同一个调用不再询问
        File.Delete(target); // Write 覆盖已有文件前要先 Read;删掉它,这里只关心「不再询问」
        var other = await _rig.CreateSessionAsync(_client, _project);
        _rig.Llm.Then(Up.Calls(("c2", "Write", ConversationRig.WriteJson(target, "again")))).Then(Up.Say("second done"));
        var second = await _rig.StartAsync(_client, other.Id, "again");
        var watch = await RunWatch.OpenAsync(_client, second.RunId);
        await watch.SettledAsync();
        Assert.Empty(watch.Of<InteractionRequestEventDto>());
        Assert.Equal("again", File.ReadAllText(target));
    }

    // ───────────────────────── 设置页的值进 SessionSettings ─────────────────────────

    [Fact]
    public async Task Values_from_the_settings_row_reach_the_run_the_personalization_in_the_system_prompt_and_the_output_budget_in_the_request()
    {
        using (var connection = OpenDb())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings (id, json) VALUES (1, $j) ON CONFLICT (id) DO UPDATE SET json = excluded.json";
            command.Parameters.AddWithValue("$j", """
                {"maxOutputTokens":4096,"contextManagement":{"autoCompact":false},
                 "personalization":{"name":"Ada","background":"compilers","instructions":"Always answer in rhymes."}}
                """);
            command.ExecuteNonQuery();
        }

        var session = await _rig.CreateSessionAsync(_client, _project);
        _rig.Llm.Then(Up.Say("ok"));
        var start = await _rig.StartAsync(_client, session.Id, "hello");
        await (await RunWatch.OpenAsync(_client, start.RunId)).SettledAsync();

        var request = Assert.Single(_rig.Llm.Requests);
        Assert.Contains("Always answer in rhymes.", request.System);
        Assert.Contains("Ada", request.System);
        Assert.Equal(4096, request.Options.MaxOutputTokens);
    }

    [Fact]
    public async Task Without_a_settings_row_or_with_a_corrupt_one_the_factory_values_apply()
    {
        using (var connection = OpenDb())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO settings (id, json) VALUES (1, '{not json') ON CONFLICT (id) DO UPDATE SET json = excluded.json";
            command.ExecuteNonQuery();
        }

        var session = await _rig.CreateSessionAsync(_client, _project);
        _rig.Llm.Then(Up.Say("ok"));
        var start = await _rig.StartAsync(_client, session.Id, "hello");
        var watch = await RunWatch.OpenAsync(_client, start.RunId);
        await watch.SettledAsync();

        Assert.Equal("done", watch.Of<RunEndEventDto>().Single().Status);
        Assert.DoesNotContain("Always answer in rhymes.", Assert.Single(_rig.Llm.Requests).System);
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ncwj-{Guid.NewGuid():N}"[..16]);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
            }
        }
    }
}
