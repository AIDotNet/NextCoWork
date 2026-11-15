using Avalonia.Controls;
using Avalonia.VisualTree;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Converters;
using TKCode.Arc;

namespace Ncw.UI.Views.Desktop;

public partial class RemoteSettingsPage : UserControl
{
    private static readonly string[] Roles = ["owner", "member", "viewer"];

    public RemoteSettingsPage()
    {
        InitializeComponent();
        ControllerList.ContainerPrepared += (_, e) => BuildRoleMenus(e.Container);
    }

    /// <summary>每一行的角色菜单:三个角色,点选即调用同一个 SetRoleCommand。</summary>
    private void BuildRoleMenus(Control container) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not RemoteSettingsViewModel vm)
            {
                return;
            }

            foreach (var menu in container.GetVisualDescendants().OfType<NcwDropdownMenu>())
            {
                if (menu.Tag is not string fingerprint)
                {
                    continue;
                }

                menu.ItemsSource = Roles
                    .Select(role => new MenuEntry
                    {
                        Header = RoleTextConverter.Text(role),
                        Command = vm.SetRoleCommand,
                        CommandParameter = new RoleChange(fingerprint, role),
                    })
                    .ToList();
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
}
