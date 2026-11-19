using Avalonia.Controls;
using Ncw.Client.ViewModels;

namespace Ncw.UI.Views.Desktop.Models;

/// <summary>模型页的提示(只读、失败、OAuth、导入模型)。新建 / 编辑供应商改成了弹窗(<see cref="Dialogs.ProviderEditorDialog"/>)。按钮的意图都在 ViewModel 里。</summary>
public partial class ModelsNotices : UserControl
{
    public ModelsNotices()
    {
        InitializeComponent();
        // 导入弹窗的按钮:更新列表 / 取消 都是 ViewModel 的命令
        ApplyImportButton.Click += (_, _) => Vm?.ApplyImportCommand.Execute(null);
        CloseImportButton.Click += (_, _) => Vm?.CloseImportCommand.Execute(null);
    }

    private ModelsSettingsViewModel? Vm => DataContext as ModelsSettingsViewModel;
}
