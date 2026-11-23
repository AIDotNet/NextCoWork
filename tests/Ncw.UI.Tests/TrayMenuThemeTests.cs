using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;

namespace Ncw.UI.Tests;

/// <summary>
/// Windows 托盘右键菜单(issue #200)。Avalonia.Win32 的 <c>TrayIconImpl.OnRightClicked</c> 弹一个 Window 子类(无边框、SizeToContent、背景 null),
/// 内容是 MenuFlyoutPresenter 子类,条目由 <c>NativeMenuBarPresenter.CreateContainerForNativeItem</c> 生成(NativeMenuItemPresenter / Separator)。
/// 这里按同样的结构搭一份:只有 Arc 主题时这几个控件没有模板,菜单收成 0×0 —— 用户看到的就是「右键没反应、退不出去」。
/// 修复是 <c>Themes/NcwTrayMenuTheme.axaml</c>(随 Ncw.UI 的 Arc 资产包加载,测试宿主也会加载)。
/// </summary>
public class TrayMenuThemeTests
{
    private sealed class TrayPresenter : MenuFlyoutPresenter
    {
        // Avalonia 内部方法;托盘菜单在 Windows 上就是用它生成条目的。升级 Avalonia 后取不到就让测试直接失败,提醒重新核对。
        [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "测试代码,不裁剪")]
        private static readonly MethodInfo CreateNative = typeof(NativeMenu).Assembly.GetType("Avalonia.Controls.NativeMenuBarPresenter")!
            .GetMethod("CreateContainerForNativeItem", BindingFlags.Static | BindingFlags.NonPublic)!;

        protected override Type StyleKeyOverride => typeof(MenuFlyoutPresenter);

        protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
            (Control?)CreateNative.Invoke(null, [item, index, recycleKey]) ?? base.CreateContainerForItemOverride(item, index, recycleKey);
    }

    private sealed class TrayPopupRoot : Window
    {
        public TrayPopupRoot()
        {
            Topmost = true;
            ShowInTaskbar = false;
        }
    }

    [AvaloniaFact]
    public void The_windows_tray_popup_draws_its_items_and_quit_is_clickable()
    {
        var quits = 0;
        var menu = new NativeMenu
        {
            new NativeMenuItem("Open NextCoWork"),
            new NativeMenuItemSeparator(),
            new NativeMenuItem("Quit NextCoWork") { Command = new RelayCommand(() => quits++) },
        };
        var presenter = new TrayPresenter { ItemsSource = menu.Items };
        var popup = new TrayPopupRoot
        {
            WindowDecorations = 0,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = null,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            Content = presenter,
        };

        popup.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(popup.Bounds.Height > 40, $"popup {popup.Bounds.Size}");
        var items = presenter.GetVisualDescendants().OfType<MenuItem>().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.Bounds.Height > 0));
        var quit = items[^1];
        Assert.Contains(quit.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Quit NextCoWork");

        var center = quit.TranslatePoint(new Point(quit.Bounds.Width / 2, quit.Bounds.Height / 2), popup)!.Value;
        popup.MouseDown(center, MouseButton.Left);
        popup.MouseUp(center, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, quits);
    }
}
