using System.Collections.Immutable;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>
/// 一台设备上「某个项目看到的钩子清单」的镜像的键(key 带 deviceId,AGENTS.md §3 规则 1)。<see cref="WorkspaceId"/> 为 null = 只看全局层。
/// 清单是按项目取的(全局层 + 该项目的项目层),所以同一台设备上可以同时持有好几份。
/// </summary>
public readonly record struct HooksKey(string DeviceId, string? WorkspaceId);

/// <summary>
/// 一份钩子清单(<c>hooks.list</c> 的结果)和它的配置诊断(<c>hooks.diagnostics</c>,单独取,没取过为 <see cref="HasDiagnostics"/> = false)。
/// <see cref="Known"/> = false 表示设备断开后状态未知(规则 7):界面不把旧值当现状,重连后按 <c>extensions</c> 主题的 <c>HooksRev</c> 重新取。
/// <see cref="Hooks"/> 里<b>没有环境变量的值</b>(设备不下发,只有变量名)。
/// </summary>
/// <param name="FetchSeq">取这份数据的请求序号(本控制端进程内单调):乱序到达的旧响应不能盖掉新的(规则 6)。</param>
public sealed record HooksLayer(bool Known, long FetchSeq, ImmutableArray<HookDto> Hooks, bool HasDiagnostics, long DiagnosticsSeq, ImmutableArray<HookDiagnosticDto> Diagnostics)
{
    public static readonly HooksLayer Empty = new(false, 0, [], false, 0, []);
}

/// <summary>各设备上各项目的钩子清单镜像(设备上的设置文件是真源;控制端只镜像)。</summary>
public sealed record HooksMirror(ImmutableDictionary<HooksKey, HooksLayer> Lists)
{
    public static readonly HooksMirror Empty = new(ImmutableDictionary<HooksKey, HooksLayer>.Empty);

    /// <summary>没取过 = null(和「取过、没有钩子」区分开)。</summary>
    public HooksLayer? Of(string deviceId, string? workspaceId) => Lists.GetValueOrDefault(new HooksKey(deviceId, workspaceId));

    /// <summary>这台设备上控制端持有清单的那些项目(<c>null</c> 在内 = 全局层):修订号变了要重拉的就是它们。</summary>
    public IReadOnlyList<string?> WorkspacesOf(string deviceId) => [.. Lists.Keys.Where(k => k.DeviceId == deviceId).Select(k => k.WorkspaceId)];
}

/// <summary>
/// 钩子镜像的折叠规则(纯函数)。清单是全量:收到不更旧的(<see cref="HooksLayer.FetchSeq"/> 不小于手里的)就整体替换;乱序到达 / 重放的旧响应不能改回去(规则 6);
/// 设备断开标为未知而不是丢掉(规则 7:不把旧值当现状,也不凭空造出「没有钩子」)。
/// </summary>
public static class HooksReducer
{
    public static HooksMirror ApplyList(HooksMirror mirror, string deviceId, string? workspaceId, HooksListResult result, long fetchSeq)
    {
        var key = new HooksKey(deviceId, workspaceId);
        var current = mirror.Lists.GetValueOrDefault(key) ?? HooksLayer.Empty;
        if (current.Known && fetchSeq < current.FetchSeq)
        {
            return mirror;
        }

        var next = current with { Known = true, FetchSeq = fetchSeq, Hooks = [.. result.Hooks] };
        return Equals(next, current) ? mirror : mirror with { Lists = mirror.Lists.SetItem(key, next) };
    }

    public static HooksMirror ApplyDiagnostics(HooksMirror mirror, string deviceId, string? workspaceId, HooksDiagnosticsResult result, long fetchSeq)
    {
        var key = new HooksKey(deviceId, workspaceId);
        var current = mirror.Lists.GetValueOrDefault(key) ?? HooksLayer.Empty;
        if (current.HasDiagnostics && fetchSeq < current.DiagnosticsSeq)
        {
            return mirror;
        }

        var next = current with { HasDiagnostics = true, DiagnosticsSeq = fetchSeq, Diagnostics = [.. result.Diagnostics] };
        return mirror with { Lists = mirror.Lists.SetItem(key, next) };
    }

    public static HooksMirror MarkDeviceUnknown(HooksMirror mirror, string deviceId)
    {
        var changed = mirror.Lists.Where(kv => kv.Key.DeviceId == deviceId && kv.Value.Known).ToList();
        return changed.Count == 0
            ? mirror
            : mirror with { Lists = mirror.Lists.SetItems(changed.Select(kv => KeyValuePair.Create(kv.Key, kv.Value with { Known = false }))) };
    }
}
