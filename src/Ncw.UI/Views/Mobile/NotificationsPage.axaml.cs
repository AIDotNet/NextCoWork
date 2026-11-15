using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Mobile;

/// <summary>通知设置页:打开时让 <see cref="PushViewModel"/> 读一次权限 / 令牌状态。</summary>
public partial class NotificationsPage : NcwPage
{
    public NotificationsPage() => InitializeComponent();

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is PushViewModel push)
        {
            _ = push.RefreshAsync();
        }
    }
}
