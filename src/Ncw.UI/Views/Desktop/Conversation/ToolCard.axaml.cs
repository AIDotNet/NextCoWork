using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>一次工具调用(一行文字,不是卡片)。展开 / 折叠是纯 UI 状态,留在行视图模型上(数据更新不会丢)。</summary>
public partial class ToolCard : UserControl
{
    public ToolCard() => InitializeComponent();

    private bool _openingImage;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (MediaFeedback is not null)
        {
            MediaFeedback.IsVisible = false;
        }
    }

    private async void OnViewImage(object? sender, RoutedEventArgs e)
    {
        if (_openingImage || DataContext is not ToolRowViewModel { CanViewMediaImage: true, Row.Media.Url: var url } row
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        _openingImage = true;
        MediaFeedback.IsVisible = false;
        try
        {
            var bytes = await row.LoadMediaImageAsync(url, CancellationToken.None);
            if (!ReferenceEquals(DataContext, row) || TopLevel.GetTopLevel(this) != top)
            {
                return;
            }

            using var bitmap = bytes is { } data ? await Task.Run(() => NcwImageReveal.Decode(data.Data, 2400)) : null;
            if (bitmap is null || bytes is not { } original)
            {
                ShowFeedback(row, NcwTextKind.MsgImageFailed, failed: true);
                return;
            }

            if (!ReferenceEquals(DataContext, row) || TopLevel.GetTopLevel(this) != top)
            {
                return;
            }

            var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false, Classes = { "hint" } };
            var copy = new NcwButton { Name = "CopyGeneratedImage", Content = NcwTexts.Get(NcwTextKind.MediaImageCopy), Variant = ButtonVariant.Secondary, IsEnabled = top.Clipboard is not null };
            copy.Click += async (_, _) =>
            {
                copy.IsEnabled = false;
                var ok = await CopyBitmapAsync(top, original.Data);
                SetFeedback(feedback, ok ? NcwTextKind.MediaImageCopied : NcwTextKind.MediaImageCopyFailed, failed: !ok);
                copy.IsEnabled = true;
            };
            var save = new NcwButton { Content = NcwTexts.Get(NcwTextKind.FilesSaveAs), Variant = ButtonVariant.Secondary, IsVisible = row.CanSaveMedia && top.StorageProvider.CanSave };
            save.Click += async (_, _) =>
            {
                save.IsEnabled = false;
                if (!await SaveMediaAsync(top.StorageProvider, row))
                {
                    SetFeedback(feedback, NcwTextKind.MediaSaveFailed, failed: true);
                }

                save.IsEnabled = true;
            };
            var close = new NcwButton { Content = NcwTexts.Get(NcwTextKind.Close), Variant = ButtonVariant.Primary };
            Dialog.SetResult(close, false);
            var dialog = new Dialog
            {
                Title = NcwTexts.Get(NcwTextKind.MediaImageGenerated),
                Size = ControlSize.Large,
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children = { new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = Math.Clamp(top.Bounds.Height - 210, 80, 1000) }, feedback },
                },
                Actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { copy, save, close } },
            };
            dialog.Resources[TKCode.Arc.DesignTokens.DialogSurfaceTokenKind.PanelWidthLG] = Math.Clamp(top.Bounds.Width - 48, 224, 960);
            using var airspace = NcwWidgetAirspace.Suspend();
            await dialog.ShowAsync(top);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowFeedback(row, NcwTextKind.MsgImageFailed, failed: true);
        }
        finally
        {
            _openingImage = false;
        }
    }

    private async void OnCopyImage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolRowViewModel { CanViewMediaImage: true, Row.Media.Url: var url } row
            || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        MediaCopyButton.IsEnabled = false;
        MediaFeedback.IsVisible = false;
        try
        {
            var bytes = await row.LoadMediaImageAsync(url, CancellationToken.None);
            var ok = bytes is { } image && ReferenceEquals(DataContext, row) && await CopyBitmapAsync(top, image.Data);
            ShowFeedback(row, ok ? NcwTextKind.MediaImageCopied : NcwTextKind.MediaImageCopyFailed, failed: !ok);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowFeedback(row, NcwTextKind.MediaImageCopyFailed, failed: true);
        }
        finally
        {
            MediaCopyButton.IsEnabled = true;
        }
    }

    internal static async Task<bool> CopyBitmapAsync(TopLevel top, byte[] bytes)
    {
        Avalonia.Media.Imaging.Bitmap? bitmap = null;
        try
        {
            if (top.Clipboard is not { } clipboard)
            {
                return false;
            }

            bitmap = await Task.Run(() => NcwImageReveal.Decode(bytes, int.MaxValue));
            if (bitmap is null)
            {
                return false;
            }

            // 部分平台延迟读取剪贴板。成功后让剪贴板持有独立的原尺寸位图，不和预览共享生命周期。
            await clipboard.SetBitmapAsync(bitmap);
            bitmap = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            bitmap?.Dispose();
        }
    }

    private async void OnSaveMedia(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ToolRowViewModel { CanSaveMedia: true } row
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
        {
            return;
        }

        MediaSaveButton.IsEnabled = false;
        MediaFeedback.IsVisible = false;
        try
        {
            if (!await SaveMediaAsync(storage, row))
            {
                ShowFeedback(row, NcwTextKind.MediaSaveFailed, failed: true);
            }
        }
        finally
        {
            MediaSaveButton.IsEnabled = true;
        }
    }

    private static async Task<bool> SaveMediaAsync(IStorageProvider storage, ToolRowViewModel row)
    {
        try
        {
            using var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = NcwTexts.Get(NcwTextKind.FilesSaveAs),
                SuggestedFileName = NcwTexts.Get(NcwTextKind.MediaSaveFileName) + row.MediaFileExtension,
                ShowOverwritePrompt = true,
            });
            if (file is null)
            {
                return true; // 用户取消不是保存失败。
            }

            // 完整读取已认证附件之后才写出，保留原始编码，不保存预览位图。
            await using var staged = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite,
                FileShare.None, bufferSize: 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            if (!await row.SaveMediaAsync(staged, CancellationToken.None))
            {
                return false;
            }

            staged.Position = 0;
            await using var output = await file.OpenWriteAsync();
            if (output.CanSeek)
            {
                output.SetLength(0);
            }

            await staged.CopyToAsync(output);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private void ShowFeedback(ToolRowViewModel row, NcwTextKind message, bool failed)
    {
        if (ReferenceEquals(DataContext, row))
        {
            SetFeedback(MediaFeedback, message, failed);
        }
    }

    private static void SetFeedback(TextBlock feedback, NcwTextKind message, bool failed)
    {
        feedback.Text = NcwTexts.Get(message);
        feedback.Classes.Set("failed", failed);
        feedback.IsVisible = true;
    }

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
