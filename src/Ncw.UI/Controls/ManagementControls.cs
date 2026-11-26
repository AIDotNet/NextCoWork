using Avalonia;
using Ncw.Client.ViewModels;
using Ncw.UI.Localization;
using TKCode.Arc.Localization;

namespace Ncw.UI.Controls;

/// <summary>Arc 折线图(用量日序列)。</summary>
public class NcwLineChart : TKCode.Arc.LineChart
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.LineChart);
}

/// <summary>Arc 柱状图(用量日序列)。</summary>
public class NcwBarChart : TKCode.Arc.BarChart
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.BarChart);
}

/// <summary>Arc 分段控件(范围 / 标签切换)。</summary>
public class NcwSegmented : TKCode.Arc.SegmentedControl
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.SegmentedControl);
}

/// <summary>分段控件的一段。</summary>
public class NcwSegmentedOption : TKCode.Arc.SegmentedOption
{
}

/// <summary>Arc 进度条(存储占用)。</summary>
public class NcwProgress : TKCode.Arc.Progress
{
    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Progress);
}

/// <summary>下拉框里 <see cref="ChoiceItem"/> 的文字与值(<c>Text</c> 为 null = 「跟随默认」)。</summary>
public static class ChoiceTexts
{
    public static string? Text(object? item, string? nullText = null) => item switch
    {
        ChoiceItem c => c.Text ?? nullText ?? NcwTexts.Get(NcwTextKind.ChoiceFollowDefault),
        null => null,
        _ => item.ToString(),
    };

    public static object? Value(object? item) => item is ChoiceItem c ? c.Value : item;
}

/// <summary>
/// 内置的几组固定选项(权限档位、思考档位、代理方式……):值是设备线上的字符串,文字按当前语言(<see cref="KeyText"/> 的前缀约定)。
/// XAML 里写 <c>&lt;ui:NcwSelect ChoiceSet="Thinking" .../&gt;</c>。
/// </summary>
public static class ChoiceSets
{
    private static IReadOnlyList<ChoiceItem> Make(string prefix, params string[] values) =>
        [.. values.Select(v => new ChoiceItem(v, KeyText.Get(prefix, v)))];

    public static IReadOnlyList<ChoiceItem> Get(string? name) => name switch
    {
        "PermissionMode" => Make("Permission", "ask", "auto", "full"),
        "Thinking" => Make("Thinking", "auto", "off", "minimal", "low", "medium", "high", "xhigh", "max"),
        "ThinkingInherit" => Make("Thinking", "inherit", "auto", "off", "minimal", "low", "medium", "high", "xhigh", "max"),
        "ThinkingFollow" => [new ChoiceItem("", null), .. Make("Thinking", "inherit", "auto", "off", "minimal", "low", "medium", "high", "xhigh", "max")],
        "ModelProposedGoals" => Make("GoalMode", "auto", "alwaysAsk", "disabled"),
        "Shell" => Make("Shell", "system", "cmd", "powershell", "pwsh", "zsh", "bash", "fish", "sh"),
        "ProxyMode" => Make("ProxyMode", "system", "manual"),
        "ProxyScheme" => Make("ProxyScheme", "http", "https", "socks5", "socks4"),
        "BackupFrequency" => Make("BackupFreq", "manual", "daily", "weekly"),
        "CleanupAge" => Make("StorageAge", "7d", "15d", "1", "3", "6", "12"),
        "HookEvent" => Make("HookEvent", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SubagentStop"),
        "HookType" => Make("HookType", "command", "prompt"),
        "HookScope" => Make("HookScope", "global", "project"),
        "McpTransport" => Make("McpTransport", "stdio", "streamable-http", "sse"),
        "ScheduleKind" => Make("SchedKind", "once", "daily", "weekly"),
        "InstallScope" => Make("InstallScope", "global", "project"),
        "ProjectMode" => Make("ProjectMode", "code", "plan"),
        "SshTargetKind" => Make("SshTargetKind", "manual", "config"),
        "SshAuthMethod" => Make("SshAuthMethod", "auto", "password", "key", "interactive", "ask"),
        "SshPlatform" => Make("SshPlatform", "auto", "linux", "macos", "windows"),
        "SshProxyKind" => Make("SshProxyKind", "socks5", "socks4", "http"),
        _ => [],
    };
}

/// <summary>Arc 下拉选择。设备切换器等 <see cref="ChoiceItem"/> 列表不用额外配置;固定选项组用 <see cref="ChoiceSet"/>。</summary>
public class NcwSelect : TKCode.Arc.Select
{
    public static readonly StyledProperty<string?> ChoiceSetProperty = AvaloniaProperty.Register<NcwSelect, string?>(nameof(ChoiceSet));

    public static readonly StyledProperty<string?> NullTextProperty = AvaloniaProperty.Register<NcwSelect, string?>(nameof(NullText));

    public NcwSelect()
    {
        TextSelector = item => ChoiceTexts.Text(item, NullText);
        ValueSelector = ChoiceTexts.Value;
    }

    protected override Type StyleKeyOverride => typeof(TKCode.Arc.Select);

    /// <summary>「没有文字」的那一项(<see cref="ChoiceItem.Text"/> 为 null)显示什么;缺省是「跟随默认」。</summary>
    public string? NullText
    {
        get => GetValue(NullTextProperty);
        set => SetValue(NullTextProperty, value);
    }

    /// <summary>内置选项组的名字(见 <see cref="ChoiceSets"/>)。</summary>
    public string? ChoiceSet
    {
        get => GetValue(ChoiceSetProperty);
        set => SetValue(ChoiceSetProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ChoiceSetProperty)
        {
            ItemsSource = ChoiceSets.Get(change.NewValue as string);
        }
    }
}

/// <summary>状态小圆点:<see cref="Tone"/> 是 <c>ok</c> / <c>warn</c> / <c>bad</c> / <c>mute</c>,颜色由样式按 Arc token 给。</summary>
public class NcwStatusDot : Avalonia.Controls.Border
{
    public static readonly StyledProperty<string?> ToneProperty = AvaloniaProperty.Register<NcwStatusDot, string?>(nameof(Tone), "mute");

    public NcwStatusDot()
    {
        Classes.Add("dot");
        Classes.Add("mute");
    }

    protected override Type StyleKeyOverride => typeof(Avalonia.Controls.Border); // 样式选择器 Border.dot 按样式键匹配

    public string? Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty)
        {
            foreach (var tone in new[] { "ok", "warn", "bad", "mute" })
            {
                Classes.Set(tone, tone == (change.NewValue as string));
            }
        }
    }
}
