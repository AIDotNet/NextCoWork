using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using TKCode.Arc;

namespace Ncw.UI.Views.Mobile;

/// <summary>
/// 手机外壳:底部标签栏 + 每个标签一个 <see cref="NcwNavigationStack"/>。
/// 导航的真源是 <see cref="MobileNavigationViewModel"/>(每个标签的页面栈、当前标签,都是纯 UI 状态);这里把它同步到 Arc 的栈——
/// ViewModel 里栈变长就 Push 对应的页,变短就 Pop,公共前缀不动;左缘右滑 / 返回按钮在 Arc 的栈上弹出后,再把深度回写给 ViewModel。
/// 页面都是按路由现建的(状态全部来自 Store 派生,所以重建不丢东西),弹出时直接丢弃。
/// </summary>
public partial class MobileShell : UserControl
{
    private readonly Dictionary<MobileTab, NcwNavigationStack> _stacks = [];
    private readonly Dictionary<MobileTab, List<MobileRoute>> _shown = [];
    private ShellViewModel? _vm;
    private bool _syncing;

    public MobileShell()
    {
        InitializeComponent();
        foreach (var tab in Enum.GetValues<MobileTab>())
        {
            _stacks[tab] = new NcwNavigationStack();
            _shown[tab] = [];
            _stacks[tab].Navigated += (_, _) => OnStackNavigated(tab);
        }

        InboxTab.Icon = ArcIcons.Inbox;
        SessionsTab.Icon = ArcIcons.MessageSquare;
        DevicesTab.Icon = ArcIcons.Monitor;
        SettingsTab.Icon = ArcIcons.Settings;
        Tabs.SelectionChanged += OnTabSelected;
        DataContextChanged += (_, _) => Attach();
    }

    /// <summary>当前标签的栈里推入了几页(不含根页;测试用)。</summary>
    public int Depth(MobileTab tab) => _shown[tab].Count;

    /// <summary>Arc 的栈里实际推入了几页(不含根页;动画进行中它已经是最终值,测试等动画落定用)。</summary>
    public int StackDepth(MobileTab tab) => _stacks[tab].Depth - 1;

    /// <summary>某个标签当前显示的栈顶页面(根页或推入的页;测试用)。</summary>
    public Control? TopPage(MobileTab tab) => _stacks[tab].Top;

    private void Attach()
    {
        if (_vm is not null)
        {
            _vm.Navigation.StackChanged -= OnStackChanged;
            _vm.Navigation.PropertyChanged -= OnNavigationChanged;
        }

        _vm = DataContext as ShellViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.Navigation.StackChanged += OnStackChanged;
        _vm.Navigation.PropertyChanged += OnNavigationChanged;
        foreach (var tab in Enum.GetValues<MobileTab>())
        {
            _stacks[tab].Root = Touch(CreateRoot(tab));
            _shown[tab].Clear();
            Sync(tab);
        }

        ShowTab(_vm.Navigation.SelectedTab);
    }

    private void OnNavigationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MobileNavigationViewModel.SelectedTab) && _vm is not null)
        {
            ShowTab(_vm.Navigation.SelectedTab);
        }
    }

    private void ShowTab(MobileTab tab)
    {
        StackHost.Content = _stacks[tab];
        _syncing = true;
        Tabs.SelectedIndex = (int)tab;
        _syncing = false;
    }

    private void OnTabSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && _vm is not null && Tabs.SelectedIndex is >= 0 and var index)
        {
            _vm.Navigation.SelectTabCommand.Execute((MobileTab)index);
        }
    }

    private void OnStackChanged(MobileTab tab) => Sync(tab);

    /// <summary>ViewModel 的栈 → Arc 的栈:公共前缀不动,多出来的弹掉,缺的推入。</summary>
    private void Sync(MobileTab tab)
    {
        if (_vm is null)
        {
            return;
        }

        var want = _vm.Navigation.StackOf(tab);
        var shown = _shown[tab];
        var stack = _stacks[tab];
        var common = 0;
        while (common < shown.Count && common < want.Count && shown[common].Equals(want[common]))
        {
            common++;
        }

        _syncing = true;
        try
        {
            while (shown.Count > common)
            {
                shown.RemoveAt(shown.Count - 1);
                stack.Pop();
            }

            for (var i = common; i < want.Count; i++)
            {
                shown.Add(want[i]);
                var page = Touch(CreatePage(want[i]));
                page.ShowBack(stack.BackCommand);
                stack.Push(page);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Arc 的栈自己弹出了(左缘右滑 / 返回按钮):把深度回写给 ViewModel。</summary>
    private void OnStackNavigated(MobileTab tab)
    {
        if (_syncing || _vm is null)
        {
            return;
        }

        var depth = _stacks[tab].Depth - 1;
        var shown = _shown[tab];
        if (depth >= shown.Count)
        {
            return;
        }

        shown.RemoveRange(depth, shown.Count - depth);
        var navigation = _vm.Navigation;
        _syncing = true;
        try
        {
            // 只回写,不再触发 Sync 去重复弹栈
            while (navigation.StackOf(tab).Count > depth)
            {
                navigation.PopSilently(tab);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    // ───────────────────────── 页面工厂 ─────────────────────────

    /// <summary>
    /// 触控密度对每个页面显式设置:Arc 的栈用自己的容器托管页面,继承属性不一定沿着它传下去,所以不靠外壳根上的继承。
    /// </summary>
    private static NcwPage Touch(NcwPage page)
    {
        NcwDensity.SetIsTouch(page, true);
        return page;
    }

    private NcwPage CreateRoot(MobileTab tab)
    {
        var vm = _vm!;
        return tab switch
        {
            MobileTab.Inbox => new InboxPage { DataContext = vm.Inbox, RefreshAsync = () => vm.Sessions.RefreshAllAsync() },
            MobileTab.Sessions => new ProjectsPage { DataContext = vm.Rail, Navigation = vm.Navigation, RefreshAsync = () => vm.Sessions.RefreshAllAsync() },
            MobileTab.Devices => new DevicesPage { DataContext = vm.DevicesPage },
            _ => new SettingsHomePage { DataContext = vm, Navigation = vm.Navigation },
        };
    }

    private NcwPage CreatePage(MobileRoute route)
    {
        var vm = _vm!;
        return route switch
        {
            ProjectRoute => new ProjectSessionsPage { DataContext = vm.Panel, Navigation = vm.Navigation, RefreshAsync = () => vm.Sessions.RefreshAllAsync() },
            SessionRoute => new SessionPage { DataContext = vm.Conversation },
            SettingsRoute settings => SettingsSectionPage.Create(vm.Settings, settings.Section),
            NotificationsRoute => new NotificationsPage { DataContext = vm.Push },
            QrPairRoute => new QrPairPage { DataContext = vm.QrPairing, Preview = vm.Host.QrScanner as Ncw.Client.Services.IQrPreviewProvider },
            ManualAddRoute => new ManualAddPage { DataContext = vm.Settings.Devices },
            _ => new NcwPage(),
        };
    }
}
