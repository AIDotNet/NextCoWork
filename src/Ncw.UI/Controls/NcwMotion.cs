using Avalonia.Animation.Easings;
using Avalonia.Threading;
using Ncw.Client.Stores;
using TKCode.Arc;
using TKCode.Arc.Motion;

namespace Ncw.UI.Controls;

/// <summary>动效时长档位(docs/design/03 §9.2)。数值全部取自 Arc 的时长 token,这里只给语义名。</summary>
public enum NcwMotionTier
{
    /// <summary>悬停 / 按下的颜色变化(Arc <c>DurationInstant</c>,120 ms)。</summary>
    Instant,

    /// <summary>小淡入淡出、减弱动效下替代位移的短淡入(Arc <c>DurationFast</c>,160 ms)。</summary>
    Fast,

    /// <summary>大多数出现 / 状态过渡(Arc <c>DurationStandard</c>,240 ms)。</summary>
    Base,

    /// <summary>较大的、有意为之的变化(Arc <c>DurationConsidered</c>,480 ms)。</summary>
    Slow,
}

/// <summary>
/// 统一动效系统的入口(docs/design/03 §9)。<b>不另造一套 token</b>:时长、缓动、弹簧、减少动效的三档级别全部是 Arc 的(<c>ArcMotionPresets</c> / <c>ArcEasings</c> / <c>ArcSpring</c> / <c>ArcMotionLevel</c>),
/// 这里只做三件事:① 给 Arc 的数值起业务语义名,不让魔法数字散落;② 把「本应用」的动效偏好(<see cref="UiPrefs.Motion"/>)解析并应用到 Arc 的主题级别;③ 提供几个封装了「中断、降级、清理」的行为
/// (<see cref="NcwCollapsible"/>、<see cref="NcwMotion.IsPresentProperty"/>、<see cref="NcwMotion.ArriveOnAttachProperty"/>、<see cref="NcwMotion.BusyProperty"/>)。
/// </summary>
public static partial class NcwMotion
{
    /// <summary>档位对应的时长(来自 Arc 的 <c>Duration…</c> token)。</summary>
    public static TimeSpan Duration(NcwMotionTier tier) => tier switch
    {
        NcwMotionTier.Instant => ArcMotionPresets.Instant,
        NcwMotionTier.Fast => ArcMotionPresets.Fast,
        NcwMotionTier.Base => ArcMotionPresets.Standard,
        _ => ArcMotionPresets.Considered,
    };

    /// <summary>离开的时长(Arc <c>DurationExit</c>,180 ms):退出比进入快。</summary>
    public static TimeSpan ExitDuration => ArcMotionPresets.Exit;

    /// <summary>进入用减速缓动(Arc <c>EaseEnter</c>)。</summary>
    public static Easing EnterEasing => ArcEasings.Enter;

    /// <summary>退出用加速缓动(Arc <c>EaseExit</c>)。</summary>
    public static Easing ExitEasing => ArcEasings.Exit;

    /// <summary>
    /// 出现时的上移幅度(px)。Arc 的 Accordion 展开时答案「落进 6 px」的位置,同一个数,保证两边手感一致;
    /// 位移只用于 <see cref="NcwArrival.Rise"/>,而且只动 RenderTransform(合成层,不触发布局)。
    /// </summary>
    public const double RiseOffset = 6;

    /// <summary>展开 / 收起的高度弹簧(Arc <c>Smooth</c>:0.4 s、无回弹,Arc 把它用于「状态报告类」的面板与进度)。被打断时从当前位置和速度接着走。</summary>
    public const ArcSpring ExpandSpring = ArcSpring.Smooth;

    /// <summary>
    /// 展开 / 收起一个面板的运动规格。弹簧的好处是被打断(快速连点)时连续,不会从头来过;
    /// 减弱 / 关闭级别下 Arc 的 <see cref="MotionValue{T}"/> 会让弹簧直接跳到终点。
    /// </summary>
    public static MotionSpec ExpandSpec => MotionSpec.Spring(ExpandSpring, TimeSpan.Zero);

    /// <summary>进入的淡入(档位时长 + 进入缓动)。</summary>
    public static MotionSpec EnterSpec(NcwMotionTier tier = NcwMotionTier.Base) => MotionSpec.Tween(Duration(tier), EnterEasing, TimeSpan.Zero);

    /// <summary>退出的淡出(退出时长 + 退出缓动)。</summary>
    public static MotionSpec ExitSpec => MotionSpec.Tween(ExitDuration, ExitEasing, TimeSpan.Zero);

    /// <summary>某个控件此刻实际生效的级别(用户偏好 × Arc 主题级别 × 容器上继承的 <c>ArcMotion.Level</c> 取低者)。</summary>
    public static ArcMotionLevel EffectiveLevel(Avalonia.Visual visual) => ArcMotion.GetEffectiveLevel(visual);

    // ───────────────────────── 本应用设置 → Arc ─────────────────────────

    /// <summary>
    /// 操作系统是否要求「减少动态效果」。Avalonia 12.1.5 的 <c>IPlatformSettings</c> 没有这个信息(只有主题与对比度),Arc 也不读,
    /// 所以由宿主在启动前注入各平台的探测(桌面:macOS / Windows;手机宿主:iOS / Android 按各自 API);返回 <c>null</c> = 读不到,按「完整」处理。
    /// </summary>
    public static Func<bool?> SystemReducesMotionProbe { get; set; } = static () => null;

    /// <summary>最近一次应用到 Arc 的级别(诊断与测试用)。</summary>
    public static UiMotionLevel Current { get; private set; } = UiMotionLevel.Full;

    /// <summary>
    /// 把偏好解析成级别并写进 Arc 主题(<c>MotionMode</c> token):此后 Arc 自带控件(树、菜单、弹窗、导航栈、标签栏、按钮……)和本文件里的行为一起降级。
    /// 这是整个应用唯一一处写 Arc 动效级别的地方。
    /// </summary>
    public static void Apply(UiMotionPreference preference)
    {
        var level = UiMotion.Resolve(preference, SystemReducesMotionProbe());
        Apply(level);
    }

    /// <summary>直接应用一个已经解析好的级别(测试默认 <see cref="UiMotionLevel.Off"/>,让截图稳定)。</summary>
    public static void Apply(UiMotionLevel level)
    {
        Current = level;
        ArcTheme.Apply(t => t.MotionLevel(ToArc(level)));
    }

    /// <summary>Client 的级别映射到 Arc 的(Client 不引用 Arc)。</summary>
    public static ArcMotionLevel ToArc(UiMotionLevel level) => level switch
    {
        UiMotionLevel.Reduced => ArcMotionLevel.Reduced,
        UiMotionLevel.Off => ArcMotionLevel.Off,
        _ => ArcMotionLevel.Full,
    };

    /// <summary>
    /// 启动时调用一次:立即应用当前偏好,之后偏好(「外观」页)变化时重新应用。返回的对象在退出时释放。
    /// 偏好 Store 可能在任意线程更新,应用 Arc 主题一定回到 UI 线程。
    /// </summary>
    public static IDisposable Bind(ClientStores stores)
    {
        var last = (UiMotionPreference?)null;
        void Sync()
        {
            var preference = stores.Prefs.State.Motion;
            if (last == preference)
            {
                return;
            }

            last = preference;
            Apply(preference);
        }

        void OnChanged()
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                Sync();
            }
            else
            {
                Dispatcher.UIThread.Post(Sync);
            }
        }

        Sync();
        stores.Prefs.Changed += OnChanged;
        return new Unbinder(() => stores.Prefs.Changed -= OnChanged);
    }

    private sealed class Unbinder(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
