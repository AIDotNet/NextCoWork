using PolyType;

namespace Ncw.Protocol.Methods;

/// <summary>没有参数 / 没有结果。</summary>
public sealed record Unit
{
    public static readonly Unit Value = new();
}

/// <summary><c>device.info</c> 的结果。</summary>
public sealed record DeviceInfoResult(string DeviceId, string Name, string Kind, string AgentVersion, string Os, string DataRoot);

/// <summary>设备上的一个项目(执行环境 + 根目录)。</summary>
public sealed record ProjectDto(string WorkspaceId, string Name, string Path, string Environment, string? EnvironmentName, long CreatedAt);

public sealed record ProjectsListResult(ProjectDto[] Projects, long Revision);

public sealed record ProjectsAddParams(string Path, string? Name);

public sealed record ProjectsRemoveParams(string WorkspaceId);

/// <summary>运行中的顶层 Run(待处理交互数含所有后代)。</summary>
public sealed record ActiveRunDto(string RunId, string SessionId, string WorkspaceId, int PendingInteractions);

/// <summary>
/// <c>device</c> 主题的状态 = 权威广播(docs/design/09 §8.2)。首帧和后续都由设备上同一个函数生成。
/// </summary>
public sealed record DeviceTopicState(ActiveRunDto[] Runs, long ProjectsRevision);

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

public static class DataStates
{
    public const string Preparing = "preparing";
    public const string TakingOver = "taking-over";
    public const string Ready = "ready";

    /// <summary>需要用户处理后重试(例如先退出 2.x)。</summary>
    public const string Blocked = "blocked";

    /// <summary>出错了;接管提交前的失败已经自动回退,可以重试。</summary>
    public const string Failed = "failed";
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
public sealed record PairFinishResult(byte[] M2, byte[] Proof, string Role, string RouteToken);

/// <summary>远程开关的状态(<c>remote</c> 主题的快照,只给本机通道)。</summary>
/// <param name="Port">实际监听的端口;没打开时为 null。</param>
/// <param name="Addresses">本机的局域网地址。</param>
/// <param name="PairingCode">当前配对码(6 位);没打开时为 null。</param>
/// <param name="Error">打开失败的原因代码(例如端口全被占用);正常为 null。</param>
public sealed record RemoteStatus(bool Enabled, int? Port, int PreferredPort, bool LanOnly, string[] Addresses, string? PairingCode, string? Error);

public sealed record RemoteSetEnabledParams(bool Enabled);

/// <summary>一个已配对的控制端。</summary>
public sealed record TrustedControllerDto(string Fingerprint, string Name, string Role, long PairedAt, long? LastSeenAt, bool Connected);

/// <summary><c>remote</c> 主题的状态:开关 + 已配对的控制端。</summary>
public sealed record RemoteState(RemoteStatus Status, TrustedControllerDto[] Controllers);

public sealed record TrustRevokeParams(string Fingerprint);

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

    /// <summary>配对第一步。只有远程连接、未配对也能调用。</summary>
    public static readonly MethodDef<PairBeginParams, PairBeginResult> PairBegin =
        new("pair.begin", MethodKind.Command, Role.Unpaired, Transports.Remote, PayloadShapes.GetShape<PairBeginParams>(), PayloadShapes.GetShape<PairBeginResult>());

    public static readonly MethodDef<PairFinishParams, PairFinishResult> PairFinish =
        new("pair.finish", MethodKind.Command, Role.Unpaired, Transports.Remote, PayloadShapes.GetShape<PairFinishParams>(), PayloadShapes.GetShape<PairFinishResult>());

    /// <summary>远程开关和配对码只能在设备本机上看和改(09 §8.4)。</summary>
    public static readonly MethodDef<Unit, RemoteStatus> RemoteGetStatus = LocalOwner<Unit, RemoteStatus>("remote.getStatus", MethodKind.Query);

    public static readonly MethodDef<RemoteSetEnabledParams, RemoteStatus> RemoteSetEnabled = LocalOwner<RemoteSetEnabledParams, RemoteStatus>("remote.setEnabled", MethodKind.Command);

    public static readonly MethodDef<Unit, RemoteStatus> RemoteRotateCode = LocalOwner<Unit, RemoteStatus>("remote.rotateCode", MethodKind.Command);

    public static readonly MethodDef<TrustRevokeParams, Unit> TrustRevoke = LocalOwner<TrustRevokeParams, Unit>("trust.revoke", MethodKind.Command);

    /// <summary>远程开关和已配对的控制端;含配对码,只给本机 owner。</summary>
    public static readonly TopicDef<RemoteState> RemoteTopic = new("remote", PayloadShapes.GetShape<RemoteState>(), Role.Owner, Transports.Local);

    public static readonly TopicDef<DeviceTopicState> DeviceTopic = new("device", PayloadShapes.GetShape<DeviceTopicState>());

    /// <summary>设备数据状态(权威广播:首帧和 <see cref="DataEvents.Changed"/> 用同一个类型)。</summary>
    public static readonly TopicDef<DataStatus> DataTopic = new("data", PayloadShapes.GetShape<DataStatus>());

    public static readonly IReadOnlyList<MethodDef> All =
    [
        Info, ProjectsList, ProjectsAdd, ProjectsRemove, DataStatus, DataRetry,
        PairBegin, PairFinish, RemoteGetStatus, RemoteSetEnabled, RemoteRotateCode, TrustRevoke,
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
[GenerateShapeFor<PairBeginParams>]
[GenerateShapeFor<PairBeginResult>]
[GenerateShapeFor<PairFinishParams>]
[GenerateShapeFor<PairFinishResult>]
[GenerateShapeFor<RemoteStatus>]
[GenerateShapeFor<RemoteSetEnabledParams>]
[GenerateShapeFor<RemoteState>]
[GenerateShapeFor<TrustRevokeParams>]
public sealed partial class PayloadShapes
{
    public static ITypeShape<T> GetShape<T>() =>
        (ITypeShape<T>)(GeneratedTypeShapeProvider.GetTypeShape(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T)} has no generated shape; add [GenerateShapeFor] to PayloadShapes"));
}
