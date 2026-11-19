using Avalonia;
using Avalonia.Controls;
using Ncw.Client.ViewModels;
using Ncw.UI.Converters;
using Ncw.UI.Localization;

namespace Ncw.UI.Views.Dialogs;

/// <summary>
/// 「编辑模型」窗口的内容。这里只放纯 UI 的事:下拉框的文字(协议、思考方式、强度)、中间区域的最大高度跟窗口高度走。意图都是 ViewModel 的命令。
/// </summary>
public partial class ModelEditorDialog : UserControl
{
    /// <summary>窗口里除了中间区域以外的高度(同 <see cref="ProviderEditorDialog"/>):弹窗上下边距、标题栏、按钮行。</summary>
    private const double ChromeHeight = 260;

    public ModelEditorDialog()
    {
        InitializeComponent();
        ProtocolSelect.TextSelector = item => item is "" ? FollowProviderText() : ProtocolTextConverter.Text(item as string);
        ThinkingSelect.TextSelector = item => item as string == ModelEditorViewModel.ThinkingAuto
            ? (DataContext as ModelEditorViewModel)?.EffectiveThinkingMode is { } now
                ? NcwTexts.Format(NcwTextKind.ModelEditorThinkingAutoCurrent, ModeText(now))
                : NcwTexts.Get(NcwTextKind.ModelEditorThinkingAuto)
            : ModeText(item as string);
        DefaultEffortSelect.TextSelector = item => EffortTextConverter.Text(item as string);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ModelEditorViewModel vm)
        {
            ProtocolSelect.ItemsSource = ModelEditorViewModel.ProtocolChoices;
            ThinkingSelect.ItemsSource = vm.ThinkingModes;
        }
    }

    private string FollowProviderText() => (DataContext as ModelEditorViewModel)?.ProviderProtocol is { } protocol
        ? NcwTexts.Format(NcwTextKind.ModelEditorProtocolFollow, ProtocolTextConverter.Text(protocol))
        : NcwTexts.Get(NcwTextKind.ModelEditorProtocolFollowPlain);

    /// <summary>思考控制方式 → 文字(设备的 <c>ThinkingMode</c> 线上名;不认识的照原样)。</summary>
    internal static string ModeText(string? mode) => mode switch
    {
        "unsupported" => NcwTexts.Get(NcwTextKind.ModelEditorThinkingUnsupported),
        "always" => NcwTexts.Get(NcwTextKind.ModelEditorThinkingAlways),
        "toggle" => NcwTexts.Get(NcwTextKind.ModelEditorThinkingToggle),
        "budget" => NcwTexts.Get(NcwTextKind.ModelEditorThinkingBudget),
        "effort" => NcwTexts.Get(NcwTextKind.ModelEditorThinkingEffort),
        _ => mode ?? "",
    };

    /// <summary>打开后焦点放在别名上。</summary>
    internal void FocusFirst() => AliasInput.Focus();

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
