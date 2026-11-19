using Ncw.Client.Services;
using TKCode.Arc.Localization;

namespace Ncw.UI.Localization;

/// <summary>界面支持的语言(.xlf 文件)和启动语言。</summary>
public static class NcwLanguages
{
    public static IReadOnlyList<(LanguageTag Tag, string Name)> All { get; } =
    [
        (LanguageTags.ZhCN, "简体中文"),
        (LanguageTags.EnUS, "English"),
    ];

    public static IEnumerable<LanguageTag> Supported => All.Select(l => l.Tag);

    /// <summary>启动语言:环境变量 <c>NCW_LANG</c>(如 en-US),否则简体中文。设置页做好后改为读「本应用」设置。</summary>
    public static LanguageTag Initial =>
        Environment.GetEnvironmentVariable("NCW_LANG") is { Length: > 0 } text
        && LanguageTag.TryParse(text, out var tag)
        && Supported.Contains(tag)
            ? tag
            : LanguageTags.ZhCN;
}

/// <summary><see cref="IClientText"/> 的实现:按当前语言取 <see cref="NcwTexts"/>。</summary>
public sealed class NcwClientText : IClientText
{
    public static readonly NcwClientText Instance = new();

    public string NewGroupDefaultName => NcwTexts.Get(NcwTextKind.NewGroupDefaultName);

    public string RenameGroupTitle => NcwTexts.Get(NcwTextKind.RenameGroupTitle);

    public string RenameGroupDescription => NcwTexts.Get(NcwTextKind.RenameGroupDescription);

    public string NameLabel => NcwTexts.Get(NcwTextKind.NameLabel);

    public string LocalDeviceName => NcwTexts.Get(NcwTextKind.LocalDeviceName);

    public string AddProjectPickTitle => NcwTexts.Get(NcwTextKind.AddProjectPickTitle);

    public string AddProjectFailedTitle => NcwTexts.Get(NcwTextKind.AddProjectFailedTitle);

    public string DeviceNotConnected => NcwTexts.Get(NcwTextKind.DeviceNotConnected);

    public string RenameSessionTitle => NcwTexts.Get(NcwTextKind.RenameSessionTitle);

    public string RenameSessionDescription => NcwTexts.Get(NcwTextKind.RenameSessionDescription);

    public string DeleteSessionTitle => NcwTexts.Get(NcwTextKind.DeleteSessionTitle);

    public string DeleteSessionMessage(string sessionTitle) => NcwTexts.Format(NcwTextKind.DeleteSessionMessage, sessionTitle);

    public string DeleteLabel => NcwTexts.Get(NcwTextKind.DeleteLabel);

    public string DeleteProviderTitle => NcwTexts.Get(NcwTextKind.DeleteProviderTitle);

    public string DeleteProviderMessage(string providerName) => NcwTexts.Format(NcwTextKind.DeleteProviderMessage, providerName);

    public string TemplateVariant(string variant) => Converters.TemplateVariantTextConverter.Text(variant);
}
