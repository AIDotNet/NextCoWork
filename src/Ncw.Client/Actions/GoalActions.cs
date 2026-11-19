using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 目标相关的用户意图(AGENTS.md §3 规则 4)。目标的真源在设备(<c>GoalStore</c> + 转录里的 <c>goal_status</c> 标记);控制端按 <c>(deviceId, 会话)</c> 镜像,
/// 首帧(<c>goal</c> 主题的快照、<c>goal.get</c>)和后续变更(<c>goal.changed</c>)是同一个全量类型。设定 / 清除的结果不直接改 Store:状态经主题回来。
/// </summary>
/// <summary>输入框的 <c>/goal</c> 用到的两个目标意图(<see cref="GoalActions"/> 的子集):ViewModel 依赖它,单测里换成假实现。</summary>
public interface IGoalCommands
{
    Task<ActionResult<GoalSetResultDto>> SetAsync(SessionRef session, string condition, SendOptionsDto? options = null, CancellationToken ct = default);

    Task<ActionResult<Unit>> ClearAsync(SessionRef session, CancellationToken ct = default);
}

public sealed class GoalActions(DeviceRegistry devices) : IGoalCommands
{
    /// <summary>
    /// 取一个会话当前的目标(设备会先从转录恢复)。结果同时按「不更旧」的版本规则合进 Store(打开会话时用:恢复本身也会广播,这里保证没订阅 <c>goal</c> 主题时也拿得到)。
    /// </summary>
    public async Task<ActionResult<ActiveGoal?>> GetAsync(SessionRef session, CancellationToken ct = default)
    {
        var result = await Run(session.DeviceId, link => link.CallAsync(OrchestrationMethods.GoalGet, new SessionRefParams(session.SessionId), ct)).ConfigureAwait(false);
        if (!result.IsOk || result.Value is not { } state)
        {
            return new ActionResult<ActiveGoal?>(result.Outcome, null, result.ErrorKey, result.ErrorParams, result.Detail);
        }

        devices.Find(session.DeviceId)?.UpdateConversations(c => OrchestrationReducer.ApplyGoalState(c, session.DeviceId, state));
        return ActionResult<ActiveGoal?>.Success(state.Goal is null ? null : OrchestrationReducer.ToGoal(state.Goal));
    }

    /// <summary>
    /// 设定目标。校验失败(空 / 超长)不是错误,是结果的 <see cref="GoalSetResultDto.Outcome"/>;<paramref name="options"/> 是当前选中的发送档位
    /// (设备拿它给 kickoff 起内部 Run:会话从没发过消息时,设备没有别的档位可用)。
    /// </summary>
    public Task<ActionResult<GoalSetResultDto>> SetAsync(SessionRef session, string condition, SendOptionsDto? options = null, CancellationToken ct = default) =>
        Run(session.DeviceId, link => link.CallAsync(OrchestrationMethods.GoalSet, new GoalSetParams(session.SessionId, condition, options), ct, idem: Idem("set")));

    /// <summary>清除目标(幂等:没有目标也不报错)。</summary>
    public Task<ActionResult<Unit>> ClearAsync(SessionRef session, CancellationToken ct = default) =>
        Run(session.DeviceId, link => link.CallAsync(OrchestrationMethods.GoalClear, new SessionRefParams(session.SessionId), ct, idem: Idem("clear")));

    private static string Idem(string what) => $"goal.{what}:{Guid.NewGuid():N}";

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
