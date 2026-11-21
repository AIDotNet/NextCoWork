using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>临时预览的标签样式、双击固定和文件路径条的真实按钮交互。</summary>
public class WorkspacePreviewUiTests
{
    private const string FilePath = "src/Ncw.Client/ViewModels/InspectorViewModel.cs";

    private static (Window Window, WorkspaceArea Area, ShellViewModel Shell) Render()
    {
        ArcTheme.Apply(t => t.MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
        var rig = InspectorRig.Create();
        rig.Files.SetContent(InspectorRig.Project, FilePath, "public class InspectorViewModel {}\n");
        var shell = new ShellViewModel(rig.Stores, inspector: rig.Services);
        var area = new WorkspaceArea { DataContext = shell };
        var window = new Window { Width = 1200, Height = 800, Content = area };
        window.Show();
        Dispatch();
        return (window, area, shell);
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 3; i++)
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
        frame.Save(Path.Combine(directory, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    private static void Click(Window window, Visual target)
    {
        var center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Target is not in the window.");
        window.MouseMove(center);
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatch();
    }

    [AvaloniaFact]
    public async Task Double_clicking_a_preview_tab_pins_it_and_removes_italic_styling()
    {
        var (window, area, shell) = Render();
        try
        {
            await shell.Workspace.OpenPreviewAsync(InspectorRig.Project, FilePath);
            Dispatch();
            var file = shell.Workspace.ActiveFile!;
            var tab = area.GetVisualDescendants().OfType<PlainButton>()
                .Single(b => b.DataContext is WorkspaceTabViewModel t && ReferenceEquals(t.File, file) && ReferenceEquals(b.Command, shell.Workspace.ActivateCommand));
            var title = tab.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == file.Name);
            Assert.Contains("preview", title.Classes);
            Assert.Equal(FontStyle.Italic, title.FontStyle);
            Snapshot(window, "workspace-preview-tab");

            Click(window, tab);
            Click(window, tab);

            Assert.False(file.IsPreview);
            Assert.DoesNotContain("preview", title.Classes);
            Assert.Equal(FontStyle.Normal, title.FontStyle);
            Snapshot(window, "workspace-pinned-tab");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Breadcrumb_buttons_reveal_directory_and_file_paths_and_pin_button_hides_after_pinning()
    {
        var (window, area, shell) = Render();
        try
        {
            string? revealed = null;
            shell.Workspace.RevealRequested = (_, path) => revealed = path;
            await shell.Workspace.OpenPreviewAsync(InspectorRig.Project, FilePath);
            Dispatch();
            var file = shell.Workspace.ActiveFile!;
            var view = area.GetVisualDescendants().OfType<FileTabView>().Single();
            var crumbs = view.GetVisualDescendants().OfType<PlainButton>()
                .Where(b => ReferenceEquals(b.Command, file.RevealBreadcrumbCommand)).ToArray();
            Assert.Equal(file.Breadcrumbs.Count, crumbs.Length);
            Snapshot(window, "workspace-file-breadcrumbs");
            Click(window, crumbs[1]);
            Assert.Equal("src/Ncw.Client", revealed);
            Click(window, crumbs[^1]);
            Assert.Equal(FilePath, revealed);

            var pin = view.GetVisualDescendants().OfType<NcwButton>().Single(b => ReferenceEquals(b.Command, file.PinCommand));
            Assert.True(pin.IsVisible);
            Click(window, pin);
            Assert.False(file.IsPreview);
            Assert.False(pin.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Reference_button_requires_a_session_and_returns_to_the_conversation_without_closing_file()
    {
        var (window, area, shell) = Render();
        try
        {
            string? referenced = null;
            var canReference = false;
            shell.Workspace.ReferenceRequested = (_, path) => referenced = path;
            shell.Workspace.CanReferenceRequested = _ => canReference;
            await shell.Workspace.OpenPreviewAsync(InspectorRig.Project, FilePath);
            Dispatch();
            var file = shell.Workspace.ActiveFile!;
            var view = area.GetVisualDescendants().OfType<FileTabView>().Single();
            var reference = view.GetVisualDescendants().OfType<NcwButton>().Single(b => ReferenceEquals(b.Command, file.ReferenceCommand));
            Assert.True(reference.IsVisible);
            Assert.False(reference.IsEnabled);
            Click(window, reference);
            Assert.Null(referenced);
            Assert.Same(file, shell.Workspace.ActiveFile);

            canReference = true;
            file.RefreshReferenceAvailability();
            Dispatch();
            Assert.True(reference.IsEnabled);
            Snapshot(window, "workspace-file-reference");
            Click(window, reference);
            Assert.Equal(FilePath, referenced);
            Assert.Null(shell.Workspace.ActiveFile);
            Assert.Contains(shell.Workspace.Tabs, t => ReferenceEquals(t.File, file));
            Assert.False(view.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
