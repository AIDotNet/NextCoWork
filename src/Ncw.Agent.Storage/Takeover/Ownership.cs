using Microsoft.Data.Sqlite;

namespace Ncw.Agent.Storage.Takeover;

/// <summary>所有权标记(2.x 的 <c>service_ownership</c> 表,单行)。3.0 新建的库 <see cref="JournalId"/> 为空。</summary>
public sealed record OwnershipRecord(string DeviceId, string JournalId, string BackupRef, long TakenOverAt);

/// <summary>读写所有权标记。规则照搬 2.x 的 <c>db/takeover.ts</c>:只追加,已有则拒绝覆盖;写入要求 synchronous=FULL。</summary>
public static class Ownership
{
    /// <summary>从一个没打开的库文件只读地读标记(表不存在也返回 null)。</summary>
    public static OwnershipRecord? Read(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return Read(connection);
    }

    public static OwnershipRecord? Read(AgentDatabase database)
    {
        lock (database.Gate)
        {
            return Read(database.Connection);
        }
    }

    /// <summary>写入标记。已有同一 journal 的标记时什么都不做;已有别的标记时抛 <see cref="TakeoverException"/>。</summary>
    public static void Record(AgentDatabase database, OwnershipRecord record)
    {
        lock (database.Gate)
        {
            var connection = database.Connection;
            AgentDatabase.Exec(connection, "PRAGMA synchronous = FULL");
            try
            {
                using var tx = connection.BeginTransaction();
                if (Read(connection, tx) is { } existing)
                {
                    if (existing.JournalId == record.JournalId)
                    {
                        return;
                    }

                    throw new TakeoverException(TakeoverFailure.Conflict, "This data root is already owned.");
                }

                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO service_ownership (id, device_id, journal_id, backup_ref, taken_over_at) VALUES (1, $d, $j, $b, $t)";
                insert.Parameters.AddWithValue("$d", record.DeviceId);
                insert.Parameters.AddWithValue("$j", record.JournalId);
                insert.Parameters.AddWithValue("$b", record.BackupRef);
                insert.Parameters.AddWithValue("$t", record.TakenOverAt);
                insert.ExecuteNonQuery();
                tx.Commit();
            }
            finally
            {
                AgentDatabase.Exec(connection, "PRAGMA synchronous = NORMAL");
            }
        }
    }

    private static OwnershipRecord? Read(SqliteConnection connection, SqliteTransaction? tx = null)
    {
        if (AgentDatabase.Scalar(connection, "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'service_ownership'", tx) is null)
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT device_id, journal_id, backup_ref, taken_over_at FROM service_ownership WHERE id = 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new OwnershipRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)) : null;
    }
}
