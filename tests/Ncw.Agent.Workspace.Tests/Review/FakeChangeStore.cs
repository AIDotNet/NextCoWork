using System.Security.Cryptography;
using System.Text;
using Ncw.Agent.Workspace.Review;

namespace Ncw.Agent.Workspace.Tests.Review;

/// <summary>内存里的改动集存储(存储本身在 Storage 的 FileChangeTests 里测过;这里只给服务层喂快照行)。</summary>
internal sealed class FakeChangeStore : IFileChangeStore
{
    public const string Run = "run-1";

    private readonly Dictionary<string, ReviewSnapshot> _snapshots = new(StringComparer.Ordinal);
    private ReviewState _state = ReviewState.Applied;
    private long _updatedAt;

    public List<string> Saves { get; } = [];

    public ReviewState State => _state;

    public long UpdatedAt => _updatedAt;

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>摆一条快照行。<paramref name="after"/> 是「Run 结束时磁盘上的内容」(hash 据此算)。</summary>
    public FakeChangeStore Add(string path, ReviewChangeKind kind, string? before, string after, bool oversize = false, bool inWorkspace = true)
    {
        _snapshots[path] = new ReviewSnapshot(
            path, kind, oversize ? null : before, oversize ? null : after, before is null ? null : Hash(before), Hash(after), oversize, inWorkspace);
        return this;
    }

    public void Save(string rootRunId, string sourceRunId, string sessionId, string workspaceId, IReadOnlyList<RecordedChange> changes, long at) =>
        Saves.Add($"{rootRunId}/{sourceRunId}/{sessionId}/{workspaceId}/{changes.Count}");

    public ReviewChangeSet? Get(string runId) => runId != Run || _snapshots.Count == 0
        ? null
        : new ReviewChangeSet(
            Run, "sess", "ws", _state, _snapshots.Count, 0, 0,
            [.. _snapshots.Values.OrderBy(s => s.Path, StringComparer.Ordinal).Select(s => new ReviewFileEntry(s.Path, s.Kind, 0, 0, s.Oversize, s.InWorkspace))], 1, _updatedAt);

    public IReadOnlyList<ReviewChangeSet> ListBySession(string sessionId) => Get(Run) is { } set && sessionId == "sess" ? [set] : [];

    public ReviewFileDiff? GetDiff(string runId, string path) =>
        runId == Run && _snapshots.TryGetValue(path, out var s) ? new ReviewFileDiff(s.Path, s.Kind, s.Before ?? "", s.After ?? "", s.Oversize) : null;

    public IReadOnlyList<ReviewSnapshot> ListSnapshots(string runId) =>
        runId == Run ? [.. _snapshots.Values.OrderBy(s => s.Path, StringComparer.Ordinal)] : [];

    public void SetState(string runId, ReviewState state, long at)
    {
        _state = state;
        _updatedAt = at;
    }
}
