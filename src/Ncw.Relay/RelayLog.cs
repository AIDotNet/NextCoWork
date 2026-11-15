using Microsoft.Extensions.Logging;

namespace Ncw.Relay;

/// <summary>
/// 结构化日志(<c>LoggerMessage</c> 源生成,AOT 友好)。<b>只记元数据</b>:设备 id(公钥哈希,本来就是公开标识)、来源 IP、错误码、时长、字节数。
/// 绝不记路由令牌(明文或哈希)、签名、nonce 或任何转发的内容。
/// </summary>
internal static partial class RelayLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "relay listening on {Urls}")]
    public static partial void Listening(this ILogger log, string urls);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "device registered device={DeviceId} remote={Remote} tokens={Tokens}")]
    public static partial void DeviceRegistered(this ILogger log, string deviceId, string remote, int tokens);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "device went offline device={DeviceId} sessions={Sessions}")]
    public static partial void DeviceOffline(this ILogger log, string deviceId, int sessions);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "device registration replaced device={DeviceId}")]
    public static partial void DeviceReplaced(this ILogger log, string deviceId);

    [LoggerMessage(EventId = 13, Level = LogLevel.Information, Message = "device tokens updated device={DeviceId} tokens={Tokens} revokedSessions={Revoked}")]
    public static partial void TokensUpdated(this ILogger log, string deviceId, int tokens, int revoked);

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "session started device={DeviceId} session={Session} remote={Remote}")]
    public static partial void SessionStarted(this ILogger log, string deviceId, string session, string remote);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "session ended device={DeviceId} session={Session} reason={Reason} durationMs={DurationMs} bytesToDevice={ToDevice} bytesToController={ToController}")]
    public static partial void SessionEnded(this ILogger log, string deviceId, string session, string reason, long durationMs, long toDevice, long toController);

    [LoggerMessage(EventId = 30, Level = LogLevel.Warning, Message = "refused {Path} reason={Reason} remote={Remote} device={DeviceId}")]
    public static partial void Refused(this ILogger log, string path, string reason, string remote, string deviceId);

    [LoggerMessage(EventId = 31, Level = LogLevel.Warning, Message = "registration failed reason={Reason} remote={Remote} device={DeviceId}")]
    public static partial void RegistrationFailed(this ILogger log, string reason, string remote, string deviceId);

    [LoggerMessage(EventId = 40, Level = LogLevel.Debug, Message = "connection ended with error: {Error}")]
    public static partial void ConnectionError(this ILogger log, string error);
}
