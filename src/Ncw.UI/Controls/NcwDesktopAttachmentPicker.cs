using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Ncw.Client.Services;

namespace Ncw.UI.Controls;

/// <summary>桌面端的附件选择器:经系统的 <see cref="IStorageProvider"/> 选图片(支持多选);非图片文件走拖拽 / 路径引用。</summary>
public sealed class NcwDesktopAttachmentPicker(Func<TopLevel?> topLevel) : IAttachmentPicker
{
    public bool IsAvailable(AttachmentSource source) => source == AttachmentSource.PhotoLibrary;

    public async Task<IReadOnlyList<PickedAttachment>> PickAsync(AttachmentSource source, CancellationToken ct = default)
    {
        if (source != AttachmentSource.PhotoLibrary || topLevel()?.StorageProvider is not { CanOpen: true } storage)
        {
            return [];
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count == 0)
        {
            return [];
        }

        var picked = new List<PickedAttachment>(files.Count);
        foreach (var file in files)
        {
            if (StorageAttachments.TryGetImageMime(file.Name) is not { } mime)
            {
                continue;
            }

            picked.Add(await StorageAttachments.CreateImageAttachmentAsync(file, StorageAttachments.TryGetPath(file), mime));
        }

        return picked;
    }
}
