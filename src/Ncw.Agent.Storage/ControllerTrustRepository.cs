namespace Ncw.Agent.Storage;

/// <summary>一个已配对的控制端。</summary>
public sealed record ControllerTrust(string Fingerprint, string Name, string Role, long PairedAt, long? LastSeenAt);

/// <summary>
/// 已配对的控制端(<c>controller_trust</c> 表,3.0 迁移 038)。设备上的信任真源:
/// 远程连接按对端公钥指纹在这里查角色,查不到就是未配对。
/// </summary>
public sealed class ControllerTrustRepository(AgentDatabase database)
{
    public IReadOnlyList<ControllerTrust> List()
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT fingerprint, name, role, paired_at, last_seen_at FROM controller_trust ORDER BY paired_at";
            using var reader = command.ExecuteReader();
            var list = new List<ControllerTrust>();
            while (reader.Read())
            {
                list.Add(Read(reader));
            }

            return list;
        }
    }

    public ControllerTrust? Get(string fingerprint)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT fingerprint, name, role, paired_at, last_seen_at FROM controller_trust WHERE fingerprint = $f";
            command.Parameters.AddWithValue("$f", fingerprint);
            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    /// <summary>登记(或重新配对时覆盖)一个控制端。</summary>
    public void Put(ControllerTrust trust, string routeTokenHash)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO controller_trust (fingerprint, name, role, paired_at, last_seen_at, route_token_hash)
                VALUES ($f, $n, $r, $p, $l, $h)
                ON CONFLICT (fingerprint) DO UPDATE SET name = excluded.name, role = excluded.role,
                  paired_at = excluded.paired_at, last_seen_at = excluded.last_seen_at, route_token_hash = excluded.route_token_hash
                """;
            command.Parameters.AddWithValue("$f", trust.Fingerprint);
            command.Parameters.AddWithValue("$n", trust.Name);
            command.Parameters.AddWithValue("$r", trust.Role);
            command.Parameters.AddWithValue("$p", trust.PairedAt);
            command.Parameters.AddWithValue("$l", (object?)trust.LastSeenAt ?? DBNull.Value);
            command.Parameters.AddWithValue("$h", routeTokenHash);
            command.ExecuteNonQuery();
        }
    }

    public void Touch(string fingerprint, long at)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "UPDATE controller_trust SET last_seen_at = $t WHERE fingerprint = $f";
            command.Parameters.AddWithValue("$f", fingerprint);
            command.Parameters.AddWithValue("$t", at);
            command.ExecuteNonQuery();
        }
    }

    public bool Remove(string fingerprint)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "DELETE FROM controller_trust WHERE fingerprint = $f";
            command.Parameters.AddWithValue("$f", fingerprint);
            return command.ExecuteNonQuery() > 0;
        }
    }

    private static ControllerTrust Read(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.IsDBNull(4) ? null : reader.GetInt64(4));
}

/// <summary>kv 表(2.x 的 key → json)。3.0 自己的键都以 <c>v3.</c> 开头,不碰 2.x 的键。</summary>
public sealed class KvStore(AgentDatabase database)
{
    public string? Get(string key)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT json FROM kv WHERE key = $k";
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void Set(string key, string json)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "INSERT INTO kv (key, json) VALUES ($k, $j) ON CONFLICT (key) DO UPDATE SET json = excluded.json";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$j", json);
            command.ExecuteNonQuery();
        }
    }
}
