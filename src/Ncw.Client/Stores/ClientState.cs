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
    bool WorkingTreeClean,
    string? EnvironmentId = null);

/// <summary>项目在项目栏上的外观。属于用户布局,和 <see cref="RailLayout"/> 一起同步。</summary>
public sealed record ProjectAppearance(string? Glyph, string Color);

/// <summary>控制端看到的一台设备。</summary>
/// <param name="Data">设备数据状态(<c>data</c> 主题的镜像);没连上时为 null(未知)。</param>
/// <param name="Remote">远程开关和已配对的控制端(<c>remote</c> 主题的镜像,只有本机设备有);没连上时为 null。</param>
/// <param name="Role">本控制端在这台设备上的角色(<c>owner</c> / <c>member</c> / <c>viewer</c>,握手时得知);没连上时为 null。「完全访问」档位只有 owner 能用。</param>
public sealed record DeviceInfo(
    string DeviceId,
    string Name,
    DeviceKind Kind,
    DeviceConnection Connection,
    ActiveRunIndex? Index,
    Ncw.Protocol.Methods.DataStatus? Data = null,
    Ncw.Protocol.Methods.RemoteState? Remote = null,
    string? Role = null);

/// <summary>
/// 本控制端配对过的一台设备(docs/design/09 §3 第 5 步):设备 id、钉扎的公钥指纹、已知地址。
/// 这是控制端自己的事实,真源是 <see cref="ClientStores.Paired"/>,持久化在客户端目录。
/// </summary>
/// <param name="Fingerprint">设备公钥指纹(小写十六进制),连接时钉扎。</param>
/// <param name="RouteToken">中继路由令牌(经中继连接时出示)。</param>
/// <param name="Relay">
/// 设备的中继地址(<c>wss://…</c>,09 §4 的 <c>relay: route</c>,和 <see cref="RouteToken"/> 合起来就是中继路由);设备没配置中继为 null。
/// 配对时由设备告知;设备后来才配置中继的,控制端在局域网连上时从 <c>device.info</c> 学到并更新这里。
/// <see cref="Host"/> 为空 = 没有局域网地址(例如云端设备,只能走中继)。
/// </param>
public sealed record SavedDevice(string DeviceId, string Name, string Fingerprint, string Host, int Port, string Role, string RouteToken, long PairedAt, string? Relay = null);

/// <summary>会话列表里的一行(<c>sessions.list</c> 的镜像;完整会话由 session/&lt;id&gt; 主题提供)。运行状态不在这里,只由 activeRuns 回答。</summary>
public sealed record SessionSummary(string Id, ProjectRef Project, string Title, DateTimeOffset UpdatedAt, bool Archived = false, bool Favorited = false);

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
    /// <param name="clock">启动里程碑的时间戳用的时钟(默认系统时钟;测试传手动时钟)。</param>
    public ClientStores(TimeProvider? clock = null) => Startup = new StartupStore(clock);

    public Store<LayoutState> Layout { get; } = new(LayoutState.Empty);

    public Store<WorkspaceState> Workspace { get; } = new(WorkspaceState.Empty);

    /// <summary>对话链路的镜像:会话、Run、待处理的交互(设备推送 → <see cref="ConversationReducer"/> → 这里)。</summary>
    public Store<ConversationState> Conversations { get; } = new(ConversationState.Empty);

    /// <summary>各设备的模型目录镜像(<c>catalog</c> 主题;目录本身按需用 <c>CatalogActions.QueryAsync</c> 拉,不进 Store)。</summary>
    public Store<CatalogMirror> Catalog { get; } = new(CatalogMirror.Empty);

    /// <summary>各设备的供应商配置镜像(<c>providers</c> / <c>providers.oauth</c> 主题 + 模型选择器候选)。不含任何凭据。</summary>
    public Store<ProvidersMirror> Providers { get; } = new(ProvidersMirror.Empty);

    /// <summary>各设备上各项目的 git 概览镜像(<c>git.overview</c>;写操作之后由 <c>GitActions</c> 刷新)。</summary>
    public Store<GitMirror> Git { get; } = new(GitMirror.Empty);

    /// <summary>各设备上各 Run 的改动审查变更集摘要镜像(<c>review.changeSet</c>)。</summary>
    public Store<ReviewMirror> Review { get; } = new(ReviewMirror.Empty);

    /// <summary>各设备上终端会话表的镜像(<c>terminals.list</c> + <c>terminal/&lt;id&gt;</c> 的元数据)。</summary>
    public Store<TerminalMirror> Terminals { get; } = new(TerminalMirror.Empty);

    /// <summary>各设备的设备设置镜像(<c>settings</c> 主题,member 起)。本应用作用域的设置是 <see cref="Prefs"/>,不在这里。</summary>
    public Store<SettingsMirror> Settings { get; } = new(SettingsMirror.Empty);

    /// <summary>各设备的定时任务与运行镜像(<c>schedules</c> 主题,全量权威)。</summary>
    public Store<SchedulesMirror> Schedules { get; } = new(SchedulesMirror.Empty);

    /// <summary>各设备扩展域(Skills / 命令 / Hooks)的修订号镜像(<c>extensions</c> 主题):修订号前进 = 别处改了配置,持有这些列表的镜像据此重拉。</summary>
    public Store<ExtensionRevisionsMirror> ExtensionRevisions { get; } = new(ExtensionRevisionsMirror.Empty);

    /// <summary>各设备按项目取的 Skill 清单镜像(<c>skills.list</c> 的结果,经 <c>ExtensionActions</c> 合入;key 带 deviceId + 项目)。</summary>
    public Store<SkillsMirror> Skills { get; } = new(SkillsMirror.Empty);

    /// <summary>各设备按项目取的斜杠命令镜像。</summary>
    public Store<CommandsMirror> Commands { get; } = new(CommandsMirror.Empty);

    /// <summary>各设备按项目取的 Hook 配置镜像(不含任何环境变量的值)。</summary>
    public Store<HooksMirror> Hooks { get; } = new(HooksMirror.Empty);

    /// <summary>各设备的 MCP 服务器状态镜像(<c>mcp</c> 主题:全量 + 单调 Rev;不含任何密钥)。</summary>
    public Store<McpMirror> Mcp { get; } = new(McpMirror.Empty);

    /// <summary>各设备的插件镜像(<c>plugins</c> 主题:全量 + 单调 Rev,含挂起的插件提问;不含任何凭据)。</summary>
    public Store<PluginsMirror> Plugins { get; } = new(PluginsMirror.Empty);

    /// <summary>各设备的执行环境镜像(<c>environments</c> 主题:全量 + 单调 Rev,member 起;不含任何凭据,只有「有没有」)。设备断开标为未知。</summary>
    public Store<EnvironmentsMirror> Environments { get; } = new(EnvironmentsMirror.Empty);

    /// <summary>各设备上内联可视化卡片的镜像(小组件状态、提交进度;key 带 deviceId + 会话 + 调用 id)。卡片本身(代码 / 快照)在会话消息里,经转录派生,不在这里。</summary>
    public Store<Ncw.Client.Widgets.WidgetsMirror> Widgets { get; } = new(Ncw.Client.Widgets.WidgetsMirror.Empty);

    /// <summary>本控制端的账户会话(07 §2:每个控制端各自登录,各自持有;令牌在系统钥匙串里,不进 Store)。真源是这一个 Store,由 <see cref="Ncw.Client.Accounts.AccountService"/> 维护。</summary>
    public Store<AccountState> Account { get; } = new(AccountState.Initial);

    /// <summary>自更新的状态(当前版本、通道、检查 / 下载 / 待重启、失败原因、待装更新的强制更新策略)。真源是这一个 Store,由 <see cref="Ncw.Client.Updates.UpdateService"/> 维护;界面只读。</summary>
    public Store<Ncw.Client.Updates.UpdateState> Updates { get; } = new(Ncw.Client.Updates.UpdateState.Initial);

    /// <summary>启动事实(里程碑锁存、本机 Agent 守护的状态)。启动加载界面只读它(和设备 / 数据状态一起派生);真源规则见 <see cref="StartupState"/>。</summary>
    public StartupStore Startup { get; }

    public Store<SelectionState> Selection { get; } = new(SelectionState.None);

    /// <summary>本机界面偏好(持久化见 <see cref="Services.PrefsPersistence"/>)。</summary>
    public Store<UiPrefs> Prefs { get; } = new(UiPrefs.Default);

    /// <summary>本控制端配对过的远程设备(持久化见 <see cref="Services.IDeviceBook"/>)。</summary>
    public Store<ImmutableArray<SavedDevice>> Paired { get; } = new(ImmutableArray<SavedDevice>.Empty);
}
