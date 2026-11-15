using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Storage.Tests;

/// <summary>
/// 用真实 2.x v37 库的**副本**验证会话与消息仓库的兼容性。库在 <c>NCW_LEGACY_DB</c> 环境变量指向的文件里
/// (例如 <c>~/.next-cowork/data/nextcowork.db</c> 的副本);没设就跳过。原库一个字节都不碰:测试先把文件复制到临时目录再打开。
/// 这条测试用来证明「2.x 写的行 3.0 读得出、3.0 写的行形状不变」,不依赖具体数据,只断言不变量。
/// </summary>
public sealed class LegacyDatabaseCompatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwreal-{Guid.NewGuid():N}");
    private AgentDatabase? _db;

    public void Dispose()
    {
        _db?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SessionRepository OpenCopy()
    {
        var source = Environment.GetEnvironmentVariable("NCW_LEGACY_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(source) || !File.Exists(source), "NCW_LEGACY_DB is not set to a 2.x database; skipping the real-database check.");
        Directory.CreateDirectory(_root);
        File.Copy(source!, Path.Combine(_root, AgentDatabase.FileName));
        _db = AgentDatabase.Open(_root);
        Assert.True(_db.UserVersion() >= 37);
        return new SessionRepository(_db);
    }

    private long Count(string sql, params (string, object)[] args)
    {
        using var command = _db!.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private string Scope()
    {
        using var command = _db!.Connection.CreateCommand();
        command.CommandText = "SELECT json FROM kv WHERE key = 'config-profile.scope'";
        return command.ExecuteScalar() is string json ? JsonDocument.Parse(json).RootElement.GetString() ?? "local" : "local";
    }

    [Fact]
    public void Every_visible_2x_session_reads_and_the_counts_match_raw_sql()
    {
        var repo = OpenCopy();
        var scope = Scope();
        var visible = $"""
            (EXISTS (SELECT 1 FROM workspaces w WHERE w.id = s.workspace_id AND w.owner = '{scope}')
             OR ('{scope}' = 'local' AND NOT EXISTS (SELECT 1 FROM workspaces w WHERE w.id = s.workspace_id)))
            """;

        var all = repo.ListAll(topLevelOnly: false);
        var top = repo.ListAll(topLevelOnly: true);

        Assert.Equal(Count($"SELECT count(*) FROM sessions s WHERE {visible}"), all.Count);
        Assert.Equal(Count($"SELECT count(*) FROM sessions s WHERE s.parent_session_id IS NULL AND {visible}"), top.Count);
        // 列是真源:每一行都读得出来,标题与列一致
        foreach (var session in all)
        {
            Assert.Equal(Text("SELECT title FROM sessions WHERE id = $id", session.Id), session.Title);
        }

        // 侧边栏列表 = 顶层 + 非定时任务;逐工作区拼起来要等于全局
        var listed = top.Select(s => s.WorkspaceId).Distinct().SelectMany(w => repo.List(w)).Count();
        Assert.Equal(top.Count(s => s.Origin != "scheduled"), listed);
    }

    private string? Text(string sql, string id)
    {
        using var command = _db!.Connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    [Fact]
    public void History_pages_summaries_and_usage_work_on_the_largest_real_sessions()
    {
        var repo = OpenCopy();
        var biggest = new List<(string Id, long N)>();
        using (var command = _db!.Connection.CreateCommand())
        {
            command.CommandText = "SELECT session_id, count(*) AS n FROM messages GROUP BY session_id ORDER BY n DESC LIMIT 30";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                biggest.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        var checkedSessions = 0;
        foreach (var (id, n) in biggest)
        {
            if (repo.Get(id) is null)
            {
                continue; // 别的账户作用域的会话
            }

            checkedSessions++;
            Assert.Equal(n, repo.CountMessages(id));
            var history = repo.History(id);
            Assert.Equal(n, history.Count);
            Assert.All(history, m => Assert.StartsWith("[", m.PartsJson));

            // 往前翻页要能无缝拼回整段历史
            var rebuilt = new List<string>();
            var page = repo.HistoryPage(id, 50);
            rebuilt.InsertRange(0, page.Messages.Select(m => m.Id));
            while (page.HasMore)
            {
                page = repo.HistoryPage(id, 50, page.Messages[0].Id);
                rebuilt.InsertRange(0, page.Messages.Select(m => m.Id));
            }

            Assert.Equal(history.Select(m => m.Id), rebuilt);
            Assert.NotNull(repo.Page(id, 20));
            Assert.NotNull(repo.Summary(id));
            _ = repo.RunUsage(id);
            _ = repo.RunModel(id);
        }

        Assert.True(checkedSessions > 0, "no visible session with messages found in the database");
    }

    [Fact]
    public void The_full_text_index_is_mapped_one_to_one_and_search_runs_on_real_data()
    {
        var repo = OpenCopy();

        Assert.Equal(Count("SELECT count(*) FROM messages"), Count("SELECT count(*) FROM messages_fts_rowid"));
        Assert.Equal(0, Count("SELECT count(*) FROM messages_fts WHERE rowid NOT IN (SELECT fts_rowid FROM messages_fts_rowid)"));

        // 用库里真实出现过的一个词(取最新一条带文字的用户消息的第一个词)做一次搜索
        using var command = _db!.Connection.CreateCommand();
        command.CommandText = "SELECT message_id, content FROM messages_fts WHERE length(content) BETWEEN 10 AND 200 LIMIT 1";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var word = reader.GetString(1).Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];

        var hits = repo.Search(word, limit: 20);

        Assert.True(hits.Count <= 20);
        Assert.All(hits, h => Assert.NotNull(repo.Get(h.SessionId)));
        Assert.All(hits, h => Assert.Null(repo.Get(h.SessionId)!.ParentSessionId)); // 子代理转录不进搜索
    }

    [Fact]
    public void Rewriting_a_real_session_keeps_every_json_key_it_had()
    {
        var repo = OpenCopy();
        var target = repo.ListAll(topLevelOnly: true).First();
        var before = JsonDocument.Parse(Text("SELECT json FROM sessions WHERE id = $id", target.Id)!).RootElement
            .EnumerateObject().Select(p => p.Name).ToHashSet();

        repo.Put(target);

        var after = JsonDocument.Parse(Text("SELECT json FROM sessions WHERE id = $id", target.Id)!).RootElement;
        var afterKeys = after.EnumerateObject().Select(p => p.Name).ToHashSet();
        // 2.x 写进去的每个键 3.0 写回之后都还在(不认识的原样保留;已知字段按 2.x 的形状重写)
        Assert.Subset(afterKeys, before);
        Assert.Equal("idle", after.GetProperty("status").GetString());
        Assert.Equal(target.Title, after.GetProperty("title").GetString());
    }

    [Fact]
    public void Writes_on_a_real_database_round_trip_and_leave_the_counts_as_they_were()
    {
        var repo = OpenCopy();
        var workspace = repo.ListAll(topLevelOnly: true).First().WorkspaceId;
        long Sessions() => Count("SELECT count(*) FROM sessions");
        long Messages() => Count("SELECT count(*) FROM messages");
        long Fts() => Count("SELECT count(*) FROM messages_fts");
        var (s0, m0, f0) = (Sessions(), Messages(), Fts());

        var session = repo.Create(new SessionCreate(workspace, Title: "兼容性检查"));
        repo.Commit(session.Id, new MessageRecord("compat-u1", "user", """[{"type":"text","text":"compatneedle 提问"}]""", session.CreatedAt));
        repo.Commit(session.Id, new MessageRecord("compat-a1", "assistant", """[{"type":"text","text":"compatneedle 回答"}]""", session.CreatedAt + 1), "compat-run");
        repo.EditMessage(session.Id, "compat-u1", "compatneedle 改过的提问", truncate: false);

        Assert.Equal(2, repo.Search("compatneedle").Count);
        Assert.Equal(["compat-u1"], repo.CommittedInputIds(session.Id, ["compat-u1", "compat-a1"]));
        var branch = repo.Branch(session.Id, "compat-u1", "兼容性检查 · branch");
        Assert.Equal(2, repo.CountMessages(branch.Id));

        repo.Delete(session.Id);
        repo.Delete(branch.Id);

        Assert.Equal((s0, m0, f0), (Sessions(), Messages(), Fts()));
        Assert.Equal(Count("SELECT count(*) FROM messages_fts"), Count("SELECT count(*) FROM messages_fts_rowid"));
        Assert.Empty(repo.Search("compatneedle"));
    }
}
