using Avalonia.Platform.Storage;
using Ncw.Client.Services;
using Ncw.Core.Domain;

namespace Ncw.UI.Controls;

/// <summary>从 Avalonia <see cref="IStorageFile"/> / <see cref="IStorageItem"/> 解析本地路径或按流读取为图片附件的共用辅助。</summary>
internal static class StorageAttachments
{
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

    internal static async Task<PickedAttachment> CreateImageAttachmentAsync(IStorageFile file, string? localPath, string mime)
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
