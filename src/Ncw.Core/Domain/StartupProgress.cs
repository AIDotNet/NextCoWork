using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Ncw.Core.Domain;

// 启动与加载的唯一派生入口(docs/design/03 §10,AGENTS.md §3 规则 3)。
// 桌面启动加载界面、手机壳的简化加载界面、诊断文本共用这里的纯函数;时钟由调用方传入,所以每一条规则都能精确测试。
// 规则 6(状态单调):每个阶段「完成」是一个只增不减的锁存(里程碑时间戳,第一次到达时记下,重放的旧事件覆盖不了它),进度只由锁存算出,所以不会倒退;
// 规则 7:设备断开不会把已完成的阶段退回「正在启动」(只会让当前一行显示「正在重新连接」),而且一旦应用进入过可交互状态,本文件的结果永远是 <see cref="StartupPhase.Done"/> ——
// 运行中的断线重连只用已有的断线提示,不再弹全屏加载。

/// <summary>启动的形态。</summary>
public enum StartupProfile
{
    /// <summary>桌面:拉起并连接本机 Agent(sidecar),可能接管 2.x 数据。阶段:应用 → Agent → 连接 → 数据 → 内容。</summary>
    Desktop,

    /// <summary>手机壳 / 只连远程设备:没有本机 Agent。阶段:应用 → 连接 → 内容;设备连不上不是失败(外壳照常进入,设备页显示离线)。</summary>
    Remote,
}

/// <summary>清单里可见的启动阶段,顺序固定。</summary>
public enum StartupStage
{
    /// <summary>窗口、平台服务(偏好 / 主题 / 动效)、单实例与协议链接、账户记录读取。</summary>
    App,

    /// <summary>本机 Agent(sidecar):进程拉起 → 本机通道握手成功才算就绪。</summary>
    Agent,

    /// <summary>设备连接:连接 → 握手 → 设备信息。</summary>
    Connect,

    /// <summary>设备数据就绪:全新 / 已接管的很快,首次接管 2.x 数据时有备份 / 升级 / 提交 / 凭据这些真实步骤。</summary>
    Data,

    /// <summary>首批数据:各主题的首帧快照、项目列表、各项目的会话列表。</summary>
    Content,
}

/// <summary>
/// 时间线上的里程碑(每个只记「第一次到达」的时间,单调)。<see cref="StartupStage"/> 的完成条件由它们组合而成;
/// <see cref="ProcessStarted"/> / <see cref="MainEntered"/> / <see cref="FrameworkReady"/> / <see cref="ShellCreated"/> / <see cref="AgentSpawned"/> 只用于时间线(校准阈值、诊断),不是任何阶段的完成条件。
/// </summary>
public enum StartupMilestone
{
    /// <summary>进程启动(操作系统记的进程创建时间)。</summary>
    ProcessStarted,

    /// <summary>进入 <c>Main</c>(运行时初始化完成;只用于时间线:ProcessStarted → MainEntered 是运行时自己的启动耗时)。</summary>
    MainEntered,

    /// <summary>单实例锁拿到(此后才创建窗口)。</summary>
    InstanceReady,

    /// <summary>Avalonia / Arc 初始化完成,进入 <c>NcwApp.OnFrameworkInitializationCompleted</c>(只用于时间线:InstanceReady → FrameworkReady 是框架自己的启动耗时)。</summary>
    FrameworkReady,

    /// <summary>主窗口和外壳的 ViewModel / 视图树创建完毕、窗口还没显示(只用于时间线:FrameworkReady → ShellCreated 是创建外壳的耗时,ShellCreated → WindowShown 是显示与首次布局)。</summary>
    ShellCreated,

    /// <summary>主窗口已打开。</summary>
    WindowShown,

    /// <summary>平台服务就绪:偏好读完、动效级别应用到主题。</summary>
    PlatformReady,

    /// <summary>账户记录读完(账户状态不再是「读取中」;没有账户服务的宿主在开始时直接记下)。</summary>
    AccountDecided,

    /// <summary>本机 Agent 进程已拉起(接上上次留下的 Agent 时不记)。</summary>
    AgentSpawned,

    /// <summary>本机 Agent 就绪:本机通道 + 令牌握手成功。</summary>
    AgentReady,

    /// <summary>设备连接完成(握手 + 设备信息)。</summary>
    Connected,

    /// <summary>设备数据就绪(data 主题:ready)。</summary>
    DataReady,

    /// <summary>设备各主题的首帧快照都收到了(连接层首次同步完成)。</summary>
    TopicsSynced,

    /// <summary>项目列表已写进镜像(哪怕是空列表)。</summary>
    ProjectsLoaded,

    /// <summary>各项目的会话列表都拉回来了(哪怕某个失败:列表保持原样,不卡住启动)。</summary>
    SessionsLoaded,

    /// <summary>加载界面下面的外壳在拿到数据后完成了一次完整布局。</summary>
    ShellLaidOut,

    /// <summary>加载界面已淡出并释放,应用可交互。之后本文件的结果永远是 <see cref="StartupPhase.Done"/>。</summary>
    Interactive,
}

/// <summary>本机 Agent 守护的状态(Desktop 的 <c>SidecarStatus</c> 的结构化投影;Core 不引用 Desktop)。</summary>
public enum StartupSidecarPhase
{
    Starting,
    Running,

    /// <summary>意外退出,退避后重启(不是失败:还在自动恢复)。</summary>
    Restarting,

    /// <summary>不会自动继续了,等用户处理或重试。</summary>
    Failed,

    Stopped,
}

/// <param name="Issue">问题的稳定 key(kebab-case,和 <c>SidecarIssue</c> 一一对应:<c>agent-not-found</c>、<c>launch-failed</c>、<c>already-running-elsewhere</c>、<c>agent-outdated</c>、<c>client-outdated</c>、<c>crash-loop</c>)。</param>
/// <param name="Detail">给诊断的原始信息(退出码、异常消息);不是界面文字。</param>
public sealed record StartupSidecar(StartupSidecarPhase Phase, string? Issue = null, int RestartCount = 0, TimeSpan? RetryIn = null, string? Detail = null);

/// <summary>设备在控制端看来的连接状态(启动视角)。</summary>
public enum StartupConnection
{
    /// <summary>连接层还没建起来。</summary>
    NotStarted,

    Connecting,
    Connected,

    /// <summary>连续连不上(连接层退避三次后标记;仍在后台重试)。</summary>
    Offline,

    /// <summary>设备不再信任本控制端。</summary>
    Revoked,

    /// <summary>没有需要连接的设备(手机壳还没配对任何设备)。</summary>
    None,
}

/// <summary>数据页给出的数据状态(启动视角):由 <c>DataGateViewModel</c> 的模式映射而来,启动加载界面和数据页读的是同一个事实。</summary>
public enum StartupDataPhase
{
    /// <summary>没连上,数据状态未知。</summary>
    Unknown,

    Preparing,
    TakingOver,

    /// <summary>需要用户处理:被挡住(2.x 还在运行)、失败、刚接管完等「继续」。归数据页管,启动加载界面让位。</summary>
    NeedsUser,

    Ready,
}

/// <param name="Step">接管进行中的步骤(<c>backing-up</c> / <c>migrating</c> / <c>committing</c> / <c>credentials</c>)。</param>
public sealed record StartupData(StartupDataPhase Phase, string? Step = null);

/// <summary>
/// 启动事实:已经发生的事。里程碑是只增不减的锁存;<see cref="Sidecar"/> / <see cref="Connection"/> / <see cref="Data"/> 是「此刻」的状态(失败判断和当前一行的文字用),
/// 不参与进度计算(进度只看锁存)。事实的写入点只有一个 Store(<c>ClientStores.Startup</c>),这里只有纯数据和纯合并。
/// </summary>
public sealed record StartupFacts(
    StartupProfile Profile,
    DateTimeOffset StartedAt,
    ImmutableDictionary<StartupMilestone, DateTimeOffset> Reached,
    int DataStepReached,
    StartupSidecar? Sidecar,
    StartupConnection Connection,
    StartupData Data)
{
    public static StartupFacts Begin(StartupProfile profile, DateTimeOffset startedAt) =>
        new(profile, startedAt, ImmutableDictionary<StartupMilestone, DateTimeOffset>.Empty, 0, null, StartupConnection.NotStarted, new StartupData(StartupDataPhase.Unknown));

    public bool Has(StartupMilestone milestone) => Reached.ContainsKey(milestone);

    /// <summary>记下里程碑。已经到达过的不改(第一次的时间是事实,重放的旧事件覆盖不了它)。</summary>
    public StartupFacts Reach(StartupMilestone milestone, DateTimeOffset at) =>
        Reached.ContainsKey(milestone) ? this : this with { Reached = Reached.Add(milestone, at) };

    /// <summary>数据接管的步骤序号(1..4)只增不减:接管失败回退后重试从头再来,进度条也不退。</summary>
    public StartupFacts ReachDataStep(int ordinal) => ordinal > DataStepReached ? this with { DataStepReached = ordinal } : this;
}

/// <summary>清单里一行的状态。</summary>
public enum StartupStepState
{
    Pending,
    Active,
    Done,
}

public readonly record struct StartupStep(StartupStage Stage, StartupStepState State);

/// <summary>当前这一行下面补充的说明(界面按它取文字;数据接管的步骤另见 <see cref="StartupView.DataStep"/>)。</summary>
public enum StartupDetail
{
    None,
    AppStarting,
    AgentLaunching,
    AgentHandshake,
    AgentRestarting,
    Connecting,

    /// <summary>已经连上过、此刻断了(启动期间 Agent 重启):不退回「正在启动」,只换这一行的说明。</summary>
    Reconnecting,

    DataPreparing,
    DataTakeover,
    LoadingProjects,
    LoadingSessions,

    /// <summary>数据都到齐了,在等下面的外壳完成首次布局。</summary>
    Settling,
}

/// <summary>慢启动时给的温和提示的种类(不是错误,界面用平静的文字)。</summary>
public enum StartupSlowHint
{
    None,
    Generic,
    Agent,
    Data,
    Content,
}

/// <summary>启动界面此刻所处的阶段。</summary>
public enum StartupPhase
{
    /// <summary>在等。</summary>
    Loading,

    /// <summary>出了问题,不会自己继续(或卡住太久):显示错误面板。</summary>
    Failed,

    /// <summary>数据需要用户处理(被挡住 / 失败 / 接管完等「继续」):加载界面让位给数据页。</summary>
    NeedsUser,

    /// <summary>所有阶段都完成了,等下面的外壳完成首次布局。</summary>
    Settling,

    /// <summary>外壳已布局好,可以淡出了(是否马上淡出还要过 <see cref="StartupPresentation"/> 的最短停留)。</summary>
    Revealing,

    /// <summary>已经进入过可交互状态。永远停在这里。</summary>
    Done,
}

/// <summary>谁拥有整窗覆盖层。同一时刻只有一个全屏层:启动加载 / 数据页不会叠在一起。</summary>
public enum StartupOwner
{
    None,
    Startup,
    DataGate,
}

/// <summary>失败的原因(稳定 key)和可用的动作。</summary>
/// <param name="Reason">
/// 稳定 key(界面按它取文字):<c>agent-not-found</c>、<c>launch-failed</c>、<c>already-running-elsewhere</c>、<c>agent-outdated</c>、<c>client-outdated</c>、<c>crash-loop</c>、
/// <c>connection-failed</c>(Agent 在运行但连不上)、<c>timeout</c>(某一阶段卡住超过阈值)。
/// </param>
public sealed record StartupFailure(string Reason, bool CanRetry, string? Detail = null)
{
    /// <summary>「查看诊断」「复制诊断信息」「退出」总是可用(没有路可走的情形也要能把信息带走、能退出)。</summary>
    public bool CanShowDiagnostics => true;

    public bool CanQuit => true;
}

/// <summary>确定进度:有真实的分子分母时才有。</summary>
/// <param name="Fraction">0..1,只由锁存算出,单调不降。</param>
/// <param name="StepsDone">已完成的阶段数。</param>
/// <param name="StepsTotal">阶段总数(随形态:桌面 5、手机 3)。</param>
/// <param name="SubStep">当前阶段内有真实步骤时的序号(数据接管:1..4);否则 0。</param>
/// <param name="SubTotal">当前阶段内的步骤数;没有则 0。</param>
public sealed record StartupProgressValue(double Fraction, int StepsDone, int StepsTotal, int SubStep, int SubTotal)
{
    /// <summary>当前阶段有真实的步骤进度(数据接管)。否则是不确定的(只有阶段数)。</summary>
    public bool IsDeterminateInStage => SubTotal > 0 && SubStep > 0;
}

/// <summary>派生出来的启动视图。</summary>
/// <param name="Current">当前阶段(全部完成时是最后一个阶段)。</param>
/// <param name="DataStep">当前在接管 2.x 数据的哪一步(<c>backing-up</c> 等);不在接管时 null。</param>
/// <param name="IsReconnecting">已经连上过、此刻断了。</param>
/// <param name="NextTimeAt">不依赖任何新事实、仅因时间流逝输出会变的下一个时刻(慢启动、阶段超时);没有则 null。调用方在那一刻再算一次。</param>
public sealed record StartupView(
    StartupPhase Phase,
    StartupOwner Owner,
    StartupStage Current,
    ImmutableArray<StartupStep> Steps,
    StartupDetail Detail,
    string? DataStep,
    StartupProgressValue Progress,
    bool IsSlow,
    StartupSlowHint SlowHint,
    StartupFailure? Failure,
    bool IsReconnecting,
    DateTimeOffset? NextTimeAt)
{
    /// <summary>所有阶段都完成(不含外壳布局)。</summary>
    public bool AllStepsDone => Steps.All(s => s.State == StartupStepState.Done);
}

/// <summary>时间阈值。取值依据是真实启动时间线(docs/design/03 §10.7),不是拍脑袋。</summary>
public sealed record StartupTimings
{
    /// <summary>加载界面的内容(标识、清单)在第一帧之后多久才出现。更快就绪时内容根本不出现,底色直接淡出,不闪一下。</summary>
    public TimeSpan ShowDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>内容一旦出现,至少停留这么久才淡出,避免一闪而过。</summary>
    public TimeSpan MinDwell { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>从进程启动算起,超过它给一行温和的提示。</summary>
    public TimeSpan SlowAfter { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>「连接」「内容」阶段卡住超过它算失败(可重试);Agent 阶段由守护自己的就绪超时和退避管,数据接管本来就可能很久,都不套用。</summary>
    public TimeSpan StageTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public static readonly StartupTimings Default = new();
}

/// <summary>启动阶段与进度的唯一派生函数。</summary>
public static class StartupProgress
{
    /// <summary>数据接管的步骤总数:备份、升级数据库、提交、转换凭据(最后一步只有 2.x 库里有旧凭据时才会出现)。和 <c>Ncw.Protocol.Methods.DataSteps</c> 一一对应(Client 的测试核对)。</summary>
    public const int DataStepCount = 4;

    /// <summary>接管步骤序号:<c>backing-up</c>=1、<c>migrating</c>=2、<c>committing</c>=3、<c>credentials</c>=4;不认识的返回 0。</summary>
    public static int DataStepOrdinal(string? step) => step switch
    {
        "backing-up" => 1,
        "migrating" => 2,
        "committing" => 3,
        "credentials" => 4,
        _ => 0,
    };

    /// <summary>某个形态的阶段(按显示顺序)。</summary>
    public static ImmutableArray<StartupStage> StagesOf(StartupProfile profile) => profile == StartupProfile.Desktop
        ? [StartupStage.App, StartupStage.Agent, StartupStage.Connect, StartupStage.Data, StartupStage.Content]
        : [StartupStage.App, StartupStage.Connect, StartupStage.Content];

    /// <summary>一个阶段的完成条件(锁存:到过就一直算完成)。</summary>
    public static bool IsDone(StartupFacts f, StartupStage stage)
    {
        var settledWithoutDevice = f.Profile == StartupProfile.Remote && !f.Has(StartupMilestone.Connected)
            && f.Connection is StartupConnection.Offline or StartupConnection.Revoked or StartupConnection.None;
        return stage switch
        {
            StartupStage.App => f.Has(StartupMilestone.WindowShown) && f.Has(StartupMilestone.PlatformReady)
                                && f.Has(StartupMilestone.InstanceReady) && f.Has(StartupMilestone.AccountDecided),
            StartupStage.Agent => f.Has(StartupMilestone.AgentReady),
            StartupStage.Connect => f.Has(StartupMilestone.Connected) || settledWithoutDevice,
            StartupStage.Data => f.Has(StartupMilestone.DataReady),
            _ => (f.Has(StartupMilestone.TopicsSynced) && f.Has(StartupMilestone.ProjectsLoaded) && f.Has(StartupMilestone.SessionsLoaded)) || settledWithoutDevice,
        };
    }

    /// <summary>用户感知的起点:进程启动时间(记了就用它),否则开始记录的时间。慢启动、总耗时都从这里算。</summary>
    public static DateTimeOffset OriginOf(StartupFacts f) => f.Reached.TryGetValue(StartupMilestone.ProcessStarted, out var at) ? at : f.StartedAt;

    /// <summary>一个阶段是从什么时候开始的:上一个阶段完成的时刻(没有则起点)。</summary>
    public static DateTimeOffset StageStartedAt(StartupFacts f, StartupStage stage)
    {
        var stages = StagesOf(f.Profile);
        var index = stages.IndexOf(stage);
        var at = OriginOf(f);
        for (var i = 0; i < index; i++)
        {
            at = Max(at, CompletedAt(f, stages[i]));
        }

        return at;
    }

    private static DateTimeOffset CompletedAt(StartupFacts f, StartupStage stage)
    {
        StartupMilestone[] needs = stage switch
        {
            StartupStage.App => [StartupMilestone.WindowShown, StartupMilestone.PlatformReady, StartupMilestone.InstanceReady, StartupMilestone.AccountDecided],
            StartupStage.Agent => [StartupMilestone.AgentReady],
            StartupStage.Connect => [StartupMilestone.Connected],
            StartupStage.Data => [StartupMilestone.DataReady],
            _ => [StartupMilestone.TopicsSynced, StartupMilestone.ProjectsLoaded, StartupMilestone.SessionsLoaded],
        };
        var at = DateTimeOffset.MinValue;
        foreach (var m in needs)
        {
            if (f.Reached.TryGetValue(m, out var t))
            {
                at = Max(at, t);
            }
        }

        return at;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    public static StartupView Derive(StartupFacts f, DateTimeOffset now, StartupTimings? timings = null)
    {
        timings ??= StartupTimings.Default;
        var stages = StagesOf(f.Profile);
        var done = stages.Select(s => IsDone(f, s)).ToArray();
        var firstOpen = Array.IndexOf(done, false);
        var allDone = firstOpen < 0;
        var current = allDone ? stages[^1] : stages[firstOpen];
        var steps = ImmutableArray.CreateRange(stages.Select((s, i) =>
            new StartupStep(s, done[i] ? StartupStepState.Done : i == firstOpen ? StartupStepState.Active : StartupStepState.Pending)));
        var stepsDone = done.Count(d => d);

        // 进度:完成的阶段数 + 当前阶段里真实的步骤(只有数据接管有)。锁存算出,所以单调。
        var sub = 0;
        var subTotal = 0;
        double subFraction = 0;
        if (!allDone && current == StartupStage.Data && f.DataStepReached > 0)
        {
            sub = f.DataStepReached;
            subTotal = DataStepCount;
            subFraction = Math.Min(DataStepCount - 1, sub - 1) / (double)DataStepCount;   // 进行中的那一步不算已完成
        }

        var progress = new StartupProgressValue(Math.Min(1, (stepsDone + subFraction) / stages.Length), stepsDone, stages.Length, sub, subTotal);

        if (f.Has(StartupMilestone.Interactive))
        {
            return new StartupView(StartupPhase.Done, StartupOwner.None, stages[^1], steps, StartupDetail.None, null,
                new StartupProgressValue(1, stages.Length, stages.Length, 0, 0), false, StartupSlowHint.None, null, false, null);
        }

        var reconnecting = f.Has(StartupMilestone.Connected) && f.Connection is StartupConnection.Connecting or StartupConnection.Offline;
        var failure = FailureOf(f, current, allDone, now, timings);
        var needsUser = f.Profile == StartupProfile.Desktop && f.Data.Phase == StartupDataPhase.NeedsUser;
        var phase = failure is not null ? StartupPhase.Failed
            : needsUser ? StartupPhase.NeedsUser
            : !allDone ? StartupPhase.Loading
            : f.Has(StartupMilestone.ShellLaidOut) ? StartupPhase.Revealing
            : StartupPhase.Settling;
        var owner = phase == StartupPhase.NeedsUser ? StartupOwner.DataGate : StartupOwner.Startup;

        var origin = OriginOf(f);
        var waiting = phase is StartupPhase.Loading or StartupPhase.Settling;
        var slow = waiting && now - origin >= timings.SlowAfter;
        var hint = !slow ? StartupSlowHint.None : current switch
        {
            StartupStage.Agent => StartupSlowHint.Agent,
            StartupStage.Data when f.Data.Phase == StartupDataPhase.TakingOver => StartupSlowHint.Data,
            StartupStage.Content => StartupSlowHint.Content,
            _ => StartupSlowHint.Generic,
        };

        string? dataStep = !allDone && current == StartupStage.Data && f.Data.Phase == StartupDataPhase.TakingOver ? f.Data.Step : null;
        var detail = DetailOf(f, current, allDone, reconnecting);

        // 下一个「仅因时间流逝输出会变」的时刻:慢启动阈值、阶段超时
        DateTimeOffset? next = null;
        if (waiting)
        {
            if (!slow)
            {
                next = origin + timings.SlowAfter;
            }

            if (!allDone && current is StartupStage.Connect or StartupStage.Content && f.Profile == StartupProfile.Desktop)
            {
                var at = StageStartedAt(f, current) + timings.StageTimeout;
                if (at > now && (next is null || at < next))
                {
                    next = at;
                }
            }
        }

        return new StartupView(phase, owner, current, steps, detail, dataStep, progress, slow, hint, failure, reconnecting, next);
    }

    private static StartupDetail DetailOf(StartupFacts f, StartupStage current, bool allDone, bool reconnecting)
    {
        if (allDone)
        {
            return f.Has(StartupMilestone.ShellLaidOut) ? StartupDetail.None : StartupDetail.Settling;
        }

        // 已经连上过又断了:不退回「正在启动」,这一行说明「正在重新连接」
        if (reconnecting && current is StartupStage.Data or StartupStage.Content)
        {
            return StartupDetail.Reconnecting;
        }

        return current switch
        {
            StartupStage.App => StartupDetail.AppStarting,
            StartupStage.Agent => f.Sidecar?.Phase == StartupSidecarPhase.Restarting ? StartupDetail.AgentRestarting
                : f.Has(StartupMilestone.AgentSpawned) ? StartupDetail.AgentHandshake : StartupDetail.AgentLaunching,
            StartupStage.Connect => StartupDetail.Connecting,
            StartupStage.Data => f.Data.Phase == StartupDataPhase.TakingOver ? StartupDetail.DataTakeover : StartupDetail.DataPreparing,
            _ => f.Has(StartupMilestone.ProjectsLoaded) ? StartupDetail.LoadingSessions : StartupDetail.LoadingProjects,
        };
    }

    private static StartupFailure? FailureOf(StartupFacts f, StartupStage current, bool allDone, DateTimeOffset now, StartupTimings timings)
    {
        // 手机壳:设备连不上不是启动失败,外壳照常进入(设备页显示离线)
        if (f.Profile != StartupProfile.Desktop)
        {
            return null;
        }

        if (f.Sidecar is { Phase: StartupSidecarPhase.Failed } failed)
        {
            var reason = failed.Issue ?? "crash-loop";
            return new StartupFailure(reason, CanRetry: reason is not ("client-outdated" or "agent-outdated"), failed.Detail);
        }

        // 守护在退避重启,但这类问题不先有人处理(退出占着数据目录的进程 / 放开权限或安全软件)就不会好:不让用户对着转圈干等——
        // 守护每隔一会儿还会再试,一旦成功这条失败就自己消失(失败是派生的,不是锁存的)
        if (f.Sidecar is { Phase: StartupSidecarPhase.Restarting, Issue: "already-running-elsewhere" or "launch-failed" } blocked)
        {
            return new StartupFailure(blocked.Issue!, CanRetry: true, blocked.Detail);
        }

        // Agent 一次都没就绪过,却已经连续退出两次:再等下去只是在重复同一个结局(守护要等到第 8 次连续崩溃才放弃,中间一百多秒)。
        // 同样不是锁存:哪一次启动成功了,失败就消失
        if (f.Sidecar is { Phase: StartupSidecarPhase.Restarting, Issue: null, RestartCount: >= 2 } crashing && !f.Has(StartupMilestone.AgentReady))
        {
            return new StartupFailure("crash-loop", CanRetry: true, crashing.Detail);
        }

        // Agent 在运行,但本机通道连不上(连接层退避三次后标记离线):给出口,而不是无限转圈
        if (f.Connection == StartupConnection.Offline && !f.Has(StartupMilestone.Connected))
        {
            return new StartupFailure("connection-failed", CanRetry: true);
        }

        if (!allDone && current is StartupStage.Connect or StartupStage.Content
            && now - StageStartedAt(f, current) >= timings.StageTimeout)
        {
            return new StartupFailure("timeout", CanRetry: true, current.ToString());
        }

        return null;
    }

    /// <summary>
    /// 每个里程碑相对起点的毫秒数(没到达的不在里)。校准阈值和诊断用。
    /// </summary>
    public static ImmutableArray<(StartupMilestone Milestone, double Ms)> Timeline(StartupFacts f)
    {
        var origin = OriginOf(f);
        return [.. f.Reached.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key).Select(kv => (kv.Key, (kv.Value - origin).TotalMilliseconds))];
    }

    /// <summary>诊断文本(给「复制诊断信息」和「查看诊断」):阶段、里程碑时间线、守护状态。只含结构化事实,不含令牌、密钥;面向开发者,不是界面文字。</summary>
    public static string Diagnostics(StartupFacts f, StartupView view, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.Append("NextCoWork startup diagnostics\n");
        sb.Append(inv, $"profile: {f.Profile}\nphase: {view.Phase}\nstage: {view.Current}\nelapsed: {(now - OriginOf(f)).TotalMilliseconds:F0} ms\n");
        sb.Append(inv, $"steps: {string.Join(", ", view.Steps.Select(s => $"{s.Stage}={s.State}"))}\n");
        if (view.Failure is { } fail)
        {
            sb.Append(inv, $"failure: {fail.Reason}{(fail.Detail is null ? "" : $" ({fail.Detail})")}\n");
        }

        if (f.Sidecar is { } sc)
        {
            sb.Append(inv, $"sidecar: {sc.Phase} issue={sc.Issue ?? "-"} restarts={sc.RestartCount}{(sc.Detail is null ? "" : $" detail={sc.Detail}")}\n");
        }

        sb.Append(inv, $"connection: {f.Connection}\ndata: {f.Data.Phase}{(f.Data.Step is null ? "" : $" step={f.Data.Step}")} stepReached={f.DataStepReached}\n");
        sb.Append("timeline (ms since process start):\n");
        foreach (var (m, ms) in Timeline(f))
        {
            sb.Append(inv, $"  {m,-15} {ms,8:F0}\n");
        }

        return sb.ToString();
    }
}

/// <summary>
/// 加载界面的出现 / 停留 / 淡出时序(纯函数;时钟由调用方传入)。规则:
/// <list type="number">
/// <item><b>第一帧就是底色</b>:覆盖层的底色从窗口出现的第一帧起就在(界面层保证),<b>内容</b>(标识、清单)要等 <see cref="StartupTimings.ShowDelay"/> 才出现——
/// 更快就绪时内容根本不出现,底色直接淡出,不闪一下。</item>
/// <item><b>失败立刻显示</b>:错误面板不等延迟(没有理由让用户对着空白等)。</item>
/// <item><b>最短停留</b>:内容一旦出现,至少停留 <see cref="StartupTimings.MinDwell"/> 才淡出。内容没出现过则没有停留要求。</item>
/// <item><b>让位</b>:数据需要用户处理时,整个加载层(含底色)让位给数据页;回来时内容不用再等延迟。</item>
/// <item><b>淡出一次</b>:开始淡出后状态不再变化(重放的旧事件不能把它拉回来);淡出结束由界面层报告,之后是 <see cref="StartupPhase.Done"/>。</item>
/// </list>
/// </summary>
public static class StartupPresentation
{
    /// <summary>表现层自己的状态(纯 UI 状态,放在 ViewModel 里,不进 Store)。</summary>
    /// <param name="OriginAt">
    /// 覆盖层<b>真正显示出来</b>的时刻(窗口打开,即 <see cref="StartupMilestone.WindowShown"/>);还没显示出来时为 null。延迟出现和最短停留都从这一刻算——
    /// 不能从 ViewModel 创建的时刻算:窗口显示之前 UI 线程要花几秒创建整个外壳(真窗口实测),那段时间用户什么也看不到,不能算作「内容已经停留过了」。
    /// </param>
    public sealed record State(DateTimeOffset? OriginAt = null, DateTimeOffset? ContentShownAt = null, DateTimeOffset? RevealStartedAt = null);

    /// <param name="IsOpen">覆盖层(含底色)是否盖在窗口上。</param>
    /// <param name="ContentVisible">内容(标识、清单、提示)是否显示。</param>
    /// <param name="Reveal">已经开始淡出。</param>
    /// <param name="WakeAt">下一次需要重新计算的时刻(延迟出现、最短停留结束);没有则 null。事实变化时调用方照常重算。</param>
    public sealed record Decision(bool IsOpen, bool ContentVisible, bool Reveal, DateTimeOffset? WakeAt, State Next);

    public static Decision Decide(StartupView view, State state, DateTimeOffset now, StartupTimings? timings = null)
    {
        timings ??= StartupTimings.Default;
        if (view.Phase == StartupPhase.Done)
        {
            return new Decision(false, false, false, null, state);   // 淡出结束、已经可交互:覆盖层不在了
        }

        if (state.RevealStartedAt is not null)
        {
            return new Decision(true, state.ContentShownAt is not null, true, null, state);
        }

        if (view.Owner == StartupOwner.DataGate)
        {
            // 让位:整层(含底色)交给数据页;内容出现过的不用再等延迟
            return new Decision(false, false, false, null, state);
        }

        var next = state;
        var displayed = state.OriginAt is not null;
        var delayAt = (state.OriginAt ?? now) + timings.ShowDelay;
        var wake = view.NextTimeAt;
        if (view.Phase == StartupPhase.Revealing)
        {
            var earliest = state.ContentShownAt is { } shown ? shown + timings.MinDwell : now;
            if (now >= earliest)
            {
                next = state with { RevealStartedAt = now };
                return new Decision(true, state.ContentShownAt is not null, true, null, next);
            }

            wake = earliest;
        }

        var contentVisible = state.ContentShownAt is not null;
        if (!contentVisible && view.Phase != StartupPhase.Revealing)
        {
            // 失败立刻显示(哪怕窗口还没出来:出来的那一刻就是错误面板);其余的等「显示出来之后再过 ShowDelay」
            if (view.Failure is not null || (displayed && now >= delayAt))
            {
                next = state with { ContentShownAt = now };
                contentVisible = true;
            }
            else if (displayed)
            {
                wake = Earliest(wake, delayAt);
            }
        }

        return new Decision(true, contentVisible, false, wake, next);
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset b) => a is { } x && x < b ? x : b;
}
