using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Ncw.Client.Services;
using Ncw.Client.ViewModels;
using Ncw.UI.Converters;
using Ncw.UI.Localization;

namespace Ncw.UI.Views.Dialogs;

/// <summary>
/// 「添加 / 编辑供应商」窗口的内容。这里只放纯 UI 的事:网格列数跟内容宽度走、中间区域的最大高度跟窗口高度走、
/// 协议下拉框的文字、测试失败那一句话的拼接、「获取 API Key」交给浏览器。意图都是 ViewModel 的命令。
/// </summary>
public partial class ProviderEditorDialog : UserControl
{
    /// <summary>
    /// 窗口里除了中间区域以外的高度:Arc 弹窗离窗口上下各 16、标题栏(标题 + 一行说明 + 分隔线)约 120、按钮行约 90。
    /// 中间区域最多 = 窗口高 − 这个值(下限 240),这样按钮行永远在窗口里。
    /// </summary>
    private const double ChromeHeight = 260;

    private ProviderEditorViewModel? _vm;

    public ProviderEditorDialog()
    {
        InitializeComponent();
        Tag = 3;
        KindSelect.ItemsSource = ProviderEditorViewModel.Kinds;
        KindSelect.TextSelector = item => ProviderKindTextConverter.Text(item as string);
        ProtocolSelect.ItemsSource = ProviderEditorViewModel.Protocols;
        ProtocolSelect.TextSelector = item => ProtocolTextConverter.Text(item as string);
        KeyUrlButton.Click += (_, _) =>
        {
            if (_vm?.KeyUrl is { } url && Launcher is { } launcher)
            {
                _ = launcher.OpenAsync(url);
            }
        };
    }

    /// <summary>打开「获取 API Key」链接用(NcwDialogService 给)。</summary>
    internal IUrlLauncher? Launcher { get; set; }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = DataContext as ProviderEditorViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnVmChanged;
        }

        UpdateProtocols();
        UpdateTestFailed();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProviderEditorViewModel.Kind) or nameof(ProviderEditorViewModel.AvailableProtocols):
                UpdateProtocols();
                break;
            case nameof(ProviderEditorViewModel.TestFailed) or nameof(ProviderEditorViewModel.TestErrorKey):
                UpdateTestFailed();
                break;
            case nameof(ProviderEditorViewModel.Step):
                Body.Offset = default;
                FocusStep();
                break;
        }
    }

    private void UpdateProtocols()
    {
        if (_vm is null)
        {
            return;
        }

        ProtocolSelect.ItemsSource = _vm.AvailableProtocols;
        ProtocolSelect.SelectedValue = _vm.Protocol;
    }

    private void UpdateTestFailed() =>
        TestFailedText.Text = _vm?.TestErrorKey is { } key ? NcwTexts.Format(NcwTextKind.ProviderEditorTestFailed, ProviderErrorTextConverter.Text(key)) : string.Empty;

    /// <summary>打开后 / 换步骤后的焦点:第一步在搜索框,第二步在 API Key(没有 key 的在名字)。</summary>
    internal void FocusStep()
    {
        if (_vm is null)
        {
            return;
        }

        if (_vm.IsPickStep)
        {
            SearchInput.Focus();
        }
        else if (_vm.NeedsKey)
        {
            KeyInput.Focus();
        }
        else
        {
            NameInput.Focus();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var columns = e.NewSize.Width >= 560 ? 3 : 2;
        if (!Equals(Tag, columns))
        {
            Tag = columns;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            Body.MaxHeight = Math.Max(240, top.Bounds.Height - ChromeHeight);
            top.PropertyChanged += OnTopLevelChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.PropertyChanged -= OnTopLevelChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty && sender is TopLevel top)
        {
            Body.MaxHeight = Math.Max(240, top.Bounds.Height - ChromeHeight);
        }
    }
}
