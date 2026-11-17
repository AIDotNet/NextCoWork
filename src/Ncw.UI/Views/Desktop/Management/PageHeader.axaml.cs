using Avalonia;
using Avalonia.Controls;

namespace Ncw.UI.Views.Desktop.Management;

/// <summary>管理页页头(标题 + 说明 + 作用域切换器)。属性只是文字、是否显示项目切换器、是否显示切换器。</summary>
public partial class PageHeader : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<PageHeader, string?>(nameof(Title));
    public static readonly StyledProperty<string?> HintProperty = AvaloniaProperty.Register<PageHeader, string?>(nameof(Hint));
    public static readonly StyledProperty<string?> ScopeTextProperty = AvaloniaProperty.Register<PageHeader, string?>(nameof(ScopeText));
    public static readonly StyledProperty<bool> ShowProjectProperty = AvaloniaProperty.Register<PageHeader, bool>(nameof(ShowProject));

    /// <summary>
    /// 是否在页头里显示设备 / 项目切换器(默认显示)。桌面设置页把所有分区排在一个滚动区里,切换器在「设备」分组标题处只放一份,
    /// 用样式把各页页头里的关掉——同一份作用域被十几个切换器同时双向绑定,会互相改写、反复触发重新加载。
    /// </summary>
    public static readonly StyledProperty<bool> ShowSwitchersProperty = AvaloniaProperty.Register<PageHeader, bool>(nameof(ShowSwitchers), defaultValue: true);

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

    public bool ShowSwitchers
    {
        get => GetValue(ShowSwitchersProperty);
        set => SetValue(ShowSwitchersProperty, value);
    }
}
