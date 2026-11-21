using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Inspector;

public class CompactExplorerTests
{
    private static readonly ProjectRef Project = new("local", "p1");

    [Fact]
    public async Task Compact_chains_are_lazy_and_keep_real_identity()
    {
        var files = new FakeFiles().Add("packages/client/views/App.cs");
        var tree = new FileTreeViewModel(files);
        await tree.BindAsync(Project, "Demo", "/demo");
        var packages = Assert.Single(tree.Roots);
        Assert.Equal([""], files.ListCalls);
        Assert.Equal("packages", packages.DisplayName);

        await tree.ExpandAsync(packages);

        Assert.Equal("packages/client/views", packages.DisplayName);
        Assert.Equal("packages", packages.Name);
        var views = tree.Find("packages/client/views");
        Assert.Same(views, packages.DisplayNode);
        Assert.Same(views!.Children, packages.DisplayChildren);
        Assert.Same(packages.Children[0], tree.Find("packages/client"));
        Assert.Same(packages, tree.DisplayRow(views));
        Assert.Same(packages, tree.DisplayRow(tree.Find("packages/client")!));
        Assert.Equal("App.cs", Assert.Single(packages.DisplayChildren).Name);

        tree.CompactFolders = false;
        Assert.Equal("packages", packages.DisplayName);
        Assert.Same(packages.Children, packages.DisplayChildren);
        Assert.Same(views, tree.Find("packages/client/views"));
    }

    [Fact]
    public async Task Reveal_loads_collapsed_ancestors_and_all_pages_without_opening_files()
    {
        var files = new FakeFiles { PageSize = 1 };
        files.Add("zeta/a.txt").Add("alpha/deep/end.txt");
        var tree = new FileTreeViewModel(files);
        await tree.BindAsync(Project, "Demo", "/demo");

        var node = await tree.RevealPathAsync("alpha/deep/end.txt");

        Assert.Equal("alpha/deep/end.txt", node!.Path);
        Assert.Same(node, tree.Find(node.Path));
        Assert.True(tree.Find("alpha")!.IsExpanded);
        Assert.True(tree.Find("alpha/deep")!.IsExpanded);
        Assert.Equal(2, files.ListCalls.Count(p => p == ""));
    }

    [Fact]
    public async Task Compact_chain_stops_at_siblings_and_pagination()
    {
        var files = new FakeFiles().Add("a/b/c.txt").Add("a/other.txt");
        var tree = new FileTreeViewModel(files);
        await tree.BindAsync(Project, "Demo", "/demo");
        await tree.ExpandAsync(tree.Roots[0]);
        Assert.Equal("a", tree.Roots[0].DisplayName);
        Assert.DoesNotContain("a/b", files.ListCalls);
    }

    [Fact]
    public async Task Inline_create_rename_cancel_and_permission_checks_do_not_use_dialogs()
    {
        var files = new FakeFiles().Add("a/b/original.cs");
        var panel = Panel(files);
        panel.UseInlineEditing = true;
        await panel.BindAsync(Project, "Demo", "/demo", true, false);
        await panel.RevealPathAsync("a/b");
        await panel.NewFileCommand.ExecuteAsync(panel.Selected);
        var draft = Assert.Single(panel.Tree.Find("a/b")!.Children, n => n.IsDraft);
        draft.EditName = "new.cs";
        await panel.CommitInlineEditCommand.ExecuteAsync(null);
        Assert.Contains("a/b/new.cs", files.Files.Keys);
        Assert.DoesNotContain(panel.Tree.Find("a/b")!.Children, n => n.IsDraft);

        var file = panel.Tree.Find("a/b/new.cs")!;
        await panel.RenameCommand.ExecuteAsync(file);
        Assert.True(file.IsEditing);
        file.EditName = "renamed.cs";
        await panel.CommitInlineEditCommand.ExecuteAsync(null);
        Assert.Contains("a/b/renamed.cs", files.Files.Keys);
        Assert.DoesNotContain("a/b/new.cs", files.Files.Keys);

        await panel.NewFolderCommand.ExecuteAsync(panel.Tree.Find("a/b"));
        panel.CancelInlineEditCommand.Execute(null);
        Assert.DoesNotContain(panel.Tree.Find("a/b")!.Children, n => n.IsDraft);
        panel.CanWrite = false;
        await panel.NewFileCommand.ExecuteAsync(null);
        Assert.DoesNotContain(panel.Tree.Roots, n => n.IsDraft);
    }

    [Fact]
    public async Task Invalid_inline_names_keep_the_editor_and_reference_only_targets_real_files()
    {
        var files = new FakeFiles().Add("doc.md");
        var panel = Panel(files);
        panel.UseInlineEditing = true;
        await panel.BindAsync(Project, "Demo", "/demo", true, false);
        await panel.NewFileCommand.ExecuteAsync(null);
        var draft = Assert.Single(panel.Tree.Roots, n => n.IsDraft);
        draft.EditName = "../outside";
        await panel.CommitInlineEditCommand.ExecuteAsync(null);
        Assert.True(draft.IsEditing);
        Assert.Equal("files.invalidPath", panel.Failure!.Value.Key);
        Assert.Empty(files.Mutations);
        panel.CancelInlineEditCommand.Execute(null);
        string? reference = null;
        panel.ReferenceRequested = (project, path) => reference = path;
        panel.ReferenceCommand.Execute(panel.Tree.Find("doc.md"));
        Assert.Null(reference);
        panel.IsReferenceAvailable = true;
        panel.ReferenceCommand.Execute(panel.Tree.Find("doc.md"));
        Assert.Equal("doc.md", reference);
        Assert.False(panel.IsFilterVisible);
    }

    private static FilesPanelViewModel Panel(FakeFiles files) => new(files, () => new NoDialogs(), () => DefaultInspectorText.Instance, () => NoLocalFiles.Instance);

    private sealed class NoDialogs : IDialogService
    {
        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) => throw new InvalidOperationException("Inline editing must not prompt.");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => Task.FromResult(false);
    }
}
