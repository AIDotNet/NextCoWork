using Ncw.Agent.Environments;

namespace Ncw.Agent.Workspace.Files;

public sealed partial class FileService
{
    /// <summary>
    /// 覆写一个已存在的文件。<paramref name="text"/>(UTF-8 文本)和 <paramref name="data"/>(字节,只允许覆写已经是图片的文件)二选一。
    /// <list type="bullet">
    /// <item><b>乐观锁</b>:<paramref name="revision"/> 必须等于当前内容的修订号,对不上 <see cref="FilesErrorCode.Conflict"/>(防止盖掉外部的修改,即使长度没变)。</item>
    /// <item><b>原子</b>:同目录临时文件 → 提交前再核对一次修订号 → 改名替换。保存中断不会留下被截断的用户文件;硬链接被「替换」而不是原地改写,外部目标不受影响。</item>
    /// <item>内容没变就不写(「只是看了看」也把 mtime 顶起来是错的);权限位尽量保留(经创建模式,受 umask 约束)。</item>
    /// <item>文本必须能无损编回 UTF-8、不含控制字符,超过 <see cref="FileLimits.TextMax"/> 拒绝,都在动文件之前检查。</item>
    /// </list>
    /// </summary>
    public ValueTask<FileWriteResult> WriteAsync(FileProject project, string path, string revision, string? text, byte[]? data, CancellationToken ct = default) =>
        Guard(async () =>
        {
            if ((text is null) == (data is null) || revision is null)
            {
                throw new FilesException(FilesErrorCode.InvalidEncoding, "exactly one of text or data is required");
            }

            var target = await ProjectPaths.ResolveAsync(project, path, allowRoot: false, LinkPolicy.FollowDirs, ct).ConfigureAwait(false);
            byte[] bytes;
            if (text is not null)
            {
                if (text.Length > FileLimits.TextMax)
                {
                    throw new FilesException(FilesErrorCode.TooLarge, "text is over the limit");
                }

                bytes = FileClassifier.EncodeText(text) is { } encoded && !FileClassifier.IsBinaryText(text)
                    ? encoded
                    : throw new FilesException(FilesErrorCode.InvalidEncoding, "text is not valid UTF-8 text");
                if (bytes.Length > FileLimits.TextMax)
                {
                    throw new FilesException(FilesErrorCode.TooLarge, "text is over the limit");
                }
            }
            else
            {
                if (data!.Length > FileLimits.ImageMax)
                {
                    throw new FilesException(FilesErrorCode.TooLarge, "data is over the limit");
                }

                bytes = data;
            }

            var current = await ReadAtAsync(project, path, target, ct).ConfigureAwait(false);
            if (text is not null)
            {
                if (current.Kind != FileKind.Text)
                {
                    throw new FilesException(FilesErrorCode.Unsupported, "not a text file");
                }
            }
            else
            {
                if (current is { Kind: FileKind.Binary, Reason: BinaryReason.TooLarge })
                {
                    throw new FilesException(FilesErrorCode.TooLarge, "the existing file is over the limit");
                }

                if (current.Kind != FileKind.Image)
                {
                    throw new FilesException(FilesErrorCode.Unsupported, "binary writes only overwrite images");
                }
            }

            if (current.Revision != revision)
            {
                throw new FilesException(FilesErrorCode.Conflict, "stale revision");
            }

            if ((text is not null && current.Text == text) || (data is not null && current.Revision == FileClassifier.Hash(bytes)))
            {
                return new FileWriteResult(path, current.Size, current.Revision);
            }

            await ReplaceAtomicallyAsync(project, path, target, bytes, revision, ct).ConfigureAwait(false);
            var written = await project.Environment.Fs.LStatAsync(target, ct).ConfigureAwait(false);
            var newRevision = bytes.Length <= (text is not null ? FileLimits.TextMax : FileLimits.InlineImageMax)
                ? FileClassifier.Hash(bytes)
                : FileClassifier.LargeRevision(written.Size, written.ModifiedAt);
            return new FileWriteResult(path, bytes.Length, newRevision);
        });

    private static async ValueTask ReplaceAtomicallyAsync(FileProject project, string path, EnvPath target, byte[] bytes, string revision, CancellationToken ct)
    {
        var fs = project.Environment.Fs;
        var info = await fs.LStatAsync(target, ct).ConfigureAwait(false);
        var temp = target.Parent.Join($".ncw-save-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            var stream = await fs.OpenWriteAsync(temp, new FsWriteOptions(Exclusive: true, info.UnixMode == 0 ? null : info.UnixMode), ct).ConfigureAwait(false);
            created = true;
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            // 提交前再核对一次:路径没被换成链,内容也还是读到的那一版(临时文件写的这段时间里别人可能改了它)
            var again = await ProjectPaths.ResolveAsync(project, path, allowRoot: false, LinkPolicy.FollowDirs, ct).ConfigureAwait(false);
            var latest = await ReadAtAsync(project, path, again, ct).ConfigureAwait(false);
            if (latest.Revision != revision)
            {
                throw new FilesException(FilesErrorCode.Conflict, "the file changed while saving");
            }

            await fs.RenameAsync(temp, target, replace: true, ct).ConfigureAwait(false);
            created = false;
        }
        finally
        {
            if (created)
            {
                try
                {
                    await fs.DeleteFileAsync(temp, CancellationToken.None).ConfigureAwait(false);
                }
                catch (EnvironmentException)
                {
                    // 清理失败不能盖住真正的错误
                }
            }
        }
    }
}
