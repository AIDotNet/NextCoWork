namespace Ncw.Agent.Cloud;

/// <summary>空闲判定的输入(Host 每次检查时现取)。时间都是 Unix 毫秒。</summary>
/// <param name="Now">现在。</param>
/// <param name="ActiveRuns">运行中的 Run 数(含子代理 Run)。</param>
/// <param name="Controllers">已连接的控制端数(局域网之外的中继会话;云端没有局域网)。</param>
/// <param name="LastActivityAt">最后一次「有人在用」的时间:启动、控制端连接 / 断开、Run 开始 / 结束、指令应用。</param>
/// <param name="NextScheduledAt">最近一个启用的定时任务的触发时间;没有为 null。</param>
public sealed record IdleInputs(long Now, int ActiveRuns, int Controllers, long LastActivityAt, long? NextScheduledAt);

/// <summary>空闲结论。</summary>
/// <param name="CanSleep">现在可以休眠。</param>
/// <param name="Blocker">不能休眠的原因:<c>runs</c> / <c>controllers</c> / <c>recentActivity</c> / <c>schedule</c>。</param>
/// <param name="WakeAt">可以休眠时,希望编排服务唤醒的时间(下一个定时任务 − 提前量);null = 只在控制端连接时唤醒。</param>
public sealed record IdleVerdict(bool CanSleep, string? Blocker = null, long? WakeAt = null);

public static class IdleBlockers
{
    public const string Runs = "runs";
    public const string Controllers = "controllers";
    public const string RecentActivity = "recentActivity";
    public const string Schedule = "schedule";
}

/// <summary>
/// 空闲检测(02 A6「生命周期」):没有运行中的 Run、没有连接的控制端、没有临近的定时任务,并且已经空闲了 N 分钟,才可以休眠。纯函数,时钟由调用方给。
/// </summary>
/// <param name="IdleAfter">空闲多久之后休眠(N)。</param>
/// <param name="WakeLead">唤醒提前量。</param>
/// <param name="MinSleep">下一个定时任务离现在不到这么久就不休眠(睡不到这么久再唤醒不划算)。</param>
public sealed record IdlePolicy(TimeSpan IdleAfter, TimeSpan WakeLead, TimeSpan MinSleep)
{
    public IdleVerdict Evaluate(IdleInputs input)
    {
        if (input.ActiveRuns > 0)
        {
            return new IdleVerdict(false, IdleBlockers.Runs);
        }

        if (input.Controllers > 0)
        {
            return new IdleVerdict(false, IdleBlockers.Controllers);
        }

        if (input.Now - input.LastActivityAt < (long)IdleAfter.TotalMilliseconds)
        {
            return new IdleVerdict(false, IdleBlockers.RecentActivity);
        }

        if (input.NextScheduledAt is { } next)
        {
            if (next - input.Now < (long)MinSleep.TotalMilliseconds)
            {
                return new IdleVerdict(false, IdleBlockers.Schedule);
            }

            return new IdleVerdict(true, null, Math.Max(input.Now, next - (long)WakeLead.TotalMilliseconds));
        }

        return new IdleVerdict(true);
    }
}
