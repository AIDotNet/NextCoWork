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

/// <summary>M2 的方法表。按领域分文件,完整清单在功能盘点(Q15)后补齐。</summary>
public static class DeviceMethods
{
    public static readonly MethodDef<Unit, DeviceInfoResult> Info = Query<Unit, DeviceInfoResult>("device.info", Role.Viewer);

    public static readonly MethodDef<Unit, ProjectsListResult> ProjectsList = Query<Unit, ProjectsListResult>("projects.list", Role.Viewer);

    public static readonly MethodDef<ProjectsAddParams, ProjectDto> ProjectsAdd = Command<ProjectsAddParams, ProjectDto>("projects.add", Role.Member);

    public static readonly MethodDef<ProjectsRemoveParams, Unit> ProjectsRemove = Command<ProjectsRemoveParams, Unit>("projects.remove", Role.Member);

    public static readonly TopicDef<DeviceTopicState> DeviceTopic = new("device", PayloadShapes.GetShape<DeviceTopicState>());

    public static readonly IReadOnlyList<MethodDef> All = [Info, ProjectsList, ProjectsAdd, ProjectsRemove];

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
public sealed partial class PayloadShapes
{
    public static ITypeShape<T> GetShape<T>() =>
        (ITypeShape<T>)(GeneratedTypeShapeProvider.GetTypeShape(typeof(T))
            ?? throw new InvalidOperationException($"{typeof(T)} has no generated shape; add [GenerateShapeFor] to PayloadShapes"));
}
