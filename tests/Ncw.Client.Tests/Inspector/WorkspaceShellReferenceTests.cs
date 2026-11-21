using Ncw.Client.Mock;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Inspector;

/// <summary>文件路径条通过外壳恢复右侧文件面板,会话不可用时不接受引用。</summary>
public class WorkspaceShellReferenceTests
{
    private static readonly ProjectRef Project = new("local", "nextcowork");

    private static (ClientStores Stores, ShellViewModel Shell) Create()
    {
        var stores = MockWorkspace.Create(DateTimeOffset.UnixEpoch);
        stores.Selection.Update(_ => new SelectionState(Project, "s-ncw-1"));
        return (stores, new ShellViewModel(stores, inspector: MockInspectorServices.Create(stores)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revealing_a_breadcrumb_reopens_the_inspector_or_right_drawer(bool narrow)
    {
        var (_, shell) = Create();
        shell.IsInspectorNarrow = narrow;
        shell.IsInspectorOpen = false;
        shell.IsInspectorDrawerOpen = false;
        shell.IsSidebarOpen = false;
        shell.IsDrawerOpen = false;
        shell.Inspector.Files.IsExpanded = false;
        await shell.Workspace.OpenPreviewAsync(Project, "src/Ncw.Client/ViewModels/InspectorViewModel.cs");
        var file = shell.Workspace.ActiveFile!;

        file.RevealBreadcrumbCommand.Execute(file.Breadcrumbs[0]);

        Assert.True(shell.Inspector.Files.IsExpanded);
        Assert.True(shell.Inspector.IsFiles);
        Assert.Equal(narrow, shell.IsInspectorDrawerOpen);
        Assert.Equal(!narrow, shell.IsInspectorOpen);
        Assert.False(shell.IsSidebarOpen);
        Assert.False(shell.IsDrawerOpen);
    }

    [Fact]
    public async Task Clearing_the_session_disables_the_open_file_reference_command()
    {
        var (stores, shell) = Create();
        await shell.Workspace.OpenPreviewAsync(Project, "README.md");
        var file = shell.Workspace.ActiveFile!;
        Assert.True(file.ReferenceCommand.CanExecute(null));
        var notified = false;
        file.ReferenceCommand.CanExecuteChanged += (_, _) => notified = true;

        stores.Selection.Update(_ => new SelectionState(Project, null));

        Assert.True(notified);
        Assert.False(shell.Conversation.HasSession);
        Assert.False(file.CanReference);
        Assert.False(file.ReferenceCommand.CanExecute(null));
    }
}
