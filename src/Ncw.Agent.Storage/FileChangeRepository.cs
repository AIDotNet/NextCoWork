using System.Security.Cryptography;
using System.Text;
using Ncw.Core.Domain;

namespace Ncw.Agent.Storage;

/// <summary>一个文件在采集侧的原始改动(Host 从工具层的 <c>PendingChange</c> 映射而来)。<see cref="ChangeKind"/>:<c>created</c> / <c>modified</c>(<c>deleted</c> 预留)。</summary>
public sealed record FileChangeInput(string RelPath, string AbsPath, string? Before, string After, string ChangeKind, bool InWorkspace);

/// <summary>撤销 / 重做要用的完整快照行(含全文)。oversize 的两侧全文为 null(内容没入库)。</summary>
public sealed record FileSnapshotRecord(
    string Path,
    string AbsPath,
    string ChangeKind,
    string? Before,
    string? After,
    string? BeforeHash,
    string AfterHash,
    bool Oversize,
    bool InWorkspace);

/// <summary>摘要里的一个文件条目(不含全文)。</summary>
public sealed record FileChangeEntry(string Path, string ChangeKind, int Additions, int Deletions, bool Oversize, bool InWorkspace);

/// <summary>一轮(顶层 Run)的改动集摘要(不含全文)。<see cref="State"/>:<c>applied</c> / <c>reverted</c>。时间是 Unix 毫秒。</summary>
public sealed record FileChangeSetRecord(
    string RunId,
    string SessionId,
    string WorkspaceId,
    string State,
    int FileCount,
    int Additions,
    int Deletions,
    IReadOnlyList<FileChangeEntry> Files,
    long CreatedAt,
    long UpdatedAt);

/// <summary>单文件的 before / after 全文(diff 预览用)。created 的 before 是空串;oversize 的两侧都是空串。</summary>
public sealed record FileSnapshotDiffRecord(string Path, string ChangeKind, string Before, string After, bool Oversize);

/// <summary>
/// 改动快照(审查卡 / 撤销·恢复)的仓库,对接 2.x 的 <c>file_change_sets</c> / <c>file_snapshots</c> 表(迁移 23,不改结构;移植自 2.x <c>repo.ts</c> 的改动快照部分)。
/// <list type="bullet">
/// <item><b>可叠加</b>:子代理的 Run 先收尾、把改动落到顶层 root Run 下;父 Run 后收尾时再叠加自己的。同一文件两边都改过时<b>保留最早的 before、推进到最新的 after</b>,
/// <c>created</c> 一旦置位不被后续 modify 翻转。</item>
/// <item>单文件(before 或 after)超过 <see cref="SnapshotMaxBytes"/> → oversize:内容存 NULL(禁 diff / 撤销),行数计数留 0;之前已 oversize 的行保持 oversize。</item>
/// <item>空改动不落盘;集合级 +X −Y 与文件数从当前全部快照行重算(叠加后也对);文件按路径排序。</item>
/// <item>会话删除时级联清理(外键 <c>ON DELETE CASCADE</c>,连接级外键开关见 <see cref="AgentDatabase"/>)。</item>
/// </list>
/// 单进程单连接,所有访问经 <see cref="AgentDatabase.Gate"/>;多语句操作经 <see cref="AgentDatabase.InTransaction{T}"/>。
/// </summary>
public sealed class FileChangeRepository(AgentDatabase database)
{
    /// <summary>单文件超过这个字节数就不入库内容(禁 diff / 撤销),防一份大文件把两份全文塞进 SQLite。</summary>
    public const int SnapshotMaxBytes = 1024 * 1024;

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>落盘一轮的文件改动集(可叠加,见类说明)。<paramref name="changes"/> 为空什么都不做。</summary>
    public void Save(string rootRunId, string sourceRunId, string sessionId, string workspaceId, IReadOnlyList<FileChangeInput> changes, long at)
    {
        if (changes.Count == 0)
        {
            return;
        }

        database.InTransaction(() =>
        {
            database.Run(
                """
                INSERT INTO file_change_sets (run_id, session_id, workspace_id, state, created_at, updated_at)
                VALUES ($run, $session, $ws, 'applied', $at, $at)
                ON CONFLICT (run_id) DO NOTHING
                """,
                ("$run", rootRunId), ("$session", sessionId), ("$ws", workspaceId), ("$at", at));

            foreach (var change in changes)
            {
                var existing = database.Row(
                    "SELECT before_content, before_hash, change_kind, oversize FROM file_snapshots WHERE run_id = $run AND file_path = $path",
                    r => new Existing(r.Text(0), r.Text(1), r.GetString(2), r.Int(3) == 1),
                    ("$run", rootRunId), ("$path", change.RelPath));

                // 已有行:沿用它记的 before / change_kind(最早那次);新行:用本次采集的。
                var before = existing is not null ? existing.Before : change.Before;
                var beforeHash = existing is not null ? existing.BeforeHash : change.Before is null ? null : Sha256(change.Before);
                var kind = existing is not null ? existing.Kind : change.ChangeKind;
                var priorOversize = existing is { Oversize: true };

                var afterHash = Sha256(change.After);
                var oversize = priorOversize
                    || (before is not null && Encoding.UTF8.GetByteCount(before) > SnapshotMaxBytes)
                    || Encoding.UTF8.GetByteCount(change.After) > SnapshotMaxBytes;
                // 超限的文件不做 O(n·m) 的行 diff(可能是几 MB),计数留 0。
                var (additions, deletions) = oversize ? (0, 0) : LineDiff.Count(before, change.After);

                database.Run(
                    """
                    INSERT INTO file_snapshots
                      (run_id, file_path, source_run_id, abs_path, change_kind, before_content, after_content, before_hash, after_hash,
                       additions, deletions, oversize, in_workspace, created_at)
                    VALUES ($run, $path, $source, $abs, $kind, $before, $after, $beforeHash, $afterHash, $add, $del, $oversize, $inws, $at)
                    ON CONFLICT (run_id, file_path) DO UPDATE SET
                      source_run_id = excluded.source_run_id,
                      after_content = excluded.after_content,
                      after_hash    = excluded.after_hash,
                      additions     = excluded.additions,
                      deletions     = excluded.deletions,
                      oversize      = excluded.oversize
                    """,
                    ("$run", rootRunId), ("$path", change.RelPath), ("$source", sourceRunId), ("$abs", change.AbsPath), ("$kind", kind),
                    ("$before", oversize ? null : before), ("$after", oversize ? null : change.After),
                    ("$beforeHash", beforeHash), ("$afterHash", afterHash),
                    ("$add", additions), ("$del", deletions), ("$oversize", oversize ? 1 : 0), ("$inws", change.InWorkspace ? 1 : 0), ("$at", at));
            }

            // 集合级 +X −Y 与文件数从当前全部快照行重算(叠加后也对)。
            var totals = database.Row(
                "SELECT COUNT(*), COALESCE(SUM(additions), 0), COALESCE(SUM(deletions), 0) FROM file_snapshots WHERE run_id = $run",
                r => new Totals(r.Int(0), r.Int(1), r.Int(2)),
                ("$run", rootRunId))!;
            database.Run(
                "UPDATE file_change_sets SET file_count = $fc, additions = $a, deletions = $d, updated_at = $at WHERE run_id = $run",
                ("$fc", totals.Count), ("$a", totals.Additions), ("$d", totals.Deletions), ("$at", at), ("$run", rootRunId));
        });
    }

    /// <summary>一轮的改动集摘要(不含全文)。没有返回 null。</summary>
    public FileChangeSetRecord? Get(string runId)
    {
        lock (database.Gate)
        {
            var set = database.Row(
                "SELECT run_id, session_id, workspace_id, state, file_count, additions, deletions, created_at, updated_at FROM file_change_sets WHERE run_id = $run",
                Summary, ("$run", runId));
            return set is null ? null : set with { Files = Files(runId) };
        }
    }

    /// <summary>一个会话里改过文件的各轮(按更新时间倒序)。</summary>
    public IReadOnlyList<FileChangeSetRecord> ListBySession(string sessionId)
    {
        lock (database.Gate)
        {
            var sets = database.Rows(
                "SELECT run_id, session_id, workspace_id, state, file_count, additions, deletions, created_at, updated_at FROM file_change_sets WHERE session_id = $s ORDER BY updated_at DESC, run_id DESC",
                Summary, ("$s", sessionId));
            return [.. sets.Select(s => s with { Files = Files(s.RunId) })];
        }
    }

    /// <summary>单文件的 before / after 全文。没有返回 null。</summary>
    public FileSnapshotDiffRecord? GetDiff(string runId, string path) =>
        database.Row(
            "SELECT file_path, change_kind, before_content, after_content, oversize FROM file_snapshots WHERE run_id = $run AND file_path = $path",
            r => new FileSnapshotDiffRecord(r.GetString(0), r.GetString(1), r.Text(2) ?? "", r.Text(3) ?? "", r.Int(4) == 1),
            ("$run", runId), ("$path", path));

    /// <summary>撤销 / 重做要用的完整快照行(含 before / after 全文与 hash),按路径排序。</summary>
    public IReadOnlyList<FileSnapshotRecord> ListSnapshots(string runId) =>
        database.Rows(
            """
            SELECT file_path, abs_path, change_kind, before_content, after_content, before_hash, after_hash, oversize, in_workspace
            FROM file_snapshots WHERE run_id = $run ORDER BY file_path
            """,
            r => new FileSnapshotRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.Text(3), r.Text(4), r.Text(5), r.GetString(6), r.Int(7) == 1, r.Int(8) == 1),
            ("$run", runId));

    /// <summary>改动集的撤销态;没有这个改动集返回 null。</summary>
    public string? GetState(string runId) =>
        database.Value("SELECT state FROM file_change_sets WHERE run_id = $run", ("$run", runId)) as string;

    /// <summary>翻转整轮的撤销态(<c>applied</c> ⇄ <c>reverted</c>)并记下时间。</summary>
    public void SetState(string runId, string state, long at) =>
        database.Run("UPDATE file_change_sets SET state = $s, updated_at = $at WHERE run_id = $run", ("$s", state), ("$at", at), ("$run", runId));

    private sealed record Existing(string? Before, string? BeforeHash, string Kind, bool Oversize);

    private sealed record Totals(long Count, long Additions, long Deletions);

    private static FileChangeSetRecord Summary(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), (int)r.Int(4), (int)r.Int(5), (int)r.Int(6), [], r.Int(7), r.Int(8));

    private List<FileChangeEntry> Files(string runId) =>
        database.Rows(
            "SELECT file_path, change_kind, additions, deletions, oversize, in_workspace FROM file_snapshots WHERE run_id = $run ORDER BY file_path",
            r => new FileChangeEntry(r.GetString(0), r.GetString(1), (int)r.Int(2), (int)r.Int(3), r.Int(4) == 1, r.Int(5) == 1),
            ("$run", runId));
}
