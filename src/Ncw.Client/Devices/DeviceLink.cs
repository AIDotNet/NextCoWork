using System.Collections.Concurrent;
using System.Collections.Immutable;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Core.Layout;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Devices;

/// <summary>
/// 控制端到一台设备的长连接:连接 → 握手 → 设备信息 → 项目列表 → 订阅 device 主题,把结果写进 Store。
/// 断开后设备标为「连接中」(运行中的状态变成未知,不判结束),按退避重连;连续失败后标为离线(仍在重试)。
/// 「断开」包括对端静默消失(NAT 超时、切网、中间设备丢包:没有 FIN 也没有 RST,读一直挂着):<see cref="DeviceClient"/> 的心跳
/// (<see cref="HeartbeatOptions"/>:20 秒 ping,连续 3 个间隔没收到任何帧就判定连接已死)让 <c>Completion</c> 完成,和普通断线走同一条路径。
/// 设备的事实只经这里写入 Store(AGENTS.md §3 规则 1)。
/// </summary>
public sealed class DeviceLink : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly ClientStores _stores;
    private readonly IUiDispatcher _ui;
    private readonly Func<CancellationToken, Task<Stream>> _connect;
    private readonly ClientHello _hello;
    private readonly DeviceKind _kind;
    private readonly string? _displayName;
    private readonly string? _initialName;
    private readonly HeartbeatOptions? _heartbeat;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DeviceConfigSync _config;
    private readonly DeviceServicesSync _services;
    private readonly ProjectToolsSync _tools;
    private readonly DeviceExtensionsSync _extensions;
    private readonly DeviceEnvironmentsSync _environments;
    private readonly object _watchGate = new();
    private readonly HashSet<string> _watchedSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _watchedRuns = new(StringComparer.Ordinal);

    /// <summary>每个被订阅的 Run 收到的最后一个 seq:这条连接的传输层游标,重连后按它续传(镜像里的 <c>RunView.Seq</c> 是同一个数,但 Store 只能在 UI 线程上读)。</summary>
    private readonly ConcurrentDictionary<string, long> _runCursors = new(StringComparer.Ordinal);
    private Task _loop = Task.CompletedTask;
    private DeviceClient? _client;
    private readonly StartupSignals _startup;
    private bool _sessionsTracked;

    /// <param name="placeholderId">连上之前用来显示的设备 id;连上后换成设备报告的真实 id。</param>
    /// <param name="displayName">显示名;为 null 时用设备自己报告的名字(远程设备)。</param>
    public DeviceLink(ClientStores stores, IUiDispatcher ui, string placeholderId, DeviceKind kind, string? displayName,
        Func<CancellationToken, Task<Stream>> connect, ClientHello hello, string? initialName = null, HeartbeatOptions? heartbeat = null)
    {
        _initialName = initialName;
        _heartbeat = heartbeat;
        DeviceId = placeholderId;
        _config = new DeviceConfigSync(stores, ui, () => DeviceId);
        _services = new DeviceServicesSync(stores, ui, () => DeviceId);
        _tools = new ProjectToolsSync(stores, ui, () => DeviceId);
        _extensions = new DeviceExtensionsSync(stores, ui, () => DeviceId);
        _environments = new DeviceEnvironmentsSync(stores, ui, () => DeviceId);
        _stores = stores;
        _ui = ui;
        _startup = new StartupSignals(stores, ui);
        _connect = connect;
        _hello = hello;
        _kind = kind;
        _displayName = displayName;
    }

    /// <summary>设备 id。连上之前是占位 id。</summary>
    public string DeviceId { get; private set; }

    /// <summary>当前是否已连接。</summary>
    public bool IsConnected => _client is not null;

    public DeviceKind Kind => _kind;

    /// <summary>设备在握手时声明的能力(<c>welcome.features</c>);没连上为空。控制端据此决定能调哪些方法(例如本机设备有没有 <c>account</c>)。</summary>
    public IReadOnlyCollection<string> Features => (IReadOnlyCollection<string>?)_client?.Welcome.Features ?? [];

    /// <summary>调用设备上的一个方法(各个 Actions 用)。没连上时抛可重试的 unavailable。</summary>
    /// <param name="idem">command 的幂等键。重发同一个操作(重连后、超时后)传同一个键,设备只执行一次。</param>
    public Task<TResult> CallAsync<TParams, TResult>(MethodDef<TParams, TResult> method, TParams parameters, CancellationToken ct = default, string? idem = null) =>
        (_client ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device is not connected", retryable: true))
            .CallAsync(method, parameters, ct, idem);

    /// <summary>当前连接(没连上为 null)。工作区工具的同步和 Actions 用它订阅 <c>terminal/&lt;id&gt;</c> 之类的主题。</summary>
    internal DeviceClient? CurrentClient => _client;

    /// <summary>在 UI 线程上执行(各个 Actions 把自己的 Store 更新交给调度器:Store 只在 UI 线程上读写)。</summary>
    public void PostToUi(Action action) => _ui.Post(action);

    /// <summary>每次连上并完成首次同步后触发(测试和启动流程用)。</summary>
    public event Action? Synced;

    public void Start()
    {
        SetConnection(DeviceConnection.Connecting);
        _loop = Task.Run(RunAsync);
    }

    /// <summary>在这台设备上添加一个项目。设备推送的项目变化会更新 Store,这里返回新项目的引用方便立即选中。</summary>
    public async Task<ProjectRef> AddProjectAsync(string path, CancellationToken ct = default)
    {
        var client = _client ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device is not connected", retryable: true);
        var project = await client.CallAsync(DeviceMethods.ProjectsAdd, new ProjectsAddParams(path, null), ct).ConfigureAwait(false);
        var reference = new ProjectRef(DeviceId, project.WorkspaceId);
        // 推送可能晚于结果到达:先把这一个项目合进 Store,之后的推送是全量,会覆盖成同样的值。
        var list = await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value, ct).ConfigureAwait(false);
        _ui.Post(() => ApplyProjects(list));
        return reference;
    }

    /// <summary>重试被挡住或失败的数据准备(例如用户退出 2.x 之后)。结果也会经 data 主题推送到 Store。</summary>
    public async Task<DataStatus> RetryDataAsync(CancellationToken ct = default)
    {
        var client = _client ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device is not connected", retryable: true);
        return await client.CallAsync(DeviceMethods.DataRetry, Unit.Value, ct).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        var failures = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var stream = await _connect(_lifetime.Token).ConfigureAwait(false);
                var client = await DeviceClient.ConnectAsync(stream, _hello, _lifetime.Token, _heartbeat).ConfigureAwait(false);
                await using (client.ConfigureAwait(false))
                {
                    if (client.Welcome.Role == "unpaired")
                    {
                        // 设备不再信任本控制端(被吊销):重连也没用,标出来让用户重新配对。
                        _client = null;
                        SetConnection(DeviceConnection.Revoked);
                        return;
                    }

                    // 先让动作可用,再在 Store 里显示「已连接」:否则界面已经显示连上,用户此刻的操作却会报「未连接」。
                    _client = client;
                    await SyncAsync(client).ConfigureAwait(false);
                    failures = 0;
                    Synced?.Invoke();
                    await client.Completion.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (ProtocolException e) when (!e.Retryable && e.Code is ErrorCodes.Unauthorized or ErrorCodes.ClientOutdated)
            {
                // 令牌不对或版本太旧:重试也没用,标为离线并停下。
                _client = null;
                SetConnection(DeviceConnection.Offline);
                break;
            }
            catch (Ncw.Protocol.Transport.RelayException e) when (e.Code == Ncw.Protocol.Transport.RelayErrors.Unauthorized)
            {
                // 局域网没连上,而中继明确说令牌无效:设备已经不再信任本控制端(被吊销),重试没用,标出来让用户重新配对。
                _client = null;
                SetConnection(DeviceConnection.Revoked);
                return;
            }
            catch (Exception)
            {
                // 连不上 / 握手失败 / 同步失败:下面统一退避重连。
            }

            _client = null;
            failures++;
            SetConnection(failures >= 3 ? DeviceConnection.Offline : DeviceConnection.Connecting);
            using var kick = new CancellationTokenSource();
            Volatile.Write(ref _kick, kick);
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, kick.Token);
                await Task.Delay(Backoff[Math.Min(failures - 1, Backoff.Length - 1)], wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested)
            {
                // 用户点了「重试」:不等退避,立刻再连;连续失败从头数,界面回到「连接中」
                failures = 0;
                SetConnection(DeviceConnection.Connecting);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                Volatile.Write(ref _kick, null);
            }
        }
    }

    private CancellationTokenSource? _kick;

    /// <summary>
    /// 不等退避,立刻重连(启动加载界面和设备页的「重试」)。只在退避等待中有效;正在连接 / 已连上时什么也不做。
    /// 连续失败的计数清零,设备从「离线」回到「连接中」。
    /// </summary>
    public void RetryNow()
    {
        try
        {
            Volatile.Read(ref _kick)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 退避刚好结束:本来就要重连
        }
    }

    private async Task SyncAsync(DeviceClient client)
    {
        var info = await client.CallAsync(DeviceMethods.Info, Unit.Value, _lifetime.Token).ConfigureAwait(false);
        var previousId = DeviceId;
        DeviceId = info.DeviceId;

        _ui.Post(() => _stores.Workspace.Update(state =>
        {
            var devices = state.Devices.Remove(previousId);
            var existing = state.Devices.GetValueOrDefault(info.DeviceId);
            return state with
            {
                Devices = devices.SetItem(info.DeviceId, new DeviceInfo(info.DeviceId, _displayName ?? info.Name, _kind, DeviceConnection.Connected, existing?.Index, Role: client.Welcome.Role)),
            };
        }));

        // 先订阅 device 主题:数据晚于连接就绪(接管 2.x 数据)时,设备会在这里推送项目列表。
        await client.SubscribeAsync(DeviceMethods.DeviceTopic,
            (state, _) => _ui.Post(() => ApplyDeviceState(state)),
            evt =>
            {
                switch (evt.Type)
                {
                    case DeviceEvents.ProjectsChanged:
                        var list = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<ProjectsListResult>());
                        _ui.Post(() => ApplyProjects(list));
                        break;
                    case DeviceEvents.ActiveRunsChanged:
                        var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<DeviceTopicState>());
                        _ui.Post(() => ApplyDeviceState(state));
                        break;
                }
            }, _lifetime.Token).ConfigureAwait(false);

        // data 主题:快照和事件同一类型,按到达顺序写进 Store。数据就绪之前项目方法会返回 unavailable,所以先看状态。
        var first = new TaskCompletionSource<DataStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        await client.SubscribeAsync(DeviceMethods.DataTopic,
            (status, _) =>
            {
                _ui.Post(() => ApplyData(status));
                first.TrySetResult(status);
            },
            evt =>
            {
                if (evt.Type == DataEvents.Changed)
                {
                    var status = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<DataStatus>());
                    _ui.Post(() => ApplyData(status));
                }
            }, _lifetime.Token).ConfigureAwait(false);

        if (_kind == DeviceKind.Local)
        {
            // 远程开关和配对码只给本机通道(remote 主题限制为本机 owner)。
            await client.SubscribeAsync(DeviceMethods.RemoteTopic,
                (state, _) => _ui.Post(() => ApplyRemote(state)),
                evt =>
                {
                    if (evt.Type == RemoteEvents.Changed)
                    {
                        var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<RemoteState>());
                        _ui.Post(() => ApplyRemote(state));
                    }
                }, _lifetime.Token).ConfigureAwait(false);
        }

        // 对话链路:挂起的交互(全量权威)、以及断线前正在看的会话和 Run(Run 按游标续传)。设备不支持对话能力(老设备)就跳过。
        if (client.Welcome.Features.Contains(Capabilities.Conversation))
        {
            await SubscribeInteractionsAsync(client).ConfigureAwait(false);
            if (client.Welcome.Features.Contains(Capabilities.Planning))
            {
                // 目标:全量权威(快照与 goal.changed 同一个语义,带单调版本号);老设备没有这个能力就跳过
                await SubscribeGoalsAsync(client).ConfigureAwait(false);
            }

            string[] sessions;
            string[] runs;
            lock (_watchGate)
            {
                sessions = [.. _watchedSessions];
                runs = [.. _watchedRuns];
            }

            foreach (var sessionId in sessions)
            {
                await SubscribeSessionAsync(client, sessionId).ConfigureAwait(false);
            }

            foreach (var runId in runs)
            {
                await SubscribeRunAsync(client, runId).ConfigureAwait(false);
            }
        }

        // 模型目录与供应商配置(能力 catalog / providers;老设备没有就跳过,角色不够的主题也跳过)。
        await _config.AttachAsync(client, _lifetime.Token).ConfigureAwait(false);
        // 设备设置与定时任务(能力 settings / schedules;老设备没有就跳过)。
        await _services.AttachAsync(client, _lifetime.Token).ConfigureAwait(false);

        // 工作区工具(git / 改动审查 / 终端;能力不在的老设备就跳过)。
        await _tools.AttachAsync(client, _lifetime.Token).ConfigureAwait(false);

        // 扩展域(Skills / 命令 / Hooks 的修订号、MCP 服务器状态;能力不在的老设备就跳过)。
        await _extensions.AttachAsync(client, _lifetime.Token).ConfigureAwait(false);

        // 执行环境(SSH 多设备执行的环境清单与连接状态;能力不在的老设备、角色不够的 viewer 就跳过)。
        await _environments.AttachAsync(client, _lifetime.Token).ConfigureAwait(false);

        var initial = await first.Task.WaitAsync(TimeSpan.FromSeconds(10), _lifetime.Token).ConfigureAwait(false);
        _startup.Milestone(StartupMilestone.TopicsSynced);   // 各主题的首帧都到了(启动时间线;不在跟踪时是空操作)
        if (initial.State == DataStates.Ready)
        {
            var projects = await client.CallAsync(DeviceMethods.ProjectsList, Unit.Value, _lifetime.Token).ConfigureAwait(false);
            _ui.Post(() => ApplyProjects(projects));
        }
    }

    private void ApplyRemote(RemoteState state)
    {
        var deviceId = DeviceId;
        _stores.Workspace.Update(s => s.Devices.TryGetValue(deviceId, out var device) && device.Remote != state
            ? s with { Devices = s.Devices.SetItem(deviceId, device with { Remote = state }) }
            : s);
    }

    private void ApplyData(DataStatus status)
    {
        var deviceId = DeviceId;
        _stores.Workspace.Update(s => s.Devices.TryGetValue(deviceId, out var device) && device.Data != status
            ? s with { Devices = s.Devices.SetItem(deviceId, device with { Data = status }) }
            : s);
    }

    /// <summary>设备上的项目列表是全量:这台设备的项目整体替换;新出现的项目追加到项目栏末尾。</summary>
    private void ApplyProjects(ProjectsListResult list)
    {
        var deviceId = DeviceId;
        // 会话列表跟着项目走:每个项目拉一次 sessions.list(设备没有「会话列表」主题,变化靠下面几处触发重拉)
        var refreshes = list.Projects.Select(p => RefreshSessionsAsync(p.WorkspaceId)).ToList();

        var refs = list.Projects.Select(p => new ProjectRef(deviceId, p.WorkspaceId)).ToList();
        _stores.Workspace.Update(state =>
        {
            var others = state.Projects.Where(kv => kv.Key.DeviceId != deviceId);
            var mine = list.Projects.Select(p => KeyValuePair.Create(
                new ProjectRef(deviceId, p.WorkspaceId),
                new ProjectInfo(new ProjectRef(deviceId, p.WorkspaceId), p.Name, p.Path,
                    p.Environment == "ssh" ? EnvironmentKind.Ssh : EnvironmentKind.Local, p.EnvironmentName, null, true, p.EnvironmentId)));
            return state with { Projects = ImmutableDictionary.CreateRange(others.Concat(mine)) };
        });
        _stores.Layout.Update(layout =>
        {
            var rail = RailLayoutOps.Reconcile(layout.Rail, refs);
            return ReferenceEquals(rail, layout.Rail) ? layout : layout with { Rail = rail };
        });
        if (_stores.Selection.State.Project is null && refs.Count > 0)
        {
            _stores.Selection.Update(_ => new SelectionState(refs[0], null));
        }

        _stores.Startup.Reach(StartupMilestone.ProjectsLoaded);   // 项目列表(哪怕是空的)已经写进镜像
        if (!_sessionsTracked)
        {
            // 启动时间线:第一份项目列表写进镜像之后,等这些会话列表都拉回来(RefreshSessionsAsync 在完成前已把结果投递到 UI 线程,
            // 所以这里再投递的里程碑一定排在它们的 Store 更新之后;放在项目列表写入之后,是因为没有会话要拉时延续会立刻执行);失败的不拖住启动(列表保持原样)
            _sessionsTracked = true;
            _ = Task.WhenAll(refreshes).ContinueWith(_ => _startup.Milestone(StartupMilestone.SessionsLoaded), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// device 主题是权威广播:活动 Run 索引整体替换(AGENTS.md §3 规则 5),只收比手里更新的那份(<c>Rev</c> 单调;乱序到达的旧广播丢弃)。
    /// 收下之后拿它给对话镜像里的 Run 对账:丢了结束事件的 Run 在这里收口。
    /// </summary>
    private void ApplyDeviceState(DeviceTopicState state)
    {
        var deviceId = DeviceId;
        var index = new ActiveRunIndex(deviceId,
            [.. state.Runs.Select(r => new ActiveRun(deviceId, r.RunId, r.SessionId, r.WorkspaceId, r.PendingInteractions))])
        {
            Subagents = [.. state.Subagents.Select(s => new ActiveSubagent(deviceId, s.RunId, s.ParentRunId, s.SessionId, s.WorkspaceId, DateTimeOffset.FromUnixTimeMilliseconds(s.StartedAt)))],
            Rev = state.Rev,
        };
        var accepted = false;
        ActiveRunIndex? previous = null;
        _stores.Workspace.Update(s =>
        {
            if (!s.Devices.TryGetValue(deviceId, out var device) || (device.Index is { } current && current.Rev > index.Rev))
            {
                return s;
            }

            accepted = true;
            previous = device.Index;
            return s with { Devices = s.Devices.SetItem(deviceId, device with { Index = index }) };
        });
        if (accepted)
        {
            // 有 Run 开始或结束:会话的标题(自动命名)、更新时间、新会话都可能变了——重拉受影响项目的列表
            foreach (var workspaceId in ChangedWorkspaces(previous, index))
            {
                _ = RefreshSessionsAsync(workspaceId);
            }

            _stores.Conversations.Update(c => ConversationReducer.Reconcile(c, index));
        }
    }

    private static IEnumerable<string> ChangedWorkspaces(ActiveRunIndex? before, ActiveRunIndex after)
    {
        var old = before?.Runs.ToDictionary(r => r.RunId, r => r.WorkspaceId, StringComparer.Ordinal) ?? [];
        var now = after.Runs.ToDictionary(r => r.RunId, r => r.WorkspaceId, StringComparer.Ordinal);
        return old.Where(kv => !now.ContainsKey(kv.Key)).Select(kv => kv.Value)
            .Concat(now.Where(kv => !old.ContainsKey(kv.Key)).Select(kv => kv.Value))
            .Distinct(StringComparer.Ordinal);
    }

    /// <summary>
    /// 重拉一个项目的会话列表(<c>sessions.list</c>,含归档和收藏标记)并整体替换镜像。各个 Actions 在改了会话之后调它;
    /// 没连上就什么也不做(列表保持原样,界面按设备状态显示未知)。
    /// </summary>
    public async Task RefreshSessionsAsync(string workspaceId, CancellationToken ct = default)
    {
        if (_client is not { } client)
        {
            return;
        }

        try
        {
            var result = await client.CallAsync(ConversationMethods.SessionsList, new SessionsListParams(workspaceId), ct).ConfigureAwait(false);
            var project = new ProjectRef(DeviceId, workspaceId);
            _ui.Post(() => _stores.Workspace.Update(s => SessionListReducer.ApplyList(s, project, result.Sessions)));
        }
        catch (ProtocolException)
        {
            // 设备没有对话能力 / 项目已不存在 / 断线:列表保持原样
        }
    }

    /// <summary>在 UI 线程上更新对话镜像(各个 Actions 用:Store 只在 UI 线程上读写)。</summary>
    public void UpdateConversations(Func<ConversationState, ConversationState> change) =>
        _ui.Post(() => _stores.Conversations.Update(change));

    /// <summary>当前已知的 activeRuns 版本(创建 Run 镜像时记下,对账用)。只能在 UI 线程上调。</summary>
    public long KnownRev() => _stores.Workspace.State.Devices.GetValueOrDefault(DeviceId)?.Index?.Rev ?? 0;

    // ───────────────────────── 对话链路的订阅 ─────────────────────────

    /// <summary>
    /// 开始跟踪一个会话(<c>session/&lt;id&gt;</c> 主题):快照进 Store,之后的增量跟着更新;断线重连后自动重新订阅。
    /// 没连上时只记下来,连上后订阅。
    /// </summary>
    public async Task WatchSessionAsync(string sessionId, CancellationToken ct = default)
    {
        lock (_watchGate)
        {
            _watchedSessions.Add(sessionId);
        }

        if (_client is { } client)
        {
            await SubscribeSessionAsync(client, sessionId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 开始跟踪一个 Run(<c>run/&lt;id&gt;</c> 主题,attach)。断线重连后带上收到的最后一个 seq 续传:设备补发之后的事件,游标续不上就给快照;
    /// Run 收口(<c>run_settled</c>)后不再跟踪。
    /// </summary>
    public async Task WatchRunAsync(string runId, CancellationToken ct = default)
    {
        lock (_watchGate)
        {
            _watchedRuns.Add(runId);
        }

        if (_client is { } client)
        {
            await SubscribeRunAsync(client, runId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>不再跟踪一个会话(界面关掉了它)。当前连接上的订阅随连接结束;重连后不再订阅。</summary>
    public void UnwatchSession(string sessionId)
    {
        lock (_watchGate)
        {
            _watchedSessions.Remove(sessionId);
        }
    }

    private Task SubscribeInteractionsAsync(DeviceClient client)
    {
        var deviceId = DeviceId;
        return client.SubscribeAsync(ConversationMethods.InteractionsTopic,
            (state, _) => _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, deviceId, state))),
            evt =>
            {
                if (evt.Type == InteractionEvents.Changed)
                {
                    var state = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<InteractionsDto>());
                    _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, deviceId, state)));
                }
            }, _lifetime.Token);
    }

    private Task SubscribeGoalsAsync(DeviceClient client)
    {
        var deviceId = DeviceId;
        return client.SubscribeAsync(OrchestrationMethods.GoalTopic,
            (state, _) => _ui.Post(() => _stores.Conversations.Update(c => OrchestrationReducer.ApplyGoalsSnapshot(c, deviceId, state))),
            evt =>
            {
                if (evt.Type == GoalEvents.Changed)
                {
                    var change = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<GoalChangeDto>());
                    _ui.Post(() => _stores.Conversations.Update(c => OrchestrationReducer.ApplyGoalChange(c, deviceId, change)));
                }
            }, _lifetime.Token);
    }

    private Task SubscribeSessionAsync(DeviceClient client, string sessionId, CancellationToken ct = default)
    {
        var deviceId = DeviceId;
        var session = new SessionRef(deviceId, sessionId);
        return client.SubscribeAsync(ConversationMethods.SessionTopic(sessionId),
            (state, _) => _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.ApplySession(c, deviceId, state, KnownRev()))),
            evt =>
            {
                switch (evt.Type)
                {
                    case SubagentEvents.Report:
                        var report = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<SubagentReportDto>());
                        _ui.Post(() => _stores.Conversations.Update(c => OrchestrationReducer.ApplySubagentReport(c, deviceId, report)));
                        break;
                    case SubagentEvents.Stopped:
                        var stopped = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<SubagentStoppedDto>());
                        _ui.Post(() => _stores.Conversations.Update(c => OrchestrationReducer.ApplySubagentStopped(c, deviceId, stopped, KnownRev())));
                        break;
                    case SessionEvents.Updated:
                        var meta = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<SessionDto>());
                        _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.ApplySessionUpdated(c, deviceId, meta)));
                        _ui.Post(() => _stores.Workspace.Update(w => SessionListReducer.ApplyMeta(w, deviceId, meta)));
                        break;
                    case SessionEvents.QueueUpdated:
                        var queue = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<QueueDto>());
                        _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.ApplyQueue(c, deviceId, queue)));
                        break;
                    case SessionEvents.MessageCommitted:
                        var message = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<MessageDto>());
                        _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.ApplyMessage(c, session, message, KnownRev())));
                        break;
                    case SessionEvents.HistoryReset:
                        var page = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<SessionPageDto>());
                        _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.ApplyHistoryReset(c, deviceId, page, KnownRev())));
                        break;
                    case SessionEvents.Deleted:
                        UnwatchSession(sessionId);
                        _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.RemoveSession(c, session)));
                        _ui.Post(() => _stores.Workspace.Update(w => SessionListReducer.Remove(w, deviceId, sessionId)));
                        break;
                }
            }, ct == default ? _lifetime.Token : ct);
    }

    private async Task SubscribeRunAsync(DeviceClient client, string runId, CancellationToken ct = default)
    {
        var deviceId = DeviceId;
        var cursor = _runCursors.TryGetValue(runId, out var last) ? last : (long?)null;
        try
        {
            await client.SubscribeAsync(ConversationMethods.RunTopic(runId),
                (snapshot, seq) =>
                {
                    _runCursors[runId] = seq;
                    _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.ApplyRunSnapshot(c, deviceId, snapshot, KnownRev())));
                },
                evt =>
                {
                    var dto = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<RunEventDto>());
                    _runCursors[runId] = evt.Seq;
                    _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.ApplyRunEvent(c, deviceId, runId, evt.Seq, dto, KnownRev())));
                    if (dto is RunSettledEventDto)
                    {
                        _tools.OnRunSettled(client, runId);
                        lock (_watchGate)
                        {
                            _watchedRuns.Remove(runId);
                        }
                    }
                }, ct == default ? _lifetime.Token : ct, cursor).ConfigureAwait(false);
        }
        catch (ProtocolException e) when (e.Code == ErrorCodes.NotFound)
        {
            // 设备上已经没有这个 Run(重启了,或结束后被回收):不再跟踪。镜像里的状态由 activeRuns 广播对账。
            lock (_watchGate)
            {
                _watchedRuns.Remove(runId);
            }
        }
    }

    private void SetConnection(DeviceConnection connection)
    {
        _ui.Post(() => _stores.Workspace.Update(state =>
        {
            var device = state.Devices.GetValueOrDefault(DeviceId)
                         ?? new DeviceInfo(DeviceId, _displayName ?? _initialName ?? DeviceId, _kind, connection, null);
            // 断开后设备的数据状态未知(AGENTS.md §3 规则 7),不沿用旧值。
            var connected = connection == DeviceConnection.Connected;
            var data = connected ? device.Data : null;
            var remote = connected ? device.Remote : null;
            var role = connected ? device.Role : null;
            return device.Connection == connection && device.Data == data && device.Remote == remote && device.Role == role && state.Devices.ContainsKey(DeviceId)
                ? state
                : state with { Devices = state.Devices.SetItem(DeviceId, device with { Connection = connection, Data = data, Remote = remote, Role = role }) };
        }));
        if (connection != DeviceConnection.Connected)
        {
            // 断开后这台设备上运行中的 Run 状态未知(规则 7):既不判结束,也不继续算运行中。
            _ui.Post(() => _stores.Conversations.Update(c => ConversationReducer.MarkDeviceUnknown(c, DeviceId)));
            _config.MarkUnknown();
            _services.MarkUnknown();
            _tools.MarkUnknown();
            _extensions.MarkUnknown();
            _environments.MarkUnknown();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _lifetime.Dispose();
    }

    /// <summary>
    /// 暂停(手机进入后台 / 网络没了,docs/design/11 §2):断开、停止重连,并把这台设备标成断开 —— 状态未知,不判成结束(规则 7)。
    /// 用完即弃(不能再 <see cref="Start"/>);恢复时由 <see cref="RemoteDeviceManager.Resume"/> 新建连接。
    /// </summary>
    public async Task SuspendAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
        _client = null;
        SetConnection(DeviceConnection.Offline);
    }
}
