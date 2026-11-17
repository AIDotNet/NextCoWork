using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.Motion;

namespace Ncw.UI.Controls;

// 位置过渡(docs/design/03 §9.4「位置过渡」):布局已经一次到位,元素只用 RenderTransform 从旧位置滑到新位置(FLIP),不逐帧改布局。
public static partial class NcwMotion
{
    private static readonly AttachedProperty<GlideState?> GlideStateProperty =
        AvaloniaProperty.RegisterAttached<Control, Control, GlideState?>("GlideState");

    /// <summary>
    /// 元素刚被布局挪到了新位置:让它看起来从旧位置(相对新位置纵向偏 <paramref name="fromOffsetY"/> px)滑过去。
    /// 用 <see cref="ExpandSpring"/>(和展开 / 收起同一个弹簧,被打断时连续);只动 <c>RenderTransform</c>,不触发布局。
    /// 只在完整级别播放:减弱 / 关闭级别位置立即到终态(Arc 的约定:减弱不做位移)。元素已有自己的 <c>RenderTransform</c> 时不动它。
    /// 再次调用会先停掉上一次(调用方量旧位置时已经包含了进行中的位移,所以从量到的位置接着走)。
    /// </summary>
    public static void Glide(Control element, double fromOffsetY)
    {
        StopGlide(element);
        if (Math.Abs(fromOffsetY) < 0.5 || EffectiveLevel(element) != ArcMotionLevel.Full || element.RenderTransform is not null)
        {
            return;
        }

        element.SetValue(GlideStateProperty, new GlideState(element, fromOffsetY));
    }

    /// <summary>停掉进行中的位置过渡,元素立即回到布局位置。</summary>
    public static void StopGlide(Control element)
    {
        if (element.GetValue(GlideStateProperty) is { } state)
        {
            element.ClearValue(GlideStateProperty);
            state.Dispose();
        }
    }

    /// <summary>进行中的位置过渡(测试与诊断用)。</summary>
    public static bool IsGliding(Control element) => element.GetValue(GlideStateProperty) is not null;

    private sealed class GlideState : IDisposable
    {
        private const double Epsilon = 0.25;

        private readonly Control _owner;
        private readonly TranslateTransform _translate = new();
        private MotionValue<double>? _y;

        public GlideState(Control owner, double from)
        {
            _owner = owner;
            _translate.Y = from;
            owner.SetCurrentValue(Visual.RenderTransformProperty, _translate);
            owner.DetachedFromVisualTree += OnDetached;
            _y = new MotionValue<double>(owner, from, MotionVectors.Double);
            _y.Changed += OnStep;
            _y.AnimateTo(0, ExpandSpec);
        }

        private void OnStep(double value)
        {
            _translate.Y = value;
            if (Math.Abs(value) < Epsilon)
            {
                StopGlide(_owner);
            }
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => StopGlide(_owner);

        public void Dispose()
        {
            _owner.DetachedFromVisualTree -= OnDetached;
            if (_y is { } y)
            {
                _y = null;
                y.Changed -= OnStep;
                y.Dispose();
            }

            if (ReferenceEquals(_owner.RenderTransform, _translate))
            {
                _owner.SetCurrentValue(Visual.RenderTransformProperty, null);
            }
        }
    }
}
