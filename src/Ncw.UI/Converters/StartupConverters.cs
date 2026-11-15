using System.Globalization;
using Avalonia.Data.Converters;
using Ncw.Core.Domain;
using Ncw.UI.Localization;

namespace Ncw.UI.Converters;

// 启动加载界面(docs/design/03 §10)的文字:Core / Client 只给枚举和稳定 key,这里按界面语言翻译。

/// <summary>阶段(<see cref="StartupStage"/>)→ 清单里的名字。</summary>
public sealed class StartupStageTextConverter : IValueConverter
{
    public static readonly StartupStageTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StartupStage.App => NcwTexts.Get(NcwTextKind.StartupStageApp),
        StartupStage.Agent => NcwTexts.Get(NcwTextKind.StartupStageAgent),
        StartupStage.Connect => NcwTexts.Get(NcwTextKind.StartupStageConnect),
        StartupStage.Data => NcwTexts.Get(NcwTextKind.StartupStageData),
        StartupStage.Content => NcwTexts.Get(NcwTextKind.StartupStageContent),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>(说明 <see cref="StartupDetail"/>, 接管步骤)→ 当前这一行下面的说明。接管中用和数据页同一套步骤文字。</summary>
public sealed class StartupDetailTextConverter : IMultiValueConverter
{
    public static readonly StartupDetailTextConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 1 || values[0] is not StartupDetail detail)
        {
            return null;
        }

        return detail switch
        {
            StartupDetail.AppStarting => NcwTexts.Get(NcwTextKind.StartupDetailAppStarting),
            StartupDetail.AgentLaunching => NcwTexts.Get(NcwTextKind.StartupDetailAgentLaunching),
            StartupDetail.AgentHandshake => NcwTexts.Get(NcwTextKind.StartupDetailAgentHandshake),
            StartupDetail.AgentRestarting => NcwTexts.Get(NcwTextKind.StartupDetailAgentRestarting),
            StartupDetail.Connecting => NcwTexts.Get(NcwTextKind.StartupDetailConnecting),
            StartupDetail.Reconnecting => NcwTexts.Get(NcwTextKind.StartupDetailReconnecting),
            StartupDetail.DataPreparing => NcwTexts.Get(NcwTextKind.StartupDetailDataPreparing),
            StartupDetail.DataTakeover => DataStepConverter.Instance.Convert(values.Count > 1 ? values[1] : null, typeof(string), null, culture),
            StartupDetail.LoadingProjects => NcwTexts.Get(NcwTextKind.StartupDetailLoadingProjects),
            StartupDetail.LoadingSessions => NcwTexts.Get(NcwTextKind.StartupDetailLoadingSessions),
            StartupDetail.Settling => NcwTexts.Get(NcwTextKind.StartupDetailSettling),
            _ => null,
        };
    }
}

/// <summary>慢启动的提示(平静的文字,不是错误)。</summary>
public sealed class StartupSlowTextConverter : IValueConverter
{
    public static readonly StartupSlowTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StartupSlowHint.Agent => NcwTexts.Get(NcwTextKind.StartupSlowAgent),
        StartupSlowHint.Data => NcwTexts.Get(NcwTextKind.StartupSlowData),
        StartupSlowHint.Content => NcwTexts.Get(NcwTextKind.StartupSlowContent),
        StartupSlowHint.Generic => NcwTexts.Get(NcwTextKind.StartupSlowGeneric),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>失败原因 key → 标题(参数 <c>hint</c> 时给说明)。认不出来的 key 用通用文字,不把英文 key 直接丢给用户。</summary>
public sealed class StartupFailureTextConverter : IValueConverter
{
    public static readonly StartupFailureTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hint = parameter as string == "hint";
        var prefix = hint ? "StartupFailHint" : "StartupFailTitle";
        if (value is string reason && reason.Length > 0
            && Enum.TryParse<NcwTextKind>(prefix + KeyText.Pascal(reason), out var known) && Enum.IsDefined(known))
        {
            return NcwTexts.Get(known);
        }

        return NcwTexts.Get(hint ? NcwTextKind.StartupFailHintUnknown : NcwTextKind.StartupFailTitleUnknown);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
