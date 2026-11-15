using System.Collections.Immutable;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>一个顶层 Run 在一台设备上的改动集镜像的键(key 带 deviceId,AGENTS.md §3 规则 1)。</summary>
public readonly record struct ReviewKey(string DeviceId, string RunId);

/// <summary>
/// 一个 Run 的变更集摘要镜像。<see cref="ChangeSet"/> 为 null = 已经问过设备、这个 Run 没改过文件(或还没落库);<see cref="Known"/> = false 表示设备断开后状态未知(规则 7):
/// 界面不把旧值当现状(例如不显示「已撤销」按钮的可点状态),重连后重新取。
/// </summary>
public sealed record RunReview(bool Known, ReviewChangeSetDto? ChangeSet);

/// <summary>各设备上各 Run 的改动审查变更集摘要镜像(<c>review.changeSet</c>;设备库是真源)。摘要不带全文,全文按需用 <c>ReviewActions.FileDiffAsync</c> 取、不进 Store。</summary>
public sealed record ReviewMirror(ImmutableDictionary<ReviewKey, RunReview> Runs)
{
    public static readonly ReviewMirror Empty = new(ImmutableDictionary<ReviewKey, RunReview>.Empty);

    public RunReview? Of(string deviceId, string runId) => Runs.GetValueOrDefault(new ReviewKey(deviceId, runId));

    /// <summary>这个 Run 的变更集(没有 / 没问过 / 没改过文件都是 null)。</summary>
    public ReviewChangeSetDto? ChangeSetOf(string deviceId, string runId) => Of(deviceId, runId)?.ChangeSet;
}

/// <summary>
/// 改动审查镜像的折叠规则(纯函数)。变更集是单调的:同一个 Run 的摘要以 <see cref="ReviewChangeSetDto.UpdatedAt"/> 较新的为准,
/// 乱序到达 / 重放的旧摘要不能把「已撤销」改回「已应用」(规则 6);变更集不会凭空消失,所以一份「没有」不能盖掉已有的摘要;设备断开标为未知(规则 7)。
/// </summary>
public static class ReviewReducer
{
    public static ReviewMirror Apply(ReviewMirror mirror, string deviceId, string runId, ReviewChangeSetDto? incoming)
    {
        var key = new ReviewKey(deviceId, runId);
        var current = mirror.Runs.GetValueOrDefault(key);
        var set = current?.ChangeSet;
        if (set is not null && (incoming is null || incoming.UpdatedAt < set.UpdatedAt))
        {
            // 手里的更新(或这次没取到):保留内容,只是现在又确认过一次了
            return current!.Known ? mirror : mirror with { Runs = mirror.Runs.SetItem(key, current with { Known = true }) };
        }

        var next = new RunReview(true, incoming);
        return current == next ? mirror : mirror with { Runs = mirror.Runs.SetItem(key, next) };
    }

    /// <summary>会话里改过文件的各轮(<c>review.list</c>)整批合进来,逐个按单调规则。</summary>
    public static ReviewMirror ApplyMany(ReviewMirror mirror, string deviceId, IEnumerable<ReviewChangeSetDto> sets) =>
        sets.Aggregate(mirror, (m, set) => Apply(m, deviceId, set.RunId, set));

    public static ReviewMirror MarkDeviceUnknown(ReviewMirror mirror, string deviceId)
    {
        var changed = mirror.Runs.Where(kv => kv.Key.DeviceId == deviceId && kv.Value.Known).ToList();
        return changed.Count == 0
            ? mirror
            : mirror with { Runs = mirror.Runs.SetItems(changed.Select(kv => KeyValuePair.Create(kv.Key, kv.Value with { Known = false }))) };
    }
}
