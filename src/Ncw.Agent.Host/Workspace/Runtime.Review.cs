using Ncw.Agent.Storage;
using Ncw.Agent.Tools;
using Ncw.Agent.Workspace.Review;

namespace Ncw.Agent.Host.Workspace;

internal sealed partial class WorkspaceToolsRuntime
{
    /// <summary>改动审查:变更集的读、撤销 / 重做 / 预检(<c>review.*</c>)。</summary>
    public ReviewService Review { get; private set; } = null!;

    partial void InitReview()
    {
        Review = new ReviewService(new StoredFileChanges(new FileChangeRepository(Database)), Clock);
        // Run 收尾时工具层采集到的改动 → 落库(聚合到顶层 Run)。回调里的失败只记诊断,不影响 Run 的生命周期。
        _conversation.RunChangesCollected += OnRunChanges;
    }

    private void OnRunChanges(Conversation.RunChanges collected)
    {
        try
        {
            var changes = collected.Changes.Select(static c => new RecordedChange(
                c.DisplayPath, c.Path.Value, c.Before, c.After, c.Kind == ChangeKind.Created ? ReviewChangeKind.Created : ReviewChangeKind.Modified, c.InWorkspace)).ToList();
            Review.RecordRun(
                collected.RootRunId, collected.Run.RunId.Value, collected.RootSessionId, collected.Run.WorkspaceId,
                Clock.GetUtcNow().ToUnixTimeMilliseconds(), changes);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warn($"Recording the file changes of run {collected.Run.RunId} failed.", e);
        }
    }

    /// <summary>Workspace 的存储接口在 Storage 的 <see cref="FileChangeRepository"/> 上的适配(Workspace 不引用 Storage;两边各有自己的记录类型)。</summary>
    private sealed class StoredFileChanges(FileChangeRepository repository) : IFileChangeStore
    {
        public void Save(string rootRunId, string sourceRunId, string sessionId, string workspaceId, IReadOnlyList<RecordedChange> changes, long at) =>
            repository.Save(rootRunId, sourceRunId, sessionId, workspaceId,
                [.. changes.Select(static c => new FileChangeInput(c.RelPath, c.AbsPath, c.Before, c.After, KindName(c.Kind), c.InWorkspace))], at);

        public ReviewChangeSet? Get(string runId) => repository.Get(runId) is { } set ? Map(set) : null;

        public IReadOnlyList<ReviewChangeSet> ListBySession(string sessionId) => [.. repository.ListBySession(sessionId).Select(Map)];

        public ReviewFileDiff? GetDiff(string runId, string path) =>
            repository.GetDiff(runId, path) is { } d ? new ReviewFileDiff(d.Path, Kind(d.ChangeKind), d.Before, d.After, d.Oversize) : null;

        public IReadOnlyList<ReviewSnapshot> ListSnapshots(string runId) =>
            [.. repository.ListSnapshots(runId).Select(static s => new ReviewSnapshot(s.Path, Kind(s.ChangeKind), s.Before, s.After, s.BeforeHash, s.AfterHash, s.Oversize, s.InWorkspace))];

        public void SetState(string runId, ReviewState state, long at) =>
            repository.SetState(runId, state == ReviewState.Reverted ? "reverted" : "applied", at);

        private static ReviewChangeSet Map(FileChangeSetRecord s) => new(
            s.RunId, s.SessionId, s.WorkspaceId, s.State == "reverted" ? ReviewState.Reverted : ReviewState.Applied, s.FileCount, s.Additions, s.Deletions,
            [.. s.Files.Select(static f => new ReviewFileEntry(f.Path, Kind(f.ChangeKind), f.Additions, f.Deletions, f.Oversize, f.InWorkspace))], s.CreatedAt, s.UpdatedAt);

        private static ReviewChangeKind Kind(string name) => name switch
        {
            "created" => ReviewChangeKind.Created,
            "deleted" => ReviewChangeKind.Deleted,
            _ => ReviewChangeKind.Modified,
        };

        private static string KindName(ReviewChangeKind kind) => kind switch
        {
            ReviewChangeKind.Created => "created",
            ReviewChangeKind.Deleted => "deleted",
            _ => "modified",
        };
    }
}
