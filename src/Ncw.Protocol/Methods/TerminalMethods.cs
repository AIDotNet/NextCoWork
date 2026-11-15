using PolyType;

namespace Ncw.Protocol.Methods;

/// <summary>
/// 终端的方法表和主题(docs/design/06 §9.4,功能盘点 10 的 <c>terminal</c> 模块;<b>取代</b> 09 早先草案里的 <c>terminals.open</c>(stream))。能力 <see cref="Capabilities.Terminals"/>,承载 本机 + 远程。
/// 实现:<c>Ncw.Agent.Host/Terminals</c>(把 <c>Ncw.Agent.Terminal.TerminalManager</c> 接到协议),终端的限额、环形缓冲、游标续传、远程批准都在 <c>TerminalManager</c> 里。
/// <list type="bullet">
/// <item>读取(<c>list</c> / <c>getBuffer</c> + 主题)viewer 起;其余 member 起,command 带幂等键(<c>write</c> 的同一个键重发不重复写)。</item>
/// <item><b>SSH 环境的终端要先逐个批准</b>:<c>prepare</c> 返回带执行环境标识(设备 + 环境)的 <see cref="TerminalIntentDto"/>,发起它的控制端 <c>approve</c> 得到一次性凭据,凭它 <c>create</c>。
/// 本机项目 <c>prepare</c> 直接就绪。远程终端只有创建它的控制端能看、写、杀、列出;控制端的连接断开时它名下的意图和远程终端作废,本机终端属于设备不受影响。</item>
/// <item>租约闸(02 A2:控制端申请租约后才能写)尚未做:member 即可写,多个控制端同时写同一个终端不互斥。</item>
/// </list>
/// 错误映射(<c>TerminalManager</c> → 协议):<c>NotFound</c> → <c>not_found</c>;<c>Conflict</c> / <c>ApprovalRequired</c> / <c>ApprovalExpired</c> → <c>conflict</c>(<see cref="TerminalErrorDto.Key"/> 区分);
/// <c>ResourceLimit</c> → <c>busy</c>;<c>TooLarge</c> → <c>too_large</c>;<c>InvalidArgument</c> → <c>invalid_request</c>;<c>Cancelled</c> → <c>unavailable</c>;环境层 <c>Disconnected</c> → <c>unavailable</c>,<c>Unsupported</c> → <c>unsupported</c>。
/// </para>
/// </summary>
public static class TerminalMethods
{
    public static readonly MethodDef<TerminalsListParams, TerminalsListResult> List = Query<TerminalsListParams, TerminalsListResult>("terminals.list", Role.Viewer);

    /// <summary>结果是 <c>Terminal</c>(本机环境,或这个 id 已有活终端:直接就绪)或 <c>Intent</c>(远程环境:要先批准)。</summary>
    public static readonly MethodDef<TerminalsPrepareParams, TerminalPrepareResult> Prepare = Command<TerminalsPrepareParams, TerminalPrepareResult>("terminals.prepare", Role.Member);

    /// <summary>批准或拒绝一个远程终端意图。只有发起意图的控制端能批;别人的、过期的得到 <c>conflict</c>(<c>terminals.approvalExpired</c>,不泄漏它是否存在)。</summary>
    public static readonly MethodDef<TerminalsApproveParams, TerminalApproveResult> Approve = Command<TerminalsApproveParams, TerminalApproveResult>("terminals.approve", Role.Member);

    /// <summary>创建终端。<c>Id</c> 给了就复用:同一个 id 的活终端直接返回,已退出的在同一个 id 上重新开(序号和缓冲接着走,订阅者续订)。</summary>
    public static readonly MethodDef<TerminalsCreateParams, TerminalDto> Create = Command<TerminalsCreateParams, TerminalDto>("terminals.create", Role.Member);

    /// <summary>页签关闭:终止(若还在跑)并从表里移除。</summary>
    public static readonly MethodDef<TerminalRefParams, Unit> Close = Command<TerminalRefParams, Unit>("terminals.close", Role.Member);

    /// <summary>当前环形缓冲(最近 256 KiB)。不订阅就想看一眼(例如手机的只读查看)用它;要实时用 <c>terminal/&lt;id&gt;</c> 主题。</summary>
    public static readonly MethodDef<TerminalRefParams, TerminalBufferDto> GetBuffer = Query<TerminalRefParams, TerminalBufferDto>("terminals.getBuffer", Role.Viewer);

    /// <summary>键盘输入(最多 64 KiB 一次)。终端已退出 <c>conflict</c>。</summary>
    public static readonly MethodDef<TerminalsWriteParams, Unit> Write = Command<TerminalsWriteParams, Unit>("terminals.write", Role.Member);

    public static readonly MethodDef<TerminalsResizeParams, Unit> Resize = Command<TerminalsResizeParams, Unit>("terminals.resize", Role.Member);

    /// <summary>终止终端进程。会话保留为「已退出」(可回看输出),页签关闭用 <c>close</c>。</summary>
    public static readonly MethodDef<TerminalRefParams, Unit> Kill = Command<TerminalRefParams, Unit>("terminals.kill", Role.Member);

    /// <summary><c>terminal/&lt;id&gt;</c> 的前缀。</summary>
    public const string TopicPrefix = "terminal/";

    /// <summary>
    /// 某个终端的主题:<c>terminal/&lt;id&gt;</c>。<b>来源主题</b>:主题的 <c>seq</c> 就是终端输出批的 <c>Seq</c>(每个终端内严格连续,退出事件也占一个),游标就是上次收到的最后一个 <c>seq</c>。
    /// 不带游标或游标已被日志挤出去:发一个 <see cref="TerminalSnapshotDto"/>(元数据 + 整个环形缓冲,控制端用它重置显示);游标还在日志里:不发快照,只补发游标之后的 <c>terminals.data</c> / <c>terminals.exit</c> 事件,之后实时推送。
    /// 终端输出没有「可丢的增量」:每一批都是结构性的,发送缓冲满时对该主题发 <c>resync</c>(控制端不带游标重订,拿到整个缓冲)。终端不存在(从没有过或已回收)订阅得到 <c>not_found</c>。
    /// </summary>
    public static TopicDef<TerminalSnapshotDto> Topic(string terminalId) => new(TopicPrefix + terminalId, PayloadShapes.GetShape<TerminalSnapshotDto>());

    public static string TopicName(string terminalId) => TopicPrefix + terminalId;

    public static readonly IReadOnlyList<MethodDef> All = [List, Prepare, Approve, Create, Close, GetBuffer, Write, Resize, Kill];

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Terminals };

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Terminals };
}

/// <summary><c>terminal/&lt;id&gt;</c> 主题的事件类型。</summary>
public static class TerminalEvents
{
    /// <summary>一批输出(<see cref="TerminalDataDto"/>)。</summary>
    public const string Data = "terminals.data";

    /// <summary>终态(<see cref="TerminalExitDto"/>);之后不会再有事件。</summary>
    public const string Exit = "terminals.exit";
}

/// <summary><see cref="TerminalErrorDto.Key"/> 的取值(控制端翻译)。</summary>
public static class TerminalErrorKeys
{
    public const string NotFound = "terminals.notFound";
    public const string Exited = "terminals.exited";
    public const string Conflict = "terminals.conflict";
    public const string LimitReached = "terminals.limitReached";
    public const string InvalidArgument = "terminals.invalidArgument";
    public const string TooLarge = "terminals.tooLarge";
    public const string ApprovalRequired = "terminals.approvalRequired";
    public const string ApprovalExpired = "terminals.approvalExpired";
    public const string Cancelled = "terminals.cancelled";
    public const string Disconnected = "terminals.disconnected";
    public const string Unsupported = "terminals.unsupported";
    public const string ProjectUnavailable = "terminals.projectUnavailable";
    public const string InvalidPath = "terminals.invalidPath";
}

/// <summary><c>error.details</c> 的结构化补充(只增键,未知键忽略)。<see cref="Detail"/> 是英文诊断,不含界面文字。</summary>
public sealed record TerminalErrorDto(string Key, string? Detail = null);

/// <summary>终端在哪个环境里运行:设备 + 环境(审批卡片和终端页签都要显示)。<see cref="Kind"/>:<c>local</c> / <c>ssh</c>。</summary>
public sealed record TerminalEnvDto(string DeviceId, string Environment, string Name, string Kind);

/// <summary>
/// 一个终端会话的元数据。<see cref="Status"/>:<c>running</c> / <c>exited</c>(<b>单调</b>:退出之后不会回到 running;同一个 id 重新创建是新的一段)。
/// <see cref="ExitReason"/>:<c>exited</c> / <c>killed</c> / <c>connectionLost</c>(只在已退出时有)。时间是 Unix 毫秒。
/// </summary>
public sealed record TerminalDto(
    string Id,
    string ProjectId,
    TerminalEnvDto Environment,
    string Title,
    string Cwd,
    string Shell,
    int Columns,
    int Rows,
    string Status,
    long CreatedAt,
    int? ExitCode = null,
    int? Signal = null,
    string? ExitReason = null,
    long? ExitedAt = null);

/// <summary>一次待批准的终端创建。只带结构化数据,控制端翻译成「将在 dev-server(SSH,经由 本机)上打开终端」。<see cref="ExpiresAt"/> 之后作废(缺省 60 秒)。</summary>
public sealed record TerminalIntentDto(string Id, string TerminalId, string ProjectId, TerminalEnvDto Environment, string Cwd, string Shell, long ExpiresAt);

public sealed record TerminalsListParams(string ProjectId);

public sealed record TerminalsListResult(TerminalDto[] Terminals);

/// <param name="Cwd">项目相对或绝对的目录,必须落在项目根内;缺省 = 项目根。</param>
public sealed record TerminalsPrepareParams(string ProjectId, int Columns, int Rows, string? Id = null, string? Cwd = null);

public sealed record TerminalPrepareResult(TerminalDto? Terminal = null, TerminalIntentDto? Intent = null);

public sealed record TerminalsApproveParams(string IntentId, bool Approved);

/// <param name="Approval">一次性凭据(绑定控制端、终端 id、环境实例、连接代、目录,60 秒过期);拒绝时为空。</param>
public sealed record TerminalApproveResult(string? Approval = null);

/// <param name="Approval">远程(SSH)环境必填:<c>prepare</c> → <c>approve</c> 拿到的凭据;本机环境不需要。</param>
public sealed record TerminalsCreateParams(string ProjectId, int Columns, int Rows, string? Id = null, string? Cwd = null, string? Approval = null);

public sealed record TerminalRefParams(string Id);

/// <param name="Data">原始字节(不做任何解码:多字节字符可能被切在两批之间,由显示端的流式解码器处理)。</param>
/// <param name="Truncated">缓冲装不下,最早的输出已被丢弃(从中间裁时对齐到 UTF-8 字符边界)。</param>
/// <param name="Seq">缓冲包含的最后一批的序号(含退出事件);之后用它作游标订阅主题。</param>
public sealed record TerminalBufferDto(string Id, byte[] Data, bool Truncated, long Seq);

public sealed record TerminalsWriteParams(string Id, byte[] Data);

public sealed record TerminalsResizeParams(string Id, int Columns, int Rows);

/// <summary>
/// <c>terminal/&lt;id&gt;</c> 的快照(游标空或续不上)。<see cref="Data"/> 是整个环形缓冲,控制端用它重置显示;<see cref="Seq"/> 是它包含的最后一批的序号,之后推送的第一个事件是 <c>Seq + 1</c>。
/// </summary>
public sealed record TerminalSnapshotDto(TerminalDto Terminal, byte[] Data, bool Truncated, long Seq);

public sealed record TerminalDataDto(byte[] Data);

/// <param name="Reason"><c>exited</c> / <c>killed</c> / <c>connectionLost</c>。</param>
public sealed record TerminalExitDto(int? ExitCode, int? Signal, string Reason);

/// <summary>终端的载荷形状(与其它领域同属一个 <c>PayloadShapes</c>)。</summary>
[GenerateShapeFor<TerminalsListParams>]
[GenerateShapeFor<TerminalsListResult>]
[GenerateShapeFor<TerminalsPrepareParams>]
[GenerateShapeFor<TerminalPrepareResult>]
[GenerateShapeFor<TerminalsApproveParams>]
[GenerateShapeFor<TerminalApproveResult>]
[GenerateShapeFor<TerminalsCreateParams>]
[GenerateShapeFor<TerminalDto>]
[GenerateShapeFor<TerminalRefParams>]
[GenerateShapeFor<TerminalBufferDto>]
[GenerateShapeFor<TerminalsWriteParams>]
[GenerateShapeFor<TerminalsResizeParams>]
[GenerateShapeFor<TerminalSnapshotDto>]
[GenerateShapeFor<TerminalDataDto>]
[GenerateShapeFor<TerminalExitDto>]
[GenerateShapeFor<TerminalErrorDto>]
public sealed partial class PayloadShapes;
