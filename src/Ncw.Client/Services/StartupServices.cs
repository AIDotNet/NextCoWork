using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Services;

/// <summary>
/// 宿主提供给启动加载界面的少数本机能力(桌面:本机 Agent 守护的重试和诊断)。没有宿主能力(手机、测试)时为 null,界面据此隐藏对应的动作。
/// </summary>
public interface IStartupHost
{
    /// <summary>让本机 Agent 守护立刻再来一次(从「不再自动继续」或退避等待中)。</summary>
    void Retry();

    /// <summary>补充的诊断文字(守护日志的尾部等);没有则 null。只含结构化事实和日志,不含令牌。</summary>
    string? Diagnostics();

    /// <summary>
    /// 让本机 Agent 优雅退出,<b>不管「关窗后继续运行」开没开</b>,之后守护不再重启它(设置 · 数据 · 回退到 2.x:回退在 Agent 关库之后执行)。
    /// 没有本机 Agent 的宿主什么都不做。
    /// </summary>
    Task StopLocalAgentAsync() => Task.CompletedTask;
}

/// <summary>
/// 任何线程都能调的启动事实写入口:取时间戳,再切到 UI 线程写 <see cref="StartupStore"/>(Store 只在 UI 线程上读写)。
/// 本机 Agent 守护(在守护线程上)、连接层(在连接线程上)用它;没有在跟踪启动时全部是空操作。
/// </summary>
public sealed class StartupSignals(ClientStores stores, IUiDispatcher ui)
{
    /// <summary>此刻到达了一个里程碑。</summary>
    public void Milestone(StartupMilestone milestone) => MilestoneAt(milestone, stores.Startup.Clock.GetUtcNow());

    /// <summary>里程碑发生的时间已知(例如进程创建时间)。</summary>
    public void MilestoneAt(StartupMilestone milestone, DateTimeOffset at) => ui.Post(() => stores.Startup.Reach(milestone, at));

    /// <summary>本机 Agent 守护的最新状态。</summary>
    public void Sidecar(StartupSidecar sidecar) => ui.Post(() => stores.Startup.SetSidecar(sidecar));
}

/// <summary>
/// 从 <see cref="WorkspaceState"/> 读「此刻」的连接状态和数据状态,给启动视图用。设备的这两个事实的真源仍然是 Workspace Store(和数据页读的是同一份),这里不存副本。
/// </summary>
public static class StartupLive
{
    /// <summary>桌面:本机设备的连接状态;手机(远程):所有设备里最好的那个(任一已连接 → 已连接;否则任一在连接 → 连接中;……;没有设备 → <see cref="StartupConnection.None"/>)。</summary>
    public static StartupConnection ConnectionOf(WorkspaceState state, DeviceRegistry devices, StartupProfile profile)
    {
        if (profile == StartupProfile.Desktop)
        {
            return devices.Local is { } link && state.Devices.TryGetValue(link.DeviceId, out var device) ? Map(device.Connection) : StartupConnection.NotStarted;
        }

        var all = state.Devices.Values.Where(d => d.Kind != DeviceKind.Local).Select(d => Map(d.Connection)).ToList();
        if (all.Count == 0)
        {
            return StartupConnection.None;
        }

        foreach (var wanted in new[] { StartupConnection.Connected, StartupConnection.Connecting, StartupConnection.Offline })
        {
            if (all.Contains(wanted))
            {
                return wanted;
            }
        }

        return StartupConnection.Revoked;
    }

    /// <summary>桌面:本机设备的数据状态(没连上为 null)。手机不参与数据阶段。</summary>
    public static DataStatus? LocalDataOf(WorkspaceState state, DeviceRegistry devices) =>
        devices.Local is { } link && state.Devices.TryGetValue(link.DeviceId, out var device) ? device.Data : null;

    private static StartupConnection Map(DeviceConnection connection) => connection switch
    {
        DeviceConnection.Connected => StartupConnection.Connected,
        DeviceConnection.Connecting => StartupConnection.Connecting,
        DeviceConnection.Revoked => StartupConnection.Revoked,
        _ => StartupConnection.Offline,   // 离线、休眠:不在连接,也不是「在等」
    };
}

/// <summary>
/// 把 Store 里已经发生的事「锁存」成启动里程碑(第一次到达的时间):设备第一次连上、数据第一次就绪、接管走到了哪一步、账户记录读完。
/// 它是 Workspace / Account Store 的订阅者,不是 ViewModel;锁存之后重放的旧状态改不了它(<see cref="StartupFacts.Reach"/>)。进入可交互状态后自己退订。
/// </summary>
public sealed class StartupRecorder : IDisposable
{
    private readonly ClientStores _stores;
    private readonly DeviceRegistry _devices;
    private bool _disposed;

    public StartupRecorder(ClientStores stores, DeviceRegistry devices)
    {
        _stores = stores;
        _devices = devices;
        stores.Workspace.Changed += Observe;
        stores.Account.Changed += Observe;
        stores.Startup.Changed += Observe;
        Observe();
    }

    private void Observe()
    {
        if (_disposed || _stores.Startup.State.Facts is not { } facts)
        {
            return;
        }

        if (facts.Has(StartupMilestone.Interactive))
        {
            Dispose();
            return;
        }

        if (_stores.Account.State.Mode != AccountMode.Loading)
        {
            _stores.Startup.Reach(StartupMilestone.AccountDecided);
        }

        if (StartupLive.ConnectionOf(_stores.Workspace.State, _devices, facts.Profile) == StartupConnection.Connected)
        {
            _stores.Startup.Reach(StartupMilestone.Connected);
        }

        if (facts.Profile == StartupProfile.Desktop && StartupLive.LocalDataOf(_stores.Workspace.State, _devices) is { } data)
        {
            if (data.State == DataStates.Ready)
            {
                _stores.Startup.Reach(StartupMilestone.DataReady);
            }

            if (data.State == DataStates.TakingOver)
            {
                _stores.Startup.ReachDataStep(StartupProgress.DataStepOrdinal(data.Step));
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stores.Workspace.Changed -= Observe;
        _stores.Account.Changed -= Observe;
        _stores.Startup.Changed -= Observe;
    }
}
