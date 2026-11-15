using System.Text;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Tests.Inspector;

internal static class R
{
    public static ActionResult<T> Ok<T>(T value) => new(ActionOutcome.Ok, value);

    public static ActionResult<T> Fail<T>(ActionOutcome outcome, string? key = null) => new(outcome, default, key);
}

/// <summary>内存里的项目文件系统 + 调用记录(惰性加载、分页、忽略表、乐观锁、分块读都能脚本化)。</summary>
internal sealed class FakeFiles : IFilesCommands
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Ignored { get; } = new(StringComparer.Ordinal);

    public List<string> ListCalls { get; } = [];

    public List<bool> HideIgnoredCalls { get; } = [];

    public int PageSize { get; set; } = int.MaxValue;

    /// <summary>设备没连上:列目录得到「不可用」。</summary>
    public bool Offline { get; set; }

    public int Revision { get; set; } = 1;

    public bool KindAsLargeText { get; set; }

    public byte[]? RawBytes { get; set; }

    public string? BinaryReason { get; set; }

    public byte[]? ImageBytes { get; set; }

    public Func<string, Task>? Gate { get; set; }

    public List<(string Path, string Content, string Revision)> Writes { get; } = [];

    public List<string> Mutations { get; } = [];

    public FakeFiles Add(string path, string content = "")
    {
        Files[path] = content;
        for (var i = path.IndexOf('/', StringComparison.Ordinal); i > 0; i = path.IndexOf('/', i + 1))
        {
            Directories.Add(path[..i]);
        }

        return this;
    }

    public string CurrentRevision => $"r{Revision}";

    public async Task<ActionResult<FilesListResult>> ListAsync(string deviceId, string projectId, string path = "", string sort = "name", bool hideIgnored = false, int offset = 0, int limit = 0, CancellationToken ct = default)
    {
        if (Offline)
        {
            return R.Fail<FilesListResult>(ActionOutcome.Unavailable);
        }

        ListCalls.Add(path);
        HideIgnoredCalls.Add(hideIgnored);
        if (Gate is not null)
        {
            await Gate(path);
        }

        var prefix = path.Length == 0 ? "" : path + "/";
        var names = Files.Keys.Concat(Directories)
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.Length > prefix.Length && !p[prefix.Length..].Contains('/'))
            .Distinct()
            .Select(p =>
            {
                var name = p[prefix.Length..];
                return new FileEntryDto(name, p, Directories.Contains(p) ? "dir" : "file", false, name.StartsWith('.'), Ignored.Contains(name), Directories.Contains(p) ? null : Files[p].Length);
            })
            .Where(e => !(hideIgnored && e.Ignored))
            // 设备会自己排序;这里故意乱序返回,验证客户端用 Core 的排序再排一次
            .OrderByDescending(e => e.Name, StringComparer.Ordinal)
            .ToArray();
        var page = names.Skip(offset).Take(PageSize).ToArray();
        return R.Ok(new FilesListResult(path, page, names.Length, offset + page.Length < names.Length));
    }

    public Task<ActionResult<FileContentDto>> ReadAsync(string deviceId, string projectId, string path, CancellationToken ct = default)
    {
        if (ImageBytes is { } image)
        {
            return Task.FromResult(R.Ok(new FileContentDto("image", path, image.Length, CurrentRevision, null, "image/png", image)));
        }

        if (BinaryReason is { } reason)
        {
            return Task.FromResult(R.Ok(new FileContentDto("binary", path, 10, CurrentRevision, Reason: reason)));
        }

        if (KindAsLargeText)
        {
            return Task.FromResult(R.Ok(new FileContentDto("binary", path, RawBytes!.Length, "large:1:1", Reason: "tooLarge")));
        }

        return Files.TryGetValue(path, out var content)
            ? Task.FromResult(R.Ok(new FileContentDto("text", path, content.Length, CurrentRevision, content)))
            : Task.FromResult(R.Fail<FileContentDto>(ActionOutcome.NotFound, FilesErrorKeys.NotFound));
    }

    public List<(long Offset, int Length, string? Revision)> RangeCalls { get; } = [];

    public int FailRangeAfter { get; set; } = int.MaxValue;

    public Task<ActionResult<FilesRangeResult>> ReadRangeAsync(string deviceId, string projectId, string path, long offset, int length, string? revision = null, CancellationToken ct = default)
    {
        RangeCalls.Add((offset, length, revision));
        if (RangeCalls.Count > FailRangeAfter)
        {
            return Task.FromResult(R.Fail<FilesRangeResult>(ActionOutcome.Conflict, FilesErrorKeys.Conflict));
        }

        var bytes = RawBytes ?? Encoding.UTF8.GetBytes(Files[path]);
        var take = (int)Math.Max(0, Math.Min(length, bytes.Length - offset));
        return Task.FromResult(R.Ok(new FilesRangeResult(bytes[(int)offset..(int)(offset + take)], offset, take, bytes.Length, "large:1:1", offset + take >= bytes.Length)));
    }

    public Task<ActionResult<FilesWriteResult>> WriteTextAsync(string deviceId, string projectId, string path, string content, string revision, CancellationToken ct = default)
    {
        Writes.Add((path, content, revision));
        if (revision != CurrentRevision)
        {
            return Task.FromResult(R.Fail<FilesWriteResult>(ActionOutcome.Conflict, FilesErrorKeys.Conflict));
        }

        Files[path] = content;
        Revision++;
        return Task.FromResult(R.Ok(new FilesWriteResult(path, content.Length, CurrentRevision)));
    }

    private Task<ActionResult<FilesMutateResult>> Mutate(string what, string path, Action change)
    {
        Mutations.Add($"{what}:{path}");
        change();
        return Task.FromResult(R.Ok(new FilesMutateResult(path)));
    }

    public Task<ActionResult<FilesMutateResult>> CreateFileAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => Mutate("createFile", path, () => Add(path));

    public Task<ActionResult<FilesMutateResult>> CreateDirectoryAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => Mutate("createDirectory", path, () => Directories.Add(path));

    public Task<ActionResult<FilesMutateResult>> RenameAsync(string deviceId, string projectId, string path, string destination, bool overwrite = false, CancellationToken ct = default) =>
        Mutate($"rename->{destination}", path, () =>
        {
            Files[destination] = Files[path];
            Files.Remove(path);
        });

    public Task<ActionResult<FilesMutateResult>> DeleteAsync(string deviceId, string projectId, string path, CancellationToken ct = default) =>
        Mutate("delete", path, () => Files.Remove(path));

    public string? LastSuggestQuery { get; private set; }

    public Task<ActionResult<FilesSuggestResult>> SuggestAsync(string deviceId, string projectId, string query, int limit = 0, CancellationToken ct = default)
    {
        LastSuggestQuery = query;
        var hits = Files.Keys.Where(p => p.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal).Select(p => new FileSuggestionDto(p, p[(p.LastIndexOf('/') + 1)..])).ToArray();
        return Task.FromResult(R.Ok(new FilesSuggestResult(hits, false)));
    }

    public Task<ActionResult<FilesSearchResult>> SearchAsync(string deviceId, string projectId, FilesSearchParams query, CancellationToken ct = default)
    {
        var matches = Files
            .SelectMany(kv => kv.Value.Split('\n').Select((line, i) => (kv.Key, line, i)).Where(x => x.line.Contains(query.Pattern, StringComparison.OrdinalIgnoreCase)).Select(x => new FileMatchDto(x.Key, x.i + 1, 1, x.line)))
            .ToArray();
        return Task.FromResult(R.Ok(new FilesSearchResult(matches, Files.Count, false)));
    }
}

/// <summary>记录调用的 git:概览写进 Store(和真实的 GitActions 一样),写操作成功后刷新。</summary>
internal sealed class FakeGit(ClientStores stores) : IGitCommands
{
    public GitOverviewDto Overview { get; set; } = new(true, null, "/repo", "main", false, false, "origin/main", 0, 0, []);

    public List<string> Calls { get; } = [];

    public string? CommitFailureKey { get; set; }

    public GitDiffDto Diff { get; set; } = new("a.cs", false, false, false, "");

    private long _seq;

    public Task<ActionResult<GitOverviewDto>> RefreshAsync(ProjectRef project, CancellationToken ct = default)
    {
        Calls.Add("refresh");
        var seq = ++_seq;
        stores.Git.Update(m => GitReducer.Apply(m, project, Overview, seq));
        return Task.FromResult(R.Ok(Overview));
    }

    public Task<ActionResult<GitBranchesResult>> ListBranchesAsync(ProjectRef project, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new GitBranchesResult([new GitBranchDto("main", true, "origin/main"), new GitBranchDto("dev", false, "")])));

    public Task<ActionResult<GitCommitsResult>> ListCommitsAsync(ProjectRef project, int limit = 50, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new GitCommitsResult([new GitCommitDto("abc123", "abc123", "init", "me", 1_700_000_000_000)])));

    public Task<ActionResult<GitDiffDto>> GetDiffAsync(ProjectRef project, string path, bool staged, CancellationToken ct = default)
    {
        Calls.Add($"diff:{path}:{(staged ? "staged" : "worktree")}");
        return Task.FromResult(R.Ok(Diff with { Path = path, Staged = staged }));
    }

    private async Task<ActionResult<T>> Write<T>(ProjectRef project, string call, T value)
    {
        Calls.Add(call);
        await RefreshAsync(project);
        return R.Ok(value);
    }

    public Task<ActionResult<Unit>> StageAsync(ProjectRef project, IReadOnlyList<string> paths, CancellationToken ct = default) => Write(project, $"stage:{string.Join(',', paths)}", Unit.Value);

    public Task<ActionResult<Unit>> UnstageAsync(ProjectRef project, IReadOnlyList<string> paths, CancellationToken ct = default) => Write(project, $"unstage:{string.Join(',', paths)}", Unit.Value);

    public Task<ActionResult<GitCommitDto>> CommitAsync(ProjectRef project, string message, CancellationToken ct = default) =>
        CommitFailureKey is { } key
            ? Task.FromResult(new ActionResult<GitCommitDto>(ActionOutcome.Failed, null, key, null, "fatal: boom"))
            : Write(project, $"commit:{message}", new GitCommitDto("deadbeef00", "deadbeef", message, "me", 1));

    public Task<ActionResult<Unit>> CheckoutAsync(ProjectRef project, string branch, CancellationToken ct = default) => Write(project, $"checkout:{branch}", Unit.Value);

    public Task<ActionResult<GitBranchDto>> CreateBranchAsync(ProjectRef project, string name, bool checkout, CancellationToken ct = default) => Write(project, $"createBranch:{name}", new GitBranchDto(name, checkout, ""));

    public Task<ActionResult<Unit>> PullAsync(ProjectRef project, CancellationToken ct = default) => Write(project, "pull", Unit.Value);

    public Task<ActionResult<Unit>> PushAsync(ProjectRef project, CancellationToken ct = default) => Write(project, "push", Unit.Value);

    public Task<ActionResult<GitGeneratedMessageDto>> GenerateMessageAsync(ProjectRef project, CancellationToken ct = default) =>
        Task.FromResult(R.Ok(new GitGeneratedMessageDto("feat: generated")));
}

internal sealed class FakeReview : IReviewCommands
{
    public ReviewChangeSetDto[] Sets { get; set; } = [];

    public Dictionary<string, ReviewFileDiffDto> Diffs { get; } = [];

    public string[] Conflicts { get; set; } = [];

    public List<string> Calls { get; } = [];

    public ReviewMutationDto? NextMutation { get; set; }

    public Task<ActionResult<ReviewChangeSetDto[]>> ListAsync(string deviceId, string sessionId, CancellationToken ct = default)
    {
        Calls.Add("list");
        return Task.FromResult(R.Ok(Sets));
    }

    public Task<ActionResult<ReviewFileDiffDto?>> FileDiffAsync(string deviceId, string runId, string path, CancellationToken ct = default) =>
        Task.FromResult(R.Ok<ReviewFileDiffDto?>(Diffs.GetValueOrDefault(path)));

    public Task<ActionResult<string[]>> PrecheckUndoAsync(string deviceId, string runId, CancellationToken ct = default)
    {
        Calls.Add("precheck");
        return Task.FromResult(R.Ok(Conflicts));
    }

    public Task<ActionResult<ReviewMutationDto>> UndoAsync(string deviceId, string runId, bool force = false, CancellationToken ct = default)
    {
        Calls.Add($"undo:{force}");
        return Task.FromResult(R.Ok(NextMutation ?? new ReviewMutationDto(runId, ReviewStates.Reverted, [])));
    }

    public Task<ActionResult<ReviewMutationDto>> RedoAsync(string deviceId, string runId, CancellationToken ct = default)
    {
        Calls.Add("redo");
        return Task.FromResult(R.Ok(NextMutation ?? new ReviewMutationDto(runId, ReviewStates.Applied, [])));
    }
}

/// <summary>脚本化的终端后端:写命令可以挂起(测试合并)、记录订阅 / 退订、保存 sink 供测试推输出。</summary>
internal sealed class FakeTerminals : ITerminalCommands
{
    public List<TerminalDto> Existing { get; } = [];

    public TerminalIntentDto? NextIntent { get; set; }

    public List<byte[]> Writes { get; } = [];

    public List<(int Columns, int Rows)> Resizes { get; } = [];

    public List<string> Calls { get; } = [];

    public Queue<TaskCompletionSource<bool>> WriteGates { get; } = new();

    public Dictionary<string, ITerminalSink> Sinks { get; } = [];

    public int Attaches { get; private set; }

    public ClientStores? Stores { get; set; }

    public TerminalDto Make(string id, string status = "running", string env = "local", string kind = "local") =>
        new(id, "p1", new TerminalEnvDto("local", env, env, kind), "proj", "/work/proj", "/bin/zsh", 80, 24, status, 100 + Existing.Count);

    public Task<ActionResult<TerminalDto[]>> ListAsync(string deviceId, string projectId, CancellationToken ct = default)
    {
        Calls.Add("list");
        Stores?.Terminals.Update(m => TerminalReducer.ApplyList(m, deviceId, projectId, Existing));
        return Task.FromResult(R.Ok(Existing.ToArray()));
    }

    public Task<ActionResult<TerminalPrepareResult>> PrepareAsync(string deviceId, string projectId, int columns, int rows, string? terminalId = null, string? cwd = null, CancellationToken ct = default)
    {
        Calls.Add($"prepare:{columns}x{rows}");
        if (NextIntent is { } intent)
        {
            return Task.FromResult(R.Ok(new TerminalPrepareResult(null, intent)));
        }

        var terminal = Make("t-new");
        Existing.Add(terminal);
        Stores?.Terminals.Update(m => TerminalReducer.ApplyInfo(m, deviceId, terminal, 0));
        return Task.FromResult(R.Ok(new TerminalPrepareResult(terminal, null)));
    }

    public Task<ActionResult<TerminalApproveResult>> ApproveAsync(string deviceId, string intentId, bool approved, CancellationToken ct = default)
    {
        Calls.Add($"approve:{approved}");
        return Task.FromResult(R.Ok(new TerminalApproveResult(approved ? "cred-1" : null)));
    }

    public Task<ActionResult<TerminalDto>> CreateAsync(string deviceId, string projectId, int columns, int rows, string? terminalId = null, string? cwd = null, string? approval = null, CancellationToken ct = default)
    {
        Calls.Add($"create:{terminalId}:{approval}");
        var terminal = Make(terminalId ?? "t-created", env: "dev-server", kind: "ssh");
        Existing.Add(terminal);
        Stores?.Terminals.Update(m => TerminalReducer.ApplyInfo(m, deviceId, terminal, 0));
        return Task.FromResult(R.Ok(terminal));
    }

    public Task<ActionResult<Unit>> AttachAsync(string deviceId, string terminalId, ITerminalSink sink, CancellationToken ct = default)
    {
        Calls.Add($"attach:{terminalId}");
        Attaches++;
        Sinks[terminalId] = sink;
        return Task.FromResult(R.Ok(Unit.Value));
    }

    public Task DetachAsync(string deviceId, string terminalId)
    {
        Calls.Add($"detach:{terminalId}");
        Sinks.Remove(terminalId);
        return Task.CompletedTask;
    }

    public async Task<ActionResult<Unit>> WriteAsync(string deviceId, string terminalId, byte[] data, string? idem = null, CancellationToken ct = default)
    {
        Writes.Add(data);
        if (WriteGates.TryDequeue(out var gate) && !await gate.Task)
        {
            return R.Fail<Unit>(ActionOutcome.Unavailable);
        }

        return R.Ok(Unit.Value);
    }

    public Task<ActionResult<Unit>> ResizeAsync(string deviceId, string terminalId, int columns, int rows, CancellationToken ct = default)
    {
        Resizes.Add((columns, rows));
        return Task.FromResult(R.Ok(Unit.Value));
    }

    public Task<ActionResult<Unit>> KillAsync(string deviceId, string terminalId, CancellationToken ct = default)
    {
        Calls.Add($"kill:{terminalId}");
        return Task.FromResult(R.Ok(Unit.Value));
    }

    public Task<ActionResult<Unit>> CloseAsync(string deviceId, string terminalId, CancellationToken ct = default)
    {
        Calls.Add($"close:{terminalId}");
        Stores?.Terminals.Update(m => TerminalReducer.ApplyRemoved(m, deviceId, terminalId));
        return Task.FromResult(R.Ok(Unit.Value));
    }
}

internal sealed class FakeSurface : Ncw.Client.ViewModels.ITerminalSurface
{
    public List<string> Log { get; } = [];

    public (int Columns, int Rows) Grid { get; set; } = (100, 30);

    public event Action<byte[]>? Input;

    public event Action<int, int>? GridChanged;

    public void Reset() => Log.Add("reset");

    public void Write(byte[] data) => Log.Add(Encoding.UTF8.GetString(data));

    public void Type(string text) => Input?.Invoke(Encoding.UTF8.GetBytes(text));

    public void Resize(int columns, int rows)
    {
        Grid = (columns, rows);
        GridChanged?.Invoke(columns, rows);
    }
}
