using System.Text.Json;
using Microsoft.Data.Sqlite;
using Ncw.Core.Ids;

namespace Ncw.Agent.Storage.Sessions;

/// <summary>
/// 消息写入的旁路观察者:在消息落盘的**同一个事务**里被调用(消息行与全文索引已写完)。
/// 附件登记(2.x 的 <c>recordMessageAttachments</c>:draft 升 committed、清掉不再引用的行)是附件域的事,
/// 由附件仓库实现这个接口接进来;会话仓库本身不碰 <c>attachments</c> 表的登记。
/// </summary>
public interface IMessageWriteObserver
{
    void OnMessageWritten(AgentDatabase database, SessionRecord session, MessageRecord message);

    /// <summary>
    /// 改写历史时,<b>在删消息之前</b>被调用(同一个事务),只在调用方要求保留附件时(<c>keepAttachmentsOfRemoved</c>:编辑后重新运行的截断)。
    /// 消息删除会级联删掉它的附件行,旧的 <c>ncw://</c> 引用立刻失效;而重新运行要用同样的附件重发,所以附件域在这里把它们降回草稿(文件留着,新消息提交时重新认领)。
    /// 默认什么都不做(删除一轮 / 一条回复不需要:被删的图不该回到输入框)。
    /// </summary>
    void OnMessagesRemoving(AgentDatabase database, SessionRecord session, IReadOnlyList<string> removedMessageIds)
    {
    }
}

/// <summary>
/// 2.x 的会话与消息仓库(<c>db/repo.ts</c> 的 sessions / messages / runs 部分 + <c>ipc/sessions.ts</c>、
/// <c>services/session-metadata.ts</c> 的存储动作)。规则照搬 2.x:
/// <list type="bullet">
/// <item>会话没有自己的账户列,归属取决于它挂的那个工作区(<c>workspaces.owner</c>):同一个事实不存两处。
/// 所有读都把「当前作用域看得见」写进 SQL;看不见的会话当作不存在(返回 null / 空 / false,而不是一条「存在但无权限」的信息泄露)。
/// 写入撞上别的作用域(目标工作区或已有的同 id 会话属于别的账户)时抛 <see cref="StorageException"/>,不顶替、不静默跳过。</item>
/// <item>子代理转录(<c>parent_session_id</c> 非空)在库里、不在任何面向用户的枚举里:列表、搜索、最近指令都带 <c>parent_session_id IS NULL</c>。</item>
/// <item>消息只在完成时写;同一 id 重放幂等;<c>run_id</c> 重写时 COALESCE;用户消息的消费回执与消息在同一个事务里。</item>
/// <item>时间由注入的 <see cref="TimeProvider"/> 给,便于测试。</item>
/// </list>
/// 单进程单连接,所有访问经 <see cref="AgentDatabase.Gate"/> 串行;多语句操作经 <see cref="AgentDatabase.InTransaction{T}"/>。
/// </summary>
public sealed partial class SessionRepository(AgentDatabase database, TimeProvider? clock = null, IMessageWriteObserver? observer = null)
{
    /// <summary>
    /// 2.x 存进库里的默认标题占位(领域数据,不是界面文字:界面按 <c>titleSource == "default"</c> 或
    /// <see cref="IsDefaultTitle"/> 显示本地化的名字)。沿用它,2.x 读 3.0 写的库时才认得出「还没起名」。
    /// </summary>
    public const string DefaultTitle = "新对话";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>2.x 的 <c>isDefaultSessionTitle</c>:历史上的占位值都是数据。</summary>
    public static bool IsDefaultTitle(string title) => title.Trim() is "" or "新对话" or "New conversation" or "New chat";

    /// <summary>2.x 的 <c>normalizeModeId</c>:读旧值,不保留旧模式本身。</summary>
    public static string NormalizeMode(string? value)
    {
        if (value is null)
        {
            return "code";
        }

        var id = value.Trim().ToLowerInvariant();
        if (id is "normal" or "goal")
        {
            return "code";
        }

        return IsModeId(id) ? id : "code";
    }

    /// <summary>权限档位归一:<c>ask</c> / <c>auto</c> / <c>full</c>,其余一律按默认 <c>ask</c>。</summary>
    public static string NormalizePermissionMode(string? value) =>
        value?.Trim().ToLowerInvariant() is "auto" ? "auto" : value?.Trim().ToLowerInvariant() is "full" ? "full" : "ask";

    private static bool IsModeId(string id)
    {
        // ^[a-z0-9][a-z0-9-]{0,63}$
        if (id.Length is 0 or > 64 || !IsLowerAlnum(id[0]))
        {
            return false;
        }

        return id.All(c => IsLowerAlnum(c) || c == '-');

        static bool IsLowerAlnum(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';
    }

    // ── 会话 ──────────────────────────────────────────────────────────────────

    private const string SessionColumns =
        "s.id, s.workspace_id, s.parent_session_id, s.title, s.model, s.mode, s.thinking, s.root_path_at_creation, s.archived, s.favorited, s.created_at, s.updated_at, s.json";

    private static readonly string VisibleSession = ConfigScope.WorkspaceVisible("s.workspace_id");

    private (string, object?) Scope() => (ConfigScope.Param, ConfigScope.Current(database));

    /// <summary>创建或补齐一条会话(2.x 的 <c>createSession</c>:同 id 已存在就整行覆盖,运行链路和界面都可安全调用)。</summary>
    public SessionRecord Create(SessionCreate input)
    {
        var now = input.CreatedAt ?? _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var title = input.Title?.Trim() is { Length: > 0 } t ? t : DefaultTitle;
        var session = new SessionRecord(
            Id: input.Id ?? Ulid.New(new FixedClock(now)),
            WorkspaceId: input.WorkspaceId,
            ParentSessionId: input.ParentSessionId,
            Origin: input.Origin == "scheduled" ? "scheduled" : "chat",
            Title: title,
            TitleSource: IsDefaultTitle(input.Title ?? "") ? "default" : "manual",
            Model: input.Model ?? "",
            ModelProviderId: input.ModelProviderId,
            Mode: NormalizeMode(input.Mode),
            Thinking: input.Thinking ?? "auto",
            RootPathAtCreation: input.RootPathAtCreation ?? "",
            SkillSourceSessionId: input.SkillSourceSessionId,
            Archived: false,
            Favorited: false,
            CreatedAt: now,
            UpdatedAt: now,
            PermissionMode: NormalizePermissionMode(input.PermissionMode));
        return Put(session);
    }

    /// <summary>
    /// 不存在时创建,存在时返回现有记录(界面先生成 id 的 Tab 用)。
    /// ★ 唯一一处「命中已有行还要写」的例外:这一行是在加上 <c>parent_session_id</c> 之前建的,而它决定这条转录进不进侧边栏;
    /// 不补的话,一条续跑的子代理会话会永远留在列表里。其余字段照旧不覆盖 —— 用户改过的标题归用户。
    /// </summary>
    public SessionRecord Ensure(SessionCreate input) => database.InTransaction(() =>
    {
        if (input.Id is not null && Get(input.Id) is { } existing)
        {
            return input.ParentSessionId is not null && existing.ParentSessionId is null
                ? Put(existing with { ParentSessionId = input.ParentSessionId })
                : existing;
        }

        return Create(input);
    });

    /// <summary>当前作用域看得见这条会话吗(2.x 的 <c>sessionScopeVisible</c>)。</summary>
    public bool ScopeVisible(string id) => Get(id) is not null;

    /// <summary>目标会话是否还在(2.x 的 <c>sessionExists</c>;3.0 按作用域收窄,别的账户的会话当作不在)。</summary>
    public bool Exists(string id) => ScopeVisible(id);

    public SessionRecord? Get(string id) => database.Row(
        $"SELECT {SessionColumns} FROM sessions s WHERE s.id = $id AND {VisibleSession}", MapSession, ("$id", id), Scope());

    /// <summary>
    /// 写一条会话(2.x 的 <c>putSession</c>)。保证:状态永远 idle;标题 trim、空则回默认;模式归一;
    /// ★ 自环的 <c>parent_session_id</c> 丢弃(会让子树递归原地打转);标题变了就同步全文索引里冗余的标题列;
    /// json 里 3.0 不认识的字段原样保留。
    /// </summary>
    public SessionRecord Put(SessionRecord session) => database.InTransaction(() =>
    {
        var normalized = session with
        {
            ParentSessionId = session.ParentSessionId == session.Id ? null : session.ParentSessionId,
            Title = session.Title.Trim() is { Length: > 0 } title ? title : DefaultTitle,
            Mode = NormalizeMode(session.Mode),
            PermissionMode = NormalizePermissionMode(session.PermissionMode),
        };

        var previous = database.Row("SELECT workspace_id, title, json FROM sessions WHERE id = $id",
            r => new PreviousRow(r.GetString(0), r.GetString(1), r.Text(2)), ("$id", normalized.Id));
        if (previous is not null)
        {
            RequireWorkspaceVisible(previous.WorkspaceId, $"Session {normalized.Id} belongs to another account scope.");
        }

        RequireWorkspaceVisible(normalized.WorkspaceId, $"Workspace {normalized.WorkspaceId} is not available in the current account scope.");

        var json = new SessionJson
        {
            Id = normalized.Id,
            WorkspaceId = normalized.WorkspaceId,
            ParentSessionId = normalized.ParentSessionId,
            Origin = normalized.Origin,
            Title = normalized.Title,
            TitleSource = normalized.TitleSource,
            Model = normalized.Model,
            ModelProviderId = normalized.ModelProviderId,
            Mode = normalized.Mode,
            Thinking = normalized.Thinking,
            RootPathAtCreation = normalized.RootPathAtCreation,
            SkillSource = normalized.SkillSourceSessionId is null ? null : new SkillSourceJson { SessionId = normalized.SkillSourceSessionId },
            Status = "idle",
            Archived = normalized.Archived,
            Favorited = normalized.Favorited,
            CreatedAt = normalized.CreatedAt,
            UpdatedAt = normalized.UpdatedAt,
            PermissionMode = normalized.PermissionMode == "ask" ? null : normalized.PermissionMode,
            Extra = ParseJson(previous?.Json)?.Extra,
        };

        database.Run("""
            INSERT INTO sessions
              (id, workspace_id, parent_session_id, title, model, mode, thinking, root_path_at_creation, status,
               archived, favorited, created_at, updated_at, json)
            VALUES ($id, $w, $parent, $title, $model, $mode, $thinking, $root, 'idle', $archived, $favorited, $created, $updated, $json)
            ON CONFLICT (id) DO UPDATE SET
              workspace_id = excluded.workspace_id,
              parent_session_id = excluded.parent_session_id,
              title = excluded.title,
              model = excluded.model,
              mode = excluded.mode,
              thinking = excluded.thinking,
              root_path_at_creation = excluded.root_path_at_creation,
              status = 'idle',
              archived = excluded.archived,
              favorited = excluded.favorited,
              created_at = excluded.created_at,
              updated_at = excluded.updated_at,
              json = excluded.json
            """,
            ("$id", normalized.Id), ("$w", normalized.WorkspaceId), ("$parent", normalized.ParentSessionId), ("$title", normalized.Title),
            ("$model", normalized.Model), ("$mode", normalized.Mode), ("$thinking", normalized.Thinking), ("$root", normalized.RootPathAtCreation),
            ("$archived", normalized.Archived ? 1 : 0), ("$favorited", normalized.Favorited ? 1 : 0),
            ("$created", normalized.CreatedAt), ("$updated", normalized.UpdatedAt),
            ("$json", JsonSerializer.Serialize(json, StorageJsonContext.Relaxed.SessionJson)));

        // 消息正文在 FTS5 里单独索引;改名(或导入)时同步冗余的标题列,
        // 否则按会话标题搜索会一直返回旧标题,直到下一条消息提交。
        if (previous is not null && previous.Title != normalized.Title)
        {
            MessageFts.RenameSession(database.Connection, database.CurrentTransaction, normalized.Id, normalized.Title);
        }

        return normalized;
    });

    /// <summary>
    /// 侧边栏的那张列表(顶层、非定时任务产生的会话)。看不见的工作区就是空列表。
    /// ★ <c>parent_session_id IS NULL</c> 是子代理转录的过滤口。
    /// </summary>
    public IReadOnlyList<SessionListItem> List(string workspaceId, bool? archived = null)
    {
        var sql = $"""
            SELECT s.id, s.title, s.updated_at, s.archived, s.favorited, s.json,
                   NOT EXISTS (SELECT 1 FROM messages m WHERE m.session_id = s.id)
            FROM sessions s
            WHERE s.workspace_id = $w AND s.parent_session_id IS NULL {(archived is null ? "" : "AND s.archived = $a")}
              AND {ConfigScope.WorkspaceVisible("s.workspace_id")}
            ORDER BY s.updated_at DESC, s.id DESC
            """;
        var args = new List<(string, object?)> { ("$w", workspaceId), Scope() };
        if (archived is { } a)
        {
            args.Add(("$a", a ? 1 : 0));
        }

        return [.. database.Rows(sql, r =>
        {
            var title = r.GetString(1);
            var isArchived = r.Int(3) != 0;
            var parsed = ParseJson(r.Text(5));
            var hasNoMessages = r.Int(6) != 0;
            var isDefaultTitle = parsed?.TitleSource == "default" || (parsed?.TitleSource is null && IsDefaultTitle(title));
            return (Origin: parsed?.Origin, Item: new SessionListItem(
                r.GetString(0), title, r.Int(2), isArchived, r.Int(4) != 0, Running: false,
                SkillExtraction: parsed?.SkillSource?.SessionId is not null,
                IsEmpty: hasNoMessages && !isArchived && isDefaultTitle));
        }, [.. args]).Where(x => x.Origin != "scheduled").Select(x => x.Item)];
    }

    /// <summary>
    /// 库里当前作用域看得见的全部会话,子代理转录也算(<paramref name="topLevelOnly"/> = false)。
    /// ★ 备份 manifest 用「全部」:校验端拿裸 <c>COUNT(*)</c> 对账,在它上面加过滤会让每个新备份都恢复不了;
    /// 数据导出给人看的那份用 topLevelOnly。
    /// </summary>
    public IReadOnlyList<SessionRecord> ListAll(bool topLevelOnly) => database.Rows(
        $"SELECT {SessionColumns} FROM sessions s WHERE {(topLevelOnly ? "s.parent_session_id IS NULL AND " : "")}{VisibleSession} ORDER BY s.updated_at DESC, s.id DESC",
        MapSession, Scope());

    /// <summary>会话元数据 + 消息条数,不读任何消息正文。</summary>
    public SessionSummary? Summary(string sessionId) => database.InTransaction(() =>
        Get(sessionId) is { } session ? new SessionSummary(session, CountMessages(sessionId)) : null);

    /// <summary>用户改名:标题 trim、空则回默认,标题来源记为 manual,更新时间前移。会话不存在抛异常(2.x 同)。</summary>
    public void Rename(string id, string title) => database.InTransaction(() =>
        Put(Require(id) with
        {
            Title = title.Trim() is { Length: > 0 } t ? t : DefaultTitle,
            TitleSource = "manual",
            UpdatedAt = Now(),
        }));

    /// <summary>
    /// 自动标题的第一步(2.x <c>SessionTitleGenerator.start</c> 里的预览写入):仍是「还没起名」(来源 default,或没记来源且标题是占位)的会话,
    /// 标题换成 <paramref name="preview"/>、来源记为 generated。已经是 manual / generated 的返回 null(不覆盖用户起的名字,也不重复生成)。
    /// <b>不动 <c>UpdatedAt</c></b>:自动起名不该把会话顶到侧边栏最上面。
    /// </summary>
    public SessionRecord? BeginGeneratedTitle(string id, string preview) => database.InTransaction(() =>
    {
        if (Get(id) is not { } current || !(current.TitleSource == "default" || (current.TitleSource is null && IsDefaultTitle(current.Title))))
        {
            return null;
        }

        return Put(current with { Title = preview.Trim() is { Length: > 0 } t ? t : current.Title, TitleSource = "generated" });
    });

    /// <summary>
    /// 自动标题的第二步(模型的回答迟到之后):只有会话<b>仍是</b>第一步写下的那一条(来源 generated、标题 = 预览、创建时间与工作区没变)才写。
    /// 期间被手动改名 / 被删除 / 删了又以同 id 重建的,一律返回 null,让更新的事实赢。其余元数据(归档、收藏、模型、更新时间)原样保留。
    /// </summary>
    public SessionRecord? FinishGeneratedTitle(SessionRecord preview, string title) => database.InTransaction(() =>
    {
        if (Get(preview.Id) is not { } current
            || current.TitleSource != "generated" || current.Title != preview.Title
            || current.CreatedAt != preview.CreatedAt || current.WorkspaceId != preview.WorkspaceId)
        {
            return null;
        }

        return Put(current with { Title = title });
    });

    public void SetArchived(string id, bool archived) => database.InTransaction(() => Put(Require(id) with { Archived = archived, UpdatedAt = Now() }));

    public void SetFavorited(string id, bool favorited) => database.InTransaction(() => Put(Require(id) with { Favorited = favorited, UpdatedAt = Now() }));

    /// <summary>改模式。会话不存在或模式没变则什么都不做并返回 false(2.x 同)。</summary>
    public bool SetMode(string id, string mode) => database.InTransaction(() =>
    {
        if (Get(id) is not { } session)
        {
            return false;
        }

        var normalized = NormalizeMode(mode);
        if (session.Mode == normalized)
        {
            return false;
        }

        Put(session with { Mode = normalized, UpdatedAt = Now() });
        return true;
    });

    /// <summary>
    /// 记住这条会话选中的模型。★ 不动更新时间:换个模型不是「这条对话有了新进展」,碰它会让侧边栏的最近顺序因一次纯界面操作而跳动。
    /// ★ <paramref name="modelProviderId"/> 无条件写(包括 null):别名换了而供应商没跟着换,留下的是「新别名 + 旧供应商」这个谁也没配过的组合。
    /// </summary>
    public bool SetModel(string id, string model, string? modelProviderId) => database.InTransaction(() =>
    {
        if (Get(id) is not { } session || (session.Model == model && session.ModelProviderId == modelProviderId))
        {
            return false;
        }

        Put(session with { Model = model, ModelProviderId = modelProviderId });
        return true;
    });

    /// <summary>
    /// 记住这条会话选中的权限档位(<c>ask</c> / <c>auto</c> / <c>full</c>)。和 <see cref="SetModel"/> 一样不动更新时间:
    /// 在输入框切换权限档位不是对话内容更新,不该让侧边栏的会话顺序跳动。
    /// </summary>
    public bool SetPermissionMode(string id, string permissionMode) => database.InTransaction(() =>
    {
        var normalized = NormalizePermissionMode(permissionMode);
        if (Get(id) is not { } session || session.PermissionMode == normalized)
        {
            return false;
        }

        Put(session with { PermissionMode = normalized });
        return true;
    });

    /// <summary>
    /// 一条会话 + 它派生出来的全部子代理转录(任意深度),root 在前。
    /// ★ <c>UNION</c> 而不是 <c>UNION ALL</c>:畸形数据造出的环会被去重终止。每一层都按作用域收窄。
    /// </summary>
    public IReadOnlyList<string> SubtreeIds(string rootId) => database.Rows($"""
        WITH RECURSIVE subtree(id) AS (
          SELECT s.id FROM sessions s WHERE s.id = $root AND {VisibleSession}
          UNION
          SELECT c.id FROM sessions c JOIN subtree t ON c.parent_session_id = t.id
            WHERE {ConfigScope.WorkspaceVisible("c.workspace_id")}
        )
        SELECT id FROM subtree
        """, r => r.GetString(0), ("$root", rootId), Scope());

    /// <summary>
    /// 删一条会话,连同它派生出来的全部子代理转录。返回真正删掉的 id(看不见的 / 不存在的是空列表)。
    /// 级联写在仓库里:按时长清理、清空历史都经过它,子转录在过滤之后是不朽的(没有任何枚举看得见它)。
    /// 附件文件在磁盘上的清理由调用方在删之前用 <see cref="AttachmentPaths"/> 记路径、删之后做。
    /// </summary>
    public IReadOnlyList<string> Delete(string id) => database.InTransaction(() =>
    {
        var ids = SubtreeIds(id);
        var now = Now();
        // 先叶后根。sessions 之间没有真外键;保持和 CASCADE 同向。
        foreach (var sid in Enumerable.Reverse(ids))
        {
            MessageFts.DeleteSession(database.Connection, database.CurrentTransaction, sid);
            database.Run("DELETE FROM sessions WHERE id = $id", ("$id", sid));
            // 草稿附件没有 session_id(外键要求上传时会话可以尚未创建),不能只依赖 CASCADE;owner_id 是它们的会话归属。
            database.Run("DELETE FROM attachments WHERE scope = 'session' AND owner_id = $id", ("$id", sid));
            // kv 没有外键,得手删。
            database.Run("DELETE FROM kv WHERE key IN ($input, $widget)", ("$input", SessionInputStore.Key(sid)), ("$widget", $"session.widget.{sid}"));
            // ★ 导入来的会话留墓碑,不删映射行:删掉的话,下一轮自动同步看不到任何记录,会把用户明确删掉的会话原地复活。
            database.Run("UPDATE import_mappings SET sync_state = 'suppressed', target_id = '', updated_at = $t WHERE target_id = $id AND entity_kind = 'session'",
                ("$t", now), ("$id", sid));
        }

        return ids;
    });

    /// <summary>
    /// 一条会话(含子代理转录)的附件文件路径;包括尚未提交、只用 <c>owner_id</c> 归属的草稿(2.x 的 <c>sessionAttachmentPaths</c>)。
    /// 删除会话之前先取,因为级联会带走这些行。传进来的 id 原样保留:草稿附件可以早于 sessions 行存在。
    /// </summary>
    public IReadOnlyList<string> AttachmentPaths(string sessionId) => database.InTransaction(() =>
    {
        // 会话行存在但属于别的作用域:不给。不存在:草稿归属,照给。
        if (database.Exists("SELECT 1 FROM sessions WHERE id = $id", ("$id", sessionId)) && Get(sessionId) is null)
        {
            return (IReadOnlyList<string>)[];
        }

        var wanted = new HashSet<string>(SubtreeIds(sessionId)) { sessionId };
        return database.Rows("""
            SELECT path FROM attachments WHERE scope = 'session'
              AND (session_id IN (SELECT value FROM json_each($ids)) OR owner_id IN (SELECT value FROM json_each($ids)))
            """, r => r.GetString(0), ("$ids", JsonSerializer.Serialize(wanted.ToArray(), StorageJsonContext.Default.StringArray)));
    });

    /// <summary>手动编辑转录 = 永久脱离导入来源(只有内容修改算,改名/归档/收藏不算)。返回被改动的映射行数。</summary>
    public int DetachImported(string sessionId) => database.Run(
        "UPDATE import_mappings SET sync_state = 'detached', updated_at = $t WHERE target_id = $id AND entity_kind = 'session' AND sync_state <> 'detached'",
        ("$t", Now()), ("$id", sessionId));

    // ── 内部 ──────────────────────────────────────────────────────────────────

    private long Now() => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private SessionRecord Require(string id) => Get(id) ?? throw new StorageException($"Session not found: {id}");

    private void RequireWorkspaceVisible(string workspaceId, string message)
    {
        if (!database.Exists($"SELECT 1 WHERE {ConfigScope.WorkspaceVisible("$w")}", ("$w", workspaceId), Scope()))
        {
            throw new StorageException(message);
        }
    }

    private static SessionJson? ParseJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, StorageJsonContext.Default.SessionJson);
        }
        catch (JsonException)
        {
            // 旧/损坏的 json 仍可由提列字段恢复,避免整个侧边栏消失。
            return null;
        }
    }

    private static SessionRecord MapSession(SqliteDataReader r)
    {
        var parsed = ParseJson(r.Text(12));
        return new SessionRecord(
            Id: r.GetString(0),
            WorkspaceId: r.GetString(1),
            // ★ 只认真列,不走 json 兜底:列是过滤和级联唯一读的地方,不一致时必须以列为准,
            // 否则一条 json 损坏的行会重新出现在侧边栏里。
            ParentSessionId: r.Text(2),
            Origin: parsed?.Origin == "scheduled" ? "scheduled" : "chat",
            Title: r.Text(3) ?? parsed?.Title ?? DefaultTitle,
            TitleSource: parsed?.TitleSource is "default" or "generated" or "manual" ? parsed.TitleSource : null,
            Model: r.Text(4) ?? parsed?.Model ?? "",
            // 只活在 json 里的字段:严格收窄,形状不对就当没有。
            ModelProviderId: parsed?.ModelProviderId,
            Mode: NormalizeMode(r.Text(5) ?? parsed?.Mode),
            Thinking: r.Text(6) ?? parsed?.Thinking ?? "auto",
            RootPathAtCreation: r.Text(7) ?? parsed?.RootPathAtCreation ?? "",
            SkillSourceSessionId: parsed?.SkillSource?.SessionId,
            Archived: (r.IsDBNull(8) ? (parsed?.Archived == true ? 1 : 0) : r.GetInt64(8)) != 0,
            Favorited: (r.IsDBNull(9) ? (parsed?.Favorited == true ? 1 : 0) : r.GetInt64(9)) != 0,
            CreatedAt: r.IsDBNull(10) ? parsed?.CreatedAt ?? 0 : r.GetInt64(10),
            UpdatedAt: r.IsDBNull(11) ? parsed?.UpdatedAt ?? 0 : r.GetInt64(11),
            PermissionMode: NormalizePermissionMode(parsed?.PermissionMode));
    }

    private sealed record PreviousRow(string WorkspaceId, string Title, string? Json);

    /// <summary>固定时间的时钟:用消息自己的时间戳铸 ULID(分支复制出来的消息 id 与 2.x 的 <c>ulid(message.createdAt)</c> 同形)。</summary>
    private sealed class FixedClock(long unixMs) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }
}
