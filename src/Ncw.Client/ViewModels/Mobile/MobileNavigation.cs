using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Actions;
using Ncw.Core.Domain;

namespace Ncw.Client.ViewModels;

/// <summary>手机壳底部标签栏的四个页面(docs/design/03 §3)。</summary>
public enum MobileTab
{
    /// <summary>活动收件箱:跨设备「待我处理」。</summary>
    Inbox,

    /// <summary>会话:项目(按组)→ 项目的会话列表 → 对话。</summary>
    Sessions,

    /// <summary>设备:配对过的设备、添加设备(扫码 / IP+端口+配对码)。</summary>
    Devices,

    /// <summary>设置(导航栈):入口列表 → 各设置页。</summary>
    Settings,
}

/// <summary>导航栈里的一页。纯 UI 状态(页面只引用 id,不带事实快照):页面内容全部从 Store 派生。</summary>
public abstract record MobileRoute;

/// <summary>一个项目的会话列表。</summary>
public sealed record ProjectRoute(ProjectRef Project) : MobileRoute;

/// <summary>一个会话的对话页(这一页不显示底部标签栏,把空间留给输入框)。</summary>
public sealed record SessionRoute(ProjectRef Project, string SessionId) : MobileRoute;

/// <summary>设置里的一个分组页。</summary>
public sealed record SettingsRoute(SettingsSection Section) : MobileRoute;

/// <summary>设置里的「通知」页(推送令牌登记状态)。</summary>
public sealed record NotificationsRoute : MobileRoute;

/// <summary>扫码添加设备。</summary>
public sealed record QrPairRoute : MobileRoute;

/// <summary>用 IP:端口 + 配对码添加设备。</summary>
public sealed record ManualAddRoute : MobileRoute;

/// <summary>
/// 手机壳的导航状态:当前标签 + 每个标签各一个页面栈(切标签不丢各自的栈,同 Arc 文档「一个 NavigationStack 一个标签」)。
/// 栈是纯 UI 状态,不持久化;选中哪个项目 / 会话仍是 <c>ClientStores.Selection</c> 的事实,打开会话走 <see cref="SessionActions.Open"/>(一个意图一个方法)。
/// 视图只做两件事:把这里的栈同步到 Arc 的 NavigationStack(推入 / 弹出),以及把左缘右滑弹出的结果告诉这里(<see cref="Pop"/>)。
/// </summary>
public sealed partial class MobileNavigationViewModel : ObservableObject
{
    private readonly Dictionary<MobileTab, List<MobileRoute>> _stacks = Enum.GetValues<MobileTab>().ToDictionary(static t => t, static _ => new List<MobileRoute>());
    private readonly SessionActions? _sessions;
    private readonly LayoutActions? _layout;
    private readonly ConversationViewModel? _conversation;
    private readonly SettingsViewModel? _settings;

    public MobileNavigationViewModel(SessionActions? sessions = null, LayoutActions? layout = null, ConversationViewModel? conversation = null, SettingsViewModel? settings = null)
    {
        _sessions = sessions;
        _layout = layout;
        _conversation = conversation;
        _settings = settings;
    }

    [ObservableProperty] public partial MobileTab SelectedTab { get; private set; } = MobileTab.Inbox;

    /// <summary>某个标签的栈(不含根页:根页就是标签本身)。</summary>
    public IReadOnlyList<MobileRoute> StackOf(MobileTab tab) => _stacks[tab];

    public IReadOnlyList<MobileRoute> CurrentStack => _stacks[SelectedTab];

    /// <summary>栈顶的页面;在根页为 null。</summary>
    public MobileRoute? Top => CurrentStack.Count > 0 ? CurrentStack[^1] : null;

    public bool CanGoBack => CurrentStack.Count > 0;

    /// <summary>会话页不显示底部标签栏,把空间留给输入框(docs/design/03 §3)。</summary>
    public bool IsTabBarVisible => Top is not SessionRoute;

    /// <summary>某个标签的栈变了(推入 / 弹出 / 重置);视图据此同步 Arc 的 NavigationStack。</summary>
    public event Action<MobileTab>? StackChanged;

    /// <summary>
    /// 切标签。点已经选中的标签:栈里有页面就回到根页(iOS 惯例),已在根页就什么也不做。
    /// </summary>
    [RelayCommand]
    private void SelectTab(MobileTab tab)
    {
        if (tab == SelectedTab)
        {
            PopToRoot();
            return;
        }

        SelectedTab = tab;
        Notify(tab);
        if (tab == MobileTab.Settings)
        {
            _settings?.Devices.Refresh();
        }
    }

    public void Push(MobileRoute route)
    {
        _stacks[SelectedTab].Add(route);
        Notify(SelectedTab);
    }

    /// <summary>弹出栈顶(返回按钮、左缘右滑)。已在根页返回 false。</summary>
    public bool Pop()
    {
        var stack = _stacks[SelectedTab];
        if (stack.Count == 0)
        {
            return false;
        }

        stack.RemoveAt(stack.Count - 1);
        Notify(SelectedTab);
        return true;
    }

    /// <summary>
    /// 视图里 Arc 的栈已经自己弹出了一页(左缘右滑):把这个标签的栈也弹一页,但<b>不</b>再通知视图同步(它已经在位了)。
    /// </summary>
    public void PopSilently(MobileTab tab)
    {
        var stack = _stacks[tab];
        if (stack.Count == 0)
        {
            return;
        }

        stack.RemoveAt(stack.Count - 1);
        if (tab == SelectedTab)
        {
            OnPropertyChanged(nameof(CurrentStack));
            OnPropertyChanged(nameof(Top));
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(IsTabBarVisible));
        }
    }

    public void PopToRoot()
    {
        var stack = _stacks[SelectedTab];
        if (stack.Count == 0)
        {
            return;
        }

        stack.Clear();
        Notify(SelectedTab);
    }

    /// <summary>「会话」标签:点开一个项目 → 它的会话列表(同时是 <c>Selection</c> 的当前项目)。</summary>
    public void OpenProject(ProjectRef project)
    {
        _layout?.Select(project);
        SelectedTab = MobileTab.Sessions;
        _stacks[MobileTab.Sessions].Clear();
        _stacks[MobileTab.Sessions].Add(new ProjectRoute(project));
        Notify(MobileTab.Sessions);
    }

    /// <summary>
    /// 打开一个会话:切到「会话」标签,栈变成 项目 → 对话(返回键回到这个项目的会话列表)。点项目列表里的会话、点收件箱里的条目都走这里。
    /// <paramref name="focusRowKey"/> 非空(例如 <c>i:交互 id</c>)时对话页滚到并高亮那一行,行还没到(快照在路上)就等它到。
    /// </summary>
    public void OpenSession(ProjectRef project, string sessionId, string? focusRowKey = null)
    {
        _sessions?.Open(project, sessionId);
        SelectedTab = MobileTab.Sessions;
        var stack = _stacks[MobileTab.Sessions];
        stack.Clear();
        stack.Add(new ProjectRoute(project));
        stack.Add(new SessionRoute(project, sessionId));
        _conversation?.RequestFocus(focusRowKey);
        Notify(MobileTab.Sessions);
    }

    /// <summary>从会话列表新建会话并进入。</summary>
    public async Task NewSessionAsync(ProjectRef project)
    {
        if (_sessions is null)
        {
            return;
        }

        var result = await _sessions.CreateAsync(project);
        if (result.IsOk && result.Value is { } sessionId)
        {
            var stack = _stacks[MobileTab.Sessions];
            if (!stack.OfType<ProjectRoute>().Any())
            {
                stack.Add(new ProjectRoute(project));
            }

            stack.Add(new SessionRoute(project, sessionId));
            SelectedTab = MobileTab.Sessions;
            Notify(MobileTab.Sessions);
        }
    }

    /// <summary>「设备」标签的子页。</summary>
    public void OpenDevicesPage(MobileRoute route)
    {
        SelectedTab = MobileTab.Devices;
        _stacks[MobileTab.Devices].Add(route);
        Notify(MobileTab.Devices);
    }

    /// <summary>设置里推入一个分组页:先把分组交给设置页的 ViewModel(会拉数据),再推页面。</summary>
    public void OpenSettingsSection(SettingsSection section)
    {
        if (_settings is not null)
        {
            _settings.Section = section;
            _ = _settings.Pages.ActivateAsync(section);
        }

        SelectedTab = MobileTab.Settings;
        _stacks[MobileTab.Settings].Add(new SettingsRoute(section));
        Notify(MobileTab.Settings);
    }

    private void Notify(MobileTab tab)
    {
        OnPropertyChanged(nameof(CurrentStack));
        OnPropertyChanged(nameof(Top));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(IsTabBarVisible));
        StackChanged?.Invoke(tab);
    }
}

/// <summary>
/// 手机设置页的入口清单(docs/design/07):分组与顺序是纯数据,文字和图标由界面层按 <see cref="SettingsSection"/> 取。
/// 只列手机上有意义的已实现页面:「连接与远程」「开放网关」是本机 Agent 的开关,手机没有本机 Agent,所以不列。
/// </summary>
public static class MobileSettingsMenu
{
    /// <summary>本应用:设备、外观(动效级别);通知(<see cref="NotificationsRoute"/>)单独放,不是 <see cref="SettingsSection"/>。</summary>
    public static readonly IReadOnlyList<SettingsSection> App = [SettingsSection.Devices, SettingsSection.Appearance];

    public static readonly IReadOnlyList<SettingsSection> Device =
    [
        SettingsSection.General, SettingsSection.Models, SettingsSection.Skills, SettingsSection.Commands,
        SettingsSection.Hooks, SettingsSection.Mcp, SettingsSection.Usage, SettingsSection.Storage,
    ];

    public static readonly IReadOnlyList<SettingsSection> Project = [SettingsSection.Project];

    public static IEnumerable<SettingsSection> All => App.Concat(Device).Concat(Project);
}
