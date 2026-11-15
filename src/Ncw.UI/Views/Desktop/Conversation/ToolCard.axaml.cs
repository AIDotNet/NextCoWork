using Avalonia.Controls;
using Avalonia.Interactivity;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>工具卡片。展开 / 折叠是纯 UI 状态,留在行视图模型上(数据更新不会丢)。</summary>
public partial class ToolCard : UserControl
{
    public ToolCard() => InitializeComponent();

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ToolRowViewModel { CanExpand: true } row)
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }
}
