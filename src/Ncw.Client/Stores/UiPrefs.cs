using System.Collections.Immutable;
using Ncw.Client.Shortcuts;
using Ncw.Client.ViewModels;

namespace Ncw.Client.Stores;

/// <summary>
/// 控制端本机的界面偏好:栏宽、栏的开关、右侧面板添加了哪些页签、动效级别、快捷键覆盖、通知。
/// 这是「本应用」作用域的设置(docs/design/07 §3),只在 <see cref="ClientStores.Prefs"/> 里有一份,ViewModel 读写它。
/// </summary>
public sealed record UiPrefs(
    double SidebarWidth,
    double InspectorWidth,
    bool IsSidebarOpen,
    bool IsInspectorOpen,
    ImmutableArray<InspectorTabKind> InspectorTabs,
    UiMotionPreference Motion = UiMotionPreference.System,
    ImmutableDictionary<AppCommand, string>? Shortcuts = null,
    AttentionPrefs? Attention = null)
{
    /// <summary>用户改过的快捷键(docs/design/03 §11);没改过是空表。</summary>
    public ImmutableDictionary<AppCommand, string> ShortcutOverrides => Shortcuts ?? ImmutableDictionary<AppCommand, string>.Empty;

    /// <summary>通知与提醒(docs/design/07 §3「通知」);没设置过是默认值。</summary>
    public AttentionPrefs AttentionOrDefault => Attention ?? AttentionPrefs.Default;

    /// <summary>左侧边栏(项目树,docs/design/03 §1)。旧版本「项目栏 + 项目面板」存下的宽度超出新范围时由 <see cref="Normalize"/> 夹紧。</summary>
    public const double SidebarDefaultWidth = 280;
    public const double SidebarMinWidth = 240;
    public const double SidebarMaxWidth = 420;
    public const double InspectorDefaultWidth = 320;
    public const double InspectorMinWidth = 260;
    public const double InspectorMaxWidth = 520;

    public static readonly UiPrefs Default = new(SidebarDefaultWidth, InspectorDefaultWidth, true, true, []);

    /// <summary>读进来的值可能来自旧版本或被手改过:夹到合法范围,去掉不认识的页签。</summary>
    public UiPrefs Normalize() => this with
    {
        SidebarWidth = Clamp(SidebarWidth, SidebarMinWidth, SidebarMaxWidth, SidebarDefaultWidth),
        InspectorWidth = Clamp(InspectorWidth, InspectorMinWidth, InspectorMaxWidth, InspectorDefaultWidth),
        InspectorTabs = InspectorTabs.IsDefault
            ? []
            : [.. InspectorTabs.Where(ViewModels.InspectorTabs.Addable.Contains).Distinct()],
        Motion = Enum.IsDefined(Motion) ? Motion : UiMotionPreference.System,
        Shortcuts = Shortcuts is null ? null : Shortcuts.Where(kv => Enum.IsDefined(kv.Key) && (kv.Value.Length == 0 || ShortcutCatalog.IsUsable(kv.Value)))
            .ToImmutableDictionary(kv => kv.Key, kv => kv.Value.Length == 0 ? "" : ShortcutCatalog.Normalize(kv.Value)),
    };

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    // ImmutableArray 默认按引用比较;偏好里需要按内容比较,Store 才能识别「没有变化」。
    public bool Equals(UiPrefs? other) =>
        other is not null
        && SidebarWidth.Equals(other.SidebarWidth)
        && InspectorWidth.Equals(other.InspectorWidth)
        && IsSidebarOpen == other.IsSidebarOpen
        && IsInspectorOpen == other.IsInspectorOpen
        && Motion == other.Motion
        && AttentionOrDefault == other.AttentionOrDefault
        && ShortcutOverrides.Count == other.ShortcutOverrides.Count
        && ShortcutOverrides.All(kv => other.ShortcutOverrides.TryGetValue(kv.Key, out var v) && v == kv.Value)
        && (InspectorTabs.IsDefault ? [] : InspectorTabs).SequenceEqual(other.InspectorTabs.IsDefault ? [] : other.InspectorTabs);

    public override int GetHashCode() => HashCode.Combine(SidebarWidth, InspectorWidth, IsSidebarOpen, IsInspectorOpen, Motion);
}

/// <summary>
/// 桌面的通知与提醒(本应用作用域,docs/design/07 §3「通知」):系统通知分三类开关;Dock / 任务栏角标是「现在能处理的待办」数;
/// 托盘(菜单栏)图标显示运行中和待处理的数量。窗口在前台、而且正看着那个会话时不弹通知(人已经看到了)。
/// </summary>
public sealed record AttentionPrefs(bool NotifyPending = true, bool NotifyCompleted = true, bool NotifyFailed = true, bool Badge = true, bool Tray = true)
{
    public static readonly AttentionPrefs Default = new();
}
