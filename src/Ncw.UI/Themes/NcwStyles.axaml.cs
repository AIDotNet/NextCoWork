using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Ncw.UI.Themes;

/// <summary>NextCoWork 的共享样式。按类型创建,兼容 AOT。</summary>
public partial class NcwStyles : Styles
{
    public NcwStyles() => AvaloniaXamlLoader.Load(this);
}
