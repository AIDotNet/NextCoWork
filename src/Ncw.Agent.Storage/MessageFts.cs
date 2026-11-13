using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ncw.Agent.Storage;

/// <summary>
/// 消息全文索引(messages_fts + messages_fts_rowid 映射表),行为与 2.x 的 <c>db/message-text.ts</c> 一致。
/// ★ FTS 行一律按 rowid 增删改:message_id 是 UNINDEXED 列,按它查是全表扫描(2.x 实测 21 万条消息时每次 0.3–0.65 秒)。
/// </summary>
public static class MessageFts
{
    /// <summary>
    /// 一条消息的可搜索正文(FTS 的 content 列)。两条写入路径必须算出逐字相同的结果,所以只有这一个实现。
    /// 容错:不是数组当空;单个坏块只丢它自己。
    /// </summary>
    public static string SearchableText(string partsJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(partsJson);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return string.Empty;
            }

            var chunks = new List<string>();
            foreach (var part in document.RootElement.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type))
                {
                    continue;
                }

                switch (type.GetString())
                {
                    case "text" or "thinking":
                        AddString(chunks, part, "text");
                        break;
                    case "tool_call":
                        AddString(chunks, part, "name");
                        if (part.TryGetProperty("input", out var input))
                        {
                            chunks.Add(input.GetRawText());
                        }

                        break;
                    case "tool_result":
                        if (part.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
                        {
                            AddString(chunks, output, "content");
                        }

                        break;
                    case "subagent":
                        AddString(chunks, part, "summary");
                        break;
                    case "error":
                        if (part.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                        {
                            AddString(chunks, error, "message");
                        }

                        break;
                }
            }

            return string.Join('\n', chunks.Where(c => c.Length > 0));
        }

        static void AddString(List<string> chunks, JsonElement element, string name)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                chunks.Add(value.GetString()!);
            }
        }
    }

    /// <summary>写入或替换一条消息的索引行,沿用它原有的 FTS rowid。调用方负责事务。</summary>
    public static void Write(SqliteConnection connection, SqliteTransaction? tx, string messageId, string sessionId, string title, string content)
    {
        long rowid;
        var existing = Scalar(connection, tx, "SELECT fts_rowid FROM messages_fts_rowid WHERE message_id = $m", ("$m", messageId));
        if (existing is null)
        {
            Run(connection, tx, "INSERT INTO messages_fts_rowid (message_id, session_id) VALUES ($m, $s)", ("$m", messageId), ("$s", sessionId));
            rowid = (long)Scalar(connection, tx, "SELECT last_insert_rowid()")!;
            // 新分到的 rowid 上只可能是 V37 之前版本留下的无映射行:不先腾掉,下面的 INSERT 会被 FTS5 拒绝。
            Run(connection, tx, "DELETE FROM messages_fts WHERE rowid = $r", ("$r", rowid));
        }
        else
        {
            rowid = (long)existing;
            Run(connection, tx, "DELETE FROM messages_fts WHERE rowid = $r", ("$r", rowid));
            Run(connection, tx, "UPDATE messages_fts_rowid SET session_id = $s WHERE fts_rowid = $r", ("$s", sessionId), ("$r", rowid));
        }

        Run(connection, tx, "INSERT INTO messages_fts (rowid, message_id, session_id, title, content) VALUES ($r, $m, $s, $t, $c)",
            ("$r", rowid), ("$m", messageId), ("$s", sessionId), ("$t", title), ("$c", content));
    }

    /// <summary>清空并按消息表重建全部索引(2.x 第 29 条迁移)。</summary>
    internal static void Rebuild(SqliteConnection connection, SqliteTransaction tx)
    {
        Run(connection, tx, "DELETE FROM messages_fts");
        if (HasRowidMap(connection, tx))
        {
            Run(connection, tx, "DELETE FROM messages_fts_rowid");
        }

        var rows = new List<(string Id, string Session, string Parts, string Title)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText = "SELECT m.id, m.session_id, m.parts, s.title FROM messages m JOIN sessions s ON s.id = m.session_id ORDER BY m.id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3)));
            }
        }

        var mapped = HasRowidMap(connection, tx);
        foreach (var row in rows)
        {
            var content = SearchableText(row.Parts);
            if (mapped)
            {
                Write(connection, tx, row.Id, row.Session, row.Title, content);
            }
            else
            {
                // V37 之前没有映射表:退回旧写法,之后 V37 会从现有 FTS 行补齐映射。
                Run(connection, tx, "INSERT INTO messages_fts (message_id, session_id, title, content) VALUES ($m, $s, $t, $c)",
                    ("$m", row.Id), ("$s", row.Session), ("$t", row.Title), ("$c", content));
            }
        }
    }

    /// <summary>
    /// 每次开库:把映射最大 rowid 之后、没有映射的 FTS 行补进映射(V37 之前的版本开过同一个库会留下这种行),
    /// 剩下无主的删掉。尽力而为,失败只回滚这一段,不拦开库。
    /// </summary>
    internal static void Reconcile(SqliteConnection connection)
    {
        if (!HasRowidMap(connection, null))
        {
            return;
        }

        using var tx = connection.BeginTransaction();
        try
        {
            var max = Convert.ToInt64(Scalar(connection, tx, "SELECT coalesce(max(fts_rowid), 0) FROM messages_fts_rowid"));
            Run(connection, tx, """
                INSERT INTO messages_fts_rowid (fts_rowid, message_id, session_id)
                  SELECT f.rowid, f.message_id, f.session_id FROM messages_fts f
                  WHERE f.rowid > $max AND EXISTS (SELECT 1 FROM messages m WHERE m.id = f.message_id) ORDER BY f.rowid
                ON CONFLICT(message_id) DO UPDATE SET fts_rowid = excluded.fts_rowid, session_id = excluded.session_id
                """, ("$max", max));
            Run(connection, tx, "DELETE FROM messages_fts WHERE rowid > $max AND rowid NOT IN (SELECT fts_rowid FROM messages_fts_rowid WHERE fts_rowid > $max)",
                ("$max", max));
            tx.Commit();
        }
        catch (SqliteException)
        {
            tx.Rollback();
        }
    }

    private static bool HasRowidMap(SqliteConnection connection, SqliteTransaction? tx) =>
        Scalar(connection, tx, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'messages_fts_rowid'") is not null;

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteScalar();
    }

    private static void Run(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object Value)[] args)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
