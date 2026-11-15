using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc.Motion;

namespace Ncw.UI.Controls;

/// <summary>
/// 一段圆弧:从十二点钟方向顺时针画出 <see cref="Progress"/>(0–1)的比例,底下可以带一圈轨道。启动加载界面的标识外圈用它(docs/design/03 §10.4):
/// 弧长是<b>真实阶段进度</b>(已完成的阶段数 + 接管里真实的步骤),不是计时器;进度变化时弧长走 Arc 的 <c>Smooth</c> 弹簧(无回弹,被打断时从当前位置接着走),
/// 减弱 / 关闭级别下 Arc 让弹簧直接跳到终点——最终状态完全一致。颜色全部由使用处用 Arc token 设置(这里没有任何颜色值)。
/// 也用来画「正在进行」的短弧(固定进度 + <c>NcwMotion.Busy=Spin</c> 旋转):不确定的阶段转一小段弧,离开界面 / 隐藏自动停。
/// </summary>
public sealed class NcwProgressArc : Control
{
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<NcwProgressArc, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<NcwProgressArc, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> TrackStrokeProperty = AvaloniaProperty.Register<NcwProgressArc, IBrush?>(nameof(TrackStroke));

    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<NcwProgressArc, double>(nameof(Thickness), 3);

    private MotionValue<double>? _motion;
    private double _display;
    private bool _finishing;

    static NcwProgressArc()
    {
        AffectsRender<NcwProgressArc>(StrokeProperty, TrackStrokeProperty, ThicknessProperty);
        ProgressProperty.Changed.AddClassHandler<NcwProgressArc>(static (c, e) => c.OnProgressChanged(Math.Clamp(e.GetNewValue<double>(), 0, 1)));
    }

    /// <summary>目标进度 0–1(来自 ViewModel)。</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>弧的颜色(Arc token)。</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>底下一整圈轨道的颜色;为 null 不画轨道。</summary>
    public IBrush? TrackStroke
    {
        get => GetValue(TrackStrokeProperty);
        set => SetValue(TrackStrokeProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>此刻画出来的进度(动画中间是介于旧值和目标之间的数;测试断言中间帧用它)。</summary>
    public double DisplayProgress => _display;

    /// <summary>弧长动画正在进行(测试与诊断用)。</summary>
    public bool IsMoving => _motion is { IsAnimating: true };

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // 离开界面树:落到目标并释放运动值(不留订阅、不再请求帧)
        Release();
        SetDisplay(Math.Clamp(Progress, 0, 1));
    }

    private void OnProgressChanged(double target)
    {
        var level = NcwMotion.EffectiveLevel(this);
        if (!this.IsAttachedToVisualTree() || level == TKCode.Arc.Motion.ArcMotionLevel.Off)
        {
            _motion?.Jump(target);
            SetDisplay(target);
            return;
        }

        if (_motion is null)
        {
            _motion = new MotionValue<double>(this, _display, MotionVectors.Double);
            _motion.Changed += OnStep;
        }

        _motion.AnimateTo(target, NcwMotion.ExpandSpec);
    }

    private void OnStep(double value)
    {
        if (_finishing)
        {
            return;
        }

        var target = Math.Clamp(Progress, 0, 1);
        if (Math.Abs(value - target) < 0.0005)
        {
            // 到站:精确落在目标,停掉弹簧的尾巴
            _finishing = true;
            try
            {
                _motion?.Jump(target);
            }
            finally
            {
                _finishing = false;
            }

            SetDisplay(target);
            return;
        }

        SetDisplay(Math.Clamp(value, 0, 1));
    }

    private void SetDisplay(double value)
    {
        if (Math.Abs(value - _display) < double.Epsilon)
        {
            return;
        }

        _display = value;
        InvalidateVisual();
    }

    private void Release()
    {
        if (_motion is { } motion)
        {
            _motion = null;
            motion.Changed -= OnStep;
            motion.Dispose();
        }
    }

    public override void Render(DrawingContext context)
    {
        var thickness = Math.Max(0.5, Thickness);
        var diameter = Math.Min(Bounds.Width, Bounds.Height) - thickness;
        if (diameter <= 0)
        {
            return;
        }

        var radius = diameter / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (TrackStroke is { } track)
        {
            context.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);
        }

        if (Stroke is not { } stroke || _display < 0.0005)
        {
            return;
        }

        var pen = new Pen(stroke, thickness, lineCap: PenLineCap.Round);
        if (_display >= 0.9995)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var sweep = _display * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);   // 十二点钟方向
        var end = new Point(center.X + radius * Math.Sin(sweep), center.Y - radius * Math.Cos(sweep));
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(start, false);
            g.ArcTo(end, new Size(radius, radius), 0, sweep > Math.PI, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }
}
