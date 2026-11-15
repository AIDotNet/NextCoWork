using Avalonia;
using Avalonia.Controls;
using TKCode.Arc.Motion;

namespace Ncw.UI.Controls;

/// <summary>
/// 统一的展开 / 收起容器(docs/design/03 §9.3):工具卡片详情、思考块、待办、子代理转录、审批计划预览、设置里的分区等都用它,不再各写一套。
/// 子内容放在 <see cref="Decorator.Child"/>;<see cref="IsOpen"/> 绑定到 ViewModel 里的展开状态(纯 UI 状态)。
/// <list type="bullet">
/// <item><b>怎么动</b>:高度因子 0→1 走 Arc 的 <c>Smooth</c> 弹簧(<see cref="NcwMotion.ExpandSpec"/>),透明度走进入 / 退出的淡入淡出。控件自己的测量高度 = 内容高度 × 因子,内容按完整高度排布并被裁剪,
/// 所以内容里的文字不会随动画反复换行。高度动画触发布局,代价限定在这一个控件和它的祖先链(见 §9.2 的性能规则)。</item>
/// <item><b>被打断</b>:快速连点时弹簧从当前位置和速度接着走(Arc <see cref="MotionValue{T}"/> 的语义),不回到起点,也不会停在半截;动画途中内容变高 / 变矮,内容自己重新测量,因子照旧。</item>
/// <item><b>降级</b>:关闭级别、尚未挂到界面上、数据上下文刚换(虚拟化列表回收容器)时直接跳到终态,没有任何动画;减弱级别:展开时位置和尺寸跳到终态、只做短淡入,收起时立即消失。
/// 最终状态与完整动画完全一致。</item>
/// <item><b>清理</b>:离开界面树时停掉动画并落到终态,不留定时器或订阅。收起到底后控件 <c>IsVisible=false</c>,内容不参与布局、焦点与命中测试。</item>
/// </list>
/// </summary>
public sealed class NcwCollapsible : Decorator
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<NcwCollapsible, bool>(nameof(IsOpen));

    private const double Epsilon = 0.002; // 下限;实际阈值按内容高度取「半个像素」

    private MotionValue<double>? _size;
    private MotionValue<double>? _fade;
    private double _progress;
    private bool _attached;
    private bool _settling;
    private bool _finishing;

    static NcwCollapsible()
    {
        IsOpenProperty.Changed.AddClassHandler<NcwCollapsible>(static (c, e) => c.OnIsOpenChanged(e.GetNewValue<bool>()));
        AffectsMeasure<NcwCollapsible>(ChildProperty);
    }

    public NcwCollapsible()
    {
        // 初始状态直接就位(没有动画):XAML 里 IsOpen 的初值在构造 / 绑定阶段到来。
        Apply(0, 0);
        SetCurrentValue(IsVisibleProperty, false);
    }

    /// <summary>展开状态。</summary>
    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>高度因子:0 = 完全收起,1 = 完全展开(动画中间是小数)。</summary>
    public double Progress => _progress;

    /// <summary>动画正在进行(测试与诊断用)。</summary>
    public bool IsMoving => _size is { IsAnimating: true } || _fade is { IsAnimating: true };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        JumpToState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        DisposeValues();
        JumpToState();
    }

    // 虚拟化列表回收容器时数据上下文会整个换掉,IsOpen 随之从上一行的值变成这一行的值:那不是用户的操作,不能动画。
    protected override void OnDataContextBeginUpdate()
    {
        base.OnDataContextBeginUpdate();
        _settling = true;
    }

    protected override void OnDataContextEndUpdate()
    {
        base.OnDataContextEndUpdate();
        _settling = false;
        // 这一步之后 IsOpen 可能已经变了(跳过了动画),把因子对齐到终态
        if (!IsMoving)
        {
            JumpToState();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child || _progress <= 0)
        {
            return default;
        }

        child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return new Size(child.DesiredSize.Width, child.DesiredSize.Height * _progress);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is { } child)
        {
            // 内容按完整高度排布(不随动画换行),多出来的部分被裁掉
            child.Arrange(new Rect(0, 0, finalSize.Width, Math.Max(child.DesiredSize.Height, finalSize.Height)));
        }

        return finalSize;
    }

    private void OnIsOpenChanged(bool open)
    {
        var level = NcwMotion.EffectiveLevel(this);
        if (!_attached || _settling || level == ArcMotionLevel.Off)
        {
            JumpToState();
            return;
        }

        if (!open && level == ArcMotionLevel.Reduced)
        {
            // 减弱:收起没有时长(Arc 的约定),立即消失
            JumpToState();
            return;
        }

        EnsureValues();
        if (open)
        {
            SetCurrentValue(IsVisibleProperty, true);
        }

        // 展开 / 收起:高度走弹簧(减弱级别下 Arc 会让它直接跳到终点),透明度走进入 / 退出的淡入淡出
        _size!.AnimateTo(open ? 1 : 0, NcwMotion.ExpandSpec);
        _fade!.AnimateTo(open ? 1 : 0, open ? NcwMotion.EnterSpec(NcwMotionTier.Fast) : NcwMotion.ExitSpec);
    }

    private void EnsureValues()
    {
        if (_size is not null)
        {
            return;
        }

        _size = new MotionValue<double>(this, _progress, MotionVectors.Double);
        _fade = new MotionValue<double>(this, Opacity, MotionVectors.Double);
        _size.Changed += OnSizeStep;
        _fade.Changed += OnFadeStep;
    }

    private void DisposeValues()
    {
        if (_size is not null)
        {
            _size.Changed -= OnSizeStep;
            _size.Dispose();
            _size = null;
        }

        if (_fade is not null)
        {
            _fade.Changed -= OnFadeStep;
            _fade.Dispose();
            _fade = null;
        }
    }

    private void OnSizeStep(double value)
    {
        if (_finishing)
        {
            return;
        }

        var open = IsOpen;
        // 差不到半个像素就算到站(弹簧的尾巴很长,但肉眼早就看不出来了)
        var epsilon = Math.Max(Epsilon, 0.5 / Math.Max(1, Child?.DesiredSize.Height ?? 1));
        if (open ? value >= 1 - epsilon : value <= epsilon)
        {
            // 到站:精确落在终态,停掉弹簧的尾巴(淡入淡出是独立的一个值,各自到站)
            _finishing = true;
            try
            {
                _size?.Jump(open ? 1 : 0);
            }
            finally
            {
                _finishing = false;
            }

            Apply(open ? 1 : 0, Opacity);
            return;
        }

        Apply(value, Opacity);
    }

    private void OnFadeStep(double value)
    {
        if (_finishing)
        {
            return;
        }

        var open = IsOpen;
        if (open ? value >= 1 - Epsilon : value <= Epsilon)
        {
            _finishing = true;
            try
            {
                _fade?.Jump(open ? 1 : 0);
            }
            finally
            {
                _finishing = false;
            }

            Apply(_progress, open ? 1 : 0);
            return;
        }

        Apply(_progress, value);
    }

    /// <summary>不经动画,直接落到 <see cref="IsOpen"/> 对应的终态。</summary>
    private void JumpToState()
    {
        var open = IsOpen;
        _size?.Jump(open ? 1 : 0);
        _fade?.Jump(open ? 1 : 0);
        Apply(open ? 1 : 0, open ? 1 : 0);
    }

    private void Apply(double progress, double opacity)
    {
        var changed = Math.Abs(progress - _progress) > double.Epsilon;
        _progress = Math.Clamp(progress, 0, 1);
        var fade = Math.Clamp(opacity, 0, 1);
        if (Math.Abs(Opacity - fade) > double.Epsilon)
        {
            Opacity = fade;
        }

        // 只在动画中间才裁剪(展开到底后让阴影、弹出层等溢出的内容照常显示)
        var clip = _progress is > 0 and < 1;
        if (ClipToBounds != clip)
        {
            ClipToBounds = clip;
        }

        var visible = IsOpen || _progress > 0;
        if (IsVisible != visible)
        {
            SetCurrentValue(IsVisibleProperty, visible);
        }

        if (changed)
        {
            InvalidateMeasure();
        }
    }
}
