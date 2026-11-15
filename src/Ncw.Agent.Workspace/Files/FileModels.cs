using Ncw.Agent.Environments;
using Ncw.Core.Domain;

namespace Ncw.Agent.Workspace.Files;

/// <summary>一个项目:标识 + 执行环境 + 环境里的根目录。文件服务所有操作都从它起步(AGENTS.md §5:只经 <see cref="IExecutionEnvironment"/>)。</summary>
public sealed record FileProject(string Id, IExecutionEnvironment Environment, EnvPath Root);

/// <summary>尺寸约定(和协议 <c>FilesLimits</c> 同值;协议层在 Host 里核对一致)。</summary>
public static class FileLimits
{
    public const int TextMax = 2 * 1024 * 1024;
    public const int InlineImageMax = 3 * 1024 * 1024;
    public const int ImageMax = 16 * 1024 * 1024;
    public const int RangeMax = 1024 * 1024;
    public const int ListDefault = 2000;
    public const int ListMax = 5000;
    public const int SuggestDefault = 20;
    public const int SuggestMax = 50;
    public const int SearchDefault = 200;
    public const int SearchMax = 1000;
}

public sealed record FileEntry(string Name, string Path, bool IsDirectory, bool IsLink, bool Hidden, bool Ignored, long? Size, long? Mtime);

public sealed record DirListing(string Path, IReadOnlyList<FileEntry> Entries, int Total, bool Truncated);

public enum FileKind
{
    Text,
    Image,
    Binary,
}

public enum BinaryReason
{
    Unsupported,
    TooLarge,
    Encoding,
}

/// <summary>一个文件的内容(分类后)。<see cref="Revision"/> 是内容摘要(SHA-256 十六进制;过大没读的是 <c>large:&lt;size&gt;:&lt;mtime&gt;</c>)。</summary>
public sealed record FileContent(
    FileKind Kind,
    string Path,
    long Size,
    string Revision,
    string? Text = null,
    string? Mime = null,
    byte[]? Data = null,
    bool Chunked = false,
    BinaryReason? Reason = null);

public sealed record FileRange(byte[] Data, long Offset, int Length, long Size, string Revision, bool Eof);

public sealed record FileWriteResult(string Path, long Size, string Revision);

public enum FileOperation
{
    CreateFile,
    CreateDirectory,
    Rename,
    Move,
    Copy,
    Delete,
}

public sealed record FileMutation(string Path, string? Destination = null, string? RecoveryPath = null);

public sealed record FileSuggestion(string Path, string Name);

public sealed record SuggestResult(IReadOnlyList<FileSuggestion> Suggestions, bool Truncated);

public sealed record SearchRequest(string Pattern, string Path = "", string? Glob = null, bool Regex = false, bool CaseSensitive = false, int Limit = 0);

public sealed record FileMatch(string Path, int Line, int Column, string Text);

public sealed record SearchResult(IReadOnlyList<FileMatch> Matches, int FilesSearched, bool Truncated);

/// <summary>服务的可调参数(测试里缩小量和时间)。</summary>
public sealed class FileServiceOptions
{
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>文件名建议的索引新鲜期。够撑住一串连续敲键,又短到切了分支几秒后就能看到新文件。</summary>
    public TimeSpan IndexTtl { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>一次遍历最多收多少项(建议索引 / 搜索共用)。</summary>
    public int MaxWalkEntries { get; init; } = 40_000;

    /// <summary>遍历的墙钟预算:超时比结果不全严重得多,所以截断并如实标出来。</summary>
    public TimeSpan WalkDeadline { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan SearchDeadline { get; init; } = TimeSpan.FromSeconds(8);

    public int MaxWalkDepth { get; init; } = 32;

    /// <summary>内容搜索跳过大于它的文件。</summary>
    public int SearchFileMax { get; init; } = 1024 * 1024;

    public int CopyMaxEntries { get; init; } = 10_000;

    public long CopyMaxBytes { get; init; } = 256L * 1024 * 1024;
}
