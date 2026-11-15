using System.Text;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Auth;

namespace Ncw.Agent.Parity.Tests.Rig;

/// <summary>
/// 整机回放用的假上游:多个会话(主会话、子代理、判定器……)并发调用同一个上游,按「谁先到」消费脚本不可靠,所以按<b>路由键</b>分派:每个路由键有自己的脚本队列
/// (2.x 一侧的录制脚本用同一份路由规则:首条用户消息以 <c>firstUser</c> 开头、系统提示词含 <c>systemIncludes</c>、最后一条用户消息含 <c>lastUserIncludes</c>,取第一条匹配的)。
/// 脚本项可以带 <c>gate</c>:响应要等这道闸门被放行(用来排定「子代理在父 Run 结束之后才完成」这类跨 Run 的先后)。
/// 响应的构造交给每个键各自的 <see cref="ScriptedUpstream"/>(和单会话回放同一份 SSE 回放逻辑)。
/// </summary>
public sealed class RoutedUpstream : IUpstreamHttp
{
    private readonly IReadOnlyList<JsonObject> _routes;
    private readonly Dictionary<string, ScriptedUpstream> _byKey = [];
    private readonly Dictionary<string, List<JsonObject>> _scripts = [];
    private readonly Dictionary<string, int> _counts = [];
    private readonly Func<string, Task> _gate;
    private readonly object _lock = new();

    public RoutedUpstream(JsonArray routes, JsonObject upstream, Action<string, int, int> chunkConsumed, Func<string, Task> gate)
    {
        _routes = [.. routes.Select(r => r!.AsObject())];
        _gate = gate;
        foreach (var (key, items) in upstream)
        {
            var script = items!.AsArray().Select(i => i!.AsObject()).ToList();
            _scripts[key] = script;
            _byKey[key] = new ScriptedUpstream(script, (request, chunk) => chunkConsumed(key, request, chunk));
        }
    }

    /// <summary>每个路由键收到的请求(按到达顺序)。</summary>
    public IReadOnlyList<(Uri Url, JsonNode? Body)> RequestsOf(string key) => _byKey.TryGetValue(key, out var u) ? u.Requests.ToList() : [];

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var key = RouteOf(body) ?? throw new InvalidOperationException($"No route matches this upstream request (first user message: {Clip(FirstUser(body))}).");
        int index;
        lock (_lock)
        {
            index = _counts.GetValueOrDefault(key);
            _counts[key] = index + 1;
        }

        if (index < _scripts[key].Count)
        {
            SubstitutePlanPath(_scripts[key][index], body);
            if (_scripts[key][index]["gate"]?.GetValue<string>() is { } gate)
            {
                await _gate(gate).WaitAsync(ct).ConfigureAwait(false);
            }
        }

        return await _byKey[key].SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 计划文件的路径每次都不同,而录制的 SSE 是静态文本:脚本里写 <c>{{PLAN_PATH}}</c>,回放时(2.x 录制侧同一份做法)换成「这次请求的历史里最近一次 EnterPlanMode 结果
    /// (<c>plan_file_entered</c>)给出的路径」。
    /// </summary>
    private static void SubstitutePlanPath(JsonObject item, JsonNode? body)
    {
        if (item["chunks"] is not JsonArray chunks || !chunks.Any(c => c!.GetValue<string>().Contains("{{PLAN_PATH}}", StringComparison.Ordinal)))
        {
            return;
        }

        string? path = null;
        foreach (var message in body?["messages"]?.AsArray() ?? [])
        {
            foreach (var block in message?["content"] as JsonArray ?? [])
            {
                if (block?["type"]?.GetValue<string>() == "tool_result" && TextOf(block["content"]) is { } text && text.Contains("\"plan_file_entered\"", StringComparison.Ordinal))
                {
                    path = JsonNode.Parse(text)!["path"]!.GetValue<string>();
                }
            }
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            chunks[i] = chunks[i]!.GetValue<string>().Replace("{{PLAN_PATH}}", path ?? throw new InvalidOperationException("The script uses {{PLAN_PATH}} but the request history has no EnterPlanMode result."), StringComparison.Ordinal);
        }
    }

    private string? RouteOf(JsonNode? body)
    {
        var first = FirstUser(body);
        var last = LastUser(body);
        var system = SystemText(body);
        foreach (var route in _routes)
        {
            if (route["firstUser"]?.GetValue<string>() is { } f && !first.StartsWith(f, StringComparison.Ordinal))
            {
                continue;
            }

            if (route["systemIncludes"]?.GetValue<string>() is { } s && !system.Contains(s, StringComparison.Ordinal))
            {
                continue;
            }

            if (route["lastUserIncludes"]?.GetValue<string>() is { } l && !last.Contains(l, StringComparison.Ordinal))
            {
                continue;
            }

            return route["key"]!.GetValue<string>();
        }

        return null;
    }

    private static string Clip(string text) => text.Length > 80 ? text[..80] : text;

    private static string TextOf(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join('\n', a.Select(b => b?["type"]?.GetValue<string>() switch
        {
            "text" => b["text"]?.GetValue<string>() ?? "",
            "tool_result" => TextOf(b["content"]),
            _ => "",
        })),
        _ => "",
    };

    private static string FirstUser(JsonNode? body)
    {
        foreach (var m in body?["messages"]?.AsArray() ?? [])
        {
            if (m?["role"]?.GetValue<string>() == "user")
            {
                return m["content"] is JsonValue v && v.TryGetValue<string>(out var s)
                    ? s
                    : string.Join('\n', (m["content"]?.AsArray() ?? []).Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>()));
            }
        }

        return "";
    }

    private static string LastUser(JsonNode? body) =>
        TextOf((body?["messages"]?.AsArray() ?? []).LastOrDefault(m => m?["role"]?.GetValue<string>() == "user")?["content"]);

    private static string SystemText(JsonNode? body) => body?["system"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray a => string.Join('\n', a.Select(b => b?["text"]?.GetValue<string>() ?? "")),
        _ => "",
    };
}
