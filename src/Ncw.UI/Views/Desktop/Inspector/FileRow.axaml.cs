using Avalonia;
using Avalonia.Controls;
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
    public FileRow() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    private void Rebuild()
    {
        if (DataContext is not FileNodeViewModel { IsEntry: true } node || this.FindAncestorOfType<FilesPane>()?.DataContext is not FilesPanelViewModel panel)
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
