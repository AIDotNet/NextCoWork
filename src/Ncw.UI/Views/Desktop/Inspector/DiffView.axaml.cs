using Avalonia.Controls;

namespace Ncw.UI.Views.Desktop.Inspector;

/// <summary>diff 视图(Git 页签和审查页签共用)。纯展示:行模型在 <c>UnifiedDiff</c>,显示方式是 <c>DiffViewModel.Mode</c>。</summary>
public partial class DiffView : UserControl
{
    public DiffView() => InitializeComponent();
}
