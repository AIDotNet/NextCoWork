using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Mobile;

/// <summary>手机壳的导航状态:每个标签各一个页面栈、会话页隐藏标签栏、栈变化通知视图、设置分组清单。</summary>
public class MobileNavigationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly ProjectRef Ncw = new("local", "nextcowork");

    private static MobileNavigationViewModel Make(out ClientStores stores)
    {
        stores = MockWorkspace.Create(Now);
        return new MobileNavigationViewModel(layout: new LayoutActions(stores));
    }

    [Fact]
    public void The_first_tab_is_the_inbox_and_nothing_is_pushed()
    {
        var nav = Make(out _);

        Assert.Equal(MobileTab.Inbox, nav.SelectedTab);
        Assert.Empty(nav.CurrentStack);
        Assert.False(nav.CanGoBack);
        Assert.True(nav.IsTabBarVisible);
    }

    [Fact]
    public void Each_tab_keeps_its_own_stack_when_you_switch_away_and_back()
    {
        var nav = Make(out _);
        nav.OpenProject(Ncw);
        nav.SelectTabCommand.Execute(MobileTab.Settings);
        nav.OpenSettingsSection(SettingsSection.Models);

        nav.SelectTabCommand.Execute(MobileTab.Sessions);

        Assert.Equal([new ProjectRoute(Ncw)], nav.CurrentStack);
        Assert.Equal([new SettingsRoute(SettingsSection.Models)], nav.StackOf(MobileTab.Settings));
    }

    [Fact]
    public void Tapping_the_selected_tab_again_returns_to_its_root()
    {
        var nav = Make(out _);
        nav.SelectTabCommand.Execute(MobileTab.Settings);
        nav.OpenSettingsSection(SettingsSection.Usage);

        nav.SelectTabCommand.Execute(MobileTab.Settings);

        Assert.Empty(nav.CurrentStack);
    }

    [Fact]
    public void Opening_a_project_selects_it_and_opening_a_session_stacks_project_then_conversation_without_a_tab_bar()
    {
        var nav = Make(out var stores);

        nav.OpenProject(Ncw);
        Assert.Equal(Ncw, stores.Selection.State.Project);
        Assert.True(nav.IsTabBarVisible);

        nav.OpenSession(Ncw, "s-ncw-2");

        Assert.Equal([new ProjectRoute(Ncw), new SessionRoute(Ncw, "s-ncw-2")], nav.CurrentStack);
        Assert.False(nav.IsTabBarVisible);
        Assert.IsType<SessionRoute>(nav.Top);

        Assert.True(nav.Pop()); // 返回项目的会话列表,标签栏回来
        Assert.True(nav.IsTabBarVisible);
        Assert.True(nav.Pop());
        Assert.False(nav.Pop());
    }

    [Fact]
    public void Stack_changes_are_announced_per_tab_so_the_view_can_sync_its_navigation_stack()
    {
        var nav = Make(out _);
        var changed = new List<MobileTab>();
        nav.StackChanged += changed.Add;

        nav.SelectTabCommand.Execute(MobileTab.Devices);
        nav.OpenDevicesPage(new QrPairRoute());
        nav.Pop();

        Assert.Equal([MobileTab.Devices, MobileTab.Devices, MobileTab.Devices], changed);
    }

    [Fact]
    public void A_swipe_back_that_already_happened_in_the_view_pops_the_state_without_asking_the_view_to_pop_again()
    {
        var nav = Make(out _);
        nav.OpenProject(Ncw);
        var changed = 0;
        nav.StackChanged += _ => changed++;

        nav.PopSilently(MobileTab.Sessions);

        Assert.Empty(nav.StackOf(MobileTab.Sessions));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Opening_a_settings_section_hands_the_section_to_the_settings_view_model()
    {
        var stores = MockWorkspace.Create(Now);
        var shell = new ShellViewModel(stores);

        shell.Navigation.OpenSettingsSection(SettingsSection.Hooks);

        Assert.Equal(MobileTab.Settings, shell.Navigation.SelectedTab);
        Assert.Equal(SettingsSection.Hooks, shell.Settings.Section);
        Assert.False(shell.Settings.IsOpen); // 手机没有整窗覆盖的设置层,打开与否是桌面外壳的事
    }

    [Fact]
    public void The_phone_settings_menu_lists_only_pages_that_make_sense_without_a_local_agent()
    {
        var all = MobileSettingsMenu.All.ToList();

        Assert.DoesNotContain(SettingsSection.Remote, all);
        Assert.DoesNotContain(SettingsSection.Gateway, all);
        Assert.Contains(SettingsSection.Devices, all);
        Assert.Contains(SettingsSection.Models, all);
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
