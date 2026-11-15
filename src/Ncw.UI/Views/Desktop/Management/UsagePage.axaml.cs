using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Converters;

namespace Ncw.UI.Views.Desktop.Management;

/// <summary>用量页。图表的轴文字格式(日期、紧凑数字)是纯显示,在这里给 Arc 图表的格式化函数(AOT 安全,不用反射绑定)。</summary>
public partial class UsagePage : UserControl
{
    public UsagePage()
    {
        InitializeComponent();
        DailyChart.XAxisFormatter = x => x is DateOnly d ? d.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;
        DailyChart.XLabelFormatter = x => x is DateOnly d ? d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "";
        DailyChart.TickFormatter = v => UsageView.Compact((long)v);
        DailyChart.ValueFormat = "N0";
    }
}
