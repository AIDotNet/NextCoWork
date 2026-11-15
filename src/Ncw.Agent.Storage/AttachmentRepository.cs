using Microsoft.Data.Sqlite;
using Ncw.Core.Domain;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Storage;

/// <summary>
/// <c>attachments</c> 表的一行(2.x 迁移 4 / 5 / 6 / 7)。<see cref="Scope"/>:<c>session</c> / <c>theme</c> / <c>export</c>(只有 <c>session</c> 挂在会话上、被级联带走;其余无主)。
/// <see cref="Status"/>:<c>draft</c>(上传了还没发送,只有 <see cref="OwnerId"/>,没有 session / message)/ <c>committed</c>(随消息提交,<see cref="SessionId"/> / <see cref="MessageId"/> 填上)。
/// </summary>
public sealed record AttachmentRow(
    string Id,
    string Scope,
    string? OwnerId,
    string? SessionId,
    string? MessageId,
    string Path,
    long Size,
    string? Checksum,
    string Status,
    string? DisplayName,
    long CreatedAt);

/// <summary>
/// 附件登记(2.x <c>db/repo.ts</c> 的附件部分)。规则照搬:
/// <list type="bullet">
/// <item><b>草稿附件早于会话行存在</b>:上传发生在会话和消息都还不存在的时候,所以草稿只填 <c>owner_id</c>(无外键),消息提交时才填 <c>session_id</c> / <c>message_id</c>(那两列有外键)。</item>
/// <item><b>去重限定在同 scope + 同 owner</b>:跨会话共用一个物理文件,删一个会话就会波及另一个;要正确处理得上引用计数,省下的磁盘不值得那份正确性负担。</item>
/// <item><b>清理按 <c>(status, created_at)</c> 找超期草稿</b>;<c>themes/</c>、<c>exports/</c> 这类无主 scope 绝不当孤儿。</item>
/// <item>作用域:草稿看 <c>owner_id</c> 那个会话所属的工作区;会话行还不存在<b>不算</b>不可见(草稿恰恰在会话行落库之前上传),不然新对话里传的图既恢复不了也删不掉。</item>
/// </list>
/// </summary>
public sealed class AttachmentRepository(AgentDatabase database, TimeProvider? clock = null) : IMessageWriteObserver
{
    private const string Columns = "id, scope, owner_id, session_id, message_id, path, size, checksum, status, display_name, created_at";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private static AttachmentRow Map(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.Text(2), r.Text(3), r.Text(4), r.GetString(5), r.Int(6), r.Text(7), r.GetString(8), r.Text(9), r.GetInt64(10));

    /// <summary>某个会话归属的工作区当前作用域看不看得见;会话行还不存在 = 看得见(草稿)。</summary>
    public bool OwnerVisible(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return ConfigScope.Current(database) == ConfigScope.Local;
        }

        return database.Exists(
            $"SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM sessions WHERE id = $id) OR EXISTS (SELECT 1 FROM sessions s WHERE s.id = $id AND {ConfigScope.WorkspaceVisible("s.workspace_id")})",
            ("$id", sessionId), (ConfigScope.Param, ConfigScope.Current(database)));
    }

    private bool Visible(AttachmentRow row) => OwnerVisible(row.SessionId ?? row.OwnerId);

    public AttachmentRow? Get(string id) => database.Row($"SELECT {Columns} FROM attachments WHERE id = $id", Map, ("$id", id)) is { } row && Visible(row) ? row : null;

    /// <summary>全部附件行(清理 / 统计用,不收窄作用域:磁盘上的文件不属于任何账户)。</summary>
    public IReadOnlyList<AttachmentRow> All() => database.Rows($"SELECT {Columns} FROM attachments", Map);

    /// <summary>
    /// 某个会话下所有还没发出去的附件(重启后恢复草稿附件区)。会话存在但不在本作用域时返回空:草稿附件是按会话 id 直接查的,
    /// 它是唯一一条绕过会话归属判定的入口,少了这一句跨账户随便给一个会话 id 就能列出别人的待发附件。
    /// </summary>
    public IReadOnlyList<AttachmentRow> ListDrafts(string sessionId) =>
        OwnerVisible(sessionId) ? database.Rows($"SELECT {Columns} FROM attachments WHERE owner_id = $o AND status = 'draft' ORDER BY created_at", Map, ("$o", sessionId)) : [];

    /// <summary>某条消息提交的全部附件。</summary>
    public IReadOnlyList<AttachmentRow> ForMessage(string messageId) =>
        database.Rows($"SELECT {Columns} FROM attachments WHERE message_id = $m ORDER BY created_at, id", Map, ("$m", messageId));

    /// <summary>
    /// 同 scope + 同 owner + 同校验和的已有行(上传去重)。★ 按会话 owner 限定:跨会话去重会让删一个会话波及另一个。
    /// 取最近的一条;只认能用的状态(草稿或已提交)。
    /// </summary>
    public AttachmentRow? FindByChecksum(string checksum, string scope, string? ownerId) => database.Row($"""
        SELECT {Columns} FROM attachments WHERE checksum = $c AND scope = $s AND COALESCE(owner_id, '') = $o AND status IN ('draft', 'committed')
        ORDER BY created_at DESC, id DESC LIMIT 1
        """, Map, ("$c", checksum), ("$s", scope), ("$o", ownerId ?? ""));

    public AttachmentRow? FindByOwnerAndFileName(string ownerId, string fileName) =>
        database.Rows($"SELECT {Columns} FROM attachments WHERE scope = 'session' AND owner_id = $o AND status IN ('draft', 'committed') ORDER BY created_at DESC, id DESC", Map, ("$o", ownerId))
            .FirstOrDefault(r => System.IO.Path.GetFileName(r.Path) == fileName);

    /// <summary>登记一个刚落盘的草稿附件。</summary>
    public void PutDraft(AttachmentRow row) => database.Run("""
        INSERT INTO attachments (id, session_id, message_id, path, size, checksum, created_at, scope, status, owner_id, display_name)
        VALUES ($id, NULL, NULL, $path, $size, $sum, $at, $scope, 'draft', $owner, $name)
        """, ("$id", row.Id), ("$path", row.Path), ("$size", row.Size), ("$sum", row.Checksum), ("$at", row.CreatedAt), ("$scope", row.Scope), ("$owner", row.OwnerId), ("$name", row.DisplayName));

    public bool Remove(string id) => database.Run("DELETE FROM attachments WHERE id = $id", ("$id", id)) > 0;

    /// <summary>还有几行指向同一个物理路径(删文件之前问一句)。</summary>
    public long ReferenceCount(string path, string? excludeId = null) => excludeId is null
        ? database.Count("SELECT COUNT(*) FROM attachments WHERE path = $p", ("$p", path))
        : database.Count("SELECT COUNT(*) FROM attachments WHERE path = $p AND id <> $id", ("$p", path), ("$id", excludeId));

    /// <summary>超过宽限期的草稿(清理用)。未超期的草稿绝不动:用户挑好图去倒杯水,回来时图必须还在。</summary>
    public IReadOnlyList<AttachmentRow> StaleDrafts(long cutoff) =>
        database.Rows($"SELECT {Columns} FROM attachments WHERE status = 'draft' AND created_at < $c", Map, ("$c", cutoff));

    /// <summary>
    /// 指定会话(连同它派生的子代理转录)的会话附件,包括只用 owner 归属的草稿。★ 传进来的 id 原样保留(草稿可以早于会话行存在),子树只负责多认一些。
    /// 预览算字节、清理换算真实大小、删会话前记路径<b>必须</b>是同一个集合,否则预览的数字和实际删掉的量对不上,或者磁盘上留下永久孤儿文件。
    /// </summary>
    public IReadOnlyList<AttachmentRow> ForSessions(IReadOnlyCollection<string> sessionIds, SessionRepository sessions)
    {
        if (sessionIds.Count == 0)
        {
            return [];
        }

        var wanted = new HashSet<string>(sessionIds, StringComparer.Ordinal);
        foreach (var id in sessionIds)
        {
            foreach (var sub in sessions.SubtreeIds(id))
            {
                wanted.Add(sub);
            }
        }

        return [.. All().Where(r => r.Scope == "session" && ((r.SessionId is not null && wanted.Contains(r.SessionId)) || (r.OwnerId is not null && wanted.Contains(r.OwnerId))))];
    }

    // ───────────────────────── 提交(IMessageWriteObserver) ─────────────────────────

    /// <summary>
    /// 消息落盘的<b>同一个事务</b>里被调用(2.x 的 <c>recordMessageAttachments</c>):消息里引用了的草稿附件升为 committed 并填上 session / message
    /// (那一刻会话行必然已存在,级联从此生效);已经提交给别的消息的行不挪,为这条消息另建一行引用(<c>&lt;messageId&gt;:managed:&lt;n&gt;</c>,按路径引用计数)。
    /// 引用的写法是内容块里的 <c>ncw://attachments/sessions/&lt;会话&gt;/&lt;文件&gt;</c>;不是本会话 owner 的附件一律不认(不能靠消息把别的会话的附件认领过来)。
    /// </summary>
    public void OnMessageWritten(AgentDatabase db, SessionRecord session, MessageRecord message)
    {
        if (message.Role != "user")
        {
            return;
        }

        var refs = RefsOf(message.PartsJson);
        if (refs.Count > 0)
        {
            CommitByRefs(refs, message.Id, session.Id);
        }
    }

    /// <summary>
    /// 编辑后重新运行的截断(<see cref="IMessageWriteObserver.OnMessagesRemoving"/>):被移除消息的会话附件降回草稿(文件和 <c>ncw://</c> 引用都还在,
    /// 新消息提交时由 <see cref="CommitByRefs"/> 重新认领;不重发就按草稿的 7 天规则回收)。<c>&lt;messageId&gt;:managed:&lt;n&gt;</c> 那种「引用别的消息的附件」的行只是引用,直接删。
    /// </summary>
    public void OnMessagesRemoving(AgentDatabase db, SessionRecord session, IReadOnlyList<string> removedMessageIds)
    {
        foreach (var messageId in removedMessageIds)
        {
            foreach (var row in ForMessage(messageId).Where(r => r.Scope == "session" && r.Status == "committed" && r.OwnerId == session.Id).ToList())
            {
                if (row.Id.Contains(":managed:", StringComparison.Ordinal))
                {
                    Remove(row.Id);
                }
                else
                {
                    database.Run("UPDATE attachments SET status = 'draft', message_id = NULL, session_id = NULL WHERE id = $id AND scope = 'session' AND status = 'committed'", ("$id", row.Id));
                }
            }
        }
    }

    /// <summary>消息内容块里引用的会话附件(<c>image</c> / <c>file_ref</c> 块的 <c>dataRef</c> 是 <c>ncw://</c> URL),去重、保序。</summary>
    public static IReadOnlyList<AttachmentLocator> RefsOf(string partsJson)
    {
        var refs = new List<AttachmentLocator>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(partsJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return refs;
            }

            foreach (var part in doc.RootElement.EnumerateArray())
            {
                if (part.ValueKind == System.Text.Json.JsonValueKind.Object && part.TryGetProperty("dataRef", out var dataRef)
                    && dataRef.ValueKind == System.Text.Json.JsonValueKind.String && Attachments.ParseUrl(dataRef.GetString() ?? "") is { Scope: Attachments.Session, OwnerId: not null } locator
                    && !refs.Contains(locator))
                {
                    refs.Add(locator);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 坏 JSON 的消息不引用任何附件。
        }

        return refs;
    }

    public void CommitByRefs(IReadOnlyList<AttachmentLocator> refs, string messageId, string sessionId) => database.InTransaction(() =>
    {
        var current = ForMessage(messageId).ToList();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < refs.Count; index++)
        {
            if (refs[index].OwnerId != sessionId || FindByOwnerAndFileName(sessionId, refs[index].FileName) is not { Scope: "session" } row)
            {
                continue;
            }

            // 已经挂在这条消息上的(同一条消息被重放):原样保留。
            var existing = current.FirstOrDefault(c => !used.Contains(c.Id) && c.Path == row.Path);
            if (existing is not null)
            {
                used.Add(existing.Id);
                continue;
            }

            if (!used.Contains(row.Id) && row.Status == "draft" && row.MessageId is null && row.SessionId is null)
            {
                database.Run("""
                    UPDATE attachments SET status = 'committed', message_id = $m, session_id = $s, owner_id = $s
                    WHERE id = $id AND scope = 'session' AND owner_id = $s AND status = 'draft' AND message_id IS NULL AND session_id IS NULL
                    """, ("$m", messageId), ("$s", sessionId), ("$id", row.Id));
                used.Add(row.Id);
                continue;
            }

            // 已提交给别的消息:不挪,为这条消息另建一行引用(同 owner / message / 路径的已有引用幂等复用)。
            var baseId = $"{messageId}:managed:{index}";
            var referenceId = baseId;
            var suffix = 0;
            var reused = false;
            while (Get(referenceId) is { } clash)
            {
                if (clash.Scope == "session" && clash.OwnerId == sessionId && clash.MessageId == messageId && clash.Path == row.Path)
                {
                    used.Add(referenceId);
                    reused = true;
                    break;
                }

                suffix++;
                referenceId = $"{baseId}:{row.Id}:{suffix}";
            }

            if (reused)
            {
                continue;
            }

            database.Run("""
                INSERT INTO attachments (id, session_id, message_id, path, size, checksum, scope, status, owner_id, display_name, created_at)
                VALUES ($id, $s, $m, $path, $size, $sum, 'session', 'committed', $s, $name, $at)
                """, ("$id", referenceId), ("$s", sessionId), ("$m", messageId), ("$path", row.Path), ("$size", row.Size), ("$sum", row.Checksum), ("$name", row.DisplayName), ("$at", row.CreatedAt));
            used.Add(referenceId);
        }
    });

    public long Now() => _clock.GetUtcNow().ToUnixTimeMilliseconds();
}
