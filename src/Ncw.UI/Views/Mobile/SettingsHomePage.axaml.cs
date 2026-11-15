using Avalonia.Controls;
using Avalonia.Interactivity;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Mobile;

/// <summary>手机设置的入口列表。点一行只是告诉导航「推入这个分组」。</summary>
public partial class SettingsHomePage : NcwPage
{
    public SettingsHomePage() => InitializeComponent();

    public MobileNavigationViewModel? Navigation { get; set; }

    private void OnSectionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is string tag && Enum.TryParse<SettingsSection>(tag, out var section))
        {
            Navigation?.OpenSettingsSection(section);
        }
    }

    private void OnNotificationsClick(object? sender, RoutedEventArgs e) => Navigation?.Push(new NotificationsRoute());
}
