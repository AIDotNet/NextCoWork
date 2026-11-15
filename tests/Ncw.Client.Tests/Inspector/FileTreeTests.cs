using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Inspector;

public class FileTreeTests
{
    private static readonly ProjectRef Project = new("local", "p1");

    private static FakeFiles Sample() => new FakeFiles()
        .Add("README.md", "hi")
        .Add("src/Program.cs", "class P {}")
        .Add("src/Util/Strings.cs", "")
        .Add("src/file10.cs", "")
        .Add("src/file2.cs", "")
        .Add("docs/guide.md", "")
        .Add("docs/api/index.md", "")
        .Add(".gitignore", "bin/");

    [Fact]
    public async Task Root_is_listed_one_level_and_directories_have_a_placeholder_until_expanded()
    {
        var files = Sample();
        var tree = new FileTreeViewModel(files);

        await tree.BindAsync(Project, "Demo", "/work/demo");

        Assert.Equal([""], files.ListCalls.Take(1)); // 根;默认展开的 src 随后才列
        var docs = tree.Roots.Single(n => n.Name == "docs");
        Assert.False(docs.ChildrenLoaded);
        Assert.True(Assert.Single(docs.Children).IsPlaceholder); // 树控件靠这个占位认出它是「能展开的」
        Assert.DoesNotContain("docs", files.ListCalls);
    }

    [Fact]
    public async Task Expanding_lists_the_directory_once_and_replaces_the_placeholder()
    {
        var files = Sample();
        var tree = new FileTreeViewModel(files);
        await tree.BindAsync(Project, "Demo", "/work/demo");
        var docs = tree.Roots.Single(n => n.Name == "docs");

        await tree.ExpandAsync(docs);
        await tree.ExpandAsync(docs); // 再展开不再请求

        Assert.Equal(1, files.ListCalls.Count(p => p == "docs"));
        Assert.True(docs.IsExpanded && docs.ChildrenLoaded);
        Assert.Equal(["api", "guide.md"], docs.Children.Select(c => c.Name));
        Assert.DoesNotContain(docs.Children, c => c.IsPlaceholder);
        Assert.Equal("docs/api", docs.Children[0].Path);
    }

    [Fact]
    public async Task Entries_are_sorted_directories_first_with_natural_order_whatever_order_the_device_returns()
    {
        var files = Sample();
        var tree = new FileTreeViewModel(files);
        await tree.BindAsync(Project, "Demo", "/work/demo");

        // 根:目录在前,然后文件(排序规则只有 Core 的 FileEntrySort 一份)
        Assert.Equal(["docs", "src", ".gitignore", "README.md"], tree.Roots.Select(n => n.Name));
        // 数字按值:file2 在 file10 前
        var src = tree.Roots.Single(n => n.Name == "src");
        Assert.Equal(["Util", "file2.cs", "file10.cs", "Program.cs"], src.Children.Select(c => c.Name));
    }

    [Fact]
    public async Task Src_is_expanded_by_default_and_expansion_survives_a_refresh()
    {
        var files = Sample();
        var tree = new FileTreeViewModel(files);
        await tree.BindAsync(Project, "Demo", "/work/demo");
        var src = tree.Roots.Single(n => n.Name == "src");
        Assert.True(src.IsExpanded);
        Assert.True(src.ChildrenLoaded);

        var docs = tree.Roots.Single(n => n.Name == "docs");
        await tree.ExpandAsync(docs);
        files.Add("docs/new.md");
        await tree.RefreshAsync();

        var docsAfter = tree.Roots.Single(n => n.Name == "docs");
        Assert.True(docsAfter.IsExpanded);
        Assert.Contains(docsAfter.Children, c => c.Name == "new.md"); // 刷新后展开着的目录重新列了
        Assert.True(tree.Roots.Single(n => n.Name == "src").IsExpanded);
    }

    [Fact]
    public async Task Ignored_entries_are_hidden_by_default_and_dimmed_when_shown()
    {
        var files = Sample().Add("node_modules/x/index.js");
        files.Ignored.Add("node_modules");
        var tree = new FileTreeViewModel(files);
        await tree.BindAsync(Project, "Demo", "/work/demo");

        Assert.DoesNotContain(tree.Roots, n => n.Name == "node_modules");
        Assert.All(files.HideIgnoredCalls, hide => Assert.True(hide));

        tree.ShowIgnored = true;
        await tree.RefreshAsync();

        var ignored = tree.Roots.Single(n => n.Name == "node_modules");
        Assert.True(ignored.IsIgnored && ignored.IsDimmed);
        Assert.True(tree.Roots.Single(n => n.Name == ".gitignore").IsDimmed); // 点号开头也淡显
        Assert.Contains(false, files.HideIgnoredCalls);
    }

    [Fact]
    public async Task A_truncated_listing_ends_with_a_show_more_row_that_appends_the_next_page()
    {
        var files = new FakeFiles { PageSize = 3 };
        for (var i = 1; i <= 7; i++)
        {
            files.Add($"f{i}.txt");
        }

        var tree = new FileTreeViewModel(files);
        await tree.BindAsync(Project, "Demo", "/work/demo");

        var more = Assert.Single(tree.Roots, n => n.IsMore);
        Assert.Equal(4, more.Remaining);
        Assert.Equal(3, tree.Roots.Count(n => n.IsEntry));

        await tree.LoadMoreAsync(more);

        Assert.Equal(6, tree.Roots.Count(n => n.IsEntry));
        Assert.Equal(1, Assert.Single(tree.Roots, n => n.IsMore).Remaining);
    }

    [Fact]
    public async Task A_listing_that_arrives_after_switching_project_is_discarded()
    {
        var files = Sample();
        var gate = new TaskCompletionSource();
        files.Gate = _ => gate.Task;
        var tree = new FileTreeViewModel(files);

        var pending = tree.BindAsync(Project, "Demo", "/work/demo");
        files.Gate = null;
        await tree.BindAsync(new ProjectRef("local", "p2"), "Other", "/work/other");
        var after = tree.Roots.Select(n => n.Name).ToList();

        gate.SetResult();
        await pending;

        Assert.Equal(after, tree.Roots.Select(n => n.Name)); // 旧项目的结果没有盖掉新项目的树
        Assert.Equal("Other", tree.ProjectName);
    }

    [Fact]
    public async Task Failures_are_structured_and_do_not_leave_the_directory_half_expanded()
    {
        var files = Sample();
        var tree = new FileTreeViewModel(new FailingList(files));
        await tree.BindAsync(Project, "Demo", "/work/demo");

        Assert.NotNull(tree.Failure);
        Assert.Equal("files.notFound", tree.Failure!.Value.Key);
        Assert.Empty(tree.Roots);
    }

    [Fact]
    public async Task Git_markers_decorate_files_and_dim_a_dot_after_directories_even_when_not_loaded()
    {
        var files = Sample();
        var tree = new FileTreeViewModel(files);
        await tree.BindAsync(Project, "Demo", "/work/demo");

        tree.ApplyChanges(new Dictionary<string, GitChange>
        {
            ["README.md"] = GitChange.Modified,
            ["docs/api/index.md"] = GitChange.Untracked,
        });

        Assert.Equal("M", tree.Roots.Single(n => n.Name == "README.md").ChangeLetter);
        var docs = tree.Roots.Single(n => n.Name == "docs");
        Assert.False(docs.ChildrenLoaded);
        Assert.True(docs.HasChangedDescendant); // 目录没展开也知道里面有变更
        Assert.False(tree.Roots.Single(n => n.Name == "src").HasChangedDescendant);

        await tree.ExpandAsync(docs); // 展开后新加载的节点也带上角标
        Assert.True(docs.Children.Single(c => c.Name == "api").HasChangedDescendant);
    }

    [Fact]
    public async Task Collapse_all_forgets_expansion_so_a_refresh_does_not_reopen_it()
    {
        var files = Sample();
        var tree = new FileTreeViewModel(files);
        await tree.BindAsync(Project, "Demo", "/work/demo");

        tree.CollapseAll();
        await tree.RefreshAsync();

        Assert.All(tree.Roots, n => Assert.False(n.IsExpanded));
    }

    private sealed class FailingList(FakeFiles inner) : Ncw.Client.Actions.IFilesCommands
    {
        public Task<Ncw.Client.Actions.ActionResult<Ncw.Protocol.Methods.FilesListResult>> ListAsync(string deviceId, string projectId, string path = "", string sort = "name", bool hideIgnored = false, int offset = 0, int limit = 0, CancellationToken ct = default) =>
            Task.FromResult(R.Fail<Ncw.Protocol.Methods.FilesListResult>(Ncw.Client.Actions.ActionOutcome.NotFound, "files.notFound"));

        public Task<Ncw.Client.Actions.ActionResult<Ncw.Protocol.Methods.FileContentDto>> ReadAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => inner.ReadAsync(deviceId, projectId, path, ct);

        public Task<Ncw.Client.Actions.ActionResult<Ncw.Protocol.Methods.FilesRangeResult>> ReadRangeAsync(string deviceId, string projectId, string path, long offset, int length, string? revision = null, CancellationToken ct = default) => inner.ReadRangeAsync(deviceId, projectId, path, offset, length, revision, ct);

        public Task<Ncw.Client.Actions.ActionResult<Ncw.Protocol.Methods.FilesWriteResult>> WriteTextAsync(string deviceId, string projectId, string path, string content, string revision, CancellationToken ct = default) => inner.WriteTextAsync(deviceId, projectId, path, content, revision, ct);

        public Task<Ncw.Client.Actions.ActionResult<Ncw.Protocol.Methods.FilesMutateResult>> CreateFileAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => inner.CreateFileAsync(deviceId, projectId, path, ct);

        public Task<Ncw.Client.Actions.ActionResult<Ncw.Protocol.Methods.FilesMutateResult>> CreateDirectoryAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => inner.CreateDirectoryAsync(deviceId, projectId, path, ct);

        public Task<Ncw.Client.Actions.ActionResult<Ncw.Protocol.Methods.FilesMutateResult>> RenameAsync(string deviceId, string projectId, string path, string destination, bool overwrite = false, CancellationToken ct = default) => inner.RenameAsync(deviceId, projectId, path, destination, overwrite, ct);

        public Task<Ncw.Client.Actions.ActionResult<Ncw.Protocol.Methods.FilesMutateResult>> DeleteAsync(string deviceId, string projectId, string path, CancellationToken ct = default) => inner.DeleteAsync(deviceId, projectId, path, ct);

        public Task<Ncw.Client.Actions.ActionResult<Ncw.Protocol.Methods.FilesSuggestResult>> SuggestAsync(string deviceId, string projectId, string query, int limit = 0, CancellationToken ct = default) => inner.SuggestAsync(deviceId, projectId, query, limit, ct);

        public Task<Ncw.Client.Actions.ActionResult<Ncw.Protocol.Methods.FilesSearchResult>> SearchAsync(string deviceId, string projectId, Ncw.Protocol.Methods.FilesSearchParams query, CancellationToken ct = default) => inner.SearchAsync(deviceId, projectId, query, ct);
    }
}

public class FileGlyphTests
{
    [Theory]
    [InlineData("Program.cs", FileGlyph.Code)]
    [InlineData("MainWindow.AXAML", FileGlyph.Code)]
    [InlineData("README.md", FileGlyph.Text)]
    [InlineData("logo.PNG", FileGlyph.Image)]
    [InlineData("Directory.Packages.props", FileGlyph.Config)]
    [InlineData(".editorconfig", FileGlyph.Config)]
    [InlineData("LICENSE", FileGlyph.Other)]
    [InlineData("archive.zip", FileGlyph.Other)]
    public void The_icon_follows_the_extension(string name, FileGlyph expected) => Assert.Equal(expected, FileGlyphs.Of(name));
}
