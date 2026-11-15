using Ncw.Core.Domain;
using Ncw.Agent.Storage.Sessions;

namespace Ncw.Agent.Storage;

/// <summary>库里的计数(设置 › 数据页的「本机有几条对话」)。<see cref="Conversations"/> 数<b>顶层</b>会话(子代理转录不是一条对话);<see cref="MessageBytes"/> 是整库消息正文的 UTF-8 字节数(含子代理转录:它们占的字节照实计入)。</summary>
public sealed record StorageCounts(long Conversations, long Messages, long MessageBytes, long Attachments, long Workspaces, long UsageRecords);

/// <summary>一次清理的预览 / 结果的计数部分。<see cref="SessionCount"/> 数顶层(确认框里那句「将删除 N 条对话」),<see cref="MessageCount"/> / <see cref="Bytes"/> 数整棵子树(真正会消失的字节)。</summary>
public sealed record CleanupCounts(long SessionCount, long MessageCount, long AttachmentCount, long Bytes);

/// <summary>
/// 存储维护的 SQL(设置 › 数据页):统计、按年龄 / 全部清理历史、整理。「Storage 子代理留下的 <c>storage.*</c> 缺口」:<c>sessionIdsBefore</c> / <c>cleanupPreview</c> / <c>deleteByAge</c> / <c>deleteAllHistory</c> / <c>storageStats</c>。
/// 磁盘上的文件(附件目录、备份)归服务层;这里只管库。
/// </summary>
public sealed class StorageRepository(AgentDatabase database, SessionRepository sessions, AttachmentRepository attachments, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public StorageCounts Counts() => new(
        database.Count("SELECT COUNT(*) FROM sessions WHERE parent_session_id IS NULL"),
        database.Count("SELECT COUNT(*) FROM messages"),
        // 在 SQL 里求和,不把每行 parts 拉进内存;LENGTH(CAST(parts AS BLOB)) 数字节(LENGTH(TEXT) 数字符,中文消息会被算成三分之一)。
        database.Count("SELECT COALESCE(SUM(LENGTH(CAST(parts AS BLOB))), 0) FROM messages"),
        database.Count("SELECT COUNT(*) FROM attachments"),
        database.Count("SELECT COUNT(*) FROM workspaces"),
        database.Count("SELECT COUNT(*) FROM usage_records"));

    /// <summary>按会话更新时间取待删的<b>顶层</b>会话 id(子代理转录由删除时跟着父一起走)。</summary>
    public IReadOnlyList<string> SessionIdsBefore(long cutoff) =>
        database.Rows("SELECT id FROM sessions WHERE updated_at < $c AND parent_session_id IS NULL", r => r.GetString(0), ("$c", cutoff));

    /// <summary>「删除 cutoff 之前的会话」的预览:数顶层会话,消息和字节按整棵子树,附件按会话附件(与实际清理共用 <see cref="AttachmentRepository.ForSessions"/>)。</summary>
    public (CleanupCounts Counts, IReadOnlyList<string> TopIds, IReadOnlyList<AttachmentRow> Attachments) PreviewByAge(long cutoff)
    {
        var top = SessionIdsBefore(cutoff);
        var tree = top.SelectMany(sessions.SubtreeIds).Distinct(StringComparer.Ordinal).ToList();
        long messages = 0;
        long bytes = 0;
        foreach (var id in tree)
        {
            messages += database.Count("SELECT COUNT(*) FROM messages WHERE session_id = $s", ("$s", id));
            bytes += database.Count("SELECT COALESCE(SUM(LENGTH(CAST(parts AS BLOB))), 0) FROM messages WHERE session_id = $s", ("$s", id));
        }

        var rows = attachments.ForSessions(top, sessions);
        return (new CleanupCounts(top.Count, messages, rows.Count, bytes + rows.Sum(static r => r.Size)), top, rows);
    }

    /// <summary>「清空对话历史」的预览:全部顶层会话、全部消息、全部会话附件。</summary>
    public (CleanupCounts Counts, IReadOnlyList<AttachmentRow> Attachments) PreviewHistory()
    {
        var c = Counts();
        var rows = attachments.All().Where(static r => r.Scope == Attachments.Session).ToList();
        return (new CleanupCounts(c.Conversations, c.Messages, rows.Count, c.MessageBytes + rows.Sum(static r => r.Size)), rows);
    }

    /// <summary>删这些顶层会话(连同子代理转录)。一个事务;返回真正删掉的会话 id(含子转录)。</summary>
    public IReadOnlyList<string> DeleteSessions(IEnumerable<string> topIds) => database.InTransaction(() =>
        (IReadOnlyList<string>)[.. topIds.SelectMany(sessions.Delete)]);

    /// <summary>
    /// 清空对话历史(2.x 的 <c>deleteAllHistory</c>):消息索引、会话(级联消息 / 附件行 / 改动快照)、Run 记录、会话草稿与排队输入(kv,可以早于会话行存在)一并清掉;
    /// 主题 / 导出附件不属于对话历史,保留;导入来的会话留<b>墓碑</b>,不然自动同步三十秒后把几百条对话原样搬回来。工作区、设置、供应商、用量不动。
    /// </summary>
    public void DeleteAllHistory() => database.InTransaction(() =>
    {
        MessageFts.Clear(database.Connection, database.CurrentTransaction);
        database.Run("DELETE FROM sessions");
        database.Run("DELETE FROM attachments WHERE scope = 'session'"); // 没有 session_id 的草稿不会被级联带走
        database.Run("DELETE FROM runs");
        database.Run("DELETE FROM file_snapshots");
        database.Run("DELETE FROM file_change_sets");
        database.Run("DELETE FROM kv WHERE key LIKE 'session.input.%' OR key LIKE 'session.widget.%'");
        database.Run("UPDATE import_mappings SET sync_state = 'suppressed', target_id = '', updated_at = $t WHERE entity_kind = 'session'", ("$t", _clock.GetUtcNow().ToUnixTimeMilliseconds()));
    });

    /// <summary>整理:先把 WAL 并进主库,再 VACUUM(回收删除留下的空洞)。持有库锁的整个期间别的读写都在等。</summary>
    public void Vacuum()
    {
        lock (database.Gate)
        {
            AgentDatabase.Exec(database.Connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            AgentDatabase.Exec(database.Connection, "VACUUM");
        }
    }
}
