using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Storage.Tests;

/// <summary>真文件库、每个用例独立目录(回执、全文索引的价值有一半在「重开库之后还在」,内存库测不出来)。</summary>
public abstract class SessionTestBase : IDisposable
{
    protected const long At = 1_700_000_000_000;

    protected string Root { get; } = Path.Combine(Path.GetTempPath(), $"ncwsess-{Guid.NewGuid():N}");

    protected AgentDatabase Db { get; set; }

    protected SessionRepository Repo { get; set; }

    protected SessionInputStore Inputs { get; set; }

    protected SessionTestBase()
    {
        (Db, Repo, Inputs) = Open();
    }

    private (AgentDatabase, SessionRepository, SessionInputStore) Open()
    {
        var db = AgentDatabase.Open(Root);
        return (db, new SessionRepository(db), new SessionInputStore(db));
    }

    /// <summary>关库重开:测的就是落盘之后的东西。</summary>
    protected void Restart()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        (Db, Repo, Inputs) = Open();
    }

    public void Dispose()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    protected void Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var command = Db.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }

    protected long Count(string sql, params (string Name, object? Value)[] args)
    {
        using var command = Db.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return Convert.ToInt64(command.ExecuteScalar());
    }

    protected string? Text(string sql, params (string Name, object? Value)[] args)
    {
        using var command = Db.Connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteScalar() as string;
    }

    /// <summary>登记一个工作区行(owner 即账户作用域)。</summary>
    protected void Workspace(string id, string owner = "local") =>
        Exec("INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES ($id, 0, $j, $o)", ("$id", id), ("$j", $$"""{"id":"{{id}}"}"""), ("$o", owner));

    protected void SetScope(string scope) =>
        Exec("INSERT INTO kv (key, json) VALUES ('config-profile.scope', $j) ON CONFLICT (key) DO UPDATE SET json = excluded.json", ("$j", Quote(scope)));

    protected SessionRecord NewSession(string id, string workspace = "ws", string title = "T") =>
        Repo.Create(new SessionCreate(workspace, id, Title: title));

    // 消息构造:parts 是 JSON 文本,和 2.x 落库的形状一致。

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    protected static string Quote(string value) => JsonValue.Create(value).ToJsonString(Relaxed);

    protected static string TextPart(string text) => $$"""{"type":"text","text":{{Quote(text)}}}""";

    protected static string Parts(params string[] parts) => "[" + string.Join(",", parts) + "]";

    protected static MessageRecord Ask(string id, string text, long at = At) => new(id, "user", Parts(TextPart(text)), at);

    protected static MessageRecord Say(string id, string text, long at = At) => new(id, "assistant", Parts(TextPart(text)), at);

    protected static MessageRecord ToolCall(string id, string callId, string name, string inputJson, long at = At) =>
        new(id, "assistant", Parts($$"""{"type":"tool_call","callId":"{{callId}}","name":"{{name}}","input":{{inputJson}}}"""), at);

    protected static MessageRecord ToolResult(string id, string callId, string content, long at = At) =>
        new(id, "user", Parts($$"""{"type":"tool_result","callId":"{{callId}}","output":{"content":{{Quote(content)}}},"isError":false}"""), at);

    protected static string[] Ids(IEnumerable<MessageRecord> messages) => [.. messages.Select(m => m.Id)];
}
