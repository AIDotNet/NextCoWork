using System.Collections.Immutable;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

/// <summary>终端在控制端镜像里的 key:<c>(deviceId, terminalId)</c>(AGENTS.md §3 规则 1:key 带 deviceId)。</summary>
public readonly record struct TerminalKey(string DeviceId, string TerminalId);

/// <summary>
/// 一个终端的元数据镜像。<see cref="Seq"/> 是最近一次元数据同步(快照 / 退出 / 列表)时的输出序号——输出字节不进 Store,
/// 走显示端的接收器(<see cref="Devices.ITerminalSink"/>),所以每一批输出不会引起 Store 变化。
/// <see cref="Known"/> = false:设备断开(或还没拿到这台设备的新数据),状态是未知(规则 7):不判成结束,也不当现状。
/// </summary>
public sealed record TerminalView(TerminalDto Info, long Seq, bool Known)
{
    public bool Exited => Info.Status == "exited";
}

/// <summary>
/// 各设备上终端会话表的镜像(<c>terminals.list</c> + <c>terminal/&lt;id&gt;</c> 主题的元数据)。真源在设备的 <c>TerminalManager</c>。
/// 变更只经 <see cref="TerminalReducer"/> 的纯函数;<b>状态单调</b>:已退出的终端不会被迟到的「运行中」元数据改回运行中。
/// </summary>
public sealed record TerminalMirror(ImmutableDictionary<TerminalKey, TerminalView> Terminals)
{
    public static readonly TerminalMirror Empty = new(ImmutableDictionary<TerminalKey, TerminalView>.Empty);

    public TerminalView? Of(string deviceId, string terminalId) => Terminals.GetValueOrDefault(new TerminalKey(deviceId, terminalId));

    /// <summary>一个项目下的终端(按创建时间排)。</summary>
    public ImmutableArray<TerminalView> InProject(string deviceId, string projectId) =>
        [.. Terminals.Where(kv => kv.Key.DeviceId == deviceId && kv.Value.Info.ProjectId == projectId).Select(static kv => kv.Value).OrderBy(static v => v.Info.CreatedAt)];
}

/// <summary>终端镜像的归约规则(纯函数,只有这一份)。</summary>
public static class TerminalReducer
{
    /// <summary>
    /// 合并一份元数据。同一个 id 的「段」用 <c>CreatedAt</c> 区分(已退出的终端在同一个 id 上重新创建是新的一段):
    /// 更老的段忽略;更新的段整个替换;同一段里,已退出的不被「运行中」改回去(终态压过运行中),其余以后到的为准。
    /// </summary>
    public static TerminalMirror ApplyInfo(TerminalMirror mirror, string deviceId, TerminalDto info, long seq)
    {
        var key = new TerminalKey(deviceId, info.Id);
        if (mirror.Terminals.TryGetValue(key, out var current))
        {
            if (info.CreatedAt < current.Info.CreatedAt)
            {
                return mirror;
            }

            if (info.CreatedAt == current.Info.CreatedAt && current.Exited)
            {
                // 终态压过运行中;已退出的再来一份已退出的只刷新「已知」和序号
                var next = current with { Seq = Math.Max(current.Seq, seq), Known = true };
                return next == current ? mirror : mirror with { Terminals = mirror.Terminals.SetItem(key, next) };
            }
        }

        var view = new TerminalView(info, seq, Known: true);
        return current == view ? mirror : mirror with { Terminals = mirror.Terminals.SetItem(key, view) };
    }

    /// <summary>
    /// 一个项目下的终端列表是全量:这台设备这个项目里不在列表里的终端被移除(已关闭 / 已回收)。
    /// 每一项仍走 <see cref="ApplyInfo"/> 的单调规则。
    /// </summary>
    public static TerminalMirror ApplyList(TerminalMirror mirror, string deviceId, string projectId, IReadOnlyList<TerminalDto> terminals)
    {
        var present = terminals.Select(static t => t.Id).ToHashSet(StringComparer.Ordinal);
        var next = mirror;
        foreach (var gone in mirror.Terminals.Where(kv => kv.Key.DeviceId == deviceId && kv.Value.Info.ProjectId == projectId && !present.Contains(kv.Key.TerminalId)).Select(static kv => kv.Key))
        {
            next = next with { Terminals = next.Terminals.Remove(gone) };
        }

        foreach (var terminal in terminals)
        {
            next = ApplyInfo(next, deviceId, terminal, mirror.Of(deviceId, terminal.Id)?.Seq ?? 0);
        }

        return next;
    }

    /// <summary>终态(<c>terminals.exit</c>)。没见过这个终端(元数据还没来)什么都不做;已经退出的不改(第一个终态胜出)。</summary>
    public static TerminalMirror ApplyExit(TerminalMirror mirror, string deviceId, string terminalId, TerminalExitDto exit, long seq)
    {
        var key = new TerminalKey(deviceId, terminalId);
        if (!mirror.Terminals.TryGetValue(key, out var current) || current.Exited)
        {
            return mirror;
        }

        var info = current.Info with { Status = "exited", ExitCode = exit.ExitCode, Signal = exit.Signal, ExitReason = exit.Reason };
        return mirror with { Terminals = mirror.Terminals.SetItem(key, new TerminalView(info, Math.Max(current.Seq, seq), Known: true)) };
    }

    /// <summary>终端在设备上已经不存在(关闭、回收、订阅得到 not_found)。</summary>
    public static TerminalMirror ApplyRemoved(TerminalMirror mirror, string deviceId, string terminalId) =>
        mirror.Terminals.ContainsKey(new TerminalKey(deviceId, terminalId))
            ? mirror with { Terminals = mirror.Terminals.Remove(new TerminalKey(deviceId, terminalId)) }
            : mirror;

    /// <summary>设备断开:这台设备上的终端状态未知(规则 7)。不判成结束,也不删——重连后新的元数据把它们补上。</summary>
    public static TerminalMirror MarkDeviceUnknown(TerminalMirror mirror, string deviceId)
    {
        var next = mirror;
        foreach (var (key, view) in mirror.Terminals.Where(kv => kv.Key.DeviceId == deviceId && kv.Value.Known))
        {
            next = next with { Terminals = next.Terminals.SetItem(key, view with { Known = false }) };
        }

        return next;
    }
}
