using Ncw.Agent.Environments;

namespace Ncw.Agent.Workspace.Files;

public sealed partial class FileService
{
    /// <summary>
    /// 读一个文件并分类:文本(UTF-8 原文,保留 BOM 和换行)、图片(≤ 内联上限带字节,更大的 <see cref="FileContent.Chunked"/>)、二进制(带原因)。
    /// 修订号是内容的 SHA-256;超限没读的是大小 + 修改时间。符号链接的最后一段拒,祖先可以是链(<see cref="LinkPolicy.FollowDirs"/>)。
    /// </summary>
    public ValueTask<FileContent> ReadAsync(FileProject project, string path, CancellationToken ct = default) =>
        Guard(async () =>
        {
            var target = await ProjectPaths.ResolveAsync(project, path, allowRoot: false, LinkPolicy.FollowDirs, ct).ConfigureAwait(false);
            return await ReadAtAsync(project, path, target, ct).ConfigureAwait(false);
        });

    private static async ValueTask<FileContent> ReadAtAsync(FileProject project, string path, EnvPath target, CancellationToken ct)
    {
        var fs = project.Environment.Fs;
        var info = await fs.LStatAsync(target, ct).ConfigureAwait(false);
        if (!info.IsFile)
        {
            throw new FilesException(info.IsSymlink ? FilesErrorCode.Symlink : FilesErrorCode.NotFile);
        }

        var extension = ExtensionOf(path);
        var size = info.Size;
        if (size > FileClassifier.ReadLimit(extension))
        {
            return FileClassifier.Classify(path, extension, null, size, info.ModifiedAt);
        }

        if (FileClassifier.ImageMimeOf(extension) is { } mime && size > FileLimits.InlineImageMax)
        {
            // 太大,放不进一帧:不读,告诉调用方用 readRange 分块取
            return new FileContent(FileKind.Image, path, size, FileClassifier.LargeRevision(size, info.ModifiedAt), Mime: mime, Chunked: true);
        }

        byte[] bytes;
        var stream = await fs.OpenReadAsync(target, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            bytes = await ReadAllAsync(stream, size, ct).ConfigureAwait(false);
        }

        return FileClassifier.Classify(path, extension, bytes, size, info.ModifiedAt);
    }

    /// <summary>
    /// 范围读取:一次读一段(上限 <see cref="FileLimits.RangeMax"/>),大文件分块、预览、视频 seek 用。修订号是大小 + 修改时间(第一块之后带回,对不上 = 分块读的中途文件变了 <see cref="FilesErrorCode.Conflict"/>)。
    /// </summary>
    public ValueTask<FileRange> ReadRangeAsync(FileProject project, string path, long offset, int length, string? revision, CancellationToken ct = default) =>
        Guard(async () =>
        {
            if (offset < 0 || length < 0)
            {
                throw new FilesException(FilesErrorCode.InvalidPath, "negative range");
            }

            var target = await ProjectPaths.ResolveAsync(project, path, allowRoot: false, LinkPolicy.FollowDirs, ct).ConfigureAwait(false);
            var fs = project.Environment.Fs;
            var info = await fs.LStatAsync(target, ct).ConfigureAwait(false);
            if (!info.IsFile)
            {
                throw new FilesException(info.IsSymlink ? FilesErrorCode.Symlink : FilesErrorCode.NotFile);
            }

            var current = FileClassifier.LargeRevision(info.Size, info.ModifiedAt);
            if (revision is not null && revision != current)
            {
                throw new FilesException(FilesErrorCode.Conflict, "the file changed between chunks");
            }

            var wanted = (int)Math.Min(Math.Min(length, FileLimits.RangeMax), Math.Max(0, info.Size - offset));
            var buffer = new byte[wanted];
            var read = 0;
            if (wanted > 0)
            {
                var stream = await fs.OpenReadAsync(target, ct).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    await SkipAsync(stream, offset, ct).ConfigureAwait(false);
                    while (read < wanted)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
                        if (n == 0)
                        {
                            break;
                        }

                        read += n;
                    }
                }
            }

            return new FileRange(read == buffer.Length ? buffer : buffer[..read], offset, read, info.Size, current, offset + read >= info.Size);
        });

    private static async ValueTask SkipAsync(Stream stream, long count, CancellationToken ct)
    {
        if (count <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            stream.Seek(count, SeekOrigin.Begin);
            return;
        }

        var scratch = new byte[81920];
        while (count > 0)
        {
            var n = await stream.ReadAsync(scratch.AsMemory(0, (int)Math.Min(scratch.Length, count)), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return;
            }

            count -= n;
        }
    }
}
