using Ncw.Client.Actions;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 一次失败的结构化描述(检查器各面板共用):稳定的 <c>files.*</c> / <c>git.*</c> / <c>terminals.*</c> key + 结局类别 + 设备给的英文诊断。
/// Client 不产出界面文字,视图按 <see cref="Key"/>(没有就按 <see cref="Outcome"/>)翻译,<see cref="Detail"/> 放进「详情」提示。
/// </summary>
public readonly record struct Problem(ActionOutcome Outcome, string? Key, string? Detail = null)
{
    public static Problem Of<T>(ActionResult<T> result) => new(result.Outcome, result.ErrorKey, result.Detail);

    public bool IsConflict => Outcome == ActionOutcome.Conflict;

    public bool IsUnavailable => Outcome == ActionOutcome.Unavailable;
}
