using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Storage.Takeover;

namespace Ncw.Agent.Storage.Tests;

/// <summary>移植自 2.x 的 <c>src/main/migration/__tests__/takeover.test.ts</c>(见 docs/parity.md)。</summary>
public sealed class TakeoverTests : IDisposable
{
    private static readonly TakeoverOptions Dead = new(IsAlive: _ => false);
    private static readonly TakeoverOptions Alive = new(IsAlive: _ => true);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ncwt-{Guid.NewGuid():N}");

    public TakeoverTests()
    {
        Directory.CreateDirectory(_dir);
        Seed("a", "b", "c");
        File.WriteAllText(Path.Combine(_dir, "credential.key"), "MASTER-KEY-BYTES");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string Db => Path.Combine(_dir, AgentDatabase.FileName);

    /// <summary>模拟 2.x 写入:直接用连接写 kv(不经过所有权标记)。</summary>
    private void Seed(params string[] keys)
    {
        using var db = AgentDatabase.Open(_dir);
        foreach (var key in keys)
        {
            using var command = db.Connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO kv (key, json) VALUES ($k, $j)";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$j", new JsonObject { ["key"] = key }.ToJsonString());
            command.ExecuteNonQuery();
        }
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

    private TakeoverJournal CommitWith(string deviceId = "dev_test")
    {
        using var db = AgentDatabase.Open(_dir);
        return DataRootTakeover.Commit(_dir, db, deviceId, Dead);
    }

    private static TakeoverFailure FailureOf(Action action) => Assert.Throws<TakeoverException>(action).Failure;

    [Fact]
    public void Makes_a_verified_consistent_backup_and_leaves_the_source_untouched()
    {
        var journal = DataRootTakeover.Begin(_dir, Dead);

        Assert.Equal(TakeoverPhase.Verified, journal.Phase);
        Assert.Equal(["credential.key"], journal.Sidecars);
        Assert.Equal("MASTER-KEY-BYTES", File.ReadAllText(Path.Combine(_dir, journal.BackupDir, "credential.key")));
        Assert.Equal("MASTER-KEY-BYTES", File.ReadAllText(Path.Combine(_dir, "credential.key"))); // 主密钥原样保留
        Assert.Null(DatabaseBackup.Verify(Db, Path.Combine(_dir, journal.BackupDir, AgentDatabase.FileName)));
        Assert.Equal(["a", "b", "c"], Keys());
        Assert.Equal(journal.Id, DataRootTakeover.ReadJournal(_dir)?.Id);
    }

    [Fact]
    public void Journal_uses_the_2x_phase_names()
    {
        DataRootTakeover.Begin(_dir, Dead);

        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, DataRootTakeover.JournalFile)))!;
        Assert.Equal(1, json["v"]!.GetValue<int>());
        Assert.Equal("verified", json["phase"]!.GetValue<string>());
        Assert.NotNull(json["backupDir"]);
    }

    [Fact]
    public void Refuses_while_2x_is_alive_but_not_for_stale_locks()
    {
        if (OperatingSystem.IsWindows()) return; // SingletonLock 符号链接是 Unix 的形态

        File.CreateSymbolicLink(Path.Combine(_dir, "SingletonLock"), $"host-{Environment.ProcessId}");
        Assert.Equal(TakeoverFailure.LegacyAppRunning, FailureOf(() => DataRootTakeover.Begin(_dir, Alive)));
        Assert.False(Directory.Exists(Path.Combine(_dir, DataRootTakeover.BackupRoot))); // 拒绝时什么都没做
        DataRootTakeover.Begin(_dir, Dead); // 陈旧锁不挡路

        File.Delete(Path.Combine(_dir, "SingletonLock"));
        File.WriteAllText(Path.Combine(_dir, "service.lock"), """{"pid":4242,"token":"xxxxxxxxxxxxxxxx","startedAt":1}""");
        Assert.Equal(TakeoverFailure.LegacyServiceRunning, FailureOf(() => DataRootTakeover.Begin(_dir, Alive)));
        File.WriteAllText(Path.Combine(_dir, "service.lock"), "{ unreadable");
        Assert.Equal(TakeoverFailure.LegacyServiceRunning, FailureOf(() => DataRootTakeover.Begin(_dir, Dead)));
    }

    [Fact]
    public void Checks_the_profile_root_above_a_data_directory()
    {
        if (OperatingSystem.IsWindows()) return;

        var profile = Path.Combine(_dir, "profile");
        var data = Path.Combine(profile, LegacyLayout.DataSubdirectory);
        Directory.CreateDirectory(data);
        File.Copy(Db, Path.Combine(data, AgentDatabase.FileName));
        File.CreateSymbolicLink(Path.Combine(profile, "SingletonLock"), "host-4242");

        Assert.Equal(TakeoverFailure.LegacyAppRunning, FailureOf(() => DataRootTakeover.Begin(data, Alive)));
    }

    [Fact]
    public void Rejects_a_data_root_without_a_database()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(Db);

        Assert.Equal(TakeoverFailure.NoDatabase, FailureOf(() => DataRootTakeover.Begin(_dir, Dead)));
    }

    [Fact]
    public void Is_reentrant_valid_backup_is_reused_missing_one_is_redone()
    {
        var first = DataRootTakeover.Begin(_dir, Dead);
        Assert.Equal(first.Id, DataRootTakeover.Begin(_dir, Dead).Id);

        Directory.Delete(Path.Combine(_dir, first.BackupDir), recursive: true);
        var redone = DataRootTakeover.Begin(_dir, Dead);

        Assert.NotEqual(first.Id, redone.Id);
        Assert.True(File.Exists(Path.Combine(_dir, redone.BackupDir, AgentDatabase.FileName)));
    }

    [Fact]
    public void Commit_appends_an_ownership_mark_idempotently()
    {
        DataRootTakeover.Begin(_dir, Dead);
        var committed = CommitWith();

        Assert.Equal(TakeoverPhase.Committed, committed.Phase);
        Assert.Equal(committed.Id, CommitWith().Id);
        Assert.Equal(new OwnershipRecord("dev_test", committed.Id, committed.BackupDir, committed.CommittedAt!.Value), Ownership.Read(Db));
        Assert.Equal(DataRootKind.Owned, LegacyLayout.Inspect(_dir));
        Assert.Equal(["a", "b", "c"], Keys()); // 旧数据一行不少
        Assert.Equal(TakeoverFailure.Conflict, FailureOf(() => DataRootTakeover.Begin(_dir, Dead)));
    }

    [Fact]
    public void Refuses_to_commit_without_a_verified_backup()
    {
        Assert.Equal(TakeoverFailure.Conflict, FailureOf(() => CommitWith()));
    }

    [Fact]
    public void Rollback_before_commit_restores_backup_sidecars_and_drops_stale_wal()
    {
        var journal = DataRootTakeover.Begin(_dir, Dead);
        Seed("a", "b", "c", "written-after-backup");
        File.WriteAllText(Path.Combine(_dir, "credential.key"), "CHANGED");
        SqliteConnection.ClearAllPools();
        File.WriteAllText(Db + "-wal", "stale");

        var rolled = DataRootTakeover.Rollback(_dir, options: Dead);

        Assert.Equal(TakeoverPhase.RolledBack, rolled.Phase);
        Assert.False(File.Exists(Db + "-wal"));
        Assert.Equal(["a", "b", "c"], Keys());
        Assert.Equal("MASTER-KEY-BYTES", File.ReadAllText(Path.Combine(_dir, "credential.key")));
        Assert.Equal(journal.Id, DataRootTakeover.ReadJournal(_dir)?.Id);
        Assert.Equal(TakeoverFailure.NothingToRollBack, FailureOf(() => DataRootTakeover.Rollback(_dir, options: Dead)));
    }

    [Fact]
    public void Rollback_after_commit_needs_acknowledgement_and_keeps_a_copy_of_post_takeover_data()
    {
        DataRootTakeover.Begin(_dir, Dead);
        var committed = CommitWith();
        Seed("a", "b", "c", "written-by-3.0");

        Assert.Equal(TakeoverFailure.DataLossNotAcknowledged, FailureOf(() => DataRootTakeover.Rollback(_dir, options: Dead)));
        Assert.Contains("written-by-3.0", Keys()); // 没确认就什么都没动

        SqliteConnection.ClearAllPools();
        var rolled = DataRootTakeover.Rollback(_dir, acknowledgeDataLoss: true, options: Dead);

        Assert.Equal(committed.Id, rolled.Id);
        Assert.True(File.Exists(Path.Combine(_dir, rolled.PostTakeoverCopy!))); // 接管后写入的数据另存了,没有销毁
        Assert.Null(Ownership.Read(Db));
        Assert.Equal(DataRootKind.Legacy, LegacyLayout.Inspect(_dir));
        Assert.Equal(["a", "b", "c"], Keys());
    }

    [Fact]
    public void Refuses_to_roll_back_while_a_2x_service_is_running()
    {
        DataRootTakeover.Begin(_dir, Dead);
        File.WriteAllText(Path.Combine(_dir, "service.lock"), """{"pid":4242}""");

        Assert.Equal(TakeoverFailure.LegacyServiceRunning, FailureOf(() => DataRootTakeover.Rollback(_dir, options: Alive)));
    }

    [Fact]
    public void Refuses_to_continue_on_a_corrupt_journal()
    {
        var path = Path.Combine(_dir, DataRootTakeover.JournalFile);
        File.WriteAllText(path, "{ nope");
        Assert.Equal(TakeoverFailure.JournalCorrupt, FailureOf(() => DataRootTakeover.Begin(_dir, Dead)));

        File.WriteAllText(path, """{"v":1,"id":"x","phase":"weird","startedAt":1,"backupDir":"b","sidecars":[]}""");
        Assert.Equal(TakeoverFailure.JournalCorrupt, FailureOf(() => DataRootTakeover.Rollback(_dir, options: Dead)));
    }

    [Fact]
    public void Backup_verification_detects_drift_and_damage()
    {
        var backup = Path.Combine(_dir, "copy.db");
        DatabaseBackup.Create(Db, backup);
        Assert.Null(DatabaseBackup.Verify(Db, backup));

        Seed("a", "b", "c", "d");
        Assert.Equal("backup row count differs for kv", DatabaseBackup.Verify(Db, backup));

        SqliteConnection.ClearAllPools();
        using (var stream = new FileStream(backup, FileMode.Open)) stream.SetLength(100);
        Assert.Matches("could not be opened|integrity", DatabaseBackup.Verify(Db, backup));
    }

    [Fact]
    public void Inspect_tells_fresh_legacy_and_owned_apart()
    {
        var fresh = Path.Combine(_dir, "fresh");
        Assert.Equal(DataRootKind.Fresh, LegacyLayout.Inspect(fresh));
        Assert.Equal(DataRootKind.Legacy, LegacyLayout.Inspect(_dir));
    }

    [Fact]
    public void Flat_2x_layout_is_reported_as_unmigrated()
    {
        var profile = Path.Combine(_dir, "flat");
        Directory.CreateDirectory(profile);
        File.Copy(Db, Path.Combine(profile, AgentDatabase.FileName));

        Assert.True(LegacyLayout.HasUnmigratedLayout(Path.Combine(profile, LegacyLayout.DataSubdirectory)));
        Assert.False(LegacyLayout.HasUnmigratedLayout(_dir));
    }
}
