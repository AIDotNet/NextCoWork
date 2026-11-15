using Ncw.Agent.Environments;
using Ncw.Agent.Terminal;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Terminals;

/// <summary>终端领域类型 → 协议 DTO,以及 <see cref="TerminalException"/> / <see cref="EnvironmentException"/> → 协议错误的映射(06 §9.4)。</summary>
internal static class TerminalMapping
{
    public static TerminalEnvDto ToDto(TerminalEnvironmentRef environment) =>
        new(environment.Id.DeviceId, environment.Id.Environment, environment.Name, environment.Kind == EnvironmentKind.Ssh ? "ssh" : "local");

    public static TerminalDto ToDto(TerminalInfo info) => new(
        info.Id,
        info.ScopeId,
        ToDto(info.Environment),
        info.Title,
        info.Cwd,
        info.Shell,
        info.Columns,
        info.Rows,
        info.Status == TerminalStatus.Running ? "running" : "exited",
        info.CreatedAt.ToUnixTimeMilliseconds(),
        info.ExitCode,
        info.Signal,
        info.ExitReason is { } reason ? ReasonName(reason) : null,
        info.ExitedAt?.ToUnixTimeMilliseconds());

    public static TerminalIntentDto ToDto(TerminalIntent intent) =>
        new(intent.Id, intent.TerminalId, intent.ScopeId, ToDto(intent.Environment), intent.Cwd, intent.Shell, intent.ExpiresAt.ToUnixTimeMilliseconds());

    public static string ReasonName(ProcessExitReason reason) => reason switch
    {
        ProcessExitReason.Killed => "killed",
        ProcessExitReason.ConnectionLost => "connectionLost",
        _ => "exited",
    };

    /// <summary>终端事件 → 主题的事件帧:<c>seq</c> 就是 <see cref="TerminalEvent.Seq"/>(每个终端内严格连续,退出事件也占一个)。</summary>
    public static EventFrame ToFrame(string topic, TerminalEvent evt) => evt switch
    {
        TerminalOutput output => new EventFrame(topic, output.Seq, TerminalEvents.Data,
            FrameCodec.EncodePayload(new TerminalDataDto(output.Data.ToArray()), PayloadShapes.GetShape<TerminalDataDto>())),
        TerminalExit exit => new EventFrame(topic, exit.Seq, TerminalEvents.Exit,
            FrameCodec.EncodePayload(new TerminalExitDto(exit.ExitCode, exit.Signal, ReasonName(exit.Reason)), PayloadShapes.GetShape<TerminalExitDto>())),
        _ => throw new InvalidOperationException($"Unknown terminal event {evt.GetType().Name}"),
    };

    public static ProtocolException Error(string code, string key, string? detail = null, bool retryable = false) =>
        new(code, detail ?? key, retryable, FrameCodec.EncodePayload(new TerminalErrorDto(key, detail), PayloadShapes.GetShape<TerminalErrorDto>()));

    /// <summary>终端层 / 环境层的失败 → 协议错误;不认识的异常返回 null(调用方原样抛,路由收成 <c>internal</c>)。</summary>
    public static ProtocolException? Map(Exception exception) => exception switch
    {
        TerminalException e => e.Code switch
        {
            TerminalErrorCode.NotFound => Error(ErrorCodes.NotFound, TerminalErrorKeys.NotFound),
            TerminalErrorCode.Conflict when e.Detail?.Contains("exited", StringComparison.Ordinal) == true => Error(ErrorCodes.Conflict, TerminalErrorKeys.Exited, e.Detail),
            TerminalErrorCode.Conflict => Error(ErrorCodes.Conflict, TerminalErrorKeys.Conflict, e.Detail),
            TerminalErrorCode.ApprovalRequired => Error(ErrorCodes.Conflict, TerminalErrorKeys.ApprovalRequired),
            TerminalErrorCode.ApprovalExpired => Error(ErrorCodes.Conflict, TerminalErrorKeys.ApprovalExpired),
            TerminalErrorCode.ResourceLimit => Error(ErrorCodes.Busy, TerminalErrorKeys.LimitReached, e.Detail),
            TerminalErrorCode.TooLarge => Error(ErrorCodes.TooLarge, TerminalErrorKeys.TooLarge, e.Detail),
            TerminalErrorCode.InvalidArgument => Error(ErrorCodes.InvalidRequest, TerminalErrorKeys.InvalidArgument, e.Detail),
            TerminalErrorCode.Cancelled => Error(ErrorCodes.Unavailable, TerminalErrorKeys.Cancelled, retryable: true),
            _ => null,
        },
        EnvironmentException e => e.Code switch
        {
            EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.StaleGeneration or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.ResultUnknown
                => Error(ErrorCodes.Unavailable, TerminalErrorKeys.Disconnected, e.Detail, retryable: true),
            EnvironmentErrorCode.Unsupported or EnvironmentErrorCode.UnsupportedPlatform => Error(ErrorCodes.Unsupported, TerminalErrorKeys.Unsupported, e.Detail),
            EnvironmentErrorCode.InvalidPath or EnvironmentErrorCode.PathEscape or EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory
                => Error(ErrorCodes.InvalidRequest, TerminalErrorKeys.InvalidPath, e.Detail),
            _ => null,
        },
        _ => null,
    };
}
