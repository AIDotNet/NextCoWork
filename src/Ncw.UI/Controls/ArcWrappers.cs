using Avalonia;
using Avalonia.Controls;

namespace Ncw.UI.Controls;

// Ncw.UI.Controls:对 TKCode.Arc 的薄封装。业务视图只引用这里的类型,Arc 升级时的破坏性变更收敛在这一个文件(决策 D5)。
// 每个封装都把样式键指回 Arc 的原控件,所以外观、token、动效完全沿用 Arc。

/// <summary>Arc 按钮。</summary>
public class NcwButton : TKCode.Arc.Button
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Button);
}

/// <summary>Arc 搜索框。</summary>
public class NcwSearchField : TKCode.Arc.SearchField
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.SearchField);
}

/// <summary>Arc 多行输入框。</summary>
public class NcwTextArea : TKCode.Arc.Textarea
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Textarea);
}

/// <summary>
/// Arc 头像。<see cref="PhotoUrl"/> 是照片地址(业务视图只给字符串,不碰 Arc 的 <c>ImageSource</c>):Arc 负责下载、缓存、淡入,
/// 加载失败或没有地址时显示 <c>DisplayName</c> 的首字母。只认 http / https 的绝对地址,别的当作没有。
/// </summary>
public class NcwAvatar : TKCode.Arc.Avatar
{
    public static readonly StyledProperty<string?> PhotoUrlProperty = AvaloniaProperty.Register<NcwAvatar, string?>(nameof(PhotoUrl));

    static NcwAvatar()
    {
        PhotoUrlProperty.Changed.AddClassHandler<NcwAvatar>((avatar, e) => avatar.Source = SourceOf(e.NewValue as string));
    }

    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Avatar);

    public string? PhotoUrl
    {
        get => GetValue(PhotoUrlProperty);
        set => SetValue(PhotoUrlProperty, value);
    }

    internal static TKCode.Arc.ImageSource? SourceOf(string? url) =>
        url is { Length: > 0 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? TKCode.Arc.ImageSource.FromUri(uri)
            : null;
}

/// <summary>Arc 状态标签。</summary>
public class NcwBadge : TKCode.Arc.Badge
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Badge);
}

/// <summary>Arc 明暗主题切换。</summary>
public class NcwThemeSwitcher : TKCode.Arc.ThemeSwitcher
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.ThemeSwitcher);
}

/// <summary>
/// 无装饰的可点击区域:项目栏图标、会话行、标签页。外观由 <c>Themes/NcwTheme.axaml</c> 里的 ControlTheme 定义,
/// 颜色全部来自 Arc token。
/// </summary>
public class PlainButton : Button
{
    protected override Type StyleKeyOverride => typeof(PlainButton);
}

/// <summary>Arc 窗口(自定义标题栏)。</summary>
public class NcwWindow : TKCode.Arc.ArcWindow
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.ArcWindow);
}

/// <summary>Arc 标题栏。</summary>
public class NcwTitleBar : TKCode.Arc.TitleBar
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.TitleBar);
}

/// <summary>Arc 树(文件管理器)。和 Avalonia 的 TreeView 同名,所以统一经过这个封装。</summary>
public class NcwTreeView : TKCode.Arc.TreeView
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.TreeView);
}

/// <summary>Arc 下拉菜单(右侧面板的「+」)。</summary>
public class NcwDropdownMenu : TKCode.Arc.DropdownMenu
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.DropdownMenu);
}

/// <summary>Arc 单行输入框(带标签、说明和错误行)。</summary>
public class NcwInput : TKCode.Arc.Input
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Input);
}

/// <summary>Arc 开关(设置里立即生效的选项)。</summary>
public class NcwSwitch : TKCode.Arc.Switch
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Switch);
}

/// <summary>Arc 数字输入(带步进按钮)。</summary>
public class NcwNumberField : TKCode.Arc.NumberField
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.NumberField);
}

/// <summary>Arc Markdown(助手回复、计划、子代理汇报)。<c>Text</c> 绑定即可,流式增量到来时整段重排。</summary>
public class NcwMarkdown : TKCode.Arc.Markdown
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Markdown);
}

/// <summary>Arc 复选框(导入模型列表的勾选)。</summary>
public class NcwCheckbox : TKCode.Arc.Checkbox
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Checkbox);
}

// NcwCodeBlock(Arc 代码块,文件预览用)在 NcwCodeBlock.cs:它多了「无外框」「在可视区内滚动」两个选项,不只是薄封装。
