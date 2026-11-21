using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Inspector;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Ncw.UI.Tests;

/// <summary>名称优先、最长后缀与 Arc 动态色板的文件图标;树、结果、标签共用解析器。</summary>
public class FileIconTests
{
    private static readonly (string Path, bool Directory)[] Gallery =
    [
        ("Program.cs", false), ("App.csproj", false), ("NextCoWork.slnx", false), ("global.json", false),
        ("View.axaml", false), ("View.xaml", false), ("settings.json", false), ("README.md", false),
        ("ci.yaml", false), ("config.toml", false), ("data.xml", false), ("index.d.ts", false),
        ("index.ts", false), ("index.js", false), ("main.py", false), ("main.go", false),
        ("main.rs", false), ("index.html", false), ("style.css", false), ("setup.sh", false),
        ("setup.ps1", false), ("schema.sql", false), ("image.svg", false), ("archive.tar.gz", false),
        ("LICENSE.md", false), ("Dockerfile.prod", false), (".gitignore", false), (".editorconfig", false),
        (".env.local", false), ("unknown.bin", false), ("src", true), ("tests", true),
        ("docs", true), ("assets", true), (".git", true), ("unknown", true),
    ];

    [Theory]
    [InlineData("Program.CS", NcwFileIconKind.CSharp)]
    [InlineData("LICENSE.md", NcwFileIconKind.License)]
    [InlineData("nested/LICENSE.TXT", NcwFileIconKind.License)]
    [InlineData("GLOBAL.JSON", NcwFileIconKind.Build)]
    [InlineData("NuGet.Config", NcwFileIconKind.Config)]
    [InlineData("package.json", NcwFileIconKind.Project)]
    [InlineData("Dockerfile.yaml", NcwFileIconKind.Docker)]
    [InlineData("Containerfile", NcwFileIconKind.Docker)]
    [InlineData(".env.json", NcwFileIconKind.Environment)]
    [InlineData(".gitignore", NcwFileIconKind.Git)]
    [InlineData(".gitattributes", NcwFileIconKind.Git)]
    [InlineData(".editorconfig", NcwFileIconKind.Config)]
    [InlineData("file.unknown", NcwFileIconKind.File)]
    [InlineData(".unknown", NcwFileIconKind.File)]
    [InlineData("extensionless", NcwFileIconKind.File)]
    [InlineData("", NcwFileIconKind.File)]
    public void File_names_have_priority_over_extensions_and_unknown_files_have_a_fallback(string path, NcwFileIconKind kind)
    {
        Assert.Equal(kind, NcwFileIcon.Resolve(path, false).Kind);
        Assert.Equal(NcwFileIcon.Resolve(path, false), NcwFileIcon.Resolve(path.ToUpperInvariant(), false));
    }

    [Fact]
    public void Longest_suffix_and_both_platform_path_separators_are_resolved()
    {
        var declaration = NcwFileIcon.Resolve("src/index.d.ts", false);
        Assert.Equal(NcwFileIconKind.TypeScript, declaration.Kind);
        Assert.Equal(NcwFileIconShape.Code, declaration.Shape);
        Assert.Equal("TS", declaration.Mark);
        Assert.Equal(NcwFileIconShape.Document, NcwFileIcon.Resolve("index.ts", false).Shape);
        Assert.Equal(NcwFileIconKind.Archive, NcwFileIcon.Resolve("archive.tar.gz", false).Kind);
        Assert.Equal(declaration, NcwFileIcon.Resolve("C:\\src\\INDEX.D.TS", false));
    }

    [Theory]
    [InlineData("SRC", NcwFileIconKind.SourceFolder)]
    [InlineData("source", NcwFileIconKind.SourceFolder)]
    [InlineData("project/tests/", NcwFileIconKind.TestsFolder)]
    [InlineData("DOCS", NcwFileIconKind.DocsFolder)]
    [InlineData("images", NcwFileIconKind.AssetsFolder)]
    [InlineData(".git", NcwFileIconKind.GitFolder)]
    [InlineData("unknown.cs", NcwFileIconKind.Folder)]
    public void Directory_classification_is_separate_from_file_extensions(string path, NcwFileIconKind kind)
    {
        var icon = NcwFileIcon.Resolve(path, true);
        Assert.Equal(kind, icon.Kind);
        Assert.Equal(NcwFileIconShape.Folder, icon.Shape);
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 4; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Snapshot(Window window, string name)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }

    private static byte[] RenderIcon(NcwFileIcon icon)
    {
        using var frame = new RenderTargetBitmap(new PixelSize(16, 16), new Vector(96, 96));
        frame.Render(icon);
        using var bytes = new MemoryStream();
        frame.Save(bytes, new PngBitmapEncoderOptions());
        return bytes.ToArray();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void File_icon_gallery_is_legible_at_tree_and_tab_sizes_with_a_distinct_theme_palette(bool dark)
    {
        ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
        var gallery = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 230, ItemHeight = 34 };
        foreach (var (path, directory) in Gallery)
        {
            gallery.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new NcwFileIcon { Path = path, IsDirectory = directory, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
                    new NcwFileIcon { Path = path, IsDirectory = directory, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
                    new NcwFileIcon { Path = path, IsDirectory = directory, IsExpanded = directory, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = path, VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }

        var host = new Border { Padding = new Thickness(16), Child = gallery };
        var window = new Window { Width = 980, Height = 380, Content = host };
        try
        {
            window.Show();
            host.Background = new SolidColorBrush(ArcTokens.GetColor(window, ArcDesignTokenKind.Background));
            Dispatch();
            var colors = Gallery.Select(item => ArcTokens.GetColor(window, NcwFileIcon.Resolve(item.Path, item.Directory).Token)).Distinct().ToArray();
            Assert.True(colors.Length > 3);
            Assert.Equal(Gallery.Length * 3, gallery.GetVisualDescendants().OfType<NcwFileIcon>().Count());
            Snapshot(window, $"file-icons-{(dark ? "dark" : "light")}");
        }
        finally
        {
            window.Close();
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
        }
    }

    [AvaloniaFact]
    public void Switching_theme_updates_the_existing_icon_token_brush_without_recreating_it()
    {
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
        var icon = new NcwFileIcon { Path = "unknown", Width = 16, Height = 16 };
        var window = new Window { Width = 100, Height = 100, Content = new Border { Child = icon } };
        try
        {
            window.Show();
            Dispatch();
            var token = NcwFileIcon.Resolve(icon.Path, false).Token;
            var dark = ArcTokens.GetColor(icon, token);
            var before = RenderIcon(icon);
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Light));
            Dispatch();
            var light = ArcTokens.GetColor(icon, token);
            var after = RenderIcon(icon);
            Assert.NotEqual(dark, light);
            Assert.False(before.SequenceEqual(after));
            Assert.Same(icon, ((Border)window.Content!).Child);
        }
        finally
        {
            window.Close();
            ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark));
        }
    }

    [AvaloniaFact]
    public void Expanded_folder_uses_a_different_outline_without_changing_its_type_or_theme_token()
    {
        var icon = new NcwFileIcon { Path = "src", IsDirectory = true, Width = 16, Height = 16 };
        var window = new Window { Width = 100, Height = 100, Content = new Border { Child = icon } };
        try
        {
            window.Show();
            Dispatch();
            var descriptor = NcwFileIcon.Resolve(icon.Path, icon.IsDirectory);
            var before = RenderIcon(icon);
            icon.IsExpanded = true;
            Dispatch();
            Assert.Equal(descriptor, NcwFileIcon.Resolve(icon.Path, icon.IsDirectory));
            Assert.False(before.SequenceEqual(RenderIcon(icon)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Tree_search_results_and_workspace_tabs_use_the_same_file_icon_descriptor()
    {
        var rig = InspectorRig.Create();
        var shell = new ShellViewModel(rig.Stores, inspector: rig.Services);
        var desktop = new DesktopShell { DataContext = shell, Chrome = new WindowChrome(78, false) };
        var window = new Window { Width = 1500, Height = 850, Content = desktop };
        try
        {
            window.Show();
            await shell.Inspector.Files.RevealPathAsync("README.md");
            Dispatch();
            var pane = desktop.GetVisualDescendants().OfType<FilesPane>().Single();
            var treeIcon = pane.GetVisualDescendants().OfType<FileRow>()
                .SelectMany(row => row.GetVisualDescendants().OfType<NcwFileIcon>()).Single(icon => icon.Path == "README.md");
            Assert.False(treeIcon.IsDirectory);
            shell.Inspector.Files.Tree.ApplyChanges(new Dictionary<string, GitChange>());
            Dispatch();
            Assert.Null(shell.Inspector.Files.Tree.Find("README.md")!.ChangeLetter);
            var initialPixels = RenderIcon(treeIcon);
            shell.Inspector.Files.Tree.ApplyChanges(new Dictionary<string, GitChange> { ["README.md"] = GitChange.Modified });
            Dispatch();
            Assert.Equal("M", shell.Inspector.Files.Tree.Find("README.md")!.ChangeLetter);
            Assert.True(initialPixels.SequenceEqual(RenderIcon(treeIcon)));
            var descriptor = NcwFileIcon.Resolve(treeIcon.Path, treeIcon.IsDirectory);

            shell.Inspector.Files.IsFilterVisible = true;
            shell.Inspector.Files.Search.Query = "README";
            await shell.Inspector.Files.Search.Settled;
            Dispatch();
            var resultIcon = pane.GetVisualDescendants().OfType<NcwFileIcon>()
                .Single(icon => icon.DataContext is FileSearchResult { Path: "README.md" });
            Assert.Equal(descriptor, NcwFileIcon.Resolve(resultIcon.Path, resultIcon.IsDirectory));

            await shell.Workspace.OpenPreviewAsync(InspectorRig.Project, "README.md");
            Dispatch();
            var tabIcon = desktop.GetVisualDescendants().OfType<WorkspaceArea>().Single()
                .GetVisualDescendants().OfType<NcwFileIcon>().Single(icon => icon.DataContext is WorkspaceTabViewModel { Path: "README.md" });
            Assert.Equal(14, tabIcon.Width);
            Assert.Equal(descriptor, NcwFileIcon.Resolve(tabIcon.Path, tabIcon.IsDirectory));
            Snapshot(window, "file-icons-tree-result-tab");
        }
        finally
        {
            window.Close();
        }
    }
}
