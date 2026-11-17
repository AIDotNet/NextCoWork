using CommunityToolkit.Mvvm.ComponentModel;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>
/// 设置 · 本应用 · 通知(docs/design/07 §3):系统通知(待处理 / 完成 / 失败)、Dock / 任务栏角标、托盘图标的开关。
/// 真源是 <see cref="UiPrefs.Attention"/>;这里的属性只是读写它的入口,不缓存。
/// </summary>
public sealed partial class NotificationsSettingsViewModel : ObservableObject
{
    private readonly ClientStores _stores;

    public NotificationsSettingsViewModel(ClientStores stores)
    {
        _stores = stores;
        stores.Prefs.Changed += () =>
        {
            OnPropertyChanged(nameof(NotifyPending));
            OnPropertyChanged(nameof(NotifyCompleted));
            OnPropertyChanged(nameof(NotifyFailed));
            OnPropertyChanged(nameof(Badge));
            OnPropertyChanged(nameof(Tray));
        };
    }

    /// <summary>宿主能不能发系统通知(桌面装配时设置;不支持的平台界面如实说明)。</summary>
    [ObservableProperty] public partial bool IsSystemNotificationSupported { get; set; } = true;

    /// <summary>宿主能不能显示 Dock / 任务栏角标。</summary>
    [ObservableProperty] public partial bool IsBadgeSupported { get; set; } = true;

    private AttentionPrefs Current => _stores.Prefs.State.AttentionOrDefault;

    public bool NotifyPending
    {
        get => Current.NotifyPending;
        set => Set(a => a with { NotifyPending = value });
    }

    public bool NotifyCompleted
    {
        get => Current.NotifyCompleted;
        set => Set(a => a with { NotifyCompleted = value });
    }

    public bool NotifyFailed
    {
        get => Current.NotifyFailed;
        set => Set(a => a with { NotifyFailed = value });
    }

    public bool Badge
    {
        get => Current.Badge;
        set => Set(a => a with { Badge = value });
    }

    public bool Tray
    {
        get => Current.Tray;
        set => Set(a => a with { Tray = value });
    }

    private void Set(Func<AttentionPrefs, AttentionPrefs> change) =>
        _stores.Prefs.Update(p => p with { Attention = change(p.AttentionOrDefault) });
}
