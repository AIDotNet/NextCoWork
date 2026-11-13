using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Ncw.Agent.Storage.Credentials;

/// <summary>旧行迁移的结果。</summary>
/// <param name="Migrated">旧 safeStorage 行成功改写成 NCK1 的条数。</param>
/// <param name="Skipped">已经是 NCK1、无事可做的条数。</param>
/// <param name="Failed">解不开而原样保留的条数(界面上列为「需要重新输入」)。</param>
public sealed record CredentialMigrationResult(int Migrated, int Skipped, int Failed);

/// <summary>
/// <c>credentials</c> 表(ref → blob),规则来自 2.x 的 <c>main/host/index.ts</c>:
/// <list type="bullet">
/// <item>读:NCK1 行用主密钥解;没有 NCK1 前缀的行退回 safeStorage 解密。这条退路长期保留(旧备份随时可能把旧行恢复进来)。</item>
/// <item>写:库里已有 NCK1 行时主密钥必须存在且能解开它们,否则抛异常;生成新密钥会让同一张表出现两套不兼容的密文。</item>
/// <item>主密钥每次现读、用完清零,不缓存(恢复备份会在同一路径替换库和密钥)。</item>
/// </list>
/// 这里的 ref 是表里的物理 ref;按账户作用域换算 ref 的逻辑随供应商模块一起移植。
/// </summary>
public sealed class CredentialStore(AgentDatabase database, string dataRoot, ILegacySecretDecryptor legacy)
{
    /// <summary>取明文;没有这一行返回 null。NCK1 解不开抛 <see cref="CryptographicException"/>,旧行解不开返回 null。</summary>
    public string? Get(string reference)
    {
        if (Blob(reference) is not { } blob)
        {
            return null;
        }

        if (!CredentialCrypto.IsProgramEncrypted(blob))
        {
            return legacy.TryDecrypt(blob);
        }

        var key = CredentialCrypto.LoadMasterKey(dataRoot);
        try
        {
            return CredentialCrypto.Decrypt(key, blob);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Set(string reference, string value)
    {
        var existing = AllBlobs().FirstOrDefault(row => CredentialCrypto.IsProgramEncrypted(row.Blob));
        var key = existing is null ? CredentialCrypto.LoadOrCreateMasterKey(dataRoot) : CredentialCrypto.LoadMasterKey(dataRoot);
        try
        {
            if (existing is not null)
            {
                // 证明这把密钥就是库里那套密文的密钥;不是就抛,不写入。
                CredentialCrypto.Decrypt(key, existing.Blob);
            }

            Put(reference, CredentialCrypto.Encrypt(key, value));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public bool Remove(string reference)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "DELETE FROM credentials WHERE ref = $r";
            command.Parameters.AddWithValue("$r", reference);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>还没转成 NCK1 的旧行数(决定接管时要不要提示钥匙串授权)。</summary>
    public int LegacyCount() => AllBlobs().Count(row => !CredentialCrypto.IsProgramEncrypted(row.Blob));

    /// <summary>
    /// 把旧 safeStorage 行改写成 NCK1。幂等:转换过的行带前缀,再跑一遍全部计入 skipped,零写入。
    /// 库里已有 NCK1 行却拿不到主密钥时什么都不改,旧行全部计为失败(不能制造第二套密文)。
    /// </summary>
    public CredentialMigrationResult MigrateLegacy()
    {
        var rows = AllBlobs();
        if (rows.Count == 0)
        {
            return new CredentialMigrationResult(0, 0, 0);
        }

        var hasProgramRows = rows.Any(row => CredentialCrypto.IsProgramEncrypted(row.Blob));
        byte[] key;
        try
        {
            key = hasProgramRows ? CredentialCrypto.LoadMasterKey(dataRoot) : CredentialCrypto.LoadOrCreateMasterKey(dataRoot);
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            var skipped = rows.Count(row => CredentialCrypto.IsProgramEncrypted(row.Blob));
            return new CredentialMigrationResult(0, skipped, rows.Count - skipped);
        }

        try
        {
            return MigrateRows(rows, Put, blob => legacy.TryDecrypt(blob), plain => CredentialCrypto.Encrypt(key, plain));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// 数据根的主密钥,库还没打开时也能用(例如加密设备私钥)。不存在时只有在库里没有 NCK1 行的情况下才生成;
    /// 已有 NCK1 行却丢了密钥文件时抛异常:生成新密钥会让同一张表出现两套互不兼容的密文。
    /// </summary>
    public static byte[] MasterKeyForDataRoot(string dataRoot)
    {
        try
        {
            return CredentialCrypto.LoadMasterKey(dataRoot);
        }
        catch (FileNotFoundException) when (!HasProgramRows(Path.Combine(dataRoot, AgentDatabase.FileName)))
        {
            return CredentialCrypto.LoadOrCreateMasterKey(dataRoot);
        }
    }

    private static bool HasProgramRows(string dbPath)
    {
        if (!File.Exists(dbPath))
        {
            return false;
        }

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        if (AgentDatabase.Scalar(connection, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'credentials'") is null)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT blob FROM credentials";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetValue(0) is byte[] blob && CredentialCrypto.IsProgramEncrypted(blob))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>迁移编排(2.x 的 <c>migrateCredentialRows</c>),依赖注入,便于直接测。</summary>
    public static CredentialMigrationResult MigrateRows(
        IEnumerable<CredentialRow> rows, Action<string, byte[]> put, Func<byte[], string?> decryptLegacy, Func<string, byte[]> encrypt)
    {
        int migrated = 0, skipped = 0, failed = 0;
        foreach (var row in rows)
        {
            if (CredentialCrypto.IsProgramEncrypted(row.Blob))
            {
                skipped++;
                continue;
            }

            if (decryptLegacy(row.Blob) is not { } plain)
            {
                failed++;
                continue;
            }

            put(row.Reference, encrypt(plain));
            migrated++;
        }

        return new CredentialMigrationResult(migrated, skipped, failed);
    }

    private byte[]? Blob(string reference)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT blob FROM credentials WHERE ref = $r";
            command.Parameters.AddWithValue("$r", reference);
            return command.ExecuteScalar() as byte[];
        }
    }

    private List<CredentialRow> AllBlobs()
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT ref, blob FROM credentials";
            using var reader = command.ExecuteReader();
            var rows = new List<CredentialRow>();
            while (reader.Read())
            {
                // 2.x 曾经有 TEXT 形态的值;统一按字节处理。
                var blob = reader.GetValue(1) switch
                {
                    byte[] bytes => bytes,
                    string text => System.Text.Encoding.UTF8.GetBytes(text),
                    _ => [],
                };
                rows.Add(new CredentialRow(reader.GetString(0), blob));
            }

            return rows;
        }
    }

    private void Put(string reference, byte[] blob)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "INSERT INTO credentials (ref, blob) VALUES ($r, $b) ON CONFLICT (ref) DO UPDATE SET blob = excluded.blob";
            command.Parameters.AddWithValue("$r", reference);
            command.Parameters.Add("$b", SqliteType.Blob).Value = blob;
            command.ExecuteNonQuery();
        }
    }
}

/// <summary>表里的一行(物理 ref + 密文)。</summary>
public sealed record CredentialRow(string Reference, byte[] Blob);
