using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Ncw.UI.Views.Desktop;

/// <summary>
/// 文件标签页的视图。code-behind 只管「更多」菜单的条目(复制路径、显示、用默认程序打开、另存为),动作都在 <see cref="FilePreviewViewModel"/> 上。
/// </summary>
public partial class FileTabView : UserControl
{
    public FileTabView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not FilePreviewViewModel file)
        {
            return;
        }

        List<NcwMenus.Item> items =
        [
            new(NcwTexts.Get(NcwTextKind.FilesReferenceInChat), file.ReferenceCommand, null) { Glyph = Lucide.Geometry(LucideIconKind.AtSign) },
            new(NcwTexts.Get(NcwTextKind.FilePinTab), file.PinCommand, null) { Glyph = Lucide.Geometry(LucideIconKind.Pin) },
            new(NcwTexts.Get(NcwTextKind.FilesCopyContent), file.CopyContentCommand, null) { Glyph = Lucide.Geometry(LucideIconKind.Clipboard) },
            new(NcwTexts.Get(NcwTextKind.FilesCopyPath), file.CopyPathCommand, null, SeparatorBefore: true) { Glyph = ArcIcons.Copy },
            new(NcwTexts.Get(NcwTextKind.FilesCopyRelativePath), file.CopyRelativePathCommand, null) { Glyph = Lucide.Geometry(LucideIconKind.ClipboardCopy) },
        ];
        if (file.CanOpenLocally)
        {
            items.Add(new(NcwTexts.Get(NcwTextKind.FilesReveal), file.RevealCommand, null, SeparatorBefore: true) { Glyph = ArcIcons.FolderOpen });
            items.Add(new(NcwTexts.Get(NcwTextKind.FilesOpenWith), file.OpenExternallyCommand, null) { Glyph = Lucide.Geometry(LucideIconKind.ExternalLink) });
        }

        items.Add(new(NcwTexts.Get(NcwTextKind.FilesSaveAs), file.SaveAsCommand, null, SeparatorBefore: true) { Glyph = Lucide.Geometry(LucideIconKind.Save) });
        NcwMenus.Populate(MoreMenu, items);
    }
}
