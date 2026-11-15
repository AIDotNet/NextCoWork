namespace Ncw.Agent.Workspace.Review;

public enum ReviewChangeKind
{
    Created,
    Modified,

    /// <summary>预留:工具层的 <c>ChangeRecorder</c> 目前只产生 Created / Modified。</summary>
    Deleted,
}

/// <summary>整轮的撤销态。</summary>
public enum ReviewState
{
    Applied,
    Reverted,
}

/// <summary>Run 收尾时工具层采集到的一个文件改动(Host 把 Tools 的 <c>PendingChange</c> 转成它:Workspace 不引用 Tools)。</summary>
/// <param name="RelPath">项目相对路径(<c>/</c> 分隔);项目外的文件是绝对路径。</param>
/// <param name="AbsPath">解析并 realpath 之后的绝对路径(只给诊断,撤销时不用它碰文件)。</param>
/// <param name="Before">首个 before(去掉 BOM 的文本);新建为 null。</param>
/// <param name="After">最后一个 after。</param>
/// <param name="InWorkspace">false = 改到了项目外的文件,禁撤销。</param>
public sealed record RecordedChange(string RelPath, string AbsPath, string? Before, string After, ReviewChangeKind Kind, bool InWorkspace);

/// <summary>摘要里的一个文件条目(不含全文)。</summary>
public sealed record ReviewFileEntry(string Path, ReviewChangeKind Kind, int Additions, int Deletions, bool Oversize, bool InWorkspace);

/// <summary>一轮(顶层 Run)的改动集摘要。时间是 Unix 毫秒。</summary>
public sealed record ReviewChangeSet(
    string RunId,
    string SessionId,
    string WorkspaceId,
    ReviewState State,
    int FileCount,
    int Additions,
    int Deletions,
    IReadOnlyList<ReviewFileEntry> Files,
    long CreatedAt,
    long UpdatedAt);

/// <summary>撤销 / 重做要用的完整快照行(含全文)。oversize 的两侧全文为 null(内容没入库)。</summary>
public sealed record ReviewSnapshot(
    string Path,
    ReviewChangeKind Kind,
    string? Before,
    string? After,
    string? BeforeHash,
    string AfterHash,
    bool Oversize,
    bool InWorkspace);

/// <summary>单文件的 before / after 全文。created 的 before 是空串;oversize 的两侧都是空串。</summary>
public sealed record ReviewFileDiff(string Path, ReviewChangeKind Kind, string Before, string After, bool Oversize);

public enum ReviewFileStatus
{
    /// <summary>已回写。</summary>
    Ok,

    /// <summary>磁盘现状与记录不符且没有强制。</summary>
    Conflict,

    /// <summary>超限(内容没入库)或在项目外:跳过。原因见 <see cref="ReviewFileResult.Skip"/>。</summary>
    Skipped,

    /// <summary>写回失败(<see cref="ReviewFileResult.Error"/> 是英文诊断,不含路径)。</summary>
    Error,
}

public enum ReviewSkipReason
{
    None,
    Oversize,
    OutsideWorkspace,
}

public sealed record ReviewFileResult(string Path, ReviewFileStatus Status, ReviewSkipReason Skip = ReviewSkipReason.None, string? Error = null);

/// <summary>撤销 / 重做整轮的结果。<see cref="State"/> 是操作之后整轮的撤销态(有文件真的回写了才翻转)。</summary>
public sealed record ReviewMutation(string RunId, ReviewState State, IReadOnlyList<ReviewFileResult> Files);

/// <summary>
/// 改动集的存储接口(Workspace 不引用存储层;Host 用 <c>FileChangeRepository</c> 实现)。
/// 语义见存储实现:同一 root Run 叠加时保留最早的 before、推进到最新的 after;超限不存内容。
/// </summary>
public interface IFileChangeStore
{
    void Save(string rootRunId, string sourceRunId, string sessionId, string workspaceId, IReadOnlyList<RecordedChange> changes, long at);

    ReviewChangeSet? Get(string runId);

    IReadOnlyList<ReviewChangeSet> ListBySession(string sessionId);

    ReviewFileDiff? GetDiff(string runId, string path);

    IReadOnlyList<ReviewSnapshot> ListSnapshots(string runId);

    void SetState(string runId, ReviewState state, long at);
}
