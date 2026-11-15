using PolyType;

namespace Ncw.Protocol.Methods;

// 存储维护(docs/design/10 的 `storage` 模块 → `storage.*`:统计、整理、备份、清理、导出导入;选目录 / 打开数据目录 / 文件对话框是客户端本地)。
// 真源在 Agent 的 `StorageMaintenance` / `BackupService` / `DataTransfer`。能力 `storage`。
// ★ 危险操作(恢复备份、按年龄清理、清空历史、清除本地数据、应用导入)必须同时满足:owner + 本机通道 + 二次确认参数。
//   二次确认 = 一枚一次性令牌:先调对应的预览方法拿到 `ConfirmToken`(绑定到这一次操作的种类和参数,10 分钟过期),执行时出示。
//   令牌证明「调用方刚刚看过预览」;一个布尔的 `confirm: true` 谁都可以写死,所以不用它。远程 owner 只能做不会丢用户内容的维护(统计 / 整理 / 备份 / 回收没有引用的附件 / 导出)。
// 业务失败是粗粒度错误码 + `error.details`(`ProviderErrorDto`:稳定的 `storage.*` key):`invalid_request`、`not_found`、`busy`(有运行中的 Agent,可重试)、
// `forbidden` + key `storage.confirmationRequired`(令牌缺失 / 过期 / 对不上)。

/// <summary>设置 › 数据页的统计。附件字节是附件目录的实际占用(符号链接只按自身计,不跟随)。</summary>
public sealed record StorageStatsDto(
    long DbBytes,
    long WalBytes,
    long ConversationBytes,
    long AttachmentBytes,
    long ConversationCount,
    long MessageCount,
    long AttachmentCount,
    long UsageRecordCount,
    long WorkspaceCount,
    long? LastBackupAt);

public sealed record BackupStatusDto(string? Directory, long? LastBackupAt, string? LastBackupPath, string? LastError, bool Running);

/// <summary><see cref="Manual"/> 缺省真:手动备份(带时间戳的文件名,永不被清理);假 = 自动备份(固定文件名,覆盖上一份)。备份目录取自设置 <c>data.backupDirectory</c>(必须是绝对路径、与数据根不重叠、可写)。</summary>
public sealed record BackupCreateParams(bool Manual = true);

public sealed record BackupManifestDto(string AppVersion, long CreatedAt, int SchemaVersion, long SessionCount, long MessageCount, bool EncryptedCredentials);

/// <summary>要恢复的备份文件(设备上的绝对路径;只有本机 owner 能调,路径由用户在本机文件对话框里选)。</summary>
public sealed record RestorePreviewParams(string Path);

public sealed record RestorePreviewDto(string Path, BackupManifestDto Manifest, string ConfirmToken);

public sealed record RestoreParams(string Path, string ConfirmToken);

/// <param name="Kind"><c>attachments</c> / <c>age</c> / <c>history</c> / <c>local-data</c>。</param>
/// <param name="Age"><c>age</c> 才要:<c>7d</c> / <c>15d</c> 是天数,<c>1</c> / <c>3</c> / <c>6</c> / <c>12</c> 是日历月数。</param>
public sealed record CleanupPreviewParams(string Kind, string? Age = null);

/// <summary>清理预览:<see cref="SessionCount"/> 数顶层会话(子代理转录不是一条对话),消息数 / 字节数按整棵子树(真正会消失的量)。需要确认的种类带 <see cref="ConfirmToken"/>。</summary>
public sealed record CleanupPreviewDto(string Kind, long SessionCount, long MessageCount, long AttachmentCount, long Bytes, string[] Undeletable, string? ConfirmToken);

public sealed record CleanupExecuteParams(string Kind, string? Age, string ConfirmToken);

public sealed record CleanupResultDto(CleanupPreviewDto Preview, long Deleted);

public sealed record ExportInfoDto(string ExportId, long Bytes, long Workspaces, long Sessions, long Messages);

/// <summary>分块读导出文件:<see cref="Length"/> ≤ 1 MiB(帧上限 4 MiB)。</summary>
public sealed record ExportReadParams(string ExportId, long Offset, int Length);

public sealed record ExportChunkDto(byte[] Data, bool Eof);

public sealed record ExportRefParams(string ExportId);

/// <summary>分块上传导入文件:<see cref="UploadId"/> 为 null = 新开一份(此时 <see cref="Offset"/> 必须是 0);块必须按顺序到(偏移 = 已收到的字节数,重发同一块幂等)。</summary>
public sealed record ImportWriteParams(string? UploadId, long Offset, byte[] Data);

public sealed record ImportUploadDto(string UploadId, long Received);

public sealed record ImportRefParams(string UploadId);

public sealed record ImportPreviewDto(string UploadId, long Version, long Workspaces, long Sessions, long Messages, long NewCount, long OverwriteCount, long SkippedCount, string ConfirmToken);

public sealed record ImportApplyParams(string UploadId, string ConfirmToken);

public sealed record ImportResultDto(long Imported, long Overwritten, long Skipped, long WorkspacesImported, long SessionsImported, long MessagesImported);

/// <summary>存储方法表(能力 <see cref="Capabilities.Storage"/>):统计 / 备份状态 member 起;整理、备份、附件回收、导出 owner;危险操作 owner + 仅本机通道 + 确认令牌。</summary>
public static class StorageMethods
{
    public static readonly MethodDef<Unit, StorageStatsDto> GetStats =
        MethodTable.Query<Unit, StorageStatsDto>("storage.getStats", Role.Member, Capabilities.Storage);

    public static readonly MethodDef<Unit, StorageStatsDto> Vacuum =
        MethodTable.Command<Unit, StorageStatsDto>("storage.vacuum", Role.Owner, Capabilities.Storage);

    public static readonly MethodDef<Unit, BackupStatusDto> BackupStatus =
        MethodTable.Query<Unit, BackupStatusDto>("storage.backup.status", Role.Member, Capabilities.Storage);

    public static readonly MethodDef<BackupCreateParams, BackupStatusDto> BackupCreate =
        MethodTable.Command<BackupCreateParams, BackupStatusDto>("storage.backup.create", Role.Owner, Capabilities.Storage);

    /// <summary>验证一份备份并签发确认令牌。不改任何东西。仅本机 owner。</summary>
    public static readonly MethodDef<RestorePreviewParams, RestorePreviewDto> BackupRestorePreview =
        MethodTable.Query<RestorePreviewParams, RestorePreviewDto>("storage.backup.restorePreview", Role.Owner, Capabilities.Storage, Transports.Local);

    /// <summary>恢复(危险):出示预览签发的令牌。有运行中的 Agent → <c>busy</c>。仅本机 owner。</summary>
    public static readonly MethodDef<RestoreParams, BackupStatusDto> BackupRestore =
        MethodTable.Command<RestoreParams, BackupStatusDto>("storage.backup.restore", Role.Owner, Capabilities.Storage, Transports.Local);

    public static readonly MethodDef<CleanupPreviewParams, CleanupPreviewDto> CleanupPreview =
        MethodTable.Query<CleanupPreviewParams, CleanupPreviewDto>("storage.cleanup.preview", Role.Owner, Capabilities.Storage);

    /// <summary>回收没有任何引用的附件文件(残片 / 孤儿 / 超期草稿 / 文件已不在的行)。不删用户内容,owner 即可(含远程),不需要令牌。</summary>
    public static readonly MethodDef<Unit, CleanupResultDto> CleanupAttachments =
        MethodTable.Command<Unit, CleanupResultDto>("storage.cleanup.attachments", Role.Owner, Capabilities.Storage);

    /// <summary>按年龄清理 / 清空历史 / 清除本地数据(危险):owner + 仅本机通道 + 确认令牌。</summary>
    public static readonly MethodDef<CleanupExecuteParams, CleanupResultDto> CleanupExecute =
        MethodTable.Command<CleanupExecuteParams, CleanupResultDto>("storage.cleanup.execute", Role.Owner, Capabilities.Storage, Transports.Local);

    public static readonly MethodDef<Unit, ExportInfoDto> ExportCreate =
        MethodTable.Command<Unit, ExportInfoDto>("storage.export.create", Role.Owner, Capabilities.Storage);

    public static readonly MethodDef<ExportReadParams, ExportChunkDto> ExportRead =
        MethodTable.Query<ExportReadParams, ExportChunkDto>("storage.export.read", Role.Owner, Capabilities.Storage);

    public static readonly MethodDef<ExportRefParams, Unit> ExportDiscard =
        MethodTable.Command<ExportRefParams, Unit>("storage.export.discard", Role.Owner, Capabilities.Storage);

    public static readonly MethodDef<ImportWriteParams, ImportUploadDto> ImportWrite =
        MethodTable.Command<ImportWriteParams, ImportUploadDto>("storage.import.write", Role.Owner, Capabilities.Storage, Transports.Local);

    public static readonly MethodDef<ImportRefParams, ImportPreviewDto> ImportPreview =
        MethodTable.Query<ImportRefParams, ImportPreviewDto>("storage.import.preview", Role.Owner, Capabilities.Storage, Transports.Local);

    /// <summary>应用导入(会覆盖较旧的同 id 会话):owner + 仅本机通道 + 确认令牌。</summary>
    public static readonly MethodDef<ImportApplyParams, ImportResultDto> ImportApply =
        MethodTable.Command<ImportApplyParams, ImportResultDto>("storage.import.apply", Role.Owner, Capabilities.Storage, Transports.Local);

    public static readonly MethodDef<ImportRefParams, Unit> ImportDiscard =
        MethodTable.Command<ImportRefParams, Unit>("storage.import.discard", Role.Owner, Capabilities.Storage, Transports.Local);

    public static readonly IReadOnlyList<MethodDef> All =
    [
        GetStats, Vacuum, BackupStatus, BackupCreate, BackupRestorePreview, BackupRestore, CleanupPreview, CleanupAttachments, CleanupExecute,
        ExportCreate, ExportRead, ExportDiscard, ImportWrite, ImportPreview, ImportApply, ImportDiscard,
    ];
}

[GenerateShapeFor<StorageStatsDto>]
[GenerateShapeFor<BackupStatusDto>]
[GenerateShapeFor<BackupCreateParams>]
[GenerateShapeFor<BackupManifestDto>]
[GenerateShapeFor<RestorePreviewParams>]
[GenerateShapeFor<RestorePreviewDto>]
[GenerateShapeFor<RestoreParams>]
[GenerateShapeFor<CleanupPreviewParams>]
[GenerateShapeFor<CleanupPreviewDto>]
[GenerateShapeFor<CleanupExecuteParams>]
[GenerateShapeFor<CleanupResultDto>]
[GenerateShapeFor<ExportInfoDto>]
[GenerateShapeFor<ExportReadParams>]
[GenerateShapeFor<ExportChunkDto>]
[GenerateShapeFor<ExportRefParams>]
[GenerateShapeFor<ImportWriteParams>]
[GenerateShapeFor<ImportUploadDto>]
[GenerateShapeFor<ImportRefParams>]
[GenerateShapeFor<ImportPreviewDto>]
[GenerateShapeFor<ImportApplyParams>]
[GenerateShapeFor<ImportResultDto>]
public sealed partial class PayloadShapes;
