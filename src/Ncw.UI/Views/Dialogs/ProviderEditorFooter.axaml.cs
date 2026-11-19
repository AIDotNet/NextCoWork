using Avalonia.Controls;

namespace Ncw.UI.Views.Dialogs;

/// <summary>「添加 / 编辑供应商」的按钮行(取消按钮的关窗口由 NcwDialogService 接)。</summary>
public partial class ProviderEditorFooter : UserControl
{
    public ProviderEditorFooter()
    {
        InitializeComponent();
    }

    internal Button Cancel => CancelButton;
}
