using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Ncw.Client.Services;
using Ncw.UI.Localization;
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
