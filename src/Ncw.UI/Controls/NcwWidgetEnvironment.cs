using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Ncw.Client.Actions;
using Ncw.Client.Widgets;
using Ncw.UI.Localization;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Ncw.UI.Controls;

/// <summary>
/// 原生视图的「空域」问题:WebView 是盖在窗口上的原生视图,Avalonia 画的对话框 / 设置页盖不住它。凡是要盖在对话区上面的东西(确认框、设置页、首次启动页、窄屏抽屉),
/// 打开时调 <see cref="Suspend"/>,所有 widget 的 WebView 随之隐藏(页面不销毁,状态保留),关掉时恢复。UI 线程专用。
/// </summary>
public static class NcwWidgetAirspace
{
    private static int _count;

    public static bool IsSuspended => _count > 0;

    /// <summary>当前未释放的挂起个数(测试用:布尔值会被别的用例遗留的泄漏污染,断言「我这次操作」引起的增减要看个数)。</summary>
    internal static int ActiveCount => _count;

    public static event Action? Changed;

    public static IDisposable Suspend()
    {
        _count++;
        Changed?.Invoke();
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _count--;
            Changed?.Invoke();
        }
    }
}

/// <summary>
/// widget 的 WebView 摆放时要避开的区域(<see cref="NcwWidgetSurface"/> 的裁剪)。<see cref="BottomInsetProperty"/> 设在一个祖先上(通常是转录的滚动容器):
/// 它的底边往上收这么多,盖在内容底部的 Avalonia 浮层(「回到最新」按钮)就不会被原生视图盖住。改了之后让那个祖先重新排布(<c>InvalidateArrange</c>),
/// 各个 WebView 在下一轮布局之后重新摆放。
/// </summary>
public static class NcwWidgetClip
{
    public static readonly AttachedProperty<double> BottomInsetProperty =
        AvaloniaProperty.RegisterAttached<Visual, double>("BottomInset", typeof(NcwWidgetClip), defaultValue: 0);

    public static double GetBottomInset(Visual visual) => visual.GetValue(BottomInsetProperty);

    public static void SetBottomInset(Visual visual, double value) => visual.SetValue(BottomInsetProperty, value);
}

/// <summary>
/// 把 Arc 的 token 解析成 <see cref="WidgetTokens"/> 要的源值(颜色 → CSS 颜色,字体 → CSS font-family)。<b>这里不写任何色值</b>:每一个值都是从当前主题的 Arc token 读出来的,
/// 所以深浅色切换、换强调色之后重推就跟着变。映射(名字 → token 枚举)写成 switch 而不是反射,裁剪 / AOT 安全。
/// </summary>
public static class NcwWidgetTheme
{
    public static WidgetTheme Current(Control owner)
    {
        var source = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in WidgetTokens.Sources)
        {
            if (KindOf(name) is not { } kind)
            {
                continue;
            }

            if (name.EndsWith("FontFamily", StringComparison.Ordinal))
            {
                if (ArcTokens.Get<FontFamily>(owner, kind) is { } family)
                {
                    source[name] = CssFontFamily(family, name.StartsWith("Mono", StringComparison.Ordinal));
                }
            }
            else
            {
                var c = ArcTokens.GetColor(owner, kind);
                source[name] = WidgetTokens.CssColor(c.A, c.R, c.G, c.B);
            }
        }

        var dark = owner.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? WidgetAppearance.Light : WidgetAppearance.Dark;
        return WidgetTokens.Map(dark, source);
    }

    private static ArcDesignTokenKind? KindOf(string name) => name switch
    {
        "Background" => ArcDesignTokenKind.Background,
        "Surface" => ArcDesignTokenKind.Surface,
        "SurfaceRaised" => ArcDesignTokenKind.SurfaceRaised,
        "SurfaceMuted" => ArcDesignTokenKind.SurfaceMuted,
        "Foreground" => ArcDesignTokenKind.Foreground,
        "TextSecondary" => ArcDesignTokenKind.TextSecondary,
        "TextMuted" => ArcDesignTokenKind.TextMuted,
        "Accent" => ArcDesignTokenKind.Accent,
        "AccentForeground" => ArcDesignTokenKind.AccentForeground,
        "AccentSubtle" => ArcDesignTokenKind.AccentSubtle,
        "Success" => ArcDesignTokenKind.Success,
        "Warning" => ArcDesignTokenKind.Warning,
        "Danger" => ArcDesignTokenKind.Danger,
        "SuccessSubtle" => ArcDesignTokenKind.SuccessSubtle,
        "WarningSubtle" => ArcDesignTokenKind.WarningSubtle,
        "DangerSubtle" => ArcDesignTokenKind.DangerSubtle,
        "BodyFontFamily" => ArcDesignTokenKind.BodyFontFamily,
        "MonoFontFamily" => ArcDesignTokenKind.MonoFontFamily,
        _ => null,
    };

    /// <summary>Avalonia 的字体名(可能是逗号分隔的一串,也可能带 <c>resm:…#名字</c>)→ CSS font-family:取 <c>#</c> 之后的族名逐个加引号,末尾补通用族。</summary>
    internal static string CssFontFamily(FontFamily family, bool mono)
    {
        var names = family.FamilyNames.Select(n => n.Contains('#', StringComparison.Ordinal) ? n[(n.LastIndexOf('#') + 1)..] : n)
            .Where(n => n.Length > 0 && n.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.'))
            .Select(n => $"\"{n}\"");
        return string.Join(", ", names.Concat(mono ? ["ui-monospace", "monospace"] : ["system-ui", "-apple-system", "sans-serif"]));
    }
}

/// <summary>
/// 小组件里「打开外链 / 跑动作」的确认框(Arc Dialog):框里显示<b>真实</b>的地址 / 工具 + 入参 + 执行环境(设备 + 环境,AGENTS.md §5),不是小组件自己写的文案。
/// 打开期间挂起所有 WebView(见 <see cref="NcwWidgetAirspace"/>),否则原生视图会盖住对话框。
/// </summary>
public sealed class NcwWidgetConfirmations(Visual owner, Func<string, string>? deviceName = null) : IWidgetConfirmations
{
    public async Task<bool> ConfirmOpenUrlAsync(string url)
    {
        using var _ = NcwWidgetAirspace.Suspend();
        return await Dialog.ConfirmAsync(owner, NcwTexts.Get(NcwTextKind.WidgetOpenLinkTitle), NcwTexts.Format(NcwTextKind.WidgetOpenLinkBody, url),
            NcwTexts.Get(NcwTextKind.WidgetOpenLinkConfirm), NcwTexts.Get(NcwTextKind.Cancel), false);
    }

    public async Task<bool> ConfirmActionAsync(WidgetActionPrompt prompt)
    {
        using var _ = NcwWidgetAirspace.Suspend();
        var where = prompt.Environment is { } env ? $"{(deviceName?.Invoke(env.DeviceId) ?? env.DeviceId)} · {(env.Name.Length > 0 ? env.Name : env.Environment)}" : "?";
        return await Dialog.ConfirmAsync(owner, NcwTexts.Format(NcwTextKind.WidgetActionTitle, prompt.Label),
            NcwTexts.Format(NcwTextKind.WidgetActionBody, prompt.ToolName, where, Truncate(prompt.InputJson, 1200)),
            NcwTexts.Get(NcwTextKind.WidgetActionConfirm), NcwTexts.Get(NcwTextKind.Cancel), true);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
