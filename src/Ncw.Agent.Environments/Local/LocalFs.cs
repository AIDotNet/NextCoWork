namespace Ncw.Agent.Environments;

/// <summary>本机文件系统。全项目唯一直接使用 <c>System.IO</c> 触碰设备本地文件的地方之一(另一处是 SSH 环境读自己的密钥和配置)。</summary>
internal sealed class LocalFs : IEnvironmentFs
{
    private static readonly PathStyle HostStyle = OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Posix;

    public static PathStyle Style => HostStyle;

    public ValueTask<FsEntryInfo> StatAsync(EnvPath path, CancellationToken ct = default) =>
        Run(path, static p => Inspect(p, follow: true), ct);

    public ValueTask<FsEntryInfo> LStatAsync(EnvPath path, CancellationToken ct = default) =>
        Run(path, static p => Inspect(p, follow: false), ct);

    public ValueTask<EnvPath> RealPathAsync(EnvPath path, CancellationToken ct = default) =>
        Run(path, static p => EnvPath.Parse(RealPath(p), HostStyle), ct);

    public ValueTask<IReadOnlyList<FsDirEntry>> ListAsync(EnvPath path, CancellationToken ct = default) =>
        Run<IReadOnlyList<FsDirEntry>>(path, static p =>
        {
            if (File.Exists(p))
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotDirectory, p);
            }

            var entries = new List<FsDirEntry>();
            foreach (var info in new DirectoryInfo(p).EnumerateFileSystemInfos())
            {
                entries.Add(new FsDirEntry(info.Name, Describe(info)));
            }

            entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return entries;
        }, ct);

    public ValueTask<Stream> OpenReadAsync(EnvPath path, CancellationToken ct = default) =>
        Run<Stream>(path, static p =>
        {
            if (Directory.Exists(p))
            {
                throw new EnvironmentException(EnvironmentErrorCode.IsDirectory, p);
            }

            return new FileStream(p, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
        }, ct);

    public ValueTask<Stream> OpenWriteAsync(EnvPath path, FsWriteOptions options, CancellationToken ct = default) =>
        Run<Stream>(path, p =>
        {
            if (Directory.Exists(p))
            {
                throw new EnvironmentException(EnvironmentErrorCode.IsDirectory, p);
            }

            if (options.CreateParents && Path.GetDirectoryName(p) is { Length: > 0 } parent)
            {
                Directory.CreateDirectory(parent);
            }

            var streamOptions = new FileStreamOptions
            {
                Mode = options.Exclusive ? FileMode.CreateNew : FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous,
            };
            if (options.UnixMode is { } mode && !OperatingSystem.IsWindows())
            {
                streamOptions.UnixCreateMode = (UnixFileMode)mode;
            }

            return new FileStream(p, streamOptions);
        }, ct);

    public ValueTask MkdirAsync(EnvPath path, bool recursive, CancellationToken ct = default) =>
        RunVoid(path, p =>
        {
            if (recursive)
            {
                if (File.Exists(p))
                {
                    throw new EnvironmentException(EnvironmentErrorCode.AlreadyExists, p);
                }

                Directory.CreateDirectory(p);
                return;
            }

            if (File.Exists(p) || Directory.Exists(p))
            {
                throw new EnvironmentException(EnvironmentErrorCode.AlreadyExists, p);
            }

            if (Path.GetDirectoryName(p) is { Length: > 0 } parent && !Directory.Exists(parent))
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotFound, parent);
            }

            Directory.CreateDirectory(p);
        }, ct);

    public ValueTask RenameAsync(EnvPath source, EnvPath destination, bool replace, CancellationToken ct = default) =>
        RunPair(source, destination, (from, to) =>
        {
            var sourceInfo = Inspect(from, follow: false);
            var destinationExists = Exists(to);
            if (destinationExists && !replace)
            {
                throw new EnvironmentException(EnvironmentErrorCode.AlreadyExists, to);
            }

            if (sourceInfo.IsDirectory)
            {
                if (destinationExists && Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
                {
                    throw new EnvironmentException(EnvironmentErrorCode.DirectoryNotEmpty, to);
                }

                if (destinationExists)
                {
                    Directory.Delete(to);
                }

                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to, overwrite: replace);
            }

            return 0L;
        }, ct).ToVoid();

    public ValueTask DeleteFileAsync(EnvPath path, CancellationToken ct = default) =>
        RunVoid(path, static p =>
        {
            var info = Inspect(p, follow: false);
            if (info.IsDirectory)
            {
                throw new EnvironmentException(EnvironmentErrorCode.IsDirectory, p);
            }

            File.Delete(p);
        }, ct);

    public ValueTask DeleteDirectoryAsync(EnvPath path, bool recursive, CancellationToken ct = default) =>
        RunVoid(path, p =>
        {
            var info = Inspect(p, follow: false);
            if (!info.IsDirectory && !info.IsSymlink)
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotDirectory, p);
            }

            if (!recursive && info.IsDirectory && Directory.EnumerateFileSystemEntries(p).Any())
            {
                throw new EnvironmentException(EnvironmentErrorCode.DirectoryNotEmpty, p);
            }

            Directory.Delete(p, recursive);
        }, ct);

    public ValueTask<long> CopyFileAsync(EnvPath source, EnvPath destination, long maxBytes, CancellationToken ct = default) =>
        RunPair(source, destination, (from, to) =>
        {
            var info = Inspect(from, follow: true);
            if (info.IsDirectory)
            {
                throw new EnvironmentException(EnvironmentErrorCode.IsDirectory, from);
            }

            if (!info.IsFile)
            {
                throw new EnvironmentException(EnvironmentErrorCode.Unsupported, from);
            }

            if (info.Size > maxBytes)
            {
                throw new EnvironmentException(EnvironmentErrorCode.TooLarge, from);
            }

            if (Exists(to))
            {
                throw new EnvironmentException(EnvironmentErrorCode.AlreadyExists, to);
            }

            File.Copy(from, to, overwrite: false);
            return info.Size;
        }, ct);

    public IFsWatcher Watch(EnvPath directory, FsWatchOptions options) => new LocalFsWatcher(Native(directory), directory.Style, options);

    internal static string Native(EnvPath path)
    {
        if (path.Style != HostStyle || !path.IsAbsolute)
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidPath, $"local environment needs an absolute {HostStyle} path");
        }

        return path.Value;
    }

    /// <summary>
    /// 同步完成的文件操作,统一在这里把异常映射成 <see cref="EnvironmentException"/>,并以「出错的任务」而不是同步抛出的方式返回
    /// (调用方把任务存起来、<c>Task.WhenAll</c> 时行为才和真正的异步操作一致)。
    /// </summary>
    private static ValueTask<T> Run<T>(EnvPath path, Func<string, T> action, CancellationToken ct)
    {
        string native;
        try
        {
            ct.ThrowIfCancellationRequested();
            native = Native(path);
        }
        catch (Exception e)
        {
            return ValueTask.FromException<T>(e);
        }

        try
        {
            return ValueTask.FromResult(action(native));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ValueTask.FromException<T>(LocalFsErrors.Map(e, native));
        }
    }

    private static ValueTask RunVoid(EnvPath path, Action<string> action, CancellationToken ct) =>
        Run(path, p =>
        {
            action(p);
            return 0;
        }, ct).ToVoid();

    private static ValueTask<T> RunPair<T>(EnvPath first, EnvPath second, Func<string, string, T> action, CancellationToken ct)
    {
        string from;
        string to;
        try
        {
            ct.ThrowIfCancellationRequested();
            from = Native(first);
            to = Native(second);
        }
        catch (Exception e)
        {
            return ValueTask.FromException<T>(e);
        }

        try
        {
            return ValueTask.FromResult(action(from, to));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return ValueTask.FromException<T>(LocalFsErrors.Map(e, to));
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path);
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static FsEntryInfo Inspect(string path, bool follow)
    {
        var attributes = File.GetAttributes(path);
        FileSystemInfo info = attributes.HasFlag(FileAttributes.Directory) ? new DirectoryInfo(path) : new FileInfo(path);
        if (follow && IsLink(info))
        {
            FileSystemInfo? target;
            try
            {
                target = info.ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (IOException)
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotFound, path);
            }

            if (target is null || !Exists(target.FullName))
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotFound, path);
            }

            return Inspect(target.FullName, follow: false);
        }

        return Describe(info);
    }

    private static bool IsLink(FileSystemInfo info) => info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null;

    private static FsEntryInfo Describe(FileSystemInfo info)
    {
        if (IsLink(info))
        {
            return new FsEntryInfo(FsEntryKind.Symlink, 0, info.LastWriteTimeUtc);
        }

        var mode = 0;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                mode = (int)info.UnixFileMode;
            }
            catch (IOException)
            {
                // 条目刚被删掉
            }
        }

        return info switch
        {
            DirectoryInfo => new FsEntryInfo(FsEntryKind.Directory, 0, info.LastWriteTimeUtc, mode),
            FileInfo file when file.Attributes.HasFlag(FileAttributes.Device) => new FsEntryInfo(FsEntryKind.Other, 0, info.LastWriteTimeUtc, mode),
            FileInfo file => new FsEntryInfo(FsEntryKind.File, file.Length, info.LastWriteTimeUtc, mode),
            _ => new FsEntryInfo(FsEntryKind.Other, 0, info.LastWriteTimeUtc, mode),
        };
    }

    /// <summary>
    /// 手写的 realpath:逐段展开符号链接(相对和绝对目标都处理),中间段不存在抛 NotFound。
    /// 不依赖 libc,三个平台同一实现;macOS 的 <c>/var</c> → <c>/private/var</c> 靠它展开。
    /// </summary>
    internal static string RealPath(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var pending = new LinkedList<string>(path[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries));
        var resolved = new List<string>();
        var current = root;
        var links = 0;
        while (pending.First is { } node)
        {
            pending.RemoveFirst();
            var part = node.Value;
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (resolved.Count > 0)
                {
                    resolved.RemoveAt(resolved.Count - 1);
                    current = Combine(root, resolved);
                }

                continue;
            }

            var candidate = Path.Combine(current, part);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(candidate);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotFound, candidate);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw LocalFsErrors.Map(e, candidate);
            }

            var info = new FileInfo(candidate);
            string? target = attributes.HasFlag(FileAttributes.ReparsePoint) ? info.LinkTarget : null;
            if (target is null)
            {
                resolved.Add(part);
                current = candidate;
                continue;
            }

            if (++links > 40)
            {
                throw new EnvironmentException(EnvironmentErrorCode.NotFound, "too many levels of symbolic links");
            }

            var targetRoot = Path.GetPathRoot(target) ?? "";
            if (targetRoot.Length > 0)
            {
                root = targetRoot;
                resolved.Clear();
                current = root;
            }

            foreach (var segment in target[targetRoot.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                pending.AddFirst(segment);
            }
        }

        return Combine(root, resolved);
    }

    private static string Combine(string root, List<string> parts) =>
        parts.Count == 0 ? root : Path.Combine(root, string.Join(Path.DirectorySeparatorChar, parts));
}

internal static class LocalFsErrors
{
    public static EnvironmentException Map(Exception e, string path) => e switch
    {
        EnvironmentException env => env,
        FileNotFoundException or DirectoryNotFoundException => new EnvironmentException(EnvironmentErrorCode.NotFound, path, e),
        UnauthorizedAccessException when Directory.Exists(path) => new EnvironmentException(EnvironmentErrorCode.IsDirectory, path, e),
        UnauthorizedAccessException => new EnvironmentException(EnvironmentErrorCode.PermissionDenied, path, e),
        PathTooLongException or ArgumentException or NotSupportedException => new EnvironmentException(EnvironmentErrorCode.InvalidPath, path, e),
        IOException io when File.Exists(path) || Directory.Exists(path) => Classify(io, path),
        IOException => new EnvironmentException(EnvironmentErrorCode.Unsupported, $"{path}: {e.Message}", e),
        _ => new EnvironmentException(EnvironmentErrorCode.Unsupported, $"{path}: {e.Message}", e),
    };

    // 目标存在时的 IOException:区分「已存在」和「目录非空」等
    private static EnvironmentException Classify(IOException e, string path) =>
        Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()
            ? new EnvironmentException(EnvironmentErrorCode.DirectoryNotEmpty, path, e)
            : new EnvironmentException(EnvironmentErrorCode.AlreadyExists, path, e);

}
