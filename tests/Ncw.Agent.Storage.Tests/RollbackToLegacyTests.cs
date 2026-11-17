using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage.Takeover;

namespace Ncw.Agent.Storage.Tests;

/// <summary>设置 · 数据 · 回退到 2.x 的存储层(docs/design/04 §5):先登记请求,关库之后执行;执行后不自动再接管。</summary>
public sealed class RollbackToLegacyTests : IDisposable
{
    private static readonly TakeoverOptions Dead = new(IsAlive: _ => false);
    private static readonly TakeoverOptions Alive = new(IsAlive: _ => true);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ncwr-{Guid.NewGuid():N}");

    public RollbackToLegacyTests()
    {
        Directory.CreateDirectory(_dir);
        Put("from-2x");
        File.WriteAllText(Path.Combine(_dir, "credential.key"), "MASTER-KEY-BYTES");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private void Put(string key)
    {
        using var db = AgentDatabase.Open(_dir);
        using var command = db.Connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO kv (key, json) VALUES ($k, $j)";
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$j", new JsonObject { ["key"] = key }.ToJsonString());
        command.ExecuteNonQuery();
    }

    private List<string> Keys()
    {
        using var db = AgentDatabase.Open(_dir);
        using var command = db.Connection.CreateCommand();
        command.CommandText = "SELECT key FROM kv ORDER BY key";
        using var reader = command.ExecuteReader();
        var keys = new List<string>();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary>完整地接管一次(备份 → 提交),然后在 3.0 里再写一条。</summary>
    private TakeoverJournal TakeOver()
    {
        DataRootTakeover.Begin(_dir, Dead);
        TakeoverJournal journal;
        using (var db = AgentDatabase.Open(_dir))
        {
            journal = DataRootTakeover.Commit(_dir, db, "dev_v3", Dead);
        }

        Put("written-in-3.0");
        return journal;
    }

    [Fact]
    public void Only_data_taken_over_from_2x_with_its_backup_can_go_back()
    {
        Assert.Equal(new RollbackAvailability(false, null, false), RollbackToLegacy.Inspect(_dir)); // 2.x 数据,还没接管
        Assert.Equal(TakeoverFailure.NothingToRollBack, Assert.Throws<TakeoverException>(() => RollbackToLegacy.Request(_dir, 1)).Failure);

        var journal = TakeOver();
        var info = RollbackToLegacy.Inspect(_dir);
        Assert.True(info.Available);
        Assert.Equal(journal.CommittedAt, info.TakenOverAt);
        Assert.False(info.Pending);

        // 备份没了就不能回退(也不显示入口)
        Directory.Delete(Path.Combine(_dir, journal.BackupDir), recursive: true);
        Assert.False(RollbackToLegacy.Inspect(_dir).Available);
    }

    [Fact]
    public void A_request_is_only_recorded_and_can_be_withdrawn()
    {
        TakeOver();
        RollbackToLegacy.Request(_dir, 42);
        RollbackToLegacy.Request(_dir, 43); // 幂等
        Assert.True(RollbackToLegacy.Inspect(_dir).Pending);
        Assert.Equal(["from-2x", "written-in-3.0"], Keys()); // 只登记,什么都没动

        RollbackToLegacy.Cancel(_dir);
        Assert.False(RollbackToLegacy.Inspect(_dir).Pending);
        Assert.Null(RollbackToLegacy.RunPending(_dir, Dead));
    }

    [Fact]
    public void Running_the_request_restores_the_2x_database_keeps_the_3x_copy_and_marks_the_root_rolled_back()
    {
        var taken = TakeOver();
        RollbackToLegacy.Request(_dir, 42);

        var journal = RollbackToLegacy.RunPending(_dir, Dead);

        Assert.NotNull(journal);
        Assert.Equal(TakeoverPhase.RolledBack, journal.Phase);
        Assert.Equal(["from-2x"], Keys()); // 2.x 看到的是迁移前的样子
        Assert.Null(Ownership.Read(Path.Combine(_dir, AgentDatabase.FileName))); // 2.9 不会把它当成「被 3.0 接管」
        Assert.True(File.Exists(Path.Combine(_dir, taken.BackupDir, "post-takeover.db"))); // 3.0 里写的那份另存了,没删
        Assert.Equal("MASTER-KEY-BYTES", File.ReadAllText(Path.Combine(_dir, "credential.key")));
        Assert.False(RollbackToLegacy.Inspect(_dir).Pending);
        Assert.True(RollbackToLegacy.IsRolledBack(_dir));
        Assert.Equal(DataRootKind.Legacy, LegacyLayout.Inspect(_dir)); // 再次接管要用户明确要求(DataGate 负责)
    }

    [Fact]
    public void A_request_that_cannot_run_yet_is_kept_and_one_that_never_can_is_dropped()
    {
        TakeOver();
        RollbackToLegacy.Request(_dir, 42);

        // 2.x 正在运行(Chromium 的 SingletonLock 指向活进程):之后能成功,请求留着
        if (!OperatingSystem.IsWindows()) // SingletonLock 符号链接是 Unix 的形态
        {
            File.CreateSymbolicLink(Path.Combine(_dir, "SingletonLock"), $"host-{Environment.ProcessId}");
            Assert.Equal(TakeoverFailure.LegacyAppRunning, Assert.Throws<TakeoverException>(() => RollbackToLegacy.RunPending(_dir, Alive)).Failure);
            Assert.True(RollbackToLegacy.Inspect(_dir).Pending);
            File.Delete(Path.Combine(_dir, "SingletonLock"));
        }

        // 备份没了:永远不会成功,请求删掉(否则每次启动都失败一次)
        Directory.Delete(Path.Combine(_dir, DataRootTakeover.ReadJournal(_dir)!.BackupDir), recursive: true);
        Assert.Throws<TakeoverException>(() => RollbackToLegacy.RunPending(_dir, Dead));
        Assert.False(RollbackToLegacy.Inspect(_dir).Pending);
    }

    [Fact]
    public void After_going_back_the_data_can_be_taken_over_again()
    {
        TakeOver();
        RollbackToLegacy.Request(_dir, 42);
        RollbackToLegacy.RunPending(_dir, Dead);
        Put("written-in-2.x-afterwards");

        var again = DataRootTakeover.Begin(_dir, Dead);
        using (var db = AgentDatabase.Open(_dir))
        {
            DataRootTakeover.Commit(_dir, db, "dev_v3", Dead);
        }

        Assert.Equal(TakeoverPhase.Verified, again.Phase);
        Assert.False(RollbackToLegacy.IsRolledBack(_dir));
        Assert.Equal(["from-2x", "written-in-2.x-afterwards"], Keys());
    }
}
