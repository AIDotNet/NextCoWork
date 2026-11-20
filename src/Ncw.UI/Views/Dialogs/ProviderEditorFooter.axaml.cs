using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;

namespace Ncw.UI.Views.Dialogs;

/// <summary>「添加 / 编辑供应商」的按钮行(取消按钮的关窗口由 NcwDialogService 接)。「保存」的文字跟设备走:能调试的是「保存」,老设备是「保存并测试」。</summary>
public partial class ProviderEditorFooter : UserControl
{
    public ProviderEditorFooter()
    {
        InitializeComponent();
    }

    internal Button Cancel => CancelButton;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var tests = (DataContext as ProviderEditorViewModel)?.SaveTests ?? true;
        SaveButton.Content = NcwTexts.Get(tests ? NcwTextKind.ProviderEditorSaveAndTest : NcwTextKind.Save);
        SavingText.Text = NcwTexts.Get(tests ? NcwTextKind.ProviderEditorSaving : NcwTextKind.ProviderEditorSavingOnly);
    }
}
