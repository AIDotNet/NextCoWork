using Avalonia;
using Avalonia.Collections;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.UI.Localization;
using TKCode.Arc;

namespace Ncw.UI.Controls;

/// <summary>
/// 上下文窗口卡片(Arc 的 <c>ContextWindow</c>,TKCode.Arc.Special):输入框的上下文环点开后显示,
/// 用设备推来的最近一次 <c>context_usage</c>(窗口与各部分的 tokens)把 200 格画满。
/// <para>
/// 只做展示:Arc 的卡片自带「发送 / 附加 / 压缩」的演示逻辑(在客户端里假装折叠消息),这里全部关掉——
/// 不给它可发送的回合和文件(按钮隐藏)、不自动压缩、保留的消息数设成最大(「立即压缩」不可用)。
/// 真正的压缩只由 Agent 按 <see cref="ContextWindows.AutoCompactThreshold"/> 触发,虚线也画在这条线上。
/// </para>
/// </summary>
public class NcwContextCard : ContextWindow
{
    public static readonly StyledProperty<ContextUsageEventDto?> UsageProperty =
        AvaloniaProperty.Register<NcwContextCard, ContextUsageEventDto?>(nameof(Usage));

    public static readonly StyledProperty<string?> ModelNameProperty =
        AvaloniaProperty.Register<NcwContextCard, string?>(nameof(ModelName));

    public NcwContextCard()
    {
        AutoCompact = false;
        KeepRecentMessages = int.MaxValue;
        Labels = LocalizedLabels();
    }

    protected override Type StyleKeyOverride => typeof(ContextWindow);

    /// <summary>最近一次上下文占用;null 时卡片是空的。</summary>
    public ContextUsageEventDto? Usage
    {
        get => GetValue(UsageProperty);
        set => SetValue(UsageProperty, value);
    }

    /// <summary>当前模型的显示名(卡片的模型标签)。</summary>
    public string? ModelName
    {
        get => GetValue(ModelNameProperty);
        set => SetValue(ModelNameProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UsageProperty || change.Property == ModelNameProperty)
        {
            Apply();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 语言可能在卡片不在界面上时换过:每次出现都按当前语言重建文字
        Labels = LocalizedLabels();
    }

    private void Apply()
    {
        if (Usage is not { Window: > 0 } usage)
        {
            Items = [];
            return;
        }

        var reserve = ContextWindows.ResolveMaxOutputTokens(null, usage.Window);
        var model = new ContextWindowModel("current", ModelName ?? "", usage.Window, reserve, null, null);
        Models = [model];
        SelectedModel = model;
        Threshold = Math.Clamp((double)ContextWindows.AutoCompactThreshold(usage.Window, reserve) / usage.Window, 0, 1);
        Items = new AvaloniaList<ContextWindowItem>(usage.Segments.Where(static s => s.Tokens > 0).Select(static s => new ContextWindowItem(CategoryOf(s.Kind), s.Kind, s.Tokens, s.Kind)));
    }

    /// <summary>设备的分段(<c>ContextSegmentKinds</c>)→ 卡片的类别。项目说明与注入的提醒放在「文件与记忆」那一档。</summary>
    internal static ContextWindowCategory CategoryOf(string kind) => kind switch
    {
        "tools" => ContextWindowCategory.Tools,
        "instructions" => ContextWindowCategory.Files,
        "messages" => ContextWindowCategory.Messages,
        _ => ContextWindowCategory.System,
    };

    /// <summary>卡片上会出现的文字(只展示用的部分);发送 / 附加 / 折叠的演示文字不会出现,保留 Arc 的默认值。</summary>
    private static ContextWindowLabels LocalizedLabels() => new()
    {
        Region = NcwTexts.Get(NcwTextKind.ContextCardRegion),
        OfTokens = window => NcwTexts.Format(NcwTextKind.ContextCardOfTokens, window),
        Used = NcwTexts.Get(NcwTextKind.ContextCardUsed),
        FreeTokens = free => NcwTexts.Format(NcwTextKind.ContextCardFreeTokens, free),
        CellSize = size => NcwTexts.Format(NcwTextKind.ContextCardCellSize, size),
        CompactsAt = percent => NcwTexts.Format(NcwTextKind.ContextCardCompactsAt, percent),
        SystemPrompt = NcwTexts.Get(NcwTextKind.ContextCardSystem),
        Tools = NcwTexts.Get(NcwTextKind.ContextCardTools),
        Files = NcwTexts.Get(NcwTextKind.ContextCardInstructions),
        Summary = NcwTexts.Get(NcwTextKind.ContextCardSummary),
        Messages = NcwTexts.Get(NcwTextKind.ContextCardMessages),
        Free = NcwTexts.Get(NcwTextKind.ContextCardFree),
        Reserve = NcwTexts.Get(NcwTextKind.ContextCardReserve),
        NoSummary = NcwTexts.Get(NcwTextKind.ContextCardNoSummary),
        Legend = NcwTexts.Get(NcwTextKind.ContextCardLegend),
        LegendRow = (name, tokens, percent) => NcwTexts.Format(NcwTextKind.ContextCardLegendRow, name, tokens, percent),
        ModelPill = (name, window) => NcwTexts.Format(NcwTextKind.ContextCardModelPill, name, window),
        ModelOption = (name, window) => NcwTexts.Format(NcwTextKind.ContextCardModelPill, name, window),
        ModelMenu = NcwTexts.Get(NcwTextKind.ContextCardModelMenu),
        GridPart = (name, tokens) => NcwTexts.Format(NcwTextKind.ContextCardGridPart, name, tokens),
        GridDescription = (model, used, window, parts, threshold) => NcwTexts.Format(NcwTextKind.ContextCardGridDescription, model, used, window, parts, threshold),
        Idle = threshold => NcwTexts.Format(NcwTextKind.ContextCardIdle, threshold),
        Compact = NcwTexts.Get(NcwTextKind.ContextCardCompact),
        NothingToFold = NcwTexts.Get(NcwTextKind.ContextCardNothingToFold),
    };
}
