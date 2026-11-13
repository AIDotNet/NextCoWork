using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Storage.Tests;

public sealed class WorkspaceRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncww-{Guid.NewGuid():N}");
    private readonly AgentDatabase _db;
    private readonly WorkspaceRepository _repo;

    public WorkspaceRepositoryTests()
    {
        _db = AgentDatabase.Open(_root);
        _repo = new WorkspaceRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    private void Copy(string table, string[] columns, object[] values)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = $"INSERT OR REPLACE INTO {table} ({string.Join(",", columns)}) VALUES ({string.Join(",", columns.Select((_, i) => $"$p{i}"))})";
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue($"$p{i}", values[i]);
        }

        command.ExecuteNonQuery();
    }

    private void Exec(string sql)
    {
        using var command = _db.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void New_workspace_has_the_2x_shape()
    {
        var w = _repo.AddLocal("/work/app");

        Assert.Matches("^ws_[0-9A-HJKMNP-TV-Z]{26}$", w.Id);
        Assert.Equal("app", w.Name);
        Assert.Equal("local", w.EnvironmentKind);
        Assert.Null(w.Json["environment"]); // 2.x:本机工作区不写 environment
        var settings = w.Json["settings"]!.AsObject();
        Assert.Equal("ask", settings["permissionMode"]!.GetValue<string>());
        Assert.True(settings["webSearch"]!.GetValue<bool>());
        Assert.Equal("all", settings["skillSelectionMode"]!.GetValue<string>());
    }

    [Fact]
    public void Same_folder_is_registered_once_and_unknown_fields_survive()
    {
        var first = _repo.AddLocal("/work/app");
        // 2.x(或以后的版本)写进去、3.0 不认识的字段
        var json = (JsonObject)first.Json.DeepClone();
        json["futureField"] = "keep me";
        json["unavailable"] = true;
        Exec($"UPDATE workspaces SET json = '{json.ToJsonString()}' WHERE id = '{first.Id}'");

        var again = _repo.AddLocal("/work/app/");

        Assert.Equal(first.Id, again.Id);
        Assert.Single(_repo.List());
        Assert.Equal("keep me", again.Json["futureField"]!.GetValue<string>());
        Assert.Null(again.Json["unavailable"]); // 重新打开即视为可用(与 2.x 相同)
    }

    [Fact]
    public void Other_account_scope_is_invisible_and_cannot_be_overwritten()
    {
        var mine = _repo.AddLocal("/work/mine");
        Exec("""INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES ('ws_other', 1, '{"id":"ws_other","name":"x","rootPath":"/x","settings":{},"createdAt":1,"lastOpenedAt":1}', 'acct-2')""");

        Assert.Equal([mine.Id], _repo.List().Select(w => w.Id));
        Assert.Null(_repo.Get("ws_other"));
        Assert.False(_repo.Remove("ws_other"));

        // 切到另一个账户作用域
        Exec("""INSERT INTO kv (key, json) VALUES ('config-profile.scope', '"acct-2"')""");
        Assert.Equal("acct-2", _repo.CurrentScope());
        Assert.Equal(["ws_other"], _repo.List().Select(w => w.Id));
    }

    [Fact]
    public void Ssh_workspace_shows_connection_name()
    {
        Exec("""INSERT INTO connection_profiles (id, json) VALUES ('c1', '{"id":"c1","name":"dev-server"}')""");
        Exec("""INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES ('ws_ssh', 1, '{"id":"ws_ssh","name":"api","rootPath":"/srv/api","environment":{"kind":"connection","connectionId":"c1"},"settings":{},"createdAt":1,"lastOpenedAt":1}', 'local')""");

        var w = Assert.Single(_repo.List());

        Assert.Equal("connection", w.EnvironmentKind);
        Assert.Equal("dev-server", _repo.ConnectionName(w.ConnectionId!));
    }

    [Fact]
    public void Broken_rows_do_not_break_the_list()
    {
        _repo.AddLocal("/work/ok");
        Exec("INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES ('bad', 2, 'not json', 'local')");

        Assert.Single(_repo.List());
    }

    /// <summary>
    /// 本机如果有真实的 2.x 库:把它的工作区行(只读、不改原库)复制进一个新库,确认 3.0 每一行都读得出来。
    /// 没有 2.x 库的机器上跳过。
    /// </summary>
    [Fact]
    public void Real_2x_workspaces_are_all_readable()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".next-cowork", "data", AgentDatabase.FileName);
        if (!File.Exists(real))
        {
            Assert.Skip("No 2.x database on this machine");
        }

        // 只读、不改原库:immutable=1 不加锁也不写 WAL。逐行复制到测试库。
        long rows = 0;
        using (var source = new SqliteConnection($"Data Source=file:{real}?immutable=1;Mode=ReadOnly;Pooling=False"))
        {
            source.Open();
            string scope = "local";
            using (var kv = source.CreateCommand())
            {
                kv.CommandText = "SELECT json FROM kv WHERE key = 'config-profile.scope'";
                if (kv.ExecuteScalar() is string scopeJson)
                {
                    Copy("kv", ["key", "json"], ["config-profile.scope", scopeJson]);
                    scope = JsonNode.Parse(scopeJson)?.GetValue<string>() is { Length: > 0 } s2 ? s2 : "local";
                }
            }

            using (var read = source.CreateCommand())
            {
                read.CommandText = "SELECT id, last_opened_at, json, owner FROM workspaces";
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    Copy("workspaces", ["id", "last_opened_at", "json", "owner"], [r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3)]);
                    if (r.GetString(3) == scope)
                    {
                        rows++;
                    }
                }
            }

            using (var read = source.CreateCommand())
            {
                read.CommandText = "SELECT id, json FROM connection_profiles";
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    Copy("connection_profiles", ["id", "json"], [r.GetString(0), r.GetString(1)]);
                }
            }
        }

        var listed = _repo.List();
        Assert.Equal(rows, listed.Count);
        Assert.All(listed, w => Assert.False(string.IsNullOrEmpty(w.RootPath)));
    }
}
