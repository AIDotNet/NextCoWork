using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;
using Ncw.UI.Localization;

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
            new(NcwTexts.Get(NcwTextKind.FilesCopyContent), file.CopyContentCommand, null),
            new(NcwTexts.Get(NcwTextKind.FilesCopyPath), file.CopyPathCommand, null, SeparatorBefore: true),
            new(NcwTexts.Get(NcwTextKind.FilesCopyRelativePath), file.CopyRelativePathCommand, null),
        ];
        if (file.CanOpenLocally)
        {
            items.Add(new(NcwTexts.Get(NcwTextKind.FilesReveal), file.RevealCommand, null, SeparatorBefore: true));
            items.Add(new(NcwTexts.Get(NcwTextKind.FilesOpenWith), file.OpenExternallyCommand, null));
        }

        items.Add(new(NcwTexts.Get(NcwTextKind.FilesSaveAs), file.SaveAsCommand, null, SeparatorBefore: true));
        NcwMenus.Populate(MoreMenu, items);
    }
}
