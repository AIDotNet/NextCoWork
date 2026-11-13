using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Fonts;
using Ncw.UI.Localization;

namespace Ncw.UI;

/// <summary>
/// Avalonia + Arc 的装配,所有宿主(桌面、测试、以后的移动端)都从这里开始。
/// Arc 在 AppBuilder 上注册,App 里不放 Fluent / Simple 主题(AGENTS.md §2)。
/// </summary>
public static class NcwAppSetup
{
    public static AppBuilder UseNcw(this AppBuilder builder) =>
        builder.WithInterFont()   // 正文 Inter(Arc 默认)
               .WithArcFonts()  // 标题 Geist、代码 Geist Mono
               .UseArc(arc => arc
                   .UseLanguages(NcwLanguages.Initial, NcwLanguages.Supported)
                   .UseArcControls()
                   // 本程序集的 Arc 资源(界面文字的语言包)
                   .UsePackage("Ncw.UI", static p => TKCode.Arc.Generated.NcwUI.ArcGeneratedAssets.AddTo(p))
                   // 默认深色(docs/design/03 §4);设置页可改为跟随系统和其他强调色(M4)。
                   .WithTheme(t => t.Appearance(ArcAppearance.Dark).Accent(ArcAccentPreset.Blue)));
}
