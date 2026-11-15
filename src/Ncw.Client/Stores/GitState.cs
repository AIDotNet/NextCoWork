using System.Collections.Immutable;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 一个项目的 git 概览镜像(<c>git.overview</c>)。设备上的仓库是真源(AGENTS.md §3 规则 1,key 带 deviceId):这里只有「最近一次取到的样子」。
/// <see cref="Known"/> = false:设备断开了(或还没取到),<see cref="Overview"/> 是断开前的旧值,界面不把它当现状(规则 7)。
/// <see cref="Summary"/> 由 Core 的 <see cref="GitStatusSummary"/> 唯一生成函数算出(Agent 和控制端共用),界面的分组标题和角标读它,不自己数。
/// </summary>
/// <param name="Seq">取到这份概览的请求序号(见 <see cref="GitReducer.Apply"/>:乱序到达的旧结果丢弃)。</param>
public sealed record ProjectGit(bool Known, GitOverviewDto? Overview, GitStatusSummary Summary, long Seq)
{
    public static readonly ProjectGit Unknown = new(false, null, GitStatusSummary.Empty, 0);

    /// <summary>这个项目有 git 可管(已取到、可用)。</summary>
    public bool Available => Known && Overview is { Available: true };
}

public sealed record GitMirror(ImmutableDictionary<ProjectRef, ProjectGit> Projects)
{
    public static readonly GitMirror Empty = new(ImmutableDictionary<ProjectRef, ProjectGit>.Empty);

    public ProjectGit Of(ProjectRef project) => Projects.GetValueOrDefault(project) ?? ProjectGit.Unknown;
}

/// <summary>git 镜像的纯函数归约(Store 只在 UI 线程上更新)。</summary>
public static class GitReducer
{
    /// <summary>
    /// 收下一份概览。<paramref name="seq"/> 是发起这次读取时领的单调序号:两次刷新的结果可能乱序到达(先发的后到),
    /// 比手里更旧的丢弃,所以界面永远不会被一份过期的状态盖掉(例如「暂存」之后的刷新被它之前发出的刷新覆盖,文件看起来又回到了未暂存)。
    /// </summary>
    public static GitMirror Apply(GitMirror mirror, ProjectRef project, GitOverviewDto overview, long seq)
    {
        if (mirror.Projects.TryGetValue(project, out var current) && current.Seq > seq)
        {
            return mirror;
        }

        var summary = overview.Available && overview.Files is { } files
            ? GitStatusSummary.Of(files.Select(static f => new GitFileChange(f.Path, f.Index, f.Worktree, f.Staged, f.Unstaged, f.Untracked, f.Conflicted, f.RenamedFrom)))
            : GitStatusSummary.Empty;
        return mirror with { Projects = mirror.Projects.SetItem(project, new ProjectGit(true, overview, summary, seq)) };
    }

    /// <summary>设备断开:这台设备上所有项目的概览标为未知(规则 7),不判成「干净」,也不当现状。</summary>
    public static GitMirror MarkDeviceUnknown(GitMirror mirror, string deviceId)
    {
        var next = mirror;
        foreach (var (project, value) in mirror.Projects)
        {
            if (project.DeviceId == deviceId && value.Known)
            {
                next = next with { Projects = next.Projects.SetItem(project, value with { Known = false }) };
            }
        }

        return next;
    }

    /// <summary>项目被移除(或界面不再关心它):丢掉它的镜像。</summary>
    public static GitMirror Remove(GitMirror mirror, ProjectRef project) =>
        mirror.Projects.ContainsKey(project) ? mirror with { Projects = mirror.Projects.Remove(project) } : mirror;
}
