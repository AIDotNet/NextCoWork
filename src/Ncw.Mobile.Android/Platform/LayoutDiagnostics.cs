using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>开发诊断:把根视图尺寸和平台安全区写进 logcat(tag <c>NcwLayout</c>),模拟器上核对安全区 / 软键盘 / 旋转时用。不产生界面文字,不含任何用户数据。</summary>
internal static class LayoutDiagnostics
{
    public static void Log(string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not ISingleViewApplicationLifetime { MainView: { } view })
            {
                return;
            }

            var top = TopLevel.GetTopLevel(view);
            var insets = top?.InsetsManager;
            global::Android.Util.Log.Info("NcwLayout",
                $"{reason}: view={view.Bounds.Width:F0}x{view.Bounds.Height:F0} top={top?.Bounds.Width:F0}x{top?.Bounds.Height:F0} scaling={top?.RenderScaling:F2} " +
                $"safeArea={insets?.SafeAreaPadding} edgeToEdge={insets?.DisplayEdgeToEdgePreference} imeHeight={insets?.IsSystemBarVisible}");
            var names = Avalonia.Media.FontManager.Current.SystemFonts.Select(f => f.Name).ToArray();
            var matches = string.Join("|", names.Where(n => n.Contains("CJK", StringComparison.OrdinalIgnoreCase) || n.Contains("Han", StringComparison.OrdinalIgnoreCase) || n.Contains("sans-serif", StringComparison.OrdinalIgnoreCase)));
            global::Android.Util.Log.Info("NcwLayout", $"fonts={names.Length} default={Avalonia.Media.FontManager.Current.DefaultFontFamily.Name} cjk=[{matches}]");
            if (System.Environment.GetEnvironmentVariable("NCW_LAYOUT_TREE") == "1" || reason.EndsWith("tree", StringComparison.Ordinal))
            {
                Dump(view, 0);
            }
        });
    }

    private static void Dump(Avalonia.Visual visual, int depth)
    {
        if (depth > 9)
        {
            return;
        }

        if (visual is Control control && control.Bounds.Height > 0)
        {
            global::Android.Util.Log.Info("NcwLayout", $"{new string(' ', depth * 2)}{control.GetType().Name} {control.Bounds.X:F0},{control.Bounds.Y:F0} {control.Bounds.Width:F0}x{control.Bounds.Height:F0} margin={control.Margin}");
        }

        if (visual is TKCode.Arc.Mobile.Page page)
        {
            global::Android.Util.Log.Info("NcwLayout", $"{new string(' ', depth * 2)}  Page safeArea={page.SafeArea} usePlatform={page.UsePlatformSafeArea} keyboardInset={page.KeyboardInset} avoidKeyboard={page.AvoidKeyboard}");
        }

        foreach (var child in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(visual))
        {
            Dump(child, depth + 1);
        }
    }

    /// <summary>字体回退实验(开发用,<c>adb shell am start -n … --ez fonttest true</c>):用不同的字体族 / 字重画同一句中文。</summary>
    public static void FontTest()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is not ISingleViewApplicationLifetime single)
            {
                return;
            }

            var panel = new StackPanel { Margin = new Thickness(8, 60, 8, 8), Spacing = 4, Background = Avalonia.Media.Brushes.Black };
            foreach (var family in new[] { "", "Noto Sans CJK SC" })
            {
                foreach (var weight in new[] { (Avalonia.Media.FontWeight)350, (Avalonia.Media.FontWeight)399, Avalonia.Media.FontWeight.Normal, (Avalonia.Media.FontWeight)401, (Avalonia.Media.FontWeight)450 })
                {
                    var block = new TextBlock { Text = $"{family}/{(int)weight} 还没有设备 Abc", Foreground = Avalonia.Media.Brushes.White, FontSize = 14, FontWeight = weight };
                    if (family.Length > 0)
                    {
                        block.FontFamily = new Avalonia.Media.FontFamily(family);
                    }

                    panel.Children.Add(block);
                }
            }

            if (single.MainView is ContentControl host)
            {
                host.Content = panel;
            }
        });
    }
}
