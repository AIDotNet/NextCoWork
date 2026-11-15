using Ncw.Agent.Environments;
using Ncw.Agent.Host.Extensions;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Environments;

/// <summary>
/// 环境层错误 → 协议错误:粗粒度的错误码给路由 / 通用处理,稳定 <c>environments.*</c> key 放进 <c>details</c>(<see cref="ProviderErrorDto"/>,控制端翻译)。
/// <b>消息是固定的短句</b>:主机名、路径、服务器给的文本、系统异常消息都不外泄(它们可能带出内网布局,也从不含凭据)。
/// </summary>
internal static class EnvironmentErrors
{
    public static string KeyOf(EnvironmentErrorCode code) => code switch
    {
        EnvironmentErrorCode.Authentication => EnvironmentErrorKeys.Authentication,
        EnvironmentErrorCode.HostKeyChanged => EnvironmentErrorKeys.HostKeyChanged,
        EnvironmentErrorCode.HostKeyRejected => EnvironmentErrorKeys.HostKeyRejected,
        EnvironmentErrorCode.Cancelled => EnvironmentErrorKeys.Cancelled,
        EnvironmentErrorCode.Timeout => EnvironmentErrorKeys.Timeout,
        EnvironmentErrorCode.ConnectionFailed => EnvironmentErrorKeys.ConnectionFailed,
        EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.StaleGeneration or EnvironmentErrorCode.ResultUnknown => EnvironmentErrorKeys.Disconnected,
        EnvironmentErrorCode.InvalidProfile => EnvironmentErrorKeys.InvalidProfile,
        EnvironmentErrorCode.UnsupportedPlatform => EnvironmentErrorKeys.UnsupportedPlatform,
        EnvironmentErrorCode.Unsupported or EnvironmentErrorCode.SftpUnavailable => EnvironmentErrorKeys.Unsupported,
        EnvironmentErrorCode.InvalidPath or EnvironmentErrorCode.PathEscape or EnvironmentErrorCode.NotDirectory => EnvironmentErrorKeys.InvalidPath,
        EnvironmentErrorCode.NotFound => EnvironmentErrorKeys.NotFoundPath,
        EnvironmentErrorCode.EnvironmentNotFound => EnvironmentErrorKeys.NotFound,
        EnvironmentErrorCode.PermissionDenied => EnvironmentErrorKeys.PermissionDenied,
        _ => EnvironmentErrorKeys.Internal,
    };

    public static ProtocolException ToProtocol(EnvironmentException e)
    {
        var key = KeyOf(e.Code);
        return e.Code switch
        {
            EnvironmentErrorCode.Authentication or EnvironmentErrorCode.HostKeyChanged or EnvironmentErrorCode.HostKeyRejected
                => Coded(ErrorCodes.Unavailable, "The environment could not be connected.", key),
            EnvironmentErrorCode.Cancelled or EnvironmentErrorCode.Timeout or EnvironmentErrorCode.ConnectionFailed
                or EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.StaleGeneration or EnvironmentErrorCode.ResultUnknown
                => Coded(ErrorCodes.Unavailable, "The environment is not available.", key, retryable: true),
            EnvironmentErrorCode.InvalidProfile or EnvironmentErrorCode.InvalidPath or EnvironmentErrorCode.PathEscape or EnvironmentErrorCode.NotDirectory
                => Coded(ErrorCodes.InvalidRequest, "The environment request was rejected.", key),
            EnvironmentErrorCode.UnsupportedPlatform or EnvironmentErrorCode.Unsupported or EnvironmentErrorCode.SftpUnavailable
                => Coded(ErrorCodes.Unsupported, "The environment does not support this.", key),
            EnvironmentErrorCode.NotFound or EnvironmentErrorCode.EnvironmentNotFound => Coded(ErrorCodes.NotFound, "The environment or path does not exist.", key),
            EnvironmentErrorCode.PermissionDenied => Coded(ErrorCodes.Forbidden, "The remote host denied access.", key),
            _ => Coded(ErrorCodes.Internal, "The environment request failed.", key),
        };
    }

    public static ProtocolException Coded(string code, string message, string key, bool retryable = false, IReadOnlyDictionary<string, string>? parameters = null) =>
        ExtensionErrors.Coded(code, message, key, parameters, retryable);

    public static ProtocolException NotFound(string id) =>
        Coded(ErrorCodes.NotFound, "The environment does not exist.", EnvironmentErrorKeys.NotFound);

    /// <summary>环境暂时连不上(还没连接 / 正在连接 / 失败):可重试的 <c>unavailable</c>,key 说明原因。</summary>
    public static ProtocolException NotConnected(string key = EnvironmentErrorKeys.NotConnected) =>
        Coded(ErrorCodes.Unavailable, "The project's environment is not connected.", key, retryable: true);
}
