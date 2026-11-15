using Ncw.Client.Updates;
using Ncw.UI.Localization;
using TKCode.Arc.Localization;

namespace Ncw.UI.Tests;

/// <summary>
/// 移植自 2.x <c>update-error.test.ts</c>:更新失败码必须在两种语言里都翻译得出来。
/// 防的是一类不会报错、只有用户看得见的 bug —— key 对不上时 <see cref="KeyText"/> 兜底成「把值原样显示」,于是界面上蹦出 <c>SignatureInvalid</c> 这样的英文标识符。
/// </summary>
public class AboutTextTests
{
    public static IEnumerable<object[]> Kinds() => Enum.GetValues<UpdateFailureKind>().Select(k => new object[] { k });

    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [MemberData(nameof(Kinds))]
    public void Every_failure_kind_has_a_real_sentence_in_both_languages(UpdateFailureKind kind)
    {
        foreach (var language in new[] { LanguageTags.ZhCN, LanguageTags.EnUS })
        {
            ArcLanguage.Set(language);
            try
            {
                var text = KeyText.Get("UpdateFail", kind.ToString());
                Assert.False(string.IsNullOrWhiteSpace(text), $"{kind} has no text in {language}");
                Assert.NotEqual(kind.ToString(), text); // 兜底成了枚举名:缺 UpdateFail<Kind> 这一条
                Assert.True(text.Length >= 6, $"{kind} text in {language} is suspiciously short: '{text}'");
            }
            finally
            {
                ArcLanguage.Set(LanguageTags.ZhCN);
            }
        }
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void The_two_languages_do_not_say_the_same_thing_for_a_failure()
    {
        ArcLanguage.Set(LanguageTags.EnUS);
        var en = KeyText.Get("UpdateFail", nameof(UpdateFailureKind.SignatureInvalid));
        ArcLanguage.Set(LanguageTags.ZhCN);
        var zh = KeyText.Get("UpdateFail", nameof(UpdateFailureKind.SignatureInvalid));
        Assert.NotEqual(en, zh);
    }
}
