using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

// 右侧检查器的 ViewModel 依赖的意图接口(IConversationCommands 的同一个做法):真实现是对应的 *Actions,
// 单测里换成脚本化的假实现,不用起设备连接就能测文件树的惰性加载、Git 分组、终端的输入合并这些状态机。

/// <summary>文件管理器用到的 <see cref="FilesActions"/> 子集。</summary>
public interface IFilesCommands
{
    Task<ActionResult<FilesListResult>> ListAsync(string deviceId, string projectId, string path = "", string sort = "name", bool hideIgnored = false, int offset = 0, int limit = 0, CancellationToken ct = default);

    Task<ActionResult<FileContentDto>> ReadAsync(string deviceId, string projectId, string path, CancellationToken ct = default);

    Task<ActionResult<FilesRangeResult>> ReadRangeAsync(string deviceId, string projectId, string path, long offset, int length, string? revision = null, CancellationToken ct = default);

    Task<ActionResult<FilesWriteResult>> WriteTextAsync(string deviceId, string projectId, string path, string content, string revision, CancellationToken ct = default);

    Task<ActionResult<FilesMutateResult>> CreateFileAsync(string deviceId, string projectId, string path, CancellationToken ct = default);

    Task<ActionResult<FilesMutateResult>> CreateDirectoryAsync(string deviceId, string projectId, string path, CancellationToken ct = default);

    Task<ActionResult<FilesMutateResult>> RenameAsync(string deviceId, string projectId, string path, string destination, bool overwrite = false, CancellationToken ct = default);

    Task<ActionResult<FilesMutateResult>> DeleteAsync(string deviceId, string projectId, string path, CancellationToken ct = default);

    Task<ActionResult<FilesSuggestResult>> SuggestAsync(string deviceId, string projectId, string query, int limit = 0, CancellationToken ct = default);

    Task<ActionResult<FilesSearchResult>> SearchAsync(string deviceId, string projectId, FilesSearchParams query, CancellationToken ct = default);
}

/// <summary>Git 面板用到的 <see cref="GitActions"/> 子集。</summary>
public interface IGitCommands
{
    Task<ActionResult<GitOverviewDto>> RefreshAsync(ProjectRef project, CancellationToken ct = default);

    Task<ActionResult<GitBranchesResult>> ListBranchesAsync(ProjectRef project, CancellationToken ct = default);

    Task<ActionResult<GitCommitsResult>> ListCommitsAsync(ProjectRef project, int limit = 50, CancellationToken ct = default);

    Task<ActionResult<GitDiffDto>> GetDiffAsync(ProjectRef project, string path, bool staged, CancellationToken ct = default);

    Task<ActionResult<Unit>> StageAsync(ProjectRef project, IReadOnlyList<string> paths, CancellationToken ct = default);

    Task<ActionResult<Unit>> UnstageAsync(ProjectRef project, IReadOnlyList<string> paths, CancellationToken ct = default);

    Task<ActionResult<GitCommitDto>> CommitAsync(ProjectRef project, string message, CancellationToken ct = default);

    Task<ActionResult<Unit>> CheckoutAsync(ProjectRef project, string branch, CancellationToken ct = default);

    Task<ActionResult<GitBranchDto>> CreateBranchAsync(ProjectRef project, string name, bool checkout, CancellationToken ct = default);

    Task<ActionResult<Unit>> PullAsync(ProjectRef project, CancellationToken ct = default);

    Task<ActionResult<Unit>> PushAsync(ProjectRef project, CancellationToken ct = default);

    Task<ActionResult<GitGeneratedMessageDto>> GenerateMessageAsync(ProjectRef project, CancellationToken ct = default);
}

/// <summary>改动审查面板用到的 <see cref="ReviewActions"/> 子集。</summary>
public interface IReviewCommands
{
    Task<ActionResult<ReviewChangeSetDto[]>> ListAsync(string deviceId, string sessionId, CancellationToken ct = default);

    Task<ActionResult<ReviewFileDiffDto?>> FileDiffAsync(string deviceId, string runId, string path, CancellationToken ct = default);

    Task<ActionResult<string[]>> PrecheckUndoAsync(string deviceId, string runId, CancellationToken ct = default);

    Task<ActionResult<ReviewMutationDto>> UndoAsync(string deviceId, string runId, bool force = false, CancellationToken ct = default);

    Task<ActionResult<ReviewMutationDto>> RedoAsync(string deviceId, string runId, CancellationToken ct = default);
}

/// <summary>终端面板用到的 <see cref="TerminalActions"/> 子集。</summary>
public interface ITerminalCommands
{
    Task<ActionResult<TerminalDto[]>> ListAsync(string deviceId, string projectId, CancellationToken ct = default);

    Task<ActionResult<TerminalPrepareResult>> PrepareAsync(string deviceId, string projectId, int columns, int rows, string? terminalId = null, string? cwd = null, CancellationToken ct = default);

    Task<ActionResult<TerminalApproveResult>> ApproveAsync(string deviceId, string intentId, bool approved, CancellationToken ct = default);

    Task<ActionResult<TerminalDto>> CreateAsync(string deviceId, string projectId, int columns, int rows, string? terminalId = null, string? cwd = null, string? approval = null, CancellationToken ct = default);

    Task<ActionResult<Unit>> AttachAsync(string deviceId, string terminalId, Devices.ITerminalSink sink, CancellationToken ct = default);

    Task DetachAsync(string deviceId, string terminalId);

    Task<ActionResult<Unit>> WriteAsync(string deviceId, string terminalId, byte[] data, string? idem = null, CancellationToken ct = default);

    Task<ActionResult<Unit>> ResizeAsync(string deviceId, string terminalId, int columns, int rows, CancellationToken ct = default);

    Task<ActionResult<Unit>> KillAsync(string deviceId, string terminalId, CancellationToken ct = default);

    Task<ActionResult<Unit>> CloseAsync(string deviceId, string terminalId, CancellationToken ct = default);
}
