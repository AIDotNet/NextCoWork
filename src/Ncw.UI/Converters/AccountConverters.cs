using System.Globalization;
using Avalonia.Data.Converters;
using Ncw.UI.Localization;
using TKCode.Arc.Localization;

namespace Ncw.UI.Converters;

/// <summary>「最近活动 {相对时间}」。没有时间返回 null。</summary>
public sealed class ControllerActivityConverter : IValueConverter
{
    public static readonly ControllerActivityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset time && RelativeTimeConverter.Instance.Convert(time, typeof(string), null, culture) is string relative
            ? NcwTexts.Format(NcwTextKind.AccountControllerLastActive, relative)
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>整数 ≥ 参数(首次启动的进度点:当前步数达到第 n 步就点亮)。</summary>
public sealed class AtLeastConverter : IValueConverter
{
    public static readonly AtLeastConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && parameter is string text && int.TryParse(text, CultureInfo.InvariantCulture, out var min) && n >= min;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>首次启动当前步骤的名字(进度点下面的说明)。</summary>
public sealed class OnboardingStepTextConverter : IValueConverter
{
    public static readonly OnboardingStepTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Ncw.Client.ViewModels.OnboardingStep.Welcome => NcwTexts.Get(NcwTextKind.OnboardingStepWelcome),
        Ncw.Client.ViewModels.OnboardingStep.Data => NcwTexts.Get(NcwTextKind.OnboardingStepData),
        Ncw.Client.ViewModels.OnboardingStep.Provider => NcwTexts.Get(NcwTextKind.OnboardingStepModels),
        Ncw.Client.ViewModels.OnboardingStep.Done => NcwTexts.Get(NcwTextKind.OnboardingStepDone),
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>项目栏头像的角标:已登录可用 = 绿点;过期 / 还要选团队 = 提醒色点;没登录不显示。</summary>
public sealed class AccountPresenceConverter : IValueConverter
{
    public static readonly AccountPresenceConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Ncw.Client.ViewModels.AccountPresence.Active => TKCode.Arc.AvatarStatus.Online,
        Ncw.Client.ViewModels.AccountPresence.NeedsAttention => TKCode.Arc.AvatarStatus.Away,
        _ => TKCode.Arc.AvatarStatus.None,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>状态条里登录进行中的文字:换令牌中 / 等浏览器。</summary>
public sealed class AccountBarProgressTextConverter : IValueConverter
{
    public static readonly AccountBarProgressTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        NcwTexts.Get(value is true ? NcwTextKind.AccountBarExchanging : NcwTextKind.AccountBarSigningIn);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>团队里的角色:认得的(owner / member)翻译,别的(后端新增的角色)原样显示,不会被当成「成员」。</summary>
public sealed class TeamRoleTextConverter : IValueConverter
{
    public static readonly TeamRoleTextConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "owner" => NcwTexts.Get(NcwTextKind.RoleOwner),
        "member" => NcwTexts.Get(NcwTextKind.RoleMember),
        string other => other,
        _ => null,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
