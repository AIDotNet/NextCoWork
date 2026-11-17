using System.Collections.Immutable;
using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Shortcuts;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests;

public class ShortcutCatalogTests
{
    [Fact]
    public void Defaults_use_cmd_on_mac_and_ctrl_elsewhere()
    {
        var mac = ShortcutCatalog.Resolve(null, mac: true).Single(b => b.Command == AppCommand.CommandPalette);
        var other = ShortcutCatalog.Resolve(null, mac: false).Single(b => b.Command == AppCommand.CommandPalette);

        Assert.Equal("Meta+K", mac.Gesture);
        Assert.Equal("Ctrl+K", other.Gesture);
        Assert.False(mac.IsCustom);
    }

    [Fact]
    public void Default_gestures_do_not_collide()
    {
        foreach (var mac in new[] { true, false })
        {
            var gestures = ShortcutCatalog.Resolve(null, mac).Where(b => b.Gesture is not null).Select(b => b.Gesture).ToList();
            Assert.Equal(gestures.Count, gestures.Distinct().Count());
        }
    }

    [Fact]
    public void Every_command_resolves_in_declaration_order()
    {
        var bindings = ShortcutCatalog.Resolve(null, mac: true);
        Assert.Equal(Enum.GetValues<AppCommand>(), bindings.Select(b => b.Command));
    }

    [Theory]
    [InlineData("meta+shift+k", "Shift+Meta+K")]
    [InlineData("K+Ctrl", "Ctrl+K")]
    [InlineData("Cmd + Alt + I", "Alt+Meta+I")]
    [InlineData("", "")]
    public void Normalize_orders_modifiers(string input, string expected) => Assert.Equal(expected, ShortcutCatalog.Normalize(input));

    [Theory]
    [InlineData("Meta+K", true)]
    [InlineData("Ctrl+Shift+P", true)]
    [InlineData("Shift+K", false)]
    [InlineData("K", false)]
    [InlineData("F5", true)]
    [InlineData("Meta", false)]
    public void Usable_gestures_need_a_real_modifier(string gesture, bool usable) => Assert.Equal(usable, ShortcutCatalog.IsUsable(gesture));

    [Fact]
    public void Assigning_a_taken_gesture_clears_the_other_command()
    {
        var overrides = ShortcutCatalog.Assign(ImmutableDictionary<AppCommand, string>.Empty, AppCommand.OpenTerminal, "Meta+K", mac: true);
        var bindings = ShortcutCatalog.Resolve(overrides, mac: true);

        Assert.Equal("Meta+K", bindings.Single(b => b.Command == AppCommand.OpenTerminal).Gesture);
        var palette = bindings.Single(b => b.Command == AppCommand.CommandPalette);
        Assert.Null(palette.Gesture);
        Assert.True(palette.IsCustom); // 被用户明确清空,不是让出
    }

    [Fact]
    public void Assigning_the_default_back_drops_the_override()
    {
        var overrides = ShortcutCatalog.Assign(ImmutableDictionary<AppCommand, string>.Empty, AppCommand.NewSession, "Meta+Shift+N", mac: true);
        Assert.True(overrides.ContainsKey(AppCommand.NewSession));

        overrides = ShortcutCatalog.Assign(overrides, AppCommand.NewSession, "Meta+N", mac: true);
        Assert.False(overrides.ContainsKey(AppCommand.NewSession));

        overrides = ShortcutCatalog.Assign(overrides, AppCommand.NewSession, "", mac: true);
        Assert.Null(ShortcutCatalog.Resolve(overrides, mac: true).Single(b => b.Command == AppCommand.NewSession).Gesture);

        overrides = ShortcutCatalog.Assign(overrides, AppCommand.NewSession, null, mac: true);
        Assert.Equal("Meta+N", ShortcutCatalog.Resolve(overrides, mac: true).Single(b => b.Command == AppCommand.NewSession).Gesture);
    }

    [Fact]
    public void Prefs_round_trip_shortcuts_and_attention_and_drop_unknown_keys()
    {
        var prefs = UiPrefs.Default with
        {
            Shortcuts = ImmutableDictionary<AppCommand, string>.Empty.Add(AppCommand.OpenGit, "Meta+Shift+H").Add(AppCommand.NewSession, ""),
            Attention = new AttentionPrefs(NotifyCompleted: false, Tray: false),
        };

        var path = Path.Combine(Path.GetTempPath(), $"ncw-prefs-{Guid.NewGuid():N}.json");
        try
        {
            var repo = new JsonFilePrefsRepository(path);
            repo.Save(prefs);
            // 文件里多出一个不认识的命令名:丢掉,其余照常
            File.WriteAllText(path, File.ReadAllText(path).Replace("\"OpenGit\":", "\"NoSuchCommand\": \"Meta+Q\", \"OpenGit\":", StringComparison.Ordinal));
            var back = repo.Load();
            var restored = back!.Normalize();
            Assert.Equal("Shift+Meta+H", restored.ShortcutOverrides[AppCommand.OpenGit]);
            Assert.Equal("", restored.ShortcutOverrides[AppCommand.NewSession]);
            Assert.Equal(2, restored.ShortcutOverrides.Count);
            Assert.False(restored.AttentionOrDefault.NotifyCompleted);
            Assert.False(restored.AttentionOrDefault.Tray);
            Assert.True(restored.AttentionOrDefault.NotifyPending);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Normalize_drops_unusable_overrides()
    {
        var prefs = UiPrefs.Default with { Shortcuts = ImmutableDictionary<AppCommand, string>.Empty.Add(AppCommand.OpenGit, "Shift+G") };
        Assert.Empty(prefs.Normalize().ShortcutOverrides);
    }
}

public class ShortcutsSettingsTests
{
    [Fact]
    public void Recording_sets_override_and_reports_displaced_command()
    {
        var stores = new ClientStores();
        var vm = new ShortcutsSettingsViewModel(stores, () => true);
        var terminal = vm.Rows.Single(r => r.Command == AppCommand.OpenTerminal);
        Assert.Null(terminal.Gesture);

        vm.StartRecordingCommand.Execute(terminal);
        Assert.True(terminal.IsRecording);
        Assert.False(vm.Record("Shift+T"));   // 只有 Shift:不能用,继续录
        Assert.True(vm.LastRejected);
        Assert.True(terminal.IsRecording);

        Assert.True(vm.Record("Meta+J"));
        Assert.False(terminal.IsRecording);
        Assert.Equal("Meta+J", terminal.Gesture);
        Assert.True(terminal.IsCustom);
        Assert.Equal(AppCommand.NextAttention, vm.Displaced);
        Assert.Null(vm.Rows.Single(r => r.Command == AppCommand.NextAttention).Gesture);
        Assert.True(vm.HasCustom);

        vm.ResetAllCommand.Execute(null);
        Assert.Null(terminal.Gesture);
        Assert.Equal("Meta+J", vm.Rows.Single(r => r.Command == AppCommand.NextAttention).Gesture);
        Assert.False(vm.HasCustom);
    }
}

public class CommandPaletteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Empty_query_lists_commands_then_sessions_then_projects()
    {
        var stores = MockWorkspace.Create(Now);
        var palette = new CommandPaletteViewModel(stores, isMac: () => true, fileDelay: TimeSpan.Zero);

        palette.Open();

        Assert.True(palette.IsOpen);
        Assert.Equal(PaletteItemKind.Command, palette.Items[0].Kind);
        Assert.True(palette.Items[0].IsActive);
        Assert.DoesNotContain(palette.Items, i => i.Command == AppCommand.CommandPalette);
        Assert.DoesNotContain(palette.Items, i => i.Kind == PaletteItemKind.Settings);
        var kinds = palette.Items.Select(i => i.Kind).ToList();
        Assert.Equal(kinds.OrderBy(k => k), kinds);
        Assert.Contains(palette.Items, i => i.Kind == PaletteItemKind.Session);
        Assert.Equal("Meta+N", palette.Items.Single(i => i.Command == AppCommand.NewSession).Gesture);
    }

    [Fact]
    public void Query_filters_with_fuzzy_ranking_and_reaches_settings()
    {
        var stores = MockWorkspace.Create(Now);
        var palette = new CommandPaletteViewModel(stores, isMac: () => true, fileDelay: TimeSpan.Zero)
        {
            SectionTitle = s => s == SettingsSection.Shortcuts ? "Keyboard shortcuts" : s.ToString(),
        };
        palette.Open();

        palette.Query = "keyboard";

        var first = palette.Items[0];
        Assert.Equal(PaletteItemKind.Settings, first.Kind);
        Assert.Equal(SettingsSection.Shortcuts, first.Section);

        palette.Query = "zzzzqqq";
        Assert.Empty(palette.Items);
        Assert.True(palette.ShowsNoMatch);
    }

    [Fact]
    public void Move_wraps_and_accept_closes_and_invokes()
    {
        var stores = MockWorkspace.Create(Now);
        var palette = new CommandPaletteViewModel(stores, isMac: () => true, fileDelay: TimeSpan.Zero);
        PaletteItemViewModel? invoked = null;
        palette.Invoked += i => invoked = i;
        palette.Open();

        palette.Move(-1);
        Assert.Same(palette.Items[^1], palette.Active);
        palette.Move(1);
        Assert.Same(palette.Items[0], palette.Active);

        palette.Accept();
        Assert.False(palette.IsOpen);
        Assert.Same(palette.Items[0], invoked);
    }

    [Fact]
    public async Task Files_come_from_the_device_after_two_characters()
    {
        var stores = MockWorkspace.Create(Now);
        var files = new Inspector.FakeFiles().Add("src/Shell.cs").Add("docs/readme.md");
        var palette = new CommandPaletteViewModel(stores, files, () => true, TimeSpan.Zero);
        palette.Open();

        palette.Query = "s";
        Assert.Null(files.LastSuggestQuery);

        palette.Query = "shell";
        for (var i = 0; i < 50 && palette.IsSearchingFiles; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("shell", files.LastSuggestQuery);

        Assert.Contains(palette.Items, i => i.Kind == PaletteItemKind.File && i.Path == "src/Shell.cs");
        Assert.False(palette.IsSearchingFiles);
    }

    [Fact]
    public void Shell_executes_palette_choices_through_the_single_intents()
    {
        var stores = MockWorkspace.Create(Now);
        var shell = new ShellViewModel(stores);
        var first = stores.Layout.State.Rail.Projects().First();
        var second = stores.Layout.State.Rail.Projects().Skip(1).First();

        Assert.True(shell.Execute(AppCommand.SelectProject2));
        Assert.Equal(second, stores.Selection.State.Project);

        // ⌃Tab:回到上一个项目,再按一次又回来
        Assert.True(shell.Execute(AppCommand.RecentProject));
        Assert.Equal(first, stores.Selection.State.Project);
        Assert.True(shell.Execute(AppCommand.RecentProject));
        Assert.Equal(second, stores.Selection.State.Project);

        Assert.True(shell.Execute(AppCommand.CommandPalette));
        Assert.True(shell.Palette.IsOpen);
        var settings = shell.Palette.Items.First(i => i.Kind == PaletteItemKind.Command && i.Command == AppCommand.OpenSettings);
        shell.Palette.Accept(settings);
        Assert.True(shell.Settings.IsOpen);

        // 设置盖着时,别的快捷键不动作;⌘, 关掉设置
        Assert.False(shell.Execute(AppCommand.ToggleSidebar));
        Assert.True(shell.Execute(AppCommand.OpenSettings));
        Assert.False(shell.Settings.IsOpen);

        Assert.True(shell.Execute(AppCommand.OpenReview));
        Assert.Equal(InspectorTabKind.Review, shell.Inspector.Active);
    }

    [Fact]
    public void Approve_first_and_stop_do_nothing_when_there_is_nothing_to_do()
    {
        var shell = new ShellViewModel(new ClientStores());
        Assert.False(shell.Execute(AppCommand.ApproveFirst));
        Assert.False(shell.Execute(AppCommand.StopRun));
        Assert.False(shell.Execute(AppCommand.NextAttention));
        Assert.False(shell.Execute(AppCommand.RecentProject));
    }
}
