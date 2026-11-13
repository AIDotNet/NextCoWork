using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;

namespace Ncw.Agent.Storage.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncws-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static long Count(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>1–37 是 2.x 原样的迁移,38 起是 3.0 的;版本号连续。</summary>
    private const int Latest = 38;

    [Fact]
    public void Migrations_are_the_37_from_2x_then_3x_and_contiguous()
    {
        Assert.Equal(Latest, AgentDatabase.LatestVersion);
        Assert.Equal("core", AgentDatabase.Migrations[0].Name);
        Assert.Equal("message-fts-rowid", AgentDatabase.Migrations[36].Name);
        Assert.Equal("controller-trust", AgentDatabase.Migrations[37].Name);
    }

    [Fact]
    public void Fresh_database_gets_full_2x_schema_and_reopening_is_idempotent()
    {
        using (var db = AgentDatabase.Open(_root))
        {
            Assert.Equal(Latest, db.UserVersion());
            Assert.Equal(Latest, Count(db.Connection, "SELECT count(*) FROM migrations"));
            Assert.Equal(1, Count(db.Connection, "SELECT count(*) FROM sqlite_master WHERE name = 'workspaces'"));
            Assert.Equal(1, Count(db.Connection, "PRAGMA foreign_keys"));
            Assert.Equal("wal", (string)AgentDatabaseTestHelpers.Scalar(db.Connection, "PRAGMA journal_mode")!);
        }

        using var again = AgentDatabase.Open(_root);
        Assert.Equal(Latest, Count(again.Connection, "SELECT count(*) FROM migrations"));
    }

    [Fact]
    public void Newer_database_is_refused()
    {
        using (var db = AgentDatabase.Open(_root))
        {
            Exec(db.Connection, "INSERT INTO migrations (version, name, applied_at) VALUES (99, 'from-the-future', 0)");
        }

        Assert.Throws<StorageException>(() => AgentDatabase.Open(_root));
    }

    /// <summary>直接接管一个停在 2.x 中间版本的库:补跑剩下的迁移,并按第 29 条重建全文索引。</summary>
    [Fact]
    public void Old_2x_database_is_upgraded_and_v29_reindexes_existing_messages()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, AgentDatabase.FileName);
        using (var legacy = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            legacy.Open();
            Exec(legacy, "CREATE TABLE migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)");
            foreach (var m in AgentDatabase.Migrations.Where(m => m.Version <= 20))
            {
                Exec(legacy, m.Sql);
                Exec(legacy, $"INSERT INTO migrations VALUES ({m.Version}, '{m.Name}', 0)");
            }

            Exec(legacy, "PRAGMA user_version = 20");
            Exec(legacy, """
                INSERT INTO sessions (id, workspace_id, title, model, mode, thinking, root_path_at_creation, status, archived, created_at, updated_at, json)
                VALUES ('s1', 'ws1', '旧会话', 'm', 'code', 'auto', '/tmp', 'idle', 0, 0, 0, '{}')
                """);
            Exec(legacy, """
                INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at)
                VALUES ('m1', 's1', 0, 'user', '[{"type":"text","text":"重构 avalonia 外壳"}]', 1, 0)
                """);
        }

        using var db = AgentDatabase.Open(_root);

        Assert.Equal(Latest, db.UserVersion());
        Assert.Equal(1, Count(db.Connection, "SELECT count(*) FROM messages_fts WHERE messages_fts MATCH 'avalonia'"));
        Assert.Equal(1, Count(db.Connection, "SELECT count(*) FROM messages_fts_rowid WHERE message_id = 'm1'"));
    }

    [Theory]
    [InlineData("""[{"type":"text","text":"a"},{"type":"thinking","text":"b"}]""", "a\nb")]
    [InlineData("""[{"type":"tool_call","name":"Read","input":{"path":"x"}}]""", "Read\n{\"path\":\"x\"}")]
    [InlineData("""[{"type":"tool_result","output":{"content":"ok"}},{"type":"subagent","summary":"done"}]""", "ok\ndone")]
    [InlineData("""[{"type":"error","error":{"message":"boom"}},{"type":"image"},42]""", "boom")]
    [InlineData("""{"not":"an array"}""", "")]
    [InlineData("not json", "")]
    public void Searchable_text_matches_2x_rules(string parts, string expected)
    {
        Assert.Equal(expected, MessageFts.SearchableText(parts));
    }
}

internal static class AgentDatabaseTestHelpers
{
    public static object? Scalar(SqliteConnection c, string sql)
    {
        using var command = c.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
