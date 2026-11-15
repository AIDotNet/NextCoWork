using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.UI.Controls;

namespace Ncw.UI.Views.Mobile;

/// <summary>
/// 扫码配对页。相机预览由宿主提供(<see cref="IQrPreviewProvider"/>,返回原生预览控件);没有时占位框一直显示。
/// 页面离开视图树(返回)时把流程重置(取消进行中的扫描)。流程本身在 <see cref="QrPairingViewModel"/>。
/// </summary>
public partial class QrPairPage : NcwPage
{
    public QrPairPage() => InitializeComponent();

    /// <summary>宿主的预览提供者(<c>ShellViewModel.Host.QrScanner</c> 同时实现 <see cref="IQrPreviewProvider"/> 时)。</summary>
    public IQrPreviewProvider? Preview
    {
        set
        {
            if (value is not null && value.CreatePreview() is Avalonia.Controls.Control control)
            {
                PreviewHost.Content = control;
                PreviewPlaceholder.IsVisible = false;
            }
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as QrPairingViewModel)?.ResetCommand.Execute(null);
    }
}
