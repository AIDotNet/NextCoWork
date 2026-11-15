namespace Ncw.Agent.Environments;

public enum FsEntryKind
{
    File,
    Directory,
    Symlink,
    Other,
}

/// <param name="Kind"><see cref="IEnvironmentFs.StatAsync"/> 跟随符号链接,所以不会得到 Symlink;<c>LStatAsync</c> 和目录列表可以。</param>
/// <param name="Size">字节数;目录和特殊文件的值由平台决定。</param>
/// <param name="UnixMode">权限位(低 12 位);不是 POSIX 的环境为 0。</param>
public sealed record FsEntryInfo(FsEntryKind Kind, long Size, DateTimeOffset ModifiedAt, int UnixMode = 0)
{
    public bool IsDirectory => Kind == FsEntryKind.Directory;

    public bool IsFile => Kind == FsEntryKind.File;

    public bool IsSymlink => Kind == FsEntryKind.Symlink;
}

/// <param name="Info">条目自身的信息(lstat,不跟随符号链接)。</param>
public sealed record FsDirEntry(string Name, FsEntryInfo Info);

/// <param name="Exclusive">目标已存在则失败(<see cref="EnvironmentErrorCode.AlreadyExists"/>),由文件系统原子判定。</param>
/// <param name="UnixMode">创建新文件时的权限位;<c>null</c> = 用系统默认。Windows 忽略。</param>
/// <param name="CreateParents">父目录不存在时一起创建。</param>
public sealed record FsWriteOptions(bool Exclusive = false, int? UnixMode = null, bool CreateParents = false);

public enum FsChangeKind
{
    Created,
    Modified,
    Deleted,
}

public sealed record FsChange(FsChangeKind Kind, EnvPath Path);

public sealed record FsWatchOptions
{
    public bool Recursive { get; init; }

    /// <summary>一批变化的静默窗口:最后一次变化之后等这么久没有新变化才送出。</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>只对靠轮询实现的环境(SSH)有意义:两次扫描的间隔。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>轮询时最多记录多少个条目,超出后只监视到这个数量为止(防止对巨大目录无限扫描)。</summary>
    public int MaxEntries { get; init; } = 20_000;
}

/// <summary>
/// 目录监视。本机用系统通知;SSH 的 SFTP 没有变更通知,改为对目录轮询,并在执行工具后调用 <see cref="Refresh"/> 主动刷新(06 §3)。
/// </summary>
public interface IFsWatcher : IAsyncDisposable
{
    /// <summary>按静默窗口合并后的变化批次;监视器释放后结束。只允许枚举一次。</summary>
    IAsyncEnumerable<IReadOnlyList<FsChange>> Batches { get; }

    /// <summary>请求立刻重新检查一次(本机实现是空操作)。</summary>
    void Refresh();
}

/// <summary>
/// 环境里的文件系统(移植自 2.x 的 <c>EnvironmentFs</c>)。所有路径都是 <see cref="EnvPath"/>,风格必须和环境一致。
/// 工具只能通过它访问文件;不要在这一层之外直接用 <c>System.IO</c>。
/// </summary>
public interface IEnvironmentFs
{
    /// <summary>跟随符号链接。不存在抛 <see cref="EnvironmentErrorCode.NotFound"/>。</summary>
    ValueTask<FsEntryInfo> StatAsync(EnvPath path, CancellationToken ct = default);

    /// <summary>不跟随符号链接。</summary>
    ValueTask<FsEntryInfo> LStatAsync(EnvPath path, CancellationToken ct = default);

    /// <summary>规范路径(解析所有符号链接)。不存在抛 <see cref="EnvironmentErrorCode.NotFound"/>。</summary>
    ValueTask<EnvPath> RealPathAsync(EnvPath path, CancellationToken ct = default);

    /// <summary>目录条目,按名字的序数顺序排序;条目信息不跟随符号链接。</summary>
    ValueTask<IReadOnlyList<FsDirEntry>> ListAsync(EnvPath path, CancellationToken ct = default);

    /// <summary>以流的方式读文件。调用方负责释放。</summary>
    ValueTask<Stream> OpenReadAsync(EnvPath path, CancellationToken ct = default);

    /// <summary>以流的方式写文件(覆盖,除非 <see cref="FsWriteOptions.Exclusive"/>)。调用方负责释放;释放即提交。</summary>
    ValueTask<Stream> OpenWriteAsync(EnvPath path, FsWriteOptions options, CancellationToken ct = default);

    /// <summary>创建目录。<paramref name="recursive"/> 为真时等同 <c>mkdir -p</c>(已存在不报错);否则已存在抛 <see cref="EnvironmentErrorCode.AlreadyExists"/>。</summary>
    ValueTask MkdirAsync(EnvPath path, bool recursive, CancellationToken ct = default);

    /// <summary>移动/改名。<paramref name="replace"/> 为假时目标已存在抛 <see cref="EnvironmentErrorCode.AlreadyExists"/>。</summary>
    ValueTask RenameAsync(EnvPath source, EnvPath destination, bool replace, CancellationToken ct = default);

    ValueTask DeleteFileAsync(EnvPath path, CancellationToken ct = default);

    /// <summary>删目录。<paramref name="recursive"/> 为假时目录非空抛 <see cref="EnvironmentErrorCode.DirectoryNotEmpty"/>。</summary>
    ValueTask DeleteDirectoryAsync(EnvPath path, bool recursive, CancellationToken ct = default);

    /// <summary>复制文件,源超过 <paramref name="maxBytes"/> 抛 <see cref="EnvironmentErrorCode.TooLarge"/>。目标已存在则失败。返回复制的字节数。</summary>
    ValueTask<long> CopyFileAsync(EnvPath source, EnvPath destination, long maxBytes, CancellationToken ct = default);

    IFsWatcher Watch(EnvPath directory, FsWatchOptions options);
}
