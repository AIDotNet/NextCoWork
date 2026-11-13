using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Desktop;

/// <summary>
/// 桌面主窗口(参考 Octopus):没有自己的标题栏,内容从窗口顶端开始;macOS 的红绿灯浮在左上角,
/// Windows / Linux 的窗口按钮由外壳画在右上角。顶部 40px 这一行由侧边栏头部、标签栏、检查器页签拼成,
/// 这一行里没有被标签、按钮、输入框处理的按下用来拖动窗口,双击最大化 / 还原。
/// </summary>
public partial class MainWindow : NcwWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Shell.Chrome = WindowChrome.ForCurrentPlatform();
        AddHandler(PointerPressedEvent, OnTopRowPressed, RoutingStrategies.Bubble);
    }

    private void OnTopRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.GetPosition(this).Y >= WindowChrome.TopRowHeight)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else if (WindowState != WindowState.FullScreen)
        {
            BeginMoveDrag(e);
        }

        e.Handled = true;
    }
}
