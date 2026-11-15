using PolyType;

namespace Ncw.Protocol.Methods;

// 附件(docs/design/10 的 `attachment` 模块 → `attachments.*`;选文件对话框是客户端本地)。真源在设备的 `AttachmentService`(2.x 的 `attachments` 表原样沿用)。
// ★ 协议帧上限 4 MiB、单个附件上限 32 MiB,所以上传是分块的 command:begin(announce 总字节)→ chunk(≤ 1 MiB,按顺序,偏移 = 已收到的字节数,重发幂等)→ commit(校验、去重、原子落盘、登记草稿)/ abort。
// ★ `AttachmentDto.Url` 是 `ncw://attachments/sessions/<会话>/<文件>` 引用,**绝对路径不出设备**;远程控制端按 `attachments.read` 分块取字节来显示。
// 业务失败是粗粒度错误码 + `error.details`(`ProviderErrorDto`:稳定的 `attachments.error.*` key + 参数,控制端翻译):`invalid_request`、`too_large`、`not_found`、`conflict`。
// 工作区上传三步(SSH 项目把附件送到远端主机)随 SSH 执行环境接线一起做(`unsupported` 之前不声明方法)。

/// <param name="Scope"><c>session</c>(上传的都是它;theme / export 是设备内部用的)。</param>
/// <param name="OwnerId">会话 id(草稿可以早于会话行存在)。</param>
public sealed record AttachmentDto(string Id, string Scope, string? OwnerId, string DisplayName, string Mime, long Size, string? Checksum, long CreatedAt, string Url);

public sealed record AttachmentsUploadBeginParams(string SessionId, string DisplayName, string Mime, long TotalBytes);

public sealed record AttachmentsUploadBeginResult(string UploadId, int ChunkBytes);

public sealed record AttachmentsUploadChunkParams(string UploadId, long Offset, byte[] Data);

public sealed record AttachmentsUploadProgress(long Received);

public sealed record AttachmentsUploadRefParams(string UploadId);

public sealed record AttachmentRefParams(string Id);

public sealed record AttachmentsListParams(string SessionId);

public sealed record AttachmentsListResult(AttachmentDto[] Attachments);

/// <summary>按 <c>ncw://</c> 引用分块读(<see cref="Length"/> ≤ 1 MiB)。只读登记在案的、本作用域看得见的会话附件。</summary>
public sealed record AttachmentsReadParams(string Url, long Offset, int Length);

public sealed record AttachmentsReadResult(byte[] Data, long Total, string Mime, bool Eof);

/// <summary>附件方法表(能力 <see cref="Capabilities.Attachments"/>,承载 本机 + 远程):读 viewer 起,上传 / 移除 / 列出 member 起。</summary>
public static class AttachmentsMethods
{
    public static readonly MethodDef<AttachmentsUploadBeginParams, AttachmentsUploadBeginResult> UploadBegin =
        MethodTable.Command<AttachmentsUploadBeginParams, AttachmentsUploadBeginResult>("attachments.upload.begin", Role.Member, Capabilities.Attachments);

    public static readonly MethodDef<AttachmentsUploadChunkParams, AttachmentsUploadProgress> UploadChunk =
        MethodTable.Command<AttachmentsUploadChunkParams, AttachmentsUploadProgress>("attachments.upload.chunk", Role.Member, Capabilities.Attachments);

    public static readonly MethodDef<AttachmentsUploadRefParams, AttachmentDto> UploadCommit =
        MethodTable.Command<AttachmentsUploadRefParams, AttachmentDto>("attachments.upload.commit", Role.Member, Capabilities.Attachments);

    public static readonly MethodDef<AttachmentsUploadRefParams, Unit> UploadAbort =
        MethodTable.Command<AttachmentsUploadRefParams, Unit>("attachments.upload.abort", Role.Member, Capabilities.Attachments);

    /// <summary>移除输入框里的一张待发附件(幂等;已提交的 → <c>conflict</c>)。</summary>
    public static readonly MethodDef<AttachmentRefParams, Unit> Remove =
        MethodTable.Command<AttachmentRefParams, Unit>("attachments.remove", Role.Member, Capabilities.Attachments);

    public static readonly MethodDef<AttachmentsListParams, AttachmentsListResult> ListBySession =
        MethodTable.Query<AttachmentsListParams, AttachmentsListResult>("attachments.listBySession", Role.Member, Capabilities.Attachments);

    public static readonly MethodDef<AttachmentsReadParams, AttachmentsReadResult> Read =
        MethodTable.Query<AttachmentsReadParams, AttachmentsReadResult>("attachments.read", Role.Viewer, Capabilities.Attachments);

    public static readonly IReadOnlyList<MethodDef> All = [UploadBegin, UploadChunk, UploadCommit, UploadAbort, Remove, ListBySession, Read];
}

[GenerateShapeFor<AttachmentDto>]
[GenerateShapeFor<AttachmentsUploadBeginParams>]
[GenerateShapeFor<AttachmentsUploadBeginResult>]
[GenerateShapeFor<AttachmentsUploadChunkParams>]
[GenerateShapeFor<AttachmentsUploadProgress>]
[GenerateShapeFor<AttachmentsUploadRefParams>]
[GenerateShapeFor<AttachmentRefParams>]
[GenerateShapeFor<AttachmentsListParams>]
[GenerateShapeFor<AttachmentsListResult>]
[GenerateShapeFor<AttachmentsReadParams>]
[GenerateShapeFor<AttachmentsReadResult>]
public sealed partial class PayloadShapes;
