using Ncw.Agent.Storage;
using Ncw.Core.Domain;

namespace Ncw.Agent.Services.Storage;

/// <summary>
/// 存储维护(2.x 的 <c>ipc/storage.ts</c> 的统计 / 整理 / 清理部分,设置 › 数据页):
/// <list type="bullet">
/// <item><b>预览与执行共用同一次扫描</b>(<see cref="ScanAttachments"/> / <see cref="StorageRepository.PreviewByAge"/>):两边各写一遍判据的话,预览说要删 3 个、实际删了 5 个,而没有任何测试会红。用户看到的数字必须就是实际发生的事。</item>
/// <item><b>有运行中的 Agent 不清理</b>(<see cref="StorageEnvironment.HasActiveWork"/>)。</item>
/// <item><b>危险操作要二次确认</b>:按年龄 / 清空历史 / 清除本地数据的预览带一枚一次性确认令牌(<see cref="ConfirmTokens"/>),执行必须出示。</item>
/// <item><b>附件清理绝不动</b>:<c>themes/</c>、<c>exports/</c>(不属于会话历史);未超期的草稿(用户挑好图去倒杯水,回来图必须还在,宽限期 7 天);只回收 <c>.tmp</c> 残片、磁盘上有而表里没有的孤儿、超期草稿、以及表里有而磁盘上没有的行。</item>
/// <item>只删附件根下的受管文件:附件 id 永远不能变成任意删除原语;符号链接删的是链接本身,不跟随。</item>
/// </list>
/// </summary>
public sealed class StorageMaintenance(
    StorageEnvironment env, StorageRepository storage, AttachmentRepository attachments, ConfirmTokens tokens, BackupService backups)
{
    /// <summary>一次统计最多看多少个目录条目:有界,不把大目录读成全盘扫描。</summary>
    public const int ScanBudget = 200_000;

    public StorageStats Stats()
    {
        var c = storage.Counts();
        var db = env.Database.Path;
        return new StorageStats(
            SizeOf(db), SizeOf(db + "-wal"), c.MessageBytes, DirectoryBytes(env.AttachmentsRoot), c.Conversations, c.Messages, c.Attachments, c.UsageRecords, c.Workspaces,
            backups.Status().LastBackupAt);
    }

    public StorageStats Vacuum()
    {
        RequireIdle("storage.busy");
        storage.Vacuum();
        return Stats();
    }

    // ───────────────────────── 预览 ─────────────────────────

    public CleanupPreview Preview(string kind, string? age)
    {
        if (!CleanupKinds.IsKnown(kind))
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.invalid.kind", $"Unknown cleanup kind '{kind}'.");
        }

        var preview = kind switch
        {
            CleanupKinds.Attachments => AttachmentPreview(ScanAttachments(env.Now)),
            CleanupKinds.Age => AgePreview(Cutoff(age)),
            CleanupKinds.History => HistoryPreview(),
            _ => LocalDataPreview(),
        };
        return CleanupKinds.NeedsConfirmation(kind) ? preview with { ConfirmToken = tokens.Issue(Operation(kind, age)) } : preview;
    }

    private long Cutoff(string? age) =>
        CleanupAges.TryCutoff(env.Now, age, out var cutoff) ? cutoff : throw new StorageOperationException(StorageFailure.Invalid, "storage.invalid.age", $"'{age}' is not a cleanup age (7d, 15d, 1, 3, 6, 12).");

    private static string Operation(string kind, string? age) => kind == CleanupKinds.Age ? $"cleanup:{kind}:{age}" : $"cleanup:{kind}";

    private CleanupPreview AgePreview(long cutoff)
    {
        var (counts, _, rows) = storage.PreviewByAge(cutoff);
        return new CleanupPreview(CleanupKinds.Age, counts.SessionCount, counts.MessageCount, counts.AttachmentCount, counts.Bytes, LikelyUndeletable(rows.Select(static r => r.Path)));
    }

    private CleanupPreview HistoryPreview()
    {
        var (counts, rows) = storage.PreviewHistory();
        return new CleanupPreview(CleanupKinds.History, counts.SessionCount, counts.MessageCount, counts.AttachmentCount, counts.Bytes, LikelyUndeletable(rows.Select(static r => r.Path)));
    }

    private CleanupPreview AttachmentPreview(ReclaimScan scan) =>
        new(CleanupKinds.Attachments, 0, 0, scan.AttachmentCount, scan.Bytes, []);

    // ───────────────────────── 执行 ─────────────────────────

    /// <summary>附件清理(owner,不需要确认令牌:只回收没有任何引用的残片 / 孤儿 / 超期草稿)。</summary>
    public CleanupResult CleanupAttachments()
    {
        RequireIdle("storage.busy");
        // 预览与执行走同一次扫描,不是两次:两次之间文件可能变化。
        var scan = ScanAttachments(env.Now);
        var undeletable = new List<string>();
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var rowPaths = new HashSet<string>(scan.RowPaths.Values.Select(Path.GetFullPath), StringComparer.Ordinal);
        long deleted = 0;
        foreach (var path in scan.Files)
        {
            if (TryDelete(path))
            {
                if (!rowPaths.Contains(Path.GetFullPath(path)))
                {
                    deleted++; // 文件同时有待删数据库行时,它们合起来仍是一条附件
                }
            }
            else
            {
                failed.Add(Path.GetFullPath(path));
                undeletable.Add(path);
            }
        }

        foreach (var id in scan.RowIds)
        {
            // 文件删不掉时保留行:下一次清理仍有完整信息可重试。
            if (scan.RowPaths.TryGetValue(id, out var path) && failed.Contains(Path.GetFullPath(path)))
            {
                continue;
            }

            attachments.Remove(id);
            deleted++;
        }

        return new CleanupResult(new CleanupPreview(CleanupKinds.Attachments, 0, 0, scan.AttachmentCount, scan.Bytes, [.. undeletable.Distinct(StringComparer.Ordinal)]), deleted);
    }

    /// <summary>破坏性清理(按年龄 / 清空历史 / 清除本地数据)。必须出示这次操作的确认令牌。</summary>
    public CleanupResult Execute(string kind, string? age, string? confirmToken)
    {
        if (!CleanupKinds.NeedsConfirmation(kind))
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.invalid.kind", $"'{kind}' is not a confirmed cleanup kind.");
        }

        var cutoff = kind == CleanupKinds.Age ? Cutoff(age) : 0;
        RequireIdle("storage.busy");
        if (!tokens.Redeem(confirmToken, Operation(kind, age)))
        {
            throw new StorageOperationException(StorageFailure.Unconfirmed, "storage.confirmationRequired", "This operation needs a fresh confirmation token from a preview of the same operation.");
        }

        return kind switch
        {
            CleanupKinds.Age => DeleteByAge(cutoff),
            CleanupKinds.History => ClearHistory(),
            _ => ClearLocalData(),
        };
    }

    private CleanupResult DeleteByAge(long cutoff)
    {
        var (counts, top, rows) = storage.PreviewByAge(cutoff);
        var paths = rows.Select(static r => r.Path).ToList();
        storage.DeleteSessions(top); // 级联:消息、附件行、改动快照、子代理转录
        var physical = RemoveUnreferencedManagedFiles(paths);
        return new CleanupResult(
            new CleanupPreview(CleanupKinds.Age, counts.SessionCount, counts.MessageCount, counts.AttachmentCount, counts.Bytes, physical),
            counts.SessionCount + counts.MessageCount + counts.AttachmentCount);
    }

    private CleanupResult ClearHistory()
    {
        var (counts, rows) = storage.PreviewHistory();
        var paths = rows.Select(static r => r.Path).ToList();
        storage.DeleteAllHistory();
        var physical = RemoveUnreferencedManagedFiles(paths);
        return new CleanupResult(
            new CleanupPreview(CleanupKinds.History, counts.SessionCount, counts.MessageCount, counts.AttachmentCount, counts.Bytes, physical),
            counts.SessionCount + counts.MessageCount + counts.AttachmentCount);
    }

    // ───────────────────────── 清除本地数据 ─────────────────────────

    /// <summary>
    /// 清除本机用户数据时清掉的表(2.x 的 <c>managedLocalPaths</c> 在 3.0 的对应物:数据库里的用户内容)。
    /// <b>保留</b>:设备身份与已配对的控制端(<c>controller_trust</c>、<c>device_*</c>、<c>service_ownership</c>)——清掉它们等于把用户从自己的设备上锁出去;迁移表;备份目录里的文件(外部,不归本机数据管)。
    /// </summary>
    private static readonly string[] LocalDataTables =
    [
        "attachments", "file_snapshots", "file_change_sets", "runs", "sessions", "workspaces", "settings", "kv", "credentials", "providers", "model_aliases", "provider_accounts",
        "mcp_servers", "search_providers", "scheduled_runs", "scheduled_tasks", "usage_records", "usage_daily", "usage_daily_remote", "usage_remote_origin", "model_pricing",
        "import_batch_items", "import_batches", "import_mappings", "import_sources", "config_profiles", "sync_conflict", "sync_outbox", "sync_revisions", "sync_account",
    ];

    private CleanupPreview LocalDataPreview()
    {
        var c = storage.Counts();
        var rows = attachments.All();
        var files = Directory.Exists(env.AttachmentsRoot) ? DirectoryBytes(env.AttachmentsRoot) : 0;
        return new CleanupPreview(CleanupKinds.LocalData, c.Conversations, c.Messages, rows.Count, SizeOf(env.Database.Path) + SizeOf(env.Database.Path + "-wal") + files, []);
    }

    private CleanupResult ClearLocalData()
    {
        var preview = LocalDataPreview();
        env.Database.InTransaction(() =>
        {
            MessageFts.Clear(env.Database.Connection, env.Database.CurrentTransaction);
            foreach (var table in LocalDataTables)
            {
                if (TableExists(table))
                {
                    // 表名来自上面的常量表。
                    ExecuteNonQuery($"DELETE FROM {table}");
                }
            }
        });

        var undeletable = new List<string>();
        if (Directory.Exists(env.AttachmentsRoot))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(env.AttachmentsRoot))
            {
                try
                {
                    if (Directory.Exists(entry) && !IsLink(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry); // 符号链接删的是链接本身
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    undeletable.Add(entry);
                }
            }
        }

        storage.Vacuum(); // 删掉的数据不该还躺在数据库文件的空闲页里
        return new CleanupResult(preview with { Undeletable = undeletable }, preview.SessionCount + preview.MessageCount + preview.AttachmentCount);
    }

    private bool TableExists(string name) =>
        env.Database.InTransaction(() =>
        {
            using var command = env.Database.Command("SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n");
            command.Parameters.AddWithValue("$n", name);
            return command.ExecuteScalar() is not null;
        });

    private void ExecuteNonQuery(string sql)
    {
        lock (env.Database.Gate)
        {
            using var command = env.Database.Command(sql);
            command.ExecuteNonQuery();
        }
    }

    // ───────────────────────── 附件扫描 ─────────────────────────

    /// <summary>
    /// 一次扫描得出三类可回收对象(预览与执行共用):<see cref="ReclaimScan.Files"/>(残片 + 孤儿 + 超期草稿的文件)、<see cref="ReclaimScan.RowIds"/>(超期草稿的行 + 文件已不在的行)。
    /// themes / exports 绝不进入候选集。
    /// </summary>
    internal ReclaimScan ScanAttachments(long now)
    {
        var all = attachments.All();
        var referenced = new HashSet<string>(all.Where(r => IsManaged(r.Path)).Select(r => Path.GetFullPath(r.Path)), StringComparer.Ordinal);
        var files = new List<string>();
        long bytes = 0;

        void Add(string path)
        {
            files.Add(path);
            try
            {
                bytes += new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // 统计不精确不影响回收
            }
        }

        foreach (var path in ReclaimableSessionFiles())
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') && name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                Add(path); // 上传中断留下的残片:永远不该存在
            }
            else if (!referenced.Contains(Path.GetFullPath(path)))
            {
                Add(path); // 真孤儿:磁盘上有、表里没有
            }
        }

        var rowIds = new List<string>();
        var rowPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var cutoff = now - (long)Core.Domain.Attachments.DraftTtl.TotalMilliseconds;
        foreach (var row in attachments.StaleDrafts(cutoff))
        {
            if (row.Scope != Core.Domain.Attachments.Session || !IsManaged(row.Path))
            {
                continue;
            }

            if (!files.Contains(row.Path) && (File.Exists(row.Path) || IsLink(row.Path)))
            {
                Add(row.Path);
            }

            rowIds.Add(row.Id);
            rowPaths[row.Id] = row.Path;
        }

        foreach (var row in all)
        {
            if (row.Scope != Core.Domain.Attachments.Session || !IsManaged(row.Path) || rowIds.Contains(row.Id))
            {
                continue;
            }

            if (!File.Exists(row.Path) && !IsLink(row.Path))
            {
                rowIds.Add(row.Id); // 表里有、磁盘无:只收行
                rowPaths[row.Id] = row.Path;
            }
        }

        return new ReclaimScan(files, rowIds, rowPaths, bytes);
    }

    internal sealed record ReclaimScan(IReadOnlyList<string> Files, IReadOnlyList<string> RowIds, IReadOnlyDictionary<string, string> RowPaths, long Bytes)
    {
        /// <summary>row 与文件的对应:同一附件的文件和行合起来只算一条。</summary>
        public long AttachmentCount
        {
            get
            {
                var rowFiles = new HashSet<string>(RowPaths.Values.Select(Path.GetFullPath), StringComparer.Ordinal);
                return RowIds.Count + Files.Count(f => !rowFiles.Contains(Path.GetFullPath(f)));
            }
        }
    }

    /// <summary>附件根下现在的会话文件:<c>sessions/</c>,和 scope 引入之前直接放在根下的旧会话目录;themes / exports 显式排除。</summary>
    private IEnumerable<string> ReclaimableSessionFiles()
    {
        if (!Directory.Exists(env.AttachmentsRoot))
        {
            yield break;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(env.AttachmentsRoot))
        {
            var name = Path.GetFileName(entry);
            if (name is "themes" or "exports")
            {
                continue;
            }

            if (IsLink(entry) || !Directory.Exists(entry))
            {
                yield return entry;
                continue;
            }

            foreach (var file in WalkFiles(entry))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> WalkFiles(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (IsLink(entry))
            {
                yield return entry; // 符号链接当叶子:不跟随进不可信的树
            }
            else if (Directory.Exists(entry))
            {
                foreach (var inner in WalkFiles(entry))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }

    // ───────────────────────── 文件助手 ─────────────────────────

    /// <summary>路径是不是附件根下 <c>sessions/</c> 里的受管文件(外部绝对路径的附件只是引用,删附件行时从不碰用户的文件)。</summary>
    public bool IsManaged(string path)
    {
        var root = Path.GetFullPath(Path.Combine(env.AttachmentsRoot, "sessions"));
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return relative != "." && !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private IReadOnlyList<string> RemoveUnreferencedManagedFiles(IEnumerable<string> paths)
    {
        var undeletable = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!IsManaged(path) || attachments.ReferenceCount(path) > 0)
            {
                continue;
            }

            if (!TryDelete(path))
            {
                undeletable.Add(path);
            }
        }

        return undeletable;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path); // 符号链接删的是链接本身,不跟随;目录不是有效附件
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>删除前预判大概率删不掉的路径(只读 / 目录不可写)。</summary>
    private IReadOnlyList<string> LikelyUndeletable(IEnumerable<string> paths)
    {
        var list = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!IsManaged(path))
            {
                continue;
            }

            try
            {
                if (File.Exists(path) && new FileInfo(path).IsReadOnly)
                {
                    list.Add(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                list.Add(path);
            }
        }

        return list;
    }

    private void RequireIdle(string key)
    {
        if (env.HasActiveWork())
        {
            throw new StorageOperationException(StorageFailure.Busy, key, "An agent run or a history operation is in progress; try again when it has finished.");
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>附件目录的实际占用:有界、不跟符号链接(链接只按自身大小计)。超过预算时返回已数到的量(不抛、不冒充精确)。</summary>
    internal static long DirectoryBytes(string root)
    {
        long total = 0;
        var scanned = 0;

        void Walk(string dir)
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var entry in entries)
            {
                if (scanned++ >= ScanBudget)
                {
                    return;
                }

                try
                {
                    if (IsLink(entry))
                    {
                        total += 0; // 链接自身的字节数可忽略,绝不跟随到外部目录
                    }
                    else if (Directory.Exists(entry))
                    {
                        Walk(entry);
                    }
                    else
                    {
                        total += new FileInfo(entry).Length;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // 扫描期间消失的条目不计入
                }
            }
        }

        if (Directory.Exists(root))
        {
            Walk(root);
        }

        return total;
    }
}
