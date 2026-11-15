using System.Collections.Immutable;

namespace Ncw.Client.Widgets;

/// <summary>某台设备上某个会话里的一张 widget 卡(key 带 deviceId,AGENTS.md §3 规则 1)。<see cref="CallId"/> 是产出它的那次 <c>visualize_show_widget</c> 调用。</summary>
public readonly record struct WidgetRef(string DeviceId, string SessionId, string CallId);

/// <summary>提交的进度。<see cref="Accepted"/> 与 <see cref="Closed"/> 是终态(规则 6:只升不降)。</summary>
public enum WidgetSubmit
{
    None,
    Submitting,

    /// <summary>设备接受了这次提交(先到先得里赢了)。</summary>
    Accepted,

    /// <summary>设备说「没有在等」(别的控制端先答了、Run 结束了):这张卡不能再提交。</summary>
    Closed,
}

/// <summary>
/// 一张卡在控制端的镜像。<see cref="StateJson"/> 是设备上存的小组件状态(<c>widgets.getState</c> 的结果加上本地的写入),不是真源:真源在设备的 kv。
/// <see cref="StateRev"/> 每次本地写入加一,读回来的结果带着发出请求时的版本,版本变了说明期间有新的写入——旧的读回结果丢弃,不能盖掉更新的写入(规则 6)。
/// <see cref="Stale"/>:设备断开过 / 写失败过,状态不可信,下次要重读。
/// </summary>
public sealed record WidgetEntry(bool Loaded, string? StateJson, long StateRev, bool Stale, WidgetSubmit Submit, long Touched)
{
    public static readonly WidgetEntry Empty = new(false, null, 0, false, WidgetSubmit.None, 0);
}

/// <summary>widget 卡的镜像(<c>ClientStores.Widgets</c>)。</summary>
public sealed record WidgetsMirror(ImmutableDictionary<WidgetRef, WidgetEntry> Cards, long Clock)
{
    public static readonly WidgetsMirror Empty = new(ImmutableDictionary<WidgetRef, WidgetEntry>.Empty, 0);

    /// <summary>每台控制端最多记多少张卡(设备上每个会话的上限是 200 张;这里是全部会话合计,超了丢最久没碰过的)。</summary>
    public const int MaxCards = 256;

    public WidgetEntry Of(WidgetRef card) => Cards.GetValueOrDefault(card) ?? WidgetEntry.Empty;
}

/// <summary>widget 镜像的折叠规则(纯函数)。</summary>
public static class WidgetsReducer
{
    /// <summary>读回来的状态。只有发请求之后没有本地写入过(<paramref name="requestedRev"/> 仍是当前版本)才收下。</summary>
    public static WidgetsMirror StateLoaded(WidgetsMirror mirror, WidgetRef card, string? stateJson, long requestedRev)
    {
        var entry = mirror.Of(card);
        return entry.StateRev != requestedRev ? mirror : With(mirror, card, entry with { Loaded = true, StateJson = stateJson, Stale = false });
    }

    /// <summary>本地写入(<c>setState</c>,乐观更新)。</summary>
    public static WidgetsMirror StateWritten(WidgetsMirror mirror, WidgetRef card, string? stateJson)
    {
        var entry = mirror.Of(card);
        return With(mirror, card, entry with { Loaded = true, StateJson = stateJson, StateRev = entry.StateRev + 1, Stale = false });
    }

    /// <summary>写失败:本地的值和设备上的可能不一致,标陈旧(下次重读);版本照样加一,在途的旧读回结果不能盖掉它。</summary>
    public static WidgetsMirror StateWriteFailed(WidgetsMirror mirror, WidgetRef card)
    {
        var entry = mirror.Of(card);
        return With(mirror, card, entry with { Stale = true, StateRev = entry.StateRev + 1 });
    }

    public static WidgetsMirror SubmitStarted(WidgetsMirror mirror, WidgetRef card)
    {
        var entry = mirror.Of(card);
        return entry.Submit == WidgetSubmit.None ? With(mirror, card, entry with { Submit = WidgetSubmit.Submitting }) : mirror;
    }

    /// <summary>
    /// 提交结束。<paramref name="outcome"/> 为 <see cref="WidgetSubmit.None"/> = 没送达(设备不可用,可以重试)。终态不被覆盖(规则 6)。
    /// </summary>
    public static WidgetsMirror SubmitSettled(WidgetsMirror mirror, WidgetRef card, WidgetSubmit outcome)
    {
        var entry = mirror.Of(card);
        if (entry.Submit is WidgetSubmit.Accepted or WidgetSubmit.Closed || entry.Submit == outcome)
        {
            return mirror;
        }

        return With(mirror, card, entry with { Submit = outcome });
    }

    /// <summary>设备断开:这台设备上所有卡的状态标陈旧(规则 7:不把旧值当现状;重连后重读)。提交的终态不受影响。</summary>
    public static WidgetsMirror MarkUnknown(WidgetsMirror mirror, string deviceId)
    {
        var next = mirror.Cards;
        foreach (var (key, entry) in mirror.Cards)
        {
            if (key.DeviceId == deviceId && !entry.Stale)
            {
                next = next.SetItem(key, entry with { Stale = true, Submit = entry.Submit == WidgetSubmit.Submitting ? WidgetSubmit.None : entry.Submit });
            }
        }

        return ReferenceEquals(next, mirror.Cards) ? mirror : mirror with { Cards = next };
    }

    private static WidgetsMirror With(WidgetsMirror mirror, WidgetRef card, WidgetEntry entry)
    {
        var clock = mirror.Clock + 1;
        var cards = mirror.Cards.SetItem(card, entry with { Touched = clock });
        if (cards.Count > WidgetsMirror.MaxCards)
        {
            var oldest = cards.OrderBy(kv => kv.Value.Touched).First().Key;
            cards = cards.Remove(oldest);
        }

        return new WidgetsMirror(cards, clock);
    }
}
