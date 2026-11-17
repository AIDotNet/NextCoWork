using System.ComponentModel;
using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Desktop;

/// <summary>
/// 设置页。所有分区都在 <see cref="NcwSideNav"/> 的一个滚动区里;「当前分区」的真源是 <see cref="SettingsViewModel.Section"/>
/// (它一变,ViewModel 就去读那个分区的数据——每次打开设置只读一次,没滚到的分区不读)。这里只做两个方向的同步:
/// <list type="bullet">
/// <item>用户操作了导航(滚轮、点按、拖滚动条、键盘)之后,导航选中的分区 → 写回 <c>Section</c>;</item>
/// <item>其他时候(打开设置、别处改了 <c>Section</c>、分区内容加载完高度变了)→ 让导航选中并滚到 <c>Section</c>。</item>
/// </list>
/// Arc 的导航在滚动区每次滚动 / 排版时按位置重新选中分区;排版还没稳定时它会选到顶上那个分区,
/// 所以不是用户操作引起的选中一律不写回,而是在内容高度变了之后再把它拨回 <c>Section</c>。
/// </summary>
public partial class SettingsOverlay : UserControl
{
    /// <summary>窄于它时导航收成图标栏(纯 UI 状态)。</summary>
    public const double CompactBelow = 760;

    private SettingsViewModel? _vm;
    private bool _pushing;
    private bool _pulling;

    /// <summary>用户操作过导航(之后它选中的分区就是用户要看的分区)。程序选中分区时清掉。</summary>
    private bool _userDriven;

    /// <summary>这次换分区以后,排版过程中又把导航拨回 <c>Section</c> 的次数:有上限,滚不到(比如到底了)也不会来回拨。</summary>
    private int _retries;

    private const int MaxRetries = 4;

    public SettingsOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyCompact(e.NewSize.Width < CompactBelow);
        Nav.SelectionChanged += OnNavSelectionChanged;
        Nav.LayoutUpdated += (_, _) => Resync();
        Nav.AddHandler(PointerWheelChangedEvent, (_, _) => _userDriven = true, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Nav.AddHandler(PointerPressedEvent, (_, _) => _userDriven = true, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Nav.AddHandler(KeyDownEvent, (_, _) => _userDriven = true, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>导航是否收成了图标栏(测试用)。</summary>
    public bool IsCompact { get; private set; }

    /// <summary>导航当前选中的分区(测试用)。</summary>
    public SettingsSection? SelectedSection => (Nav.SelectedItem as NcwSideNavItem)?.Tag as SettingsSection?;

    /// <summary>分区所在的滚动区(Arc 模板里的 <c>PART_ContentViewport</c>)。</summary>
    private ScrollViewer? Viewport => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Nav).OfType<ScrollViewer>().FirstOrDefault(v => v.Name == "PART_ContentViewport");

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as SettingsViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.SearchIndex = BuildSearchIndex;
            Push(_vm.Section);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.Section) && !_pulling && _vm is { } vm)
        {
            Push(vm.Section);
        }
        else if (e.PropertyName is nameof(SettingsViewModel.IsOpen))
        {
            // 每次打开:从 Section 开始,直到用户自己滚
            _userDriven = false;
            _retries = 0;
        }
    }

    /// <summary>ViewModel → 导航:选中这个分区的导航项并滚到它。<paramref name="force"/>:已经选中也再滚一次(内容高度变了)。</summary>
    private void Push(SettingsSection section, bool force = false)
    {
        _userDriven = false;
        if (!force)
        {
            _retries = 0;
        }
        if (ItemFor(section) is not { } item || (!force && ReferenceEquals(Nav.SelectedItem, item)))
        {
            return;
        }

        _pushing = true;
        try
        {
            // SelectTab 才会滚到那一段;只改 SelectedItem 只是换了高亮
            Nav.SelectTab(Nav.Items.IndexOf(item));
        }
        finally
        {
            _pushing = false;
        }
    }

    /// <summary>
    /// 排版后:导航选中的和 Section 对不上,又不是用户操作引起的(内容刚排好、分区加载完高度变了,
    /// 控件按位置重新选中了别的分区),就再选一次、再滚一次。每次换分区最多拨 <see cref="MaxRetries"/> 次。
    /// </summary>
    private void Resync()
    {
        if (_userDriven || _pushing || _retries >= MaxRetries || _vm is not { IsOpen: true } vm || SelectedSection == vm.Section
            || Viewport is not { Extent.Height: > 0 })
        {
            return;
        }

        _retries++;
        var section = vm.Section;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_userDriven && _vm is { } now && now.Section == section && SelectedSection != section)
            {
                Push(section, force: true);
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>导航 → ViewModel:用户操作后,当前分区写回 Section(ViewModel 据此读这个分区的数据)。</summary>
    private void OnNavSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pushing || !_userDriven || _vm is not { } vm || SelectedSection is not { } section || vm.Section == section)
        {
            return;
        }

        _pulling = true;
        try
        {
            vm.Section = section;
        }
        finally
        {
            _pulling = false;
        }
    }

    /// <summary>
    /// 搜索索引:每个分区里此刻的全部文字(标题、每一项的名字、说明;当前语言)。直接从界面树上收,不另维护一份「哪个设置在哪」的表——
    /// 页面改了文字,搜索自动跟着变。还没读过数据的分区也有它的静态文字(名字和说明都是词条)。
    /// </summary>
    private IReadOnlyList<(SettingsSection Section, string Title, IReadOnlyList<string> Texts)> BuildSearchIndex() =>
        [.. Nav.Items.OfType<NcwSideNavItem>()
            .Where(i => i.Tag is SettingsSection)
            .Select(i => ((SettingsSection)i.Tag!, Converters.SettingsSectionTitleConverter.Text((SettingsSection)i.Tag!),
                (IReadOnlyList<string>)[.. Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(i).OfType<TextBlock>()
                    .Select(t => t.Text ?? "").Where(s => s.Length > 1).Distinct()]))];

    private void OnHitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SettingsHit hit })
        {
            _vm?.OpenHitCommand.Execute(hit);
        }
    }

    private NcwSideNavItem? ItemFor(SettingsSection section) =>
        Nav.Items.OfType<NcwSideNavItem>().FirstOrDefault(i => i.Tag is SettingsSection s && s == section);

    private void ApplyCompact(bool compact)
    {
        IsCompact = compact;
        Layout.Classes.Set("compact", compact);
        Nav.TabListWidth = compact ? 56 : 220;
        HeadRow.ColumnDefinitions[0].Width = new GridLength(compact ? 56 : 220);
        Nav.ContentPadding = compact ? new Avalonia.Thickness(16, 8, 16, 48) : new Avalonia.Thickness(32, 8, 32, 48);
    }
}

