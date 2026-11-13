using Avalonia.Markup.Xaml;
using Avalonia.Controls;

namespace Ncw.UI.Themes;

/// <summary>NextCoWork 自己的控件主题(PlainButton 等)。按类型创建,不按 URI 加载 XAML,兼容 AOT。</summary>
public partial class NcwTheme : ResourceDictionary
{
    public NcwTheme() => AvaloniaXamlLoader.Load(this);
}
