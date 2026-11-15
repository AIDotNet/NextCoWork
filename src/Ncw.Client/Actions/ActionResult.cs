using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

public enum ActionOutcome
{
    Ok,

    /// <summary>设备没连上 / 数据没就绪(可重试)。</summary>
    Unavailable,

    /// <summary>没有权限(角色不够,或这个方法不允许这条通道)。</summary>
    Forbidden,

    NotFound,

    /// <summary>参数不合法(<see cref="ActionResult{T}.ErrorKey"/> 说明哪一项)。</summary>
    Invalid,

    /// <summary>冲突(别名已存在、登录被新的顶掉)。</summary>
    Conflict,

    /// <summary>设备不支持(没有这个能力 / 这家不走账号登录)。</summary>
    Unsupported,

    /// <summary>设备上的操作失败(上游连不上、登录失败……);<see cref="ActionResult{T}.ErrorKey"/> 带稳定的 <c>providers.*</c> key。</summary>
    Failed,
}

/// <summary>
/// 一个意图的结局:结构化的,界面自己生成文字(Ncw.Client 不产出界面文字)。<see cref="ErrorKey"/> / <see cref="ErrorParams"/> 来自设备的
/// <c>error.details</c>(<see cref="ProviderErrorDto"/>),没有时为 null;<see cref="Detail"/> 是英文诊断,只给日志。
/// </summary>
public readonly record struct ActionResult<T>(ActionOutcome Outcome, T? Value = default, string? ErrorKey = null, IReadOnlyDictionary<string, string>? ErrorParams = null, string? Detail = null)
{
    public bool IsOk => Outcome == ActionOutcome.Ok;

    internal static ActionResult<T> Success(T value) => new(ActionOutcome.Ok, value);

    internal static ActionResult<T> From(ProtocolException e)
    {
        string? key = null;
        IReadOnlyDictionary<string, string>? parameters = null;
        if (e.Details is { Length: > 0 } bytes)
        {
            try
            {
                var details = FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProviderErrorDto>());
                key = details.Key;
                parameters = details.Params;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // details 不是 ProviderErrorDto 的形状:文件 / git / 终端的错误补充是 { Key, Detail }(同一个键名,稳定 key 在 Key 里)
                try
                {
                    key = FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<FilesErrorDto>()).Key;
                }
                catch (Exception inner) when (inner is not OutOfMemoryException)
                {
                    // 别的方法约定的补充:忽略。
                }
            }
        }

        var outcome = e.Code switch
        {
            ErrorCodes.Forbidden or ErrorCodes.Unauthorized => ActionOutcome.Forbidden,
            ErrorCodes.NotFound => ActionOutcome.NotFound,
            ErrorCodes.InvalidRequest => ActionOutcome.Invalid,
            ErrorCodes.Conflict => ActionOutcome.Conflict,
            ErrorCodes.Unsupported => ActionOutcome.Unsupported,
            ErrorCodes.Unavailable when key is null || e.Retryable => ActionOutcome.Unavailable,
            _ => ActionOutcome.Failed,
        };
        return new ActionResult<T>(outcome, default, key, parameters, e.Message);
    }

    internal static ActionResult<T> NotConnected() => new(ActionOutcome.Unavailable);

    /// <summary>设备不可用(没连上 / 没接线)。给 <c>Unavailable*</c> 空实现用。</summary>
    public static ActionResult<T> NotConnectedResult() => new(ActionOutcome.Unavailable);
}
