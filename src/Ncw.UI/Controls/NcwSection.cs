using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Ncw.UI.Controls;

/// <summary>
/// 可折叠的分区(项目面板的「会话 / 终端和智能体」、Git 的变更分组、审查的文件列表):标题行(箭头 + 标题 + 可选的右侧操作)+ 用 <see cref="NcwCollapsible"/> 展开 / 收起的内容。
/// 展开状态是这个控件自己的纯 UI 状态(<see cref="IsExpanded"/>,默认展开;不持久化),不进 ViewModel,更不进 Store。
/// 外观在 <c>Themes/NcwTheme.axaml</c>,颜色与动效全部来自 Arc token。
/// </summary>
public class NcwSection : HeaderedContentControl
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<NcwSection, bool>(nameof(IsExpanded), defaultValue: true);

    public static readonly StyledProperty<object?> TrailingProperty =
        AvaloniaProperty.Register<NcwSection, object?>(nameof(Trailing));

    private Button? _toggle;

    /// <summary>是否展开。</summary>
    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>标题行右侧的操作(例如「全部取消暂存」);点它不会折叠分区。</summary>
    public object? Trailing
    {
        get => GetValue(TrailingProperty);
        set => SetValue(TrailingProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_toggle is not null)
        {
            _toggle.Click -= OnToggle;
        }

        _toggle = e.NameScope.Find<Button>("PART_Toggle");
        if (_toggle is not null)
        {
            _toggle.Click += OnToggle;
        }
    }

    private void OnToggle(object? sender, RoutedEventArgs e) => IsExpanded = !IsExpanded;
}
