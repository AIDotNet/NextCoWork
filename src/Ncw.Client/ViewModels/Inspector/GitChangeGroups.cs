using System.Collections.Immutable;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.ViewModels;

public enum GitGroupKind
{
    /// <summary>冲突(porcelain 里含 U,或 AA / DD)。</summary>
    Conflicted,

    /// <summary>暂存区。</summary>
    Staged,

    /// <summary>未暂存(已暂存后又改过的文件两个分组都有)。</summary>
    Unstaged,

    Untracked,
}

/// <summary>Git 面板里的一行(某个文件在某个分组里的样子)。</summary>
public sealed record GitFileItem(string Path, GitGroupKind Group, string Letter, GitChange Kind, string? RenamedFrom)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public string Directory => Path.LastIndexOf('/') is > 0 and var i ? Path[..i] : "";

    public bool IsAdded => Kind is GitChange.Added or GitChange.Untracked;

    public bool IsDeleted => Kind == GitChange.Deleted;

    public bool IsModified => Kind is GitChange.Modified or GitChange.Renamed;

    public bool IsConflicted => Group == GitGroupKind.Conflicted;

    public bool IsStaged => Group == GitGroupKind.Staged;

    public bool CanStage => Group is GitGroupKind.Unstaged or GitGroupKind.Untracked;

    public bool CanUnstage => Group == GitGroupKind.Staged;
}

/// <summary>
/// 「暂存 / 未暂存 / 未跟踪 / 冲突」四个分组(纯函数)。分组<b>完全由 Core 的 <see cref="GitStatus"/> 算出的 <c>Staged</c> / <c>Unstaged</c> / <c>Untracked</c> / <c>Conflicted</c> 决定</b>
/// (Agent 和控制端共用同一个派生,AGENTS.md §3 规则 3),这里只做「哪个分组显示哪一位字母」的排列,不再自己判断一个文件算不算已暂存。
/// </summary>
public sealed record GitChangeGroups(ImmutableArray<GitFileItem> Conflicted, ImmutableArray<GitFileItem> Staged, ImmutableArray<GitFileItem> Unstaged, ImmutableArray<GitFileItem> Untracked)
{
    public static readonly GitChangeGroups Empty = new([], [], [], []);

    public static GitChangeGroups Of(IEnumerable<GitFileChange> files)
    {
        var conflicted = ImmutableArray.CreateBuilder<GitFileItem>();
        var staged = ImmutableArray.CreateBuilder<GitFileItem>();
        var unstaged = ImmutableArray.CreateBuilder<GitFileItem>();
        var untracked = ImmutableArray.CreateBuilder<GitFileItem>();
        foreach (var file in files.OrderBy(static f => f.Path, StringComparer.Ordinal))
        {
            if (file.Conflicted)
            {
                conflicted.Add(new GitFileItem(file.Path, GitGroupKind.Conflicted, "!", GitChange.Modified, file.RenamedFrom));
                continue;
            }

            if (file.Untracked)
            {
                untracked.Add(new GitFileItem(file.Path, GitGroupKind.Untracked, "U", GitChange.Untracked, file.RenamedFrom));
                continue;
            }

            if (file.Staged)
            {
                staged.Add(new GitFileItem(file.Path, GitGroupKind.Staged, LetterOf(file.Index), KindOf(file.Index), file.RenamedFrom));
            }

            if (file.Unstaged)
            {
                unstaged.Add(new GitFileItem(file.Path, GitGroupKind.Unstaged, LetterOf(file.Worktree), KindOf(file.Worktree), file.RenamedFrom));
            }
        }

        return new GitChangeGroups(conflicted.ToImmutable(), staged.ToImmutable(), unstaged.ToImmutable(), untracked.ToImmutable());
    }

    public static GitChangeGroups Of(GitOverviewDto? overview) =>
        overview is { Available: true, Files: { } files } ? Of(files.Select(ToChange)) : Empty;

    public static GitFileChange ToChange(GitFileChangeDto f) => new(f.Path, f.Index, f.Worktree, f.Staged, f.Unstaged, f.Untracked, f.Conflicted, f.RenamedFrom);

    /// <summary>文件树上的角标:每个有改动的文件一个类型(暂存区和工作区都有改动时看工作区那一位——用户最后做了什么)。</summary>
    public static IReadOnlyDictionary<string, GitChange> TreeMarkers(GitOverviewDto? overview)
    {
        var markers = new Dictionary<string, GitChange>(StringComparer.Ordinal);
        if (overview is not { Available: true, Files: { } files })
        {
            return markers;
        }

        foreach (var f in files)
        {
            markers[f.Path] = f.Untracked ? GitChange.Untracked
                : f.Conflicted ? GitChange.Modified
                : f.Unstaged ? KindOf(f.Worktree)
                : KindOf(f.Index);
        }

        return markers;
    }

    private static string LetterOf(string xy) => xy is "." or " " or "" ? "M" : xy;

    private static GitChange KindOf(string xy) => xy switch
    {
        "A" => GitChange.Added,
        "D" => GitChange.Deleted,
        "R" or "C" => GitChange.Renamed,
        _ => GitChange.Modified,
    };
}
