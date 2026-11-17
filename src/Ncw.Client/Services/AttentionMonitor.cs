using System.Collections.Immutable;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Services;

/// <summary>
/// 一条要发出去的系统通知。只带结构化数据(Client 不产出界面文字):界面层按 <see cref="Kind"/> 拼标题和正文。
/// <see cref="Key"/> 是收件箱条目的稳定 key,点通知时据此打开那一条。
/// </summary>
public sealed record AttentionNotice(
    string Key,
    InboxKind Kind,
    string DeviceId,
    string? WorkspaceId,
    string? SessionId,
    string? ProjectName,
    string? SessionTitle,
    string? ToolName,
    string? Target,
    string? Detail,
    bool Dangerous);

/// <summary>托盘(菜单栏)图标要显示的摘要。<see cref="Unknown"/>:有设备断开,数字可能不全(规则 7,界面如实说)。</summary>
public sealed record AttentionSummary(int Pending, int Running, bool Unknown);

/// <summary>提醒的出口:系统通知、Dock / 任务栏角标、托盘摘要。桌面宿主实现;测试和手机是空实现。</summary>
public interface IAttentionSink
{
    void Notify(AttentionNotice notice);

    /// <summary>应用图标上的数字;0 = 清掉。</summary>
    void SetBadge(int count);

    void SetSummary(AttentionSummary summary);
}

public sealed class NoAttention : IAttentionSink
{
    public static readonly NoAttention Instance = new();

    public void Notify(AttentionNotice notice)
    {
    }

    public void SetBadge(int count)
    {
    }

    public void SetSummary(AttentionSummary summary)
    {
    }
}

/// <summary>判断这一次要不要提醒用户时,界面那一刻的状态。</summary>
/// <param name="WindowActive">主窗口在前台。</param>
/// <param name="OpenSession">当前打开的会话(设备 + 会话 id);没有为 null。</param>
public readonly record struct AttentionContext(bool WindowActive, (string DeviceId, string SessionId)? OpenSession);

/// <summary>
/// 提醒的规则(纯函数,单测见 <c>AttentionPolicyTests</c>):收件箱里<b>新出现</b>的条目里,哪些要弹系统通知。
/// <list type="bullet">
/// <item>只看新的 key;第一次看到收件箱(启动、刚接上设备)只记基线、不提醒——不能一打开应用就把历史上完成过的 Run 全弹一遍。</item>
/// <item>待办要「现在能处理」(设备连着);断线设备上的条目是未知,不提醒(规则 7)。</item>
/// <item>窗口在前台、而且正打开着那个会话:人已经看到了,不提醒。</item>
/// <item>三类开关(待处理 / 完成 / 失败)各管各的。</item>
/// </list>
/// </summary>
public static class AttentionPolicy
{
    public static ImmutableArray<InboxEntry> Select(IReadOnlySet<string> seen, IEnumerable<InboxEntry> entries, AttentionPrefs prefs, AttentionContext context)
    {
        var result = ImmutableArray.CreateBuilder<InboxEntry>();
        foreach (var entry in entries)
        {
            if (seen.Contains(entry.Key) || entry.Unknown)
            {
                continue;
            }

            var wanted = entry.Kind switch
            {
                InboxKind.RunCompleted => prefs.NotifyCompleted,
                InboxKind.RunFailed => prefs.NotifyFailed,
                _ => prefs.NotifyPending && entry.IsActionable,
            };
            if (!wanted)
            {
                continue;
            }

            if (context.WindowActive && context.OpenSession is { } open && open.DeviceId == entry.DeviceId && open.SessionId == entry.SessionId)
            {
                continue;
            }

            result.Add(entry);
        }

        return result.ToImmutable();
    }
}

/// <summary>
/// 桌面的提醒(docs/design/03 §11.2):跟着收件箱(Core 的 <see cref="ActivityInboxView"/>,和手机收件箱、⌘J 是同一份派生)走,
/// 新出现的条目按 <see cref="AttentionPolicy"/> 发系统通知;角标 = 收件箱的 <see cref="ActivityInbox.BadgeCount"/>;托盘摘要 = 待处理数 + 运行中的顶层 Run 数。
/// 本类自己只记「见过哪些 key」(纯 UI 状态),不持有任何事实。
/// </summary>
public sealed class AttentionMonitor : IDisposable
{
    private readonly ClientStores _stores;
    private readonly InboxViewModel _inbox;
    private readonly Func<IAttentionSink> _sink;
    private readonly Func<AttentionContext> _context;
    private readonly TimeProvider _clock;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private DateTimeOffset? _firstConnectedAt;
    private bool _baselined;

    /// <summary>第一台设备连上以后多长时间内到达的内容都算基线(全量交互、activeRuns 在连上之后才陆续到)。</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(3);
    private int _lastBadge = -1;
    private AttentionSummary? _lastSummary;

    public AttentionMonitor(ClientStores stores, InboxViewModel inbox, Func<IAttentionSink> sink, Func<AttentionContext> context, TimeProvider? clock = null)
    {
        _stores = stores;
        _clock = clock ?? TimeProvider.System;
        _inbox = inbox;
        _sink = sink;
        _context = context;
        inbox.Updated += Evaluate;
        stores.Prefs.Changed += OnPrefsChanged;
    }

    public void Dispose()
    {
        _inbox.Updated -= Evaluate;
        _stores.Prefs.Changed -= OnPrefsChanged;
    }

    /// <summary>
    /// 基线:启动时设备还没接上,收件箱是空的;第一台设备连上之后 <see cref="SettleTime"/> 之内到达的条目(全量交互、activeRuns 陆续到)都只记不提醒。
    /// 之后新出现的才按 <see cref="AttentionPolicy"/> 提醒。
    /// </summary>
    public void Evaluate()
    {
        var inbox = _inbox.Current;
        var prefs = _stores.Prefs.State.AttentionOrDefault;
        var anyConnected = _stores.Workspace.State.Devices.Values.Any(static d => d.Connection == DeviceConnection.Connected);

        if (!_baselined && anyConnected)
        {
            var now = _clock.GetUtcNow();
            _firstConnectedAt ??= now;
            _baselined = now - _firstConnectedAt.Value >= SettleTime;
        }

        if (!_baselined)
        {
            foreach (var entry in inbox.Entries)
            {
                _seen.Add(entry.Key);
            }

            Publish(inbox, prefs);
            return;
        }

        foreach (var entry in AttentionPolicy.Select(_seen, inbox.Entries, prefs, _context()))
        {
            var row = _inbox.Find(entry.Key);
            _sink().Notify(new AttentionNotice(
                entry.Key, entry.Kind, entry.DeviceId, entry.WorkspaceId, entry.SessionId,
                row?.ProjectName, row?.SessionTitle, row?.ToolName, row?.Target, row?.Detail, entry.Dangerous));
        }

        // 记下现在的条目:断线设备上的不记(重连后它还在,key 也早已记过,不会当成新的);消失的 key 留着,同一条来回闪烁时不重复提醒
        foreach (var entry in inbox.Entries.Where(static e => !e.Unknown))
        {
            _seen.Add(entry.Key);
        }

        Publish(inbox, prefs);
    }

    private void OnPrefsChanged() => Publish(_inbox.Current, _stores.Prefs.State.AttentionOrDefault);

    private void Publish(ActivityInbox inbox, AttentionPrefs prefs)
    {
        var badge = prefs.Badge ? inbox.BadgeCount : 0;
        if (badge != _lastBadge)
        {
            _lastBadge = badge;
            _sink().SetBadge(badge);
        }

        var summary = new AttentionSummary(inbox.BadgeCount, RunningRuns(), inbox.HasUnknown);
        if (summary != _lastSummary)
        {
            _lastSummary = summary;
            _sink().SetSummary(summary);
        }
    }

    /// <summary>所有连着的设备上正在运行的顶层 Run(activeRuns 权威广播;断线设备不计,它们的状态未知)。</summary>
    private int RunningRuns() =>
        _stores.Workspace.State.Devices.Values.Where(static d => d.Connection == DeviceConnection.Connected).Sum(static d => d.Index?.Runs.Length ?? 0);
}
