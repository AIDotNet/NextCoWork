using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Ncw.Agent.Environments.Ssh;

/// <summary>
/// 远程文件系统(SFTP)。移植自 2.x 的 <c>ssh/sftp.ts</c>:同样的错误码映射、Windows 主机的 <c>/C:/…</c> 路径写法。
/// <list type="bullet">
/// <item>每次操作取当前连接代的 SFTP 连接;连接正在重连时最多等 <see cref="SshEnvironmentOptions.ReadyWait"/>;</item>
/// <item>写类操作遇到连接中断报 <see cref="EnvironmentErrorCode.ResultUnknown"/>(请求可能已经到达服务器),读类操作报 Disconnected;</item>
/// <item>SSH.NET 的 <c>Get</c> 会先对路径做 realpath 再 lstat,所以对符号链接得到的是目标的属性;<see cref="LStatAsync"/> 靠「规范路径是否等于规范父目录加名字」识别链接。</item>
/// </list>
/// </summary>
internal sealed class SshFs(SshEnvironment environment) : IEnvironmentFs
{
    public ValueTask<FsEntryInfo> StatAsync(EnvPath path, CancellationToken ct = default) =>
        Run(path, mutating: false, ct, async (sftp, p) =>
        {
            var file = await sftp.GetAsync(p, ct).ConfigureAwait(false);
            if (file.Attributes.IsSymbolicLink)
            {
                // 跟随之后仍是符号链接 = 悬空链接
                throw new EnvironmentException(EnvironmentErrorCode.NotFound, path.Value);
            }

            return Describe(file.Attributes);
        });

    public ValueTask<FsEntryInfo> LStatAsync(EnvPath path, CancellationToken ct = default) =>
        Run(path, mutating: false, ct, async (sftp, p) =>
        {
            ISftpFile file;
            try
            {
                file = await sftp.GetAsync(p, ct).ConfigureAwait(false);
            }
            catch (SftpPathNotFoundException) when (path.FileName.Length > 0)
            {
                // 悬空的符号链接:服务器的 realpath 会解析到(不存在的)目标,Get 因此失败。目录列表里的条目是 lstat 语义,从那里找
                if (await FindInParentAsync(sftp, path, ct).ConfigureAwait(false) is { } entry)
                {
                    return Describe(entry.Attributes);
                }

                throw;
            }

            if (file.Attributes.IsSymbolicLink)
            {
                return Describe(file.Attributes);
            }

            if (Style == PathStyle.Posix && path.FileName.Length > 0)
            {
                var parent = await sftp.GetAsync(ToSftp(path.Parent), ct).ConfigureAwait(false);
                var expected = parent.FullName.TrimEnd('/') + "/" + path.FileName;
                if (!string.Equals(file.FullName, expected, StringComparison.Ordinal))
                {
                    return new FsEntryInfo(FsEntryKind.Symlink, 0, file.Attributes.LastWriteTimeUtc);
                }
            }

            return Describe(file.Attributes);
        });

    public ValueTask<EnvPath> RealPathAsync(EnvPath path, CancellationToken ct = default) =>
        Run(path, mutating: false, ct, async (sftp, p) =>
        {
            var file = await sftp.GetAsync(p, ct).ConfigureAwait(false);
            return EnvPath.Parse(PathRules.FromSftpPath(Style, file.FullName), Style);
        });

    public ValueTask<IReadOnlyList<FsDirEntry>> ListAsync(EnvPath path, CancellationToken ct = default) =>
        Run<IReadOnlyList<FsDirEntry>>(path, mutating: false, ct, async (sftp, p) =>
        {
            var directory = await sftp.GetAsync(p, ct).ConfigureAwait(false);
            if (!directory.IsDirectory)
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotDirectory, path.Value);
            }

            var entries = new List<FsDirEntry>();
            await foreach (var file in sftp.ListDirectoryAsync(p, ct).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                entries.Add(new FsDirEntry(file.Name, Describe(file.Attributes)));
            }

            entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return entries;
        });

    public async ValueTask<Stream> OpenReadAsync(EnvPath path, CancellationToken ct = default)
    {
        var link = await environment.AcquireLinkAsync(ct).ConfigureAwait(false);
        try
        {
            var p = ToSftp(path);
            var file = await link.Sftp.GetAsync(p, ct).ConfigureAwait(false);
            if (file.IsDirectory)
            {
                throw new EnvironmentException(EnvironmentErrorCode.IsDirectory, path.Value);
            }

            var stream = await link.Sftp.OpenAsync(p, FileMode.Open, FileAccess.Read, ct).ConfigureAwait(false);
            return new SshFileStream(stream, link, path.Value, mutating: false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Wrap(link, e, path, mutating: false);
        }
    }

    public async ValueTask<Stream> OpenWriteAsync(EnvPath path, FsWriteOptions options, CancellationToken ct = default)
    {
        var link = await environment.AcquireLinkAsync(ct).ConfigureAwait(false);
        try
        {
            var p = ToSftp(path);
            if (options.CreateParents && path.Parent != path)
            {
                await MkdirsAsync(link.Sftp, path.Parent, ct).ConfigureAwait(false);
            }

            var existing = await TryGetAsync(link.Sftp, p, ct).ConfigureAwait(false);
            if (existing is { IsDirectory: true })
            {
                throw new EnvironmentException(EnvironmentErrorCode.IsDirectory, path.Value);
            }

            if (existing is not null && options.Exclusive)
            {
                throw new EnvironmentException(EnvironmentErrorCode.AlreadyExists, path.Value);
            }

            SftpFileStream stream;
            try
            {
                stream = await link.Sftp.OpenAsync(p, options.Exclusive ? FileMode.CreateNew : FileMode.Create, FileAccess.Write, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not (OperationCanceledException or EnvironmentException) && !SshErrors.IsConnectionLoss(e)
                && e is not (SftpPathNotFoundException or SftpPermissionDeniedException) && options.Exclusive)
            {
                // 独占创建失败:如果现在目标已经存在,是并发抢先创建了
                if (await TryGetAsync(link.Sftp, p, ct).ConfigureAwait(false) is not null)
                {
                    throw new EnvironmentException(EnvironmentErrorCode.AlreadyExists, path.Value, e);
                }

                throw;
            }

            Func<ValueTask>? afterClose = options.UnixMode is { } mode && Style == PathStyle.Posix
                ? () => new ValueTask(Task.Run(() => link.Sftp.ChangePermissions(p, OctalDigits(mode))))
                : null;
            return new SshFileStream(stream, link, path.Value, mutating: true, afterClose);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Wrap(link, e, path, mutating: true);
        }
    }

    public ValueTask MkdirAsync(EnvPath path, bool recursive, CancellationToken ct = default) =>
        Run(path, mutating: true, ct, async (sftp, p) =>
        {
            if (recursive)
            {
                var existing = await TryGetAsync(sftp, p, ct).ConfigureAwait(false);
                if (existing is { IsDirectory: false })
                {
                    throw new EnvironmentException(EnvironmentErrorCode.AlreadyExists, path.Value);
                }

                await MkdirsAsync(sftp, path, ct).ConfigureAwait(false);
                return 0;
            }

            if (await TryGetAsync(sftp, p, ct).ConfigureAwait(false) is not null)
            {
                throw new EnvironmentException(EnvironmentErrorCode.AlreadyExists, path.Value);
            }

            var parent = await TryGetAsync(sftp, ToSftp(path.Parent), ct).ConfigureAwait(false);
            if (parent is null)
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotFound, path.Parent.Value);
            }

            await sftp.CreateDirectoryAsync(p, ct).ConfigureAwait(false);
            return 0;
        }).ToVoid();

    public ValueTask RenameAsync(EnvPath source, EnvPath destination, bool replace, CancellationToken ct = default) =>
        Run(source, mutating: true, ct, async (sftp, from) =>
        {
            var to = ToSftp(destination);
            if (Style == PathStyle.Posix && (await LStatAsync(source, ct).ConfigureAwait(false)).IsSymlink)
            {
                if (!replace && await TryGetAsync(sftp, to, ct).ConfigureAwait(false) is not null)
                {
                    throw new EnvironmentException(EnvironmentErrorCode.AlreadyExists, destination.Value);
                }

                await ExecAsync($"mv {(replace ? "-f" : "-n")} -- {ShellCommands.PosixQuote(source.Value)} {ShellCommands.PosixQuote(destination.Value)}", source, ct).ConfigureAwait(false);
                return 0;
            }

            var info = await sftp.GetAsync(from, ct).ConfigureAwait(false);
            var existing = await TryGetAsync(sftp, to, ct).ConfigureAwait(false);
            if (existing is not null && !replace)
            {
                throw new EnvironmentException(EnvironmentErrorCode.AlreadyExists, destination.Value);
            }

            if (existing is null)
            {
                await sftp.RenameFileAsync(from, to, ct).ConfigureAwait(false);
                return 0;
            }

            if (info.IsDirectory)
            {
                if (existing.IsDirectory && await HasChildrenAsync(sftp, to, ct).ConfigureAwait(false))
                {
                    throw new EnvironmentException(EnvironmentErrorCode.DirectoryNotEmpty, destination.Value);
                }

                await (existing.IsDirectory ? sftp.DeleteDirectoryAsync(to, ct) : sftp.DeleteFileAsync(to, ct)).ConfigureAwait(false);
                await sftp.RenameFileAsync(from, to, ct).ConfigureAwait(false);
                return 0;
            }

            if (existing.IsDirectory)
            {
                throw new EnvironmentException(EnvironmentErrorCode.IsDirectory, destination.Value);
            }

            // posix-rename 扩展能原子地覆盖;服务器不支持时退到「先删后改名」
            try
            {
                await Task.Run(() => sftp.RenameFile(from, to, isPosix: true), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SshException && !SshErrors.IsConnectionLoss(e) && e is not (SftpPathNotFoundException or SftpPermissionDeniedException))
            {
                await sftp.DeleteFileAsync(to, ct).ConfigureAwait(false);
                await sftp.RenameFileAsync(from, to, ct).ConfigureAwait(false);
            }

            return 0;
        }).ToVoid();

    public ValueTask DeleteFileAsync(EnvPath path, CancellationToken ct = default) =>
        Run(path, mutating: true, ct, async (sftp, p) =>
        {
            var info = await LStatAsync(path, ct).ConfigureAwait(false);
            if (info.IsDirectory)
            {
                throw new EnvironmentException(EnvironmentErrorCode.IsDirectory, path.Value);
            }

            if (info.IsSymlink)
            {
                await RemoveLinkAsync(path, ct).ConfigureAwait(false);
                return 0;
            }

            await sftp.DeleteFileAsync(p, ct).ConfigureAwait(false);
            return 0;
        }).ToVoid();

    public ValueTask DeleteDirectoryAsync(EnvPath path, bool recursive, CancellationToken ct = default) =>
        Run(path, mutating: true, ct, async (sftp, p) =>
        {
            var info = await LStatAsync(path, ct).ConfigureAwait(false);
            if (info.IsSymlink)
            {
                await RemoveLinkAsync(path, ct).ConfigureAwait(false);
                return 0;
            }

            if (!info.IsDirectory)
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotDirectory, path.Value);
            }

            if (!recursive && await HasChildrenAsync(sftp, p, ct).ConfigureAwait(false))
            {
                throw new EnvironmentException(EnvironmentErrorCode.DirectoryNotEmpty, path.Value);
            }

            if (recursive && Style == PathStyle.Posix)
            {
                // rm -rf 不跟随符号链接,也比逐个 SFTP 请求快得多
                await ExecAsync($"rm -rf -- {ShellCommands.PosixQuote(path.Value)}", path, ct).ConfigureAwait(false);
                return 0;
            }

            await DeleteTreeAsync(sftp, path, recursive, ct).ConfigureAwait(false);
            return 0;
        }).ToVoid();

    public async ValueTask<long> CopyFileAsync(EnvPath source, EnvPath destination, long maxBytes, CancellationToken ct = default) =>
        await this.CopyFileViaStreamsAsync(source, destination, maxBytes, ct).ConfigureAwait(false);

    public IFsWatcher Watch(EnvPath directory, FsWatchOptions options) => new PollingFsWatcher(this, directory, options);

    // ---- 内部 ----

    private PathStyle Style => environment.Facts.PathStyle;

    private string ToSftp(EnvPath path)
    {
        if (path.Style != Style || !path.IsAbsolute)
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidPath, $"remote environment needs an absolute {Style} path");
        }

        return PathRules.ToSftpPath(Style, path.Value);
    }

    private async ValueTask<T> Run<T>(EnvPath path, bool mutating, CancellationToken ct, Func<SftpClient, string, Task<T>> action)
    {
        var link = await environment.AcquireLinkAsync(ct).ConfigureAwait(false);
        try
        {
            return await action(link.Sftp, ToSftp(path)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Wrap(link, e, path, mutating);
        }
    }

    private static EnvironmentException Wrap(SshLink link, Exception e, EnvPath path, bool mutating)
    {
        if (SshErrors.IsConnectionLoss(e))
        {
            link.MarkBroken();
        }

        return SshErrors.Map(e, path.Value, mutating);
    }

    private static FsEntryInfo Describe(SftpFileAttributes a)
    {
        var kind = a.IsSymbolicLink ? FsEntryKind.Symlink : a.IsDirectory ? FsEntryKind.Directory : a.IsRegularFile ? FsEntryKind.File : FsEntryKind.Other;
        var mode = (a.OwnerCanRead ? 0x100 : 0) | (a.OwnerCanWrite ? 0x80 : 0) | (a.OwnerCanExecute ? 0x40 : 0)
            | (a.GroupCanRead ? 0x20 : 0) | (a.GroupCanWrite ? 0x10 : 0) | (a.GroupCanExecute ? 0x8 : 0)
            | (a.OthersCanRead ? 0x4 : 0) | (a.OthersCanWrite ? 0x2 : 0) | (a.OthersCanExecute ? 0x1 : 0)
            | (a.IsUIDBitSet ? 0x800 : 0) | (a.IsGroupIDBitSet ? 0x400 : 0) | (a.IsStickyBitSet ? 0x200 : 0);
        return new FsEntryInfo(kind, a.Size, new DateTimeOffset(a.LastWriteTimeUtc, TimeSpan.Zero), mode);
    }

    private static async Task<ISftpFile?> TryGetAsync(SftpClient sftp, string path, CancellationToken ct)
    {
        try
        {
            return await sftp.GetAsync(path, ct).ConfigureAwait(false);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    private static async Task<bool> HasChildrenAsync(SftpClient sftp, string path, CancellationToken ct)
    {
        await foreach (var file in sftp.ListDirectoryAsync(path, ct).ConfigureAwait(false))
        {
            if (file.Name is not ("." or ".."))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>mkdir -p:自上而下补全不存在的目录。已存在但不是目录时报 AlreadyExists。</summary>
    private async Task MkdirsAsync(SftpClient sftp, EnvPath path, CancellationToken ct)
    {
        var missing = new Stack<EnvPath>();
        var current = path;
        for (;;)
        {
            var existing = await TryGetAsync(sftp, ToSftp(current), ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!existing.IsDirectory)
                {
                    throw new EnvironmentException(EnvironmentErrorCode.NotDirectory, current.Value);
                }

                break;
            }

            missing.Push(current);
            var parent = current.Parent;
            if (parent == current)
            {
                break;
            }

            current = parent;
        }

        while (missing.TryPop(out var next))
        {
            try
            {
                await sftp.CreateDirectoryAsync(ToSftp(next), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is SshException && !SshErrors.IsConnectionLoss(e) && e is not SftpPermissionDeniedException)
            {
                // 并发创建:别人先建好了,等价于成功
                if (await TryGetAsync(sftp, ToSftp(next), ct).ConfigureAwait(false) is not { IsDirectory: true })
                {
                    throw;
                }
            }
        }
    }

    private async Task DeleteTreeAsync(SftpClient sftp, EnvPath directory, bool recursive, CancellationToken ct)
    {
        if (recursive)
        {
            await foreach (var file in sftp.ListDirectoryAsync(ToSftp(directory), ct).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                var child = directory.Join(file.Name);
                // 符号链接当文件删(不跟随进去删目标)
                if (file.IsDirectory && !file.IsSymbolicLink)
                {
                    await DeleteTreeAsync(sftp, child, true, ct).ConfigureAwait(false);
                }
                else
                {
                    await sftp.DeleteFileAsync(ToSftp(child), ct).ConfigureAwait(false);
                }
            }
        }

        await sftp.DeleteDirectoryAsync(ToSftp(directory), ct).ConfigureAwait(false);
    }

    private static short OctalDigits(int mode) => short.Parse(Convert.ToString(mode & 0xFFF, 8), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<ISftpFile?> FindInParentAsync(SftpClient sftp, EnvPath path, CancellationToken ct)
    {
        await foreach (var file in sftp.ListDirectoryAsync(PathRules.ToSftpPath(path.Style, path.Parent.Value), ct).ConfigureAwait(false))
        {
            if (string.Equals(file.Name, path.FileName, StringComparison.Ordinal))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>
    /// 删除符号链接本身。SSH.NET 的 SFTP 删除/改名会先对路径做 realpath,对符号链接就变成了操作它的目标(删一个指向文件的链接会把目标文件删掉),
    /// 所以链接只能通过 exec 的 <c>rm</c> / <c>mv</c> 处理。
    /// </summary>
    private async Task RemoveLinkAsync(EnvPath path, CancellationToken ct)
    {
        if (Style != PathStyle.Posix)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "symbolic links on Windows hosts");
        }

        await ExecAsync($"rm -f -- {ShellCommands.PosixQuote(path.Value)}", path, ct).ConfigureAwait(false);
    }

    private async Task ExecAsync(string commandText, EnvPath path, CancellationToken ct)
    {
        var link = await environment.AcquireLinkAsync(ct).ConfigureAwait(false);
        var command = link.Control.CreateCommand(commandText);
        command.CommandTimeout = TimeSpan.FromMinutes(5);
        try
        {
            await command.ExecuteAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw Wrap(link, e, path, mutating: true);
        }

        if (command.ExitStatus != 0)
        {
            var error = command.Error;
            throw new EnvironmentException(
                error.Contains("ermission denied", StringComparison.Ordinal) ? EnvironmentErrorCode.PermissionDenied
                    : error.Contains("No such file", StringComparison.Ordinal) ? EnvironmentErrorCode.NotFound
                    : EnvironmentErrorCode.Unsupported,
                $"{path.Value}: {error.Trim()}");
        }
    }

    /// <summary>符号链接自身的 SFTP 路径:规范化的父目录加名字(不能用 realpath,那会得到目标)。</summary>
    private async Task<string> LinkPathAsync(SftpClient sftp, EnvPath path, CancellationToken ct)
    {
        var parent = await sftp.GetAsync(ToSftp(path.Parent), ct).ConfigureAwait(false);
        return parent.FullName.TrimEnd('/') + "/" + path.FileName;
    }
}
