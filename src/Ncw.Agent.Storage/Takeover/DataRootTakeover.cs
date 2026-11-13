using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ncw.Agent.Storage.Takeover;

public enum TakeoverPhase
{
    [JsonStringEnumMemberName("backed-up")] BackedUp,
    [JsonStringEnumMemberName("verified")] Verified,
    [JsonStringEnumMemberName("committed")] Committed,
    [JsonStringEnumMemberName("rolled-back")] RolledBack,
}

/// <summary>接管日志(<c>takeover.journal.json</c>,格式与 2.x 相同,v1)。路径都相对数据根。</summary>
public sealed record TakeoverJournal(
    int V,
    string Id,
    TakeoverPhase Phase,
    long StartedAt,
    string BackupDir,
    string[] Sidecars,
    string? DeviceId = null,
    long? CommittedAt = null,
    long? RolledBackAt = null,
    string? PostTakeoverCopy = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(TakeoverJournal))]
internal sealed partial class TakeoverJsonContext : JsonSerializerContext;

public sealed record TakeoverOptions(TimeProvider? Clock = null, Func<int, bool>? IsAlive = null)
{
    internal long Now() => (Clock ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
}

/// <summary>
/// 原地接管 2.x 的数据根(docs/design/04 §4),移植自 2.x 的 <c>main/migration/takeover.ts</c>。不复制、不上传,可验证、可重入、可回退。
/// <list type="number">
/// <item><see cref="Begin"/>:确认 2.x 不在运行 → <c>VACUUM INTO</c> 备份 + 复制凭据主密钥和供应商文件 → 验证备份。日志:backed-up → verified。</item>
/// <item><see cref="Commit"/>:在库里追加所有权标记 → 日志:committed。</item>
/// <item><see cref="Rollback"/>:提交前恢复备份,什么都不丢;提交后必须显式确认「接管之后写入的数据不在备份里」,并先把当前库另存一份。</item>
/// </list>
/// 不变量:2.x 在运行就拒绝,不抢锁;只追加,不改旧表;凭据主密钥原样保留;备份不通过验证就不进入下一阶段(并删掉坏备份)。
/// </summary>
public static class DataRootTakeover
{
    public const string JournalFile = "takeover.journal.json";
    public const string BackupRoot = "takeover-backups";

    /// <summary>与数据库同目录、丢了就解不开旧密文的文件:一并备份,回退时一并恢复。</summary>
    private static readonly string[] SidecarCandidates = ["credential.key", "providers.json"];

    public static TakeoverJournal? ReadJournal(string dataRoot)
    {
        var path = Path.Combine(dataRoot, JournalFile);
        if (!File.Exists(path))
        {
            return null;
        }

        TakeoverJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize(File.ReadAllText(path), TakeoverJsonContext.Default.TakeoverJournal);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            throw new TakeoverException(TakeoverFailure.JournalCorrupt, "Takeover journal is corrupt; refusing to continue.", e);
        }

        if (journal is not { V: 1, Id.Length: > 0, BackupDir.Length: > 0, Sidecars: not null } || !Enum.IsDefined(journal.Phase))
        {
            throw new TakeoverException(TakeoverFailure.JournalCorrupt, "Takeover journal is corrupt; refusing to continue.");
        }

        return journal;
    }

    /// <summary>阶段 1:检查 + 备份 + 验证。已有 verified 且备份仍然有效时直接返回它(重入)。</summary>
    public static TakeoverJournal Begin(string dataRoot, TakeoverOptions? options = null)
    {
        options ??= new TakeoverOptions();
        var dbPath = Path.Combine(dataRoot, AgentDatabase.FileName);
        if (!File.Exists(dbPath))
        {
            throw new TakeoverException(TakeoverFailure.NoDatabase, "No 2.x database was found in this data root.");
        }

        LiveOwnerCheck.AssertNone(LiveOwnerCheck.RootsFor(dataRoot), options.IsAlive);

        var existing = ReadJournal(dataRoot);
        if (existing?.Phase == TakeoverPhase.Committed && Ownership.Read(dbPath) is not null)
        {
            throw new TakeoverException(TakeoverFailure.Conflict, "This data root has already been taken over.");
        }

        if (existing?.Phase == TakeoverPhase.Verified)
        {
            var previous = Path.Combine(dataRoot, existing.BackupDir, AgentDatabase.FileName);
            if (File.Exists(previous) && DatabaseBackup.Verify(dbPath, previous) is null)
            {
                return existing;
            }
        }

        var id = Guid.NewGuid().ToString();
        var backupDir = Path.Combine(BackupRoot, id);
        var absolute = Path.Combine(dataRoot, backupDir);
        CreatePrivateDirectory(absolute);
        var sidecars = SidecarCandidates.Where(name => File.Exists(Path.Combine(dataRoot, name))).ToArray();
        var journal = new TakeoverJournal(1, id, TakeoverPhase.BackedUp, options.Now(), backupDir, sidecars);
        try
        {
            DatabaseBackup.Create(dbPath, Path.Combine(absolute, AgentDatabase.FileName));
            foreach (var name in sidecars)
            {
                File.Copy(Path.Combine(dataRoot, name), Path.Combine(absolute, name));
            }

            SaveJournal(dataRoot, journal);
            if (DatabaseBackup.Verify(dbPath, Path.Combine(absolute, AgentDatabase.FileName)) is { } problem)
            {
                throw new TakeoverException(TakeoverFailure.BackupInvalid, problem);
            }
        }
        catch
        {
            // 坏备份不留着冒充好备份;日志退回上一个有效状态(没有就删掉)。
            Directory.Delete(absolute, recursive: true);
            if (existing is null || existing.Phase == TakeoverPhase.RolledBack)
            {
                File.Delete(Path.Combine(dataRoot, JournalFile));
            }
            else
            {
                SaveJournal(dataRoot, existing);
            }

            throw;
        }

        var verified = journal with { Phase = TakeoverPhase.Verified };
        SaveJournal(dataRoot, verified);
        return verified;
    }

    /// <summary>
    /// 阶段 2:在已经打开(迁移过)的库里追加所有权标记。幂等:已提交返回原日志。
    /// 打开库会执行 3.0 的增量迁移;这一步在备份之后,失败时可以回退。
    /// </summary>
    public static TakeoverJournal Commit(string dataRoot, AgentDatabase database, string deviceId, TakeoverOptions? options = null)
    {
        options ??= new TakeoverOptions();
        var journal = ReadJournal(dataRoot);
        if (journal is null || journal.Phase == TakeoverPhase.RolledBack)
        {
            throw new TakeoverException(TakeoverFailure.Conflict, "No verified takeover is pending.");
        }

        if (journal.Phase == TakeoverPhase.BackedUp)
        {
            throw new TakeoverException(TakeoverFailure.Conflict, "The backup has not been verified yet.");
        }

        var now = options.Now();
        if (Ownership.Read(database) is { } owned)
        {
            if (owned.JournalId != journal.Id)
            {
                throw new TakeoverException(TakeoverFailure.Conflict, "This data root is owned by a different takeover.");
            }
        }
        else
        {
            Ownership.Record(database, new OwnershipRecord(deviceId, journal.Id, journal.BackupDir, now));
        }

        if (journal.Phase == TakeoverPhase.Committed)
        {
            return journal;
        }

        var committed = journal with { Phase = TakeoverPhase.Committed, DeviceId = deviceId, CommittedAt = now };
        SaveJournal(dataRoot, committed);
        return committed;
    }

    /// <summary>回退。调用前必须关闭数据库。提交后要 <paramref name="acknowledgeDataLoss"/>;当前库会先另存为 <c>post-takeover.db</c>。</summary>
    public static TakeoverJournal Rollback(string dataRoot, bool acknowledgeDataLoss = false, TakeoverOptions? options = null)
    {
        options ??= new TakeoverOptions();
        var journal = ReadJournal(dataRoot);
        if (journal is null || journal.Phase == TakeoverPhase.RolledBack)
        {
            throw new TakeoverException(TakeoverFailure.NothingToRollBack, "There is no takeover to roll back.");
        }

        if (journal.Phase == TakeoverPhase.Committed && !acknowledgeDataLoss)
        {
            throw new TakeoverException(TakeoverFailure.DataLossNotAcknowledged,
                "Data written after the takeover is not in the backup; confirm the loss explicitly to roll back.");
        }

        LiveOwnerCheck.AssertNone(LiveOwnerCheck.RootsFor(dataRoot), options.IsAlive);
        var backup = Path.Combine(dataRoot, journal.BackupDir, AgentDatabase.FileName);
        if (!File.Exists(backup))
        {
            throw new TakeoverException(TakeoverFailure.NothingToRollBack, "The pre-takeover backup is missing.");
        }

        // 回退前再验一次备份本身(没有源库可比,只看完整性)。
        if (DatabaseBackup.Verify(backup, backup) is not null)
        {
            throw new TakeoverException(TakeoverFailure.BackupInvalid, "The pre-takeover backup failed its integrity check.");
        }

        string? postTakeoverCopy = null;
        var dbPath = Path.Combine(dataRoot, AgentDatabase.FileName);
        if (journal.Phase == TakeoverPhase.Committed && File.Exists(dbPath))
        {
            postTakeoverCopy = Path.Combine(journal.BackupDir, "post-takeover.db");
            var copyPath = Path.Combine(dataRoot, postTakeoverCopy);
            if (File.Exists(copyPath))
            {
                File.Delete(copyPath);
            }

            DatabaseBackup.Create(dbPath, copyPath);
        }

        // 原子替换:先写同目录临时文件再改名;陈旧的 WAL/SHM 属于被替换掉的库,一并删掉。
        var temp = dbPath + ".restore";
        File.Copy(backup, temp, overwrite: true);
        File.Move(temp, dbPath, overwrite: true);
        File.Delete(dbPath + "-wal");
        File.Delete(dbPath + "-shm");
        foreach (var name in journal.Sidecars)
        {
            File.Copy(Path.Combine(dataRoot, journal.BackupDir, name), Path.Combine(dataRoot, name), overwrite: true);
        }

        var next = journal with { Phase = TakeoverPhase.RolledBack, RolledBackAt = options.Now(), PostTakeoverCopy = postTakeoverCopy };
        SaveJournal(dataRoot, next);
        return next;
    }

    private static void SaveJournal(string dataRoot, TakeoverJournal journal)
    {
        var path = Path.Combine(dataRoot, JournalFile);
        var temp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, fileOptions))
        {
            JsonSerializer.Serialize(stream, journal, TakeoverJsonContext.Default.TakeoverJournal);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
