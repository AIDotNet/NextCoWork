using Microsoft.Data.Sqlite;

namespace Ncw.Agent.Storage;

public sealed partial class AgentDatabase
{
    /// <summary>
    /// 用另一个 SQLite 文件(已经验证过的备份)<b>原地</b>替换整库内容:用 SQLite 的在线备份 API 把源库拷进当前连接,不用关库再开,
    /// 所以持有这个 <see cref="AgentDatabase"/> 的各个服务不必重新装配。持有 <see cref="Gate"/> 的整个期间别的读写都在等。
    /// 比本程序新的库不接收(回退版本后恢复了未来备份会写坏结构);比本程序旧的补跑迁移;恢复之后重新对账全文索引。
    /// </summary>
    public void RestoreFrom(string sourcePath)
    {
        lock (Gate)
        {
            if (CurrentTransaction is not null)
            {
                throw new StorageException("The database cannot be restored while a transaction is open.");
            }

            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            source.Open();
            var version = Convert.ToInt32(Scalar(source, "PRAGMA user_version"));
            if (version > LatestVersion)
            {
                throw new StorageException($"The backup is at schema v{version}, newer than this agent understands (v{LatestVersion}).", newerSchema: true);
            }

            source.BackupDatabase(Connection);
            // 备份文件的页头不带「WAL」:拷进来之后把连接级与库级的设置重新定下来。
            Exec(Connection, "PRAGMA journal_mode = WAL");
            Exec(Connection, "PRAGMA synchronous = NORMAL");
            Migrate(Connection);
            MessageFts.Reconcile(Connection);
        }
    }
}
