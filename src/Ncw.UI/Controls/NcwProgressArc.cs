using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.Motion;

namespace Ncw.UI.Controls;

/// <summary>
/// 一段圆弧与可选的 4D 超正方体晶格核心(Hyper-Tesseract):从十二点钟方向顺时针画出 <see cref="Progress"/>(0–1)的比例,底下可以带一圈轨道。
/// 启动加载界面的标识外圈与超维晶格核心用它(docs/design/03 §10.4):
/// 弧长与晶格顶点点亮比例严格来自<b>真实阶段进度</b>(已完成的阶段数 + 接管里真实的步骤),不是计时器;进度变化时弧长走 Arc 的 <c>Smooth</c> 弹簧(无回弹,被打断时从当前位置接着走),
/// 减弱 / 关闭级别下 Arc 让弹簧直接跳到终点——最终状态完全一致。颜色全部由使用处用 Arc token 设置(这里没有任何颜色值)。
/// 也用来画「正在进行」的短弧(固定进度 + <c>NcwMotion.Busy=Spin</c> 旋转)或驱动 4D 晶格投影旋转(经 Arc <see cref="LiveLoop"/>:完整级别下双平面 4D 旋转,减弱级别下明暗呼吸,离开界面 / 隐藏自动停)。
/// </summary>
public sealed class NcwProgressArc : Control
{
    public static readonly StyledProperty<double> ProgressProperty = AvaloniaProperty.Register<NcwProgressArc, double>(nameof(Progress));

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<NcwProgressArc, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> TrackStrokeProperty = AvaloniaProperty.Register<NcwProgressArc, IBrush?>(nameof(TrackStroke));

    public static readonly StyledProperty<IBrush?> LatticeStrokeProperty = AvaloniaProperty.Register<NcwProgressArc, IBrush?>(nameof(LatticeStroke));

    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<NcwProgressArc, double>(nameof(Thickness), 3);

    public static readonly StyledProperty<bool> ShowTesseractProperty = AvaloniaProperty.Register<NcwProgressArc, bool>(nameof(ShowTesseract));

    public static readonly StyledProperty<bool> ShowArcProperty = AvaloniaProperty.Register<NcwProgressArc, bool>(nameof(ShowArc), true);

    public static readonly StyledProperty<bool> IsSpinningProperty = AvaloniaProperty.Register<NcwProgressArc, bool>(nameof(IsSpinning));

    private const double SpinCycleSeconds = 6.0;
    private const double DefaultAngleA = 0.6;
    private const double DefaultAngleB = 0.4;

    private MotionValue<double>? _motion;
    private MotionValue<double>? _sweepMotion;
    private LiveLoop? _latticeLoop;
    private double _display;
    private double _sweep = 1;
    private double _phase;
    private double _pulse = 1;
    private bool _finishing;

    static NcwProgressArc()
    {
        AffectsRender<NcwProgressArc>(StrokeProperty, TrackStrokeProperty, LatticeStrokeProperty, ThicknessProperty, ShowTesseractProperty, ShowArcProperty);
        ProgressProperty.Changed.AddClassHandler<NcwProgressArc>(static (c, e) => c.OnProgressChanged(Math.Clamp(e.GetNewValue<double>(), 0, 1)));
        ShowTesseractProperty.Changed.AddClassHandler<NcwProgressArc>(static (c, _) => c.SyncLatticeLoop());
        IsSpinningProperty.Changed.AddClassHandler<NcwProgressArc>(static (c, _) => c.SyncLatticeLoop());
    }

    /// <summary>目标进度 0–1(来自 ViewModel)。</summary>
    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>弧与主晶格边的颜色(Arc token)。</summary>
    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>底下一整圈轨道与未点亮晶格顶点的颜色;为 null 不画轨道。</summary>
    public IBrush? TrackStroke
    {
        get => GetValue(TrackStrokeProperty);
        set => SetValue(TrackStrokeProperty, value);
    }

    /// <summary>4D 超正方体第四维跨层连接边的颜色(Arc token);未设置时回退到 <see cref="Stroke"/>。</summary>
    public IBrush? LatticeStroke
    {
        get => GetValue(LatticeStrokeProperty);
        set => SetValue(LatticeStrokeProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    /// <summary>是否在圆环内部绘制 4D 超正方体晶格投影(Hyper-Tesseract)。</summary>
    public bool ShowTesseract
    {
        get => GetValue(ShowTesseractProperty);
        set => SetValue(ShowTesseractProperty, value);
    }

    /// <summary>
    /// 是否画进度弧本身(默认画)。只影响绘制:<see cref="DisplayProgress"/> 与晶格节点的点亮比例照常随真实进度走。
    /// 启动界面在还在等的期间关掉它——一段不动的弧挨着一段在转的彗星,会被误读成「卡住了」。
    /// </summary>
    public bool ShowArc
    {
        get => GetValue(ShowArcProperty);
        set => SetValue(ShowArcProperty, value);
    }

    /// <summary>还在等待期间(<c>Phase == Loading</c>)驱动 4D 晶格双平面旋转(完整级别)或明暗呼吸(减弱级别)。</summary>
    public bool IsSpinning
    {
        get => GetValue(IsSpinningProperty);
        set => SetValue(IsSpinningProperty, value);
    }

    /// <summary>此刻画出来的进度(动画中间是介于旧值和目标之间的数;测试断言中间帧用它)。</summary>
    public double DisplayProgress => _display;

    /// <summary>弧长动画正在进行(测试与诊断用)。</summary>
    public bool IsMoving => _motion is { IsAnimating: true };

    /// <summary>
    /// 入场时轨道与 4D 晶格「展开 / 画出来」的比例 0–1(平时是 1)。轨道从十二点钟方向顺时针被画出来,晶格从中心展开;进度弧不会跑在轨道前面(取两者较小的),所以入场时是轨道和已有的进度一起被画出来。
    /// 测试断言入场的中间帧用它。
    /// </summary>
    public double TrackSweep => _sweep;

    /// <summary>入场的扫画还在进行(测试与诊断用)。</summary>
    public bool IsSweeping => _sweepMotion is { IsAnimating: true };

    /// <summary>4D 晶格此刻的旋转相位(秒,诊断与测试用)。</summary>
    public double TesseractPhase => _phase;

    /// <summary>
    /// 播放入场:轨道从十二点钟方向顺时针被画出来,4D 晶格同步展开(Arc 的 <c>Considered</c> 时长 + 进入缓动,<paramref name="delay"/> 用于和其他部分编排)。
    /// 只有完整级别才画;减弱 / 关闭级别直接就位(减弱级别由外层的短淡入负责出现)。已经在进行的会从头来。
    /// </summary>
    public void PlayEntrance(TimeSpan delay = default)
    {
        if (!this.IsAttachedToVisualTree() || NcwMotion.EffectiveLevel(this) != ArcMotionLevel.Full)
        {
            ReleaseSweep();
            SetSweep(1);
            return;
        }

        if (_sweepMotion is null)
        {
            _sweepMotion = new MotionValue<double>(this, 1, MotionVectors.Double);
            _sweepMotion.Changed += OnSweepStep;
        }

        _finishing = true;
        try
        {
            _sweepMotion.Jump(0);
        }
        finally
        {
            _finishing = false;
        }

        SetSweep(0);
        _sweepMotion.AnimateTo(1, NcwMotion.EnterSpec(NcwMotionTier.Slow, delay));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncLatticeLoop();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // 离开界面树:落到目标并释放运动值与 LiveLoop(不留订阅、不再请求帧)
        Release();
        ReleaseSweep();
        ReleaseLatticeLoop();
        SetSweep(1);
        SetDisplay(Math.Clamp(Progress, 0, 1));
    }

    private void SyncLatticeLoop()
    {
        if (!this.IsAttachedToVisualTree() || !ShowTesseract || !IsSpinning)
        {
            ReleaseLatticeLoop();
            return;
        }

        if (_latticeLoop is not null)
        {
            return;
        }

        _latticeLoop = new LiveLoop(this, StepLattice) { MinimumLevel = ArcMotionLevel.Reduced };
        _latticeLoop.RunningChanged += OnLatticeRunningChanged;
        _latticeLoop.Start();
    }

    private bool StepLattice(double seconds)
    {
        if (NcwMotion.EffectiveLevel(this) == ArcMotionLevel.Full)
        {
            _phase = (_phase + seconds) % SpinCycleSeconds;
            _pulse = 1;
        }
        else
        {
            _phase = ArcBusyPulse.Advance(_phase, seconds);
            _pulse = ArcBusyPulse.Opacity(_phase);
        }

        InvalidateVisual();
        return true;
    }

    private void OnLatticeRunningChanged(bool running)
    {
        if (!running && (_phase != 0 || Math.Abs(_pulse - 1) > double.Epsilon))
        {
            _phase = 0;
            _pulse = 1;
            InvalidateVisual();
        }
    }

    private void ReleaseLatticeLoop()
    {
        if (_latticeLoop is { } loop)
        {
            _latticeLoop = null;
            loop.RunningChanged -= OnLatticeRunningChanged;
            loop.Dispose();
        }

        if (_phase != 0 || Math.Abs(_pulse - 1) > double.Epsilon)
        {
            _phase = 0;
            _pulse = 1;
            InvalidateVisual();
        }
    }

    private void OnSweepStep(double value)
    {
        if (_finishing)
        {
            return;
        }

        if (value >= 0.9995)
        {
            _finishing = true;
            try
            {
                _sweepMotion?.Jump(1);
            }
            finally
            {
                _finishing = false;
            }

            SetSweep(1);
            return;
        }

        SetSweep(Math.Clamp(value, 0, 1));
    }

    private void SetSweep(double value)
    {
        if (Math.Abs(value - _sweep) < double.Epsilon)
        {
            return;
        }

        _sweep = value;
        InvalidateVisual();
    }

    private void ReleaseSweep()
    {
        if (_sweepMotion is { } motion)
        {
            _sweepMotion = null;
            motion.Changed -= OnSweepStep;
            motion.Dispose();
        }
    }

    private void OnProgressChanged(double target)
    {
        var level = NcwMotion.EffectiveLevel(this);
        if (!this.IsAttachedToVisualTree() || level == ArcMotionLevel.Off)
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
            DrawArc(context, new Pen(track, thickness, lineCap: PenLineCap.Round), center, radius, _sweep);
        }

        if (ShowTesseract && _sweep > 0.01 && Stroke is { } mainBrush)
        {
            DrawTesseract(context, center, radius, mainBrush, LatticeStroke ?? mainBrush, TrackStroke ?? mainBrush);
        }

        var shown = Math.Min(_display, _sweep);   // 入场时进度弧不会跑在轨道前面
        if (!ShowArc || Stroke is not { } stroke || shown < 0.0005)
        {
            return;
        }

        DrawArc(context, new Pen(stroke, thickness, lineCap: PenLineCap.Round), center, radius, shown);
    }

    private void DrawTesseract(DrawingContext context, Point center, double outerRadius, IBrush primaryBrush, IBrush secondaryBrush, IBrush dimBrush)
    {
        var full = NcwMotion.EffectiveLevel(this) == ArcMotionLevel.Full && IsSpinning;
        var angleA = DefaultAngleA + (full ? _phase * (2 * Math.PI / SpinCycleSeconds) : 0);
        var angleB = DefaultAngleB + (full ? _phase * (2 * Math.PI / SpinCycleSeconds) * 0.75 : 0);
        var cosA = Math.Cos(angleA);
        var sinA = Math.Sin(angleA);
        var cosB = Math.Cos(angleB);
        var sinB = Math.Sin(angleB);
        var cosTilt = Math.Cos(0.45);
        var sinTilt = Math.Sin(0.45);
        var scale = outerRadius * 0.34 * _sweep;

        Span<Point> pts = stackalloc Point[16];
        for (var i = 0; i < 16; i++)
        {
            var x = (i & 1) != 0 ? 1.0 : -1.0;
            var y = (i & 2) != 0 ? 1.0 : -1.0;
            var z = (i & 4) != 0 ? 1.0 : -1.0;
            var w4 = (i & 8) != 0 ? 1.0 : -1.0;

            // XW 平面 4D 旋转
            var x1 = (x * cosA) - (w4 * sinA);
            var w1 = (x * sinA) + (w4 * cosA);

            // ZW 平面 4D 旋转
            var z2 = (z * cosB) - (w1 * sinB);
            var w2 = (z * sinB) + (w1 * cosB);

            // 4D → 3D 透视投影
            var d4 = 2.6 / (2.6 - (w2 * 0.65));
            var x3 = x1 * d4;
            var y3 = y * d4;
            var z3 = z2 * d4;

            // 3D 旋转
            var rx = (x3 * cosB) - (z3 * sinB);
            var rz = (x3 * sinB) + (z3 * cosB);
            var ry = (y3 * cosTilt) - (rz * sinTilt);

            pts[i] = new Point(center.X + (rx * scale), center.Y + (ry * scale));
        }

        var innerPen = new Pen(primaryBrush, 1.35, lineCap: PenLineCap.Round);
        var bridgePen = new Pen(secondaryBrush, 1.15, lineCap: PenLineCap.Round);

        // 3D 子立方体边(bit 0..2)与 4D 跨维连边(bit 3)
        using (context.PushOpacity(0.72 * _pulse))
        {
            for (var i = 0; i < 16; i++)
            {
                for (var bit = 0; bit < 3; bit++)
                {
                    var j = i ^ (1 << bit);
                    if (i < j)
                    {
                        context.DrawLine(innerPen, pts[i], pts[j]);
                    }
                }
            }
        }

        using (context.PushOpacity(0.42 * _pulse))
        {
            for (var i = 0; i < 8; i++)
            {
                context.DrawLine(bridgePen, pts[i], pts[i ^ 8]);
            }
        }

        // 16 个晶格节点:按真实进度(DisplayProgress)依次点亮
        var shown = Math.Min(_display, _sweep);
        using (context.PushOpacity(0.92 * _pulse))
        {
            for (var i = 0; i < 16; i++)
            {
                var lit = (i / 16.0) <= shown;
                var r = lit ? 2.4 : 1.5;
                context.DrawEllipse(lit ? primaryBrush : dimBrush, null, pts[i], r, r);
            }
        }
    }

    private static void DrawArc(DrawingContext context, Pen pen, Point center, double radius, double fraction)
    {
        if (fraction < 0.0005)
        {
            return;
        }

        if (fraction >= 0.9995)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var sweep = fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);   // 十二点钟方向
        var end = new Point(center.X + (radius * Math.Sin(sweep)), center.Y - (radius * Math.Cos(sweep)));
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
