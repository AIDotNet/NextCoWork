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
/// <param name="Data">设备数据状态(<c>data</c> 主题的镜像);没连上时为 null(未知)。</param>
/// <param name="Remote">远程开关和已配对的控制端(<c>remote</c> 主题的镜像,只有本机设备有);没连上时为 null。</param>
public sealed record DeviceInfo(
    string DeviceId,
    string Name,
    DeviceKind Kind,
    DeviceConnection Connection,
    ActiveRunIndex? Index,
    Ncw.Protocol.Methods.DataStatus? Data = null,
    Ncw.Protocol.Methods.RemoteState? Remote = null);

/// <summary>
/// 本控制端配对过的一台设备(docs/design/09 §3 第 5 步):设备 id、钉扎的公钥指纹、已知地址。
/// 这是控制端自己的事实,真源是 <see cref="ClientStores.Paired"/>,持久化在客户端目录。
/// </summary>
/// <param name="Fingerprint">设备公钥指纹(小写十六进制),连接时钉扎。</param>
/// <param name="RouteToken">中继路由令牌(中继上线后使用)。</param>
public sealed record SavedDevice(string DeviceId, string Name, string Fingerprint, string Host, int Port, string Role, string RouteToken, long PairedAt);

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

    /// <summary>本机界面偏好(持久化见 <see cref="Services.PrefsPersistence"/>)。</summary>
    public Store<UiPrefs> Prefs { get; } = new(UiPrefs.Default);

    /// <summary>本控制端配对过的远程设备(持久化见 <see cref="Services.IDeviceBook"/>)。</summary>
    public Store<ImmutableArray<SavedDevice>> Paired { get; } = new(ImmutableArray<SavedDevice>.Empty);
}
