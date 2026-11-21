using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 会话列表相关的用户意图(规则 4:一个意图一个方法)。会话的真源在设备上;列表镜像在 <see cref="WorkspaceState.Sessions"/>,
/// 这里只经协议下命令,然后让连接重拉这个项目的 <c>sessions.list</c>(设备没有「会话列表」主题)。
/// 「打开会话」= 选中它 + 订阅它的主题,所有入口(会话行、标签页、新建)都走 <see cref="Open"/> / <see cref="CreateAsync"/>。
/// </summary>
public sealed class SessionActions(ClientStores stores, DeviceRegistry devices, ConversationActions conversations)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SessionRef, byte> _opened = [];
    private readonly Dictionary<ProjectRef, Task<ActionResult<string>>> _creating = [];

    /// <summary>打开一个会话:选中并开始跟踪(快照进 Store)。已经在跟踪就只是选中(连接会在断线重连后自己恢复订阅,不重复订阅)。</summary>
    public void Open(ProjectRef project, string sessionId)
    {
        stores.Selection.Update(s => s.Project == project && s.SessionId == sessionId ? s : new SelectionState(project, sessionId));
        var session = new SessionRef(project.DeviceId, sessionId);
        if (_opened.TryAdd(session, 0))
        {
            _ = TrackAsync(session);
        }
    }

    private async Task TrackAsync(SessionRef session)
    {
        if (!await conversations.OpenSessionAsync(session).ConfigureAwait(true))
        {
            _opened.TryRemove(session, out _); // 设备没连上:下次点开再试
        }
    }

    /// <summary>关掉最后一个标签页:项目里不再选中任何会话。</summary>
    public void Deselect(ProjectRef project) =>
        stores.Selection.Update(s => s.Project == project && s.SessionId is not null ? s with { SessionId = null } : s);

    /// <summary>「新建会话」:如果项目里已经有一个未使用的空白新会话,直接跳转到它并显示;否则在项目里建一个并打开。</summary>
    public Task<ActionResult<string>> CreateAsync(ProjectRef project, string? model = null, CancellationToken ct = default)
    {
        if (FindReusableNewSession(project) is { } existing)
        {
            Open(project, existing.Id);
            return Task.FromResult(ActionResult<string>.Success(existing.Id));
        }

        if (_creating.TryGetValue(project, out var inflight))
        {
            return inflight;
        }

        var task = CreateCoreAsync(project, model, ct);
        _creating[project] = task;
        return task;
    }

    private async Task<ActionResult<string>> CreateCoreAsync(ProjectRef project, string? model, CancellationToken ct)
    {
        try
        {
            // 注意不用 ConfigureAwait(false):后面要写 Store,Store 只能在 UI 线程上读写
            if (await conversations.CreateSessionAsync(project, model, ct) is not { } session)
            {
                return ActionResult<string>.NotConnected();
            }

            _opened.TryAdd(session, 0); // CreateSessionAsync 已经订阅了它
            stores.Selection.Update(_ => new SelectionState(project, session.SessionId));
            await RefreshAsync(project, ct);
            return ActionResult<string>.Success(session.SessionId);
        }
        finally
        {
            _creating.Remove(project);
        }
    }

    private SessionSummary? FindReusableNewSession(ProjectRef project)
    {
        var workspace = stores.Workspace.State;
        var conv = stores.Conversations.State;
        return workspace.Sessions
            .Where(s => s.Project == project && !s.Archived && s.IsEmpty && !workspace.IsSessionRunning(s))
            .Where(s => IsUnusedInConversationState(conv, new SessionRef(project.DeviceId, s.Id)))
            .OrderByDescending(s => s.UpdatedAt)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool IsUnusedInConversationState(ConversationState conv, SessionRef session)
    {
        if (conv.Sessions.TryGetValue(session, out var view)
            && (!view.Messages.IsEmpty || view.Queue.Items.Length > 0 || view.ActiveRunId is not null))
        {
            return false;
        }

        return !conv.Runs.Values.Any(r => r.Ref.DeviceId == session.DeviceId && r.SessionId == session.SessionId);
    }

    public Task<ActionResult<SessionDto>> RenameAsync(ProjectRef project, string sessionId, string title, CancellationToken ct = default) =>
        Mutate(project, link => link.CallAsync(ConversationMethods.SessionsRename, new SessionsRenameParams(sessionId, title), ct, idem: Idem("rename")), ct);

    public Task<ActionResult<SessionDto>> ArchiveAsync(ProjectRef project, string sessionId, bool archived, CancellationToken ct = default) =>
        Mutate(project, link => link.CallAsync(ConversationMethods.SessionsArchive, new SessionsArchiveParams(sessionId, archived), ct, idem: Idem("archive")), ct);

    public Task<ActionResult<SessionDto>> FavoriteAsync(ProjectRef project, string sessionId, bool favorited, CancellationToken ct = default) =>
        Mutate(project, link => link.CallAsync(ConversationMethods.SessionsFavorite, new SessionsFavoriteParams(sessionId, favorited), ct, idem: Idem("favorite")), ct);

    /// <summary>删除会话(仅 owner)。设备上有 Run 在跑会先中止。删的是当前选中的会话时,选择回到「没有会话」。</summary>
    public async Task<ActionResult<Unit>> DeleteAsync(ProjectRef project, string sessionId, CancellationToken ct = default)
    {
        var result = await Run(project.DeviceId, link => link.CallAsync(ConversationMethods.SessionsDelete, new SessionRefParams(sessionId), ct, idem: Idem("delete")));
        if (result.IsOk)
        {
            stores.Selection.Update(s => s.SessionId == sessionId && s.Project == project ? s with { SessionId = null } : s);
            await RefreshAsync(project, ct);
        }

        return result;
    }

    /// <summary>
    /// 停止一个会话正在跑的 Run(会话列表左滑的「停止」)。Run 取自设备权威广播(<c>activeRuns</c>);没有在跑 / 设备没连上返回 false。
    /// </summary>
    public async Task<bool> StopAsync(ProjectRef project, string sessionId, CancellationToken ct = default)
    {
        var run = stores.Workspace.State.Devices.GetValueOrDefault(project.DeviceId)?.Index?.Runs.FirstOrDefault(r => r.SessionId == sessionId);
        return run is not null && await conversations.AbortAsync(new RunRef(project.DeviceId, run.RunId), ct).ConfigureAwait(true);
    }

    /// <summary>下拉刷新:重拉所有已连接设备上全部项目的会话列表(设备没有「会话列表」主题,新开的会话要靠重拉才看得到)。</summary>
    public async Task RefreshAllAsync(CancellationToken ct = default)
    {
        var projects = stores.Workspace.State.Projects.Keys.Where(p => stores.Workspace.State.Devices.GetValueOrDefault(p.DeviceId)?.Connection == DeviceConnection.Connected).ToList();
        await Task.WhenAll(projects.Select(p => RefreshAsync(p, ct))).ConfigureAwait(true);
    }

    /// <summary>手动重拉一个项目的会话列表。</summary>
    public async Task RefreshAsync(ProjectRef project, CancellationToken ct = default)
    {
        if (devices.Find(project.DeviceId) is { } link)
        {
            await link.RefreshSessionsAsync(project.WorkspaceId, ct).ConfigureAwait(false);
        }
    }

    private async Task<ActionResult<SessionDto>> Mutate(ProjectRef project, Func<DeviceLink, Task<SessionDto>> call, CancellationToken ct)
    {
        var result = await Run(project.DeviceId, call).ConfigureAwait(false);
        if (result.IsOk)
        {
            await RefreshAsync(project, ct).ConfigureAwait(false);
        }

        return result;
    }

    private static string Idem(string what) => $"session.{what}:{Guid.NewGuid():N}";

    private async Task<ActionResult<T>> Run<T>(string deviceId, Func<DeviceLink, Task<T>> call)
    {
        if (devices.Find(deviceId) is not { IsConnected: true } link)
        {
            return ActionResult<T>.NotConnected();
        }

        try
        {
            return ActionResult<T>.Success(await call(link).ConfigureAwait(false));
        }
        catch (ProtocolException e)
        {
            return ActionResult<T>.From(e);
        }
    }
}
