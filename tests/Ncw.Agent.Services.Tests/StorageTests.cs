using Microsoft.Data.Sqlite;
using Ncw.Agent.Services.Storage;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Sessions;
using Ncw.Core.Domain;

namespace Ncw.Agent.Services.Tests;

/// <summary>
/// 存储维护:统计、整理、附件回收(预览 = 执行)、按年龄 / 清空历史 / 清除本地数据(二次确认令牌)、备份创建与恢复(验证、校验和、主密钥、回滚)、导出导入(合并规则、分块)、
/// 附件登记(消息落盘的同一事务里草稿升为已提交)。真实临时目录 + 真 SQLite + 假时钟;2.x 的 <c>storage-safety.test.ts</c> / <c>attachment-cleanup.test.ts</c> / <c>attachment-commit.test.ts</c> 的不变量逐条对应。
/// </summary>
public sealed class StorageTests : IDisposable
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private readonly TempDatabase _db = new();
    private readonly ManualClock _clock = new(Origin);
    private readonly string _backups = Path.Combine(Path.GetTempPath(), $"ncwbak-{Guid.NewGuid():N}");
    private readonly AttachmentRepository _attachments;
    private readonly SessionRepository _sessions;
    private readonly WorkspaceRepository _workspaces;
    private readonly StorageRepository _storage;
    private readonly BackupService _backup;
    private readonly StorageMaintenance _maintenance;
    private readonly DataTransfer _transfer;
    private bool _active;
    private string? _backupDirectory;

    public StorageTests()
    {
        _backupDirectory = _backups;
        _attachments = new AttachmentRepository(_db.Db, _clock);
        _sessions = new SessionRepository(_db.Db, _clock, _attachments);
        _workspaces = new WorkspaceRepository(_db.Db, _clock);
        _storage = new StorageRepository(_db.Db, _sessions, _attachments, _clock);
        var env = new StorageEnvironment(_db.Db, _db.Root, _clock, () => _active, () => _backupDirectory, "3.0.0-test");
        _backup = new BackupService(env);
        _maintenance = new StorageMaintenance(env, _storage, _attachments, _backup.Tokens, _backup);
        _transfer = new DataTransfer(env, _sessions, _workspaces, _backup.Tokens);
        _db.AddWorkspace("ws");
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_backups))
        {
            Directory.Delete(_backups, recursive: true);
        }
    }

    private string Attachments => Path.Combine(_db.Root, "attachments");

    private string WriteFile(string relative, int size = 10, DateTimeOffset? at = null)
    {
        var path = Path.Combine(Attachments, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private long Ms => _clock.UnixMs;

    private void Session(string id, long updatedAt, string? parent = null, string workspace = "ws") =>
        _sessions.Put(new SessionRecord(id, workspace, parent, "user", $"title {id}", null, "m", null, "code", "auto", "/", null, false, false, updatedAt, updatedAt));

    private void Say(string session, string id, string text, string role = "user", string? partsJson = null) =>
        _sessions.Commit(session, new MessageRecord(id, role, partsJson ?? $"[{{\"type\":\"text\",\"text\":\"{text}\"}}]", Ms));

    private void Draft(string id, string owner, string path, long size = 10, long? createdAt = null, string scope = "session") =>
        _attachments.PutDraft(new AttachmentRow(id, scope, owner, null, null, path, size, "sum-" + id, "draft", id, createdAt ?? Ms));

    // ───────────────────────── 统计 ─────────────────────────

    [Fact]
    public void Stats_count_top_level_conversations_message_bytes_in_utf8_and_the_real_attachment_directory_size()
    {
        Session("s1", Ms);
        Session("s1:sub:1", Ms, parent: "s1"); // 子代理转录不是一条对话
        Say("s1", "m1", "你好");              // 中文:UTF-8 字节不是字符数
        WriteFile("sessions/s1/a.bin", 100);
        WriteFile("themes/t.png", 50);

        var stats = _maintenance.Stats();

        Assert.Equal((1L, 1L), (stats.ConversationCount, stats.MessageCount));
        Assert.True(stats.ConversationBytes > "你好".Length, "bytes are counted as UTF-8");
        Assert.Equal(150, stats.AttachmentBytes);
        Assert.True(stats.DbBytes > 0);
        Assert.Equal(1, stats.WorkspaceCount);
        Assert.Null(stats.LastBackupAt);
    }

    [Fact]
    public void A_symlink_in_the_attachment_directory_is_not_followed_into_an_external_tree()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"ncwout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "big.bin"), new byte[5000]);
        try
        {
            Directory.CreateDirectory(Attachments);
            Directory.CreateSymbolicLink(Path.Combine(Attachments, "link"), outside);
            WriteFile("sessions/s1/a.bin", 10);

            Assert.Equal(10, _maintenance.Stats().AttachmentBytes); // 外部目录的 5000 字节不算本机占用
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Vacuum_refuses_while_an_agent_run_is_active_and_otherwise_returns_fresh_stats()
    {
        _active = true;
        Assert.Equal((StorageFailure.Busy, "storage.busy"), (Assert.Throws<StorageOperationException>(_maintenance.Vacuum).Kind, "storage.busy"));
        _active = false;
        Assert.True(_maintenance.Vacuum().DbBytes > 0);
    }

    // ───────────────────────── 附件回收:预览 = 执行 ─────────────────────────

    [Fact]
    public void Attachment_cleanup_reclaims_fragments_orphans_expired_drafts_and_dangling_rows_and_the_preview_equals_the_execution()
    {
        var day = (long)TimeSpan.FromDays(1).TotalMilliseconds;
        Session("s1", Ms);
        var committedFile = WriteFile("sessions/s1/committed.png");
        _attachments.PutDraft(new AttachmentRow("c1", "session", "s1", null, null, committedFile, 10, "x", "draft", "c", Ms));
        Say("s1", "m1", "see", partsJson: $"[{{\"type\":\"image\",\"mime\":\"image/png\",\"dataRef\":\"ncw://attachments/sessions/s1/committed.png\"}}]");

        WriteFile("sessions/s1/.upload.tmp", 7);                       // 上传中断的残片
        WriteFile("sessions/s1/orphan.png", 20);                       // 磁盘上有、表里没有
        var fresh = WriteFile("sessions/s2/fresh-draft.png", 30);      // 未超期草稿:绝不动
        Draft("d-fresh", "s2", fresh, 30, Ms - 2 * day);
        var stale = WriteFile("sessions/s3/stale-draft.png", 40);      // 超期草稿:文件和行一起收
        Draft("d-stale", "s3", stale, 40, Ms - 8 * day);
        Draft("d-dangling", "s4", Path.Combine(Attachments, "sessions/s4/missing.png"), 5, Ms); // 表里有、磁盘无:只收行
        WriteFile("themes/keep.png", 99);                              // themes / exports 绝不是孤儿
        WriteFile("exports/keep.zip", 99);

        var preview = _maintenance.Preview(CleanupKinds.Attachments, null);
        Assert.Null(preview.ConfirmToken); // 不删用户内容:不需要令牌
        var result = _maintenance.CleanupAttachments();

        Assert.Equal((preview.AttachmentCount, preview.Bytes), (result.Preview.AttachmentCount, result.Preview.Bytes)); // 用户看到的数字就是实际发生的事
        Assert.Equal(4, result.Preview.AttachmentCount); // 残片 + 孤儿 + 超期草稿 + 悬空行
        Assert.Equal(4, result.Deleted);
        Assert.True(File.Exists(committedFile));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(Path.Combine(Attachments, "themes/keep.png")));
        Assert.True(File.Exists(Path.Combine(Attachments, "exports/keep.zip")));
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(Path.Combine(Attachments, "sessions/s1/orphan.png")));
        Assert.False(File.Exists(Path.Combine(Attachments, "sessions/s1/.upload.tmp")));
        Assert.Null(_attachments.Get("d-stale"));
        Assert.Null(_attachments.Get("d-dangling"));
        Assert.NotNull(_attachments.Get("d-fresh"));
        Assert.Equal(0, _maintenance.Preview(CleanupKinds.Attachments, null).AttachmentCount); // 清完再预览 = 没有了
    }

    [Fact]
    public void Attachment_cleanup_refuses_while_an_agent_run_is_active()
    {
        _active = true;
        Assert.Equal(StorageFailure.Busy, Assert.Throws<StorageOperationException>(_maintenance.CleanupAttachments).Kind);
    }

    // ───────────────────────── 按年龄 / 清空历史 ─────────────────────────

    private static readonly long Day = (long)TimeSpan.FromDays(1).TotalMilliseconds;

    private void SeedOldAndNew()
    {
        Session("old", Ms - 40 * Day);
        Session("old:sub:1", Ms - 40 * Day, parent: "old");
        Say("old", "o1", "old question");
        Say("old:sub:1", "o2", "sub transcript");
        var oldFile = WriteFile("sessions/old/a.png", 100);
        _attachments.PutDraft(new AttachmentRow("a-old", "session", "old", null, null, oldFile, 100, "s1", "draft", "a", Ms - 40 * Day));
        Say("old", "o3", "pic", partsJson: "[{\"type\":\"image\",\"mime\":\"image/png\",\"dataRef\":\"ncw://attachments/sessions/old/a.png\"}]");

        Session("new", Ms - Day);
        Say("new", "n1", "recent");
        // 提交消息会把会话的更新时间推到「现在」:把它们放回设定的时间
        Session("old", Ms - 40 * Day);
        Session("old:sub:1", Ms - 40 * Day, parent: "old");
        Session("new", Ms - Day);
    }

    [Fact]
    public void The_by_age_preview_counts_top_level_sessions_but_messages_and_bytes_of_the_whole_subtree_and_execution_matches_it()
    {
        SeedOldAndNew();

        var preview = _maintenance.Preview(CleanupKinds.Age, "1"); // 1 个月前
        Assert.Equal((1L, 3L, 1L), (preview.SessionCount, preview.MessageCount, preview.AttachmentCount)); // 1 条对话(不数子转录);3 条消息含子转录
        Assert.NotNull(preview.ConfirmToken);

        var result = _maintenance.Execute(CleanupKinds.Age, "1", preview.ConfirmToken);

        Assert.Equal((preview.SessionCount, preview.MessageCount, preview.AttachmentCount, preview.Bytes), (result.Preview.SessionCount, result.Preview.MessageCount, result.Preview.AttachmentCount, result.Preview.Bytes));
        Assert.Null(_sessions.Get("old"));
        Assert.Null(_sessions.Get("old:sub:1")); // 子代理转录跟着父一起走
        Assert.NotNull(_sessions.Get("new"));
        Assert.False(File.Exists(Path.Combine(Attachments, "sessions/old/a.png"))); // 级联删行之后,没有引用的受管文件也删
    }

    [Fact]
    public void A_dangerous_cleanup_needs_the_token_from_a_preview_of_the_same_operation_which_is_single_use_and_expires()
    {
        SeedOldAndNew();
        var preview = _maintenance.Preview(CleanupKinds.Age, "1");

        // 缺失 / 随便写的 / 别的操作的令牌:都不行,什么都没删
        Assert.Equal("storage.confirmationRequired", Assert.Throws<StorageOperationException>(() => _maintenance.Execute(CleanupKinds.Age, "1", null)).Key);
        Assert.Equal(StorageFailure.Unconfirmed, Assert.Throws<StorageOperationException>(() => _maintenance.Execute(CleanupKinds.Age, "1", "deadbeef")).Kind);
        Assert.Equal(StorageFailure.Unconfirmed, Assert.Throws<StorageOperationException>(() => _maintenance.Execute(CleanupKinds.Age, "3", preview.ConfirmToken)).Kind); // 参数不同 = 另一次操作
        Assert.Equal(StorageFailure.Unconfirmed, Assert.Throws<StorageOperationException>(() => _maintenance.Execute(CleanupKinds.History, null, preview.ConfirmToken)).Kind);
        Assert.NotNull(_sessions.Get("old"));

        // 过期:10 分钟
        _clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(StorageFailure.Unconfirmed, Assert.Throws<StorageOperationException>(() => _maintenance.Execute(CleanupKinds.Age, "1", preview.ConfirmToken)).Kind);

        // 新令牌可用一次
        var fresh = _maintenance.Preview(CleanupKinds.Age, "1");
        _maintenance.Execute(CleanupKinds.Age, "1", fresh.ConfirmToken);
        Assert.Equal(StorageFailure.Unconfirmed, Assert.Throws<StorageOperationException>(() => _maintenance.Execute(CleanupKinds.Age, "1", fresh.ConfirmToken)).Kind);
    }

    [Fact]
    public void An_unknown_kind_or_age_is_invalid_and_a_busy_agent_blocks_the_execution_even_with_a_valid_token()
    {
        Assert.Equal("storage.invalid.kind", Assert.Throws<StorageOperationException>(() => _maintenance.Preview("everything", null)).Key);
        Assert.Equal("storage.invalid.age", Assert.Throws<StorageOperationException>(() => _maintenance.Preview(CleanupKinds.Age, "2")).Key);
        Assert.Equal("storage.invalid.kind", Assert.Throws<StorageOperationException>(() => _maintenance.Execute(CleanupKinds.Attachments, null, "x")).Key);

        var preview = _maintenance.Preview(CleanupKinds.History, null);
        _active = true;
        Assert.Equal(StorageFailure.Busy, Assert.Throws<StorageOperationException>(() => _maintenance.Execute(CleanupKinds.History, null, preview.ConfirmToken)).Kind);
        _active = false;
        _maintenance.Execute(CleanupKinds.History, null, preview.ConfirmToken); // 忙的时候没有消耗令牌
    }

    [Fact]
    public void Clearing_history_keeps_workspaces_theme_attachments_and_the_usage_ledger_and_removes_unreachable_drafts()
    {
        SeedOldAndNew();
        WriteFile("themes/wall.png", 70);
        _attachments.PutDraft(new AttachmentRow("theme-1", "theme", null, null, null, Path.Combine(Attachments, "themes/wall.png"), 70, "t", "committed", "wall", Ms));
        new KvStore(_db.Db).Set("session.input.new", "\"draft text\"");
        new KvStore(_db.Db).Set("v3.keep", "1");
        new UsageRepository(_db.Db).Insert(new UsageRow("u1", Ms, "r", "ws", "new", 1, "p", "P", "anthropic", "e", "a", "m", null, 1, 1, 0, 0, 0, null, 1, null, true, 200, null, null, null, 0, null, null, null, null));

        var preview = _maintenance.Preview(CleanupKinds.History, null);
        Assert.Equal(2L, preview.SessionCount); // 顶层 old + new
        var result = _maintenance.Execute(CleanupKinds.History, null, preview.ConfirmToken);

        Assert.True(result.Deleted > 0);
        Assert.Empty(_sessions.ListAll(topLevelOnly: false));
        Assert.NotNull(_workspaces.Get("ws"));
        Assert.NotNull(_attachments.Get("theme-1"));
        Assert.True(File.Exists(Path.Combine(Attachments, "themes/wall.png")));
        Assert.Null(new KvStore(_db.Db).Get("session.input.new")); // 草稿 / 排队输入在 kv 里,不清会残留在重用的 id 上复活
        Assert.Equal("1", new KvStore(_db.Db).Get("v3.keep"));
        Assert.Equal(1, new UsageRepository(_db.Db).Summary(new UsageWindow(null, Ms + 1)).RequestCount); // 用量账不是对话历史
        Assert.Equal(0, _db.Db.Count0("SELECT COUNT(*) FROM messages_fts"));
    }

    [Fact]
    public void Clearing_local_data_removes_user_content_and_files_but_keeps_the_trust_of_paired_controllers()
    {
        SeedOldAndNew();
        new ControllerTrustRepository(_db.Db).Put(new ControllerTrust("fp1", "iPhone", "member", Ms, null), "route-hash");
        new SettingsRepository(_db.Db).WriteJson("{\"maxOutputTokens\":8000}");
        new KvStore(_db.Db).Set("anything", "1");

        var preview = _maintenance.Preview(CleanupKinds.LocalData, null);
        var result = _maintenance.Execute(CleanupKinds.LocalData, null, preview.ConfirmToken);

        Assert.Empty(result.Preview.Undeletable);
        Assert.Empty(_workspaces.List());
        Assert.Empty(_sessions.ListAll(topLevelOnly: false));
        Assert.Null(new SettingsRepository(_db.Db).ReadJson());
        Assert.Null(new KvStore(_db.Db).Get("anything"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Attachments));
        Assert.Single(new ControllerTrustRepository(_db.Db).List()); // 清掉它们等于把用户从自己的设备上锁出去
    }

    // ───────────────────────── 备份 ─────────────────────────

    private string CreateBackup(bool manual = true) => _backup.Create(manual).LastBackupPath!;

    [Fact]
    public void A_backup_is_a_zip_with_a_2x_compatible_manifest_whose_numbers_match_the_database_inside_it()
    {
        Session("s1", Ms);
        Say("s1", "m1", "hello");
        var path = CreateBackup();

        Assert.EndsWith(".ncwbackup", path);
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        Assert.Equal(["database.sqlite", "manifest.json", "settings.json"], zip.Entries.Select(e => e.FullName).Order());
        using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(reader.ReadToEnd())!;
        Assert.Equal("nextcowork-backup", (string?)manifest["format"]);
        Assert.Equal(2, (int?)manifest["formatVersion"]);
        Assert.Equal((1, 1, AgentDatabase.LatestVersion), ((int?)manifest["sessionCount"], (int?)manifest["messageCount"], (int?)manifest["schemaVersion"]));
        Assert.Equal(64, ((string?)manifest["databaseSha256"])!.Length);
        Assert.Equal(Origin.ToUnixTimeMilliseconds(), _backup.Status().LastBackupAt);
    }

    [Fact]
    public void Manual_backups_get_unique_names_and_are_never_cleaned_while_automatic_ones_keep_one_fixed_file()
    {
        var first = CreateBackup();
        var second = CreateBackup();
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first) && File.Exists(second));

        var auto1 = CreateBackup(manual: false);
        var auto2 = CreateBackup(manual: false);
        Assert.Equal(auto1, auto2);
        Assert.Equal(BackupService.AutoName, Path.GetFileName(auto1));
        Assert.True(File.Exists(first)); // 自动备份只管理自己的固定文件
    }

    [Fact]
    public void The_backup_directory_must_be_set_absolute_writable_and_outside_the_data_root()
    {
        _backupDirectory = null;
        Assert.Equal("storage.backup.noDirectory", Assert.Throws<StorageOperationException>(() => _backup.Create()).Key);
        _backupDirectory = "relative/dir";
        Assert.Equal("storage.backup.directoryInvalid", Assert.Throws<StorageOperationException>(() => _backup.Create()).Key);
        _backupDirectory = Path.Combine(_db.Root, "inside");
        Assert.Equal("storage.backup.directoryOverlaps", Assert.Throws<StorageOperationException>(() => _backup.Create()).Key);
        _backupDirectory = _db.Root; // 数据根本身
        Assert.Equal("storage.backup.directoryOverlaps", Assert.Throws<StorageOperationException>(() => _backup.Create()).Key);
        Assert.NotNull(_backup.Status().LastError); // 失败记在状态里
    }

    [Fact]
    public void Restoring_replaces_the_whole_database_in_place_after_verification_and_a_token_from_a_preview_of_that_file()
    {
        Session("before", Ms);
        Say("before", "m1", "kept");
        var path = CreateBackup();
        Session("after", Ms);
        Say("after", "m2", "added later");
        new SettingsRepository(_db.Db).WriteJson("{\"maxOutputTokens\":8000}");

        var preview = _backup.PreviewRestore(path);
        Assert.Equal((1L, 1L), (preview.Manifest.SessionCount, preview.Manifest.MessageCount));
        Assert.Equal(StorageFailure.Unconfirmed, Assert.Throws<StorageOperationException>(() => _backup.Restore(path, "wrong")).Kind);
        Assert.NotNull(_sessions.Get("after")); // 令牌不对:什么都没动

        _backup.Restore(path, preview.ConfirmToken);

        Assert.NotNull(_sessions.Get("before"));
        Assert.Null(_sessions.Get("after"));
        Assert.Equal(["m1"], _sessions.History("before").Select(m => m.Id));
        Assert.Null(new SettingsRepository(_db.Db).ReadJson()); // 设置也回到备份时的样子
        Session("works-after", Ms); // 连接仍然可用,没有被关掉重开
        Assert.Equal(2, _sessions.ListAll(topLevelOnly: true).Count);
        Assert.Equal(AgentDatabase.LatestVersion, _db.Db.UserVersion());
        Assert.Equal(1, _db.Db.Count0("SELECT COUNT(*) FROM messages_fts")); // 全文索引与消息对得上
    }

    [Fact]
    public void A_restore_is_refused_for_a_tampered_database_a_wrong_manifest_a_missing_file_and_while_an_agent_run_is_active()
    {
        Session("s", Ms);
        var path = CreateBackup();

        // 篡改归档里的数据库:校验和对不上
        var tampered = Path.Combine(_backups, "tampered.ncwbackup");
        File.Copy(path, tampered);
        using (var zip = System.IO.Compression.ZipFile.Open(tampered, System.IO.Compression.ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("database.sqlite")!;
            using var stream = entry.Open();
            stream.Seek(100, SeekOrigin.Begin);
            stream.WriteByte(0xFF);
        }

        Assert.Equal("storage.backup.checksum", Assert.Throws<StorageOperationException>(() => _backup.PreviewRestore(tampered)).Key);
        Assert.Equal("storage.backup.notFound", Assert.Throws<StorageOperationException>(() => _backup.PreviewRestore(Path.Combine(_backups, "nope.ncwbackup"))).Key);
        File.WriteAllText(Path.Combine(_backups, "junk.ncwbackup"), "not a zip");
        Assert.Equal("storage.backup.unreadable", Assert.Throws<StorageOperationException>(() => _backup.PreviewRestore(Path.Combine(_backups, "junk.ncwbackup"))).Key);

        var preview = _backup.PreviewRestore(path);
        _active = true;
        Assert.Equal(StorageFailure.Busy, Assert.Throws<StorageOperationException>(() => _backup.Restore(path, preview.ConfirmToken)).Kind);
    }

    [Fact]
    public void A_backup_with_encrypted_credentials_carries_the_master_key_and_a_restore_puts_the_key_back()
    {
        var keyPath = Path.Combine(_db.Root, Ncw.Agent.Storage.Credentials.CredentialCrypto.MasterKeyFileName);
        var credentials = new Ncw.Agent.Storage.Credentials.CredentialStore(_db.Db, _db.Root, Ncw.Agent.Storage.Credentials.NoLegacySecrets.Instance);
        credentials.Set("provider:p1", "sk-secret");
        var key = File.ReadAllBytes(keyPath);
        var path = CreateBackup();
        using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
        {
            Assert.NotNull(zip.GetEntry("credential.key"));
        }

        credentials.Set("provider:p2", "sk-later");
        File.WriteAllBytes(keyPath, new byte[32]); // 钥匙被换掉了
        var preview = _backup.PreviewRestore(path);
        _backup.Restore(path, preview.ConfirmToken);

        Assert.Equal(key, File.ReadAllBytes(keyPath));
        Assert.Equal("sk-secret", credentials.Get("provider:p1"));
        Assert.Null(credentials.Get("provider:p2"));
    }

    // ───────────────────────── 导出导入 ─────────────────────────

    [Fact]
    public void An_export_imports_into_another_database_with_the_2x_merge_rules_and_chunked_transfer()
    {
        Session("s1", Ms - 100);
        Say("s1", "m1", "alpha");
        Session("s2", Ms - 100);
        Say("s2", "m2", "beta");
        var export = _transfer.CreateExport();
        Assert.Equal((1L, 2L, 2L), (export.Workspaces, export.Sessions, export.Messages));
        var bytes = ReadAll(export);

        using var other = new TempDatabase();
        var env = new StorageEnvironment(other.Db, other.Root, _clock, () => false, () => null, "x");
        var otherSessions = new SessionRepository(other.Db, _clock, new AttachmentRepository(other.Db, _clock));
        var otherWorkspaces = new WorkspaceRepository(other.Db, _clock);
        var tokens = new ConfirmTokens(_clock);
        var importer = new DataTransfer(env, otherSessions, otherWorkspaces, tokens);
        // 对方已经有 s2(更新的):保留本地;还有一个只在对方有的会话:不受影响
        other.AddWorkspace("ws");
        otherSessions.Put(new SessionRecord("s2", "ws", null, "user", "newer local", null, "m", null, "code", "auto", "/", null, false, false, Ms, Ms + 1000));
        otherSessions.Put(new SessionRecord("only-there", "ws", null, "user", "t", null, "m", null, "code", "auto", "/", null, false, false, Ms, Ms));

        var upload = Upload(importer, bytes, chunk: 64);
        Assert.Equal(bytes.Length, upload.Received);
        var preview = importer.PreviewImport(upload.UploadId);
        Assert.Equal((2L, 1L, 0L, 2L), (preview.Sessions, preview.NewCount, preview.OverwriteCount, preview.SkippedCount)); // s1 新增;s2(本地更新)和 ws(本地已有)保留本地

        Assert.Equal(StorageFailure.Unconfirmed, Assert.Throws<StorageOperationException>(() => importer.ApplyImport(upload.UploadId, "nope")).Kind);
        var result = importer.ApplyImport(upload.UploadId, preview.ConfirmToken);

        Assert.Equal(1, result.SessionsImported);
        Assert.Equal(["m1"], otherSessions.History("s1").Select(m => m.Id));
        Assert.Equal("newer local", otherSessions.Get("s2")!.Title); // 本地更新:不覆盖
        Assert.NotNull(otherSessions.Get("only-there"));
        Assert.Equal(StorageFailure.NotFound, Assert.Throws<StorageOperationException>(() => importer.PreviewImport(upload.UploadId)).Kind); // 应用之后上传文件已丢弃
    }

    [Fact]
    public void A_newer_imported_session_overwrites_a_local_one_but_an_older_or_untimestamped_one_never_does()
    {
        Assert.Equal("new", DataTransfer.MergeDecision(null, false, 5));
        Assert.Equal("overwrite", DataTransfer.MergeDecision(5, true, 6));
        Assert.Equal("skip", DataTransfer.MergeDecision(6, true, 5));
        Assert.Equal("skip", DataTransfer.MergeDecision(5, true, 5));
        Assert.Equal("skip", DataTransfer.MergeDecision(null, true, 6)); // 缺时间戳:保留本地
        Assert.Equal("skip", DataTransfer.MergeDecision(5, true, null));
    }

    [Fact]
    public void Chunks_must_arrive_in_order_a_resent_chunk_is_idempotent_and_ids_cannot_be_paths()
    {
        var first = _transfer.WriteImport(null, 0, new byte[10]);
        Assert.Equal(10, first.Received);
        Assert.Equal(10, _transfer.WriteImport(first.UploadId, 0, new byte[10]).Received); // 重发同一块:不重复追加
        Assert.Equal(20, _transfer.WriteImport(first.UploadId, 10, new byte[10]).Received);
        Assert.Equal("storage.transfer.offset", Assert.Throws<StorageOperationException>(() => _transfer.WriteImport(first.UploadId, 50, new byte[1])).Key); // 乱序
        Assert.Equal("storage.transfer.invalidId", Assert.Throws<StorageOperationException>(() => _transfer.WriteImport("../../etc/passwd", 0, new byte[1])).Key);
        Assert.Equal("storage.transfer.chunkTooLarge", Assert.Throws<StorageOperationException>(() => _transfer.WriteImport(null, 0, new byte[DataTransfer.MaxChunkBytes + 1])).Key);
        Assert.Equal("storage.import.notJson", Assert.Throws<StorageOperationException>(() => _transfer.PreviewImport(first.UploadId)).Key);
    }

    private byte[] ReadAll(ExportInfo export)
    {
        using var ms = new MemoryStream();
        long offset = 0;
        while (true)
        {
            var chunk = _transfer.ReadExport(export.ExportId, offset, 128);
            ms.Write(chunk.Data);
            offset += chunk.Data.Length;
            if (chunk.Eof)
            {
                break;
            }
        }

        _transfer.DiscardExport(export.ExportId);
        Assert.Equal(export.Bytes, ms.Length);
        return ms.ToArray();
    }

    private static ImportUpload Upload(DataTransfer importer, byte[] bytes, int chunk)
    {
        ImportUpload? upload = null;
        for (var offset = 0; offset < bytes.Length; offset += chunk)
        {
            upload = importer.WriteImport(upload?.UploadId, offset, bytes[offset..Math.Min(bytes.Length, offset + chunk)]);
        }

        return upload!;
    }

    // ───────────────────────── 附件登记(消息落盘的同一事务) ─────────────────────────

    [Fact]
    public void A_user_message_that_references_a_draft_commits_it_in_the_same_transaction_and_replays_are_idempotent()
    {
        Session("s1", Ms);
        var file = WriteFile("sessions/s1/pic.png", 12);
        Draft("a1", "s1", file, 12);
        var parts = "[{\"type\":\"image\",\"mime\":\"image/png\",\"dataRef\":\"ncw://attachments/sessions/s1/pic.png\"}]";

        Say("s1", "m1", "", partsJson: parts);

        var row = _attachments.Get("a1")!;
        Assert.Equal(("committed", "m1", "s1"), (row.Status, row.MessageId, row.SessionId));
        Say("s1", "m1", "", partsJson: parts); // 同一条消息重放
        Assert.Single(_attachments.ForMessage("m1"));
        Assert.Empty(_attachments.ListDrafts("s1")); // 不再是草稿
    }

    [Fact]
    public void An_attachment_already_committed_to_another_message_is_referenced_not_moved_and_other_sessions_attachments_are_never_claimed()
    {
        Session("s1", Ms);
        Session("s2", Ms);
        var file = WriteFile("sessions/s1/pic.png", 12);
        Draft("a1", "s1", file, 12);
        var ref1 = "[{\"type\":\"image\",\"mime\":\"image/png\",\"dataRef\":\"ncw://attachments/sessions/s1/pic.png\"}]";
        Say("s1", "m1", "", partsJson: ref1);
        Say("s1", "m2", "", partsJson: ref1); // 第二条消息引用同一个文件

        Assert.Equal("m1", _attachments.Get("a1")!.MessageId); // 不挪
        var second = Assert.Single(_attachments.ForMessage("m2"));
        Assert.Equal(("m2:managed:0", file), (second.Id, second.Path));
        Assert.Equal(2, _attachments.ReferenceCount(file)); // 按路径引用计数

        // s2 的消息引用 s1 的附件:不认领(owner 不是本会话)
        Say("s2", "m3", "", partsJson: ref1);
        Assert.Empty(_attachments.ForMessage("m3"));
    }

    [Fact]
    public void Draft_listing_is_scoped_to_the_account_that_owns_the_session_but_a_draft_before_its_session_row_exists_is_visible()
    {
        var file = WriteFile("sessions/future/pic.png", 12);
        Draft("a1", "future", file, 12); // 会话行还不存在(用户新建对话、传了图、还没发送)
        Assert.Single(_attachments.ListDrafts("future"));

        // 同一个 id 的会话属于别的账户作用域的工作区:不可见
        _db.Db.Run0("INSERT INTO workspaces (id, last_opened_at, json, owner) VALUES ('other-ws', 0, '{\"id\":\"other-ws\"}', 'acct-2')");
        _db.Db.Run0("INSERT INTO sessions (id, workspace_id, title, model, mode, thinking, root_path_at_creation, created_at, updated_at, json) VALUES ('future', 'other-ws', 't', 'm', 'code', 'auto', '/', 0, 0, '{}')");
        Assert.Empty(_attachments.ListDrafts("future"));
        Assert.Null(_attachments.Get("a1"));
    }

    [Fact]
    public void Confirm_tokens_are_bound_to_one_operation_single_use_and_expire()
    {
        var tokens = new ConfirmTokens(_clock);
        var token = tokens.Issue("a");
        Assert.False(tokens.Redeem(token, "b"));
        Assert.True(tokens.Redeem(token, "a"));
        Assert.False(tokens.Redeem(token, "a"));
        var late = tokens.Issue("a");
        _clock.Advance(ConfirmTokens.Lifetime + TimeSpan.FromSeconds(1));
        Assert.False(tokens.Redeem(late, "a"));
        Assert.False(tokens.Redeem(null, "a"));
        Assert.False(tokens.Redeem("", "a"));
    }

    [Fact]
    public void Attachment_locators_are_pure_functions_that_reject_escapes()
    {
        var loc = new AttachmentLocator("session", "s1", "01ABC.png");
        Assert.Equal("sessions/s1/01ABC.png", Core.Domain.Attachments.RelativePath(loc));
        Assert.Equal("ncw://attachments/sessions/s1/01ABC.png", Core.Domain.Attachments.BuildUrl(loc));
        Assert.Equal(loc, Core.Domain.Attachments.ParseUrl("ncw://attachments/sessions/s1/01ABC.png"));
        Assert.Equal(new AttachmentLocator("theme", null, "w.png"), Core.Domain.Attachments.ParseUrl("ncw://attachments/themes/w.png"));
        foreach (var bad in new[]
        {
            "ncw://attachments/sessions/%2e%2e/x/y.png", "ncw://attachments/sessions/s1/%2e%2e%2fsecret", "ncw://attachments/sessions/s1/a%2fb.png", "ncw://attachments/sessions//y.png",
            "ncw://attachments/themes/s1/w.png", "ncw://attachments/sessions/w.png", "ncw://other/sessions/s1/a.png", "http://attachments/sessions/s1/a.png", "ncw://attachments/sessions/s1/a%00.png",
            "ncw://attachments/unknown/s1/a.png", "ncw://attachments/sessions/s1/%zz", "not a url",
        })
        {
            Assert.Null(Core.Domain.Attachments.ParseUrl(bad));
        }

        Assert.Null(Core.Domain.Attachments.RelativePath(new AttachmentLocator("session", "..", "a.png")));
        Assert.Null(Core.Domain.Attachments.RelativePath(new AttachmentLocator("theme", "s1", "a.png"))); // 无主 scope 带了 owner:调用方搞错了
        Assert.Equal(".bin", Core.Domain.Attachments.ExtOfMime("application/x-unknown"));
        Assert.Equal("image/jpeg", Core.Domain.Attachments.NormalizeImageMime("IMAGE/JPG; charset=x"));
        Assert.Equal("image/png", Core.Domain.Attachments.ImageMimeOfBytes([137, 80, 78, 71, 13, 10, 26, 10, 0]));
        Assert.Equal("image/webp", Core.Domain.Attachments.ImageMimeOfBytes("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(Core.Domain.Attachments.ImageMimeOfBytes([1, 2, 3]));
    }
}

internal static class StorageTestExtensions
{
    public static void Run0(this AgentDatabase db, string sql)
    {
        using var command = db.Connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static long Count0(this AgentDatabase db, string sql)
    {
        using var command = db.Connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
