using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 项目面板「会话」分区的过滤与排序(纯函数,单测见 Ncw.Client.Tests)。
/// 规则:归档的默认不显示(打开「显示已归档」才出现,排在最后);收藏的置顶;同一档里按最近活动(更新时间)倒序,时间相同按 id 稳定排序;
/// 搜索按标题忽略大小写子串匹配,不分档(搜索时收藏和归档照常过滤,不改变排序规则)。
/// </summary>
public static class SessionListQuery
{
    public static IReadOnlyList<SessionSummary> Apply(IEnumerable<SessionSummary> sessions, string? search, bool showArchived)
    {
        var filter = search?.Trim() ?? "";
        return [.. sessions
            .Where(s => showArchived || !s.Archived)
            .Where(s => filter.Length == 0 || s.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Archived)
            .ThenByDescending(s => s.Favorited)
            .ThenByDescending(s => s.UpdatedAt)
            .ThenBy(s => s.Id, StringComparer.Ordinal)];
    }
}
