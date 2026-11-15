using System.Text;
using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;

namespace Ncw.Client.Tests.Inspector;

public class FilePreviewTests
{
    private static readonly ProjectRef Project = new("local", "p1");

    private static FilePreviewViewModel Open(FakeFiles files, string path, bool canWrite = true, int? line = null) => new(files, Project, path, canWrite, line);

    [Fact]
    public async Task A_small_text_file_is_read_whole_with_its_revision_and_language()
    {
        var files = new FakeFiles().Add("src/App.cs", "line1\nline2\n");
        var preview = Open(files, "src/App.cs");

        await preview.LoadAsync();

        Assert.True(preview.IsText);
        Assert.Equal("line1\nline2\n", preview.DisplayText);
        Assert.Equal("r1", preview.Revision);
        Assert.Equal("csharp", preview.Language);
        Assert.True(preview.CanEdit);
        Assert.False(preview.IsChunked);
    }

    [Fact]
    public async Task Saving_sends_the_revision_that_was_read_and_adopts_the_new_one()
    {
        var files = new FakeFiles().Add("a.txt", "old");
        var preview = Open(files, "a.txt");
        await preview.LoadAsync();

        preview.EditCommand.Execute(null);
        Assert.True(preview.IsEditing);
        Assert.False(preview.CanSave); // 没改动不能存
        preview.EditText = "new";
        Assert.True(preview.IsDirty && preview.CanSave);
        await preview.SaveCommand.ExecuteAsync(null);

        Assert.Equal([("a.txt", "new", "r1")], files.Writes);
        Assert.False(preview.IsEditing);
        Assert.Equal("new", preview.DisplayText);
        Assert.Equal("r2", preview.Revision);
        Assert.True(preview.JustSaved);
    }

    [Fact]
    public async Task A_conflict_keeps_the_edit_and_offers_reload_or_keep_mine()
    {
        var files = new FakeFiles().Add("a.txt", "old");
        var preview = Open(files, "a.txt");
        await preview.LoadAsync();
        preview.EditCommand.Execute(null);
        preview.EditText = "mine";
        files.Files["a.txt"] = "theirs"; // 别处改了
        files.Revision = 2;

        await preview.SaveCommand.ExecuteAsync(null);

        Assert.True(preview.HasConflict);
        Assert.True(preview.IsEditing);
        Assert.Equal("mine", preview.EditText); // 编辑内容没丢
        Assert.Null(preview.Failure);

        // 保留我的:先读一次拿到磁盘上现在的修订号,再用它覆盖
        await preview.KeepMineCommand.ExecuteAsync(null);

        Assert.False(preview.HasConflict);
        Assert.Equal("mine", files.Files["a.txt"]);
        Assert.Equal("r2", files.Writes[^1].Revision);
    }

    [Fact]
    public async Task Reload_drops_the_edit_and_shows_what_is_on_disk()
    {
        var files = new FakeFiles().Add("a.txt", "old");
        var preview = Open(files, "a.txt");
        await preview.LoadAsync();
        preview.EditCommand.Execute(null);
        preview.EditText = "mine";
        files.Files["a.txt"] = "theirs";
        files.Revision = 2;
        await preview.SaveCommand.ExecuteAsync(null);

        await preview.ReloadCommand.ExecuteAsync(null);

        Assert.False(preview.IsEditing);
        Assert.False(preview.HasConflict);
        Assert.Equal("theirs", preview.DisplayText);
        Assert.Equal("r2", preview.Revision);
    }

    [Fact]
    public async Task A_viewer_cannot_edit()
    {
        var files = new FakeFiles().Add("a.txt", "x");
        var preview = Open(files, "a.txt", canWrite: false);
        await preview.LoadAsync();

        Assert.False(preview.CanEdit);
        preview.EditCommand.Execute(null);
        Assert.False(preview.IsEditing);
    }

    [Fact]
    public async Task A_missing_file_is_a_structured_failure()
    {
        var preview = Open(new FakeFiles(), "gone.txt");

        await preview.LoadAsync();

        Assert.True(preview.IsFailed);
        Assert.Equal("files.notFound", preview.Failure!.Value.Key);
    }

    [Fact]
    public async Task Binary_and_non_utf8_files_only_show_a_hint()
    {
        var files = new FakeFiles { BinaryReason = "unsupported" };
        var preview = Open(files, "a.bin");

        await preview.LoadAsync();

        Assert.True(preview.IsBinary);
        Assert.Equal("unsupported", preview.Reason);
        Assert.False(preview.CanEdit);
    }

    [Fact]
    public async Task Images_are_shown_from_their_bytes()
    {
        var files = new FakeFiles { ImageBytes = [1, 2, 3] };
        var preview = Open(files, "logo.png");

        await preview.LoadAsync();

        Assert.True(preview.IsImage);
        Assert.Equal([1, 2, 3], preview.ImageData);
    }

    [Fact]
    public async Task A_text_file_over_the_inline_limit_is_read_in_chunks_and_is_read_only()
    {
        var text = string.Concat(Enumerable.Range(1, 30_000).Select(i => $"row {i}\n")); // 远超一块
        var files = new FakeFiles { KindAsLargeText = true, RawBytes = Encoding.UTF8.GetBytes(text) };
        var preview = Open(files, "big.log");

        await preview.LoadAsync();

        Assert.True(preview.IsText && preview.IsChunked);
        Assert.False(preview.CanEdit);
        Assert.Equal((0, FilePreviewViewModel.ChunkBytes, (string?)null), files.RangeCalls[0]);
        Assert.True(preview.CanLoadMore);
        Assert.StartsWith("row 1\n", preview.DisplayText);
        var firstLines = preview.DisplayText.Split('\n').Length;

        // 先放出已读的行(不再请求),放完了才读下一块,并带上上一块的修订号
        for (var i = 0; i < 100 && files.RangeCalls.Count == 1; i++)
        {
            await preview.LoadMoreCommand.ExecuteAsync(null);
        }

        Assert.True(files.RangeCalls.Count >= 2);
        Assert.Equal("large:1:1", files.RangeCalls[1].Revision);
        Assert.True(files.RangeCalls[1].Offset > 0);
        Assert.True(preview.DisplayText.Split('\n').Length > firstLines);
    }

    [Fact]
    public async Task A_multibyte_character_split_across_two_chunks_is_decoded_whole()
    {
        // 把一个三字节字符恰好切在块边界上
        var prefix = new string('a', FilePreviewViewModel.ChunkBytes - 1);
        var text = prefix + "中文结尾";
        var files = new FakeFiles { KindAsLargeText = true, RawBytes = Encoding.UTF8.GetBytes(text) };
        var preview = Open(files, "big.txt");

        await preview.LoadAsync();
        while (preview.CanLoadMore)
        {
            await preview.LoadMoreCommand.ExecuteAsync(null);
        }

        Assert.EndsWith("中文结尾", preview.DisplayText);
        Assert.DoesNotContain('�', preview.DisplayText);
    }

    [Fact]
    public async Task A_large_file_with_a_nul_byte_is_treated_as_binary()
    {
        var files = new FakeFiles { KindAsLargeText = true, RawBytes = [.. Encoding.UTF8.GetBytes("MZ"), 0, 1, 2] };
        var preview = Open(files, "x.exe");

        await preview.LoadAsync();

        Assert.True(preview.IsBinary);
        Assert.Equal("binary", preview.Reason);
    }

    [Fact]
    public async Task Changing_mid_read_stops_at_what_was_read_and_reports_the_conflict()
    {
        var text = new string('x', FilePreviewViewModel.ChunkBytes * 2);
        var files = new FakeFiles { KindAsLargeText = true, RawBytes = Encoding.UTF8.GetBytes(text), FailRangeAfter = 1 };
        var preview = Open(files, "big.txt");
        await preview.LoadAsync();

        await preview.LoadMoreCommand.ExecuteAsync(null); // 先放出已读的行
        await preview.LoadMoreCommand.ExecuteAsync(null);

        Assert.True(preview.IsText);
        Assert.Equal("files.conflict", preview.Failure?.Key);
    }

    [Fact]
    public async Task Save_as_bytes_for_a_text_file_are_the_utf8_of_what_was_read()
    {
        var files = new FakeFiles().Add("a.txt", "héllo");
        var preview = Open(files, "a.txt");
        await preview.LoadAsync();

        Assert.Equal(Encoding.UTF8.GetBytes("héllo"), await preview.ReadAllBytesAsync());
    }

    [Fact]
    public void Language_follows_the_extension()
    {
        Assert.Equal("csharp", FileLanguages.Of("src/A.cs"));
        Assert.Equal("xml", FileLanguages.Of("views/Main.axaml"));
        Assert.Equal("dockerfile", FileLanguages.Of("deploy/Dockerfile"));
        Assert.Null(FileLanguages.Of("LICENSE"));
    }
}

public class FilesPanelTests
{
    private static readonly ProjectRef Project = new("local", "p1");

    private sealed class ScriptedDialogs : IDialogService
    {
        public Queue<string?> Answers { get; } = new();

        public bool Confirm { get; set; } = true;

        public List<string> Titles { get; } = [];

        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial)
        {
            Titles.Add(title);
            return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
        }

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => Task.FromResult(Confirm);
    }

    private sealed class RecordingLocal : ILocalFiles
    {
        public List<string> Calls { get; } = [];

        public Task CopyTextAsync(string text)
        {
            Calls.Add("copy:" + text);
            return Task.CompletedTask;
        }

        public Task<bool> RevealAsync(string absolutePath)
        {
            Calls.Add("reveal:" + absolutePath);
            return Task.FromResult(true);
        }

        public Task<bool> OpenAsync(string absolutePath)
        {
            Calls.Add("open:" + absolutePath);
            return Task.FromResult(true);
        }

        public Task<bool> SaveAsync(string suggestedName, byte[] data)
        {
            Calls.Add($"save:{suggestedName}:{data.Length}");
            return Task.FromResult(true);
        }
    }

    private static async Task<(FilesPanelViewModel Panel, FakeFiles Files, ScriptedDialogs Dialogs, RecordingLocal Local)> Create(bool local = true)
    {
        var files = new FakeFiles().Add("src/App.cs", "class App {}").Add("README.md", "hello");
        var dialogs = new ScriptedDialogs();
        var localFiles = new RecordingLocal();
        var panel = new FilesPanelViewModel(files, () => dialogs, () => DefaultInspectorText.Instance, () => localFiles, searchDebounce: TimeSpan.Zero);
        await panel.BindAsync(Project, "Demo", "/work/demo", canWrite: true, canOpenLocally: local);
        return (panel, files, dialogs, localFiles);
    }

    [Fact]
    public async Task Opening_a_file_shows_the_preview_and_closing_goes_back_to_the_tree()
    {
        var (panel, _, _, _) = await Create();
        var readme = panel.Tree.Roots.Single(n => n.Name == "README.md");

        await panel.OpenFileCommand.ExecuteAsync(readme);

        Assert.True(panel.HasPreview);
        Assert.False(panel.ShowsTree);
        Assert.Equal("hello", panel.Preview!.DisplayText);

        await panel.ClosePreviewCommand.ExecuteAsync(null);
        Assert.False(panel.HasPreview);
        Assert.True(panel.ShowsTree);
    }

    [Fact]
    public async Task Closing_a_dirty_preview_asks_first_and_keeps_it_when_declined()
    {
        var (panel, _, dialogs, _) = await Create();
        await panel.OpenFileCommand.ExecuteAsync(panel.Tree.Roots.Single(n => n.Name == "README.md"));
        panel.Preview!.EditCommand.Execute(null);
        panel.Preview.EditText = "changed";
        dialogs.Confirm = false;

        await panel.ClosePreviewCommand.ExecuteAsync(null);
        Assert.True(panel.HasPreview);

        dialogs.Confirm = true;
        await panel.ClosePreviewCommand.ExecuteAsync(null);
        Assert.False(panel.HasPreview);
    }

    [Fact]
    public async Task New_file_in_a_directory_creates_it_there_refreshes_that_directory_and_opens_it()
    {
        var (panel, files, dialogs, _) = await Create();
        dialogs.Answers.Enqueue(" Extra.cs ");
        var src = panel.Tree.Roots.Single(n => n.Name == "src");

        await panel.NewFileCommand.ExecuteAsync(src);

        Assert.Contains("createFile:src/Extra.cs", files.Mutations);
        Assert.Contains(src.Children, c => c.Name == "Extra.cs");
        Assert.Equal("src/Extra.cs", panel.Preview?.Path);
    }

    [Fact]
    public async Task New_file_on_a_file_creates_it_next_to_it_and_a_cancelled_prompt_does_nothing()
    {
        var (panel, files, dialogs, _) = await Create();
        dialogs.Answers.Enqueue(null);
        await panel.NewFileCommand.ExecuteAsync(null);
        Assert.Empty(files.Mutations);

        dialogs.Answers.Enqueue("b.cs");
        var app = panel.Tree.Find("src/App.cs")!;
        await panel.NewFileCommand.ExecuteAsync(app);
        Assert.Contains("createFile:src/b.cs", files.Mutations);
    }

    [Fact]
    public async Task Rename_keeps_the_directory_and_closes_a_preview_of_the_old_path()
    {
        var (panel, files, dialogs, _) = await Create();
        var app = panel.Tree.Find("src/App.cs")!;
        await panel.OpenFileCommand.ExecuteAsync(app);
        dialogs.Answers.Enqueue("Main.cs");

        await panel.RenameCommand.ExecuteAsync(app);

        Assert.Contains("rename->src/Main.cs:src/App.cs", files.Mutations);
        Assert.False(panel.HasPreview);
        Assert.NotNull(panel.Tree.Find("src/Main.cs"));
        Assert.Null(panel.Tree.Find("src/App.cs"));
    }

    [Fact]
    public async Task Delete_asks_for_confirmation_and_goes_to_the_trash_via_the_device()
    {
        var (panel, files, dialogs, _) = await Create();
        var readme = panel.Tree.Roots.Single(n => n.Name == "README.md");
        dialogs.Confirm = false;
        await panel.DeleteCommand.ExecuteAsync(readme);
        Assert.Empty(files.Mutations);

        dialogs.Confirm = true;
        await panel.DeleteCommand.ExecuteAsync(readme);
        Assert.Contains("delete:README.md", files.Mutations);
        Assert.Null(panel.Tree.Find("README.md"));
    }

    [Fact]
    public async Task Local_actions_use_absolute_paths_and_reveal_open_are_only_for_local_projects()
    {
        var (panel, _, _, local) = await Create(local: true);
        var readme = panel.Tree.Roots.Single(n => n.Name == "README.md");

        await panel.CopyPathCommand.ExecuteAsync(readme);
        await panel.CopyRelativePathCommand.ExecuteAsync(readme);
        await panel.RevealCommand.ExecuteAsync(readme);
        await panel.OpenExternallyCommand.ExecuteAsync(readme);
        await panel.SaveAsCommand.ExecuteAsync(readme);

        var root = Path.GetFullPath("/work/demo");
        Assert.Equal([$"copy:{Path.Combine(root, "README.md")}", "copy:README.md", $"reveal:{Path.Combine(root, "README.md")}", $"open:{Path.Combine(root, "README.md")}", "save:README.md:5"], local.Calls);

        var (remote, _, _, remoteLocal) = await Create(local: false);
        var remoteReadme = remote.Tree.Roots.Single(n => n.Name == "README.md");
        await remote.RevealCommand.ExecuteAsync(remoteReadme);
        await remote.OpenExternallyCommand.ExecuteAsync(remoteReadme);
        await remote.CopyRelativePathCommand.ExecuteAsync(remoteReadme);
        Assert.Equal(["copy:README.md"], remoteLocal.Calls); // 远程设备的项目:不能「显示 / 打开」,复制路径照常
    }

    [Fact]
    public async Task Search_by_name_and_by_content_show_results_and_clicking_one_opens_the_file_at_its_line()
    {
        var (panel, files, _, _) = await Create();
        files.Add("docs/guide.md", "intro\nTODO: write\nend");

        panel.Search.Query = "guide";
        await panel.Search.Settled;
        Assert.True(panel.ShowsSearch);
        var byName = Assert.Single(panel.Search.Results);
        Assert.Equal("docs/guide.md", byName.Path);

        panel.Search.Mode = FileSearchMode.Contents;
        panel.Search.Query = "todo";
        await panel.Search.Settled;
        var hit = Assert.Single(panel.Search.Results);
        Assert.Equal((2, "TODO: write"), (hit.Line, hit.Text));

        await panel.OpenResultCommand.ExecuteAsync(hit);
        Assert.Equal(2, panel.Preview!.Line);
        Assert.Equal("docs/guide.md", panel.Preview.Path);
    }

    [Fact]
    public async Task A_newer_query_replaces_an_older_one_and_clearing_it_shows_the_tree_again()
    {
        var (panel, _, _, _) = await Create();

        panel.Search.Query = "app";
        panel.Search.Query = "readme";
        await panel.Search.Settled;
        Assert.Equal(["README.md"], panel.Search.Results.Select(r => r.Path));

        panel.Search.Query = "";
        Assert.False(panel.Search.IsActive);
        Assert.True(panel.ShowsTree);
        Assert.Empty(panel.Search.Results);
    }
}
