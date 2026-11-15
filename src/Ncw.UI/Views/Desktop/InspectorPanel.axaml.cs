using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using TKCode.Arc;

namespace Ncw.UI.Views.Desktop;

public partial class InspectorPanel : UserControl
{
    private InspectorViewModel? _vm;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    public InspectorPanel()
    {
        InitializeComponent();
        // 运行中的子代理要计时:只在需要时开定时器,定时器只调 Tick(重新派生一遍,不持有任何状态)
        _tick.Tick += (_, _) => _vm?.Tick();
    }

    /// <summary>
    /// 页签行右端让出右上角常驻按钮的宽度。Windows / Linux 上右上角还有窗口按钮,页签行更窄,
    /// 选中页签的名称照常显示(页签本来就只有选中的那个带名称)。
    /// </summary>
    public void SetTopRowTrailing(double trailing, bool compact) =>
        HeaderRow.Margin = new Thickness(8, 0, 8 + trailing, 0);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Addable.CollectionChanged -= OnAddableChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as InspectorViewModel;
        if (_vm is not null)
        {
            _vm.Addable.CollectionChanged += OnAddableChanged;
            _vm.PropertyChanged += OnViewModelChanged;
            // 弹窗、本机文件动作、弹窗文字:盖在这个面板所在的窗口上,按当前语言
            _vm.Dialogs = new NcwDialogService(this);
            _vm.LocalFiles = new NcwLocalFiles(this);
            _vm.Text = Localization.NcwInspectorText.Instance;
        }

        RebuildAddMenu();
        UpdateTimer();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(InspectorViewModel.NeedsTick))
        {
            UpdateTimer();
        }
    }

    private void UpdateTimer()
    {
        if (_vm is { NeedsTick: true })
        {
            _tick.Start();
        }
        else
        {
            _tick.Stop();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _tick.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAddableChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildAddMenu();

    /// <summary>「+」菜单的条目来自 ViewModel 的 Addable;点选即调用同一个 AddCommand。</summary>
    private void RebuildAddMenu()
    {
        if (_vm is null)
        {
            AddMenu.ItemsSource = null;
            return;
        }

        AddMenu.ItemsSource = _vm.Addable
            .Select(kind => new MenuEntry
            {
                Header = Converters.InspectorTabTitleConverter.Title(kind),
                Command = _vm.AddCommand,
                CommandParameter = kind,
            })
            .ToList();
    }
}
