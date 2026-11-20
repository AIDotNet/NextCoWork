using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.UI.Views.Desktop.Conversation;

/// <summary>
/// 输入框剪贴板粘贴解析:
/// <list type="bullet">
/// <item>先看文件(<see cref="DataFormat.File"/>):受支持的图片文件进附件托盘,其余文件 / 文件夹把本地路径插入输入框(避免 Finder 附带的纯文件名或图标覆盖真实路径)。</item>
/// <item>再看位图(<see cref="DataFormat.Bitmap"/>):编码成 PNG 放进附件托盘。</item>
/// <item>最后兜底文本(<see cref="DataFormat.Text"/>):按普通文字插到光标位置。</item>
/// </list>
/// </summary>
internal static class ComposerClipboard
{
    public static async Task<bool> PasteAsync(IAsyncDataTransfer data, ComposerViewModel vm, Action<string> insertText, TimeProvider? clock = null)
    {
        if (data.Contains(DataFormat.File) && await data.TryGetFilesAsync() is { Length: > 0 } files)
        {
            var paths = new List<string>();
            var handled = false;
            foreach (var item in files)
            {
                var localPath = TryGetPath(item);
                if (item is IStorageFile storageFile && TryGetImageMime(item.Name) is { } mime)
                {
                    var picked = await CreateImageAttachmentAsync(storageFile, localPath, mime);
                    _ = vm.Attachments.AddAsync(picked);
                    handled = true;
                    continue;
                }

                if (!string.IsNullOrEmpty(localPath))
                {
                    paths.Add(localPath);
                    handled = true;
                }
            }

            if (paths.Count > 0)
            {
                insertText(string.Join("\n", paths));
            }

            if (handled)
            {
                return true;
            }
        }

        if (data.Contains(DataFormat.Bitmap) && await data.TryGetBitmapAsync() is { } bitmap)
        {
            byte[] bytes;
            using (bitmap)
            {
                using var memory = new MemoryStream();
                bitmap.Save(memory, new PngBitmapEncoderOptions());
                bytes = memory.ToArray();
            }

            if (bytes.Length > 0)
            {
                var now = (clock ?? TimeProvider.System).GetUtcNow().ToLocalTime();
                var name = $"image-{now:yyyyMMdd-HHmmss}.png";
                _ = vm.Attachments.AddAsync(new PickedAttachment(name, "image/png", bytes.Length, _ => Task.FromResult(bytes)));
                return true;
            }
        }

        if (data.Contains(DataFormat.Text) && await data.TryGetTextAsync() is { Length: > 0 } text)
        {
            insertText(text);
            return true;
        }

        return false;
    }

    internal static string? TryGetImageMime(string fileName) =>
        Attachments.NormalizeImageMime(Attachments.MimeOfExt(fileName));

    internal static string? TryGetPath(IStorageItem item)
    {
        var local = item.TryGetLocalPath();
        if (!string.IsNullOrEmpty(local))
        {
            return local;
        }

        if (item.Path is { IsAbsoluteUri: true, IsFile: true } uri)
        {
            return uri.LocalPath;
        }

        return item.Path?.OriginalString is { Length: > 0 } raw ? raw : null;
    }

    private static async Task<PickedAttachment> CreateImageAttachmentAsync(IStorageFile file, string? localPath, string mime)
    {
        if (!string.IsNullOrEmpty(localPath) && File.Exists(localPath))
        {
            var length = new FileInfo(localPath).Length;
            return new PickedAttachment(file.Name, mime, length, token => File.ReadAllBytesAsync(localPath, token));
        }

        long knownSize = 0;
        try
        {
            var props = await file.GetBasicPropertiesAsync();
            knownSize = props.Size is { } s ? (long)s : 0;
            if (knownSize > Attachments.MaxBytes)
            {
                return new PickedAttachment(file.Name, mime, knownSize, static _ => Task.FromResult(Array.Empty<byte>()));
            }

            await using var stream = await file.OpenReadAsync();
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                total += read;
                if (total > Attachments.MaxBytes)
                {
                    return new PickedAttachment(file.Name, mime, total, static _ => Task.FromResult(Array.Empty<byte>()));
                }

                memory.Write(buffer, 0, read);
            }

            var bytes = memory.ToArray();
            return new PickedAttachment(file.Name, mime, bytes.Length, _ => Task.FromResult(bytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new PickedAttachment(file.Name, mime, Math.Max(knownSize, 1), _ => Task.FromException<byte[]>(e));
        }
    }
}
