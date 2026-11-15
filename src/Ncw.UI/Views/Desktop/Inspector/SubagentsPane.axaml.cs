using Avalonia.Controls;

namespace Ncw.UI.Views.Desktop.Inspector;

/// <summary>子代理页签的视图。纯展示:卡片、转录、停止 / 点开 / 返回都是 <c>InspectorViewModel</c> 的命令。</summary>
public partial class SubagentsPane : UserControl
{
    public SubagentsPane() => InitializeComponent();
}
