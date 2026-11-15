using PolyType;

namespace Ncw.Protocol.Methods;

/// <summary>
/// 改动审查的方法表(docs/design/09 §8.4,功能盘点 10 的 <c>review</c> 模块):回复底部的审查卡、右侧 changes 页签、撤销 / 重做。能力 <see cref="Capabilities.Review"/>,承载 本机 + 远程。
/// 读取 viewer 起;撤销 / 重做 member 起(command 带幂等键)。
/// <para>
/// 变更集的真源在设备库(<c>file_change_sets</c> / <c>file_snapshots</c>,2.x 的 schema 23):Run 结束时设备把工具采集到的「首个 before / 最后一个 after」落库,<b>按顶层 Run 聚合</b>(子代理的改动并进父 Run)。
/// 摘要(<see cref="ReviewChangeSetDto"/>)不带全文,全文只在用户点开某个文件时按 <c>review.fileDiff</c> 单文件取。
/// 撤销前先比对:磁盘现状的 SHA-256 必须等于记录里「改动后」的 hash,不等说明这个文件在那之后又被改过——非 force 的撤销把它标 <c>conflict</c> 跳过,
/// 由界面提示用户确认后带 <c>Force</c> 重发。文件的读写都经项目的执行环境,路径越界(区外文件)的条目禁撤销。
/// </para>
/// </summary>
public static class ReviewMethods
{
    /// <summary>某个顶层 Run 的变更集摘要;这个 Run 没改过文件(或还没落库)<see cref="ReviewChangeSetResult.ChangeSet"/> 为 null。</summary>
    public static readonly MethodDef<ReviewRunParams, ReviewChangeSetResult> ChangeSet = Query<ReviewRunParams, ReviewChangeSetResult>("review.changeSet", Role.Viewer);

    /// <summary>一个会话里改过文件的各轮(按时间倒序),每轮一份摘要。</summary>
    public static readonly MethodDef<ReviewSessionParams, ReviewListResult> List = Query<ReviewSessionParams, ReviewListResult>("review.list", Role.Viewer);

    /// <summary>单个文件的 before / after 全文(diff 预览用);没有为 null。</summary>
    public static readonly MethodDef<ReviewFileParams, ReviewFileDiffResult> FileDiff = Query<ReviewFileParams, ReviewFileDiffResult>("review.fileDiff", Role.Viewer);

    /// <summary>撤销前的预检:返回磁盘现状已和记录不符的文件,供界面提示确认。</summary>
    public static readonly MethodDef<ReviewRunParams, ReviewPrecheckResult> PrecheckUndo = Query<ReviewRunParams, ReviewPrecheckResult>("review.precheckUndo", Role.Viewer);

    public static readonly MethodDef<ReviewUndoParams, ReviewMutationDto> Undo = Command<ReviewUndoParams, ReviewMutationDto>("review.undo", Role.Member);

    /// <summary>恢复(重做)一个已撤销的变更集:磁盘此刻应当是 before,直接回写 after。</summary>
    public static readonly MethodDef<ReviewRunParams, ReviewMutationDto> Redo = Command<ReviewRunParams, ReviewMutationDto>("review.redo", Role.Member);

    public static readonly IReadOnlyList<MethodDef> All = [ChangeSet, List, FileDiff, PrecheckUndo, Undo, Redo];

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Review };

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Review };
}

/// <summary><see cref="ReviewFileEntryDto.ChangeKind"/> 的取值。</summary>
public static class ReviewChangeKinds
{
    public const string Created = "created";
    public const string Modified = "modified";
    public const string Deleted = "deleted";
}

/// <summary><see cref="ReviewChangeSetDto.State"/> 的取值:整轮的撤销态。</summary>
public static class ReviewStates
{
    public const string Applied = "applied";
    public const string Reverted = "reverted";
}

/// <summary><see cref="ReviewFileResultDto.Status"/> 的取值。</summary>
public static class ReviewFileStatuses
{
    /// <summary>已回写。</summary>
    public const string Ok = "ok";

    /// <summary>磁盘现状与记录不符且没有强制。</summary>
    public const string Conflict = "conflict";

    /// <summary>超限(内容没入库)或区外文件:跳过。<see cref="ReviewFileResultDto.Reason"/> = <c>oversize</c> / <c>outsideWorkspace</c>。</summary>
    public const string Skipped = "skipped";

    /// <summary>写回失败。</summary>
    public const string Error = "error";
}

public sealed record ReviewRunParams(string RunId);

public sealed record ReviewSessionParams(string SessionId);

public sealed record ReviewFileParams(string RunId, string Path);

public sealed record ReviewUndoParams(string RunId, bool Force = false);

/// <summary>一个文件在某一轮里的改动条目(不含全文)。</summary>
/// <param name="Path">项目相对路径;区外文件是绝对路径。</param>
/// <param name="Oversize">单文件超过 1 MiB:内容没入库,禁 diff / 撤销。</param>
/// <param name="InWorkspace">false = 改到了项目外的文件,禁撤销。</param>
public sealed record ReviewFileEntryDto(string Path, string ChangeKind, int Additions, int Deletions, bool Oversize, bool InWorkspace);

/// <summary>一轮(顶层 Run)的变更集摘要。<see cref="UpdatedAt"/> 是 Unix 毫秒。</summary>
public sealed record ReviewChangeSetDto(
    string RunId,
    string SessionId,
    string WorkspaceId,
    string State,
    int FileCount,
    int Additions,
    int Deletions,
    ReviewFileEntryDto[] Files,
    long UpdatedAt);

public sealed record ReviewChangeSetResult(ReviewChangeSetDto? ChangeSet);

public sealed record ReviewListResult(ReviewChangeSetDto[] ChangeSets);

/// <summary>单文件的 before / after 全文。created 的 before 是空串;oversize 的两侧都是空串。</summary>
public sealed record ReviewFileDiffDto(string Path, string ChangeKind, string Before, string After, bool Oversize);

public sealed record ReviewFileDiffResult(ReviewFileDiffDto? Diff);

public sealed record ReviewPrecheckResult(string[] Conflicts);

/// <param name="Reason">skipped / error 的原因:<c>oversize</c> / <c>outsideWorkspace</c> / 英文诊断(error)。</param>
public sealed record ReviewFileResultDto(string Path, string Status, string? Reason = null);

/// <summary>撤销 / 恢复整轮的结果。<see cref="State"/> 是操作之后整轮的撤销态(有文件真的回写了才翻转)。</summary>
public sealed record ReviewMutationDto(string RunId, string State, ReviewFileResultDto[] Files);

/// <summary>改动审查的载荷形状(与其它领域同属一个 <c>PayloadShapes</c>)。</summary>
[GenerateShapeFor<ReviewRunParams>]
[GenerateShapeFor<ReviewSessionParams>]
[GenerateShapeFor<ReviewFileParams>]
[GenerateShapeFor<ReviewUndoParams>]
[GenerateShapeFor<ReviewChangeSetResult>]
[GenerateShapeFor<ReviewChangeSetDto>]
[GenerateShapeFor<ReviewListResult>]
[GenerateShapeFor<ReviewFileDiffResult>]
[GenerateShapeFor<ReviewFileDiffDto>]
[GenerateShapeFor<ReviewPrecheckResult>]
[GenerateShapeFor<ReviewMutationDto>]
public sealed partial class PayloadShapes;
