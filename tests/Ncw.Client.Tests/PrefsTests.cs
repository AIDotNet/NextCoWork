using System.Collections.Immutable;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;

namespace Ncw.Client.Tests;

public class PrefsTests
{
    private sealed class MemoryRepository(UiPrefs? initial = null) : IPrefsRepository
    {
        public UiPrefs? Saved { get; private set; } = initial;

        public int Saves { get; private set; }

        public UiPrefs? Load() => Saved;

        public void Save(UiPrefs prefs)
        {
            Saved = prefs;
            Saves++;
        }
    }

    [Fact]
    public void Loads_saved_prefs_and_normalizes_bad_values()
    {
        var stores = new ClientStores();
        var repo = new MemoryRepository(new UiPrefs(9999, double.NaN, false, true, [InspectorTabKind.Git, InspectorTabKind.Subagents, InspectorTabKind.Git]));

        using var persistence = new PrefsPersistence(stores, repo);

        var prefs = stores.Prefs.State;
        Assert.Equal(UiPrefs.SidebarMaxWidth, prefs.SidebarWidth);
        Assert.Equal(UiPrefs.InspectorDefaultWidth, prefs.InspectorWidth);
        Assert.False(prefs.IsSidebarOpen);
        Assert.Equal([InspectorTabKind.Git], prefs.InspectorTabs);
    }

    [Fact]
    public void Changes_are_coalesced_and_written_on_flush()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        var repo = new MemoryRepository();
        using var persistence = new PrefsPersistence(stores, repo, TimeSpan.FromHours(1));
        var shell = new ShellViewModel(stores);

        shell.SidebarWidth = 400;
        shell.SidebarWidth = 410;
        shell.Inspector.AddCommand.Execute(InspectorTabKind.Git);
        Assert.Equal(0, repo.Saves);

        persistence.Flush();

        Assert.Equal(1, repo.Saves);
        Assert.Equal(410, repo.Saved!.SidebarWidth);
        Assert.Equal([InspectorTabKind.Git], repo.Saved.InspectorTabs);
    }

    [Fact]
    public void Restored_inspector_tabs_show_up_on_start()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        using var persistence = new PrefsPersistence(stores, new MemoryRepository(UiPrefs.Default with { InspectorTabs = [InspectorTabKind.History] }));

        var shell = new ShellViewModel(stores);

        Assert.Contains(shell.Inspector.Tabs, t => t.Kind == InspectorTabKind.History);
        Assert.True(shell.Inspector.IsFiles);
    }

    [Fact]
    public void Json_file_round_trips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ncw-prefs-{Guid.NewGuid():N}", "ui.json");
        var repo = new JsonFilePrefsRepository(path);
        var prefs = new UiPrefs(400, 300, true, false, ImmutableArray.Create(InspectorTabKind.Context, InspectorTabKind.Git));

        repo.Save(prefs);

        Assert.Equal(prefs, repo.Load());
        Assert.Contains("\"sidebarWidth\": 400", File.ReadAllText(path));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Toggle_sidebar_opens_drawer_when_compact()
    {
        var shell = new ShellViewModel(MockWorkspace.Create(DateTimeOffset.Now));

        shell.IsCompact = true;
        shell.ToggleSidebarCommand.Execute(null);
        Assert.True(shell.IsDrawerOpen);
        Assert.True(shell.IsSidebarOpen); // 抽屉不改宽窗口下的偏好

        shell.IsCompact = false;
        Assert.False(shell.IsDrawerOpen);
    }
}

public class RenameGroupTests
{
    private sealed class ScriptedDialogs(string? answer) : IDialogService
    {
        public string? LastInitial { get; private set; }

        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial)
        {
            LastInitial = initial;
            return Task.FromResult(answer);
        }
    }

    [Theory]
    [InlineData("前端", "前端")]
    [InlineData("  后端  ", "后端")]
    [InlineData(null, "工作")]
    [InlineData("   ", "工作")]
    public async Task Rename_uses_dialog_answer_and_ignores_cancel_or_blank(string? answer, string expected)
    {
        var dialogs = new ScriptedDialogs(answer);
        var shell = new ShellViewModel(MockWorkspace.Create(DateTimeOffset.Now), dialogs: dialogs);
        var group = shell.Rail.Items.OfType<RailGroupViewModel>().First();

        await group.RenameCommand.ExecuteAsync(null);

        Assert.Equal("工作", dialogs.LastInitial);
        Assert.Equal(expected, shell.Rail.Items.OfType<RailGroupViewModel>().First().Name);
    }
}
