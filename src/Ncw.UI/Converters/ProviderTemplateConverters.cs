using System.Globalization;
using Avalonia.Data.Converters;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;
using Ncw.UI.Localization;

namespace Ncw.UI.Converters;

/// <summary>模版端点标签(<see cref="ProviderTemplateVariants"/>)→ 文字;认不出的原样显示。</summary>
public sealed class TemplateVariantTextConverter : IValueConverter
{
    public static readonly TemplateVariantTextConverter Instance = new();

    public static string Text(string? variant) => variant switch
    {
        null => string.Empty,
        ProviderTemplateVariants.International => NcwTexts.Get(NcwTextKind.ProviderTemplateVariantIntl),
        ProviderTemplateVariants.Anthropic => NcwTexts.Get(NcwTextKind.ProviderTemplateVariantAnthropic),
        _ => variant,
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>模版网格的分组 key → 标题。</summary>
public sealed class TemplateGroupTextConverter : IValueConverter
{
    public static readonly TemplateGroupTextConverter Instance = new();

    public static string Text(string? group) => NcwTexts.Get(group switch
    {
        ProviderTemplateItems.FeaturedGroup => NcwTextKind.ProviderTemplateGroupFeatured,
        ProviderTemplateGroups.International => NcwTextKind.ProviderTemplateGroupIntl,
        ProviderTemplateGroups.China => NcwTextKind.ProviderTemplateGroupChina,
        ProviderTemplateGroups.Aggregator => NcwTextKind.ProviderTemplateGroupAggregator,
        ProviderTemplateGroups.Local => NcwTextKind.ProviderTemplateGroupLocal,
        ProviderTemplateItems.ResultsGroup => NcwTextKind.ProviderTemplateGroupResults,
        _ => NcwTextKind.ProviderTemplateGroupOther,
    });

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Text(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// 模版卡片的两行字:<c>ConverterParameter=Title</c> 是品牌名(自定义是本地化的「自定义」),
/// <c>Subtitle</c> 是端点标签 · 主机名(自定义是一句说明)。
/// </summary>
public sealed class TemplateItemTextConverter : IValueConverter
{
    public static readonly TemplateItemTextConverter Instance = new();

    public static string Title(ProviderTemplateItem item) => item.IsCustom ? NcwTexts.Get(NcwTextKind.ProviderTemplateCustom) : item.Name ?? item.Id;

    public static string Subtitle(ProviderTemplateItem item)
    {
        if (item.IsCustom)
        {
            return NcwTexts.Get(NcwTextKind.ProviderTemplateCustomHint);
        }

        var variant = TemplateVariantTextConverter.Text(item.Variant);
        return variant.Length == 0 ? item.Host ?? string.Empty : $"{variant} · {item.Host}";
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is ProviderTemplateItem item
        ? parameter as string == "Subtitle" ? Subtitle(item) : Title(item)
        : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
