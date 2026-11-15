using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop.Inspector;

/// <summary>
/// 终端页签的视图。终端控件只有一个:选中的标签变了,就把控件从旧标签的 <see cref="TerminalOutputRelay"/> 上摘下来、清屏、挂到新标签上
/// (新标签的订阅会先给整个缓冲);控件的键盘输入和行列数变化由标签转发给设备。code-behind 不放任何设备状态。
/// </summary>
public partial class TerminalPane : UserControl
{
    private TerminalPanelViewModel? _vm;
    private TerminalTabViewModel? _attached;

    public TerminalPane() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.Tabs.CollectionChanged -= OnTabsChanged;
        }

        Detach();
        _vm = DataContext as TerminalPanelViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
            _vm.Tabs.CollectionChanged += OnTabsChanged;
        }

        Sync();
    }

    private void OnTabsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Sync();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalPanelViewModel.Active))
        {
            Sync();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Detach();
        base.OnDetachedFromVisualTree(e);
    }

    private void Detach()
    {
        if (_attached is { } tab)
        {
            tab.PropertyChanged -= OnTabChanged;
            tab.DetachSurface();
            _attached = null;
        }
    }

    /// <summary>选中的标签 → 显示面挂到它上面;三种内容(终端 / 批准卡片 / 已不存在)按标签阶段切换。</summary>
    private void Sync()
    {
        var active = _vm?.Active;
        if (!ReferenceEquals(active, _attached))
        {
            Detach();
            if (active is not null && this.IsAttachedToVisualTree())
            {
                Surface.Reset();
                _attached = active;
                active.PropertyChanged += OnTabChanged;
                active.AttachSurface(Surface);
            }
        }

        ApplyPhase(active);
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalTabViewModel.Phase))
        {
            Dispatcher.UIThread.Post(() => ApplyPhase(_vm?.Active));
        }
    }

    private void ApplyPhase(TerminalTabViewModel? tab)
    {
        SurfaceFrame.IsVisible = tab is { ShowsSurface: true } && !Surface.IsUnavailable;
        ApprovalCard.IsVisible = tab is { IsAwaitingApproval: true };
        GoneNote.IsVisible = tab is { IsGone: true };
    }
}
