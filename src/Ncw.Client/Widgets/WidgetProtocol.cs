using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;

namespace Ncw.Client.Widgets;

/// <summary>
/// 渲染宿主(控制端)与 WebView 里那页外壳之间的消息名。与 2.x 的 <c>WIDGET_MESSAGE</c> 同名同值(<c>ncw:widget:*</c>),
/// 外壳的脚本(<see cref="WidgetShell.Runtime"/>)里逐字写了同一份,<c>WidgetShellTests</c> 核对两边一致。
/// </summary>
public static class WidgetMessages
{
    // 页面 → 宿主(<c>invokeCSharpAction</c>)
    public const string Ready = "ncw:widget:ready";
    public const string Height = "ncw:widget:height";
    public const string Call = "ncw:widget:call";

    // 宿主 → 页面(<c>__ncwHost.recv</c>)
    public const string Content = "ncw:widget:content";
    public const string Theme = "ncw:widget:theme";
    public const string Loading = "ncw:widget:loading";
    public const string Env = "ncw:widget:env";
    public const string Reply = "ncw:widget:reply";

    /// <summary>外壳脚本必须出现的全部消息名(核对用)。</summary>
    public static IReadOnlyList<string> All { get; } = [Ready, Height, Call, Content, Theme, Loading, Env, Reply];
}

/// <summary>桥上各通道的长度上限(字符)。超了直接拒绝、不截断:截断后的 JSON 是坏的。数值取自 2.x 的 <c>WIDGET_LIMITS</c>。</summary>
public static class WidgetLimits
{
    /// <summary>页面发来的一整条消息(含 JSON 转义的膨胀:提交值 32K 字符最坏约 6 倍)。</summary>
    public const int MaxMessageChars = 256 * 1024;

    public const int State = 16 * 1024;

    public const int Submit = 32 * 1024;

    public const int ActionId = 64;

    public const int ActionParams = 4096;

    public const int ActionParamCount = 16;

    public const int ParamName = 64;

    public const int ParamString = 256;

    public const int Url = 4096;

    /// <summary>JSON 嵌套深度上限(<c>setState</c> 的状态是模型写的代码给的,不可信)。</summary>
    public const int MaxDepth = 32;

    /// <summary>外壳报高的钳制区间(2.x:下限很小,上限 1400 只为挡住失控的上报)。</summary>
    public const double MinHeight = 24;

    public const double MaxHeight = 1400;

    public static double ClampHeight(double reported) =>
        double.IsFinite(reported) ? Math.Max(MinHeight, Math.Min(MaxHeight, Math.Round(reported))) : MinHeight;
}

/// <summary>桥方法白名单。别的名字一律拒绝。</summary>
public static class WidgetMethods
{
    public const string SetState = "setState";
    public const string Submit = "submit";
    public const string RunAction = "runAction";
    public const string OpenUrl = "openUrl";
}

/// <summary>页面发来的一条(已校验的)消息。</summary>
public abstract record WidgetInbound;

public sealed record WidgetReady : WidgetInbound;

/// <summary>外壳报上来的内容高度(未钳制)。</summary>
public sealed record WidgetHeight(double Value) : WidgetInbound;

/// <summary>桥调用:<see cref="Id"/> 是页面自己的请求号,回复带回它。</summary>
public abstract record WidgetBridgeCall(int Id) : WidgetInbound;

/// <param name="StateJson">要保存的状态(JSON 文本);null = 清掉。</param>
public sealed record SetStateCall(int Id, string? StateJson) : WidgetBridgeCall(Id);

public sealed record SubmitCall(int Id, string Value) : WidgetBridgeCall(Id);

public sealed record RunActionCall(int Id, string ActionId, string ParamsJson) : WidgetBridgeCall(Id);

public sealed record OpenUrlCall(int Id, Uri Url) : WidgetBridgeCall(Id);

/// <summary>被拒绝的消息。<see cref="Id"/> 有值时(能认出是一次调用)宿主回一条失败的回复,让页面里的 Promise 不悬着。</summary>
public sealed record WidgetRejected(string Reason, int? Id = null) : WidgetInbound;

/// <summary>
/// 页面消息的解析与白名单校验(纯函数,从不抛)。<b>页面里跑的是模型写的代码,每一条消息都当成不可信输入</b>:
/// 只认已知的消息类型和桥方法、每个字段逐一收窄、大小有上限;任何不合规矩的东西变成 <see cref="WidgetRejected"/>,绝不「尽量理解」。
/// </summary>
public static class WidgetInboundParser
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = WidgetLimits.MaxDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    public static WidgetInbound Parse(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return new WidgetRejected("empty");
        }

        if (body.Length > WidgetLimits.MaxMessageChars)
        {
            return new WidgetRejected("too-large");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body, Options);
        }
        catch (JsonException)
        {
            return new WidgetRejected("not-json");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new WidgetRejected("not-object");
            }

            switch (Str(root, "type"))
            {
                case WidgetMessages.Ready:
                    return new WidgetReady();
                case WidgetMessages.Height:
                    return root.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number && h.TryGetDouble(out var height) && double.IsFinite(height) && height is >= 0 and <= 1e7
                        ? new WidgetHeight(height)
                        : new WidgetRejected("bad-height");
                case WidgetMessages.Call:
                    return ParseCall(root);
                default:
                    return new WidgetRejected("unknown-type");
            }
        }
    }

    private static WidgetInbound ParseCall(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt32(out var id) || id < 1)
        {
            return new WidgetRejected("bad-id");
        }

        var args = root.TryGetProperty("args", out var a) ? a : default;
        if (args.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
        {
            return new WidgetRejected("bad-args", id);
        }

        switch (Str(root, "method"))
        {
            case WidgetMethods.SetState:
            {
                if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("state", out var state) || state.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    return new SetStateCall(id, null);
                }

                var raw = state.GetRawText();
                return raw.Length > WidgetLimits.State ? new WidgetRejected("state-too-large", id) : new SetStateCall(id, raw);
            }

            case WidgetMethods.Submit:
                return Str(args, "value") is { } value && value.Length <= WidgetLimits.Submit ? new SubmitCall(id, value) : new WidgetRejected("bad-args", id);

            case WidgetMethods.RunAction:
            {
                if (Str(args, "id") is not { Length: > 0 } actionId || actionId.Length > WidgetLimits.ActionId || actionId.Any(char.IsControl))
                {
                    return new WidgetRejected("bad-args", id);
                }

                return ParamsJson(args) is { } paramsJson ? new RunActionCall(id, actionId, paramsJson) : new WidgetRejected("bad-args", id);
            }

            case WidgetMethods.OpenUrl:
                return Str(args, "url") is { } url && TryExternalUrl(url, out var uri) ? new OpenUrlCall(id, uri) : new WidgetRejected("bad-args", id);

            default:
                return new WidgetRejected("unknown-method", id);
        }
    }

    /// <summary>
    /// 能交给系统浏览器的地址:绝对 http / https、有主机名、不带用户信息、长度有上限。链接点击(导航被拦下来的地址)和 <c>openUrl</c> 用同一个判据。
    /// </summary>
    public static bool TryExternalUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (url is not { Length: > 0 and <= WidgetLimits.Url } || url.Any(char.IsControl) || !Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme is not ("https" or "http") || parsed.Host.Length == 0 || parsed.UserInfo.Length > 0)
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    /// <summary>动作参数:对象,至多 16 项,值只能是有界字符串 / 数字 / 布尔(服务端再按声明的枚举与区间校验)。缺省 = 没有参数。非法返回 null。</summary>
    private static string? ParamsJson(JsonElement args)
    {
        if (!args.TryGetProperty("params", out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "{}";
        }

        if (p.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            var count = 0;
            foreach (var prop in p.EnumerateObject())
            {
                if (++count > WidgetLimits.ActionParamCount || prop.Name.Length is 0 or > WidgetLimits.ParamName)
                {
                    return null;
                }

                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String when prop.Value.GetString()!.Length <= WidgetLimits.ParamString:
                        w.WriteString(prop.Name, prop.Value.GetString());
                        break;
                    case JsonValueKind.Number:
                        w.WritePropertyName(prop.Name);
                        prop.Value.WriteTo(w);
                        break;
                    case JsonValueKind.True or JsonValueKind.False:
                        w.WriteBoolean(prop.Name, prop.Value.GetBoolean());
                        break;
                    default:
                        return null;
                }
            }

            w.WriteEndObject();
        }

        return buffer.WrittenCount <= WidgetLimits.ActionParams ? System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan) : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>令牌桶限速(时间由 <see cref="TimeProvider"/> 注入,单测不睡真的时间)。线程安全。</summary>
public sealed class WidgetRateLimiter
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly double _burst;
    private readonly double _perSecond;
    private double _tokens;
    private long _last;

    public WidgetRateLimiter(TimeProvider clock, double burst, double perSecond)
    {
        _clock = clock;
        _burst = burst;
        _perSecond = perSecond;
        _tokens = burst;
        _last = clock.GetTimestamp();
    }

    public bool TryTake()
    {
        lock (_gate)
        {
            var now = _clock.GetTimestamp();
            _tokens = Math.Min(_burst, _tokens + _clock.GetElapsedTime(_last, now).TotalSeconds * _perSecond);
            _last = now;
            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }
}

/// <summary>宿主 → 页面推送的环境:这张卡此刻的状态 / 数据 / 交互 / 动作(2.x 的 <c>WidgetEnvironment</c>)。</summary>
public sealed record WidgetEnvironment(string Title, string? StateJson, string? DataJson, bool Interactive, bool Submitted, ImmutableArray<WidgetActionInfo> Actions);

/// <summary>宿主 → 页面的消息构造(纯函数,产出 JSON 文本)。字符串里的非 ASCII、<c>&lt;</c>、<c>&gt;</c> 一律 <c>\uXXXX</c> 转义,所以也能安全地嵌进一条脚本语句。</summary>
public static class WidgetPush
{
    /// <summary>页面里接收宿主消息的入口(外壳脚本定义,见 <see cref="WidgetShell.Runtime"/>)。</summary>
    public const string HostObject = "__ncwHost";

    /// <summary>把一条消息包成 <c>InvokeScript</c> 要执行的语句。</summary>
    public static string Script(string messageJson) => $"{HostObject}.recv({messageJson});";

    public static string Content(string html, string format) =>
        Write(w =>
        {
            w.WriteString("type", WidgetMessages.Content);
            w.WriteString("html", html);
            w.WriteString("format", format);
        });

    public static string Loading(string message) =>
        Write(w =>
        {
            w.WriteString("type", WidgetMessages.Loading);
            w.WriteString("message", message);
        });

    public static string Theme(WidgetTheme theme) =>
        Write(w =>
        {
            w.WriteString("type", WidgetMessages.Theme);
            w.WriteStartObject("tokens");
            foreach (var (name, value) in theme.Tokens)
            {
                w.WriteString(name, value);
            }

            w.WriteEndObject();
            w.WriteString("appearance", theme.Appearance == WidgetAppearance.Light ? "light" : "dark");
        });

    public static string Env(WidgetEnvironment env) =>
        Write(w =>
        {
            w.WriteString("type", WidgetMessages.Env);
            w.WriteStartObject("env");
            w.WriteString("title", env.Title);
            WriteRawOrNull(w, "state", env.StateJson);
            WriteRawOrNull(w, "data", env.DataJson);
            w.WriteBoolean("interactive", env.Interactive);
            w.WriteBoolean("submitted", env.Submitted);
            w.WriteStartArray("actions");
            foreach (var action in env.Actions)
            {
                w.WriteStartObject();
                w.WriteString("id", action.Id);
                w.WriteString("label", action.Label);
                if (action.Params.Length > 0)
                {
                    w.WriteStartObject("params");
                    foreach (var (name, spec) in action.Params)
                    {
                        w.WriteStartObject(name);
                        w.WriteString("type", spec.Type);
                        if (spec.Type == "enum")
                        {
                            w.WriteStartArray("values");
                            foreach (var v in spec.Values)
                            {
                                w.WriteStringValue(v);
                            }

                            w.WriteEndArray();
                        }
                        else
                        {
                            w.WriteNumber("min", spec.Min);
                            w.WriteNumber("max", spec.Max);
                            w.WriteBoolean("integer", spec.Integer);
                        }

                        w.WriteEndObject();
                    }

                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });

    /// <summary>调用的回复。<paramref name="valueJson"/> 是成功时的返回值(JSON 文本,缺省 null);失败给稳定的英文错误码(不是界面文字)。</summary>
    public static string Reply(int id, bool ok, string? valueJson = null, string? error = null) =>
        Write(w =>
        {
            w.WriteString("type", WidgetMessages.Reply);
            w.WriteNumber("id", id);
            w.WriteBoolean("ok", ok);
            if (ok)
            {
                WriteRawOrNull(w, "value", valueJson);
            }
            else
            {
                w.WriteString("error", error ?? "failed");
            }
        });

    private static void WriteRawOrNull(Utf8JsonWriter w, string name, string? json)
    {
        // 状态 / 数据是设备给的(源头是模型的代码):必须是合法 JSON 才原样写入,否则当没有
        if (json is not null && IsValidJson(json))
        {
            w.WritePropertyName(name);
            w.WriteRawValue(json, skipInputValidation: true);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private static bool IsValidJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
