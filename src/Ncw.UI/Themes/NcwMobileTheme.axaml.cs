using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Ncw.UI.Themes;

/// <summary>手机壳专用控件(长按确认按钮等)的主题。按类型创建,不按 URI 加载 XAML,兼容 AOT。</summary>
public partial class NcwMobileTheme : ResourceDictionary
{
    public NcwMobileTheme() => AvaloniaXamlLoader.Load(this);
}
