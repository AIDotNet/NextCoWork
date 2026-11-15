using Microsoft.Data.Sqlite;
using Ncw.Core.Ids;

namespace Ncw.Agent.Storage.Sessions;

public sealed partial class SessionRepository
{
    // ── 按 id 改写转录 ─────────────────────────────────────────────────────────
    //
    // 客户端只持有转录的一页,不能再拿「手上那份」整段 ReplaceHistory:那会把页外的历史当成「用户删掉了」一并抹去。
    // 所以改写由 Agent 在**完整历史**上按 id 做(规则在 HistoryEdit,两边是同一条规则)。
    // 都在一个事务里,并且手动编辑 = 永久脱离导入来源。目标消息已经不在会话里抛异常(2.x 同)。

    /// <summary>编辑一条提问;<paramref name="truncate"/> = 从这条起重跑(这条和之后的全部移除,新提问由调用方另起一次 Run)。</summary>
    public void EditMessage(string sessionId, string messageId, string text, bool truncate) =>
        EditHistory(sessionId, history => HistoryEdit.EditUserMessage(history, messageId, text, truncate));

    /// <summary>删一整轮(提问 + 它引出的全部回复与工具回执)。</summary>
    public void DeleteTurn(string sessionId, string userMessageId) => EditHistory(sessionId, history =>
        HistoryEdit.TurnSpan(history, userMessageId) is { } span ? HistoryEdit.RemoveSpan(history, span) : null);

    /// <summary>只删一条助手回复(<c>fromId..toId</c> 与紧随其后的工具回执),提问留着。</summary>
    public void DeleteReply(string sessionId, string fromId, string toId) => EditHistory(sessionId, history =>
        HistoryEdit.ReplySpan(history, fromId, toId) is { } span ? HistoryEdit.RemoveSpan(history, span) : null);

    private void EditHistory(string sessionId, Func<IReadOnlyList<MessageRecord>, List<MessageRecord>?> edit) => database.InTransaction(() =>
    {
        _ = Require(sessionId);
        var next = edit(History(sessionId)) ?? throw new StorageException("The message to rewrite is no longer in this session.");
        ReplaceHistory(sessionId, next, detachImportedSource: true);
    });

    // ── 复制 / 分支 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 复制整条会话(2.x 的 <c>duplicateSession</c>)。<paramref name="rewrite"/> 在消息搬进新会话之前改写每一条
    /// (会话层用它把属于源会话的托管图片引用改写成新会话自己的;Storage 不读附件文件)。
    /// </summary>
    public SessionRecord Duplicate(string sourceSessionId, string title, Func<MessageRecord, MessageRecord>? rewrite = null) => database.InTransaction(() =>
    {
        var source = Require(sourceSessionId);
        return CloneInto(source, title, History(sourceSessionId), rewrite);
    });

    /// <summary>
    /// 从某一轮「分支」出一条新会话:只带上到这一轮为止的转录(<paramref name="uptoMessageId"/> 是引出这一轮的用户提问 id,
    /// 一路带到下一条可见提问之前,紧跟在提问后的工具结果一并带过去)。消息不存在抛异常。
    /// ★ 与设计稿原先的「parent_session_id 指回原会话」不同,照 2.x:分支是一条独立的**顶层**会话 —— 该列是子代理转录的过滤口,
    /// 设了它,这条分支就会从列表、搜索里消失。
    /// </summary>
    public SessionRecord Branch(string sourceSessionId, string uptoMessageId, string title, Func<MessageRecord, MessageRecord>? rewrite = null) => database.InTransaction(() =>
    {
        var source = Require(sourceSessionId);
        var history = History(sourceSessionId);
        var end = HistoryEdit.TurnEndIndex(history, uptoMessageId) ?? throw new StorageException($"Message not found: {uptoMessageId}");
        return CloneInto(source, title, history.Take(end).ToList(), rewrite);
    });

    /// <summary>
    /// 建一条新会话并把 <paramref name="messages"/> 整段搬进去(元数据沿用源会话,包括供应商和提炼来源)。
    /// 一个事务,中途失败整体回滚(2.x 是失败后手动删掉建了一半的会话)。新消息 id 全换(用消息自己的时间戳铸 ULID),
    /// run 归属不带过去,不留消费回执(与 2.x 一致:克隆走 replaceHistory,不走 commitMessage)。
    /// </summary>
    private SessionRecord CloneInto(SessionRecord source, string title, IReadOnlyList<MessageRecord> messages, Func<MessageRecord, MessageRecord>? rewrite)
    {
        var created = Create(new SessionCreate(
            WorkspaceId: source.WorkspaceId,
            Id: Ulid.New(_clock),
            Title: title,
            Model: source.Model,
            ModelProviderId: source.ModelProviderId,
            Mode: source.Mode,
            Thinking: source.Thinking,
            RootPathAtCreation: source.RootPathAtCreation,
            SkillSourceSessionId: source.SkillSourceSessionId,
            CreatedAt: Now()));
        var cloned = messages
            .Select(m => (rewrite?.Invoke(m) ?? m) with { Id = Ulid.New(new FixedClock(m.CreatedAt)) })
            .ToList();
        ReplaceHistory(created.Id, cloned);
        return Get(created.Id)!;
    }

    // ── 搜索 ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// 全局会话搜索(2.x 的 <c>searchAll</c>)。以短语查询避免用户输入的 MATCH 运算符破坏 FTS 语法。
    /// ★ 带 <c>s.parent_session_id IS NULL</c>:子代理转录不是用户的对话,FTS 与 LIKE 兜底两条都要,否则泄漏会在旧库 / 极端 Unicode 上悄悄复活。
    /// ★ 作用域收窄在 SQL 里(2.x 是 LIMIT 之后再过滤,会丢结果)。指定了工作区却看不见它就是空结果。
    /// </summary>
    public IReadOnlyList<SearchHit> Search(string query, string? workspaceId = null, int limit = 50)
    {
        var q = query.Trim();
        if (q == "")
        {
            return [];
        }

        var n = Math.Clamp(limit, 1, 200);
        var phrase = $"\"{q.Replace("\"", "\"\"")}\"";
        var visible = ConfigScope.WorkspaceVisible("s.workspace_id");
        var byWorkspace = workspaceId is null ? "" : "s.workspace_id = $w AND ";
        var args = new List<(string, object?)> { ("$n", n), Scope() };
        if (workspaceId is not null)
        {
            args.Add(("$w", workspaceId));
        }

        try
        {
            return database.Rows($"""
                SELECT f.session_id, f.message_id, s.workspace_id, s.title, snippet(messages_fts, 3, '<mark>', '</mark>', '…', 18) AS snippet, m.created_at
                FROM messages_fts f JOIN sessions s ON s.id = f.session_id JOIN messages m ON m.id = f.message_id
                WHERE {byWorkspace}s.parent_session_id IS NULL AND {visible} AND messages_fts MATCH $q ORDER BY m.created_at DESC LIMIT $n
                """, MapHit, [.. args, ("$q", phrase)]);
        }
        catch (SqliteException)
        {
            // FTS 对极端 Unicode / 旧数据库失败时,退回 LIKE,搜索仍可用。
            return database.Rows($"""
                SELECT m.session_id, m.id, s.workspace_id, s.title, substr(m.parts, 1, 240) AS snippet, m.created_at
                FROM messages m JOIN sessions s ON s.id = m.session_id
                WHERE {byWorkspace}s.parent_session_id IS NULL AND {visible} AND m.parts LIKE $q ORDER BY m.created_at DESC LIMIT $n
                """, MapHit, [.. args, ("$q", $"%{q}%")]);
        }
    }

    /// <summary>为上下文窗口提供当前会话范围的历史检索(2.x 的 <c>searchSessionHistory</c>),复用 messages_fts;非本作用域的会话 0 条。</summary>
    public IReadOnlyList<ContextSearchHit> SearchSession(string sessionId, string query, int limit = 5)
    {
        var q = query.Trim();
        if (q == "" || !ScopeVisible(sessionId))
        {
            return [];
        }

        var n = Math.Clamp(limit, 1, 10);
        try
        {
            return database.Rows("""
                SELECT f.message_id, m.role, m.created_at, snippet(messages_fts, 3, '', '', '…', 10) AS snippet
                FROM messages_fts f JOIN messages m ON m.id = f.message_id
                WHERE f.session_id = $s AND messages_fts MATCH $q ORDER BY m.created_at DESC LIMIT $n
                """, MapContextHit, ("$s", sessionId), ("$q", $"\"{q.Replace("\"", "\"\"")}\""), ("$n", n));
        }
        catch (SqliteException)
        {
            return database.Rows("""
                SELECT id, role, created_at, substr(parts, 1, 600) AS snippet FROM messages
                WHERE session_id = $s AND parts LIKE $q ORDER BY created_at DESC LIMIT $n
                """, MapContextHit, ("$s", sessionId), ("$q", $"%{q}%"), ("$n", n));
        }
    }

    private static SearchHit MapHit(SqliteDataReader r) => new(r.GetString(0), r.GetString(2), r.GetString(1), r.GetString(3), r.Text(4) ?? "", r.GetInt64(5));

    private static ContextSearchHit MapContextHit(SqliteDataReader r)
    {
        var snippet = r.Text(3) ?? "";
        return new ContextSearchHit(r.GetString(0), r.GetString(1), r.GetInt64(2), snippet.Length > 600 ? snippet[..600] : snippet);
    }

    // ── 会话详情与用量读数 ─────────────────────────────────────────────────────

    /// <summary>整条会话:元数据 + 全部消息 + Run 归属 + 逐轮用量与模型(2.x 的 <c>getSessionDetail</c>)。</summary>
    public SessionDetailRecord? Detail(string sessionId) => database.InTransaction(() =>
        Get(sessionId) is { } session
            ? new SessionDetailRecord(session, History(sessionId), false, MessageRuns(sessionId), RunUsage(sessionId), RunModel(sessionId))
            : null);

    /// <summary>
    /// 转录的一页(2.x 的 <c>getSessionPage</c>):打开会话、往上翻页都走它,不一次读整段历史。
    /// <c>MessageRuns</c> 只覆盖本页的消息;<c>RunUsage</c> / <c>RunModel</c> 是整段会话的汇总(很小)。
    /// </summary>
    public SessionDetailRecord? Page(string sessionId, int limit, string? beforeMessageId = null) => database.InTransaction(() =>
    {
        if (Get(sessionId) is not { } session)
        {
            return null;
        }

        var page = HistoryPage(sessionId, limit, beforeMessageId);
        return new SessionDetailRecord(session, page.Messages, page.HasMore,
            MessageRuns(sessionId, page.Messages.Select(m => m.Id).ToArray()), RunUsage(sessionId), RunModel(sessionId));
    });

    /// <summary>
    /// 一条会话里每个 Run 的累计用量,直接从落盘的 <c>usage_records</c> 聚合(2.x 的 <c>runUsageOf</c>)。
    /// ★ 失败的尝试也算进 token 与花费(它照样把 prompt 发上去了、照样计了费,排除掉界面就比账单小);
    /// ★★ 只有耗时(<c>UpstreamMs</c>)把失败尝试排除在外 —— token 回答「这一轮花了多少钱」,耗时回答「模型输出有多快」(平均 TPS 的分母)。
    /// ★ 花费只在算得齐的时候才给:有一次尝试 <c>cost_micros IS NULL</c>(不在价目表)或币种多于一种,就是 <c>Cost = null</c>,
    /// 而不是让 SUM() 安静地把 NULL 当 0 给出一个偏低的总额。
    /// </summary>
    public IReadOnlyDictionary<string, RunUsageRecord> RunUsage(string sessionId) => database.Rows($"""
        SELECT u.run_id,
               SUM(u.input_tokens), SUM(u.output_tokens), SUM(u.cache_read_tokens), SUM(u.cache_write_tokens), SUM(u.cache_write_1h_tokens),
               SUM(COALESCE(u.thinking_tokens, 0)),
               SUM(CASE WHEN u.ok = 1 THEN u.latency_ms ELSE 0 END),
               SUM(u.cost_micros), COUNT(u.cost_micros), COUNT(*), COUNT(DISTINCT u.currency), MIN(u.currency)
          FROM usage_records u
         WHERE u.session_id = $s AND EXISTS (SELECT 1 FROM sessions s WHERE s.id = u.session_id AND {ConfigScope.WorkspaceVisible("s.workspace_id")})
         GROUP BY u.run_id
        """, r =>
        {
            var reasoning = r.Int(6);
            var upstreamMs = r.Int(7);
            var currency = r.Text(12);
            var cost = r.Int(9) == r.Int(10) && r.Int(11) == 1 && currency is "USD" or "CNY" ? new RunCost(r.Int(8), currency!) : null;
            return (RunId: r.GetString(0), Usage: new RunUsageRecord(r.Int(1), r.Int(2), r.Int(3), r.Int(4), r.Int(5),
                reasoning > 0 ? reasoning : null,
                // 0 = 这一轮没有一次成功的请求:留 null 让展示层不画 TPS,而不是让它去除以零。
                upstreamMs > 0 ? upstreamMs : null, cost));
        }, ("$s", sessionId), Scope()).ToDictionary(x => x.RunId, x => x.Usage);

    /// <summary>
    /// 一条会话里每个 Run 发送时选中的模型别名(<c>usage_records.alias</c>,2.x 的 <c>runModelOf</c>)。
    /// 同一个 Run 的多次尝试(重试 / 故障切换)共享同一个别名,<c>MIN()</c> 只是凑聚合语法。这是发送时选的别名,不是回包里的真实上游模型名。
    /// </summary>
    public IReadOnlyDictionary<string, string> RunModel(string sessionId) => database.Rows($"""
        SELECT u.run_id, MIN(u.alias) FROM usage_records u
         WHERE u.session_id = $s AND EXISTS (SELECT 1 FROM sessions s WHERE s.id = u.session_id AND {ConfigScope.WorkspaceVisible("s.workspace_id")})
         GROUP BY u.run_id
        """, r => (r.GetString(0), r.GetString(1)), ("$s", sessionId), Scope()).ToDictionary(x => x.Item1, x => x.Item2);

    // ── 孤儿清理 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 全库孤儿清理(维护用,不按作用域:孤儿按定义没有归属)。2.x 只有「按会话」的 FTS 孤儿清理(在 <see cref="ReplaceHistory"/> 里),
    /// 3.0 在此基础上给一个全库版本,启动或「整理存储」时调用:
    /// ① 映射指向已不存在的消息的全文索引行;② 会话行已不存在、且存档已过期 / 结构对不上的 <c>session.input.*</c>
    /// (草稿可以早于会话行存在,所以不过期的不动)。
    /// </summary>
    public OrphanSweepResult SweepOrphans() => database.InTransaction(() =>
    {
        var fts = MessageFts.DeleteAllOrphans(database.Connection, database.CurrentTransaction);
        var now = Now();
        var stale = database.Rows("""
            SELECT k.key, k.json FROM kv k
            WHERE k.key LIKE 'session.input.%' AND NOT EXISTS (SELECT 1 FROM sessions s WHERE s.id = substr(k.key, 15))
            """, r => (Key: r.GetString(0), Json: r.GetString(1)))
            .Where(row => !SessionInputStore.TryParse(row.Json, now, out _))
            .ToList();
        foreach (var row in stale)
        {
            database.Run("DELETE FROM kv WHERE key = $k", ("$k", row.Key));
        }

        return new OrphanSweepResult(fts, stale.Count);
    });
}
