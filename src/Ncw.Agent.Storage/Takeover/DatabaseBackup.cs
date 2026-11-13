using Microsoft.Data.Sqlite;

namespace Ncw.Agent.Storage.Takeover;

/// <summary>
/// 一致性备份与验证(2.x 的 <c>db/takeover.ts</c>):
/// <list type="bullet">
/// <item>备份用 <c>VACUUM INTO</c>:SQLite 在一个读事务里产出一致的快照,包括 WAL 里还没检查点的页,不是可能撕裂的文件拷贝。</item>
/// <item>验证不只看能打开:<c>PRAGMA integrity_check</c> 必须是 ok,关键表的行数与源库逐一相等
///   (备份期间源库还在写入就会不相等,这时应当失败重来)。</item>
/// </list>
/// </summary>
public static class DatabaseBackup
{
    private static readonly string[] CountedTables = ["sessions", "messages", "workspaces", "kv", "credentials"];

    public static void Create(string sourcePath, string destinationPath)
    {
        using var source = OpenReadOnly(sourcePath);
        using var command = source.CreateCommand();
        command.CommandText = "VACUUM INTO $p";
        command.Parameters.AddWithValue("$p", destinationPath);
        command.ExecuteNonQuery();
    }

    /// <summary>返回 null 表示通过;否则是失败原因(不含路径和 SQL 细节)。</summary>
    public static string? Verify(string sourcePath, string backupPath)
    {
        try
        {
            Dictionary<string, long> after;
            using (var backup = OpenReadOnly(backupPath))
            {
                using var check = backup.CreateCommand();
                check.CommandText = "PRAGMA integrity_check";
                using var reader = check.ExecuteReader();
                var rows = new List<string>();
                while (reader.Read())
                {
                    rows.Add(reader.GetValue(0)?.ToString() ?? "");
                }

                if (rows is not ["ok"])
                {
                    return "backup failed its integrity check";
                }

                after = Counts(backup);
            }

            using var source = OpenReadOnly(sourcePath);
            foreach (var (table, count) in Counts(source))
            {
                if (!after.TryGetValue(table, out var copied) || copied != count)
                {
                    return $"backup row count differs for {table}";
                }
            }

            return null;
        }
        catch (SqliteException)
        {
            return "backup could not be opened";
        }
    }

    private static Dictionary<string, long> Counts(SqliteConnection connection)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in CountedTables)
        {
            using var exists = connection.CreateCommand();
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $n";
            exists.Parameters.AddWithValue("$n", table);
            if (exists.ExecuteScalar() is null)
            {
                continue;
            }

            // 表名来自上面的常量表。
            counts[table] = Convert.ToInt64(AgentDatabase.Scalar(connection, $"SELECT COUNT(*) FROM {table}"));
        }

        return counts;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        if (!File.Exists(path))
        {
            throw new SqliteException("missing", 14);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
