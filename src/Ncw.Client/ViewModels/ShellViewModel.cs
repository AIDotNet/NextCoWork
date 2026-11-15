using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Accounts;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 桌面外壳。栏宽和栏的开关是需要持久化的界面偏好,唯一真源是 <see cref="ClientStores.Prefs"/>,
/// 这里的属性只是读写它的入口;窄窗口下的抽屉开关是不持久化的纯 UI 状态,留在本类。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel(ClientStores stores, TimeProvider? clock = null, IDialogService? dialogs = null, DeviceRegistry? devices = null, InspectorServices? inspector = null, IComposerLookups? lookups = null,
        ManagementPages? management = null, SchedulesViewModel? scheduledTasks = null, MobileHostServices? host = null,
        AccountService? account = null, LinkRouter? links = null, IAttachmentUploader? attachmentUploader = null, Ncw.Client.Updates.IUpdateService? updates = null)
    {
        Stores = stores;
        Host = host ?? new MobileHostServices();
        Ledger = new InteractionLedger();
        Dialogs = dialogs ?? NoDialogs.Instance;
        Devices = devices ?? new DeviceRegistry();
        Actions = new LayoutActions(stores, () => Text);
        Projects = new ProjectActions(stores, Devices, () => Dialogs, () => Text);
        Conversations = new ConversationActions(Devices, Ledger);
        Subagents = new SubagentActions(Devices);
        Goals = new GoalActions(Devices);
        Catalog = new CatalogActions(Devices);
        Providers = new ProvidersActions(Devices);
        Extensions = new ExtensionActions(Devices, stores);
        Files = new FilesActions(Devices);
        Git = new GitActions(Devices, stores);
        Review = new ReviewActions(Devices, stores);
        Terminals = new TerminalActions(Devices, stores);
        SettingsActions = new SettingsActions(Devices);
        Usage = new UsageActions(Devices);
        Schedules = new ScheduleActions(Devices);
        Storage = new StorageActions(Devices);
        Search = new SearchActions(Devices);
        Attachments = new AttachmentActions(Devices);
        Composer = new ComposerActions(Devices);
        Widgets = new WidgetActions(Devices, stores.Widgets);
        Gateway = new GatewayActions(Devices);
        Sessions = new SessionActions(stores, Devices, Conversations);
        Rail = new RailViewModel(stores, Actions, () => Dialogs, () => Text, Projects);
        Panel = new ProjectPanelViewModel(stores, Actions, Projects, Sessions, () => Dialogs, () => Text);
        Workspace = new WorkspaceViewModel(stores, Sessions);
        Conversation = new ConversationViewModel(stores, Conversations, Subagents, Goals, clock ?? TimeProvider.System, lookups ?? new ComposerLookups(Files, Extensions.Commands),
            new AttachmentTrayViewModel(attachmentUploader ?? Attachments, () => Host.AttachmentPicker),
            new Ncw.Client.Widgets.WidgetHostContext(stores, Widgets, () => Launcher, () => WidgetConfirmations, clock),
            new MessageActions(Devices, Sessions, Conversations), new AttachmentImageSource(Attachments));
        Inspector = new InspectorViewModel(stores, clock ?? TimeProvider.System, inspector ?? new InspectorServices(Files, Git, Review, Terminals, Subagents));
        DataGate = new DataGateViewModel(stores, Devices, new DataActions(Devices));
        DeviceActions = new DeviceActions(Devices, clock);
        Account = new AccountViewModel(stores, account);
        Links = links;
        Settings = new SettingsViewModel(stores, Devices, DeviceActions,
            new ModelsSettingsViewModel(stores, Providers, Catalog, () => Dialogs, () => Text, () => Launcher, clock),
            management ?? new ManagementPages(stores, SettingsActions, Extensions.Skills, Extensions.Commands, Extensions.Hooks, Extensions.Mcp, Usage, Storage, Gateway,
                () => Dialogs, () => Launcher, clock ?? TimeProvider.System, Extensions.Plugins),
            Account, new AboutViewModel(stores, new UpdateActions(updates), clock));
        Onboarding = new OnboardingViewModel(stores, Account, DataGate, Settings, () => Projects.AddLocalProjectAsync());
        // 插件市场安装需要账户令牌(plugins.authRequired):提示卡上的「去登录」打开设置的「账户」标签
        Settings.Pages.Plugins.SignInRequested += () => OpenAccountSettingsCommand.Execute(null);
        ScheduledTasks = scheduledTasks ?? new SchedulesViewModel(stores, Schedules, clock ?? TimeProvider.System);
        Panel.ScheduledTasksRequested += () => ScheduledTasks.OpenCommand.Execute(null);
        // 手机壳(docs/design/03 §3):同一套 ViewModel,导航栈、收件箱、设备页、扫码配对、推送登记只是多出来的几个
        Navigation = new MobileNavigationViewModel(Sessions, Actions, Conversation, Settings);
        Inbox = new InboxViewModel(stores, Navigation, Ledger, clock);
        DevicesPage = new DevicesPageViewModel(stores, DeviceActions, Navigation);
        QrPairing = new QrPairingViewModel(() => Host.QrScanner, DeviceActions.PairWithQrAsync, clock);
        Push = new PushViewModel(stores, new PushActions(Devices), () => Host.Push, typeof(ShellViewModel).Assembly.GetName().Version?.ToString());
        stores.Prefs.Changed += OnPrefsChanged;
    }

    public ClientStores Stores { get; }

    /// <summary>移动端宿主提供的平台服务(扫码、推送令牌、附件选择);桌面端和测试里全是 null,界面据此降级。</summary>
    public MobileHostServices Host { get; }

    /// <summary>本控制端自己回答过哪些交互(收件箱据此区分「自己处理」和「已在另一台设备上处理」)。</summary>
    public InteractionLedger Ledger { get; }

    /// <summary>手机壳的导航状态(标签、每个标签的页面栈)。</summary>
    public MobileNavigationViewModel Navigation { get; }

    /// <summary>活动收件箱:所有设备上「待我处理」。</summary>
    public InboxViewModel Inbox { get; }

    /// <summary>设备页(所有设备的状态、添加入口)。</summary>
    public DevicesPageViewModel DevicesPage { get; }

    /// <summary>扫码配对的状态机。</summary>
    public QrPairingViewModel QrPairing { get; }

    /// <summary>推送令牌登记。</summary>
    public PushViewModel Push { get; }

    public LayoutActions Actions { get; }

    /// <summary>项目的增删(经由设备)。</summary>
    public ProjectActions Projects { get; }

    /// <summary>对话:新建会话、发送、停止、回答交互、排队消息(经由设备;状态经设备推送进 <see cref="ClientStores.Conversations"/>)。</summary>
    public ConversationActions Conversations { get; }

    /// <summary>子代理:定义的列表 / 诊断 / 启停、停止、转后台、汇报、点开完整记录(经由设备;卡片状态经设备推送进 <c>ClientStores.Conversations.Subagents</c>)。</summary>
    public SubagentActions Subagents { get; }

    /// <summary>目标:设定 / 清除 / 读取(经由设备;状态经 <c>goal</c> 主题进 <c>ClientStores.Conversations.Goals</c>)。</summary>
    public GoalActions Goals { get; }

    /// <summary>模型目录:查询、刷新、覆盖、自动更新设置(经由设备;状态经 <c>catalog</c> 主题进 <c>ClientStores.Catalog</c>)。</summary>
    public CatalogActions Catalog { get; }

    /// <summary>设备 / 项目设置的写入(经由设备;状态经 <c>settings</c> 主题进 <c>ClientStores.Settings</c>)。「本应用」作用域的设置是 <c>Prefs</c>,不经设备。</summary>
    public SettingsActions SettingsActions { get; }

    /// <summary>用量查询(只读;结果是统计页自己的状态,不进 Store)。</summary>
    public UsageActions Usage { get; }

    /// <summary>定时任务(经由设备;任务与运行经 <c>schedules</c> 主题进 <c>ClientStores.Schedules</c>)。</summary>
    public ScheduleActions Schedules { get; }

    /// <summary>存储维护:统计、整理、备份、清理、导出导入(危险操作 owner + 仅本机 + 二次确认令牌)。</summary>
    public StorageActions Storage { get; }

    /// <summary>全局会话搜索(只读)。</summary>
    public SearchActions Search { get; }

    /// <summary>附件:分块上传 / 移除 / 恢复草稿 / 按引用下载。</summary>
    public AttachmentActions Attachments { get; }

    /// <summary>输入框补全:建议下一步 / 历史指令 / 打字续写(一切失败都是「没有建议」)。</summary>
    public ComposerActions Composer { get; }

    /// <summary>内联可视化小组件:状态 / 提交 / 动作。</summary>
    public WidgetActions Widgets { get; }

    public GatewayActions Gateway { get; }

    /// <summary>供应商:配置、凭据(只写不读)、模型别名、账号、OAuth(经由设备;状态经 <c>providers</c> 主题进 <c>ClientStores.Providers</c>)。</summary>
    public ProvidersActions Providers { get; }

    /// <summary>扩展:Skills / 命令 / Hooks / MCP(经由设备;清单镜像在 <c>ClientStores.Skills / Commands / Hooks</c>,MCP 连接状态经 <c>mcp</c> 主题进 <c>ClientStores.Mcp</c>,修订号经 <c>extensions</c> 主题驱动重拉)。</summary>
    public ExtensionActions Extensions { get; }

    /// <summary>项目文件:列目录、读 / 范围读、写、变更、文件名建议、内容搜索、markdown 资源(经由设备;结果是文件树 / 编辑器自己的状态,不进 Store)。</summary>
    public FilesActions Files { get; }

    /// <summary>Git:概览、分支、提交、diff、暂存、提交、拉取 / 推送、生成提交信息(经由设备;概览镜像在 <c>ClientStores.Git</c>)。</summary>
    public GitActions Git { get; }

    /// <summary>改动审查:变更集、文件 diff、撤销 / 重做(经由设备;变更集摘要镜像在 <c>ClientStores.Review</c>)。</summary>
    public ReviewActions Review { get; }

    /// <summary>终端:创建 / 批准 / 写入 / 调整大小 / 终止 / 关闭、订阅输出(经由设备;终端元数据镜像在 <c>ClientStores.Terminals</c>,输出字节经 <c>ITerminalSink</c>)。</summary>
    public TerminalActions Terminals { get; }

    /// <summary>会话列表:新建、打开、重命名、归档、收藏、删除(经由设备;列表镜像在 <c>ClientStores.Workspace.Sessions</c>)。</summary>
    public SessionActions Sessions { get; }

    /// <summary>连着的设备。</summary>
    public DeviceRegistry Devices { get; }

    /// <summary>弹窗服务。UI 层装配时换成 Arc Dialog 的实现。</summary>
    public IDialogService Dialogs { get; set; }

    /// <summary>打开系统浏览器(OAuth 授权页)。UI 层装配时换成 Avalonia Launcher 的实现。</summary>
    public IUrlLauncher Launcher { get; set; } = NoUrlLauncher.Instance;

    /// <summary>内联可视化卡片里「打开外链 / 跑动作」的确认框。UI 层装配时换成 Arc Dialog 的实现;默认全部拒绝(没有界面就没有人点头)。</summary>
    public Ncw.Client.Widgets.IWidgetConfirmations WidgetConfirmations { get; set; } = Ncw.Client.Widgets.NoWidgetConfirmations.Instance;

    /// <summary>Client 层要给用户看的文字。UI 层装配时换成按当前语言的实现。</summary>
    public IClientText Text { get; set; } = DefaultClientText.Instance;

    public RailViewModel Rail { get; }

    public ProjectPanelViewModel Panel { get; }

    public WorkspaceViewModel Workspace { get; }

    /// <summary>当前打开的会话:转录、待办、目标条、输入框。</summary>
    public ConversationViewModel Conversation { get; }

    /// <summary>右侧面板:默认文件管理器。</summary>
    public InspectorViewModel Inspector { get; }

    /// <summary>启动时的数据页(接管 2.x 数据的进度、被挡住时的重试)。</summary>
    public DataGateViewModel DataGate { get; }

    /// <summary>设备相关的意图(添加 / 移除设备、远程开关)。</summary>
    public DeviceActions DeviceActions { get; }

    /// <summary>设置页。</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>本控制端的账户(登录 / 团队 / 退出;设置页的「账户」标签、首次启动和状态条共用这一份)。</summary>
    public AccountViewModel Account { get; }

    /// <summary>首次启动流程(欢迎 / 登录或离线 / 数据接管 / 选模型供应商 / 完成)。账户还没做过决定时才显示。</summary>
    public OnboardingViewModel Onboarding { get; }

    /// <summary><c>nextcowork://</c> 链接的路由(没接线的外壳为 null)。</summary>
    public LinkRouter? Links { get; }

    /// <summary>侧栏「定时任务」页(盖在主工作区上)。</summary>
    public SchedulesViewModel ScheduledTasks { get; }

    private UiPrefs Prefs => Stores.Prefs.State;

    /// <summary>⌘B:收起 / 展开左侧区域(项目栏 + 项目面板)。</summary>
    public bool IsSidebarOpen
    {
        get => Prefs.IsSidebarOpen;
        set => Stores.Prefs.Update(p => p with { IsSidebarOpen = value });
    }

    /// <summary>⌘⌥I:右侧面板。</summary>
    public bool IsInspectorOpen
    {
        get => Prefs.IsInspectorOpen;
        set => Stores.Prefs.Update(p => p with { IsInspectorOpen = value });
    }

    /// <summary>左侧区域宽度(拖分隔条调整)。</summary>
    public double SidebarWidth
    {
        get => Prefs.SidebarWidth;
        set => Stores.Prefs.Update(p => p with { SidebarWidth = Math.Clamp(value, UiPrefs.SidebarMinWidth, UiPrefs.SidebarMaxWidth) });
    }

    /// <summary>右侧面板宽度(拖分隔条调整)。</summary>
    public double InspectorWidth
    {
        get => Prefs.InspectorWidth;
        set => Stores.Prefs.Update(p => p with { InspectorWidth = Math.Clamp(value, UiPrefs.InspectorMinWidth, UiPrefs.InspectorMaxWidth) });
    }

    /// <summary>窗口是否窄到左侧区域只能以抽屉出现(由视图按断点设置)。</summary>
    [ObservableProperty] public partial bool IsCompact { get; set; }

    /// <summary>窄窗口下左侧抽屉是否打开(纯 UI 状态,不持久化)。</summary>
    [ObservableProperty] public partial bool IsDrawerOpen { get; set; }

    /// <summary>窗口是否窄到检查器停靠不下(&lt; 1440,由视图按断点设置):此时检查器只能以抽屉出现(docs/design/03 §2)。</summary>
    [ObservableProperty] public partial bool IsInspectorNarrow { get; set; }

    /// <summary>窄窗口下右侧检查器抽屉是否打开(纯 UI 状态,不持久化;停靠偏好 <see cref="IsInspectorOpen"/> 不受影响)。</summary>
    [ObservableProperty] public partial bool IsInspectorDrawerOpen { get; set; }

    /// <summary>⌘B:宽窗口收起 / 展开左侧区域;窄窗口打开 / 关闭抽屉。</summary>
    [RelayCommand]
    private void ToggleSidebar()
    {
        if (IsCompact)
        {
            IsDrawerOpen = !IsDrawerOpen;
        }
        else
        {
            IsSidebarOpen = !IsSidebarOpen;
        }
    }

    /// <summary>点遮罩 / Esc:关掉打开着的抽屉(左侧区域或检查器)。</summary>
    [RelayCommand]
    private void CloseDrawer()
    {
        IsDrawerOpen = false;
        IsInspectorDrawerOpen = false;
    }

    /// <summary>账户状态条上的「去选择 / 打开账户设置」:打开设置页的「账户」标签。</summary>
    [RelayCommand]
    private void OpenAccountSettings()
    {
        Settings.Section = SettingsSection.Account;
        if (!Settings.IsOpen)
        {
            Settings.OpenCommand.Execute(null);
        }
    }

    /// <summary>⌘⌥I:宽窗口停靠 / 收起检查器(偏好);窄窗口打开 / 关闭检查器抽屉。</summary>
    [RelayCommand]
    private void ToggleInspector()
    {
        if (IsInspectorNarrow)
        {
            IsInspectorDrawerOpen = !IsInspectorDrawerOpen;
        }
        else
        {
            IsInspectorOpen = !IsInspectorOpen;
        }
    }

    [RelayCommand]
    private void SelectByIndex(int oneBased) => Actions.SelectByIndex(oneBased);

    partial void OnIsCompactChanged(bool value)
    {
        if (!value)
        {
            IsDrawerOpen = false;
        }
    }

    partial void OnIsInspectorNarrowChanged(bool value)
    {
        if (!value)
        {
            IsInspectorDrawerOpen = false;
        }
    }

    private void OnPrefsChanged()
    {
        OnPropertyChanged(nameof(IsSidebarOpen));
        OnPropertyChanged(nameof(IsInspectorOpen));
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(InspectorWidth));
    }
}
