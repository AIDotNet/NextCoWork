using System.Text;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Mock;

/// <summary>
/// 模拟数据模式(<c>NCW_MOCK=1</c>)和截图测试用的检查器后端:文件、Git 都是内存里的(源头是 <see cref="WorkspaceState.Files"/> 里的平铺条目),
/// 审查和终端没有设备可连(照常给「未连接」)。只用于开发和测试,发布版不调用。
/// </summary>
public static class MockInspectorServices
{
    public static InspectorServices Create(ClientStores stores)
    {
        var devices = new DeviceRegistry();
        return new InspectorServices(new MockFiles(stores), new MockGit(stores), new ReviewActions(devices, stores), new TerminalActions(devices, stores), new SubagentActions(devices));
    }
}

/// <summary>内存里的项目文件(目录 + 文件内容)。可以在测试里 <see cref="SetContent"/> 塞指定内容。</summary>
public sealed class MockFiles(ClientStores stores) : IFilesCommands
{
    private readonly Dictionary<ProjectRef, Dictionary<string, (bool IsDir, string Content, int Rev)>> _projects = [];

    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal) { ".git", "node_modules", "bin", "obj" };

    private Dictionary<string, (bool IsDir, string Content, int Rev)> Of(ProjectRef project)
    {
        if (_projects.TryGetValue(project, out var existing))
        {
            return existing;
        }

        var map = new Dictionary<string, (bool, string, int)>(StringComparer.Ordinal);
        if (stores.Workspace.State.Files.TryGetValue(project, out var entries))
        {
            foreach (var entry in entries)
            {
                var parts = entry.RelativePath.Split('/');
                for (var i = 1; i < parts.Length; i++)
                {
                    map.TryAdd(string.Join('/', parts[..i]), (true, "", 0));
                }

                map[entry.RelativePath] = (entry.IsDirectory, entry.IsDirectory ? "" : Synthesize(entry.RelativePath), 1);
            }
        }

        return _projects[project] = map;
    }

    private static string Synthesize(string path) =>
        string.Join('\n', Enumerable.Range(1, 24).Select(i => i == 1 ? $"// {path}" : $"// line {i}")) + "\n";

    public void SetContent(ProjectRef project, string path, string content)
    {
        var map = Of(project);
        var parts = path.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            map.TryAdd(string.Join('/', parts[..i]), (true, "", 0));
        }

        map[path] = (false, content, map.TryGetValue(path, out var old) ? old.Rev + 1 : 1);
    }

    private static ProjectRef Ref(string deviceId, string projectId) => new(deviceId, projectId);

    private static string Revision(int rev, string content) => $"r{rev}:{content.Length}";

    public Task<ActionResult<FilesListResult>> ListAsync(string deviceId, string projectId, string path = "", string sort = "name", bool hideIgnored = false, int offset = 0, int limit = 0, CancellationToken ct = default)
    {
        var map = Of(Ref(deviceId, projectId));
        var prefix = path.Length == 0 ? "" : path + "/";
        var children = map
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.Length > prefix.Length && !kv.Key[prefix.Length..].Contains('/'))
            .Select(kv =>
            {
                var name = kv.Key[prefix.Length..];
                return new FileEntryDto(name, kv.Key, kv.Value.IsDir ? "dir" : "file", false, name.StartsWith('.'), Ignored.Contains(name), kv.Value.IsDir ? null : kv.Value.Content.Length);
            })
            .Where(e => !(hideIgnored && e.Ignored))
            .ToArray();
        return Task.FromResult(ActionResult<FilesListResult>.Success(new FilesListResult(path, children, children.Length, false)));
    }

    public Task<ActionResult<FileContentDto>> ReadAsync(string deviceId, string projectId, string path, CancellationToken ct = default)
    {
        if (!Of(Ref(deviceId, projectId)).TryGetValue(path, out var file) || file.IsDir)
        {
            return Task.FromResult(new ActionResult<FileContentDto>(ActionOutcome.NotFound, null, FilesErrorKeys.NotFound));
        }

        return Task.FromResult(ActionResult<FileContentDto>.Success(new FileContentDto("text", path, file.Content.Length, Revision(file.Rev, file.Content), file.Content)));
    }

    public Task<ActionResult<FilesRangeResult>> ReadRangeAsync(string deviceId, string projectId, string path, long offset, int length, string? revision = null, CancellationToken ct = default)
    {
        if (!Of(Ref(deviceId, projectId)).TryGetValue(path, out var file))
        {
            return Task.FromResult(new ActionResult<FilesRangeResult>(ActionOutcome.NotFound, null, FilesErrorKeys.NotFound));
        }

        var bytes = Encoding.UTF8.GetBytes(file.Content);
        var take = (int)Math.Max(0, Math.Min(length, bytes.Length - offset));
        return Task.FromResult(ActionResult<FilesRangeResult>.Success(new FilesRangeResult(bytes[(int)offset..(int)(offset + take)], offset, take, bytes.Length, "range", offset + take >= bytes.Length)));
    }

    public Task<ActionResult<FilesWriteResult>> WriteTextAsync(string deviceId, string projectId, string path, string content, string revision, CancellationToken ct = default)
    {
        var map = Of(Ref(deviceId, projectId));
        if (!map.TryGetValue(path, out var file))
        {
            return Task.FromResult(new ActionResult<FilesWriteResult>(ActionOutcome.NotFound, null, FilesErrorKeys.NotFound));
        }

        if (revision != Revision(file.Rev, file.Content))
        {
            return Task.FromResult(new ActionResult<FilesWriteResult>(ActionOutcome.Conflict, null, FilesErrorKeys.Conflict));
        }

        map[path] = (false, content, file.Rev + 1);
        return Task.FromResult(ActionResult<FilesWriteResult>.Success(new FilesWriteResult(path, content.Length, Revision(file.Rev + 1, content))));
    }

    public Task<ActionResult<FilesMutateResult>> CreateFileAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => Mutate(deviceId, projectId, path, m => m.TryAdd(path, (false, "", 1)));

    public Task<ActionResult<FilesMutateResult>> CreateDirectoryAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => Mutate(deviceId, projectId, path, m => m.TryAdd(path, (true, "", 0)));

    public Task<ActionResult<FilesMutateResult>> RenameAsync(string deviceId, string projectId, string path, string destination, bool overwrite = false, CancellationToken ct = default) =>
        Mutate(deviceId, projectId, path, m =>
        {
            if (!m.Remove(path, out var file) || m.ContainsKey(destination))
            {
                return false;
            }

            m[destination] = file;
            return true;
        });

    public Task<ActionResult<FilesMutateResult>> DeleteAsync(string deviceId, string projectId, string path, CancellationToken ct = default) =>
        Mutate(deviceId, projectId, path, m =>
        {
            var removed = m.Keys.Where(k => k == path || k.StartsWith(path + "/", StringComparison.Ordinal)).ToList();
            removed.ForEach(k => m.Remove(k));
            return removed.Count > 0;
        });

    private Task<ActionResult<FilesMutateResult>> Mutate(string deviceId, string projectId, string path, Func<Dictionary<string, (bool IsDir, string Content, int Rev)>, bool> change) =>
        Task.FromResult(change(Of(Ref(deviceId, projectId)))
            ? ActionResult<FilesMutateResult>.Success(new FilesMutateResult(path))
            : new ActionResult<FilesMutateResult>(ActionOutcome.Conflict, null, FilesErrorKeys.Exists));

    public Task<ActionResult<FilesSuggestResult>> SuggestAsync(string deviceId, string projectId, string query, int limit = 0, CancellationToken ct = default)
    {
        var files = Of(Ref(deviceId, projectId)).Where(kv => !kv.Value.IsDir).Select(kv => kv.Key).ToList();
        var ranked = FuzzyPath.Rank(files, query, limit > 0 ? limit : 20, static p => p, static p => p[(p.LastIndexOf('/') + 1)..]);
        return Task.FromResult(ActionResult<FilesSuggestResult>.Success(new FilesSuggestResult([.. ranked.Select(p => new FileSuggestionDto(p, p[(p.LastIndexOf('/') + 1)..]))], false)));
    }

    public Task<ActionResult<FilesSearchResult>> SearchAsync(string deviceId, string projectId, FilesSearchParams query, CancellationToken ct = default)
    {
        var matches = new List<FileMatchDto>();
        foreach (var (path, file) in Of(Ref(deviceId, projectId)).Where(kv => !kv.Value.IsDir).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var lines = file.Content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var at = lines[i].IndexOf(query.Pattern, StringComparison.OrdinalIgnoreCase);
                if (at >= 0)
                {
                    matches.Add(new FileMatchDto(path, i + 1, at + 1, lines[i]));
                }
            }
        }

        return Task.FromResult(ActionResult<FilesSearchResult>.Success(new FilesSearchResult([.. matches], 1, false)));
    }
}

/// <summary>内存里的仓库:变更来自 <see cref="WorkspaceState.Files"/> 条目上的 <see cref="GitChange"/>;暂存 / 取消暂存 / 提交改的是自己的集合并刷新 <see cref="ClientStores.Git"/>。</summary>
public sealed class MockGit(ClientStores stores) : IGitCommands
{
    private readonly Dictionary<ProjectRef, HashSet<string>> _staged = [];
    private long _seq;

    private HashSet<string> StagedOf(ProjectRef project) => _staged.TryGetValue(project, out var set) ? set : _staged[project] = [];

    private GitOverviewDto Overview(ProjectRef project)
    {
        var info = stores.Workspace.State.Projects.GetValueOrDefault(project);
        var staged = StagedOf(project);
        var files = stores.Workspace.State.Files.TryGetValue(project, out var entries)
            ? entries.Where(e => e.Change is not null).Select(e =>
            {
                if (e.Change == GitChange.Untracked)
                {
                    return staged.Contains(e.RelativePath)
                        ? GitStatus.Change(e.RelativePath, "A", ".", untracked: false)
                        : GitStatus.Change(e.RelativePath, "?", "?", untracked: true);
                }

                var letter = e.Change switch { GitChange.Added => "A", GitChange.Deleted => "D", GitChange.Renamed => "R", _ => "M" };
                return staged.Contains(e.RelativePath)
                    ? GitStatus.Change(e.RelativePath, letter, ".", untracked: false)
                    : GitStatus.Change(e.RelativePath, e.Change == GitChange.Added ? "A" : ".", e.Change == GitChange.Added ? "." : letter, untracked: false);
            }).Select(f => new GitFileChangeDto(f.Path, f.Index, f.Worktree, f.Staged, f.Unstaged, f.Untracked, f.Conflicted, f.RenamedFrom)).ToArray()
            : [];
        return new GitOverviewDto(true, null, info?.Path ?? "", info?.Branch ?? "main", false, false, "origin/" + (info?.Branch ?? "main"), 1, 0, files);
    }

    public Task<ActionResult<GitOverviewDto>> RefreshAsync(ProjectRef project, CancellationToken ct = default)
    {
        var overview = Overview(project);
        var seq = Interlocked.Increment(ref _seq);
        stores.Git.Update(m => GitReducer.Apply(m, project, overview, seq));
        return Task.FromResult(ActionResult<GitOverviewDto>.Success(overview));
    }

    public Task<ActionResult<GitBranchesResult>> ListBranchesAsync(ProjectRef project, CancellationToken ct = default)
    {
        var current = stores.Workspace.State.Projects.GetValueOrDefault(project)?.Branch ?? "main";
        return Task.FromResult(ActionResult<GitBranchesResult>.Success(new GitBranchesResult([new GitBranchDto(current, true, "origin/" + current), new GitBranchDto("main", false, "origin/main"), new GitBranchDto("feature/inspector", false, "")])));
    }

    public Task<ActionResult<GitCommitsResult>> ListCommitsAsync(ProjectRef project, int limit = 50, CancellationToken ct = default) =>
        Task.FromResult(ActionResult<GitCommitsResult>.Success(new GitCommitsResult(
        [
            new GitCommitDto("c354c385aaaaaaaa", "c354c385", "feat(desktop): 添加局域网设备与「连接与远程」设置", "token", DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeMilliseconds()),
            new GitCommitDto("49e3cd60bbbbbbbb", "49e3cd60", "feat(protocol): 局域网远程连接", "token", DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds()),
        ])));

    public Task<ActionResult<GitDiffDto>> GetDiffAsync(ProjectRef project, string path, bool staged, CancellationToken ct = default) =>
        Task.FromResult(ActionResult<GitDiffDto>.Success(new GitDiffDto(path, staged, false, false,
            $"diff --git a/{path} b/{path}\nindex 111..222 100644\n--- a/{path}\n+++ b/{path}\n@@ -1,5 +1,6 @@\n using System;\n-using System.Linq;\n+using System.Collections.Generic;\n+using System.Linq;\n \n namespace Demo;\n \n")));

    public Task<ActionResult<Unit>> StageAsync(ProjectRef project, IReadOnlyList<string> paths, CancellationToken ct = default) => Edit(project, set => set.UnionWith(paths));

    public Task<ActionResult<Unit>> UnstageAsync(ProjectRef project, IReadOnlyList<string> paths, CancellationToken ct = default) => Edit(project, set => set.ExceptWith(paths));

    private async Task<ActionResult<Unit>> Edit(ProjectRef project, Action<HashSet<string>> change)
    {
        change(StagedOf(project));
        await RefreshAsync(project).ConfigureAwait(false);
        return ActionResult<Unit>.Success(Unit.Value);
    }

    public async Task<ActionResult<GitCommitDto>> CommitAsync(ProjectRef project, string message, CancellationToken ct = default)
    {
        StagedOf(project).Clear();
        await RefreshAsync(project, ct).ConfigureAwait(false);
        return ActionResult<GitCommitDto>.Success(new GitCommitDto("d00dfeed00000000", "d00dfeed", message, "token", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public Task<ActionResult<Unit>> CheckoutAsync(ProjectRef project, string branch, CancellationToken ct = default) => Task.FromResult(ActionResult<Unit>.Success(Unit.Value));

    public Task<ActionResult<GitBranchDto>> CreateBranchAsync(ProjectRef project, string name, bool checkout, CancellationToken ct = default) => Task.FromResult(ActionResult<GitBranchDto>.Success(new GitBranchDto(name, checkout, "")));

    public Task<ActionResult<Unit>> PullAsync(ProjectRef project, CancellationToken ct = default) => Task.FromResult(ActionResult<Unit>.Success(Unit.Value));

    public Task<ActionResult<Unit>> PushAsync(ProjectRef project, CancellationToken ct = default) => Task.FromResult(ActionResult<Unit>.Success(Unit.Value));

    public Task<ActionResult<GitGeneratedMessageDto>> GenerateMessageAsync(ProjectRef project, CancellationToken ct = default) =>
        Task.FromResult(ActionResult<GitGeneratedMessageDto>.Success(new GitGeneratedMessageDto("feat(ui): 接入检查器的文件、Git 与终端页签")));
}
