using Ncw.Agent.Environments;
using Ncw.Core.Ids;

namespace Ncw.Agent.Workspace.Files;

public sealed partial class FileService
{
    /// <summary>被「删除」的东西去哪:项目根下的这个目录(同一文件系统,所以改名一步到位,可恢复)。</summary>
    public const string TrashDirectory = ".next-cowork/trash";

    /// <summary>
    /// 新建 / 改名 / 移动 / 复制 / 删除。路径逐段拒符号链接(不改链背后的东西);目标已存在 <see cref="FilesErrorCode.Exists"/>。
    /// <list type="bullet">
    /// <item><b>删除不是永久删除</b>:目标被移进 <c>.next-cowork/trash/&lt;id&gt;/</c>,<see cref="FileMutation.RecoveryPath"/> 给出位置。2.x 用系统废纸篓,无头设备没有它,而「失败时降级为永久删除」是绝不允许的。</item>
    /// <item><paramref name="overwrite"/> <b>只对 rename 生效</b>(用户盯着一个具体文件、当面把名字改成另一个名字,撞名是当面发生的),且目标必须是文件:被盖掉的文件先进回收目录再改名。目标是目录一律拒绝(把一整棵子树丢进回收目录是另一回事,不能用同一句话换到同意)。</item>
    /// <item>复制先整棵预检(数量、总字节;符号链接和特殊文件拒绝,不给它们创建新的访问入口),再暂存到同目录的 <c>.ncw-copy-*.tmp</c>、最后改名发布;失败只清理本次创建的暂存目录,绝不删除目标路径。</item>
    /// <item>目录不能复制 / 移动到自己里面。</item>
    /// </list>
    /// </summary>
    public ValueTask<FileMutation> MutateAsync(FileProject project, FileOperation operation, string path, string? destination = null, bool overwrite = false, CancellationToken ct = default) =>
        Guard(async () =>
        {
            var target = await ProjectPaths.ResolveAsync(project, path, allowRoot: false, LinkPolicy.Reject, ct).ConfigureAwait(false);
            var fs = project.Environment.Fs;
            switch (operation)
            {
                case FileOperation.CreateFile:
                    await RequireParentAsync(fs, target, ct).ConfigureAwait(false);
                    var created = await fs.OpenWriteAsync(target, new FsWriteOptions(Exclusive: true), ct).ConfigureAwait(false);
                    await created.DisposeAsync().ConfigureAwait(false);
                    return new FileMutation(path);
                case FileOperation.CreateDirectory:
                    await RequireParentAsync(fs, target, ct).ConfigureAwait(false);
                    await fs.MkdirAsync(target, recursive: false, ct).ConfigureAwait(false);
                    return new FileMutation(path);
                case FileOperation.Delete:
                    {
                        _ = await fs.LStatAsync(target, ct).ConfigureAwait(false);
                        var recovery = await TrashAsync(project, target, ct).ConfigureAwait(false);
                        return new FileMutation(path, RecoveryPath: recovery);
                    }

                case FileOperation.Rename or FileOperation.Move or FileOperation.Copy:
                    {
                        if (destination is null)
                        {
                            throw new FilesException(FilesErrorCode.InvalidPath, "destination is required");
                        }

                        var to = await ProjectPaths.ResolveAsync(project, destination, allowRoot: false, LinkPolicy.Reject, ct).ConfigureAwait(false);
                        var info = await fs.LStatAsync(target, ct).ConfigureAwait(false);
                        if (!info.IsFile && !info.IsDirectory)
                        {
                            throw new FilesException(FilesErrorCode.Unsupported, "only files and directories");
                        }

                        if (info.IsDirectory && to.IsWithin(target))
                        {
                            throw new FilesException(FilesErrorCode.InvalidPath, "a directory cannot go into itself");
                        }

                        if (overwrite && operation == FileOperation.Rename && await StatOrNullAsync(fs, to, ct).ConfigureAwait(false) is { } existing)
                        {
                            if (!existing.IsFile)
                            {
                                throw new FilesException(FilesErrorCode.Exists, "the destination is not a file");
                            }

                            await TrashAsync(project, to, ct).ConfigureAwait(false);
                        }

                        await RequireAbsentAsync(fs, to, ct).ConfigureAwait(false);
                        await RequireParentAsync(fs, to, ct).ConfigureAwait(false);
                        if (operation == FileOperation.Copy)
                        {
                            await CopyAsync(project, target, to, destination, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            await fs.RenameAsync(target, to, replace: false, ct).ConfigureAwait(false);
                        }

                        return new FileMutation(path, destination);
                    }

                default:
                    throw new FilesException(FilesErrorCode.Unsupported, "unknown operation");
            }
        });

    private static async ValueTask<FsEntryInfo?> StatOrNullAsync(IEnvironmentFs fs, EnvPath path, CancellationToken ct)
    {
        try
        {
            return await fs.LStatAsync(path, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            return null;
        }
    }

    private static async ValueTask RequireAbsentAsync(IEnvironmentFs fs, EnvPath path, CancellationToken ct)
    {
        if (await StatOrNullAsync(fs, path, ct).ConfigureAwait(false) is not null)
        {
            throw new FilesException(FilesErrorCode.Exists, "the destination already exists");
        }
    }

    private static async ValueTask RequireParentAsync(IEnvironmentFs fs, EnvPath path, CancellationToken ct)
    {
        var parent = await StatOrNullAsync(fs, path.Parent, ct).ConfigureAwait(false);
        if (parent is null)
        {
            throw new FilesException(FilesErrorCode.NotFound, "the parent directory does not exist");
        }

        if (!parent.IsDirectory)
        {
            throw new FilesException(FilesErrorCode.InvalidPath, "the parent is not a directory");
        }
    }

    /// <summary>移进 <c>.next-cowork/trash/&lt;id&gt;/&lt;name&gt;</c>,返回项目相对的回收路径。</summary>
    private static async ValueTask<string> TrashAsync(FileProject project, EnvPath target, CancellationToken ct)
    {
        var fs = project.Environment.Fs;
        var id = Ulid.New();
        var relative = $"{TrashDirectory}/{id}";
        // 走一遍路径解析:`.next-cowork` 被换成指向根外的链时拒绝,不把用户的文件送到别处去
        var directory = await ProjectPaths.ResolveAsync(project, relative, allowRoot: false, LinkPolicy.Reject, ct).ConfigureAwait(false);
        if (directory.IsWithin(target) || target.IsWithin(directory))
        {
            throw new FilesException(FilesErrorCode.InvalidPath, "cannot trash the trash");
        }

        await fs.MkdirAsync(directory, recursive: true, ct).ConfigureAwait(false);
        var name = target.FileName;
        await fs.RenameAsync(target, directory.Join(name), replace: false, ct).ConfigureAwait(false);
        return $"{relative}/{name}";
    }

    private async ValueTask CopyAsync(FileProject project, EnvPath source, EnvPath destination, string destinationRelative, CancellationToken ct)
    {
        var fs = project.Environment.Fs;
        await InspectCopyAsync(fs, source, ct).ConfigureAwait(false);
        var temp = destination.Parent.Join($".ncw-copy-{Guid.NewGuid():N}.tmp");
        var created = false;
        try
        {
            created = true; // 先登记再写:登记一个还没建成的路径是无害的(清理侧容忍它不存在)
            await CopyTreeAsync(fs, source, temp, ct).ConfigureAwait(false);
            await RequireAbsentAsync(fs, destination, ct).ConfigureAwait(false);
            await fs.RenameAsync(temp, destination, replace: false, ct).ConfigureAwait(false);
            created = false;
        }
        finally
        {
            if (created)
            {
                await RemoveQuietlyAsync(fs, temp).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask RemoveQuietlyAsync(IEnvironmentFs fs, EnvPath path)
    {
        try
        {
            var info = await StatOrNullAsync(fs, path, CancellationToken.None).ConfigureAwait(false);
            if (info is null)
            {
                return;
            }

            if (info.IsDirectory)
            {
                await fs.DeleteDirectoryAsync(path, recursive: true, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await fs.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (EnvironmentException)
        {
            // 清理失败不能盖住真正的错误
        }
    }

    /// <summary>预先验证整个复制树:数量和总字节有上限;符号链接 / 设备 / 管道一律拒(复制不给它们创建新的访问入口)。</summary>
    private async ValueTask InspectCopyAsync(IEnvironmentFs fs, EnvPath root, CancellationToken ct)
    {
        var count = 0;
        long bytes = 0;
        var pending = new Stack<EnvPath>();
        pending.Push(root);
        while (pending.TryPop(out var path))
        {
            var info = await fs.LStatAsync(path, ct).ConfigureAwait(false);
            if (++count > _options.CopyMaxEntries)
            {
                throw new FilesException(FilesErrorCode.TooLarge, "too many entries to copy");
            }

            if (info.IsFile)
            {
                bytes += info.Size;
                if (bytes > _options.CopyMaxBytes)
                {
                    throw new FilesException(FilesErrorCode.TooLarge, "too many bytes to copy");
                }
            }
            else if (info.IsDirectory)
            {
                foreach (var child in await fs.ListAsync(path, ct).ConfigureAwait(false))
                {
                    pending.Push(path.Join(child.Name));
                }
            }
            else
            {
                throw new FilesException(FilesErrorCode.Unsupported, "links and special files cannot be copied");
            }
        }
    }

    private async ValueTask CopyTreeAsync(IEnvironmentFs fs, EnvPath source, EnvPath destination, CancellationToken ct)
    {
        var info = await fs.LStatAsync(source, ct).ConfigureAwait(false);
        if (info.IsFile)
        {
            _ = await fs.CopyFileAsync(source, destination, _options.CopyMaxBytes, ct).ConfigureAwait(false);
        }
        else if (info.IsDirectory)
        {
            await fs.MkdirAsync(destination, recursive: false, ct).ConfigureAwait(false);
            foreach (var child in await fs.ListAsync(source, ct).ConfigureAwait(false))
            {
                await CopyTreeAsync(fs, source.Join(child.Name), destination.Join(child.Name), ct).ConfigureAwait(false);
            }
        }
        else
        {
            throw new FilesException(FilesErrorCode.Unsupported, "links and special files cannot be copied");
        }
    }
}
