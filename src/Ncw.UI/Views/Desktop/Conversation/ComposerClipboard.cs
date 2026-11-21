using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

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
    public static Task<bool> DropAsync(IDataTransfer data, ComposerViewModel vm, Action<string> insertText, TimeProvider? clock = null) =>
        PasteCoreAsync(
            data.Contains(DataFormat.File),
            () => Task.FromResult(data.TryGetFiles()),
            data.Contains(DataFormat.Bitmap),
            () => Task.FromResult(data.TryGetBitmap()),
            data.Contains(DataFormat.Text),
            () => Task.FromResult(data.TryGetText()),
            vm,
            insertText,
            clock);

    public static Task<bool> PasteAsync(IAsyncDataTransfer data, ComposerViewModel vm, Action<string> insertText, TimeProvider? clock = null) =>
        PasteCoreAsync(
            data.Contains(DataFormat.File),
            data.TryGetFilesAsync,
            data.Contains(DataFormat.Bitmap),
            data.TryGetBitmapAsync,
            data.Contains(DataFormat.Text),
            data.TryGetTextAsync,
            vm,
            insertText,
            clock);

    private static async Task<bool> PasteCoreAsync(
        bool hasFile,
        Func<Task<IStorageItem[]?>> getFiles,
        bool hasBitmap,
        Func<Task<Bitmap?>> getBitmap,
        bool hasText,
        Func<Task<string?>> getText,
        ComposerViewModel vm,
        Action<string> insertText,
        TimeProvider? clock)
    {
        if (hasFile && await getFiles() is { Length: > 0 } files)
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

        if (hasBitmap && await getBitmap() is { } bitmap)
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

        if (hasText && await getText() is { Length: > 0 } text)
        {
            insertText(text);
            return true;
        }

        return false;
    }

    internal static string? TryGetImageMime(string fileName) => StorageAttachments.TryGetImageMime(fileName);

    internal static string? TryGetPath(IStorageItem item) => StorageAttachments.TryGetPath(item);

    private static Task<PickedAttachment> CreateImageAttachmentAsync(IStorageFile file, string? localPath, string mime) =>
        StorageAttachments.CreateImageAttachmentAsync(file, localPath, mime);
}
