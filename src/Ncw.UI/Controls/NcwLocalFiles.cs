using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Ncw.Client.Services;

namespace Ncw.UI.Controls;

/// <summary><see cref="ILocalFiles"/> 的 Avalonia 实现:剪贴板、系统文件管理器、默认程序、「另存为」选择框。客户端本机的动作,不经设备。</summary>
public sealed class NcwLocalFiles(Visual owner) : ILocalFiles
{
    public async Task CopyTextAsync(string text)
    {
        if (TopLevel.GetTopLevel(owner)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public async Task<bool> RevealAsync(string absolutePath)
    {
        try
        {
            var isDirectory = Directory.Exists(absolutePath);
            if (!isDirectory && !File.Exists(absolutePath))
            {
                return false;
            }

            // macOS / Windows 能直接选中文件;其它系统打开所在的目录
            if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open") { ArgumentList = { "-R", absolutePath }, UseShellExecute = false })?.Dispose();
                return true;
            }

            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { $"/select,{absolutePath}" }, UseShellExecute = false })?.Dispose();
                return true;
            }

            if (TopLevel.GetTopLevel(owner) is { } top)
            {
                await top.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(isDirectory ? absolutePath : Path.GetDirectoryName(absolutePath)!));
                return true;
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // 系统文件管理器起不来:界面不提示(只是个便利动作)
        }

        return false;
    }

    public async Task<bool> OpenAsync(string absolutePath)
    {
        if (!File.Exists(absolutePath) || TopLevel.GetTopLevel(owner) is not { } top)
        {
            return false;
        }

        return await top.Launcher.LaunchFileInfoAsync(new FileInfo(absolutePath));
    }

    public async Task<bool> SaveAsync(string suggestedName, byte[] data)
    {
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { CanSave: true } storage)
        {
            return false;
        }

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = suggestedName, ShowOverwritePrompt = true });
        if (file is null)
        {
            return false;
        }

        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(data);
        return true;
    }
}
