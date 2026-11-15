using PolyType;

namespace Ncw.Protocol.Wire;

/// <summary>协议常量(docs/design/09 §6、§7)。协议版本独立于应用版本。</summary>
public static class ProtocolInfo
{
    /// <summary>当前协议版本。</summary>
    public const int Version = 1;

    /// <summary>仍然支持的最低版本(N-1 兼容)。</summary>
    public const int MinVersion = 1;

    /// <summary>单帧上限,超过的走流式方法分块。</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>每条连接排队等待发送的字节上限(docs/design/09 §8.2:8 MiB)。超过时 <c>run/*</c> 先丢增量,结构性事件改发 <c>resync</c>。</summary>
    public const int MaxPendingBytes = 8 * 1024 * 1024;
}

/// <summary>
/// 线上的一帧。L2 是 4 字节大端长度 + MessagePack;L4 的方法参数、结果、主题状态再各自编码成 <c>byte[]</c>,
/// 这样帧本身不需要知道每个方法的类型(docs/design/09 §6、§8)。字段名就是键名(字符串键),未知字段忽略。
/// </summary>
[DerivedTypeShape(typeof(HelloFrame), Name = "hello")]
[DerivedTypeShape(typeof(WelcomeFrame), Name = "welcome")]
[DerivedTypeShape(typeof(RejectFrame), Name = "reject")]
[DerivedTypeShape(typeof(RequestFrame), Name = "req")]
[DerivedTypeShape(typeof(ResponseFrame), Name = "res")]
[DerivedTypeShape(typeof(CancelFrame), Name = "cancel")]
[DerivedTypeShape(typeof(SubscribeFrame), Name = "sub")]
[DerivedTypeShape(typeof(UnsubscribeFrame), Name = "unsub")]
[DerivedTypeShape(typeof(SnapshotFrame), Name = "snapshot")]
[DerivedTypeShape(typeof(EventFrame), Name = "evt")]
[DerivedTypeShape(typeof(ResyncFrame), Name = "resync")]
[DerivedTypeShape(typeof(PingFrame), Name = "ping")]
[DerivedTypeShape(typeof(PongFrame), Name = "pong")]
public abstract record Frame;

/// <summary>控制端发起会话(docs/design/09 §7)。</summary>
public sealed record HelloFrame(
    int MinVersion,
    int MaxVersion,
    string ClientId,
    string ClientVersion,
    string FormFactor,
    string[] Features,
    int CoalesceMs,
    string? LocalToken,
    string? ResumeToken) : Frame;

public sealed record WelcomeFrame(
    int Version,
    string DeviceId,
    string DeviceVersion,
    string Role,
    string[] Features,
    string ResumeToken,
    long ServerTime) : Frame;

public sealed record RejectFrame(string Code, string Message) : Frame;

/// <summary>请求。<see cref="Idem"/> 是 command 类方法的幂等键。</summary>
public sealed record RequestFrame(ulong Id, string Method, byte[] Params, string? Idem) : Frame;

public sealed record ResponseFrame(ulong Id, bool Ok, byte[]? Result, ProtocolError? Error) : Frame;

public sealed record CancelFrame(ulong Id) : Frame;

public sealed record SubscribeFrame(ulong Id, string Topic, long? Cursor) : Frame;

public sealed record UnsubscribeFrame(string Topic) : Frame;

/// <summary>主题的权威快照(游标续不上,或首次订阅)。</summary>
public sealed record SnapshotFrame(string Topic, long Seq, byte[] State) : Frame;

/// <summary>主题的增量。同一主题内 <see cref="Seq"/> 严格递增。</summary>
public sealed record EventFrame(string Topic, long Seq, string Type, byte[] Data) : Frame;

/// <summary>设备无法继续推送该主题,控制端必须不带游标重新订阅。</summary>
public sealed record ResyncFrame(string Topic, string Reason) : Frame;

public sealed record PingFrame(long Time) : Frame;

public sealed record PongFrame(long Time) : Frame;

/// <summary>
/// 错误(docs/design/09 §8.1)。<see cref="Details"/> 是方法自己约定的结构化补充(MessagePack 编码的 DTO,例如 <c>interactions.respond</c> 的
/// <c>conflict</c> 带「谁在什么时候处理」);没有补充为 null。
/// </summary>
public sealed record ProtocolError(string Code, string Message, bool Retryable, long? RetryAfterMs = null, byte[]? Details = null);

/// <summary>错误码。</summary>
public static class ErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string Busy = "busy";
    public const string RateLimited = "rate_limited";
    public const string Unsupported = "unsupported";
    public const string TooLarge = "too_large";
    public const string Unavailable = "unavailable";
    public const string ClientOutdated = "client_outdated";
    public const string Internal = "internal";
}

/// <summary>协议层的失败:握手被拒、方法返回错误、连接断开。</summary>
public sealed class ProtocolException(string code, string message, bool retryable = false, byte[]? details = null) : Exception(message)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;

    /// <summary>方法约定的结构化补充(见 <see cref="ProtocolError.Details"/>);用 <see cref="FrameCodec.DecodePayload{T}"/> 解码。</summary>
    public byte[]? Details { get; } = details;

    public static ProtocolException From(ProtocolError error) => new(error.Code, error.Message, error.Retryable, error.Details);
}
