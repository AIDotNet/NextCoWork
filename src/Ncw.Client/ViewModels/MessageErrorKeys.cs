using Ncw.Client.Actions;

namespace Ncw.Client.ViewModels;

/// <summary>消息操作失败的稳定 key(<c>messages.*</c>):<c>Ncw.Client</c> 不产出界面文字,界面按 key 翻译。</summary>
public static class MessageErrorKeys
{
    public const string Busy = MessageActions.BusyKey;
    public const string ResendFailed = MessageActions.ResendFailedKey;
    public const string Unavailable = "messages.unavailable";
    public const string Forbidden = "messages.forbidden";
    public const string Gone = "messages.gone";
    public const string Failed = "messages.failed";

    /// <summary>一次失败的结局 → 稳定 key。</summary>
    public static string Of<T>(ActionResult<T> result) => result.ErrorKey switch
    {
        Busy => Busy,
        ResendFailed => ResendFailed,
        _ => result.Outcome switch
        {
            ActionOutcome.Unavailable => Unavailable,
            ActionOutcome.Forbidden => Forbidden,
            ActionOutcome.NotFound => Gone,
            _ => Failed,
        },
    };
}
