using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop;

/// <summary>命令面板的视图:打开时把焦点放进输入框;↑↓ / 回车 / Esc 在隧道阶段处理(先于输入框),高亮行滚进可见区。</summary>
public partial class CommandPalette : UserControl
{
    private CommandPaletteViewModel? _vm;

    public CommandPalette()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as CommandPaletteViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && _vm is { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() => QueryBox.Focus(), DispatcherPriority.Loaded);
        }
        else if (e.PropertyName == nameof(CommandPaletteViewModel.Active))
        {
            Dispatcher.UIThread.Post(BringActiveIntoView, DispatcherPriority.Loaded);
        }
    }

    private void BringActiveIntoView()
    {
        if (_vm?.Active is { } active && Results.ContainerFromItem(active) is Control container)
        {
            container.BringIntoView();
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is not { IsOpen: true } vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                vm.Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                vm.Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                vm.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnRowClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: PaletteItemViewModel item })
        {
            _vm?.Accept(item);
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.Close();
        e.Handled = true;
    }
}
