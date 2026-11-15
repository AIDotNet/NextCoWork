using Ncw.Agent.Services.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Services;

/// <summary>
/// 存储维护的方法实现(方法表在 <see cref="StorageMethods"/>)。角色、承载由路由统一检查(危险操作:owner + 仅本机通道),确认令牌由服务层核对。
/// 业务失败 → 粗粒度错误码 + 稳定 <c>storage.*</c> key:<c>busy</c>(可重试)、<c>forbidden</c>(令牌缺失 / 过期 / 对不上)、<c>not_found</c>、<c>invalid_request</c>。
/// </summary>
internal static class StorageHandlers
{
    public static void Register(MethodRouter router, Func<DeviceServices> services)
    {
        router.Handle(StorageMethods.GetStats, (_, _, _) => Guard(() => ToDto(services().Storage.Stats())));
        router.Handle(StorageMethods.Vacuum, (_, _, _) => Guard(() => ToDto(services().Storage.Vacuum())));
        router.Handle(StorageMethods.BackupStatus, (_, _, _) => Guard(() => ToDto(services().Backups.Status())));
        router.Handle(StorageMethods.BackupCreate, (p, _, _) => Guard(() => ToDto(services().Backups.Create(p.Manual))));
        router.Handle(StorageMethods.BackupRestorePreview, (p, _, _) => Guard(() => ToDto(services().Backups.PreviewRestore(p.Path))));
        router.Handle(StorageMethods.BackupRestore, (p, _, _) => Guard(() =>
        {
            var s = services();
            var status = s.Backups.Restore(p.Path, p.ConfirmToken);
            s.OnDataReplaced();
            return ToDto(status);
        }));
        router.Handle(StorageMethods.CleanupPreview, (p, _, _) => Guard(() => ToDto(services().Storage.Preview(p.Kind, p.Age))));
        router.Handle(StorageMethods.CleanupAttachments, (_, _, _) => Guard(() => ToDto(services().Storage.CleanupAttachments())));
        router.Handle(StorageMethods.CleanupExecute, (p, _, _) => Guard(() =>
        {
            var s = services();
            var result = s.Storage.Execute(p.Kind, p.Age, p.ConfirmToken);
            s.OnDataReplaced(); // 对话 / 本机数据没了:项目列表、设置、定时任务重新广播
            return ToDto(result);
        }));
        router.Handle(StorageMethods.ExportCreate, (_, _, _) => Guard(() => ToDto(services().Transfer.CreateExport())));
        router.Handle(StorageMethods.ExportRead, (p, _, _) => Guard(() =>
        {
            var chunk = services().Transfer.ReadExport(p.ExportId, p.Offset, p.Length);
            return new ExportChunkDto(chunk.Data, chunk.Eof);
        }));
        router.Handle(StorageMethods.ExportDiscard, (p, _, _) => Guard(() =>
        {
            services().Transfer.DiscardExport(p.ExportId);
            return Unit.Value;
        }));
        router.Handle(StorageMethods.ImportWrite, (p, _, _) => Guard(() =>
        {
            var upload = services().Transfer.WriteImport(p.UploadId, p.Offset, p.Data);
            return new ImportUploadDto(upload.UploadId, upload.Received);
        }));
        router.Handle(StorageMethods.ImportPreview, (p, _, _) => Guard(() =>
        {
            var preview = services().Transfer.PreviewImport(p.UploadId);
            return new ImportPreviewDto(preview.UploadId, preview.Version, preview.Workspaces, preview.Sessions, preview.Messages, preview.NewCount, preview.OverwriteCount, preview.SkippedCount, preview.ConfirmToken);
        }));
        router.Handle(StorageMethods.ImportApply, (p, _, _) => Guard(() =>
        {
            var s = services();
            var result = s.Transfer.ApplyImport(p.UploadId, p.ConfirmToken);
            s.OnDataReplaced();
            return new ImportResultDto(result.Imported, result.Overwritten, result.Skipped, result.WorkspacesImported, result.SessionsImported, result.MessagesImported);
        }));
        router.Handle(StorageMethods.ImportDiscard, (p, _, _) => Guard(() =>
        {
            services().Transfer.DiscardImport(p.UploadId);
            return Unit.Value;
        }));
    }

    private static ValueTask<T> Guard<T>(Func<T> work)
    {
        try
        {
            return ValueTask.FromResult(work());
        }
        catch (StorageOperationException e)
        {
            var code = e.Kind switch
            {
                StorageFailure.NotFound => ErrorCodes.NotFound,
                StorageFailure.Busy => ErrorCodes.Busy,
                StorageFailure.Unconfirmed => ErrorCodes.Forbidden,
                _ => ErrorCodes.InvalidRequest,
            };
            throw ServiceMapping.Error(code, e.Key, e.Message, retryable: e.Kind == StorageFailure.Busy);
        }
        catch (Exception e) when (ServiceMapping.ToProtocolError(e) is { } converted)
        {
            throw converted;
        }
        catch (Ncw.Agent.Storage.StorageException e)
        {
            throw new ProtocolException(ErrorCodes.Unavailable, e.Message, retryable: false);
        }
    }

    private static StorageStatsDto ToDto(StorageStats s) =>
        new(s.DbBytes, s.WalBytes, s.ConversationBytes, s.AttachmentBytes, s.ConversationCount, s.MessageCount, s.AttachmentCount, s.UsageRecordCount, s.WorkspaceCount, s.LastBackupAt);

    private static BackupStatusDto ToDto(BackupStatus s) => new(s.Directory, s.LastBackupAt, s.LastBackupPath, s.LastError, s.Running);

    private static RestorePreviewDto ToDto(RestorePreview p) => new(
        p.Path, new BackupManifestDto(p.Manifest.AppVersion, p.Manifest.CreatedAt, p.Manifest.SchemaVersion, p.Manifest.SessionCount, p.Manifest.MessageCount, p.Manifest.EncryptedCredentials), p.ConfirmToken);

    private static CleanupPreviewDto ToDto(CleanupPreview p) =>
        new(p.Kind, p.SessionCount, p.MessageCount, p.AttachmentCount, p.Bytes, [.. p.Undeletable], p.ConfirmToken);

    private static CleanupResultDto ToDto(CleanupResult r) => new(ToDto(r.Preview), r.Deleted);

    private static ExportInfoDto ToDto(ExportInfo e) => new(e.ExportId, e.Bytes, e.Workspaces, e.Sessions, e.Messages);
}
