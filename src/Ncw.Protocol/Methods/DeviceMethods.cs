using PolyType;

namespace Ncw.Protocol.Methods;

/// <summary>没有参数 / 没有结果。</summary>
public sealed record Unit
{
    public static readonly Unit Value = new();
}

/// <summary><c>device.info</c> 的结果。</summary>
/// <param name="Relay">设备配置的中继地址(<c>wss://…</c>),没配置为 null。已配对的控制端据此在设备后来才配置中继时「学到」它(带默认值的末尾字段)。</param>
public sealed record DeviceInfoResult(string DeviceId, string Name, string Kind, string AgentVersion, string Os, string DataRoot, string? Relay = null);

/// <summary>
/// 设备上的一个项目(执行环境 + 根目录)。<see cref="Environment"/> 是 <c>local</c> / <c>ssh</c>;SSH 项目的 <see cref="Path"/> 是<b>远程主机上的路径</b>,
/// <see cref="EnvironmentId"/> 是它绑定的环境(设备 + 这个 id = 执行环境标识),<see cref="EnvironmentName"/> 是环境的显示名。本机项目的 <see cref="EnvironmentId"/> 为 null。
/// </summary>
public sealed record ProjectDto(string WorkspaceId, string Name, string Path, string Environment, string? EnvironmentName, long CreatedAt, string? EnvironmentId = null);

public sealed record ProjectsListResult(ProjectDto[] Projects, long Revision);

/// <summary>
/// 添加项目。<see cref="EnvironmentId"/> 缺省(或 <c>local</c>)= 本机目录;给了 SSH 环境的 id = 这个环境里的目录(<see cref="Path"/> 是远程路径,必须是已存在的目录)。
/// 添加 SSH 项目会先连接环境(首次需要确认主机指纹、提供凭据,以交互出现),所以可能等一段时间。
/// </summary>
/// <remarks>
/// <b>克隆(02 A6「工作区来源」)</b>:给了 <see cref="CloneUrl"/> 时,<see cref="Path"/> 是<b>要创建的目标目录</b>(不存在,或是空目录),设备经项目环境的 git 把仓库克隆到那里,再把它登记成项目。
/// 只接受 <c>https://</c> 地址(不接受 <c>file:</c> / <c>ext::</c> / <c>ssh:</c> 等,SSH 部署密钥是后续项);私有仓库先用 <c>git.credentials.set</c> 给这个主机存一个令牌。
/// 云端 Agent 上相对路径从工作区根(启动配置的 <c>workspacesRoot</c>)解析,目标必须在它里面。<see cref="CloneBranch"/> 空 = 默认分支。
/// 克隆失败返回 <c>conflict</c>(<c>git.failed</c>,<c>error.details</c> 带 git 的 stderr,令牌已被抹掉)、地址不合法 <c>invalid_request</c>(<c>projects.clone.url</c>)、
/// 被出网策略拒绝 <c>forbidden</c>(<c>cloud.egress.denied</c>);失败时不留下半成品目录。克隆和 <c>Path</c> 的其他语义(SSH 环境)不能同时用。
/// </remarks>
public sealed record ProjectsAddParams(string Path, string? Name, string? EnvironmentId = null, string? CloneUrl = null, string? CloneBranch = null);

public sealed record ProjectsRemoveParams(string WorkspaceId);

/// <summary>运行中的顶层 Run(待处理交互数含所有后代)。</summary>
public sealed record ActiveRunDto(string RunId, string SessionId, string WorkspaceId, int PendingInteractions);

/// <summary>
/// <c>device</c> 主题的状态 = 权威广播(docs/design/09 §8.2)。首帧和后续都由设备上同一个函数生成(Core 的 <c>ActiveRunIndexBuilder</c>)。
/// <see cref="Runs"/> 只含顶层 Run,子 Run 在 <see cref="Subagents"/>;<see cref="Rev"/> 单调递增(0 = 没有 Run 在运行过的设备),控制端只收更新的那份。
/// </summary>
public sealed record DeviceTopicState(ActiveRunDto[] Runs, long ProjectsRevision)
{
    public ActiveSubagentDto[] Subagents { get; init; } = [];

    public long Rev { get; init; }
}

/// <summary><c>device</c> 主题的事件类型。</summary>
public static class DeviceEvents
{
    /// <summary>项目列表变了(数据是新的 <see cref="ProjectsListResult"/>)。</summary>
    public const string ProjectsChanged = "projects.changed";

    /// <summary>活动 Run 索引变了(数据是新的 <see cref="DeviceTopicState"/>)。</summary>
    public const string ActiveRunsChanged = "activeRuns.changed";
}

/// <summary>
/// 设备数据的状态(<c>data</c> 主题的快照,也是 <c>data.changed</c> 事件的数据)。字段都是结构化的代码,
/// 界面文字由控制端生成。数据没就绪之前,依赖数据的方法返回 <c>unavailable</c>(可重试)。
/// </summary>
/// <param name="State"><see cref="DataStates"/> 之一。</param>
/// <param name="Step">接管进行中时的步骤(<see cref="DataSteps"/>)。</param>
/// <param name="Reason">blocked / failed 的原因(<see cref="DataReasons"/>)。</param>
/// <param name="TakenOver">本次启动刚完成的 2.x 接管的摘要;没有接管时为 null。</param>
public sealed record DataStatus(string State, string? Step, string? Reason, TakeoverSummary? TakenOver);

/// <summary>2.x 接管完成后的摘要(欢迎页显示「已从 2.x 迁移 N 个项目、M 个会话」)。</summary>
public sealed record TakeoverSummary(long Projects, long Sessions, long CredentialsToReenter);

/// <summary>
/// 设置 · 数据 · 回退到 2.x(docs/design/04 §5)。<see cref="Available"/>:这份数据是从 2.x 接管来的、接管前的备份还在;
/// <see cref="Pending"/>:已经请求回退,Agent 下一次关库时(优雅退出的最后一步)执行。
/// </summary>
/// <param name="TakenOverAt">接管提交的时刻(Unix 毫秒);不能回退时为 null。</param>
public sealed record DataRollbackInfo(bool Available, long? TakenOverAt, bool Pending);

/// <param name="AcknowledgeDataLoss">用户确认过「迁移之后在 3.0 里写的数据不会带回 2.x」(那份库会另存,不删)。必须为 true。</param>
public sealed record DataRollbackParams(bool AcknowledgeDataLoss);

public static class DataStates
{
    public const string Preparing = "preparing";
    public const string TakingOver = "taking-over";
    public const string Ready = "ready";

    /// <summary>需要用户处理后重试(例如先退出 2.x)。</summary>
    public const string Blocked = "blocked";

    /// <summary>出错了;接管提交前的失败已经自动回退,可以重试。</summary>
    public const string Failed = "failed";

    /// <summary>
    /// 这份数据已经回退给 2.x(设置 · 数据 · 回退到 2.x)。3.0 不自动再接管:用户明确调用 <c>data.retry</c>(界面上是「重新迁移」)才会再接管一次。
    /// 旧控制端不认识这个值时按未知状态处理,不会误判成就绪。
    /// </summary>
    public const string RolledBack = "rolled-back";
}

public static class DataSteps
{
    public const string BackingUp = "backing-up";
    public const string Migrating = "migrating";
    public const string Committing = "committing";

    /// <summary>把 2.x 的旧 safeStorage 凭据转成 NCK1(macOS 上系统可能弹出钥匙串授权框)。</summary>
    public const string Credentials = "credentials";
}

public static class DataReasons
{
    public const string LegacyAppRunning = "legacy-app-running";
    public const string LegacyServiceRunning = "legacy-service-running";
    public const string OwnerUnknown = "owner-unknown";
    public const string UnsupportedLayout = "unsupported-layout";
    public const string BackupInvalid = "backup-invalid";
    public const string JournalCorrupt = "journal-corrupt";
    public const string NewerSchema = "newer-schema";
    public const string Conflict = "conflict";
    public const string Internal = "internal";
}

/// <summary><c>data</c> 主题的事件类型。</summary>
public static class DataEvents
{
    /// <summary>数据状态变了(数据是新的 <see cref="DataStatus"/>,全量)。</summary>
    public const string Changed = "data.changed";
}

/// <summary><c>pair.begin</c>:控制端报上名字,设备回 SRP 的 salt 和 B。</summary>
public sealed record PairBeginParams(string ClientName);

public sealed record PairBeginResult(byte[] Salt, byte[] B);

/// <summary><c>pair.finish</c>:控制端的 SRP 公钥 A、证据 M1、会话绑定证明。</summary>
public sealed record PairFinishParams(byte[] A, byte[] M1, byte[] Proof);

/// <summary>配对成功:设备的 SRP 证据 M2、绑定证明、给这个控制端的角色、中继路由令牌(中继上线后用)。</summary>
/// <param name="Relay">设备当前配置的中继地址(<c>wss://…</c>),控制端记下来,离开局域网后经它连接;没配置为 null。带默认值的末尾字段:旧载荷照常解码。</param>
public sealed record PairFinishResult(byte[] M2, byte[] Proof, string Role, string RouteToken, string? Relay = null);

/// <summary><c>pair.claim</c>:扫码配对。出示二维码里的一次性密钥,不需要 SRP(二维码本身是可信的带外通道)。</summary>
public sealed record PairClaimParams(string ClientName, byte[] Token);

public sealed record PairClaimResult(string Role, string RouteToken, string? Relay = null);

/// <summary><c>pair.createQr</c> 的结果:二维码内容(<c>nextcowork://pair?…</c>)和过期时间(Unix 毫秒)。</summary>
public sealed record PairQrResult(string Uri, long ExpiresAt);

/// <summary>远程开关的状态(<c>remote</c> 主题的快照,只给本机通道)。</summary>
/// <param name="Port">实际监听的端口;没打开时为 null。</param>
/// <param name="Addresses">本机的局域网地址。</param>
/// <param name="PairingCode">当前配对码(6 位);没打开时为 null。</param>
/// <param name="Error">打开失败的原因代码(例如端口全被占用);正常为 null。</param>
/// <param name="RelayUrl">配置的中继地址;没配置(默认,中继关闭)为 null。带默认值的末尾字段(09 §9)。</param>
/// <param name="RelayState">到中继的注册状态:<c>off</c>(没配置或远程开关关着)/ <c>connecting</c> / <c>registered</c> / <c>failed</c>;没有中继地址为 null。</param>
/// <param name="RelayError">状态是 <c>failed</c> 时的原因(<c>RelayErrors</c> 里的错误码);否则为 null。</param>
public sealed record RemoteStatus(bool Enabled, int? Port, int PreferredPort, bool LanOnly, string[] Addresses, string? PairingCode, string? Error,
    string? RelayUrl = null, string? RelayState = null, string? RelayError = null);

public sealed record RemoteSetEnabledParams(bool Enabled);

/// <summary>一个已配对的控制端。</summary>
public sealed record TrustedControllerDto(string Fingerprint, string Name, string Role, long PairedAt, long? LastSeenAt, bool Connected);

/// <summary><c>remote</c> 主题的状态:开关 + 已配对的控制端。</summary>
public sealed record RemoteState(RemoteStatus Status, TrustedControllerDto[] Controllers);

public sealed record TrustRevokeParams(string Fingerprint);

/// <summary>
/// <c>remote.configure</c>:改端口或「仅允许局域网地址」(null 表示不改)。正在监听时会重新监听,远程连接随之断开重连。
/// <paramref name="RelayUrl"/>:中继地址(<c>wss://…</c>;<c>ws://</c> 只限本机 / 局域网);null = 不改,空串 = 关闭中继。
/// 远程开关打开且配了地址时,设备向中继注册并接收会话;格式不对是 <c>invalid_request</c>。带默认值的末尾字段(09 §9)。
/// </summary>
public sealed record RemoteConfigureParams(int? Port, bool? LanOnly, string? RelayUrl = null);

/// <summary><c>trust.setRole</c>:改一个已配对控制端的角色(owner / member / viewer)。它当前的连接会断开,重连后按新角色。</summary>
public sealed record TrustSetRoleParams(string Fingerprint, string Role);

/// <summary><c>remote</c> 主题的事件类型。</summary>
public static class RemoteEvents
{
    /// <summary>开关、配对码或已配对的控制端变了(数据是新的 <see cref="RemoteState"/>,全量)。</summary>
    public const string Changed = "remote.changed";
}

/// <summary>M2 的方法表。按领域分文件,完整清单在功能盘点(Q15)后补齐。</summary>
public static class DeviceMethods
{
    public static readonly MethodDef<Unit, DeviceInfoResult> Info = Query<Unit, DeviceInfoResult>("device.info", Role.Viewer);

    public static readonly MethodDef<Unit, ProjectsListResult> ProjectsList = Query<Unit, ProjectsListResult>("projects.list", Role.Viewer);

    public static readonly MethodDef<ProjectsAddParams, ProjectDto> ProjectsAdd = Command<ProjectsAddParams, ProjectDto>("projects.add", Role.Member);

    public static readonly MethodDef<ProjectsRemoveParams, Unit> ProjectsRemove = Command<ProjectsRemoveParams, Unit>("projects.remove", Role.Member);

    public static readonly MethodDef<Unit, DataStatus> DataStatus = Query<Unit, DataStatus>("data.status", Role.Viewer);

    /// <summary>重试被挡住或失败的数据准备(例如用户退出 2.x 之后)。只允许本机通道的 owner。</summary>
    public static readonly MethodDef<Unit, DataStatus> DataRetry =
        new("data.retry", MethodKind.Command, Role.Owner, Transports.Local, PayloadShapes.GetShape<Unit>(), PayloadShapes.GetShape<DataStatus>());

    /// <summary>能不能回退到 2.x、有没有已经请求的回退。只在本机给 owner。</summary>
    public static readonly MethodDef<Unit, DataRollbackInfo> DataRollbackInfo = LocalOwner<Unit, DataRollbackInfo>("data.rollbackInfo", MethodKind.Query);

    /// <summary>
    /// 请求回退到 2.x:登记请求,Agent 在关库之后执行(控制端随后让 Agent 优雅退出并退出自己)。不能回退时 <c>invalid_request</c>(key <c>data.rollback.unavailable</c>),
    /// 没确认数据损失时 <c>invalid_request</c>(key <c>data.rollback.notAcknowledged</c>)。只在本机给 owner。
    /// </summary>
    public static readonly MethodDef<DataRollbackParams, DataRollbackInfo> DataRequestRollback = LocalOwner<DataRollbackParams, DataRollbackInfo>("data.requestRollback", MethodKind.Command);

    /// <summary>撤回还没执行的回退请求(例如退出被取消了)。只在本机给 owner。</summary>
    public static readonly MethodDef<Unit, DataRollbackInfo> DataCancelRollback = LocalOwner<Unit, DataRollbackInfo>("data.cancelRollback", MethodKind.Command);

    /// <summary>配对第一步。只有远程连接、未配对也能调用。</summary>
    public static readonly MethodDef<PairBeginParams, PairBeginResult> PairBegin =
        new("pair.begin", MethodKind.Command, Role.Unpaired, Transports.Remote, PayloadShapes.GetShape<PairBeginParams>(), PayloadShapes.GetShape<PairBeginResult>());

    public static readonly MethodDef<PairFinishParams, PairFinishResult> PairFinish =
        new("pair.finish", MethodKind.Command, Role.Unpaired, Transports.Remote, PayloadShapes.GetShape<PairFinishParams>(), PayloadShapes.GetShape<PairFinishResult>());

    /// <summary>扫码配对。只有远程连接、未配对也能调用;密钥一次性、10 分钟过期。</summary>
    public static readonly MethodDef<PairClaimParams, PairClaimResult> PairClaim =
        new("pair.claim", MethodKind.Command, Role.Unpaired, Transports.Remote, PayloadShapes.GetShape<PairClaimParams>(), PayloadShapes.GetShape<PairClaimResult>());

    /// <summary>生成配对二维码(含一次性密钥)。只在本机给 owner。</summary>
    public static readonly MethodDef<Unit, PairQrResult> PairCreateQr = LocalOwner<Unit, PairQrResult>("pair.createQr", MethodKind.Command);

    /// <summary>远程开关和配对码只能在设备本机上看和改(09 §8.4)。</summary>
    public static readonly MethodDef<Unit, RemoteStatus> RemoteGetStatus = LocalOwner<Unit, RemoteStatus>("remote.getStatus", MethodKind.Query);

    public static readonly MethodDef<RemoteSetEnabledParams, RemoteStatus> RemoteSetEnabled = LocalOwner<RemoteSetEnabledParams, RemoteStatus>("remote.setEnabled", MethodKind.Command);

    public static readonly MethodDef<Unit, RemoteStatus> RemoteRotateCode = LocalOwner<Unit, RemoteStatus>("remote.rotateCode", MethodKind.Command);

    public static readonly MethodDef<TrustRevokeParams, Unit> TrustRevoke = LocalOwner<TrustRevokeParams, Unit>("trust.revoke", MethodKind.Command);

    public static readonly MethodDef<RemoteConfigureParams, RemoteStatus> RemoteConfigure = LocalOwner<RemoteConfigureParams, RemoteStatus>("remote.configure", MethodKind.Command);

    public static readonly MethodDef<TrustSetRoleParams, Unit> TrustSetRole = LocalOwner<TrustSetRoleParams, Unit>("trust.setRole", MethodKind.Command);

    /// <summary>远程开关和已配对的控制端;含配对码,只给本机 owner。</summary>
    public static readonly TopicDef<RemoteState> RemoteTopic = new("remote", PayloadShapes.GetShape<RemoteState>(), Role.Owner, Transports.Local);

    public static readonly TopicDef<DeviceTopicState> DeviceTopic = new("device", PayloadShapes.GetShape<DeviceTopicState>());

    /// <summary>设备数据状态(权威广播:首帧和 <see cref="DataEvents.Changed"/> 用同一个类型)。</summary>
    public static readonly TopicDef<DataStatus> DataTopic = new("data", PayloadShapes.GetShape<DataStatus>());

    public static readonly IReadOnlyList<MethodDef> All =
    [
        Info, ProjectsList, ProjectsAdd, ProjectsRemove, DataStatus, DataRetry, DataRollbackInfo, DataRequestRollback, DataCancelRollback,
        PairBegin, PairFinish, PairClaim, PairCreateQr, RemoteGetStatus, RemoteSetEnabled, RemoteRotateCode, RemoteConfigure, TrustRevoke, TrustSetRole,
    ];

    private static MethodDef<TP, TR> LocalOwner<TP, TR>(string name, MethodKind kind) =>
        new(name, kind, Role.Owner, Transports.Local, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>());

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>());

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>());
}

/// <summary>所有载荷类型的形状(源生成)。新增 DTO 时在这里加一行。</summary>
[GenerateShapeFor<Unit>]
[GenerateShapeFor<DeviceInfoResult>]
[GenerateShapeFor<ProjectsListResult>]
[GenerateShapeFor<ProjectsAddParams>]
[GenerateShapeFor<ProjectsRemoveParams>]
[GenerateShapeFor<ProjectDto>]
[GenerateShapeFor<DeviceTopicState>]
[GenerateShapeFor<DataStatus>]
[GenerateShapeFor<DataRollbackInfo>]
[GenerateShapeFor<DataRollbackParams>]
[GenerateShapeFor<PairBeginParams>]
[GenerateShapeFor<PairBeginResult>]
[GenerateShapeFor<PairFinishParams>]
[GenerateShapeFor<PairFinishResult>]
[GenerateShapeFor<RemoteStatus>]
[GenerateShapeFor<RemoteSetEnabledParams>]
[GenerateShapeFor<RemoteState>]
[GenerateShapeFor<TrustRevokeParams>]
[GenerateShapeFor<PairClaimParams>]
[GenerateShapeFor<PairClaimResult>]
[GenerateShapeFor<PairQrResult>]
[GenerateShapeFor<RemoteConfigureParams>]
[GenerateShapeFor<TrustSetRoleParams>]
public sealed partial class PayloadShapes
{
    public static ITypeShape<T> GetShape<T>() =>
        (ITypeShape<T>)(GeneratedTypeShapeProvider.GetTypeShape(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T)} has no generated shape; add [GenerateShapeFor] to PayloadShapes"));
}
