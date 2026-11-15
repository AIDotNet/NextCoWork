namespace Ncw.Core.Domain;

/// <summary>会话在列表里的活动状态。<see cref="Unknown"/> 只在设备断开时出现(规则 7):既不当成结束,也不继续算运行中。</summary>
public enum SessionActivity
{
    Idle,
    Running,
    Unknown,
}

/// <summary>
/// 「这个会话在不在跑」的唯一派生函数(AGENTS.md §3 规则 3):会话列表的状态点、标签页、手机活动页都调它。
/// 真源是设备的 <c>activeRuns</c> 权威广播(<see cref="ActiveRunIndex"/>),不看任何会话字段。
/// </summary>
public static class SessionActivityView
{
    public static SessionActivity Of(string sessionId, ActiveRunIndex? index, DeviceConnection connection)
    {
        var inIndex = index is not null && index.Runs.Any(r => r.SessionId == sessionId);
        if (connection is not DeviceConnection.Connected)
        {
            return inIndex ? SessionActivity.Unknown : SessionActivity.Idle;
        }

        return inIndex ? SessionActivity.Running : SessionActivity.Idle;
    }
}
