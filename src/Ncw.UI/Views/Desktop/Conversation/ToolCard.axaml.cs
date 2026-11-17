using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>一次工具调用(一行文字,不是卡片)。展开 / 折叠是纯 UI 状态,留在行视图模型上(数据更新不会丢)。</summary>
public partial class ToolCard : UserControl
{
    public ToolCard() => InitializeComponent();

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ToolRowViewModel { CanExpand: true } row)
        {
            // 用户在展开这一行:内容变高不能再把视图拽到底(跟随最新只在用户没动手时才有意义)。收起不用管:内容变矮,贴底照旧
            if (!row.IsExpanded)
            {
                this.FindAncestorOfType<ConversationView>()?.PauseFollowing();
            }

            row.IsExpanded = !row.IsExpanded;
        }
    }
}
