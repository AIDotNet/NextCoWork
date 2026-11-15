using Avalonia;
using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Management;

namespace Ncw.UI.Views.Mobile;

/// <summary>
/// 设置的一个分组页(导航栈里推入的页面):标题 + 桌面设置同一批页面内容(同一个 ViewModel,不重做)。
/// 页面内容在手机上放进可滚动的 Page,左右留 16px。分组与页面的对应和桌面的 <c>SettingsOverlay</c> 一致。
/// </summary>
public static class SettingsSectionPage
{
    public static NcwPage Create(SettingsViewModel settings, SettingsSection section)
    {
        var content = ContentOf(settings, section);
        content.Margin = new Thickness(16, 8, 16, 24);
        return new NcwPage
        {
            IsScrollable = true,
            AvoidKeyboard = true,
            Header = new NcwNavbar { Title = TitleOf(section) },
            Content = content,
        };
    }

    public static string TitleOf(SettingsSection section) => NcwTexts.Get(section switch
    {
        SettingsSection.Devices => NcwTextKind.SettingsDevices,
        SettingsSection.Models => NcwTextKind.SettingsModels,
        SettingsSection.Remote => NcwTextKind.SettingsRemote,
        SettingsSection.General => NcwTextKind.SettingsGeneral,
        SettingsSection.Project => NcwTextKind.SettingsProject,
        SettingsSection.Skills => NcwTextKind.SettingsSkills,
        SettingsSection.Commands => NcwTextKind.SettingsCommands,
        SettingsSection.Hooks => NcwTextKind.SettingsHooks,
        SettingsSection.Mcp => NcwTextKind.SettingsMcp,
        SettingsSection.Usage => NcwTextKind.SettingsUsage,
        SettingsSection.Storage => NcwTextKind.SettingsStorage,
        SettingsSection.Gateway => NcwTextKind.SettingsGateway,
        _ => NcwTextKind.Settings,
    });

    private static Control ContentOf(SettingsViewModel s, SettingsSection section) => section switch
    {
        SettingsSection.Devices => new DevicesSettingsPage { DataContext = s.Devices },
        SettingsSection.Models => new ModelsSettingsPage { DataContext = s.Models },
        SettingsSection.Remote => new RemoteSettingsPage { DataContext = s.Remote },
        SettingsSection.General => new GeneralSettingsPage { DataContext = s.Pages.General },
        SettingsSection.Project => new ProjectSettingsPage { DataContext = s.Pages.Project },
        SettingsSection.Skills => new SkillsPage { DataContext = s.Pages.Skills },
        SettingsSection.Commands => new CommandsPage { DataContext = s.Pages.Commands },
        SettingsSection.Hooks => new HooksPage { DataContext = s.Pages.Hooks },
        SettingsSection.Mcp => new McpPage { DataContext = s.Pages.Mcp },
        SettingsSection.Usage => new UsagePage { DataContext = s.Pages.Usage },
        SettingsSection.Storage => new StoragePage { DataContext = s.Pages.Storage },
        SettingsSection.Gateway => new GatewayPage { DataContext = s.Pages.Gateway },
        _ => new TextBlock(),
    };
}
