using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ncw.Agent.Storage.Sessions;

public sealed partial class SessionRepository
{
    /// <summary>页首往前补到一轮开头时,最多再多读这么多条 —— 一轮里几百次工具调用也不该让一页无界。</summary>
    public const int PageTurnExtensionMax = 500;

    private const string MessageColumns = "m.id, m.role, m.parts, m.schema_version, m.created_at, m.internal, m.meta";

    private static readonly string VisibleMessage =
        $"EXISTS (SELECT 1 FROM sessions s WHERE s.id = m.session_id AND {ConfigScope.WorkspaceVisible("s.workspace_id")})";

    // ── 写 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 消息落盘的唯一入口(2.x 的 <c>commitMessage</c>);同一 message id 重放幂等。
    /// 一个事务里:消息行 + 全文索引 +(用户消息)消费回执 + 会话更新时间前移。
    /// ★ 用户消息一提交就留下消费回执,和消息在同一个事务里 —— 它是「这条输入已经执行过」的唯一持久事实,
    /// 分开写的话中间任何一次失败都会留下「已落盘却没被消费过」的消息,队列清理、重启恢复会据此再执行一遍。
    /// 回执用 <c>INSERT OR IGNORE</c>:重放时回执保留首次提交的时刻。assistant / tool 消息不记。
    /// </summary>
    public void Commit(string sessionId, MessageRecord message, string? runId = null) => database.InTransaction(() =>
    {
        var session = Require(sessionId);
        var existingOrdinal = database.Row("SELECT session_id, ordinal FROM messages WHERE id = $id",
            r => new OrdinalRow(r.GetString(0), r.GetInt64(1)), ("$id", message.Id));
        if (existingOrdinal is not null && existingOrdinal.SessionId != sessionId)
        {
            throw new StorageException($"Message {message.Id} already belongs to another session.");
        }

        var ordinal = existingOrdinal?.Ordinal
            ?? database.Count("SELECT COALESCE(MAX(ordinal), -1) FROM messages WHERE session_id = $s", ("$s", sessionId)) + 1;
        WriteMessage(session, message, ordinal, runId);

        if (message.Role == "user")
        {
            database.Run("INSERT OR IGNORE INTO session_input_receipts (session_id, message_id, committed_at) VALUES ($s, $m, $t)",
                ("$s", sessionId), ("$m", message.Id), ("$t", message.CreatedAt));
        }

        Put(session with { UpdatedAt = Math.Max(session.UpdatedAt, message.CreatedAt) });
    });

    /// <summary>
    /// 权威历史对账(2.x 的 <c>replaceHistory</c>):未变化的消息保留正文、索引、附件和 run 归属。
    /// 重复 id、属于别的会话的 id 在改动任何东西之前就拒绝。变化的判定按 <c>parts</c> 文本逐字比较 ——
    /// 会话层应把读到的文本原样回传,没改的消息才不会被重写。
    /// <paramref name="detachImportedSource"/>:手动编辑转录 = 永久脱离导入来源,必须和替换在同一个事务里。
    /// </summary>
    public void ReplaceHistory(string sessionId, IReadOnlyList<MessageRecord> messages, bool detachImportedSource = false, bool keepAttachmentsOfRemoved = false) => database.InTransaction(() =>
    {
        var session = Require(sessionId);
        var existing = database.Rows("SELECT id, ordinal, role, parts, schema_version, created_at, internal, meta FROM messages WHERE session_id = $s",
            r => new ExistingMessage(r.GetString(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.Int(4, 1), r.Int(5), r.Int(6) != 0, r.Text(7)),
            ("$s", sessionId)).ToDictionary(m => m.Id);

        var ids = new HashSet<string>();
        var renumber = false;
        var entries = new List<(MessageRecord Message, int Ordinal, bool Changed)>(messages.Count);
        for (var ordinal = 0; ordinal < messages.Count; ordinal++)
        {
            var message = messages[ordinal];
            RequirePartsArray(message);
            if (!ids.Add(message.Id))
            {
                throw new StorageException($"Message {message.Id} appears twice in the transcript.");
            }

            existing.TryGetValue(message.Id, out var previous);
            if (previous is null && database.Value("SELECT session_id FROM messages WHERE id = $id", ("$id", message.Id)) is string owner && owner != sessionId)
            {
                throw new StorageException($"Message {message.Id} already belongs to another session.");
            }

            if (previous is not null && previous.Ordinal != ordinal)
            {
                renumber = true;
            }

            var changed = previous is null
                || previous.Role != message.Role
                || previous.Parts != message.PartsJson
                || previous.SchemaVersion != message.SchemaVersion
                || previous.CreatedAt != message.CreatedAt
                || previous.Internal != message.Internal
                || previous.Meta != message.MetaJson;
            entries.Add((message, ordinal, changed));
        }

        var removed = existing.Keys.Where(id => !ids.Contains(id)).ToList();
        var updatedAt = messages.Aggregate(session.UpdatedAt, (latest, m) => Math.Max(latest, m.CreatedAt));
        if (detachImportedSource)
        {
            DetachImported(sessionId);
        }

        if (!renumber && removed.Count == 0 && entries.All(e => !e.Changed))
        {
            if (updatedAt != session.UpdatedAt)
            {
                Put(session with { UpdatedAt = updatedAt });
            }

            return;
        }

        // (session_id, ordinal) 唯一:先把现有的挪到负数区,再逐条写回正确的位置。
        if (renumber)
        {
            database.Run("UPDATE messages SET ordinal = -ordinal - 1 WHERE session_id = $s", ("$s", sessionId));
        }

        if (keepAttachmentsOfRemoved && removed.Count > 0)
        {
            observer?.OnMessagesRemoving(database, session, removed); // 级联删除之前:附件域把它们降回草稿
        }

        foreach (var id in removed)
        {
            database.Run("DELETE FROM messages WHERE id = $id", ("$id", id));
        }

        if (removed.Count > 0)
        {
            MessageFts.DeleteOrphans(database.Connection, database.CurrentTransaction, sessionId);
        }

        foreach (var (message, ordinal, changed) in entries)
        {
            if (changed)
            {
                WriteMessage(session, message, ordinal, runId: null);
            }
            else if (renumber)
            {
                database.Run("UPDATE messages SET ordinal = $o WHERE id = $id", ("$o", ordinal), ("$id", message.Id));
            }
        }

        Put(session with { UpdatedAt = updatedAt });
    });

    /// <summary>
    /// 只换一条消息的 parts —— 前提是它还是读出来时的那一份(比较并替换)。返回是否真的写了。
    /// 走和正常提交同一条写路径:全文索引、附件观察者一致;ordinal、run 归属、时间都不动。
    /// </summary>
    public bool ReplacePartsIfUnchanged(string sessionId, string messageId, string expectedPartsJson, string newPartsJson) => database.InTransaction(() =>
    {
        if (Get(sessionId) is not { } session)
        {
            return false;
        }

        var row = database.Row($"SELECT {MessageColumns}, m.ordinal, m.parts FROM messages m WHERE m.id = $id AND m.session_id = $s AND {VisibleMessage}",
            r => new OrdinalMessage(MapMessage(r), r.GetInt64(7), r.GetString(8)), ("$id", messageId), ("$s", sessionId), Scope());
        if (row is null || row.RawParts != expectedPartsJson)
        {
            return false;
        }

        WriteMessage(session, row.Message with { PartsJson = newPartsJson }, row.Ordinal, runId: null);
        return true;
    });

    /// <summary>登记一次 Run(2.x 的 <c>setRunRecord</c>):状态与结束时间可更新,开始时间不变。</summary>
    public void SetRunRecord(string id, string sessionId, string status, long startedAt, long? endedAt = null) => database.InTransaction(() =>
    {
        _ = Require(sessionId);
        database.Run("""
            INSERT INTO runs (id, session_id, status, started_at, ended_at) VALUES ($id, $s, $status, $start, $end)
            ON CONFLICT (id) DO UPDATE SET status = excluded.status, ended_at = excluded.ended_at
            """, ("$id", id), ("$s", sessionId), ("$status", status), ("$start", startedAt), ("$end", endedAt));
    });

    /// <summary>一次 Run 的登记行(3.0 新增的读口:编排层测试和「按会话找 Run」用;2.x 只写不读)。</summary>
    public RunRecordRow? GetRunRecord(string id) => database.Row(
        "SELECT r.id, r.session_id, r.status, r.started_at, r.ended_at FROM runs r JOIN sessions s ON s.id = r.session_id WHERE r.id = $id AND " + ConfigScope.WorkspaceVisible("s.workspace_id"),
        r => new RunRecordRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetInt64(4)),
        ("$id", id), Scope());

    /// <summary>这条会话名下登记过的全部 Run id(会话删除前:把属于它们的后台 shell 一并清掉)。</summary>
    public IReadOnlyList<string> RunIds(string sessionId) => database.Rows(
        "SELECT r.id FROM runs r JOIN sessions s ON s.id = r.session_id WHERE r.session_id = $s AND " + ConfigScope.WorkspaceVisible("s.workspace_id") + " ORDER BY r.started_at, r.id",
        r => r.GetString(0), ("$s", sessionId), Scope());

    // ── 读 ────────────────────────────────────────────────────────────────────

    public IReadOnlyList<MessageRecord> History(string sessionId) => database.Rows(
        $"SELECT {MessageColumns} FROM messages m WHERE m.session_id = $s AND {VisibleMessage} ORDER BY m.ordinal, m.id",
        MapMessage, ("$s", sessionId), Scope());

    /// <summary>一条会话有几条消息 —— 元数据卡片要的就是这个数,不必为它把整段转录读出来。</summary>
    public long CountMessages(string sessionId) => database.Count(
        $"SELECT COUNT(*) FROM messages m WHERE m.session_id = $s AND {VisibleMessage}", ("$s", sessionId), Scope());

    /// <summary>一条会话里最后一条助手消息(子代理交差的正文就是它;不挑 internal)。</summary>
    public MessageRecord? LastAssistant(string sessionId) => database.Row(
        $"SELECT {MessageColumns} FROM messages m WHERE m.session_id = $s AND m.role = 'assistant' AND {VisibleMessage} ORDER BY m.ordinal DESC, m.id DESC LIMIT 1",
        MapMessage, ("$s", sessionId), Scope());

    /// <summary>最后一条非内部的助手消息(内部的是目标 kickoff 之类,不是给用户看的回复)。</summary>
    public MessageRecord? LastVisibleAssistant(string sessionId) => database.Row(
        $"SELECT {MessageColumns} FROM messages m WHERE m.session_id = $s AND m.role = 'assistant' AND m.internal = 0 AND {VisibleMessage} ORDER BY m.ordinal DESC, m.id DESC LIMIT 1",
        MapMessage, ("$s", sessionId), Scope());

    /// <summary>
    /// 最后一条带目标标记(<c>goal_status</c> 块)的助手消息。先用 LIKE 在原文上筛(不解析),再逐条解析确认 ——
    /// <c>_</c> 是 LIKE 通配、文本里也可能恰好出现这串字符,所以筛出来的只是候选,真有那个块才算。
    /// </summary>
    public MessageRecord? LastGoalStatus(string sessionId)
    {
        var candidates = database.Rows(
            $"SELECT {MessageColumns} FROM messages m WHERE m.session_id = $s AND m.role = 'assistant' AND m.parts LIKE '%\"type\":\"goal_status\"%' AND {VisibleMessage} ORDER BY m.ordinal DESC, m.id DESC",
            MapMessage, ("$s", sessionId), Scope());
        return candidates.FirstOrDefault(m => MessageShape.HasPartType(m.PartsJson, "goal_status"));
    }

    /// <summary>
    /// 这条会话里用户亲手发的最近几条消息,新的在前(输入框「建议下一步」读它)。
    /// ★ 不复用 <see cref="HistoryPage"/>:一轮里几百次工具调用会把前面几条提问挤出一页。
    /// ★ <c>role = 'user'</c> 不等于「用户说的话」(工具结果也以 user 角色落库):SQL 先挑出带 text 块的(便宜),再交给 <see cref="MessageShape.IsTurnStart"/> 认定。
    /// </summary>
    public IReadOnlyList<MessageRecord> RecentPrompts(string sessionId, int limit) => database.Rows($"""
        SELECT {MessageColumns} FROM messages m
        WHERE m.session_id = $s AND m.role = 'user' AND m.internal = 0
          AND json_valid(m.parts) AND EXISTS (SELECT 1 FROM json_each(m.parts) WHERE json_extract(value, '$.type') = 'text')
          AND {VisibleMessage}
        ORDER BY m.ordinal DESC, m.id DESC LIMIT $n
        """, MapMessage, ("$s", sessionId), ("$n", limit), Scope()).Where(MessageShape.IsTurnStart).ToList();

    /// <summary>
    /// 这个工作区里用户发过的指令,新的在前、去重(输入框的历史补全)。
    /// ★ 两道上限:先限最近 <c>Sessions</c> 个顶层会话(子代理会话不是用户打的),再限 <c>Scan</c> 行。
    /// ★ 斜杠命令在库里是展开后的整份模板,所以超长(&gt;500 字符)、多行(&gt;3 行)的一律不要。
    /// </summary>
    public IReadOnlyList<string> RecentWorkspacePrompts(string workspaceId, RecentPromptLimits? limits = null)
    {
        limits ??= new RecentPromptLimits();
        var rows = database.Rows($"""
            SELECT {MessageColumns} FROM messages m
            WHERE m.session_id IN (
                SELECT s.id FROM sessions s WHERE s.workspace_id = $w AND s.parent_session_id IS NULL AND {VisibleSession}
                ORDER BY s.updated_at DESC LIMIT $sessions)
              AND m.role = 'user' AND m.internal = 0
            ORDER BY m.created_at DESC, m.ordinal DESC LIMIT $scan
            """, MapMessage, ("$w", workspaceId), ("$sessions", limits.Sessions), ("$scan", limits.Scan), Scope());
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var message in rows)
        {
            if (!MessageShape.IsTurnStart(message))
            {
                continue;
            }

            var text = MessageShape.VisibleText(message.PartsJson).Trim();
            if (text == "" || text.Length > 500 || text.Split('\n').Length > 3 || !seen.Add(text))
            {
                continue;
            }

            result.Add(text);
            if (result.Count >= limits.Limit)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// 转录的一页:<paramref name="beforeMessageId"/> 之前(缺省 = 最新)的 <paramref name="limit"/> 条,从一轮的开头切起。
    /// ★ 页首必须是一条可见提问:从一轮中间切开的话,页首那条 tool_result 找不到它的 tool_call。所以先按条数取,
    /// 再往前补到最近的一轮开头(最多 <see cref="PageTurnExtensionMax"/> 条)。锚点不在这条会话里:空页,不去猜。
    /// </summary>
    public HistoryPageResult HistoryPage(string sessionId, int limit, string? beforeMessageId = null)
    {
        lock (database.Gate)
        {
            var upper = long.MaxValue;
            if (beforeMessageId is not null)
            {
                var anchor = database.Value($"SELECT m.ordinal FROM messages m WHERE m.id = $id AND m.session_id = $s AND {VisibleMessage}",
                    ("$id", beforeMessageId), ("$s", sessionId), Scope());
                if (anchor is null)
                {
                    return new HistoryPageResult([], false);
                }

                upper = Convert.ToInt64(anchor);
            }

            var rows = database.Rows(
                $"SELECT {MessageColumns}, m.ordinal FROM messages m WHERE m.session_id = $s AND m.ordinal < $upper AND {VisibleMessage} ORDER BY m.ordinal DESC, m.id DESC LIMIT $n",
                r => new OrdinalMessage(MapMessage(r), r.GetInt64(7), ""),
                ("$s", sessionId), ("$upper", upper), ("$n", Math.Max(1, limit)), Scope());

            // 保持新的在前,直到找到边界,避免一轮里几百条时的几百次查询和数组头插。
            var messages = rows.Select(r => r.Message).ToList();
            long? first = rows.Count > 0 ? rows[^1].Ordinal : null;
            if (first is not null && !MessageShape.IsTurnStart(messages[^1]))
            {
                using var command = database.Command(
                    $"SELECT {MessageColumns}, m.ordinal FROM messages m WHERE m.session_id = $s AND m.ordinal < $o AND {VisibleMessage} ORDER BY m.ordinal DESC, m.id DESC LIMIT $n");
                command.Parameters.AddWithValue("$s", sessionId);
                command.Parameters.AddWithValue("$o", first.Value);
                command.Parameters.AddWithValue("$n", PageTurnExtensionMax);
                command.Parameters.AddWithValue(ConfigScope.Param, ConfigScope.Current(database));
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var message = MapMessage(reader);
                    messages.Add(message);
                    first = reader.GetInt64(7);
                    if (MessageShape.IsTurnStart(message))
                    {
                        break;
                    }
                }
            }

            var hasMore = first is not null && database.Exists(
                $"SELECT 1 FROM messages m WHERE m.session_id = $s AND m.ordinal < $o AND {VisibleMessage} LIMIT 1",
                ("$s", sessionId), ("$o", first.Value), Scope());
            messages.Reverse();
            return new HistoryPageResult(messages, hasMore);
        }
    }

    /// <summary>
    /// 消息 → 产出它的 Run。第 12 条迁移之前的消息没有归属,直接不出现在结果里
    /// (「不知道」和「属于某个 Run」在界面上是两件事:后者会去查一个查不到的 Run 然后显示 0 token)。
    /// <paramref name="messageIds"/> 缺省 = 整条会话;给了就只查这些(用 json_each 驱动,走主键,不扫会话索引)。
    /// </summary>
    public IReadOnlyDictionary<string, string> MessageRuns(string sessionId, IReadOnlyCollection<string>? messageIds = null)
    {
        var rows = messageIds is null
            ? database.Rows($"SELECT m.id, m.run_id FROM messages m WHERE m.session_id = $s AND m.run_id IS NOT NULL AND {VisibleMessage}",
                r => (r.GetString(0), r.GetString(1)), ("$s", sessionId), Scope())
            : database.Rows($"""
                SELECT m.id, m.run_id FROM json_each($ids) AS page
                CROSS JOIN messages AS m ON m.id = page.value
                WHERE m.session_id = $s AND m.run_id IS NOT NULL AND {VisibleMessage}
                """, r => (r.GetString(0), r.GetString(1)),
                ("$ids", JsonSerializer.Serialize(messageIds.ToArray(), StorageJsonContext.Default.StringArray)), ("$s", sessionId), Scope());
        return rows.ToDictionary(r => r.Item1, r => r.Item2);
    }

    /// <summary>
    /// 用户消息的消费回执:给定的 message id 里哪些已经有回执(2.x 的 <c>committedInputIds</c>)。
    /// ★ 队列判定唯一的收口:插话队列、定时任务、重试路径问的都是同一个问题。
    /// ★ 只查指定会话的行:跨会话的同名 id 读不到(读串了就等于放行一条本该被拒的输入)。
    /// ★ 参数化 + json_each,不把 ids 拼进 SQL;不做静默截断 —— 少查就是少判,也就是可能再执行一次。
    /// </summary>
    public IReadOnlyList<string> CommittedInputIds(string sessionId, IReadOnlyCollection<string> ids)
    {
        var unique = ids.Distinct().ToArray();
        if (unique.Length == 0)
        {
            return [];
        }

        return database.Rows($"""
            SELECT r.message_id FROM session_input_receipts r
            WHERE r.session_id = $s AND r.message_id IN (SELECT value FROM json_each($ids))
              AND EXISTS (SELECT 1 FROM sessions s WHERE s.id = r.session_id AND {ConfigScope.WorkspaceVisible("s.workspace_id")})
            """, r => r.GetString(0),
            ("$s", sessionId), ("$ids", JsonSerializer.Serialize(unique, StorageJsonContext.Default.StringArray)), Scope());
    }

    // ── 内部 ──────────────────────────────────────────────────────────────────

    private void WriteMessage(SessionRecord session, MessageRecord message, long ordinal, string? runId)
    {
        RequirePartsArray(message);
        if (database.Value("SELECT session_id FROM messages WHERE id = $id", ("$id", message.Id)) is string owner && owner != session.Id)
        {
            throw new StorageException($"Message {message.Id} already belongs to another session.");
        }

        // ★ run_id 用 COALESCE 合并,不能直接 = excluded.run_id:整段历史重写(编辑消息、与客户端对账)手上没有 Run 归属,
        // 直接赋值的话用户改一个错别字就会把这条会话所有历史轮次的用量读数抹成空。
        database.Run("""
            INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at, run_id, internal, meta)
            VALUES ($id, $s, $ordinal, $role, $parts, $schema, $created, $run, $internal, $meta)
            ON CONFLICT (id) DO UPDATE SET ordinal = excluded.ordinal, parts = excluded.parts, role = excluded.role,
              schema_version = excluded.schema_version, created_at = excluded.created_at,
              run_id = COALESCE(excluded.run_id, messages.run_id), internal = excluded.internal, meta = excluded.meta
            """,
            ("$id", message.Id), ("$s", session.Id), ("$ordinal", ordinal), ("$role", message.Role), ("$parts", message.PartsJson),
            ("$schema", message.SchemaVersion), ("$created", message.CreatedAt), ("$run", runId), ("$internal", message.Internal ? 1 : 0), ("$meta", message.MetaJson));
        MessageFts.Write(database.Connection, database.CurrentTransaction, message.Id, session.Id, session.Title, MessageFts.SearchableText(message.PartsJson));
        observer?.OnMessageWritten(database, session, message);
    }

    private static void RequirePartsArray(MessageRecord message)
    {
        if (message.Role.Length == 0 || !MessageShape.IsPartsArray(message.PartsJson))
        {
            throw new StorageException($"Message {message.Id} must have a role and a JSON array of parts.");
        }
    }

    private static MessageRecord MapMessage(SqliteDataReader r) => new(
        Id: r.GetString(0),
        Role: r.GetString(1),
        PartsJson: MessageShape.Normalize(r.Text(2)),
        CreatedAt: r.GetInt64(4),
        SchemaVersion: (int)r.Int(3, 1),
        Internal: r.Int(5) != 0,
        MetaJson: r.Text(6));

    private sealed record OrdinalMessage(MessageRecord Message, long Ordinal, string RawParts);

    private sealed record OrdinalRow(string SessionId, long Ordinal);

    private sealed record ExistingMessage(string Id, long Ordinal, string Role, string Parts, long SchemaVersion, long CreatedAt, bool Internal, string? Meta);
}
