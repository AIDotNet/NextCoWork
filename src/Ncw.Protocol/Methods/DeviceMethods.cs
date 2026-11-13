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

    public static readonly TopicDef<DeviceTopicState> DeviceTopic = new("device", PayloadShapes.GetShape<DeviceTopicState>());

    /// <summary>设备数据状态(权威广播:首帧和 <see cref="DataEvents.Changed"/> 用同一个类型)。</summary>
    public static readonly TopicDef<DataStatus> DataTopic = new("data", PayloadShapes.GetShape<DataStatus>());

    public static readonly IReadOnlyList<MethodDef> All = [Info, ProjectsList, ProjectsAdd, ProjectsRemove, DataStatus, DataRetry];

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
public sealed partial class PayloadShapes
{
    public static ITypeShape<T> GetShape<T>() =>
        (ITypeShape<T>)(GeneratedTypeShapeProvider.GetTypeShape(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T)} has no generated shape; add [GenerateShapeFor] to PayloadShapes"));
}
