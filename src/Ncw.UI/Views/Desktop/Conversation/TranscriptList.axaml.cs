using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>
/// 虚拟化转录列表(<see cref="ConversationView"/> 与 <see cref="Inspector.SubagentsPane"/> 共用)。
/// 暴露内部 <see cref="ItemsControl"/>(<see cref="Inner"/>)供宿主接入滚动跟踪、动画复位与工作区缩进。
/// </summary>
public partial class TranscriptList : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<TranscriptList, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<TranscriptList, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<Thickness> ContentMarginProperty =
        AvaloniaProperty.Register<TranscriptList, Thickness>(nameof(ContentMargin), new Thickness(16, 12));

    public TranscriptList()
    {
        InitializeComponent();
        Transcript.ContainerPrepared += OnContainerPrepared;
        Transcript.ContainerClearing += (_, e) => NcwMotion.Rest(e.Container);
    }

    public ItemsControl Inner => Transcript;

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public Thickness ContentMargin
    {
        get => GetValue(ContentMarginProperty);
        set => SetValue(ContentMarginProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsReadOnlyProperty)
        {
            Classes.Set("readonly", IsReadOnly);
        }
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        // 展开着的「工作区」成员缩进一格(容器会被回收复用,每次都要重写)
        e.Container.Margin = e.Container.DataContext is RowViewModel { InWorkGroup: true } ? new Thickness(18, 0, 0, 0) : default;
    }

    /// <summary>缩略图被实例化并挂上视觉树(虚拟化列表只给视野附近的行实例化):这时才向设备取字节,滚出去没挂上的不取。</summary>
    private void OnThumbAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if ((sender as Control)?.DataContext is ImageThumbViewModel thumb)
        {
            _ = thumb.EnsureLoadedAsync();
        }
    }

    private void OnOpenImage(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ImageThumbViewModel thumb)
        {
            this.FindAncestorOfType<ConversationView>()?.OpenImage(thumb);
        }
    }

    /// <summary>复制一条消息的文字(<c>Tag</c> 绑的是消息正文):本机剪贴板,纯 UI 动作,不经设备。</summary>
    private async void OnCopyText(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string text && text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 剪贴板被别的程序占着:复制失败不值得打断对话
            }
        }
    }

    private void OnToggleThinking(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ThinkingRowViewModel row)
        {
            if (!row.IsExpanded)
            {
                this.FindAncestorOfType<ConversationView>()?.PauseFollowing();
            }

            row.IsExpanded = !row.IsExpanded;
        }
    }

    private void OnToggleWorkGroup(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is WorkGroupRowViewModel row)
        {
            if (!row.IsExpanded)
            {
                // 展开会让内容变高,和展开思考块 / 工具行一样先暂停跟随最新
                this.FindAncestorOfType<ConversationView>()?.PauseFollowing();
            }

            row.IsExpanded = !row.IsExpanded;
        }
    }
}
