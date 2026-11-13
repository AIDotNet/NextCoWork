using Avalonia;
using Avalonia.Controls;

namespace Ncw.UI.Views.Desktop;

public partial class WorkspaceArea : UserControl
{
    public WorkspaceArea() => InitializeComponent();

    /// <summary>
    /// 顶部这一行左右的留白:左边让给收起后并入的侧边栏头部,右边让给右上角常驻按钮(检查器收起时)。
    /// </summary>
    public void SetTopRowInsets(double leading, double trailing) =>
        TabRow.Margin = new Thickness(8 + leading, 0, 8 + trailing, 0);
}
