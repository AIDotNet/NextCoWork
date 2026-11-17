using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Ncw.UI.Controls;

/// <summary>
/// Arc 代码块(只读,带语法着色、行号和复制按钮)。聊天里的代码块用默认形态(卡片 + 文件名头);
/// 文件标签页要的是「编辑器」形态,Arc 没有现成选项,这里在套用 Arc 模板之后调两处:
/// <list type="bullet">
/// <item><see cref="IsChromeless"/>:去掉头(文件名、语言在标签页自己的头和状态栏里已经有)和卡片的边框、圆角、底色,代码铺满。</item>
/// <item><see cref="StickyScrollBar"/>:Arc 的代码块高度等于全文高度、长行在块内横向滚动,横向滚动条就在整段代码的最下面——
/// 放进整页滚动的文件标签页里,长行被截断却看不到滚动条。开启后把这条横向滚动条吸附在外层滚动区的可视底边,随纵向滚动移动;
/// 代码块的高度和排布不动(Arc 的文字层依赖全高排布,限高会让文字消失)。</item>
/// </list>
/// 只依赖 Arc 1.1.0 模板里的部件名(PART_Frame / PART_Header / 内层滚动区里的 PART_HorizontalScrollBar),找不到就什么都不改,退回 Arc 的默认形态。
/// </summary>
public class NcwCodeBlock : TKCode.Arc.CodeBlock
{
    public static readonly StyledProperty<bool> IsChromelessProperty =
        AvaloniaProperty.Register<NcwCodeBlock, bool>(nameof(IsChromeless));

    public static readonly StyledProperty<bool> StickyScrollBarProperty =
        AvaloniaProperty.Register<NcwCodeBlock, bool>(nameof(StickyScrollBar));

    private Border? _frame;
    private Control? _header;
    private Control? _bar;
    private ScrollViewer? _outer;

    protected override Type StyleKeyOverride => typeof(TKCode.Arc.CodeBlock);

    /// <summary>不要头和卡片外框。</summary>
    public bool IsChromeless
    {
        get => GetValue(IsChromelessProperty);
        set => SetValue(IsChromelessProperty, value);
    }

    /// <summary>横向滚动条吸附在外层滚动区的可视底边(外层没有滚动区时不动)。</summary>
    public bool StickyScrollBar
    {
        get => GetValue(StickyScrollBarProperty);
        set => SetValue(StickyScrollBarProperty, value);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _frame = e.NameScope.Find<Border>("PART_Frame");
        _header = e.NameScope.Find<Control>("PART_Header");
        _bar = null; // 横向滚动条在内层滚动区自己的模板里,取用时再按名字找
        ApplyChrome();
        Reposition();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(this.FindAncestorOfType<ScrollViewer>());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Watch(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsChromelessProperty)
        {
            ApplyChrome();
        }
        else if (change.Property == StickyScrollBarProperty)
        {
            Reposition();
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        Reposition();
        return size;
    }

    private void Watch(ScrollViewer? outer)
    {
        if (ReferenceEquals(_outer, outer))
        {
            return;
        }

        if (_outer is not null)
        {
            _outer.ScrollChanged -= OnOuterScrollChanged;
            _outer.SizeChanged -= OnOuterSizeChanged;
        }

        _outer = outer;
        if (_outer is not null)
        {
            _outer.ScrollChanged += OnOuterScrollChanged;
            _outer.SizeChanged += OnOuterSizeChanged;
        }

        Reposition();
    }

    private void OnOuterScrollChanged(object? sender, ScrollChangedEventArgs e) => Reposition();

    private void OnOuterSizeChanged(object? sender, SizeChangedEventArgs e) => Reposition();

    private void ApplyChrome()
    {
        if (_frame is not null)
        {
            if (IsChromeless)
            {
                _frame.BorderThickness = default;
                _frame.CornerRadius = default;
                _frame.Background = Brushes.Transparent;
            }
            else
            {
                _frame.ClearValue(Border.BorderThicknessProperty);
                _frame.ClearValue(Border.CornerRadiusProperty);
                _frame.ClearValue(Border.BackgroundProperty);
            }
        }

        if (_header is not null)
        {
            _header.IsVisible = !IsChromeless;
        }
    }

    /// <summary>
    /// 条的原位置在代码块底部。外层可视区底边在代码块内的纵坐标 = 外层可视高度 − 代码块底边在外层里的纵坐标;
    /// 代码块底边在可视区下面时,把条往上提这么多(不超过代码块顶边)。
    /// </summary>
    private void Reposition()
    {
        _bar ??= this.GetVisualDescendants().OfType<ScrollBar>().FirstOrDefault(b => b.Name == "PART_HorizontalScrollBar");
        if (_bar is null)
        {
            return;
        }

        var lift = 0.0;
        if (StickyScrollBar && _outer is { } outer && this.TranslatePoint(new Point(0, Bounds.Height), outer) is { } bottom)
        {
            var overflow = bottom.Y - outer.Viewport.Height;
            lift = Math.Clamp(overflow, 0, Math.Max(0, Bounds.Height - _bar.Bounds.Height));
        }

        _bar.RenderTransform = lift > 0.5 ? new TranslateTransform(0, -lift) : null;
    }
}
