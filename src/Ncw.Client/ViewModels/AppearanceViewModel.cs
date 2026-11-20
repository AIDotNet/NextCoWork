using CommunityToolkit.Mvvm.ComponentModel;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 设置 · 本应用 · 外观(docs/design/07 §3、03 §9):现在只有「动效」一项(完整 / 减弱 / 关闭 / 跟随系统)。
/// 真源是 <see cref="ClientStores.Prefs"/>(<see cref="UiPrefs.Motion"/>);这里的属性只是读写它的入口,不缓存。
/// 实际生效的级别由纯函数 <see cref="UiMotion.Resolve"/> 解析(偏好 × 操作系统的「减少动态效果」),由 UI 层应用到 Arc。
/// </summary>
public sealed partial class AppearanceViewModel : ObservableObject
{
    private readonly ClientStores _stores;

    public AppearanceViewModel(ClientStores stores)
    {
        _stores = stores;
        stores.Prefs.Changed += OnPrefsChanged;
    }

    /// <summary>操作系统是否要求减少动态效果;<c>null</c> = 这个平台读不到(Avalonia 12 没有这个 API,各宿主自己探测)。UI 层装配时接上。</summary>
    public Func<bool?> SystemReducesMotion { get; set; } = static () => null;

    /// <summary>用户选的主题偏好(深色 / 浅色 / 跟随系统)。</summary>
    public UiThemePreference Theme
    {
        get => _stores.Prefs.State.Theme;
        set => _stores.Prefs.Update(p => p with { Theme = value });
    }

    /// <summary>用户选的动效偏好。</summary>
    public UiMotionPreference Motion
    {
        get => _stores.Prefs.State.Motion;
        set => _stores.Prefs.Update(p => p with { Motion = value });
    }

    /// <summary>此刻实际生效的级别。</summary>
    public UiMotionLevel EffectiveLevel => UiMotion.Resolve(Motion, SystemReducesMotion());

    /// <summary>选了「跟随系统」但这个平台读不到系统设置(按「完整」处理,界面如实说明)。</summary>
    public bool SystemUnknown => Motion == UiMotionPreference.System && SystemReducesMotion() is null;

    private void OnPrefsChanged()
    {
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(Motion));
        OnPropertyChanged(nameof(EffectiveLevel));
        OnPropertyChanged(nameof(SystemUnknown));
    }
}
