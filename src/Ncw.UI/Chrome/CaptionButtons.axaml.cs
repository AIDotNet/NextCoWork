using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Ncw.UI.Chrome;

public partial class CaptionButtons : UserControl
{
    public CaptionButtons() => InitializeComponent();

    private Window? Host => TopLevel.GetTopLevel(this) as Window;

    private void OnMinimize(object? sender, RoutedEventArgs e)
    {
        if (Host is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximize(object? sender, RoutedEventArgs e)
    {
        if (Host is { } window)
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Host?.Close();
}
