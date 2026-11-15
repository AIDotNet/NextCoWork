using Avalonia;
using Avalonia.Controls;

namespace Ncw.UI.Views.Desktop.Management;

/// <summary>管理页页头(标题 + 说明 + 作用域切换器)。三个属性只是文字和是否显示项目切换器。</summary>
public partial class PageHeader : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<PageHeader, string?>(nameof(Title));
    public static readonly StyledProperty<string?> HintProperty = AvaloniaProperty.Register<PageHeader, string?>(nameof(Hint));
    public static readonly StyledProperty<string?> ScopeTextProperty = AvaloniaProperty.Register<PageHeader, string?>(nameof(ScopeText));
    public static readonly StyledProperty<bool> ShowProjectProperty = AvaloniaProperty.Register<PageHeader, bool>(nameof(ShowProject));

    public PageHeader() => InitializeComponent();

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string? ScopeText
    {
        get => GetValue(ScopeTextProperty);
        set => SetValue(ScopeTextProperty, value);
    }

    public bool ShowProject
    {
        get => GetValue(ShowProjectProperty);
        set => SetValue(ShowProjectProperty, value);
    }
}
