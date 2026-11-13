using System.Collections.Immutable;
using Ncw.Client.ViewModels;

namespace Ncw.Client.Stores;

/// <summary>
/// 控制端本机的界面偏好:栏宽、栏的开关、右侧面板添加了哪些页签。
/// 这是「本应用」作用域的设置(docs/design/07 §3),只在 <see cref="ClientStores.Prefs"/> 里有一份,ViewModel 读写它。
/// </summary>
public sealed record UiPrefs(
    double SidebarWidth,
    double InspectorWidth,
    bool IsSidebarOpen,
    bool IsInspectorOpen,
    ImmutableArray<InspectorTabKind> InspectorTabs)
{
    public const double SidebarDefaultWidth = 352;
    public const double SidebarMinWidth = 304;   // 项目栏 64 + 项目面板 240
    public const double SidebarMaxWidth = 484;   // 项目栏 64 + 项目面板 420
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
        && (InspectorTabs.IsDefault ? [] : InspectorTabs).SequenceEqual(other.InspectorTabs.IsDefault ? [] : other.InspectorTabs);

    public override int GetHashCode() => HashCode.Combine(SidebarWidth, InspectorWidth, IsSidebarOpen, IsInspectorOpen);
}
