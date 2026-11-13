using Avalonia;
using Avalonia.Controls;

namespace Ncw.UI.Views.Desktop;

public partial class InspectorPanel : UserControl
{
    public InspectorPanel() => InitializeComponent();

    /// <summary>
    /// 页签行右端让出右上角常驻按钮的宽度。Windows / Linux 上右上角还有窗口按钮,页签只留图标。
    /// </summary>
    public void SetTopRowTrailing(double trailing, bool compact)
    {
        HeaderRow.Margin = new Thickness(8, 0, 8 + trailing, 0);
        OverviewLabel.IsVisible = !compact;
    }
}
