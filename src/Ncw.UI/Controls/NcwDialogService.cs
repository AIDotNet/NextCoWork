using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;
using Ncw.UI.Views.Dialogs;
using TKCode.Arc;

namespace Ncw.UI.Controls;

/// <summary><see cref="IDialogService"/> 的 Arc 实现:对话框盖在 <paramref name="owner"/> 所在的窗口上,Esc 或点遮罩取消。</summary>
public sealed class NcwDialogService(Visual owner) : IDialogService
{
    public async Task<string?> PromptTextAsync(string title, string description, string label, string? initial)
    {
        var input = new Input { Label = label, Text = initial ?? string.Empty };
        var dialog = new Dialog
        {
            Title = title,
            Description = description,
            Content = new StackPanel { Spacing = 16, Margin = new Thickness(0, 4, 0, 0), Children = { input } },
            Actions = Dialog.BuildActions(
            [
                new DialogAction(NcwTexts.Get(NcwTextKind.Cancel), false),
                new DialogAction(NcwTexts.Get(NcwTextKind.Save), true, ButtonVariant.Primary),
            ]),
        };

        var result = await dialog.ShowAsync(owner);
        return result is true ? input.Text : null;
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanPickFolder: true } storage)
        {
            return null;
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title.Length > 0 ? title : NcwTexts.Get(NcwTextKind.FilePickBackupFolder), AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<PickedFile?> PickFileAsync(FilePickKind kind)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var (title, filter) = kind switch
        {
            FilePickKind.SkillZip => (NcwTextKind.FilePickSkillZip, new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }),
            FilePickKind.PluginZip => (NcwTextKind.PluginFilePickTitle, new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }),
            FilePickKind.ImportData => (NcwTextKind.FilePickImport, new FilePickerFileType("JSON") { Patterns = ["*.json"] }),
            _ => (NcwTextKind.FilePickBackup, null),
        };
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = NcwTexts.Get(title),
            AllowMultiple = false,
            FileTypeFilter = filter is null ? null : [filter],
        });
        if (files.Count == 0)
        {
            return null;
        }

        var file = files[0];
        var properties = await file.GetBasicPropertiesAsync();
        return new PickedFile(file.Name, file.TryGetLocalPath(), (long)(properties.Size ?? 0), async () => await file.OpenReadAsync());
    }

    public async Task<Stream?> PickSaveStreamAsync(FilePickKind kind, string suggestedName)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanSave: true } storage)
        {
            return null;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = NcwTexts.Get(NcwTextKind.FilePickExport),
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        return file is null ? null : await file.OpenWriteAsync();
    }

    /// <summary>「新建项目」:Arc Dialog 包住 <see cref="NewProjectDialog"/>;「添加」走 ViewModel 的命令,成功后才关,失败时原因留在窗口里。</summary>
    public async Task<bool> ShowNewProjectAsync(NewProjectViewModel form)
    {
        var content = new NewProjectDialog { DataContext = form };
        var submit = new NcwButton { Variant = ButtonVariant.Primary, Content = NcwTexts.Get(NcwTextKind.NewProjectCreate), Command = form.SubmitCommand };
        var dialog = new Dialog
        {
            Title = NcwTexts.Get(NcwTextKind.NewProjectTitle),
            Description = NcwTexts.Get(NcwTextKind.NewProjectDescription),
            Content = content,
            Actions = ActionRow(CancelButton(), submit),
        };
        void Close() => dialog.Close(true);
        form.Completed += Close;
        dialog.Opened += (_, _) => Dispatcher.UIThread.Post(content.FocusPath, DispatcherPriority.Input);
        try
        {
            return await dialog.ShowAsync(owner) is true && form.Created is not null;
        }
        finally
        {
            form.Completed -= Close;
        }
    }

    /// <summary>「项目图标」:左边「恢复默认」,右边「取消 / 保存」;保存和恢复默认都由 ViewModel 落地后关窗口。</summary>
    public async Task<bool> ShowProjectIconAsync(ProjectIconViewModel form)
    {
        var reset = new NcwButton { Variant = ButtonVariant.Ghost, Content = NcwTexts.Get(NcwTextKind.ProjectIconReset), Command = form.ResetCommand };
        var save = new NcwButton { Variant = ButtonVariant.Primary, Content = NcwTexts.Get(NcwTextKind.Save), Command = form.SaveCommand };
        var actions = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(reset, Dock.Left);
        var right = ActionRow(CancelButton(), save);
        DockPanel.SetDock(right, Dock.Right);
        actions.Children.Add(reset);
        actions.Children.Add(right);
        var dialog = new Dialog
        {
            Title = NcwTexts.Get(NcwTextKind.ProjectIconTitle),
            Description = NcwTexts.Format(NcwTextKind.ProjectIconDescription, form.ProjectName),
            Content = new ProjectIconDialog { DataContext = form },
            Actions = actions,
        };
        var completed = false;
        void Close()
        {
            completed = true;
            dialog.Close(true);
        }

        form.Completed += Close;
        try
        {
            await dialog.ShowAsync(owner);
            return completed;
        }
        finally
        {
            form.Completed -= Close;
        }
    }

    /// <summary>选一张图片并压缩成项目图标(<see cref="NcwIconImages"/>);取消返回 null。</summary>
    public async Task<PickedIcon?> PickIconImageAsync()
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = NcwTexts.Get(NcwTextKind.FilePickIconImage),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(NcwTexts.Get(NcwTextKind.FilePickIconImageType))
            {
                Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"],
                MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/gif", "image/bmp"],
                AppleUniformTypeIdentifiers = ["public.png", "public.jpeg", "org.webmproject.webp", "com.compuserve.gif", "com.microsoft.bmp"],
            }],
        });
        if (files.Count == 0)
        {
            return null;
        }

        var file = files[0];
        var properties = await file.GetBasicPropertiesAsync();
        return await NcwIconImages.CompressAsync(async () => await file.OpenReadAsync(), (long)(properties.Size ?? 0));
    }

    private static NcwButton CancelButton()
    {
        var cancel = new NcwButton { Variant = ButtonVariant.Secondary, Content = NcwTexts.Get(NcwTextKind.Cancel) };
        Dialog.SetResult(cancel, false);
        return cancel;
    }

    private static StackPanel ActionRow(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        foreach (var button in buttons)
        {
            row.Children.Add(button);
        }

        return row;
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        Dialog.ConfirmAsync(owner, title, message, confirmLabel, NcwTexts.Get(NcwTextKind.Cancel), true);

    public async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new Dialog
        {
            Title = title,
            Description = message,
            Actions = Dialog.BuildActions([new DialogAction(NcwTexts.Get(NcwTextKind.Ok), true, ButtonVariant.Primary)]),
        };
        await dialog.ShowAsync(owner);
    }
}

/// <summary><see cref="IUrlLauncher"/> 的 Avalonia 实现:用系统默认浏览器打开(OAuth 授权页)。只放行 http / https。</summary>
public sealed class NcwUrlLauncher(Visual owner) : IUrlLauncher
{
    public async Task OpenAsync(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && TopLevel.GetTopLevel(owner) is { } top)
        {
            await top.Launcher.LaunchUriAsync(uri);
        }
    }
}
