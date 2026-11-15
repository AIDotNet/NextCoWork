using Ncw.Core.Domain;

namespace Ncw.Client.Stores;

/// <summary>
/// 启动事实的镜像(docs/design/03 §10)。<see cref="Facts"/> 为 null = 没有在跟踪启动(单测、模拟数据、宿主没有调 <see cref="StartupStore.Begin"/>):
/// 此时没有任何启动加载界面,行为和以前完全一样。
/// 存在这里的只有<b>别处没有的事实</b>:里程碑锁存(第一次到达的时间)、本机 Agent 守护的状态。设备的连接状态和数据状态的真源仍然是
/// <see cref="WorkspaceState"/>(以及数据页 <c>DataGateViewModel</c>),启动视图每次派生时现读,不在这里存第二份——所以 <see cref="StartupFacts.Connection"/> /
/// <see cref="StartupFacts.Data"/> 在存下来的这一份里不被写入,由 <c>StartupViewModel</c> 在派生时补上。
/// </summary>
public sealed record StartupState(StartupFacts? Facts)
{
    public static readonly StartupState NotTracking = new((StartupFacts?)null);

    public bool IsTracking => Facts is not null;
}

/// <summary>
/// 启动事实 Store。只在 UI 线程上读写(和别的 Store 一样);别的线程用 <see cref="Ncw.Client.Services.StartupSignals"/>。
/// 时钟在这里:里程碑的时间戳从它取,所以测试用手动时钟就能精确断言。
/// </summary>
public sealed class StartupStore(TimeProvider? clock = null) : Store<StartupState>(StartupState.NotTracking)
{
    /// <summary>取时间戳用的时钟(只读;任何线程都可以读)。</summary>
    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public bool IsTracking => State.IsTracking;

    /// <summary>开始跟踪一次启动。已经在跟踪就什么也不做(不重置锁存)。</summary>
    public void Begin(StartupProfile profile, DateTimeOffset? at = null)
    {
        if (IsTracking)
        {
            return;
        }

        var start = at ?? Clock.GetUtcNow();
        Update(_ => new StartupState(StartupFacts.Begin(profile, start)));
    }

    /// <summary>记下一个里程碑(没在跟踪时什么也不做;已经到达过的不改)。</summary>
    public void Reach(StartupMilestone milestone, DateTimeOffset? at = null)
    {
        if (State.Facts is not { } facts)
        {
            return;
        }

        var when = at ?? Clock.GetUtcNow();
        Update(_ => new StartupState(facts.Reach(milestone, when)));
    }

    /// <summary>数据接管的步骤序号(只增不减)。</summary>
    public void ReachDataStep(int ordinal)
    {
        if (State.Facts is { } facts)
        {
            Update(_ => new StartupState(facts.ReachDataStep(ordinal)));
        }
    }

    /// <summary>本机 Agent 守护的最新状态。</summary>
    public void SetSidecar(StartupSidecar? sidecar)
    {
        if (State.Facts is { } facts)
        {
            Update(_ => new StartupState(facts with { Sidecar = sidecar }));
        }
    }
}
