using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop;

/// <summary>
/// 设置页(docs/design/07 §3,方案 D「三栏主从」):导航 → 列表栏 → 内容,一次只显示一个分区。
/// 「当前分区」的真源是 <see cref="SettingsViewModel.Section"/>(它一变,ViewModel 就去读那个分区的数据——每次打开设置只读一次);
/// 导航高亮、列表栏、内容区都从它派生,这里只剩纯 UI 的事:窄屏收起、切换分区时滚回顶部、搜索索引。
/// </summary>
public partial class SettingsOverlay : UserControl
{
    /// <summary>窄于它时导航收成图标栏(纯 UI 状态)。</summary>
    public const double CompactBelow = 760;

    /// <summary>窄于它时列表栏收窄。</summary>
    public const double NarrowListBelow = 1000;

    private SettingsViewModel? _vm;

    public SettingsOverlay()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
    }

    /// <summary>导航是否收成了图标栏(测试用)。</summary>
    public bool IsCompact { get; private set; }

    /// <summary>内容区正在显示的分区(测试用)。</summary>
    public SettingsSection? SelectedSection =>
        SectionPages.Children.FirstOrDefault(c => c.IsVisible && c.Tag is SettingsSection)?.Tag as SettingsSection?;

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
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.Section))
        {
            ContentScroll.Offset = default; // 换了分区:从顶上看起
        }
    }

    /// <summary>
    /// 搜索索引:每个分区里此刻的全部文字(标题、每一项的名字、说明;当前语言)。直接从界面树上收,不另维护一份「哪个设置在哪」的表——
    /// 页面改了文字,搜索自动跟着变。还没打开过的分区也有它的静态文字(名字和说明都是词条)。模型与供应商还收列表栏里的文字(供应商名)。
    /// </summary>
    private IReadOnlyList<(SettingsSection Section, string Title, IReadOnlyList<string> Texts)> BuildSearchIndex() =>
        [.. SectionPages.Children.Append(ProviderList)
            .Where(c => c.Tag is SettingsSection)
            .GroupBy(c => (SettingsSection)c.Tag!)
            .Select(g => (g.Key, Converters.SettingsSectionTitleConverter.Text(g.Key),
                (IReadOnlyList<string>)[.. g.SelectMany(c => Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(c).OfType<TextBlock>())
                    .Select(t => t.Text ?? "").Where(s => s.Length > 1).Distinct()]))];

    private void OnHitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SettingsHit hit })
        {
            _vm?.OpenHitCommand.Execute(hit);
        }
    }

    /// <summary>列表栏里的子分区。</summary>
    private void OnSubSectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SettingsSubItem item })
        {
            _vm?.ShowCommand.Execute(item.Section);
        }
    }

    private void ApplyWidth(double width)
    {
        var compact = width < CompactBelow;
        IsCompact = compact;
        Layout.Classes.Set("compact", compact);
        Layout.ColumnDefinitions[0].Width = new GridLength(compact ? 56 : 220);
        ListColumn.Width = width < NarrowListBelow ? 200 : 240;
        SectionPages.Margin = compact ? new Thickness(16, 8, 16, 48) : new Thickness(32, 8, 32, 48);
    }
}
