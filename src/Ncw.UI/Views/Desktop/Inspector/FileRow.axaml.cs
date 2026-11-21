using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Views.Desktop.Inspector;

/// <summary>文件树的一行。右键菜单按节点类型(目录 / 文件)和项目所在位置(本机才有「在文件管理器中显示」)挂上;命令都是 <see cref="FilesPanelViewModel"/> 的意图。</summary>
public partial class FileRow : UserControl
{
    private FileNodeViewModel? _node;
    private bool _focusPending;

    public FileRow()
    {
        InitializeComponent();
        PointerEntered += (_, _) => Rebuild();
        LayoutUpdated += (_, _) => FocusEditorIfNeeded();
    }

    internal void FocusInlineEditor()
    {
        if (_node is { IsEditing: true })
        {
            _focusPending = true;
            FocusEditorIfNeeded();
        }
    }

    private void FocusEditorIfNeeded()
    {
        if (_focusPending && _node is { IsEditing: true } && NameEditor.IsEffectivelyVisible && NameEditor.Focus())
        {
            NameEditor.SelectAll();
            _focusPending = false;
        }
    }

    private void OnNodeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileNodeViewModel.IsEditing))
        {
            _focusPending = _node is { IsEditing: true };
            Dispatcher.UIThread.Post(FocusEditorIfNeeded, DispatcherPriority.Background);
        }
    }

    private FilesPanelViewModel? Panel => this.FindAncestorOfType<FilesPane>()?.DataContext as FilesPanelViewModel;

    private void OnReferenceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileNodeViewModel node) Panel?.ReferenceCommand.Execute(node);
        e.Handled = true;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Panel?.CommitInlineEditCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Panel?.CancelInlineEditCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FileNodeViewModel { IsEditing: true } && this.FindAncestorOfType<FilesPane>() is { IsSynchronizing: false } && Panel is { } panel && !panel.CommitInlineEditCommand.IsRunning)
        {
            panel.CancelInlineEditCommand.Execute(null);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_node is not null) _node.PropertyChanged -= OnNodeChanged;
        _node = DataContext as FileNodeViewModel;
        if (_node is not null) _node.PropertyChanged += OnNodeChanged;
        _focusPending = _node is { IsEditing: true };
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_node is not null)
        {
            _node.PropertyChanged -= OnNodeChanged;
            _node.PropertyChanged += OnNodeChanged;
        }

        Rebuild();
        _focusPending = _node is { IsEditing: true };
        Dispatcher.UIThread.Post(FocusEditorIfNeeded, DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_node is not null) _node.PropertyChanged -= OnNodeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void Rebuild()
    {
        if (DataContext is not FileNodeViewModel { IsEntry: true, IsDraft: false } node || Panel is not { } panel)
        {
            return;
        }

        List<NcwMenus.Item> items = [];
        if (node.IsDirectory)
        {
            items.Add(new(NcwTexts.Get(NcwTextKind.FilesNewFile), panel.NewFileCommand, node) { IsDisabled = !panel.CanWrite, Glyph = Lucide.Geometry(LucideIconKind.FilePlus) });
            items.Add(new(NcwTexts.Get(NcwTextKind.FilesNewFolder), panel.NewFolderCommand, node) { IsDisabled = !panel.CanWrite, Glyph = Lucide.Geometry(LucideIconKind.FolderPlus) });
        }
        else
        {
            if (panel.IsReferenceAvailable)
            {
                items.Add(new(NcwTexts.Get(NcwTextKind.FilesReferenceInChat), panel.ReferenceCommand, node) { Glyph = Lucide.Geometry(LucideIconKind.AtSign) });
            }

            items.Add(new(NcwTexts.Get(NcwTextKind.FileOpen), panel.OpenFileCommand, node) { Glyph = Lucide.Geometry(LucideIconKind.FileText) });
        }

        items.Add(new(NcwTexts.Get(NcwTextKind.Rename), panel.RenameCommand, node, SeparatorBefore: true) { IsDisabled = !panel.CanWrite, Glyph = ArcIcons.Pencil });
        items.Add(new(NcwTexts.Get(NcwTextKind.FilesCopyPath), panel.CopyPathCommand, node) { Glyph = ArcIcons.Copy });
        items.Add(new(NcwTexts.Get(NcwTextKind.FilesCopyRelativePath), panel.CopyRelativePathCommand, node) { Glyph = Lucide.Geometry(LucideIconKind.ClipboardCopy) });
        if (panel.CanOpenLocally)
        {
            items.Add(new(NcwTexts.Get(NcwTextKind.FilesReveal), panel.RevealCommand, node, SeparatorBefore: true) { Glyph = ArcIcons.FolderOpen });
            if (node.IsFile)
            {
                items.Add(new(NcwTexts.Get(NcwTextKind.FilesOpenWith), panel.OpenExternallyCommand, node) { Glyph = Lucide.Geometry(LucideIconKind.ExternalLink) });
            }
        }

        if (node.IsFile)
        {
            items.Add(new(NcwTexts.Get(NcwTextKind.FilesSaveAs), panel.SaveAsCommand, node) { Glyph = Lucide.Geometry(LucideIconKind.Save) });
        }

        items.Add(new(NcwTexts.Get(NcwTextKind.FilesMoveToTrash), panel.DeleteCommand, node, IsDestructive: true, SeparatorBefore: true) { IsDisabled = !panel.CanWrite, Glyph = ArcIcons.Trash2 });
        NcwMenus.Attach(this, items);
    }
}
