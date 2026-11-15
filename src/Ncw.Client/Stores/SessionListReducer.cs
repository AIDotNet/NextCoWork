using System.Collections.Immutable;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 把设备上的会话列表(<c>sessions.list</c>)折进 <see cref="WorkspaceState.Sessions"/> 的纯函数。列表是某个项目的全量,整体替换;
/// 会话元数据变更(<c>session.updated</c>)按 id 合并,只收不更旧的(规则 6)。
/// </summary>
public static class SessionListReducer
{
    /// <summary>这个项目的会话整体替换成设备给的全量列表;其他项目不动。</summary>
    public static WorkspaceState ApplyList(WorkspaceState state, ProjectRef project, IEnumerable<SessionListItemDto> items)
    {
        var mine = items
            .Select(i => new SessionSummary(i.Id, project, i.Title, DateTimeOffset.FromUnixTimeMilliseconds(i.UpdatedAt), i.Archived, i.Favorited))
            .ToImmutableArray();
        var others = state.Sessions.Where(s => s.Project != project);
        var next = ImmutableArray.CreateRange(others.Concat(mine));
        return next.SequenceEqual(state.Sessions) ? state : state with { Sessions = next };
    }

    /// <summary>一个会话的元数据变了(重命名、归档、收藏、有新消息)。列表里没有它就不动:新会话由下一次全量列表带进来。</summary>
    public static WorkspaceState ApplyMeta(WorkspaceState state, string deviceId, SessionDto meta)
    {
        var index = IndexOf(state, deviceId, meta.Id);
        if (index < 0)
        {
            return state;
        }

        var existing = state.Sessions[index];
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(meta.UpdatedAt);
        if (existing.UpdatedAt > updatedAt)
        {
            return state;
        }

        var next = existing with { Title = meta.Title, UpdatedAt = updatedAt, Archived = meta.Archived, Favorited = meta.Favorited };
        return next == existing ? state : state with { Sessions = state.Sessions.SetItem(index, next) };
    }

    /// <summary>会话被删除。</summary>
    public static WorkspaceState Remove(WorkspaceState state, string deviceId, string sessionId)
    {
        var index = IndexOf(state, deviceId, sessionId);
        return index < 0 ? state : state with { Sessions = state.Sessions.RemoveAt(index) };
    }

    private static int IndexOf(WorkspaceState state, string deviceId, string sessionId)
    {
        for (var i = 0; i < state.Sessions.Length; i++)
        {
            if (state.Sessions[i].Id == sessionId && state.Sessions[i].Project.DeviceId == deviceId)
            {
                return i;
            }
        }

        return -1;
    }
}
