using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Widgets;

/// <summary>
/// 动作的一个参数约束(只有枚举与有界数字两种,和 Agent 的 <c>WidgetActions</c> 同一套):渲染宿主把它连同动作的 id / 文案推给小组件,让它画控件。
/// 动作要跑的工具和入参<b>不在这里</b>:那是 Agent 从转录里取的,控制端和小组件都改不了「要跑什么」。
/// </summary>
public sealed record WidgetParamSpec(string Type, ImmutableArray<string> Values, double Min, double Max, bool Integer);

public sealed record WidgetActionInfo(string Id, string Label, ImmutableArray<KeyValuePair<string, WidgetParamSpec>> Params);

/// <summary>消毒拿掉了什么(规则 id + 例子 + 次数);界面按 id 翻译。</summary>
public sealed record WidgetFinding(string Rule, string Detail, int Count);

/// <summary>根 <c>&lt;svg&gt;</c> 的尺寸提示(Agent 从 width / height / viewBox 读出来;缺哪项就是 null)。</summary>
public readonly record struct WidgetSizeHint(double? Width, double? Height)
{
    /// <summary>宽高都有时的高宽比(高 / 宽);否则 null。</summary>
    public double? Aspect => Width is > 0 && Height is > 0 ? Height / Width : null;
}

/// <summary>
/// 一张 widget 卡(Agent 的 <c>visualize_show_widget</c> / <c>visualize_edit_widget</c> 产出的 <c>kind: "widget"</c> Details,只给界面)。
/// 用<b>引用相等</b>(类而不是记录):同一份 Details 文本只解析一次(<see cref="WidgetCardReader"/> 缓存),转录行用引用判断「没变」,不逐字比几十 KB 的代码。
/// </summary>
public sealed class WidgetCard
{
    public required string Title { get; init; }

    public required string Format { get; init; }

    /// <summary>消毒后的源码(桌面的 WebView 渲染它)。</summary>
    public required string Code { get; init; }

    /// <summary>静态快照(无脚本、无外链资源;手机端和 WebView 不可用时的降级)。</summary>
    public required string Snapshot { get; init; }

    public WidgetSizeHint Size { get; init; }

    public bool Interactive { get; init; }

    /// <summary>交互模式提交后的值(JSON 文本);没提交为 null。</summary>
    public string? Submitted { get; init; }

    public ImmutableArray<WidgetActionInfo> Actions { get; init; } = [];

    public ImmutableArray<WidgetFinding> Findings { get; init; } = [];

    /// <summary>执行环境标识(设备 + 环境,AGENTS.md §5)。</summary>
    public EnvRefDto? Environment { get; init; }

    public bool IsSvg => Format == "svg";
}

/// <summary>读卡片 Details 的唯一入口。容错:坏 JSON、缺字段、不是 widget 卡都返回 null / 默认值,不抛。</summary>
public static class WidgetCardReader
{
    public const string KindWidget = "widget";

    public const string KindData = "widgetData";

    // Details 文本是不可变字符串,同一个实例跨多次派生(流式时每个增量都重新派生一遍)只解析一次;只读
    private static readonly ConditionalWeakTable<string, CardBox> Cache = [];

    private sealed class CardBox(WidgetCard? card) { public WidgetCard? Card { get; } = card; }

    public static WidgetCard? Read(string? detailsJson) =>
        string.IsNullOrEmpty(detailsJson) ? null : Cache.GetValue(detailsJson, static json => new CardBox(ReadCore(json))).Card;

    private static WidgetCard? ReadCore(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "kind") != KindWidget)
            {
                return null;
            }

            return new WidgetCard
            {
                Title = Str(root, "title") ?? "",
                Format = Str(root, "format") is "svg" ? "svg" : "html",
                Code = Str(root, "code") ?? "",
                Snapshot = Str(root, "snapshot") ?? "",
                Size = root.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Object ? new WidgetSizeHint(Num(size, "width"), Num(size, "height")) : default,
                Interactive = root.TryGetProperty("interactive", out var i) && i.ValueKind == JsonValueKind.True,
                Submitted = Str(root, "submitted"),
                Actions = ReadActions(root),
                Findings = ReadFindings(root),
                Environment = ReadEnvironment(root),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>visualize_update_widget</c> 推来的数据卡(<c>kind: "widgetData"</c>):返回 (标题, 数据的 JSON 文本);不是数据卡返回 null。
    /// 小组件按标题认领,后来者覆盖(同 2.x)。
    /// </summary>
    public static (string Title, string Data)? ReadData(string? detailsJson)
    {
        if (string.IsNullOrEmpty(detailsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && Str(root, "kind") == KindData && Str(root, "title") is { } title && root.TryGetProperty("data", out var data)
                ? (title, data.GetRawText())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>实时进度的 Details 是不是一张 widget 卡(交互模式先把卡片推出去再挂起)。</summary>
    public static bool IsWidgetCard(string? detailsJson) => detailsJson is not null && detailsJson.Contains("widget", StringComparison.Ordinal) && Read(detailsJson) is not null;

    /// <summary>是 widget 卡或数据卡(别的工具的 Details 不进转录镜像:先用子串快筛,不为每条工具结果都解析一遍)。</summary>
    public static bool IsDetails(string? detailsJson) =>
        detailsJson is not null && detailsJson.Contains("widget", StringComparison.Ordinal) && (Read(detailsJson) is not null || ReadData(detailsJson) is not null);

    private static ImmutableArray<WidgetActionInfo> ReadActions(JsonElement root)
    {
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = ImmutableArray.CreateBuilder<WidgetActionInfo>();
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object || Str(action, "id") is not { Length: > 0 } id)
            {
                continue;
            }

            var specs = ImmutableArray.CreateBuilder<KeyValuePair<string, WidgetParamSpec>>();
            if (action.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ps.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Object && Str(p.Value, "type") is { } type)
                    {
                        var values = p.Value.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array
                            ? vs.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToImmutableArray()
                            : [];
                        specs.Add(new(p.Name, new WidgetParamSpec(type, values, Num(p.Value, "min") ?? 0, Num(p.Value, "max") ?? 0, p.Value.TryGetProperty("integer", out var ig) && ig.ValueKind == JsonValueKind.True)));
                    }
                }
            }

            list.Add(new WidgetActionInfo(id, Str(action, "label") ?? id, specs.ToImmutable()));
        }

        return list.ToImmutable();
    }

    private static ImmutableArray<WidgetFinding> ReadFindings(JsonElement root) =>
        root.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array
            ? [.. f.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object && Str(e, "rule") is not null).Select(e => new WidgetFinding(Str(e, "rule")!, Str(e, "detail") ?? "", (int)(Num(e, "count") ?? 1)))]
            : [];

    private static EnvRefDto? ReadEnvironment(JsonElement root) =>
        root.TryGetProperty("environment", out var env) && env.ValueKind == JsonValueKind.Object && Str(env, "deviceId") is { } device
            ? new EnvRefDto(device, Str(env, "environment") ?? "", Str(env, "name") ?? "", Str(env, "type") ?? "local")
            : null;

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
}
