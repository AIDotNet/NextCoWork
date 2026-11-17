using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;

namespace Ncw.UI.Views.Desktop;

/// <summary>「快捷键」页:点组合开始录制;录制期间在窗口上(隧道阶段)截住下一次按键交给 ViewModel,全局快捷键暂停。</summary>
public partial class ShortcutsSettingsPage : UserControl
{
    private ShortcutsSettingsViewModel? _vm;
    private TopLevel? _top;

    public ShortcutsSettingsPage() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
        }

        _vm = DataContext as ShortcutsSettingsViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnViewModelChanged;
        }

        UpdateDisplaced();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _top = null;
        _vm?.Cancel();
        NcwShortcutRouter.IsRecording = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShortcutsSettingsViewModel.Recording))
        {
            NcwShortcutRouter.IsRecording = _vm?.Recording is not null;
        }
        else if (e.PropertyName == nameof(ShortcutsSettingsViewModel.Displaced))
        {
            UpdateDisplaced();
        }
    }

    private void UpdateDisplaced() =>
        DisplacedText.Text = _vm?.Displaced is { } command ? NcwTexts.Format(NcwTextKind.ShortcutConflict, ShortcutText.Title(command)) : "";

    /// <summary>录制中:Esc 取消;只按了修饰键继续等;否则交给 ViewModel(不能用的组合留在录制状态)。按键一律吞掉,别让它触发别的东西(比如关闭设置)。</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm?.Recording is null)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            _vm.Cancel();
            return;
        }

        if (ShortcutText.Capture(e) is { } gesture)
        {
            _vm.Record(gesture);
        }
    }

    private void OnRecordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ShortcutRowViewModel row })
        {
            _vm?.StartRecordingCommand.Execute(row);
        }
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ShortcutRowViewModel row })
        {
            _vm?.ClearCommand.Execute(row);
        }
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ShortcutRowViewModel row })
        {
            _vm?.ResetCommand.Execute(row);
        }
    }
}
