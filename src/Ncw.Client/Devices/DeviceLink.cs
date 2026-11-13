using System.Collections.Immutable;
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
    private readonly CancellationTokenSource _lifetime = new();
    private Task _loop = Task.CompletedTask;
    private DeviceClient? _client;

    /// <param name="placeholderId">连上之前用来显示的设备 id;连上后换成设备报告的真实 id。</param>
    /// <param name="displayName">显示名;为 null 时用设备自己报告的名字(远程设备)。</param>
    public DeviceLink(ClientStores stores, IUiDispatcher ui, string placeholderId, DeviceKind kind, string? displayName,
        Func<CancellationToken, Task<Stream>> connect, ClientHello hello, string? initialName = null)
    {
        _initialName = initialName;
        _stores = stores;
        _ui = ui;
        _connect = connect;
        _hello = hello;
        _kind = kind;
        _displayName = displayName;
        DeviceId = placeholderId;
    }

    /// <summary>设备 id。连上之前是占位 id。</summary>
    public string DeviceId { get; private set; }

    /// <summary>当前是否已连接。</summary>
    public bool IsConnected => _client is not null;

    public DeviceKind Kind => _kind;

    /// <summary>调用设备上的一个方法(各个 Actions 用)。没连上时抛可重试的 unavailable。</summary>
    public Task<TResult> CallAsync<TParams, TResult>(MethodDef<TParams, TResult> method, TParams parameters, CancellationToken ct = default) =>
        (_client ?? throw new ProtocolException(ErrorCodes.Unavailable, "Device is not connected", retryable: true))
            .CallAsync(method, parameters, ct);

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
                var client = await DeviceClient.ConnectAsync(stream, _hello, _lifetime.Token).ConfigureAwait(false);
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
            catch (Exception)
            {
                // 连不上 / 握手失败 / 同步失败:下面统一退避重连。
            }

            _client = null;
            failures++;
            SetConnection(failures >= 3 ? DeviceConnection.Offline : DeviceConnection.Connecting);
            try
            {
                await Task.Delay(Backoff[Math.Min(failures - 1, Backoff.Length - 1)], _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
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
                Devices = devices.SetItem(info.DeviceId, new DeviceInfo(info.DeviceId, _displayName ?? info.Name, _kind, DeviceConnection.Connected, existing?.Index)),
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

        var initial = await first.Task.WaitAsync(TimeSpan.FromSeconds(10), _lifetime.Token).ConfigureAwait(false);
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
        var refs = list.Projects.Select(p => new ProjectRef(deviceId, p.WorkspaceId)).ToList();
        _stores.Workspace.Update(state =>
        {
            var others = state.Projects.Where(kv => kv.Key.DeviceId != deviceId);
            var mine = list.Projects.Select(p => KeyValuePair.Create(
                new ProjectRef(deviceId, p.WorkspaceId),
                new ProjectInfo(new ProjectRef(deviceId, p.WorkspaceId), p.Name, p.Path,
                    p.Environment == "ssh" ? EnvironmentKind.Ssh : EnvironmentKind.Local, p.EnvironmentName, null, true)));
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
    }

    /// <summary>device 主题是权威广播:活动 Run 索引整体替换(AGENTS.md §3 规则 5)。</summary>
    private void ApplyDeviceState(DeviceTopicState state)
    {
        var deviceId = DeviceId;
        var index = new ActiveRunIndex(deviceId,
            [.. state.Runs.Select(r => new ActiveRun(deviceId, r.RunId, r.SessionId, r.WorkspaceId, r.PendingInteractions))]);
        _stores.Workspace.Update(s => s.Devices.TryGetValue(deviceId, out var device)
            ? s with { Devices = s.Devices.SetItem(deviceId, device with { Index = index }) }
            : s);
    }

    private void SetConnection(DeviceConnection connection) =>
        _ui.Post(() => _stores.Workspace.Update(state =>
        {
            var device = state.Devices.GetValueOrDefault(DeviceId)
                         ?? new DeviceInfo(DeviceId, _displayName ?? _initialName ?? DeviceId, _kind, connection, null);
            // 断开后设备的数据状态未知(AGENTS.md §3 规则 7),不沿用旧值。
            var connected = connection == DeviceConnection.Connected;
            var data = connected ? device.Data : null;
            var remote = connected ? device.Remote : null;
            return device.Connection == connection && device.Data == data && device.Remote == remote && state.Devices.ContainsKey(DeviceId)
                ? state
                : state with { Devices = state.Devices.SetItem(DeviceId, device with { Connection = connection, Data = data, Remote = remote }) };
        }));

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
}
