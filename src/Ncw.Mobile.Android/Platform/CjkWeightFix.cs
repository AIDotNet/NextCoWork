using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;

namespace Ncw.Mobile.Droid.Platform;

/// <summary>
/// Android 上 Avalonia(Skia)字体的一个缺陷的绕行(模拟器 API 35 实测,<c>LayoutDiagnostics.FontTest</c>):
/// 请求字重<b>恰好是 400</b>(<see cref="FontWeight.Normal"/>)时,系统 CJK 字体(<c>/system/fonts/NotoSansCJK-Regular.ttc</c>)匹配到的字形表是空的,
/// 中文显示成方框;100 / 300 / 399 / 401 / 500 以上都正常,拉丁字母不受影响。字体管理器接口(<c>IFontManagerImpl</c>)不允许外部实现,不能在那一层改。
/// 绕行:顶层样式把继承属性 <see cref="TextElement.FontWeightProperty"/> 的默认值设成 401(肉眼看不出和 400 的区别)——没有显式字重的文字都走它。
/// 在控件模板里显式写了 <c>Normal</c> 的文字不受影响(目前观察到的只有部分输入框,见 docs/design/11 §6)。Avalonia / SkiaSharp 修复后删除。
/// </summary>
internal static class CjkWeightFix
{
    public const int Weight = 401;

    public static AppBuilder Install(AppBuilder builder) => builder.AfterSetup(_ =>
    {
        if (Avalonia.Application.Current is { } app)
        {
            app.Styles.Add(new Style(x => x.Is<TopLevel>())
            {
                Setters = { new Setter(TextElement.FontWeightProperty, (FontWeight)Weight) },
            });
        }
    });
}
