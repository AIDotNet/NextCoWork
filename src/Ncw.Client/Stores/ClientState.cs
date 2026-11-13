using System.Collections.Immutable;
using Ncw.Core.Domain;
using Ncw.Core.Layout;

namespace Ncw.Client.Stores;

/// <summary>设备上一个项目的事实(由设备持有,控制端只是镜像)。</summary>
public sealed record ProjectInfo(
    ProjectRef Ref,
    string Name,
    string Path,
    EnvironmentKind Environment,
    string? EnvironmentName,
    string? Branch,
    bool WorkingTreeClean);

/// <summary>项目在项目栏上的外观。属于用户布局,和 <see cref="RailLayout"/> 一起同步。</summary>
public sealed record ProjectAppearance(string? Glyph, string Color);

/// <summary>控制端看到的一台设备。</summary>
public sealed record DeviceInfo(
    string DeviceId,
    string Name,
    DeviceKind Kind,
    DeviceConnection Connection,
    ActiveRunIndex? Index);

/// <summary>会话列表里的一行(完整会话由 session/&lt;id&gt; 主题提供)。</summary>
public sealed record SessionSummary(string Id, ProjectRef Project, string Title, DateTimeOffset UpdatedAt);

/// <summary>「终端和智能体」分区里的一项。</summary>
public sealed record TerminalSummary(string Id, ProjectRef Project, string Title, TerminalKind Kind);

public enum TerminalKind
{
    Shell,
    ExternalAgent,
}

/// <summary>工作区里的 Git 变更类型。</summary>
public enum GitChange
{
    Modified,
    Added,
    Deleted,
    Renamed,
    Untracked,
}

/// <summary>项目里的一个文件或目录(相对项目根,用 / 分隔)。完整内容由 files.* 方法按需读取。</summary>
public sealed record FileEntry(string RelativePath, bool IsDirectory, GitChange? Change = null);

/// <summary>用户布局:项目栏、项目外观。通过账户配置同步(决策 D10)。</summary>
public sealed record LayoutState(RailLayout Rail, ImmutableDictionary<ProjectRef, ProjectAppearance> Appearance)
{
    public static readonly LayoutState Empty = new(RailLayout.Empty, ImmutableDictionary<ProjectRef, ProjectAppearance>.Empty);
}

/// <summary>所有设备上的项目、会话等事实的镜像。</summary>
public sealed record WorkspaceState(
    ImmutableDictionary<string, DeviceInfo> Devices,
    ImmutableDictionary<ProjectRef, ProjectInfo> Projects,
    ImmutableArray<SessionSummary> Sessions,
    ImmutableArray<TerminalSummary> Terminals,
    ImmutableDictionary<ProjectRef, ImmutableArray<FileEntry>> Files,
    ImmutableArray<SubagentRecord> Subagents)
{
    public static readonly WorkspaceState Empty = new(
        ImmutableDictionary<string, DeviceInfo>.Empty,
        ImmutableDictionary<ProjectRef, ProjectInfo>.Empty,
        ImmutableArray<SessionSummary>.Empty,
        ImmutableArray<TerminalSummary>.Empty,
        ImmutableDictionary<ProjectRef, ImmutableArray<FileEntry>>.Empty,
        ImmutableArray<SubagentRecord>.Empty);

    /// <summary>某个会话下的子代理(父会话转录里 transcript.subagents 的镜像)。</summary>
    public IEnumerable<SubagentRecord> SubagentsOf(string sessionId) =>
        Subagents.Where(s => s.ParentSessionId == sessionId);

    /// <summary>项目的活动状态,统一走 <see cref="ProjectActivityView"/>。</summary>
    public ProjectActivity ActivityOf(ProjectRef project) =>
        Devices.TryGetValue(project.DeviceId, out var device)
            ? ProjectActivityView.Of(project, device.Index, device.Connection)
            : new ProjectActivity(0, 0, Unknown: true);

    /// <summary>会话是否有运行中的 Run(会话列表的状态点)。</summary>
    public bool IsSessionRunning(SessionSummary session) =>
        Devices.TryGetValue(session.Project.DeviceId, out var device)
        && device.Connection == DeviceConnection.Connected
        && device.Index is { } index
        && index.Runs.Any(r => r.SessionId == session.Id);
}

/// <summary>纯 UI 状态以外、多个界面共享的「当前选择」。</summary>
public sealed record SelectionState(ProjectRef? Project, string? SessionId)
{
    public static readonly SelectionState None = new(null, null);
}

/// <summary>控制端的全部 Store。ViewModel 只读它们,修改只能经过 Actions。</summary>
public sealed class ClientStores
{
    public Store<LayoutState> Layout { get; } = new(LayoutState.Empty);

    public Store<WorkspaceState> Workspace { get; } = new(WorkspaceState.Empty);

    public Store<SelectionState> Selection { get; } = new(SelectionState.None);
}
