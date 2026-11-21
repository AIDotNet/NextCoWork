using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Views.Desktop.Inspector;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Ncw.UI.Tests;

public class ExplorerInteractionTests
{
    [AvaloniaFact]
    public async Task Compact_row_uses_display_chain_and_hover_reference_click_targets_real_file()
    {
        var (window, pane, panel, _) = await RenderAsync();
        await panel.RevealPathAsync("chain/one/two/leaf.cs");
        Dispatch();
        var directory = panel.Tree.Find("chain")!;
        Assert.Equal("chain/one/two", directory.DisplayName);
        var directoryRow = Assert.Single(pane.GetVisualDescendants().OfType<FileRow>(), r => r.DataContext == directory);
        Assert.Contains(directoryRow.GetVisualDescendants().OfType<TextBlock>(), label => label.Text == directory.DisplayName);
        var file = panel.Tree.Find("chain/one/two/leaf.cs")!;
        var row = Assert.Single(pane.GetVisualDescendants().OfType<FileRow>(), r => r.DataContext == file);
        var button = row.FindControl<NcwButton>("ReferenceButton")!;
        string? referenced = null;
        panel.ReferenceRequested = (_, path) => referenced = path;
        Click(window, button);
        Assert.Equal(file.Path, referenced);
        panel.IsReferenceAvailable = false;
        Dispatch();
        Assert.False(button.IsVisible);
        panel.IsReferenceAvailable = true;
        Dispatch();
        Assert.True(button.IsVisible);
        Assert.False(panel.IsFilterVisible);
        Assert.Equal(25, pane.FindControl<NcwTreeView>("Tree")!.RowHeight);
        Assert.True(pane.FindControl<NcwTreeView>("Tree")!.ShowIndentGuides);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Real_inline_editor_enter_commits_and_escape_cancels()
    {
        var (window, pane, panel, _) = await RenderAsync();
        await panel.RevealPathAsync("README.md");
        await panel.RenameCommand.ExecuteAsync(panel.Tree.Find("README.md")!);
        Dispatch();
        var row = Assert.Single(pane.GetVisualDescendants().OfType<FileRow>(), r => r.DataContext is FileNodeViewModel { IsEditing: true });
        var editor = row.FindControl<NcwInput>("NameEditor")!;
        Assert.True(editor.IsFocused || editor.IsKeyboardFocusWithin, $"Visible={editor.IsVisible}, effective={editor.IsEffectivelyVisible}, enabled={editor.IsEffectivelyEnabled}, focusable={editor.Focusable}, bounds={editor.Bounds}, focused={window.FocusManager?.GetFocusedElement()?.GetType().Name}");
        editor.Text = "renamed-readme.md";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Dispatch();
        Assert.NotNull(panel.Tree.Find("renamed-readme.md"));
        Assert.DoesNotContain(pane.GetVisualDescendants().OfType<FileRow>(), r => r.DataContext is FileNodeViewModel { IsEditing: true });

        await panel.NewFileCommand.ExecuteAsync(null);
        Dispatch();
        row = Assert.Single(pane.GetVisualDescendants().OfType<FileRow>(), r => r.DataContext is FileNodeViewModel { IsDraft: true });
        editor = row.FindControl<NcwInput>("NameEditor")!;
        Assert.True(editor.IsFocused || editor.IsKeyboardFocusWithin, $"Visible={editor.IsVisible}, effective={editor.IsEffectivelyVisible}, enabled={editor.IsEffectivelyEnabled}, focusable={editor.Focusable}, bounds={editor.Bounds}, focused={window.FocusManager?.GetFocusedElement()?.GetType().Name}");
        editor.Text = "cancelled.md";
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
        Dispatch();
        Assert.DoesNotContain(panel.Tree.Roots, n => n.IsDraft);
        Assert.Null(panel.Tree.Find("cancelled.md"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Keyboard_filter_shortcut_opens_focuses_and_escape_clears()
    {
        var (window, pane, panel, _) = await RenderAsync();
        var tree = pane.FindControl<NcwTreeView>("Tree")!;
        tree.FocusItem(panel.Tree.Roots[0]);
        window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f");
        Dispatch();
        Assert.True(panel.IsFilterVisible);
        var editor = pane.FindControl<NcwSearchField>("SearchBox")!;
        Assert.True(editor.IsFocused || editor.IsKeyboardFocusWithin, $"Visible={editor.IsVisible}, effective={editor.IsEffectivelyVisible}, enabled={editor.IsEffectivelyEnabled}, focusable={editor.Focusable}, bounds={editor.Bounds}, focused={window.FocusManager?.GetFocusedElement()?.GetType().Name}");
        panel.Search.Query = "readme";
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, "");
        Dispatch();
        Assert.False(panel.IsFilterVisible);
        Assert.Empty(panel.Search.Query);
        window.Close();
    }

    private static async Task<(Window Window, FilesPane Pane, FilesPanelViewModel Panel, InspectorRig Rig)> RenderAsync()
    {
        ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark).MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
        var rig = InspectorRig.Create();
        rig.Files.SetContent(InspectorRig.Project, "docs/design/03-ui-layout.md", "# Design");
        rig.Files.SetContent(InspectorRig.Project, "README.md", "# Readme");
        rig.Files.SetContent(InspectorRig.Project, "chain/one/two/leaf.cs", "class Leaf {}");
        var panel = new FilesPanelViewModel(rig.Files, () => new NoDialogs(), () => DefaultInspectorText.Instance, () => NoLocalFiles.Instance)
        {
            IsReferenceAvailable = true,
        };
        await panel.BindAsync(InspectorRig.Project, "Demo", "/demo", true, false);
        var pane = new FilesPane { DataContext = panel };
        var window = new Window { Width = 280, Height = 560, Content = pane };
        window.Show();
        Dispatch();
        return (window, pane, panel, rig);
    }

    private static void Dispatch()
    {
        for (var i = 0; i < 8; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void Click(Window window, Visual target)
    {
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Target is not in the window.");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatch();
    }

    private sealed class NoDialogs : IDialogService
    {
        public Task<string?> PromptTextAsync(string title, string description, string label, string? initial) => throw new InvalidOperationException("Inline editor expected.");

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task ShowErrorAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel) => Task.FromResult(false);
    }
}
