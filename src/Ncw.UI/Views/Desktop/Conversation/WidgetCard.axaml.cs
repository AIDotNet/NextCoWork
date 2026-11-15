using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>内联可视化卡片。重新加载 / 复制源码是纯 UI 动作(不改任何事实);无障碍名字带上卡片标题。</summary>
public partial class WidgetCard : UserControl
{
    public WidgetCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is WidgetRowViewModel vm)
            {
                AutomationProperties.SetName(this, NcwTexts.Format(NcwTextKind.WidgetAutomationName, vm.Title));
            }
        };
    }

    private void OnReload(object? sender, RoutedEventArgs e) => Host.Reload();

    private async void OnCopySource(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WidgetRowViewModel { Card: { } card } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(card.Code);
        }
    }
}
