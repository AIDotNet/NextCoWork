using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Ncw.Client.Mock;
using Ncw.Client.ViewModels;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Inspector;

namespace Ncw.UI.Tests;

/// <summary>按需创建(docs/design/03 §12):看不见的整页内容不建控件,显示时才建;设置页关掉后释放。</summary>
public class DeferredContentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(8));

    private static void Dispatch()
    {
        for (var i = 0; i < 3; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static (Window Window, DesktopShell Shell, ShellViewModel Vm) RenderShell()
    {
        var vm = new ShellViewModel(MockWorkspace.Create(Now));
        var shell = new DesktopShell { DataContext = vm, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = 1500, Height = 860, Content = shell };
        window.Show();
        Dispatch();
        return (window, shell, vm);
    }

    [AvaloniaFact]
    public void Content_is_built_on_first_display_including_when_an_ancestor_becomes_visible()
    {
        var builds = 0;
        var host = new NcwDeferred { Template = new FuncDataTemplate<object?>((_, _) => { builds++; return new TextBlock { Text = "page" }; }) };
        var parent = new Panel { IsVisible = false, Children = { host } };
        var window = new Window { Width = 400, Height = 300, Content = parent };
        window.Show();
        Dispatch();
        Assert.False(host.IsRealized);   // 祖先看不见:不建

        parent.IsVisible = true;
        Dispatch();
        Assert.True(host.IsRealized);
        Assert.Equal(1, builds);

        host.IsVisible = false;
        Dispatch();
        host.IsVisible = true;
        Dispatch();
        Assert.Equal(1, builds);         // 建过就留着,不重复建

        host.Release();
        Assert.False(host.IsRealized);
        Dispatch();
        Assert.True(host.IsRealized);    // 释放后再显示时重建
        Assert.Equal(2, builds);
    }

    [AvaloniaFact]
    public void Settings_pages_are_built_when_shown_all_built_for_search_and_released_after_closing()
    {
        var (window, _, vm) = RenderShell();
        var overlay = window.GetVisualDescendants().OfType<SettingsOverlay>().Single();
        Assert.Empty(overlay.RealizedSections);   // 设置没打开:一个分页都不建

        vm.OpenSettingsSection(SettingsSection.Appearance);
        Dispatch();
        Assert.Equal([SettingsSection.Appearance], overlay.RealizedSections);
        Assert.Single(overlay.GetLogicalDescendants().OfType<AppearanceSettingsPage>());

        vm.Settings.ShowCommand.Execute(SettingsSection.About);
        Dispatch();
        Assert.Equal([SettingsSection.About, SettingsSection.Appearance], overlay.RealizedSections.Order().ToArray());

        // 搜索要收集所有分区的文字:一次建出全部
        vm.Settings.SearchQuery = "x";
        Dispatch();
        Assert.Equal(Enum.GetValues<SettingsSection>().Length, overlay.RealizedSections.Count);

        vm.Settings.CloseCommand.Execute(null);
        Dispatch();
        Assert.Empty(overlay.RealizedSections);
        Assert.Empty(overlay.GetLogicalDescendants().OfType<AppearanceSettingsPage>());

        // 再打开:照常显示
        vm.OpenSettingsSection(SettingsSection.About);
        Dispatch();
        Assert.Equal([SettingsSection.About], overlay.RealizedSections);
        Assert.Equal(SettingsSection.About, overlay.SelectedSection);
    }

    [AvaloniaFact]
    public void Released_settings_pages_are_collected_so_reopening_settings_does_not_accumulate_pages()
    {
        // 释放后还有谁(ViewModel 的事件、静态事件)拿着页面,每开关一次设置就漏一整套页面——比改之前常驻还糟
        var (window, _, vm) = RenderShell();
        var overlay = window.GetVisualDescendants().OfType<SettingsOverlay>().Single();
        var pages = OpenSearchAndClose(vm, overlay);
        Assert.Equal(Enum.GetValues<SettingsSection>().Length, pages.Count);

        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Dispatch();
        }

        Assert.Empty(pages.Where(p => p.Reference.TryGetTarget(out _)).Select(p => p.Name));
        GC.KeepAlive(window);
    }

    /// <summary>打开设置、搜索(建出全部分页)、关掉;返回被释放的各分页的弱引用。单独一个方法,免得局部变量把页面留在栈上。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static List<(string Name, WeakReference<Control> Reference)> OpenSearchAndClose(ShellViewModel vm, SettingsOverlay overlay)
    {
        vm.OpenSettingsSection(SettingsSection.Account);
        Dispatch();
        vm.Settings.SearchQuery = "x";
        Dispatch();
        var pages = overlay.GetLogicalDescendants().OfType<NcwDeferred>().Where(h => h.IsRealized)
            .Select(h => (h.Child!.GetType().Name, new WeakReference<Control>(h.Child!))).ToList();
        vm.Settings.CloseCommand.Execute(null);
        Dispatch();
        return pages;
    }

    [AvaloniaFact]
    public void Inspector_pages_are_built_the_first_time_they_are_shown_and_then_kept()
    {
        var (window, _, vm) = RenderShell();
        var inspector = window.GetVisualDescendants().OfType<InspectorPanel>().Single();
        Assert.Single(inspector.GetLogicalDescendants().OfType<FilesPane>());
        Assert.Empty(inspector.GetLogicalDescendants().OfType<GitPane>());
        Assert.Empty(inspector.GetLogicalDescendants().OfType<TerminalPane>());

        vm.Inspector.AddCommand.Execute(InspectorTabKind.Git);
        Dispatch();
        Assert.Single(inspector.GetLogicalDescendants().OfType<GitPane>());

        vm.Inspector.ActivateCommand.Execute(InspectorTabKind.Files);
        Dispatch();
        Assert.Single(inspector.GetLogicalDescendants().OfType<GitPane>());   // 切走不释放(界面状态要留着)
        Assert.Empty(inspector.GetLogicalDescendants().OfType<TerminalPane>());
    }
}
