using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>侧边栏一个展开项目下显示哪些会话:<see cref="Visible"/> 按顺序显示,<see cref="Hidden"/> 条收进「显示更多」。</summary>
public sealed record SidebarSessionList(IReadOnlyList<SessionSummary> Visible, int Hidden);

/// <summary>
/// 侧边栏项目树里会话的排序与截断(docs/design/03 §1「左侧边栏」;纯函数,单测见 Ncw.Client.Tests)。
/// <list type="bullet">
/// <item>已归档的默认不显示;<c>showArchived</c> 时排在最后;</item>
/// <item>有待处理的 → 运行中的 → 收藏的 → 其余,同一档里按最近活动倒序、再按 id 稳定排序;</item>
/// <item>最多显示 <see cref="Limit"/> 条(<c>showAll</c> 时全部),当前打开的会话排在后面也一定显示(顶替最后一条,不让列表变长)。</item>
/// </list>
/// </summary>
public static class SidebarSessions
{
    public const int Limit = 4;

    public static SidebarSessionList Arrange(
        IEnumerable<SessionSummary> sessions,
        Func<SessionSummary, int> pendingOf,
        Func<SessionSummary, bool> runningOf,
        string? selectedId,
        bool showAll,
        bool showArchived)
    {
        var ordered = sessions
            .Where(s => showArchived || !s.Archived)
            .Select(s => (Session: s, Rank: s.Archived ? 4 : pendingOf(s) > 0 ? 0 : runningOf(s) ? 1 : s.Favorited ? 2 : 3))
            .OrderBy(x => x.Rank)
            .ThenByDescending(x => x.Session.UpdatedAt)
            .ThenBy(x => x.Session.Id, StringComparer.Ordinal)
            .Select(x => x.Session)
            .ToList();

        if (showAll || ordered.Count <= Limit)
        {
            return new SidebarSessionList(ordered, 0);
        }

        var visible = ordered.Take(Limit).ToList();
        if (selectedId is not null && visible.All(s => s.Id != selectedId) && ordered.FirstOrDefault(s => s.Id == selectedId) is { } selected)
        {
            visible[^1] = selected;
        }

        return new SidebarSessionList(visible, ordered.Count - visible.Count);
    }
}
