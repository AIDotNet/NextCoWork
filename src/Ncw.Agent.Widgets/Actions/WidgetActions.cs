using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Widgets.Actions;

/// <summary>
/// 预先声明的动作里一个参数的约束。<b>只有枚举和有界数字两种,没有自由字符串</b>:参数会被代进工具入参(典型是 <c>Bash</c> 的命令行),自由字符串等于让 widget 拼命令。
/// 两种约束下,所有可能的取值在声明时就能被看见(2.x <c>WidgetActionParam</c>)。
/// </summary>
public abstract record ActionParam
{
    public sealed record EnumValues(IReadOnlyList<string> Values) : ActionParam;

    public sealed record Number(double Min, double Max, bool Integer) : ActionParam;
}

/// <summary>
/// 一个预先声明的动作:widget 只能按 <see cref="Id"/> 触发,工具与入参在 <c>visualize_show_widget</c> 那次调用里就定死了(写进转录卡片)。
/// 入参里字符串形如 <c>{{name}}</c> 的片段由 <see cref="Params"/> 代入。
/// </summary>
public sealed record WidgetActionDef(string Id, string Label, string Tool, JsonElement Input, IReadOnlyDictionary<string, ActionParam> Params);

public sealed class WidgetActionException(string message) : Exception(message);

public static partial class WidgetActions
{
    public const int MaxActions = 16;

    /// <summary>
    /// 不许出现在 <c>actions</c> 里的工具:它们要么会挂起等人(在 widget 的按钮背后挂起,没有人知道它在等),要么会派出新的 agent(一颗按钮背后是一整轮看不见的运行)。
    /// 动作的定位是「一次有界的工具调用」,不是「从 widget 里再起一个 agent」。
    /// </summary>
    public static IReadOnlySet<string> ForbiddenTools { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "AskUserQuestion", "ExitPlanMode", "EnterPlanMode", "ProposeGoal", "Task", "Workflow",
        "visualize_read_me", "visualize_show_widget", "visualize_edit_widget", "visualize_update_widget",
    };

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex ParamNamePattern();

    /// <summary>占位符 <c>{{ name }}</c>。</summary>
    [GeneratedRegex(@"\{\{\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>从 <c>actions</c> 数组(模型给的 JSON)解析并校验一致性。失败是给模型看的说明。</summary>
    public static IReadOnlyList<WidgetActionDef> Parse(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new WidgetActionException("actions must be an array.");
        }

        var list = new List<WidgetActionDef>();
        foreach (var item in array.EnumerateArray())
        {
            list.Add(ParseOne(item));
        }

        if (list.Count > MaxActions)
        {
            throw new WidgetActionException($"At most {MaxActions} actions can be declared.");
        }

        Check(list);
        return list;
    }

    private static WidgetActionDef ParseOne(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new WidgetActionException("Each action must be an object.");
        }

        var id = item.Str("id") ?? "";
        if (!IdPattern().IsMatch(id))
        {
            throw new WidgetActionException($"Action id \"{id}\": use a short snake_case id.");
        }

        var label = item.Str("label")?.Trim() ?? "";
        if (label.Length is 0 or > 60)
        {
            throw new WidgetActionException($"Action {id}: label must be 1-60 characters.");
        }

        var tool = item.Str("tool") ?? "";
        if (tool.Length is 0 or > 128)
        {
            throw new WidgetActionException($"Action {id}: tool must name an existing tool.");
        }

        if (item.Obj("input") is not { } input)
        {
            throw new WidgetActionException($"Action {id}: input must be an object with the exact tool input.");
        }

        var parameters = new Dictionary<string, ActionParam>(StringComparer.Ordinal);
        if (item.Obj("params") is { } declared)
        {
            foreach (var property in declared.EnumerateObject())
            {
                if (!ParamNamePattern().IsMatch(property.Name))
                {
                    throw new WidgetActionException($"Action {id}: parameter name \"{property.Name}\" is not valid.");
                }

                parameters[property.Name] = ParseParam(id, property.Name, property.Value);
            }
        }

        return new WidgetActionDef(id, label, tool, input.Clone(), parameters);
    }

    private static ActionParam ParseParam(string actionId, string name, JsonElement spec)
    {
        switch (spec.Str("type"))
        {
            case "enum":
            {
                var values = spec.Arr("values").Where(static v => v.ValueKind == JsonValueKind.String).Select(static v => v.GetString()!).ToList();
                if (values.Count is 0 or > 32 || values.Any(static v => v.Length is 0 or > 200))
                {
                    throw new WidgetActionException($"Action {actionId}: parameter {name} needs 1-32 enum values of 1-200 characters.");
                }

                return new ActionParam.EnumValues(values);
            }

            case "number":
            {
                if (spec.Num("min") is not { } min || spec.Num("max") is not { } max)
                {
                    throw new WidgetActionException($"Action {actionId}: parameter {name} needs numeric min and max.");
                }

                return new ActionParam.Number(min, max, spec.Bool("integer") == true);
            }

            default:
                throw new WidgetActionException($"Action {actionId}: parameter {name} must have type \"enum\" or \"number\".");
        }
    }

    /// <summary>动作声明的一致性:id 不重、工具不在禁用表里、占位符与参数一一对应、数字的区间合法。</summary>
    public static void Check(IReadOnlyList<WidgetActionDef> actions)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            if (!ids.Add(action.Id))
            {
                throw new WidgetActionException($"Duplicate action id: {action.Id}.");
            }

            if (ForbiddenTools.Contains(action.Tool))
            {
                throw new WidgetActionException($"Action {action.Id}: the {action.Tool} tool cannot be used as a widget action.");
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            CollectPlaceholders(action.Input, used);
            foreach (var name in used)
            {
                if (!action.Params.ContainsKey(name))
                {
                    throw new WidgetActionException($"Action {action.Id}: placeholder {{{{{name}}}}} has no matching entry in params.");
                }
            }

            foreach (var (name, spec) in action.Params)
            {
                if (!used.Contains(name))
                {
                    throw new WidgetActionException($"Action {action.Id}: param {name} is never used in input.");
                }

                if (spec is ActionParam.Number number && !(number.Min <= number.Max))
                {
                    throw new WidgetActionException($"Action {action.Id}: param {name} has min > max.");
                }
            }
        }
    }

    private static void CollectPlaceholders(JsonElement node, HashSet<string> used)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                foreach (Match match in Placeholder().Matches(node.GetString() ?? ""))
                {
                    used.Add(match.Groups[1].Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    CollectPlaceholders(item, used);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    CollectPlaceholders(property.Value, used);
                }

                break;
        }
    }

    /// <summary>
    /// 把参数代进动作入参。参数不合法 → 抛 <see cref="WidgetActionException"/>(调用方转成给 widget 的拒绝)。
    /// 只代进<b>字符串叶子</b>;数字参数代进去仍是字符串的一部分,整串恰好是一个占位符时也保持字符串——工具入参的类型由声明者决定,不由参数决定。
    /// 枚举之外的值、越界数字、非整数、多余参数一律拒绝,不做「尽量代」。
    /// </summary>
    public static JsonElement Bind(WidgetActionDef action, JsonElement parameters)
    {
        var supplied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parameters.EnumerateObject())
            {
                if (!action.Params.ContainsKey(property.Name))
                {
                    throw new WidgetActionException($"Unknown parameter: {property.Name}");
                }

                supplied[property.Name] = property.Value;
            }
        }
        else if (parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw new WidgetActionException("Parameters must be an object.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, spec) in action.Params)
        {
            supplied.TryGetValue(name, out var value);
            switch (spec)
            {
                case ActionParam.EnumValues e:
                    if (value.ValueKind != JsonValueKind.String || !e.Values.Contains(value.GetString()!, StringComparer.Ordinal))
                    {
                        throw new WidgetActionException($"Parameter {name} must be one of: {string.Join(", ", e.Values)}");
                    }

                    values[name] = value.GetString()!;
                    break;
                case ActionParam.Number num:
                {
                    var ok = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d) && d >= num.Min && d <= num.Max && (!num.Integer || d == Math.Floor(d));
                    if (!ok)
                    {
                        throw new WidgetActionException($"Parameter {name} must be a {(num.Integer ? "integer" : "number")} in [{Format(num.Min)}, {Format(num.Max)}]");
                    }

                    values[name] = value.GetDouble().ToString("R", CultureInfo.InvariantCulture);
                    break;
                }
            }
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Rewrite(action.Input, values, writer);
        }

        return Jsn.Parse(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static void Rewrite(JsonElement node, Dictionary<string, string> values, Utf8JsonWriter writer)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                writer.WriteStringValue(Placeholder().Replace(node.GetString() ?? "", m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value));
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in node.EnumerateArray())
                {
                    Rewrite(item, values, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in node.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Rewrite(property.Value, values, writer);
                }

                writer.WriteEndObject();
                break;
            default:
                node.WriteTo(writer);
                break;
        }
    }

    /// <summary>动作写进卡片 Details 的形式(声明原样,参数约束结构化)。</summary>
    public static void Write(Utf8JsonWriter w, IReadOnlyList<WidgetActionDef> actions)
    {
        w.WriteStartArray();
        foreach (var action in actions)
        {
            w.WriteStartObject();
            w.WriteString("id", action.Id);
            w.WriteString("label", action.Label);
            w.WriteString("tool", action.Tool);
            w.WritePropertyName("input");
            action.Input.WriteTo(w);
            if (action.Params.Count > 0)
            {
                w.WriteStartObject("params");
                foreach (var (name, spec) in action.Params)
                {
                    w.WriteStartObject(name);
                    switch (spec)
                    {
                        case ActionParam.EnumValues e:
                            w.WriteString("type", "enum");
                            w.WriteStartArray("values");
                            foreach (var v in e.Values)
                            {
                                w.WriteStringValue(v);
                            }

                            w.WriteEndArray();
                            break;
                        case ActionParam.Number n:
                            w.WriteString("type", "number");
                            w.WriteNumber("min", n.Min);
                            w.WriteNumber("max", n.Max);
                            if (n.Integer)
                            {
                                w.WriteBoolean("integer", true);
                            }

                            break;
                    }

                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        w.WriteEndArray();
    }
}
