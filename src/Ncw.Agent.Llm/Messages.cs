using System.Text.Json;

namespace Ncw.Agent.Llm;

public enum Role
{
    User,
    Assistant,
}

public readonly record struct MessageId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>助手消息来自哪个线协议 / 供应商 / 上游模型(取自 pi)。换模型时据此决定能否原样回放思考内容(01-llm §4)。</summary>
public sealed record Provenance(string Api, string Provider, string Model);

/// <summary>
/// 一条消息。以 2.x 的 Anthropic 块模型为底(R2):内容是块数组而不是字符串。
/// <paramref name="Source"/> 和 <paramref name="Stop"/> 只有助手消息有;<paramref name="Stop"/> 为 Aborted、或消息带 <see cref="ErrorPart"/> 时,回放时整条跳过。
/// </summary>
public sealed record Message(
    MessageId Id,
    Role Role,
    IReadOnlyList<Part> Parts,
    DateTimeOffset CreatedAt,
    bool Internal = false,
    Provenance? Source = null,
    StopReason? Stop = null);

public abstract record Part;

public sealed record TextPart(string Text) : Part;

/// <summary><paramref name="DurationMs"/> / <paramref name="Tokens"/> 只走界面那一轨,编码器不读。</summary>
public sealed record ThinkingPart(string Text, ProtocolOpaque? Opaque = null, int? DurationMs = null, int? Tokens = null) : Part;

/// <summary><paramref name="InputError"/> 非空 = 参数 JSON 解析失败(原文在其中),上层把它变成工具错误而不是异常。</summary>
public sealed record ToolCallPart(string CallId, string Name, JsonElement Input, string? InputError = null) : Part;

public sealed record ToolResultPart(string CallId, ToolOutput Output, bool IsError, SubagentReceipt? Subagent = null) : Part;

/// <summary><paramref name="DataRef"/> 是 <c>data:</c> URL 或 <c>ncw://</c> 引用;编码器只认 <c>data:</c>,<c>ncw://</c> 由路由在发出去的副本上解析。</summary>
public sealed record ImagePart(string Mime, string DataRef) : Part;

public sealed record FileRefPart(string Path, string Name) : Part;

// 只给界面的块:编码器一律丢弃,不回传给模型。
public sealed record ErrorPart(AgentError Error) : Part;

/// <summary>
/// 目标标记(只给界面的块)。<paramref name="Met"/> / <paramref name="Failed"/>(判为不可能)/ <paramref name="Cleared"/>(用户清掉)是三个终态,
/// 都不是 = 目标当时还活着(重载恢复只认最后一条)。后面的字段是 2.x 的可选追加项:<paramref name="Set"/> = 「已设定」那一条,<paramref name="Origin"/> 是线上写法
/// (<c>user / proposal_direct / proposal_approved / restored</c>),<paramref name="Id"/> / <paramref name="CreatedAt"/>(毫秒)用来把带外写入的标记和流里的助手消息合并去重。
/// </summary>
public sealed record GoalStatusPart(
    bool Met,
    string Condition,
    string? Reason = null,
    bool? Failed = null,
    bool? Cleared = null,
    bool? Set = null,
    string? Origin = null,
    int? Iterations = null,
    long? DurationMs = null,
    long? Tokens = null,
    string? Id = null,
    long? CreatedAt = null) : Part;

/// <summary>写这份摘要的是谁(领域值,不翻译)。<paramref name="FellBack"/> = 配置的压缩模型当时解析不到、回落成了会话模型。</summary>
public sealed record SummaryModelInfo(string Model, string? ProviderId, string Thinking, bool FellBack = false);

/// <summary>
/// 压缩边界(只给界面的块:它所在的消息是 <c>Internal</c> 的用户消息,模型看到的是同一条消息里紧跟着的文字)。
/// <paramref name="Instructions"/> / <paramref name="RestoredFiles"/> / <paramref name="SummaryModel"/> 是 2.x 边界上就有的可选信息。
/// </summary>
public sealed record CompactBoundaryPart(
    string Trigger,
    int PreTokens,
    int PostTokens,
    string Summary,
    string? Instructions = null,
    IReadOnlyList<string>? RestoredFiles = null,
    SummaryModelInfo? SummaryModel = null) : Part;

/// <summary>各家私有数据(签名、加密推理、redacted thinking)。内核不解释,原样存、原样回放。带协议标签,标签对不上的编码器一律忽略。</summary>
public sealed record ProtocolOpaque(string Protocol, JsonElement Data);

public sealed record ToolOutputImage(string Mime, string DataRef);

/// <summary>工具结果里给模型看的部分。<paramref name="Details"/> 只给界面,编码器丢弃。</summary>
public sealed record ToolOutput(
    string Content,
    IReadOnlyList<ToolOutputImage>? Images = null,
    bool Truncated = false,
    long? OriginalBytes = null,
    JsonElement? Details = null);

/// <summary>
/// <c>Task</c> 工具结果上的子代理回执(父会话转录里的持久回执,05 §5):状态 <c>running / done / error / aborted</c>;
/// 后台子代理另有 <paramref name="Background"/> 和汇报进度 <paramref name="ReportStatus"/>(<c>pending / injecting / reported / blocked</c>)。
/// 全是可选追加项:2.x 写的旧回执只有前三项。
/// </summary>
public sealed record SubagentReceipt(
    string ChildRunId,
    string? Status = null,
    string? Summary = null,
    bool? Background = null,
    string? ReportStatus = null,
    string? Color = null,
    AgentError? Error = null);

/// <summary>
/// 后台子代理报告的界面标记(2.x 的 <c>subagent</c> 块):挂在那条回传给主代理的内部消息上,让界面把它认成「某个后台子代理的结果回传」画一行可展开的汇报行。
/// 只给界面那一轨:编码器丢弃(全文已经在同一条消息的文字块里,再发一份就是同一段话出现两次)。<paramref name="Summary"/> 是卡片上那截短预览。
/// </summary>
public sealed record SubagentReportPart(string CallId, string ChildRunId, string? Summary = null) : Part;
