using Ncw.Client.Actions;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Inspector;

/// <summary>临时预览、固定标签与路径段导航都是工作区的纯 UI 状态。</summary>
public class WorkspacePreviewTabsTests
{
    private static readonly ProjectRef Project = new("local", "p1");

    private sealed class DelayedFiles(FakeFiles files) : IFilesCommands
    {
        public TaskCompletionSource<ActionResult<FileContentDto>> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ActionResult<FileContentDto>> ReadAsync(string deviceId, string projectId, string path, CancellationToken ct = default) =>
            path == "src/App.cs" ? Pending.Task : files.ReadAsync(deviceId, projectId, path, ct);

        public Task<ActionResult<FilesListResult>> ListAsync(string deviceId, string projectId, string path = "", string sort = "name", bool hideIgnored = false, int offset = 0, int limit = 0, CancellationToken ct = default) =>
            files.ListAsync(deviceId, projectId, path, sort, hideIgnored, offset, limit, ct);

        public Task<ActionResult<FilesRangeResult>> ReadRangeAsync(string deviceId, string projectId, string path, long offset, int length, string? revision = null, CancellationToken ct = default) =>
            files.ReadRangeAsync(deviceId, projectId, path, offset, length, revision, ct);

        public Task<ActionResult<FilesWriteResult>> WriteTextAsync(string deviceId, string projectId, string path, string content, string revision, CancellationToken ct = default) =>
            files.WriteTextAsync(deviceId, projectId, path, content, revision, ct);

        public Task<ActionResult<FilesMutateResult>> CreateFileAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => files.CreateFileAsync(deviceId, projectId, path, ct);

        public Task<ActionResult<FilesMutateResult>> CreateDirectoryAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => files.CreateDirectoryAsync(deviceId, projectId, path, ct);

        public Task<ActionResult<FilesMutateResult>> RenameAsync(string deviceId, string projectId, string path, string destination, bool overwrite = false, CancellationToken ct = default) => files.RenameAsync(deviceId, projectId, path, destination, overwrite, ct);

        public Task<ActionResult<FilesMutateResult>> DeleteAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => files.DeleteAsync(deviceId, projectId, path, ct);

        public Task<ActionResult<FilesSuggestResult>> SuggestAsync(string deviceId, string projectId, string query, int limit = 0, CancellationToken ct = default) => files.SuggestAsync(deviceId, projectId, query, limit, ct);

        public Task<ActionResult<FilesSearchResult>> SearchAsync(string deviceId, string projectId, FilesSearchParams query, CancellationToken ct = default) => files.SearchAsync(deviceId, projectId, query, ct);
    }

    private static (WorkspaceViewModel Workspace, ClientStores Stores) Create()
    {
        var stores = new ClientStores();
        stores.Selection.Update(_ => new SelectionState(Project, "s1"));
        var files = new FakeFiles().Add("src/App.cs", "class App {}").Add("README.md", "# hi").Add("src/B.cs", "b");
        return (new WorkspaceViewModel(stores, files: files), stores);
    }

    [Fact]
    public async Task A_preview_reuses_the_project_slot_while_programmatic_tabs_stay_pinned()
    {
        var (workspace, _) = Create();
        await workspace.OpenPreviewAsync(Project, "src/App.cs");
        Assert.True(workspace.ActiveFile!.IsPreview);
        await workspace.OpenAsync(Project, "README.md");
        Assert.False(workspace.ActiveFile!.IsPreview);
        await workspace.OpenPreviewAsync(Project, "src/B.cs");

        Assert.Equal(["s:s1", "f:src/B.cs", "f:README.md"], workspace.Tabs.Select(t => t.Key));
        Assert.True(workspace.ActiveFile!.IsPreview);
        Assert.Single(workspace.Tabs, t => t.File?.IsPreview == true);
    }

    [Fact]
    public async Task Explicit_pin_and_programmatic_open_keep_previews_from_being_replaced()
    {
        var (workspace, _) = Create();
        await workspace.OpenPreviewAsync(Project, "src/App.cs");
        var file = workspace.ActiveFile!;
        workspace.PinFile(Project, file.Path);
        Assert.False(file.IsPreview);
        await workspace.OpenPreviewAsync(Project, "README.md");
        await workspace.OpenAsync(Project, "README.md");
        Assert.False(workspace.ActiveFile!.IsPreview);
        await workspace.OpenPreviewAsync(Project, "src/B.cs");

        Assert.Equal(3, workspace.Tabs.Count(t => t.IsFile));
        Assert.Same(file, workspace.Tabs.Single(t => t.Path == file.Path).File);
    }

    [Fact]
    public async Task Editing_pins_the_preview_and_a_later_preview_never_discards_the_edit()
    {
        var (workspace, _) = Create();
        await workspace.OpenPreviewAsync(Project, "src/App.cs");
        var file = workspace.ActiveFile!;
        file.EditCommand.Execute(null);
        Assert.False(file.IsPreview);
        file.EditText = "unsaved";
        await workspace.OpenPreviewAsync(Project, "README.md");

        Assert.True(file.IsDirty);
        Assert.Equal("unsaved", workspace.Tabs.Single(t => t.Path == file.Path).File!.EditText);
        Assert.True(workspace.ActiveFile!.IsPreview);
    }

    [Fact]
    public async Task Preview_slots_are_scoped_to_the_project()
    {
        var (workspace, stores) = Create();
        await workspace.OpenPreviewAsync(Project, "src/App.cs");
        var first = workspace.ActiveFile;
        var other = new ProjectRef("remote", "p1");
        stores.Selection.Update(_ => new SelectionState(other, "s2"));
        await workspace.OpenPreviewAsync(other, "README.md");
        Assert.True(workspace.ActiveFile!.IsPreview);
        stores.Selection.Update(_ => new SelectionState(Project, "s1"));
        Assert.Same(first, workspace.ActiveFile);
        Assert.True(first!.IsPreview);
    }

    [Fact]
    public async Task Opening_an_existing_pinned_tab_as_preview_does_not_demote_it()
    {
        var (workspace, _) = Create();
        await workspace.OpenAsync(Project, "src/App.cs");
        var file = workspace.ActiveFile;
        await workspace.OpenPreviewAsync(Project, "README.md");
        await workspace.OpenPreviewAsync(Project, "src/App.cs");
        Assert.Same(file, workspace.ActiveFile);
        Assert.False(file!.IsPreview);
        Assert.Single(workspace.Tabs, t => t.File?.IsPreview == true);
    }

    [Fact]
    public async Task Breadcrumbs_reveal_accumulated_paths_and_reference_returns_to_the_session()
    {
        var (workspace, stores) = Create();
        (ProjectRef Project, string Path)? revealed = null;
        (ProjectRef Project, string Path)? referenced = null;
        workspace.RevealRequested = (project, path) => revealed = (project, path);
        workspace.ReferenceRequested = (project, path) => referenced = (project, path);
        workspace.CanReferenceRequested = project => stores.Selection.State.Project == project && stores.Selection.State.SessionId is not null;
        await workspace.OpenPreviewAsync(Project, "src/App.cs");
        var file = workspace.ActiveFile!;
        Assert.Equal([new FileBreadcrumbSegment("src", "src", false), new FileBreadcrumbSegment("App.cs", "src/App.cs", true)], file.Breadcrumbs);
        file.RevealBreadcrumbCommand.Execute(file.Breadcrumbs[0]);
        Assert.Equal((Project, "src"), revealed);
        Assert.True(file.ReferenceCommand.CanExecute(null));
        file.ReferenceCommand.Execute(null);
        Assert.Equal((Project, "src/App.cs"), referenced);
        Assert.Null(workspace.ActiveFile);
        Assert.Contains(workspace.Tabs, t => t.File == file);

        stores.Selection.Update(_ => new SelectionState(Project, null));
        Assert.False(file.CanReference);
        Assert.False(file.ReferenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refreshing_reference_availability_notifies_files_in_inactive_projects_too()
    {
        var (workspace, stores) = Create();
        var available = true;
        workspace.ReferenceRequested = (_, _) => { };
        workspace.CanReferenceRequested = _ => available;
        await workspace.OpenPreviewAsync(Project, "src/App.cs");
        var first = workspace.ActiveFile!;
        var other = new ProjectRef("local", "p2");
        stores.Selection.Update(_ => new SelectionState(other, "s2"));
        await workspace.OpenPreviewAsync(other, "README.md");
        var second = workspace.ActiveFile!;
        var notified = 0;
        first.ReferenceCommand.CanExecuteChanged += (_, _) => notified++;
        second.ReferenceCommand.CanExecuteChanged += (_, _) => notified++;

        available = false;
        workspace.RefreshReferenceAvailability();

        Assert.Equal(2, notified);
        Assert.False(first.ReferenceCommand.CanExecute(null));
        Assert.False(second.ReferenceCommand.CanExecute(null));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_preview_read_never_replaces_the_new_active_file_even_after_switching_projects(bool switchProject)
    {
        var stores = new ClientStores();
        stores.Selection.Update(_ => new SelectionState(Project, "s1"));
        var files = new DelayedFiles(new FakeFiles().Add("README.md", "new preview"));
        var workspace = new WorkspaceViewModel(stores, files: files);
        var firstOpen = workspace.OpenPreviewAsync(Project, "src/App.cs");
        var first = workspace.ActiveFile!;
        Assert.True(first.IsLoading);
        Assert.False(firstOpen.IsCompleted);
        var nextProject = switchProject ? new ProjectRef("remote", "p1") : Project;
        if (switchProject)
        {
            stores.Selection.Update(_ => new SelectionState(nextProject, "s2"));
        }

        await workspace.OpenPreviewAsync(nextProject, "README.md");
        var next = workspace.ActiveFile!;
        files.Pending.SetResult(R.Ok(new FileContentDto("text", "src/App.cs", 8, "r1", "late old")));
        await firstOpen;

        Assert.Same(next, workspace.ActiveFile);
        Assert.Equal("README.md", workspace.ActiveTab!.Path);
        Assert.Equal("new preview", next.DisplayText);
        Assert.Equal("late old", first.DisplayText);
        Assert.DoesNotContain(workspace.Tabs, t => ReferenceEquals(t.File, first));
        Assert.Single(workspace.Tabs, t => t.File?.IsPreview == true);
        if (switchProject)
        {
            stores.Selection.Update(_ => new SelectionState(Project, "s1"));
            Assert.Same(first, workspace.ActiveFile);
            Assert.Equal("late old", workspace.ActiveFile!.DisplayText);
        }
    }

}
