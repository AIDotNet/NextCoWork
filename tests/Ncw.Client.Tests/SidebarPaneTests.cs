using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests;

public class SidebarPaneTests
{
    private sealed class Host : ISidebarHost
    {
        public ILocalFiles LocalFiles => NoLocalFiles.Instance;

        public void OpenPalette() { }

        public void OpenScheduledTasks() { }

        public void OpenAttention(InboxEntry entry) { }

        public void NewTerminal(ProjectRef project) { }

        public void NewFile(ProjectRef project) { }

        public void NewFolder(ProjectRef project) { }

        public void OpenSettings() { }
    }

    private static IEnumerable<SidebarProjectViewModel> Projects(SidebarViewModel vm) =>
        vm.Items.SelectMany(i => i switch
        {
            SidebarProjectViewModel p => [p],
            SidebarGroupViewModel g => g.Members,
            _ => Enumerable.Empty<SidebarProjectViewModel>(),
        });

    [Fact]
    public void Collapse_all_projects_keeps_selection_and_section_toggling_does_not_reopen_projects()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        var vm = new SidebarViewModel(stores, new LayoutActions(stores), new Host());
        var selection = stores.Selection.State;
        Assert.Contains(Projects(vm), p => p.IsExpanded);

        vm.CollapseAllProjectsCommand.Execute(null);

        Assert.All(Projects(vm), p => Assert.False(p.IsExpanded));
        Assert.Equal(selection, stores.Selection.State);
        vm.ToggleProjectsSectionCommand.Execute(null);
        Assert.False(vm.IsProjectsSectionOpen);
        vm.ToggleProjectsSectionCommand.Execute(null);
        Assert.True(vm.IsProjectsSectionOpen);
        Assert.All(Projects(vm), p => Assert.False(p.IsExpanded));
        Assert.Null(vm.Files);
    }

    [Fact]
    public void Ratio_reads_the_single_prefs_source_and_reports_external_changes()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.Now);
        var vm = new SidebarViewModel(stores, new LayoutActions(stores), new Host());
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.SidebarFilesRatio = 0.65;
        Assert.Equal(0.65, stores.Prefs.State.SidebarFilesRatio);
        stores.Prefs.Update(p => p with { SidebarFilesRatio = 0.3 });
        Assert.Equal(0.3, vm.SidebarFilesRatio);
        Assert.Contains(nameof(SidebarViewModel.SidebarFilesRatio), changes);

        vm.SidebarFilesRatio = 2;
        Assert.Equal(UiPrefs.SidebarFilesMaxRatio, vm.SidebarFilesRatio);
        vm.SidebarFilesRatio = double.NaN;
        Assert.Equal(UiPrefs.SidebarFilesDefaultRatio, vm.SidebarFilesRatio);
    }

    [Theory]
    [InlineData(-1, 0.2)]
    [InlineData(2, 0.8)]
    [InlineData(double.NaN, 0.48)]
    [InlineData(double.PositiveInfinity, 0.48)]
    [InlineData(0.6, 0.6)]
    public void Ratio_is_normalized_and_participates_in_equality(double input, double expected)
    {
        var normalized = (UiPrefs.Default with { SidebarFilesRatio = input }).Normalize();
        Assert.Equal(expected, normalized.SidebarFilesRatio);
        Assert.NotEqual(UiPrefs.Default, UiPrefs.Default with { SidebarFilesRatio = 0.7 });
    }

    [Fact]
    public void Ratio_round_trips_through_the_backward_compatible_prefs_dto()
    {
        var prefs = UiPrefs.Default with { SidebarFilesRatio = 0.72 };
        Assert.Equal(prefs, UiPrefsDto.From(prefs).ToPrefs());
        Assert.Equal(UiPrefs.SidebarFilesDefaultRatio, new UiPrefsDto(1, 280, 320, true, true, []).ToPrefs().SidebarFilesRatio);
    }
}
