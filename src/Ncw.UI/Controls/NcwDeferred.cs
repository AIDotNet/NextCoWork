using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Metadata;

namespace Ncw.UI.Controls;

/// <summary>
/// 按需创建的内容(docs/design/03 §12):第一次真正显示时才用 <see cref="Template"/> 建出内容,之前只是一个空壳。
/// <para>
/// 用在「靠 <c>IsVisible</c> 切换、多数时候看不见」的整块内容上(设置的各分页、检查器的各页):Arc 下每个控件都带着一串样式实例和资源表达式,
/// 看不见的页面常驻也照样占内存(看不见只是不测量、不绘制,样式和绑定都还在)。
/// </para>
/// <list type="bullet">
/// <item>第一次被测量时建出内容(看不见的控件不测量,见 <see cref="MeasureOverride"/>);内容建出来以后就是本控件的 <see cref="Decorator.Child"/>,跟着本控件的可见性显示 / 隐藏,DataContext 继承本控件的;</item>
/// <item><see cref="Release"/> 丢掉内容回到空壳(下次变为可见时重建),由宿主决定什么时候释放(例如设置页关掉以后);</item>
/// <item><see cref="Realize"/> 不看可见性直接建出内容(例如设置搜索要收集所有分页里的文字)。</item>
/// </list>
/// 模板写成 <c>&lt;DataTemplate x:DataType="…"&gt;</c>,类型是本控件所在处的 DataContext 类型。
/// </summary>
public sealed class NcwDeferred : Decorator
{
    public static readonly StyledProperty<IDataTemplate?> TemplateProperty =
        AvaloniaProperty.Register<NcwDeferred, IDataTemplate?>(nameof(Template));

    /// <summary>建内容用的模板。</summary>
    [Content]
    public IDataTemplate? Template
    {
        get => GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    /// <summary>内容是否已经建出来。</summary>
    public bool IsRealized => Child is not null;

    /// <summary>需要时建出内容,返回内容(没有模板时为 null)。</summary>
    public Control? Realize()
    {
        if (Child is null && Template is { } template)
        {
            Child = template.Build(DataContext);
        }

        return Child;
    }

    /// <summary>丢掉内容,回到空壳。</summary>
    public void Release() => Child = null;

    /// <summary>
    /// 看不见的控件(自己或任何祖先 <c>IsVisible=false</c>)不会被测量,所以「第一次被测量」就是「真的要显示了」——
    /// 祖先变为可见(检查器栏展开、设置页打开)也会走到这里,不需要另外监听。测量过程中添加子控件是 Avalonia 允许的(虚拟化面板也这样做)。
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        Realize();
        return base.MeasureOverride(availableSize);
    }
}
