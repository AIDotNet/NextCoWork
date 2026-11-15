using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc.Motion;

namespace Ncw.UI.Controls;

/// <summary>
/// 「长按确认」按钮(审批卡片的「长按允许」,docs/design/02 B2)。它只负责<b>输入和画面</b>:把按下 / 移动 / 松手 / 打断转成事件,
/// 按住期间每帧发一个 <see cref="Tick"/>,并把 <see cref="Progress"/>(0–1)画成从左到右的填充条;
/// 「按多久算数、移出多远算取消、什么时候触发动作」全在 Client 的 <c>HoldGesture</c>(纯逻辑,时间可注入)。
/// <b>填充的缓动</b>:按住时的填充是<b>线性</b>的、直接等于手势的进度——它就是手势的时钟,加缓动会让条满的时刻和动作触发的时刻对不上,所以不走统一的缓动;
/// 只有<b>回退</b>(松手早了 / 移出 / 被取消,<see cref="Progress"/> 变小)走统一的退出缓动,条平滑退回去而不是瞬间归零(<see cref="DisplayProgress"/>;减弱 / 关闭级别直接归零)。
/// 键盘也能操作:聚焦后按住空格或回车,松开即松手,失焦即打断。外观在 <c>Themes/NcwMobileTheme.axaml</c>(颜色全部是 Arc token)。
/// </summary>
public class NcwHoldButton : ContentControl
{
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<NcwHoldButton, double>(nameof(Progress));

    /// <summary>模板实际画的进度:按住时等于 <see cref="Progress"/>,回退时平滑退回。</summary>
    public static readonly StyledProperty<double> DisplayProgressProperty = AvaloniaProperty.Register<NcwHoldButton, double>(nameof(DisplayProgress));

    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<NcwHoldButton, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsHoldingProperty = AvaloniaProperty.Register<NcwHoldButton, bool>(nameof(IsHolding));

    public static readonly StyledProperty<bool> IsDangerousProperty = AvaloniaProperty.Register<NcwHoldButton, bool>(nameof(IsDangerous));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _pressed;
    private MotionValue<double>? _rewind;

    public NcwHoldButton()
    {
        Focusable = true;
        _timer.Tick += (_, _) => Tick?.Invoke(this, EventArgs.Empty);
        LostFocus += (_, _) => Interrupt();
    }

    /// <summary>
    /// 按钮文字(一个字符串,不是任意内容):填充条上叠一份反色的文字、被填充条裁剪,所以文字在填充边缘两侧都有足够的对比度(同 Arc 的 HoldToConfirm)。
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>填充进度 0–1(来自 ViewModel,不在这里计算)。</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>画面上的填充进度(见类说明)。</summary>
    public double DisplayProgress
    {
        get => GetValue(DisplayProgressProperty);
        private set => SetValue(DisplayProgressProperty, value);
    }

    /// <summary>手势正在进行(来自 ViewModel)。它变回假(按满、被取消)时本控件停掉帧定时器。</summary>
    public bool IsHolding
    {
        get => GetValue(IsHoldingProperty);
        set => SetValue(IsHoldingProperty, value);
    }

    /// <summary>危险操作:填充和边框用危险色。</summary>
    public bool IsDangerous
    {
        get => GetValue(IsDangerousProperty);
        set => SetValue(IsDangerousProperty, value);
    }

    public event EventHandler? HoldStarted;

    /// <summary>手指移动:参数是离按钮边缘的距离(像素)。</summary>
    public event EventHandler<double>? HoldMoved;

    public event EventHandler? HoldEnded;

    /// <summary>失焦、指针被抢走等外部打断。</summary>
    public event EventHandler? HoldInterrupted;

    /// <summary>按住期间每帧一次。</summary>
    public event EventHandler? Tick;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProgressProperty)
        {
            OnProgress(change.GetOldValue<double>(), change.GetNewValue<double>());
        }
        else if (change.Property == IsHoldingProperty)
        {
            PseudoClasses.Set(":holding", change.GetNewValue<bool>());
            if (!change.GetNewValue<bool>())
            {
                _timer.Stop();
                _pressed = false;
            }
        }
        else if (change.Property == IsDangerousProperty)
        {
            PseudoClasses.Set(":dangerous", change.GetNewValue<bool>());
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Pointer.Capture(this);
        Begin();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed)
        {
            return;
        }

        var p = e.GetPosition(this);
        var dx = Math.Max(0, Math.Max(-p.X, p.X - Bounds.Width));
        var dy = Math.Max(0, Math.Max(-p.Y, p.Y - Bounds.Height));
        // 距离是「超出按钮边缘」多远(和 Arc 的 HoldToConfirm 一致):手指在滚动列表时很快就会离开按钮
        var travel = Math.Sqrt(dx * dx + dy * dy);
        HoldMoved?.Invoke(this, travel);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed)
        {
            e.Pointer.Capture(null);
            End();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Interrupt();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Space or Key.Enter && !_pressed && IsEnabled)
        {
            Begin();
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.Key is Key.Space or Key.Enter && _pressed)
        {
            End();
            e.Handled = true;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Interrupt();
        if (_rewind is not null)
        {
            _rewind.Dispose();
            _rewind = null;
            DisplayProgress = Progress;
        }
    }

    private void OnProgress(double from, double to)
    {
        if (to >= from || !this.IsAttachedToVisualTree() || NcwMotion.EffectiveLevel(this) != ArcMotionLevel.Full)
        {
            // 前进(按住中,线性,直接跟手势)/ 没有动画条件(关闭、减弱):画面等于进度
            _rewind?.Stop();
            DisplayProgress = to;
            return;
        }

        // 回退:从画面当前位置退回去(被打断、又按下时从当前位置接着走)
        if (_rewind is null)
        {
            _rewind = new MotionValue<double>(this, DisplayProgress, MotionVectors.Double);
            _rewind.Changed += v => DisplayProgress = v;
        }

        _ = _rewind.AnimateTo(to, NcwMotion.ExitSpec);
    }

    private void Begin()
    {
        _pressed = true;
        _timer.Start();
        HoldStarted?.Invoke(this, EventArgs.Empty);
    }

    private void End()
    {
        _pressed = false;
        _timer.Stop();
        HoldEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Interrupt()
    {
        if (!_pressed)
        {
            return;
        }

        _pressed = false;
        _timer.Stop();
        HoldInterrupted?.Invoke(this, EventArgs.Empty);
    }
}
