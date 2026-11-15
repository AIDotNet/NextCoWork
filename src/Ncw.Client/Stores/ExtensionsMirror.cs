using System.Collections.Immutable;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Stores;

// Skills 与斜杠命令的列表镜像(AGENTS.md §3 规则 1:设备是真源,控制端只在 Store 里留一份镜像,key 带 deviceId)。
// 两个域的列表都是<b>按项目取的</b>(全局层 + 该项目的项目层),所以同一台设备上可以同时持有好几份;别的控制端的改动靠 <c>extensions</c> 主题的修订号触发重拉
// (<see cref="ExtensionRevisionsMirror"/>)。折叠规则都是纯函数:全量替换、只收「不更旧」的(<c>FetchSeq</c>,规则 6)、设备断开标为未知(规则 7)。

/// <summary>某台设备上「某个项目看到的 Skill 清单」的键。<see cref="WorkspaceId"/> 为 null = 只看全局层。</summary>
public readonly record struct SkillsKey(string DeviceId, string? WorkspaceId);

/// <summary>
/// 一份 Skill 清单(<c>skills.list</c>)和它的诊断(<c>skills.diagnostics</c>,单独取,没取过为 <see cref="HasDiagnostics"/> = false)。
/// <see cref="Known"/> = false:设备断开后状态未知,界面不把旧值当现状。<paramref name="FetchSeq"/> 是取这份数据的请求序号(本控制端进程内单调):乱序到达的旧响应不能盖掉新的。
/// </summary>
public sealed record SkillsLayer(bool Known, long FetchSeq, ImmutableArray<SkillInfoDto> Skills, bool HasDiagnostics, long DiagnosticsSeq, ImmutableArray<SkillDiagnosticDto> Diagnostics)
{
    public static readonly SkillsLayer Empty = new(false, 0, [], false, 0, []);
}

public sealed record SkillsMirror(ImmutableDictionary<SkillsKey, SkillsLayer> Lists)
{
    public static readonly SkillsMirror Empty = new(ImmutableDictionary<SkillsKey, SkillsLayer>.Empty);

    /// <summary>没取过 = null(和「取过、一条都没有」区分开)。</summary>
    public SkillsLayer? Of(string deviceId, string? workspaceId) => Lists.GetValueOrDefault(new SkillsKey(deviceId, workspaceId));

    /// <summary>这台设备上控制端持有清单的那些项目(<c>null</c> 在内 = 全局层):修订号变了要重拉的就是它们。</summary>
    public IReadOnlyList<string?> WorkspacesOf(string deviceId) => [.. Lists.Keys.Where(k => k.DeviceId == deviceId).Select(k => k.WorkspaceId)];
}

public static class SkillsReducer
{
    public static SkillsMirror ApplyList(SkillsMirror mirror, string deviceId, string? workspaceId, SkillsListResult result, long fetchSeq)
    {
        var key = new SkillsKey(deviceId, workspaceId);
        var current = mirror.Lists.GetValueOrDefault(key) ?? SkillsLayer.Empty;
        if (current.Known && fetchSeq < current.FetchSeq)
        {
            return mirror;
        }

        var next = current with { Known = true, FetchSeq = fetchSeq, Skills = [.. result.Skills] };
        return Equals(next, current) ? mirror : mirror with { Lists = mirror.Lists.SetItem(key, next) };
    }

    public static SkillsMirror ApplyDiagnostics(SkillsMirror mirror, string deviceId, string? workspaceId, SkillsDiagnosticsResult result, long fetchSeq)
    {
        var key = new SkillsKey(deviceId, workspaceId);
        var current = mirror.Lists.GetValueOrDefault(key) ?? SkillsLayer.Empty;
        if (current.HasDiagnostics && fetchSeq < current.DiagnosticsSeq)
        {
            return mirror;
        }

        var next = current with { HasDiagnostics = true, DiagnosticsSeq = fetchSeq, Diagnostics = [.. result.Diagnostics] };
        return mirror with { Lists = mirror.Lists.SetItem(key, next) };
    }

    /// <summary>设备断开:这台设备的清单标为未知,而不是丢掉(规则 7:不把旧值当现状,也不凭空造出「没有 Skill」)。</summary>
    public static SkillsMirror MarkDeviceUnknown(SkillsMirror mirror, string deviceId)
    {
        var changed = mirror.Lists.Where(kv => kv.Key.DeviceId == deviceId && kv.Value.Known).ToList();
        return changed.Count == 0
            ? mirror
            : mirror with { Lists = mirror.Lists.SetItems(changed.Select(kv => KeyValuePair.Create(kv.Key, kv.Value with { Known = false }))) };
    }
}

/// <summary>某台设备上「某个项目看到的命令清单」的键。<see cref="WorkspaceId"/> 为 null = 只有全局层 + 内置 + 扩展。</summary>
public readonly record struct CommandsKey(string DeviceId, string? WorkspaceId);

/// <summary>
/// 一份命令清单。<see cref="Commands"/> 是运行期清单(已滤掉被关的,<b>带正文</b>,发送前展开用);<see cref="All"/> 是管理清单(被关的也在,标 <c>Enabled</c>,不带正文)。
/// 两份各有自己的取数序号,没取过各自为 false。
/// </summary>
public sealed record CommandsLayer(
    bool Known,
    bool HasCommands,
    long CommandsSeq,
    ImmutableArray<CommandDto> Commands,
    bool HasAll,
    long AllSeq,
    ImmutableArray<CommandItemDto> All,
    bool HasDiagnostics,
    long DiagnosticsSeq,
    ImmutableArray<CommandDiagnosticDto> Diagnostics)
{
    public static readonly CommandsLayer Empty = new(false, false, 0, [], false, 0, [], false, 0, []);
}

public sealed record CommandsMirror(ImmutableDictionary<CommandsKey, CommandsLayer> Lists)
{
    public static readonly CommandsMirror Empty = new(ImmutableDictionary<CommandsKey, CommandsLayer>.Empty);

    public CommandsLayer? Of(string deviceId, string? workspaceId) => Lists.GetValueOrDefault(new CommandsKey(deviceId, workspaceId));

    public IReadOnlyList<string?> WorkspacesOf(string deviceId) => [.. Lists.Keys.Where(k => k.DeviceId == deviceId).Select(k => k.WorkspaceId)];
}

public static class CommandsReducer
{
    public static CommandsMirror ApplyList(CommandsMirror mirror, string deviceId, string? workspaceId, CommandsListResult result, long fetchSeq)
    {
        var key = new CommandsKey(deviceId, workspaceId);
        var current = mirror.Lists.GetValueOrDefault(key) ?? CommandsLayer.Empty;
        if (current.HasCommands && fetchSeq < current.CommandsSeq)
        {
            return mirror;
        }

        return With(mirror, key, current with { Known = true, HasCommands = true, CommandsSeq = fetchSeq, Commands = [.. result.Commands] });
    }

    public static CommandsMirror ApplyListAll(CommandsMirror mirror, string deviceId, string? workspaceId, CommandsListAllResult result, long fetchSeq)
    {
        var key = new CommandsKey(deviceId, workspaceId);
        var current = mirror.Lists.GetValueOrDefault(key) ?? CommandsLayer.Empty;
        if (current.HasAll && fetchSeq < current.AllSeq)
        {
            return mirror;
        }

        return With(mirror, key, current with { Known = true, HasAll = true, AllSeq = fetchSeq, All = [.. result.Commands] });
    }

    public static CommandsMirror ApplyDiagnostics(CommandsMirror mirror, string deviceId, string? workspaceId, CommandsDiagnosticsResult result, long fetchSeq)
    {
        var key = new CommandsKey(deviceId, workspaceId);
        var current = mirror.Lists.GetValueOrDefault(key) ?? CommandsLayer.Empty;
        if (current.HasDiagnostics && fetchSeq < current.DiagnosticsSeq)
        {
            return mirror;
        }

        return With(mirror, key, current with { Known = true, HasDiagnostics = true, DiagnosticsSeq = fetchSeq, Diagnostics = [.. result.Diagnostics] });
    }

    public static CommandsMirror MarkDeviceUnknown(CommandsMirror mirror, string deviceId)
    {
        var changed = mirror.Lists.Where(kv => kv.Key.DeviceId == deviceId && kv.Value.Known).ToList();
        return changed.Count == 0
            ? mirror
            : mirror with { Lists = mirror.Lists.SetItems(changed.Select(kv => KeyValuePair.Create(kv.Key, kv.Value with { Known = false }))) };
    }

    private static CommandsMirror With(CommandsMirror mirror, CommandsKey key, CommandsLayer next) =>
        Equals(mirror.Lists.GetValueOrDefault(key), next) ? mirror : mirror with { Lists = mirror.Lists.SetItem(key, next) };
}
