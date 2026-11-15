using System.Collections.Immutable;

namespace Ncw.Core.Domain;

/// <summary>
/// 设备权威广播里的一条顶层 Run(docs/design/09 §8.2 的 <c>device</c> 主题)。
/// <see cref="PendingInteractions"/> 已经包含所有后代 Run 的待处理交互。
/// </summary>
public sealed record ActiveRun(
    string DeviceId,
    string RunId,
    string SessionId,
    string WorkspaceId,
    int PendingInteractions);

/// <summary>
/// 设备权威广播里的一条运行中的子 Run(<c>activeRuns</c> 的 <c>subagents</c> 半边)。
/// <see cref="SessionId"/> 是<b>父</b>会话(子代理卡片住在那里);父 Run 已被回收时退回 <c>ParentSessionId</c>。
/// </summary>
public sealed record ActiveSubagent(
    string DeviceId,
    string RunId,
    string ParentRunId,
    string SessionId,
    string WorkspaceId,
    DateTimeOffset StartedAt);

/// <summary>
/// 一台设备的活动 Run 索引快照。首帧和后续广播是同一个类型(AGENTS.md §3 规则 5),由 <see cref="ActiveRunIndexBuilder"/> 这一个函数生成。
/// <see cref="Runs"/> 只含顶层 Run(角标不数子代理);子 Run 在 <see cref="Subagents"/> 里。
/// <see cref="Rev"/> 单调递增:广播可能乱序到达,控制端只收更新的那份(0 = 不带版本,例如 Mock 数据)。
/// </summary>
public sealed record ActiveRunIndex(string DeviceId, ImmutableArray<ActiveRun> Runs)
{
    public ImmutableArray<ActiveSubagent> Subagents { get; init; } = ImmutableArray<ActiveSubagent>.Empty;

    public long Rev { get; init; }

    public static ActiveRunIndex Empty(string deviceId) => new(deviceId, ImmutableArray<ActiveRun>.Empty);
}

/// <summary>项目栏 / 侧边栏 / 手机活动页共同使用的项目活动状态。</summary>
public readonly record struct ProjectActivity(int RunningRuns, int PendingInteractions, bool Unknown)
{
    public static readonly ProjectActivity Idle = new(0, 0, false);

    public bool IsRunning => RunningRuns > 0;

    public static ProjectActivity operator +(ProjectActivity a, ProjectActivity b) =>
        new(a.RunningRuns + b.RunningRuns, a.PendingInteractions + b.PendingInteractions, a.Unknown || b.Unknown);
}

/// <summary>
/// 「项目现在在干什么」的唯一派生函数(AGENTS.md §3 规则 3)。项目栏图标、项目组角标、项目面板、
/// 手机活动页都调用它,不许各自再判一遍。
/// </summary>
public static class ProjectActivityView
{
    /// <param name="project">要看的项目。</param>
    /// <param name="index">该项目所在设备最近一次的权威索引;没有收到过就传 null。</param>
    /// <param name="connection">该设备的连接状态。</param>
    public static ProjectActivity Of(ProjectRef project, ActiveRunIndex? index, DeviceConnection connection)
    {
        // 设备连不上时,运行中的状态是「未知」,既不能当成结束,也不能继续算作运行中(规则 7)。
        if (connection is DeviceConnection.Offline or DeviceConnection.Connecting)
        {
            return new ProjectActivity(0, 0, Unknown: index is { Runs.IsEmpty: false });
        }

        if (index is null || index.DeviceId != project.DeviceId)
        {
            return ProjectActivity.Idle;
        }

        var running = 0;
        var pending = 0;
        foreach (var run in index.Runs)
        {
            if (run.WorkspaceId != project.WorkspaceId)
            {
                continue;
            }

            running++;
            pending += run.PendingInteractions;
        }

        return new ProjectActivity(running, pending, false);
    }

    /// <summary>项目组的角标 = 组内每个项目的活动相加。</summary>
    public static ProjectActivity Sum(IEnumerable<ProjectActivity> members)
    {
        var total = ProjectActivity.Idle;
        foreach (var member in members)
        {
            total += member;
        }

        return total;
    }
}
