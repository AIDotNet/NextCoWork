using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
using Ncw.Agent.Storage.Takeover;

namespace Ncw.Agent.Services.Storage;

/// <param name="Directory">设置里的备份目录(没选 = null)。</param>
public sealed record BackupStatus(string? Directory, long? LastBackupAt, string? LastBackupPath, string? LastError, bool Running);

/// <summary>备份清单(2.x 的 <c>BackupManifest</c>,格式 <c>nextcowork-backup</c> v2:3.0 写的备份 2.x 也读得了,反之亦然)。</summary>
public sealed record BackupManifest(
    string AppVersion,
    long CreatedAt,
    int SchemaVersion,
    string DatabaseSha256,
    long SessionCount,
    long MessageCount,
    bool EncryptedCredentials,
    string? CredentialKeySha256,
    int FormatVersion);

/// <param name="ConfirmToken">恢复的确认令牌(<see cref="BackupService.Restore"/> 必须出示)。</param>
public sealed record RestorePreview(string Path, BackupManifest Manifest, string ConfirmToken);

/// <summary>
/// 备份(2.x 的 <c>ipc/storage.ts</c> 的备份部分):
/// <list type="bullet">
/// <item><b>格式</b>:无压缩要求的 ZIP,条目 <c>manifest.json</c> / <c>database.sqlite</c> / <c>settings.json</c>(设置行快照)/ <c>credential.key</c>(NCK1 主密钥;有 NCK1 密文时必须有,
/// 数据库与它唯一对应的主密钥绑成一个恢复单元)。不依赖系统 zip 命令,所以打包到另一台设备仍可恢复。</item>
/// <item><b>快照</b>:<c>VACUUM INTO</c>(SQLite 在一个读事务里产出一致的快照,含 WAL 里还没检查点的页);<b>清单数字必须和归档里的那份库同源</b>——先造快照,再从快照里读行数 / 设置 / <c>user_version</c>。</item>
/// <item><b>落盘</b>:在目标同目录写 <c>.tmp</c> 再改名(跨文件系统改名会失败,同目录一定原子):归档从不出现在目标名字上的一半状态。</item>
/// <item><b>目录</b>:设置里的备份目录必须是绝对路径、不是符号链接、与数据根不重叠、可写。自动备份只管理自己的固定文件(<c>nextcowork-auto.ncwbackup</c>);手动备份永不被清理。</item>
/// <item><b>恢复</b>(危险:owner + 本机通道 + 确认令牌):先<b>验证</b>(清单格式 / 校验和 / 文件头版本 / 行数 / 主密钥校验和),再在线替换整库(<see cref="AgentDatabase.RestoreFrom"/>),
/// 主密钥文件先备份旧的再写新的,失败回滚。有运行中的 Agent 不恢复。</item>
/// </list>
/// </summary>
public sealed class BackupService(StorageEnvironment env)
{
    public const string Extension = ".ncwbackup";
    public const string AutoName = "nextcowork-auto" + Extension;
    public const string StatusKey = "data.backup.status";
    public const int FormatVersion = 2;
    public const long MaxBackupBytes = 1024L * 1024 * 1024;
    public const int MinSchemaVersion = 4;

    private readonly ConfirmTokens _tokens = new(env.Clock);
    private readonly Lock _gate = new();
    private bool _running;

    public ConfirmTokens Tokens => _tokens;

    private KvStore Kv => new(env.Database);

    public BackupStatus Status()
    {
        long? at = null;
        string? path = null;
        string? error = null;
        if (Kv.Get(StatusKey) is { } json)
        {
            try
            {
                if (JsonNode.Parse(json) is JsonObject o)
                {
                    at = o["lastBackupAt"] is JsonValue a && a.TryGetValue<long>(out var l) ? l : null;
                    path = o["lastBackupPath"] is JsonValue p && p.TryGetValue<string>(out var s) ? s : null;
                    error = o["lastError"] is JsonValue e && e.TryGetValue<string>(out var m) ? m : null;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // 坏状态当作没有:下一次备份会重写。
            }
        }

        lock (_gate)
        {
            return new BackupStatus(env.BackupDirectory(), at, path, error, _running);
        }
    }

    private void WriteStatus(long? at, string? path, string? error) =>
        Kv.Set(StatusKey, new JsonObject { ["lastBackupAt"] = at, ["lastBackupPath"] = path, ["lastError"] = error }.ToJsonString());

    // ───────────────────────── 创建 ─────────────────────────

    /// <summary>造一份备份。<paramref name="manual"/> = 用户点的(带时间戳的文件名,永不被清理);否则是自动备份(固定文件名,覆盖上一份)。</summary>
    public BackupStatus Create(bool manual = true)
    {
        lock (_gate)
        {
            if (_running)
            {
                return Status();
            }

            _running = true;
        }

        string? temp = null;
        try
        {
            var dir = EnsureDirectory();
            var dbPath = env.Database.Path;
            var keyPath = Path.Combine(env.DataRoot, CredentialCrypto.MasterKeyFileName);
            byte[]? key = null;
            if (File.Exists(keyPath))
            {
                var info = new FileInfo(keyPath);
                if (info.LinkTarget is not null)
                {
                    throw new StorageOperationException(StorageFailure.Invalid, "storage.backup.keyInvalid", "The credential master key path is not a regular file.");
                }

                key = File.ReadAllBytes(keyPath);
                if (key.Length != 32)
                {
                    throw new StorageOperationException(StorageFailure.Invalid, "storage.backup.keyInvalid", "The credential master key file is damaged, a restorable backup cannot be made.");
                }
            }

            // 私有临时目录:快照只在目标路径上出现,失败时整棵删掉。
            temp = Directory.CreateTempSubdirectory("nextcowork-backup-").FullName;
            var snapshot = Path.Combine(temp, "database.sqlite");
            DatabaseBackup.Create(dbPath, snapshot);
            var inspected = Inspect(snapshot);
            if (inspected.HasProgramCredentials && key is null)
            {
                throw new StorageOperationException(StorageFailure.Invalid, "storage.backup.keyMissing", "The credential master key is missing, a restorable backup cannot be made.");
            }

            var createdAt = env.Now;
            var manifest = new JsonObject
            {
                ["format"] = "nextcowork-backup",
                ["formatVersion"] = FormatVersion,
                ["appVersion"] = env.AppVersion,
                ["createdAt"] = createdAt,
                ["schemaVersion"] = inspected.SchemaVersion,
                ["databaseSha256"] = Sha256(snapshot),
                ["sessionCount"] = inspected.Sessions,
                ["messageCount"] = inspected.Messages,
                ["encryptedCredentials"] = inspected.CredentialRows > 0,
            };
            if (key is not null)
            {
                manifest["credentialKeySha256"] = Convert.ToHexStringLower(SHA256.HashData(key));
            }

            var baseName = manual ? $"nextcowork-{DateTimeOffset.FromUnixTimeMilliseconds(createdAt):yyyy-MM-dd'T'HH-mm-ss}" : "nextcowork-auto";
            var name = baseName + Extension;
            if (manual)
            {
                for (var n = 1; File.Exists(Path.Combine(dir, name)); n++)
                {
                    name = $"{baseName}-{n}{Extension}";
                }
            }

            var target = Path.Combine(dir, name);
            var partial = Path.Combine(dir, $".{name}.tmp");
            try
            {
                using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    AddEntry(zip, "manifest.json", System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString()));
                    AddFile(zip, "database.sqlite", snapshot);
                    AddEntry(zip, "settings.json", System.Text.Encoding.UTF8.GetBytes(inspected.SettingsJson ?? "{}"));
                    if (key is not null)
                    {
                        AddEntry(zip, CredentialCrypto.MasterKeyFileName, key);
                    }
                }

                File.Move(partial, target, overwrite: !manual); // 自动备份覆盖上一份;手动的名字上面已经保证唯一
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            if (!manual)
            {
                // 自动备份只管理自己的固定文件:删掉其它同名规则的旧文件(这里只有固定名,覆盖语义由 overwrite 之前的 Move 保证)。
                foreach (var old in Directory.EnumerateFiles(dir, "nextcowork-auto*" + Extension))
                {
                    if (Path.GetFileName(old) != name)
                    {
                        TryDelete(old);
                    }
                }
            }

            WriteStatus(createdAt, target, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var old = Status();
            WriteStatus(old.LastBackupAt, old.LastBackupPath, e is StorageOperationException so ? so.Key : e.GetType().Name);
            throw;
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    Directory.Delete(temp, recursive: true);
                }
                catch (IOException)
                {
                    // 临时快照删不掉不影响结果
                }
            }

            lock (_gate)
            {
                _running = false;
            }
        }

        return Status();
    }

    private string EnsureDirectory()
    {
        var configured = env.BackupDirectory();
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.backup.noDirectory", "Choose a backup directory first.");
        }

        if (!Path.IsPathRooted(configured))
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.backup.directoryInvalid", "The backup directory must be an absolute path.");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(env.DataRoot));
        if (Overlaps(full, data))
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.backup.directoryOverlaps", "The backup directory overlaps the NextCoWork data directory.");
        }

        try
        {
            Directory.CreateDirectory(full);
            if (new DirectoryInfo(full).LinkTarget is not null)
            {
                throw new IOException("symlink");
            }

            var probe = Path.Combine(full, $".nextcowork-write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.backup.directoryUnwritable", "The backup directory does not exist or is not writable.");
        }

        return full;
    }

    private static bool Overlaps(string a, string b)
    {
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return a.Equals(b, cmp) || a.StartsWith(b + Path.DirectorySeparatorChar, cmp) || b.StartsWith(a + Path.DirectorySeparatorChar, cmp);
    }

    // ───────────────────────── 恢复 ─────────────────────────

    /// <summary>验证一份备份并签发确认令牌(预览)。不改任何东西。</summary>
    public RestorePreview PreviewRestore(string path)
    {
        var parsed = Parse(path);
        parsed.Dispose();
        return new RestorePreview(path, parsed.Manifest, _tokens.Issue("restore:" + Path.GetFullPath(path)));
    }

    /// <summary>
    /// 恢复(危险)。必须出示 <see cref="PreviewRestore"/> 签发的令牌:对得上<b>这份文件</b>、没过期、一次性。先验证再替换;主密钥文件先留旧的,库替换失败就还原。
    /// </summary>
    public BackupStatus Restore(string path, string? confirmToken)
    {
        if (env.HasActiveWork())
        {
            throw new StorageOperationException(StorageFailure.Busy, "storage.busy", "An agent run or a history operation is in progress; try again when it has finished.");
        }

        if (!_tokens.Redeem(confirmToken, "restore:" + Path.GetFullPath(path)))
        {
            throw new StorageOperationException(StorageFailure.Unconfirmed, "storage.confirmationRequired", "Restoring needs a fresh confirmation token from a preview of this backup.");
        }

        using var parsed = Parse(path);
        var keyPath = Path.Combine(env.DataRoot, CredentialCrypto.MasterKeyFileName);
        byte[]? oldKey = File.Exists(keyPath) ? File.ReadAllBytes(keyPath) : null;
        try
        {
            if (parsed.CredentialKey is { } newKey)
            {
                WriteKey(keyPath, newKey);
            }

            env.Database.RestoreFrom(parsed.DatabasePath);
        }
        catch
        {
            // 库没换成:主密钥也要回到原样,否则库里还是旧密文、钥匙却是新的。
            if (parsed.CredentialKey is not null)
            {
                if (oldKey is not null)
                {
                    WriteKey(keyPath, oldKey);
                }
                else
                {
                    TryDelete(keyPath);
                }
            }

            throw;
        }

        // 备份目录等设置跟着恢复的库变了;备份状态是恢复前的 kv,不该留着(它指向的是另一份库的历史)。
        var restored = Status();
        return restored;
    }

    private static void WriteKey(string path, byte[] key)
    {
        var temp = path + ".restore.tmp";
        File.WriteAllBytes(temp, key);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }

    // ───────────────────────── 解析与验证 ─────────────────────────

    private sealed class ParsedBackup(string tempDir, string databasePath, BackupManifest manifest, byte[]? credentialKey) : IDisposable
    {
        public string DatabasePath { get; } = databasePath;

        public BackupManifest Manifest { get; } = manifest;

        public byte[]? CredentialKey { get; } = credentialKey;

        public void Dispose()
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
                SqliteConnection.ClearAllPools();
            }
            catch (IOException)
            {
                // 临时文件删不掉不影响结果
            }
        }
    }

    private ParsedBackup Parse(string path)
    {
        if (!Path.IsPathRooted(path) || !File.Exists(path))
        {
            throw new StorageOperationException(StorageFailure.NotFound, "storage.backup.notFound", "The backup file does not exist.");
        }

        if (new FileInfo(path).Length > MaxBackupBytes)
        {
            throw new StorageOperationException(StorageFailure.Invalid, "storage.backup.tooLarge", "The backup file is too large.");
        }

        var temp = Directory.CreateTempSubdirectory("nextcowork-restore-").FullName;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifestEntry = zip.GetEntry("manifest.json");
            var dbEntry = zip.GetEntry("database.sqlite");
            if (manifestEntry is null || dbEntry is null || zip.GetEntry("settings.json") is null)
            {
                throw Invalid("storage.backup.incomplete", "The backup is missing required files.");
            }

            JsonObject m;
            using (var reader = new StreamReader(manifestEntry.Open()))
            {
                m = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw Invalid("storage.backup.manifestInvalid", "The backup manifest is invalid.");
            }

            var manifest = ValidateManifest(m);
            var dbPath = Path.Combine(temp, "database.sqlite");
            dbEntry.ExtractToFile(dbPath, overwrite: true);
            if (!string.Equals(Sha256(dbPath), manifest.DatabaseSha256, StringComparison.Ordinal))
            {
                throw Invalid("storage.backup.checksum", "The backup database checksum does not match.");
            }

            // SQLite 文件头:前 15 字节是魔数;user_version 是 60..63 的大端 uint32。
            using (var stream = File.OpenRead(dbPath))
            {
                var header = new byte[64];
                if (stream.Read(header, 0, 64) != 64 || System.Text.Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
                {
                    throw Invalid("storage.backup.databaseInvalid", "The backup database file is not valid.");
                }

                var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(60, 4));
                if (version != manifest.SchemaVersion)
                {
                    throw Invalid("storage.backup.schemaMismatch", "The backup database version does not match its manifest.");
                }
            }

            var inspected = Inspect(dbPath);
            if (inspected.Sessions != manifest.SessionCount || inspected.Messages != manifest.MessageCount)
            {
                throw Invalid("storage.backup.countMismatch", "The backup's manifest counts do not match its database.");
            }

            byte[]? key = null;
            if (zip.GetEntry(CredentialCrypto.MasterKeyFileName) is { } keyEntry)
            {
                using var ms = new MemoryStream();
                using (var s = keyEntry.Open())
                {
                    s.CopyTo(ms);
                }

                key = ms.ToArray();
                if (key.Length != 32)
                {
                    throw Invalid("storage.backup.keyInvalid", "The backup's credential master key has an invalid length.");
                }
            }

            if (manifest.FormatVersion == FormatVersion)
            {
                if (inspected.HasProgramCredentials && (key is null || manifest.CredentialKeySha256 is null))
                {
                    throw Invalid("storage.backup.keyMissing", "The backup contains encrypted credentials but no master key.");
                }

                if (manifest.CredentialKeySha256 is { } expected && (key is null || Convert.ToHexStringLower(SHA256.HashData(key)) != expected))
                {
                    throw Invalid("storage.backup.keyChecksum", "The backup's credential master key does not match its checksum.");
                }
            }
            else if (inspected.HasProgramCredentials)
            {
                throw Invalid("storage.backup.keyMissing", "An old-format backup cannot legitimately contain program-encrypted credentials.");
            }

            return new ParsedBackup(temp, dbPath, manifest, key);
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or IOException)
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }

            throw Invalid("storage.backup.unreadable", "The backup file could not be read.");
        }
        catch
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    private static BackupManifest ValidateManifest(JsonObject m)
    {
        static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static long? Num(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
        var formatVersion = Num(m, "formatVersion");
        var sha = Str(m, "databaseSha256");
        var keySha = Str(m, "credentialKeySha256");
        var schema = Num(m, "schemaVersion");
        var app = Str(m, "appVersion");
        var createdAt = Num(m, "createdAt");
        var sessions = Num(m, "sessionCount");
        var messages = Num(m, "messageCount");
        var encrypted = m["encryptedCredentials"] is JsonValue enc && enc.TryGetValue<bool>(out var flag) ? flag : (bool?)null;
        var ok = Str(m, "format") == "nextcowork-backup"
            && formatVersion is 1 or FormatVersion
            && app is { Length: > 0 and <= 256 }
            && createdAt is >= 0
            && sha is { Length: 64 } && IsHex(sha)
            && schema is >= MinSchemaVersion && schema <= AgentDatabase.LatestVersion
            && sessions is >= 0 && messages is >= 0
            && encrypted is not null
            && (keySha is null || (keySha.Length == 64 && IsHex(keySha)));
        if (!ok)
        {
            throw Invalid("storage.backup.unsupported", "The backup format or database version is not supported.");
        }

        return new BackupManifest(app!, createdAt!.Value, (int)schema!.Value, sha!, sessions!.Value, messages!.Value, encrypted!.Value, keySha, (int)formatVersion!.Value);
    }

    private static bool IsHex(string s) => s.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static StorageOperationException Invalid(string key, string message) => new(StorageFailure.Invalid, key, message);

    private sealed record Inspection(int SchemaVersion, long Sessions, long Messages, long CredentialRows, bool HasProgramCredentials, string? SettingsJson);

    /// <summary>从一份(快照 / 备份里的)库文件里读行数、凭据情况、设置行、版本:清单必须和归档里的那份库同源。</summary>
    private static Inspection Inspect(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        long Count(string sql) => Convert.ToInt64(Scalar(connection, sql));
        var program = false;
        long credentials = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT blob FROM credentials";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                credentials++;
                program |= reader.GetFieldValue<byte[]>(0) is { } blob && CredentialCrypto.IsProgramEncrypted(blob);
            }
        }

        return new Inspection(
            Convert.ToInt32(Scalar(connection, "PRAGMA user_version")), Count("SELECT COUNT(*) FROM sessions"), Count("SELECT COUNT(*) FROM messages"),
            credentials, program, Scalar(connection, "SELECT json FROM settings WHERE id = 1") as string);
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(data);
    }

    private static void AddFile(ZipArchive zip, string name, string path)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var file = File.OpenRead(path);
        file.CopyTo(stream);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
