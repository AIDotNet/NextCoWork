using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Provider;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Ncw.Client.Services;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// <see cref="IAttachmentPicker"/>(docs/design/11 §2):相册和文件走 Avalonia 的 <see cref="IStorageProvider"/>(Android 上是系统文档选择器 SAF,<c>ACTION_OPEN_DOCUMENT</c>,不需要存储权限),
/// 拍照走 <c>ACTION_IMAGE_CAPTURE</c> + <c>FileProvider</c> 的一次性授权 URI(应用 cache 下的临时文件,读完即删)。
/// 字节<b>按需</b>读:<see cref="PickedAttachment.ReadAllAsync"/> 才打开流,上传托盘先用 <see cref="PickedAttachment.Size"/> 判 32 MiB 上限。
/// <b>差异</b>:设计写的是系统 Photo Picker(<c>ACTION_PICK_IMAGES</c>),这里相册用同一个 SAF 选择器加图片过滤——少一条代码路径,选择体验略不如 Photo Picker。
/// </summary>
internal sealed class AndroidAttachmentPicker : IAttachmentPicker
{
    public bool IsAvailable(AttachmentSource source) => source switch
    {
        AttachmentSource.Camera => HasCaptureApp(),
        _ => AndroidHost.Activity is not null,
    };

    public async Task<IReadOnlyList<PickedAttachment>> PickAsync(AttachmentSource source, CancellationToken ct = default)
    {
        return source == AttachmentSource.Camera ? await CaptureAsync(ct).ConfigureAwait(true) : await PickDocumentsAsync(source, ct).ConfigureAwait(true);
    }

    private static async Task<IReadOnlyList<PickedAttachment>> PickDocumentsAsync(AttachmentSource source, CancellationToken ct)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not ISingleViewApplicationLifetime { MainView: { } view }
            || TopLevel.GetTopLevel(view)?.StorageProvider is not { CanOpen: true } storage)
        {
            return [];
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            FileTypeFilter = source == AttachmentSource.PhotoLibrary ? [FilePickerFileTypes.ImageAll] : null,
        }).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var result = new List<PickedAttachment>(files.Count);
        foreach (var file in files)
        {
            var properties = await file.GetBasicPropertiesAsync().ConfigureAwait(true);
            var size = properties.Size is { } known ? (long)known : QuerySize(file.Path) ?? 0;
            result.Add(new PickedAttachment(file.Name, MimeOf(file), size, async token =>
            {
                await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, token).ConfigureAwait(false);
                return memory.ToArray();
            }));
        }

        return result;
    }

    private static string MimeOf(IStorageFile file)
    {
        var mime = AndroidHost.Context.ContentResolver?.GetType(global::Android.Net.Uri.Parse(file.Path.OriginalString)!);
        return string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime;
    }

    private static long? QuerySize(Uri uri)
    {
        using var cursor = AndroidHost.Context.ContentResolver?.Query(global::Android.Net.Uri.Parse(uri.OriginalString)!, [IOpenableColumns.Size], null, null, null);
        if (cursor is not null && cursor.MoveToFirst() && cursor.GetColumnIndex(IOpenableColumns.Size) is >= 0 and var column && !cursor.IsNull(column))
        {
            return cursor.GetLong(column);
        }

        return null;
    }

    private static bool HasCaptureApp() =>
        AndroidHost.Activity is not null
        && new Intent(MediaStore.ActionImageCapture).ResolveActivity(AndroidHost.Context.PackageManager!) is not null;

    private static async Task<IReadOnlyList<PickedAttachment>> CaptureAsync(CancellationToken ct)
    {
        if (AndroidHost.Activity is not { } activity)
        {
            return [];
        }

        var directory = Path.Combine(AndroidHost.Context.CacheDir!.AbsolutePath, "capture");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"ncw-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, [], ct).ConfigureAwait(true);
        var uri = FileProvider.GetUriForFile(AndroidHost.Context, AndroidHost.Context.PackageName + ".captures", new Java.IO.File(path))!;
        var intent = new Intent(MediaStore.ActionImageCapture)
            .PutExtra(MediaStore.ExtraOutput, uri)
            .AddFlags(ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantReadUriPermission);

        var (result, _) = await ActivityResults.StartAsync(activity, intent, ActivityResults.CameraCapture).ConfigureAwait(true);
        var info = new FileInfo(path);
        if (result != Result.Ok || !info.Exists || info.Length == 0)
        {
            TryDelete(path);
            return [];
        }

        var name = $"photo-{DateTime.Now:yyyyMMdd-HHmmss}.jpg";
        // 临时文件不在读完时删:上传失败「重试」要能再读一次;过期的由 PurgeOldCaptures 在下次启动时清掉
        return [new PickedAttachment(name, "image/jpeg", info.Length, token => File.ReadAllBytesAsync(path, token))];
    }

    /// <summary>清掉超过一天的拍照临时文件(启动时调用)。</summary>
    public static void PurgeOldCaptures(string cacheDirectory)
    {
        var directory = Path.Combine(cacheDirectory, "capture");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-1))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
