using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Inspector;

/// <summary>文件在主工作区里作为标签页打开:和会话标签并排、各自的前台规则、未保存的修改先询问、重命名 / 删除时关掉。</summary>
public class WorkspaceFileTabsTests
{
    private static readonly ProjectRef Project = new("local", "p1");

    private sealed class Dialogs : IDialogService
    {
        public bool Confirm { get; set; } = true;

        public int Asked { get; private set; }

        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) => Task.FromResult<string?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
        {
            Asked++;
            return Task.FromResult(Confirm);
        }
    }

    private static (WorkspaceViewModel Workspace, ClientStores Stores, Dialogs Dialogs, FakeFiles Files) Create()
    {
        var stores = new ClientStores();
        stores.Selection.Update(_ => new SelectionState(Project, "s1"));
        var files = new FakeFiles().Add("src/App.cs", "class App {}").Add("README.md", "# hi").Add("src/B.cs", "b");
        var dialogs = new Dialogs();
        var workspace = new WorkspaceViewModel(stores, null, files, () => dialogs, () => DefaultInspectorText.Instance, () => NoLocalFiles.Instance);
        return (workspace, stores, dialogs, files);
    }

    [Fact]
    public async Task Opening_a_file_adds_a_tab_next_to_the_session_and_makes_it_current()
    {
        var (workspace, _, _, _) = Create();

        await workspace.OpenAsync(Project, "src/App.cs");

        Assert.Equal(["s:s1", "f:src/App.cs"], workspace.Tabs.Select(t => t.Key));
        Assert.Equal("App.cs", workspace.ActiveTab!.Title);
        Assert.True(workspace.ActiveTab.IsFile);
        Assert.Equal("class App {}", workspace.ActiveFile!.DisplayText);
        Assert.False(workspace.Tabs[0].IsActive); // 文件在前台时,会话标签不是当前
    }

    [Fact]
    public async Task Opening_the_same_file_again_switches_to_its_tab_instead_of_adding_one()
    {
        var (workspace, _, _, files) = Create();
        await workspace.OpenAsync(Project, "src/App.cs");
        await workspace.OpenAsync(Project, "README.md");
        var firstReads = files.Files.Count;

        await workspace.OpenAsync(Project, "src/App.cs");

        Assert.Equal(3, workspace.Tabs.Count);
        Assert.Equal("src/App.cs", workspace.ActiveFile!.Path);
        Assert.Equal(firstReads, files.Files.Count);
    }

    [Fact]
    public async Task Selecting_another_session_brings_the_conversation_back_to_the_front()
    {
        var (workspace, stores, _, _) = Create();
        await workspace.OpenAsync(Project, "src/App.cs");

        stores.Selection.Update(_ => new SelectionState(Project, "s2"));

        Assert.Null(workspace.ActiveFile);
        Assert.Equal("s:s2", workspace.ActiveTab!.Key);
        Assert.Contains(workspace.Tabs, t => t.Key == "f:src/App.cs"); // 文件标签还在,只是不在前台
    }

    [Fact]
    public async Task Activating_a_tab_moves_the_front_and_closing_the_front_hands_it_to_the_neighbour()
    {
        var (workspace, _, _, _) = Create();
        await workspace.OpenAsync(Project, "src/App.cs");
        await workspace.OpenAsync(Project, "README.md");
        await workspace.OpenAsync(Project, "src/B.cs");

        workspace.ActivateCommand.Execute(workspace.Tabs.Single(t => t.Path == "README.md"));
        Assert.Equal("README.md", workspace.ActiveFile!.Path);

        await workspace.CloseCommand.ExecuteAsync(workspace.ActiveTab);
        Assert.Equal("src/B.cs", workspace.ActiveFile!.Path); // 原位置上的邻居
        Assert.DoesNotContain(workspace.Tabs, t => t.Path == "README.md");

        await workspace.CloseCommand.ExecuteAsync(workspace.ActiveTab);
        Assert.Equal("src/App.cs", workspace.ActiveFile!.Path);
        await workspace.CloseCommand.ExecuteAsync(workspace.ActiveTab);
        Assert.Null(workspace.ActiveFile);
        Assert.Equal("s:s1", workspace.ActiveTab!.Key); // 文件都关了,回到会话
    }

    [Fact]
    public async Task Closing_a_file_with_unsaved_edits_asks_first_and_keeps_it_when_declined()
    {
        var (workspace, _, dialogs, _) = Create();
        await workspace.OpenAsync(Project, "README.md");
        workspace.ActiveFile!.EditCommand.Execute(null);
        workspace.ActiveFile.EditText = "changed";
        Assert.True(workspace.ActiveTab!.File!.IsDirty);
        dialogs.Confirm = false;

        await workspace.CloseCommand.ExecuteAsync(workspace.ActiveTab);
        Assert.Equal(1, dialogs.Asked);
        Assert.NotNull(workspace.ActiveFile);

        dialogs.Confirm = true;
        await workspace.CloseCommand.ExecuteAsync(workspace.ActiveTab);
        Assert.Null(workspace.ActiveFile);
        Assert.DoesNotContain(workspace.Tabs, t => t.IsFile);
    }

    [Fact]
    public async Task A_clean_file_closes_without_asking()
    {
        var (workspace, _, dialogs, _) = Create();
        await workspace.OpenAsync(Project, "README.md");

        await workspace.CloseCommand.ExecuteAsync(workspace.ActiveTab);

        Assert.Equal(0, dialogs.Asked);
    }

    [Fact]
    public async Task Renaming_or_deleting_closes_the_tabs_of_that_file_and_of_files_under_it()
    {
        var (workspace, _, _, _) = Create();
        await workspace.OpenAsync(Project, "src/App.cs");
        await workspace.OpenAsync(Project, "src/B.cs");
        await workspace.OpenAsync(Project, "README.md");

        workspace.CloseInside(Project, "src");

        Assert.Equal(["s:s1", "f:README.md"], workspace.Tabs.Select(t => t.Key));
        Assert.Equal("README.md", workspace.ActiveFile!.Path);
    }

    [Fact]
    public async Task Open_files_are_kept_per_project()
    {
        var (workspace, stores, _, _) = Create();
        await workspace.OpenAsync(Project, "README.md");
        var other = new ProjectRef("local", "p2");

        stores.Selection.Update(_ => new SelectionState(other, null));
        Assert.Empty(workspace.Tabs);
        Assert.Null(workspace.ActiveFile);

        stores.Selection.Update(_ => new SelectionState(Project, "s1"));
        Assert.Equal("README.md", workspace.ActiveFile?.Path);
    }

    [Fact]
    public async Task The_preview_of_a_markdown_file_is_rendered_by_default_and_editing_shows_the_source()
    {
        var (workspace, _, _, _) = Create();
        await workspace.OpenAsync(Project, "README.md");
        var file = workspace.ActiveFile!;

        Assert.True(file.IsMarkdown && file.ShowsRendered && !file.ShowsSource);
        file.ToggleRenderedCommand.Execute(null);
        Assert.True(file.ShowsSource && !file.ShowsRendered);
        file.ToggleRenderedCommand.Execute(null);
        file.EditCommand.Execute(null);
        Assert.False(file.ShowsRendered);
        Assert.False(file.ShowsSource); // 编辑时由输入框显示
        Assert.Equal(1, file.TotalLines);
    }
}
