using Ncw.Agent.Host.Tests.Support;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 存储维护经协议的端到端(真实 Agent + 真实 DeviceClient + 局域网 TLS 承载):<b>危险操作的角色 / 通道 / 二次确认限制</b>——恢复备份、按年龄清理、清空历史、清除本地数据、应用导入
/// 必须同时是 owner + 本机通道 + 出示预览签发的确认令牌;远程 owner 只能做不丢用户内容的维护(统计、整理、备份、回收附件、导出);member / viewer 更少。
/// 也验证正常流程真的有效:备份 → 改动 → 恢复回到备份时的样子,清理数字 = 预览数字,导出导入经分块传输。
/// </summary>
public sealed class StorageProtocolTests : IAsyncLifetime
{
    private readonly ConversationRig _rig = new();
    private readonly string _backups = Path.Combine(Path.GetTempPath(), $"ncwbk-{Guid.NewGuid():N}");
    private DeviceClient _owner = null!;
    private ProjectDto _project = null!;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        _project = await _rig.AddProjectAsync(_owner);
        _port = (await _owner.CallAsync(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(true))).Port!.Value;
        await _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(Data: new DataSettingsPatchDto(BackupDirectory: _backups))));
    }

    public async ValueTask DisposeAsync()
    {
        await _rig.DisposeAsync();
        if (Directory.Exists(_backups))
        {
            Directory.Delete(_backups, recursive: true);
        }
    }

    private async Task<DeviceClient> Connect(string name, string role)
    {
        var identity = PeerIdentity.Create(name);
        var paired = await RemoteDevices.PairAsync("127.0.0.1", _port, identity, _rig.Host.Remote.PairingCode!, name);
        await _owner.CallAsync(DeviceMethods.TrustSetRole, new TrustSetRoleParams(identity.FingerprintHex, role));
        var stream = await RemoteDevices.ConnectAsync("127.0.0.1", _port, identity, paired.DeviceFingerprint);
        return await DeviceClient.ConnectAsync(stream, new ClientHello(name, "3.0.0"));
    }

    private static async Task<ProtocolException> Failure<T>(Func<Task<T>> call) => await Assert.ThrowsAsync<ProtocolException>(call);

    private static string KeyOf(ProtocolException e) => FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key;

    private async Task<SessionDto> ConversationAsync(string text, string reply)
    {
        _rig.Llm.Then(Up.Say(reply));
        var session = await _rig.CreateSessionAsync(_owner, _project);
        var start = await _rig.StartAsync(_owner, session.Id, text);
        await (await RunWatch.OpenAsync(_owner, start.RunId)).SettledAsync();
        return session;
    }

    // ───────────────────────── 正常流程 ─────────────────────────

    [Fact]
    public async Task Backup_then_change_then_restore_brings_the_device_back_to_the_backup_and_the_stats_follow()
    {
        var first = await ConversationAsync("first question", "first answer");
        var stats = await _owner.CallAsync(StorageMethods.GetStats, Unit.Value);
        Assert.Equal((1L, 2L, 1L), (stats.ConversationCount, stats.MessageCount, stats.WorkspaceCount));

        var status = await _owner.CallAsync(StorageMethods.BackupCreate, new BackupCreateParams());
        Assert.Equal(_backups, status.Directory);
        Assert.NotNull(status.LastBackupAt);
        Assert.Null(status.LastError);

        var second = await ConversationAsync("second question", "second answer");
        Assert.Equal(2L, (await _owner.CallAsync(StorageMethods.GetStats, Unit.Value)).ConversationCount);

        var preview = await _owner.CallAsync(StorageMethods.BackupRestorePreview, new RestorePreviewParams(status.LastBackupPath!));
        Assert.Equal((1L, 2L), (preview.Manifest.SessionCount, preview.Manifest.MessageCount));
        await _owner.CallAsync(StorageMethods.BackupRestore, new RestoreParams(status.LastBackupPath!, preview.ConfirmToken));

        var sessions = (await _owner.CallAsync(ConversationMethods.SessionsList, new SessionsListParams(_project.WorkspaceId))).Sessions;
        Assert.Equal([first.Id], sessions.Select(s => s.Id));
        Assert.Equal(ErrorCodes.NotFound, (await Failure(() => _owner.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(second.Id)))).Code);
        Assert.Equal(1L, (await _owner.CallAsync(StorageMethods.GetStats, Unit.Value)).ConversationCount);
        // 恢复之后设备照常工作:还能发起新的对话
        await ConversationAsync("third question", "third answer");
        Assert.Equal(2L, (await _owner.CallAsync(StorageMethods.GetStats, Unit.Value)).ConversationCount);
    }

    [Fact]
    public async Task Cleanup_numbers_equal_the_preview_and_a_dangerous_cleanup_needs_the_token()
    {
        await ConversationAsync("q1", "a1");
        await ConversationAsync("q2", "a2");

        var preview = await _owner.CallAsync(StorageMethods.CleanupPreview, new CleanupPreviewParams("history"));
        Assert.Equal((2L, 4L), (preview.SessionCount, preview.MessageCount));
        Assert.NotNull(preview.ConfirmToken);

        // 缺令牌 / 随便写的令牌:forbidden + 稳定 key,什么都没删
        var missing = await Failure(() => _owner.CallAsync(StorageMethods.CleanupExecute, new CleanupExecuteParams("history", null, "")));
        Assert.Equal((ErrorCodes.Forbidden, "storage.confirmationRequired"), (missing.Code, KeyOf(missing)));
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => _owner.CallAsync(StorageMethods.CleanupExecute, new CleanupExecuteParams("history", null, "deadbeef")))).Code);
        Assert.Equal(2L, (await _owner.CallAsync(StorageMethods.GetStats, Unit.Value)).ConversationCount);

        var result = await _owner.CallAsync(StorageMethods.CleanupExecute, new CleanupExecuteParams("history", null, preview.ConfirmToken!));
        Assert.Equal((preview.SessionCount, preview.MessageCount), (result.Preview.SessionCount, result.Preview.MessageCount));
        Assert.Equal(0L, (await _owner.CallAsync(StorageMethods.GetStats, Unit.Value)).ConversationCount);
        Assert.Single((await _owner.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects); // 项目不受影响
    }

    [Fact]
    public async Task An_export_is_read_in_chunks_and_an_import_is_uploaded_in_chunks_then_previewed_confirmed_and_applied()
    {
        await ConversationAsync("exported question", "exported answer");
        var info = await _owner.CallAsync(StorageMethods.ExportCreate, Unit.Value);
        var bytes = new List<byte>();
        while (true)
        {
            var chunk = await _owner.CallAsync(StorageMethods.ExportRead, new ExportReadParams(info.ExportId, bytes.Count, 256));
            bytes.AddRange(chunk.Data);
            if (chunk.Eof)
            {
                break;
            }
        }

        Assert.Equal(info.Bytes, bytes.Count);
        await _owner.CallAsync(StorageMethods.ExportDiscard, new ExportRefParams(info.ExportId));
        Assert.Equal(ErrorCodes.NotFound, (await Failure(() => _owner.CallAsync(StorageMethods.ExportRead, new ExportReadParams(info.ExportId, 0, 10)))).Code);

        // 清空历史后再导入:会话回来了
        var clear = await _owner.CallAsync(StorageMethods.CleanupPreview, new CleanupPreviewParams("history"));
        await _owner.CallAsync(StorageMethods.CleanupExecute, new CleanupExecuteParams("history", null, clear.ConfirmToken!));
        string? uploadId = null;
        for (var offset = 0; offset < bytes.Count; offset += 300)
        {
            var part = bytes.Skip(offset).Take(300).ToArray();
            uploadId = (await _owner.CallAsync(StorageMethods.ImportWrite, new ImportWriteParams(uploadId, offset, part))).UploadId;
        }

        var preview = await _owner.CallAsync(StorageMethods.ImportPreview, new ImportRefParams(uploadId!));
        Assert.Equal((1L, 1L), (preview.Sessions, preview.NewCount));
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => _owner.CallAsync(StorageMethods.ImportApply, new ImportApplyParams(uploadId!, "nope")))).Code);
        var result = await _owner.CallAsync(StorageMethods.ImportApply, new ImportApplyParams(uploadId!, preview.ConfirmToken));

        Assert.Equal(1L, result.SessionsImported);
        var sessions = (await _owner.CallAsync(ConversationMethods.SessionsList, new SessionsListParams(_project.WorkspaceId))).Sessions;
        var back = await _owner.CallAsync(ConversationMethods.SessionsGet, new SessionRefParams(Assert.Single(sessions).Id));
        Assert.Equal(2, back.Messages.Length);
    }

    // ───────────────────────── 角色与承载 ─────────────────────────

    [Fact]
    public async Task A_remote_owner_may_do_non_destructive_maintenance_but_every_dangerous_operation_is_forbidden_over_the_network()
    {
        await ConversationAsync("q", "a");
        await using var remote = await Connect("remote-owner", "owner");

        // 可以:统计、整理、备份、回收附件、导出、清理预览
        Assert.Equal(1L, (await remote.CallAsync(StorageMethods.GetStats, Unit.Value)).ConversationCount);
        Assert.True((await remote.CallAsync(StorageMethods.Vacuum, Unit.Value)).DbBytes > 0);
        var backup = await remote.CallAsync(StorageMethods.BackupCreate, new BackupCreateParams());
        Assert.NotNull(backup.LastBackupPath);
        Assert.NotNull(await remote.CallAsync(StorageMethods.CleanupAttachments, Unit.Value));
        var preview = await remote.CallAsync(StorageMethods.CleanupPreview, new CleanupPreviewParams("history"));
        var export = await remote.CallAsync(StorageMethods.ExportCreate, Unit.Value);
        await remote.CallAsync(StorageMethods.ExportDiscard, new ExportRefParams(export.ExportId));

        // 不行:即使手里有从预览拿到的合法令牌,通道不对就是 forbidden(路由统一检查,不进方法)
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => remote.CallAsync(StorageMethods.CleanupExecute, new CleanupExecuteParams("history", null, preview.ConfirmToken!)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => remote.CallAsync(StorageMethods.BackupRestorePreview, new RestorePreviewParams(backup.LastBackupPath!)))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => remote.CallAsync(StorageMethods.BackupRestore, new RestoreParams(backup.LastBackupPath!, "x")))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => remote.CallAsync(StorageMethods.ImportWrite, new ImportWriteParams(null, 0, [1])))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => remote.CallAsync(StorageMethods.ImportApply, new ImportApplyParams("a", "b")))).Code);
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => remote.CallAsync(StorageMethods.ImportPreview, new ImportRefParams("a")))).Code);
        foreach (var kind in new[] { "age", "local-data" })
        {
            Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => remote.CallAsync(StorageMethods.CleanupExecute, new CleanupExecuteParams(kind, "1", "x")))).Code);
        }

        Assert.Equal(1L, (await _owner.CallAsync(StorageMethods.GetStats, Unit.Value)).ConversationCount); // 什么都没丢
    }

    [Fact]
    public async Task Members_and_viewers_get_less_a_member_reads_stats_a_viewer_nothing_and_neither_can_change_anything()
    {
        await using var member = await Connect("member-st", "member");
        await using var viewer = await Connect("viewer-st", "viewer");

        Assert.NotNull(await member.CallAsync(StorageMethods.GetStats, Unit.Value));
        Assert.NotNull(await member.CallAsync(StorageMethods.BackupStatus, Unit.Value));
        Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => viewer.CallAsync(StorageMethods.GetStats, Unit.Value))).Code);
        foreach (var client in new[] { member, viewer })
        {
            Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => client.CallAsync(StorageMethods.Vacuum, Unit.Value))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => client.CallAsync(StorageMethods.BackupCreate, new BackupCreateParams()))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => client.CallAsync(StorageMethods.CleanupAttachments, Unit.Value))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => client.CallAsync(StorageMethods.CleanupPreview, new CleanupPreviewParams("history")))).Code);
            Assert.Equal(ErrorCodes.Forbidden, (await Failure(() => client.CallAsync(StorageMethods.ExportCreate, Unit.Value))).Code);
        }
    }

    [Fact]
    public async Task Business_failures_have_stable_keys_a_missing_backup_directory_a_missing_file_an_unknown_kind()
    {
        await _owner.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(new DeviceSettingsPatchDto(Data: new DataSettingsPatchDto(BackupDirectory: ""))));
        var noDirectory = await Failure(() => _owner.CallAsync(StorageMethods.BackupCreate, new BackupCreateParams()));
        Assert.Equal((ErrorCodes.InvalidRequest, "storage.backup.noDirectory"), (noDirectory.Code, KeyOf(noDirectory)));

        var missing = await Failure(() => _owner.CallAsync(StorageMethods.BackupRestorePreview, new RestorePreviewParams(Path.Combine(_backups, "nope.ncwbackup"))));
        Assert.Equal((ErrorCodes.NotFound, "storage.backup.notFound"), (missing.Code, KeyOf(missing)));

        var kind = await Failure(() => _owner.CallAsync(StorageMethods.CleanupPreview, new CleanupPreviewParams("everything")));
        Assert.Equal("storage.invalid.kind", KeyOf(kind));
        var age = await Failure(() => _owner.CallAsync(StorageMethods.CleanupPreview, new CleanupPreviewParams("age", "2")));
        Assert.Equal("storage.invalid.age", KeyOf(age));
    }
}
