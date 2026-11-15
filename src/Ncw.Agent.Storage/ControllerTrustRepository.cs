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

    /// <summary>
    /// 当前所有有效的中继路由令牌哈希(每个已配对控制端一个;吊销 = 删行,哈希随之消失)。设备把它推给中继。
    /// 库里只有哈希(SHA-256,小写十六进制),令牌本身只在配对那一刻给过控制端。
    /// </summary>
    public IReadOnlyList<string> RouteTokenHashes()
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT route_token_hash FROM controller_trust WHERE route_token_hash IS NOT NULL AND route_token_hash <> '' ORDER BY paired_at";
            using var reader = command.ExecuteReader();
            var list = new List<string>();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }

            return list;
        }
    }

    /// <summary>某个控制端的路由令牌哈希(经中继接入时核对「令牌属于 TLS 里的这个控制端」);没有为 null。</summary>
    public string? RouteTokenHash(string fingerprint)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "SELECT route_token_hash FROM controller_trust WHERE fingerprint = $f";
            command.Parameters.AddWithValue("$f", fingerprint);
            return command.ExecuteScalar() as string;
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

    public bool SetRole(string fingerprint, string role)
    {
        lock (database.Gate)
        {
            using var command = database.Connection.CreateCommand();
            command.CommandText = "UPDATE controller_trust SET role = $r WHERE fingerprint = $f";
            command.Parameters.AddWithValue("$f", fingerprint);
            command.Parameters.AddWithValue("$r", role);
            return command.ExecuteNonQuery() > 0;
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
