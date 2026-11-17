using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.Motion;

namespace Ncw.UI.Controls;

/// <summary>出现时的动效种类。</summary>
public enum NcwArrival
{
    /// <summary>不做动画。</summary>
    None,

    /// <summary>只淡入。</summary>
    Fade,

    /// <summary>轻微上移 + 淡入(<see cref="NcwMotion.RiseOffset"/>):审批卡片、消息气泡、状态条。减弱级别下退化成只淡入。</summary>
    Rise,

    /// <summary>从左边缘滑入 + 淡入(整幅宽度):左侧区域、抽屉。只动 RenderTransform。减弱级别下退化成只淡入。</summary>
    SlideFromLeft,

    /// <summary>从右边缘滑入 + 淡入:右侧检查器。</summary>
    SlideFromRight,

    /// <summary>
    /// 从略小的尺寸放大到原大小 + 淡入(只动 RenderTransform 的缩放,以中心为原点;起始比例 <see cref="NcwMotion.GrowFrom"/>):启动加载界面的标识、「阶段完成」的勾。
    /// 减弱级别下退化成只淡入。
    /// </summary>
    Grow,
}

/// <summary>「忙」指示的种类(运行中图标 / 圆点)。</summary>
public enum NcwBusyKind
{
    None,

    /// <summary>匀速旋转(加载图标)。</summary>
    Spin,

    /// <summary>呼吸:轻微缩放 + 明暗(流式输出末尾的圆点)。</summary>
    Breathe,
}

// 附加行为:出现 / 消失(IsPresent、ShowMotion、Arrive)与运行中指示(Busy)。都只动 Opacity / RenderTransform(合成层,不触发布局)。
public static partial class NcwMotion
{
    /// <summary>旋转一圈的秒数(加载图标)。</summary>
    public const double SpinPeriodSeconds = 0.9;

    /// <summary>呼吸圆点的最大缩放。</summary>
    public const double BreatheScale = 1.25;

    /// <summary>
    /// <see cref="NcwArrival.Grow"/> 的起始比例。和 <see cref="RiseOffset"/> 一样是本封装自己的取值(Arc 没有公开对应的 token):只用于标识 / 勾这种小而醒目的元素,
    /// 幅度小到不抢眼,够让「长出来」被感知到。
    /// </summary>
    public const double GrowFrom = 0.84;

    // ───────────────────────── 出现 / 消失(带退出) ─────────────────────────

    /// <summary>
    /// 受控的显示 / 隐藏:绑定到一个布尔(替代 <c>IsVisible</c> 绑定)。变为真时设为可见并播放进入;变为假时先播放退出,结束后才 <c>IsVisible=false</c>。
    /// 动画途中反向(又变回来)从当前位置接着走;减弱 / 关闭级别下隐藏没有过渡。控件还没挂到界面上时的初值直接就位。
    /// 用在设置页覆盖层、状态条、整块可开关的卡片上;<b>不要</b>同时再绑定同一控件的 <c>IsVisible</c>。
    /// </summary>
    public static readonly AttachedProperty<bool> IsPresentProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, bool>("IsPresent", defaultValue: true);

    /// <summary><see cref="IsPresentProperty"/> 用哪种进入 / 退出(默认 <see cref="NcwArrival.Rise"/>;遮罩层用 <see cref="NcwArrival.Fade"/>,只淡不动)。</summary>
    public static readonly AttachedProperty<NcwArrival> PresenceKindProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, NcwArrival>("PresenceKind", NcwArrival.Rise);

    public static NcwArrival GetPresenceKind(Control element) => element.GetValue(PresenceKindProperty);

    public static void SetPresenceKind(Control element, NcwArrival value) => element.SetValue(PresenceKindProperty, value);

    public static bool GetIsPresent(Control element) => element.GetValue(IsPresentProperty);

    public static void SetIsPresent(Control element, bool value) => element.SetValue(IsPresentProperty, value);

    /// <summary>
    /// 用在「本来就靠 <c>IsVisible</c> 切换」的面板上(页签内容、检查器各页、设置页的各分页):每次从隐藏变成可见时播放进入。
    /// 只有进入,没有退出(隐藏的那一帧控件已经不画了);首次挂载不播放。
    /// </summary>
    public static readonly AttachedProperty<NcwArrival> ShowMotionProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, NcwArrival>("ShowMotion", NcwArrival.None);

    public static NcwArrival GetShowMotion(Control element) => element.GetValue(ShowMotionProperty);

    public static void SetShowMotion(Control element, NcwArrival value) => element.SetValue(ShowMotionProperty, value);

    /// <summary>
    /// <see cref="ShowMotionProperty"/> 与 <see cref="ArriveOnAttachProperty"/> 的进入时长档位(默认 <see cref="NcwMotionTier.Base"/>)。
    /// 小图标 / 状态点 / 同一位置互换的按钮用 <see cref="NcwMotionTier.Fast"/>:它们是「状态换了」的反馈,不是新内容到达,不该拖。只在完整级别生效(减弱级别固定 Fast)。
    /// </summary>
    public static readonly AttachedProperty<NcwMotionTier> ShowTierProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, NcwMotionTier>("ShowTier", NcwMotionTier.Base);

    public static NcwMotionTier GetShowTier(Control element) => element.GetValue(ShowTierProperty);

    public static void SetShowTier(Control element, NcwMotionTier value) => element.SetValue(ShowTierProperty, value);

    /// <summary>
    /// 控件每次挂到界面树上时播放一次进入。<b>只用在小的、不虚拟化的列表项上</b>(例如「已在另一台设备上处理」提示):
    /// 虚拟化列表(对话转录、文件树)回收 / 重新实例化容器时会重复触发,滚动时整屏闪动——那种列表由视图按「真正新增的行」调 <see cref="Arrive"/>。
    /// </summary>
    public static readonly AttachedProperty<NcwArrival> ArriveOnAttachProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, NcwArrival>("ArriveOnAttach", NcwArrival.None);

    public static NcwArrival GetArriveOnAttach(Control element) => element.GetValue(ArriveOnAttachProperty);

    public static void SetArriveOnAttach(Control element, NcwArrival value) => element.SetValue(ArriveOnAttachProperty, value);

    /// <summary>运行中指示:旋转 / 呼吸。减弱级别:换成 Arc 的「忙」呼吸(只有明暗,<see cref="ArcBusyPulse"/>);关闭级别:静止。离开界面、被隐藏、滚出视口时自动暂停(Arc 的 <see cref="LiveLoop"/>)。</summary>
    public static readonly AttachedProperty<NcwBusyKind> BusyProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, NcwBusyKind>("Busy", NcwBusyKind.None);

    public static NcwBusyKind GetBusy(Control element) => element.GetValue(BusyProperty);

    public static void SetBusy(Control element, NcwBusyKind value) => element.SetValue(BusyProperty, value);

    private static readonly AttachedProperty<PresenceState?> PresenceStateProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, PresenceState?>("PresenceState");

    private static readonly AttachedProperty<BusyDriver?> BusyDriverProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, BusyDriver?>("BusyDriver");

    static NcwMotion()
    {
        IsPresentProperty.Changed.AddClassHandler<Control>(static (c, e) => OnPresentChanged(c, e.GetNewValue<bool>()));
        BusyProperty.Changed.AddClassHandler<Control>(static (c, e) => OnBusyChanged(c, e.GetNewValue<NcwBusyKind>()));
        ArriveOnAttachProperty.Changed.AddClassHandler<Control>(static (c, e) =>
        {
            c.AttachedToVisualTree -= OnArriveAttached;
            if (e.GetNewValue<NcwArrival>() != NcwArrival.None)
            {
                c.AttachedToVisualTree += OnArriveAttached;
            }
        });
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>(static (c, e) =>
        {
            if (e.GetNewValue<bool>() && GetShowMotion(c) is var kind and not NcwArrival.None && c.IsAttachedToVisualTree())
            {
                Arrive(c, kind, tier: GetShowTier(c));
            }
        });
    }

    private static void OnArriveAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control c)
        {
            Arrive(c, GetArriveOnAttach(c), tier: GetShowTier(c));
        }
    }

    /// <summary>
    /// 立即播放一次进入动画(淡入,或淡入 + 上移)。用于「刚到的」元素:对话里新追加的行、新出现的卡片。
    /// 关闭级别下什么都不做;再次调用会打断上一次并从头来;元素的原有透明度 / 变换在动画结束时恢复。
    /// <paramref name="delay"/> 是编排错峰用的延迟(元素在延迟期间保持在起始状态:透明);<paramref name="tier"/> 是进入的时长档位。
    /// 这两个只在完整级别生效:减弱级别永远是没有延迟的短淡入(<see cref="NcwMotionTier.Fast"/>),关闭级别立即就位。
    /// </summary>
    public static void Arrive(Control element, NcwArrival kind = NcwArrival.Rise, bool continueFromCurrent = false, TimeSpan delay = default, NcwMotionTier tier = NcwMotionTier.Base) =>
        Enter(element, kind, continueFromCurrent, delay, tier);

    /// <summary>
    /// 播放退出动画(淡出,带位移的种类同时反向位移),结束时调用 <paramref name="completed"/>——由调用方在那里把元素隐藏 / 恢复布局。
    /// 减弱 / 关闭级别没有退出过渡:立即调用 <paramref name="completed"/>。退出到一半又被 <see cref="Arrive"/> / <c>IsPresent</c> 反向时,
    /// 从当前位置接着进入,<paramref name="completed"/> 不会被调用(元素重新出现了)。
    /// </summary>
    public static void Leave(Control element, NcwArrival kind = NcwArrival.Rise, Action? completed = null)
    {
        if (NcwMotion.EffectiveLevel(element) != ArcMotionLevel.Full || !element.IsEffectivelyVisible)
        {
            Rest(element);
            completed?.Invoke();
            return;
        }

        var state = element.GetValue(PresenceStateProperty);
        if (state is null || !state.Active)
        {
            state = Begin(element, 1, kind);
        }

        state.Kind = kind;
        state.HideAtZero = false;
        state.OnHidden = completed;
        state.Run(0, exit: true);
    }

    private static void Enter(Control element, NcwArrival kind, bool continueFromCurrent, TimeSpan delay = default, NcwMotionTier tier = NcwMotionTier.Base)
    {
        if (kind == NcwArrival.None || NcwMotion.EffectiveLevel(element) == ArcMotionLevel.Off)
        {
            Rest(element);
            return;
        }

        var state = element.GetValue(PresenceStateProperty);
        if (state is null || !state.Active)
        {
            state = Begin(element, 0, kind);
        }
        else if (!continueFromCurrent)
        {
            state.Restart(0);
        }

        state.Kind = kind;
        state.HideAtZero = false;
        state.OnHidden = null;
        state.Delay = delay;
        state.Tier = tier;
        state.Run(1);
    }

    /// <summary>把元素从任何进行中的进入 / 退出动画里拿出来,恢复成静止的终态(容器被回收、元素被复用时调用)。</summary>
    public static void Rest(Control element)
    {
        if (element.GetValue(PresenceStateProperty) is { } state)
        {
            state.Dispose();
            element.ClearValue(PresenceStateProperty);
        }
    }

    /// <summary>进行中的进入 / 退出动画(测试与诊断用)。</summary>
    public static bool IsArriving(Control element) => element.GetValue(PresenceStateProperty) is { Active: true };

    private static void OnPresentChanged(Control control, bool present)
    {
        if (!control.IsAttachedToVisualTree())
        {
            Rest(control);
            control.SetCurrentValue(Visual.IsVisibleProperty, present);
            return;
        }

        var kind = GetPresenceKind(control);
        if (present)
        {
            control.SetCurrentValue(Visual.IsVisibleProperty, true);
            Enter(control, kind, continueFromCurrent: true); // 退出到一半又要出现:从当前位置接着走
            return;
        }

        // 减弱 / 关闭:没有退出过渡(Arc 的约定);本来就看不见的控件没什么可退
        Leave(control, kind, () => control.SetCurrentValue(Visual.IsVisibleProperty, false));
    }

    private static PresenceState Begin(Control element, double start, NcwArrival kind)
    {
        var state = new PresenceState(element) { Kind = kind };
        element.SetValue(PresenceStateProperty, state);
        state.Restart(start);
        return state;
    }

    /// <summary>一个元素的进入 / 退出动画:一个 0..1 的进度值(Arc 的可打断运动值)驱动透明度和上移。进度回到 0 且需要隐藏时设 <c>IsVisible=false</c>;到 1 时恢复原样并释放自己。</summary>
    private sealed class PresenceState : IDisposable
    {
        private const double Epsilon = 0.002;

        private readonly Control _owner;
        private readonly double _restOpacity;
        private readonly ITransform? _restTransform;
        private readonly TranslateTransform _translate = new();
        private readonly ScaleTransform _scale = new();
        private MotionValue<double>? _p;
        private bool _finishing;

        public PresenceState(Control owner)
        {
            _owner = owner;
            _restOpacity = owner.Opacity;
            _restTransform = owner.RenderTransform;
        }

        public bool Active => _p is not null;

        public NcwArrival Kind { get; set; } = NcwArrival.Rise;

        public bool HideAtZero { get; set; }

        /// <summary>进入前的延迟(编排错峰;只在完整级别生效)。</summary>
        public TimeSpan Delay { get; set; }

        /// <summary>进入的时长档位(只在完整级别生效;减弱级别固定用 Fast)。</summary>
        public NcwMotionTier Tier { get; set; } = NcwMotionTier.Base;

        /// <summary>退出到 0 时的回调(调用方在这里隐藏元素)。</summary>
        public Action? OnHidden { get; set; }

        public void Restart(double start)
        {
            if (_p is null)
            {
                _p = new MotionValue<double>(_owner, start, MotionVectors.Double);
                _p.Changed += OnStep;
            }

            _finishing = true;
            _p.Jump(start);
            _finishing = false;
            Apply(start);
        }

        public void Run(double target, bool exit = false)
        {
            if (_p is null)
            {
                return;
            }

            var level = NcwMotion.EffectiveLevel(_owner);
            _p.AnimateTo(target, exit
                ? NcwMotion.ExitSpec
                : NcwMotion.EnterSpec(level == ArcMotionLevel.Full ? Tier : NcwMotionTier.Fast, level == ArcMotionLevel.Full ? Delay : TimeSpan.Zero));
        }

        private void OnStep(double value)
        {
            if (_finishing)
            {
                return;
            }

            var target = _p?.Target ?? value;
            if (target >= 1 && value >= 1 - Epsilon)
            {
                Finish(hide: false);
            }
            else if (target <= 0 && value <= Epsilon)
            {
                var done = OnHidden;
                Finish(hide: HideAtZero);
                done?.Invoke();
            }
            else
            {
                Apply(value);
            }
        }

        private void Finish(bool hide)
        {
            Restore();
            if (hide)
            {
                _owner.SetCurrentValue(Visual.IsVisibleProperty, false);
            }

            Dispose();
            _owner.ClearValue(PresenceStateProperty);
        }

        private void Apply(double p)
        {
            p = Math.Clamp(p, 0, 1);
            var full = NcwMotion.EffectiveLevel(_owner) == ArcMotionLevel.Full;
            // 整幅滑动的面板(左侧区域、抽屉、检查器)完整级别下只滑不淡:半透明的面板压在内容上,文字会叠在一起;减弱级别没有位移,改成短淡入
            var slides = Kind is NcwArrival.SlideFromLeft or NcwArrival.SlideFromRight;
            _owner.SetCurrentValue(Visual.OpacityProperty, slides && full ? _restOpacity : _restOpacity * p);
            if (_restTransform is not null || Kind is NcwArrival.Fade or NcwArrival.None || NcwMotion.EffectiveLevel(_owner) != ArcMotionLevel.Full)
            {
                return;
            }

            // 位移 / 缩放只动 RenderTransform(合成层,不触发布局)
            ITransform transform = _translate;
            switch (Kind)
            {
                case NcwArrival.Grow:
                    var scale = NcwMotion.GrowFrom + (1 - NcwMotion.GrowFrom) * p;
                    _scale.ScaleX = scale;
                    _scale.ScaleY = scale;
                    transform = _scale;
                    break;
                case NcwArrival.Rise:
                    _translate.X = 0;
                    _translate.Y = (1 - p) * NcwMotion.RiseOffset;
                    break;
                case NcwArrival.SlideFromLeft:
                    _translate.X = -(1 - p) * _owner.Bounds.Width;
                    _translate.Y = 0;
                    break;
                case NcwArrival.SlideFromRight:
                    _translate.X = (1 - p) * _owner.Bounds.Width;
                    _translate.Y = 0;
                    break;
            }

            if (!ReferenceEquals(_owner.RenderTransform, transform))
            {
                _owner.SetCurrentValue(Visual.RenderTransformProperty, transform);
            }
        }

        private void Restore()
        {
            _owner.SetCurrentValue(Visual.OpacityProperty, _restOpacity);
            if (ReferenceEquals(_owner.RenderTransform, _translate) || ReferenceEquals(_owner.RenderTransform, _scale))
            {
                _owner.SetCurrentValue(Visual.RenderTransformProperty, _restTransform);
            }
        }

        public void Dispose()
        {
            if (_p is { } p)
            {
                _p = null;
                p.Changed -= OnStep;
                p.Dispose();
                Restore();
            }
        }
    }

    // ───────────────────────── 运行中指示 ─────────────────────────

    private static void OnBusyChanged(Control control, NcwBusyKind kind)
    {
        control.GetValue(BusyDriverProperty)?.Dispose();
        control.ClearValue(BusyDriverProperty);
        if (kind != NcwBusyKind.None)
        {
            control.SetValue(BusyDriverProperty, new BusyDriver(control, kind));
        }
    }

    /// <summary>用 Arc 的 <see cref="LiveLoop"/> 驱动:只在挂在界面上、可见、在视口里、级别不是关闭时才有帧,否则零开销。</summary>
    private sealed class BusyDriver : IDisposable
    {
        private readonly Control _owner;
        private readonly NcwBusyKind _kind;
        private readonly LiveLoop _loop;
        private readonly RotateTransform _rotate = new();
        private readonly ScaleTransform _scale = new();
        private readonly double _restOpacity;
        private readonly ITransform? _restTransform;
        private double _phase;

        public BusyDriver(Control owner, NcwBusyKind kind)
        {
            _owner = owner;
            _kind = kind;
            _restOpacity = owner.Opacity;
            _restTransform = owner.RenderTransform;
            _loop = new LiveLoop(owner, Step) { MinimumLevel = ArcMotionLevel.Reduced };
            _loop.RunningChanged += OnRunningChanged;
            _loop.Start();
        }

        private bool Step(double seconds)
        {
            if (NcwMotion.EffectiveLevel(_owner) == ArcMotionLevel.Full)
            {
                if (_restTransform is not null)
                {
                    return true;
                }

                if (_kind == NcwBusyKind.Spin)
                {
                    _phase = (_phase + seconds) % SpinPeriodSeconds;
                    _rotate.Angle = 360 * _phase / SpinPeriodSeconds;
                    _owner.SetCurrentValue(Visual.RenderTransformProperty, _rotate);
                }
                else
                {
                    _phase = ArcBusyPulse.Advance(_phase, seconds);
                    var breath = (ArcBusyPulse.Opacity(_phase) - ArcBusyPulse.MinOpacity) / (1 - ArcBusyPulse.MinOpacity);
                    var scale = 1 + (BreatheScale - 1) * (1 - breath);
                    _scale.ScaleX = scale;
                    _scale.ScaleY = scale;
                    _owner.SetCurrentValue(Visual.RenderTransformProperty, _scale);
                    _owner.SetCurrentValue(Visual.OpacityProperty, _restOpacity * (0.55 + 0.45 * breath));
                }
            }
            else
            {
                // 减弱:不转不缩,只有 Arc 规定的「忙」呼吸(明暗)
                RestTransform();
                _phase = ArcBusyPulse.Advance(_phase, seconds);
                _owner.SetCurrentValue(Visual.OpacityProperty, _restOpacity * ArcBusyPulse.Opacity(_phase));
            }

            return true;
        }

        private void OnRunningChanged(bool running)
        {
            if (!running)
            {
                // 暂停 / 关闭:画静止的那一帧
                RestTransform();
                _owner.SetCurrentValue(Visual.OpacityProperty, _restOpacity);
            }
        }

        private void RestTransform()
        {
            if (ReferenceEquals(_owner.RenderTransform, _rotate) || ReferenceEquals(_owner.RenderTransform, _scale))
            {
                _owner.SetCurrentValue(Visual.RenderTransformProperty, _restTransform);
            }
        }

        public void Dispose()
        {
            _loop.RunningChanged -= OnRunningChanged;
            _loop.Dispose();
            RestTransform();
            _owner.SetCurrentValue(Visual.OpacityProperty, _restOpacity);
        }
    }
}
