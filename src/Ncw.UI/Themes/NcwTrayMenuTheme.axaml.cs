using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ncw.UI.Themes;

/// <summary>Windows 托盘右键菜单用到的 Avalonia 自带控件(MenuFlyoutPresenter / MenuItem / Separator)的模板(见 axaml 里的说明)。按类型创建,兼容 AOT。</summary>
public partial class NcwTrayMenuTheme : ResourceDictionary
{
    public NcwTrayMenuTheme() => AvaloniaXamlLoader.Load(this);
}
