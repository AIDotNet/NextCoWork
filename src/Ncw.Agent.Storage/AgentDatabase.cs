using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Ncw.Agent.Storage;

/// <summary>存储层的失败(数据库比本程序新、迁移失败等)。</summary>
public sealed class StorageException(string message, Exception? inner = null, bool newerSchema = false) : Exception(message, inner)
{
    /// <summary>数据库比本程序新(例如回退了版本):不认识的结构不能碰。</summary>
    public bool NewerSchema { get; } = newerSchema;
}

/// <summary>一条迁移。版本号与 2.x 共用一个序列:1–37 是 2.x 的,38 起是 3.0 的。</summary>
public sealed record Migration(int Version, string Name, string Sql);

/// <summary>
/// Agent 的 SQLite 数据库:<c>&lt;数据根&gt;/nextcowork.db</c>,与 2.x 同一个文件、同一套结构(决策 D13)。
/// 打开时的规则照搬 2.x 的 <c>db/index.ts</c>:WAL、synchronous=NORMAL、busy_timeout=2000、外键开(连接级开关,
/// 漏开会让级联删除静默失效)、按 migrations 表执行未跑过的迁移(每条一个事务,失败整条回滚)、user_version 作为镜像。
/// 单进程单连接,调用方用 <see cref="Gate"/> 串行化。
/// </summary>
public sealed partial class AgentDatabase : IDisposable
{
    public const string FileName = "nextcowork.db";

    private AgentDatabase(SqliteConnection connection, string path)
    {
        Connection = connection;
        Path = path;
    }

    public SqliteConnection Connection { get; }

    public string Path { get; }

    /// <summary>串行化所有读写(Microsoft.Data.Sqlite 的连接不是线程安全的)。</summary>
    public object Gate { get; } = new();

    /// <summary>当前正在进行的事务(只在持有 <see cref="Gate"/> 时有值)。仓库用 <see cref="Command"/> 建命令,命令会自动挂上它。</summary>
    public SqliteTransaction? CurrentTransaction { get; private set; }

    /// <summary>建一条挂着当前事务的命令。调用方负责持有 <see cref="Gate"/>、负责释放命令。</summary>
    public SqliteCommand Command(string sql)
    {
        var command = Connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = CurrentTransaction;
        return command;
    }

    /// <summary>
    /// 在一个事务里执行(对应 2.x 的 <c>tx()</c>)。已经在事务里时直接并入外层,不开嵌套事务;
    /// 异常使整个最外层事务回滚。其它仓库想和消息写入同一个事务,也走这里。
    /// </summary>
    public T InTransaction<T>(Func<T> body)
    {
        lock (Gate)
        {
            if (CurrentTransaction is not null)
            {
                return body();
            }

            var tx = Connection.BeginTransaction();
            CurrentTransaction = tx;
            try
            {
                var result = body();
                tx.Commit();
                return result;
            }
            catch
            {
                try
                {
                    tx.Rollback();
                }
                catch (Exception e) when (e is InvalidOperationException or SqliteException)
                {
                    // 提交失败时事务可能已经结束;原异常更有用。
                }

                throw;
            }
            finally
            {
                CurrentTransaction = null;
                tx.Dispose();
            }
        }
    }

    public void InTransaction(Action body) => InTransaction(() =>
    {
        body();
        return 0;
    });

    /// <summary>本程序认识的全部迁移(按版本排序)。</summary>
    public static IReadOnlyList<Migration> Migrations { get; } = LoadMigrations();

    public static int LatestVersion => Migrations[^1].Version;

    public static AgentDatabase Open(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var path = System.IO.Path.Combine(dataRoot, FileName);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        connection.Open();
        try
        {
            Exec(connection, "PRAGMA journal_mode = WAL");
            Exec(connection, "PRAGMA synchronous = NORMAL");
            Exec(connection, "PRAGMA busy_timeout = 2000");
            Migrate(connection);
            MessageFts.Reconcile(connection);
            return new AgentDatabase(connection, path);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public int UserVersion() => Convert.ToInt32(Scalar(Connection, "PRAGMA user_version"));

    /// <summary>会话总数(接管摘要用)。</summary>
    public long SessionCount()
    {
        lock (Gate)
        {
            return Convert.ToInt64(Scalar(Connection, "SELECT COUNT(*) FROM sessions"));
        }
    }

    /// <summary>
    /// 把 WAL 全部写回主库并截断(<c>wal_checkpoint(TRUNCATE)</c>):云端休眠前调用,这样卷快照里只有一个自洽的 <c>nextcowork.db</c>。
    /// 返回是否真的做完(有读者占着或库忙时返回 false,调用方按「尽力而为」处理,WAL 文件本身仍然是一致的)。
    /// </summary>
    public bool Checkpoint()
    {
        lock (Gate)
        {
            try
            {
                using var command = Connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                using var reader = command.ExecuteReader();
                return reader.Read() && reader.GetInt64(0) == 0; // 第一列 busy:0 = 完成
            }
            catch (SqliteException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            try
            {
                Exec(Connection, "PRAGMA wal_checkpoint(TRUNCATE)");
            }
            catch (SqliteException)
            {
            }

            Connection.Dispose();
        }
    }

    private static void Migrate(SqliteConnection connection)
    {
        Exec(connection, """
            CREATE TABLE IF NOT EXISTS migrations (
              version    INTEGER PRIMARY KEY,
              name       TEXT NOT NULL,
              applied_at INTEGER NOT NULL
            )
            """);

        var applied = new HashSet<int>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT version FROM migrations";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        // 数据库比本程序新(例如用户先装了更新的版本又回退):不认识的结构不能碰。
        if (applied.Count > 0 && applied.Max() > LatestVersion)
        {
            throw new StorageException(
                $"The database is at schema v{applied.Max()}, newer than this agent understands (v{LatestVersion}).", newerSchema: true);
        }

        foreach (var migration in Migrations)
        {
            if (applied.Contains(migration.Version))
            {
                continue;
            }

            using var tx = connection.BeginTransaction();
            try
            {
                // 2.x 的第 7 条修复了一个曾被错标为第 5 条的列:部分旧库已经有 owner_id,部分没有。先查再补。
                if (migration.Version == 7 && !HasColumn(connection, "attachments", "owner_id"))
                {
                    Exec(connection, "ALTER TABLE attachments ADD COLUMN owner_id TEXT", tx);
                    Exec(connection, "UPDATE attachments SET owner_id = session_id WHERE owner_id IS NULL", tx);
                }

                Exec(connection, migration.Sql, tx);

                // 第 29 条:清空后按消息表重建全文索引(修复历史漏索引)。
                if (migration.Version == 29)
                {
                    MessageFts.Rebuild(connection, tx);
                }

                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO migrations (version, name, applied_at) VALUES ($v, $n, $t)";
                insert.Parameters.AddWithValue("$v", migration.Version);
                insert.Parameters.AddWithValue("$n", migration.Name);
                insert.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                insert.ExecuteNonQuery();
                tx.Commit();
            }
            catch (Exception e)
            {
                tx.Rollback();
                throw new StorageException($"Migration {migration.Version} ({migration.Name}) failed; the database was rolled back.", e);
            }
        }

        // PRAGMA 不能用参数;拼进去的是本程序常量表里的整数。
        Exec(connection, $"PRAGMA user_version = {LatestVersion}");
    }

    private static IReadOnlyList<Migration> LoadMigrations()
    {
        var assembly = typeof(AgentDatabase).Assembly;
        var list = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = MigrationName().Match(resource);
            if (!match.Success)
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            list.Add(new Migration(int.Parse(match.Groups["v"].Value), match.Groups["n"].Value, reader.ReadToEnd()));
        }

        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Version != i + 1)
            {
                throw new StorageException($"Migrations must be contiguous from 1; found v{list[i].Version} at position {i + 1}.");
            }
        }

        return list;
    }

    [GeneratedRegex(@"^Ncw\.Schema/(Legacy|V3)[\\/](?<v>\d{3})-(?<n>[a-z0-9-]+)\.sql$")]
    private static partial Regex MigrationName();

    internal static void Exec(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
