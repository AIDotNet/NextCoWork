using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;

namespace Ncw.Client.Tests;

/// <summary>「动效」本应用设置(docs/design/03 §9.3):级别的解析、偏好的持久化与旧文件兼容、「外观」页的读写。</summary>
public class UiMotionTests
{
    [Theory]
    [InlineData(UiMotionPreference.System, null, UiMotionLevel.Full)]      // 读不到系统设置:按完整(外观页如实说明)
    [InlineData(UiMotionPreference.System, false, UiMotionLevel.Full)]
    [InlineData(UiMotionPreference.System, true, UiMotionLevel.Reduced)]   // 系统要求减少动态效果
    [InlineData(UiMotionPreference.Full, true, UiMotionLevel.Full)]        // 用户明确选择压过系统
    [InlineData(UiMotionPreference.Reduced, false, UiMotionLevel.Reduced)]
    [InlineData(UiMotionPreference.Off, null, UiMotionLevel.Off)]
    [InlineData(UiMotionPreference.Off, true, UiMotionLevel.Off)]
    public void Resolve_combines_the_user_choice_with_the_system_setting(UiMotionPreference preference, bool? system, UiMotionLevel expected) =>
        Assert.Equal(expected, UiMotion.Resolve(preference, system));

    [Fact]
    public void Default_prefs_follow_the_system()
    {
        Assert.Equal(UiMotionPreference.System, UiPrefs.Default.Motion);
        Assert.Equal(UiMotionPreference.System, new UiPrefs(300, 300, true, true, []).Motion); // 旧的构造调用(没有第六个参数)不受影响
    }

    [Fact]
    public void Prefs_equality_notices_a_motion_change_so_the_store_broadcasts_it()
    {
        Assert.NotEqual(UiPrefs.Default, UiPrefs.Default with { Motion = UiMotionPreference.Off });
        Assert.Equal(UiPrefs.Default with { Motion = UiMotionPreference.Reduced }, UiPrefs.Default with { Motion = UiMotionPreference.Reduced });
    }

    [Fact]
    public void Normalize_replaces_an_unknown_motion_value()
    {
        var bad = UiPrefs.Default with { Motion = (UiMotionPreference)99 };

        Assert.Equal(UiMotionPreference.System, bad.Normalize().Motion);
    }

    [Theory]
    [InlineData(UiMotionPreference.System)]
    [InlineData(UiMotionPreference.Full)]
    [InlineData(UiMotionPreference.Reduced)]
    [InlineData(UiMotionPreference.Off)]
    public void Json_file_round_trips_the_motion_preference(UiMotionPreference preference)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ncw-prefs-{Guid.NewGuid():N}", "ui.json");
        var repo = new JsonFilePrefsRepository(path);

        repo.Save(UiPrefs.Default with { Motion = preference });

        Assert.Equal(preference, repo.Load()!.Motion);
        Assert.Contains($"\"motion\": \"{preference}\"", File.ReadAllText(path));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void A_file_written_before_the_motion_setting_existed_loads_as_follow_system()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ncw-prefs-{Guid.NewGuid():N}", "ui.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "version": 1, "sidebarWidth": 400, "inspectorWidth": 300, "isSidebarOpen": true, "isInspectorOpen": false, "inspectorTabs": [] }""");

        var prefs = new JsonFilePrefsRepository(path).Load()!;

        Assert.Equal(400, prefs.SidebarWidth);
        Assert.Equal(UiMotionPreference.System, prefs.Motion);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void A_hand_edited_unknown_motion_value_falls_back_to_follow_system()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ncw-prefs-{Guid.NewGuid():N}", "ui.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "version": 1, "sidebarWidth": 352, "inspectorWidth": 320, "isSidebarOpen": true, "isInspectorOpen": true, "inspectorTabs": [], "motion": "Wobbly" }""");

        Assert.Equal(UiMotionPreference.System, new JsonFilePrefsRepository(path).Load()!.Motion);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void The_appearance_page_reads_and_writes_the_single_prefs_store()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        var page = new AppearanceViewModel(stores);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        page.Motion = UiMotionPreference.Reduced;

        Assert.Equal(UiMotionPreference.Reduced, stores.Prefs.State.Motion);          // 真源是 Prefs Store
        Assert.Contains(nameof(AppearanceViewModel.Motion), changed);
        Assert.Contains(nameof(AppearanceViewModel.EffectiveLevel), changed);
        Assert.Equal(UiMotionLevel.Reduced, page.EffectiveLevel);

        stores.Prefs.Update(p => p with { Motion = UiMotionPreference.Off });         // 别处改了,页面跟随(不缓存)
        Assert.Equal(UiMotionPreference.Off, page.Motion);
    }

    [Fact]
    public void Follow_system_uses_the_injected_probe_and_says_so_when_the_platform_cannot_tell()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        var page = new AppearanceViewModel(stores);

        Assert.True(page.SystemUnknown);                 // 默认探测读不到
        Assert.Equal(UiMotionLevel.Full, page.EffectiveLevel);

        page.SystemReducesMotion = () => true;
        Assert.False(page.SystemUnknown);
        Assert.Equal(UiMotionLevel.Reduced, page.EffectiveLevel);

        page.Motion = UiMotionPreference.Full;           // 明确选择压过系统
        Assert.Equal(UiMotionLevel.Full, page.EffectiveLevel);
        Assert.False(page.SystemUnknown);
    }

    [Fact]
    public void Appearance_is_a_phone_settings_entry_and_a_desktop_section()
    {
        Assert.Contains(SettingsSection.Appearance, MobileSettingsMenu.App);
        var shell = new ShellViewModel(MockWorkspace.Create(DateTimeOffset.Now));

        shell.Settings.ShowCommand.Execute(SettingsSection.Appearance);

        Assert.True(shell.Settings.IsAppearanceSection);
    }
}
