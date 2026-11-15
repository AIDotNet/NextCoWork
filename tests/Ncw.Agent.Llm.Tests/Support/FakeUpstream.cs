using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Tests.Support;

/// <summary>一次收到的请求。</summary>
public sealed record SeenRequest(HttpMethod Method, Uri Url, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public JsonElement Json => Jsn.Parse(Body);

    public string Host => Url.Host;

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

public sealed class FakeUpstream : HttpMessageHandler
{
    private readonly Queue<Func<SeenRequest, Task<HttpResponseMessage>>> _script = new();
    private readonly object _gate = new();
    private readonly List<SeenRequest> _requests = [];
    private readonly List<string> _violations = [];

    /// <summary>脚本用完后的默认回复(null = 500)。</summary>
    public Func<SeenRequest, Task<HttpResponseMessage>>? Fallback { get; set; }

    public IReadOnlyList<SeenRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    public IReadOnlyList<string> Violations
    {
        get
        {
            lock (_gate)
            {
                return _violations.ToList();
            }
        }
    }

    public FakeUpstream Enqueue(Func<SeenRequest, Task<HttpResponseMessage>> reply)
    {
        lock (_gate)
        {
            _script.Enqueue(reply);
        }

        return this;
    }

    public FakeUpstream Enqueue(Func<HttpResponseMessage> reply) => Enqueue(_ => Task.FromResult(reply()));

    /// <summary>像真实 API 一样严格:请求不合法就回 400,并记入 <see cref="Violations"/>(测试结束时断言为空)。</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string>();
        foreach (var (key, values) in request.Headers)
        {
            headers[key.ToLowerInvariant()] = string.Join(",", values);
        }

        if (request.Content is not null)
        {
            foreach (var (key, values) in request.Content.Headers)
            {
                headers[key.ToLowerInvariant()] = string.Join(",", values);
            }
        }

        var seen = new SeenRequest(request.Method, request.RequestUri!, headers, body);
        Func<SeenRequest, Task<HttpResponseMessage>>? reply;
        lock (_gate)
        {
            _requests.Add(seen);
            _script.TryDequeue(out reply);
        }

        if (StrictRequest.Validate(seen) is { } violation)
        {
            lock (_gate)
            {
                _violations.Add(violation);
            }

            var escaped = violation.Replace("\"", "'", StringComparison.Ordinal);
            return Reply.Json(HttpStatusCode.BadRequest, "{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"" + escaped + "\"}}");
        }

        reply ??= Fallback ?? (_ => Task.FromResult(Reply.Status(HttpStatusCode.InternalServerError, "no scripted reply")));
        return await reply(seen).ConfigureAwait(false);
    }
}

public static class Reply
{
    public static HttpResponseMessage Sse(byte[] data, int chunkSize = int.MaxValue) =>
        new(HttpStatusCode.OK) { Content = Stream(new ChunkedStream(data, chunkSize)) };

    public static HttpResponseMessage Sse(string text, int chunkSize = int.MaxValue) => Sse(Encoding.UTF8.GetBytes(text), chunkSize);

    public static HttpResponseMessage Sample(string name, int chunkSize = 13) => Sse(Samples.Bytes(name), chunkSize);

    public static HttpResponseMessage Json(HttpStatusCode status, string json, params (string Name, string Value)[] headers) =>
        WithHeaders(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, headers);

    public static HttpResponseMessage Status(HttpStatusCode status, string message, params (string Name, string Value)[] headers) =>
        Json(status, "{\"error\":{\"type\":\"error\",\"message\":\"" + message + "\"}}", headers);

    /// <summary>先给出一段前缀,然后抛异常(模拟连接在流中途被掐断)。</summary>
    public static HttpResponseMessage SseThenFail(string prefix, Exception failure) =>
        new(HttpStatusCode.OK) { Content = Stream(new FailingStream(Encoding.UTF8.GetBytes(prefix), failure)) };

    /// <summary>先给出一段前缀,然后一直不再有数据(直到被取消)。</summary>
    public static HttpResponseMessage SseThenStall(string prefix) =>
        new(HttpStatusCode.OK) { Content = Stream(new FailingStream(Encoding.UTF8.GetBytes(prefix), null)) };

    /// <summary>每个块之间等待一段时间,总时长可以远超空闲超时(只要块之间的间隔没超)。</summary>
    public static HttpResponseMessage SsePaced(IEnumerable<string> chunks, TimeSpan gap) =>
        new(HttpStatusCode.OK) { Content = Stream(new PacedStream(chunks.Select(c => Encoding.UTF8.GetBytes(c)).ToList(), gap)) };

    private static StreamContent Stream(Stream stream)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return content;
    }

    private static HttpResponseMessage WithHeaders(HttpResponseMessage response, (string Name, string Value)[] headers)
    {
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}

internal sealed class FailingStream(byte[] prefix, Exception? failure) : Stream
{
    private int _pos;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pos < prefix.Length)
        {
            var n = Math.Min(buffer.Length, prefix.Length - _pos);
            prefix.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        if (failure is not null)
        {
            throw failure;
        }

        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class PacedStream(List<byte[]> chunks, TimeSpan gap) : Stream
{
    private int _chunk;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_chunk >= chunks.Count)
        {
            return 0;
        }

        await Task.Delay(gap, ct).ConfigureAwait(false);
        var data = chunks[_chunk++];
        data.CopyTo(buffer);
        return data.Length;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// 假上游对请求的严格校验(2.x <c>demo.ts</c> 的做法:演示上游像真实 API 一样校验,代码路径里没有任何 isDemo 分支)。
/// 只校验「真实 API 会 400」的那些形状规则。
/// </summary>
public static partial class StrictRequest
{
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex ToolId();

    public static string? Validate(SeenRequest request)
    {
        if (request.Method != HttpMethod.Post)
        {
            return "method must be POST";
        }

        // OAuth 刷新等非模型请求不在这里校验。
        var path = request.Url.AbsolutePath;
        if (path.EndsWith("/v1/messages", StringComparison.Ordinal))
        {
            return Anthropic(request);
        }

        if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return Chat(request);
        }

        if (path.EndsWith("/responses", StringComparison.Ordinal))
        {
            return Responses(request);
        }

        return null;
    }

    private static string? Anthropic(SeenRequest request)
    {
        if (request.Header("anthropic-version") is null)
        {
            return "missing anthropic-version header";
        }

        if (request.Header("x-api-key") is null && request.Header("authorization") is null && request.Header("x-signature") is null)
        {
            return "missing credential header";
        }

        var body = request.Json;
        if (body.Str("model") is null || body.Count("max_tokens") is not > 0)
        {
            return "model and max_tokens are required";
        }

        if (body.Bool("stream") != true)
        {
            return "stream must be true";
        }

        if (body.Obj("metadata").Str("user_id") is null)
        {
            return "metadata.user_id is required";
        }

        if (body.Obj("cache_control") is null)
        {
            return "top-level cache_control is required";
        }

        var messages = body.Arr("messages");
        if (messages.Count == 0 || messages[0].Str("role") != "user")
        {
            return "messages must be non-empty and start with a user message";
        }

        string? previousRole = null;
        string[]? pendingToolUses = null;
        foreach (var message in messages)
        {
            var role = message.Str("role");
            if (role == previousRole)
            {
                return "messages must alternate roles";
            }

            previousRole = role;
            var content = message.Arr("content");
            if (content.Count == 0)
            {
                return "message content must be non-empty";
            }

            var seenNonResult = false;
            var results = new List<string>();
            foreach (var block in content)
            {
                switch (block.Str("type"))
                {
                    case "text" when string.IsNullOrEmpty(block.Str("text")):
                        return "text content blocks must be non-empty";
                    case "thinking" when string.IsNullOrEmpty(block.Str("signature")):
                        return "thinking blocks require a signature";
                    case "tool_use" when block.Str("id") is not { } id || !ToolId().IsMatch(id):
                        return "tool_use id does not match ^[a-zA-Z0-9_-]{1,64}$";
                    case "tool_result":
                        if (seenNonResult)
                        {
                            return "tool_result blocks must come first in the user message";
                        }

                        results.Add(block.Str("tool_use_id") ?? "");
                        break;
                    default:
                        seenNonResult = true;
                        break;
                }
            }

            if (pendingToolUses is not null && (role != "user" || !pendingToolUses.All(results.Contains)))
            {
                return "every tool_use must have a tool_result in the next user message";
            }

            pendingToolUses = role == "assistant"
                ? content.Where(b => b.Str("type") == "tool_use").Select(b => b.Str("id")!).ToArray()
                : null;
            if (pendingToolUses is { Length: 0 })
            {
                pendingToolUses = null;
            }
        }

        if (pendingToolUses is not null)
        {
            return "the last assistant message has tool_use without a tool_result";
        }

        if (body.Obj("thinking") is { } thinking && thinking.Str("type") == "enabled"
            && thinking.Count("budget_tokens") is { } budget && budget >= body.Count("max_tokens"))
        {
            return "max_tokens must be greater than thinking.budget_tokens";
        }

        return null;
    }

    private static string? Chat(SeenRequest request)
    {
        if (request.Header("authorization") is not { } auth || !auth.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return "missing bearer authorization";
        }

        var body = request.Json;
        if (body.Str("model") is null || body.Bool("stream") != true)
        {
            return "model and stream are required";
        }

        var messages = body.Arr("messages");
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var role = message.Str("role");
            if (role == "tool")
            {
                if (message.Prop("content") is { ValueKind: JsonValueKind.Array })
                {
                    return "tool messages cannot carry image parts";
                }

                continue;
            }

            if (message.Arr("tool_calls") is { Count: > 0 } calls)
            {
                var ids = calls.Select(c => c.Str("id")).ToHashSet();
                var answered = new HashSet<string?>();
                var j = i + 1;
                for (; j < messages.Count && messages[j].Str("role") == "tool"; j++)
                {
                    answered.Add(messages[j].Str("tool_call_id"));
                }

                if (!ids.SetEquals(answered))
                {
                    return "assistant tool_calls must be followed immediately by tool messages for every call";
                }
            }
        }

        return null;
    }

    private static string? Responses(SeenRequest request)
    {
        if (request.Header("authorization") is not { } auth || !auth.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return "missing bearer authorization";
        }

        var body = request.Json;
        if (body.Bool("store") != false || body.Bool("stream") != true)
        {
            return "store must be false and stream true";
        }

        var input = body.Arr("input");
        for (var i = 0; i < input.Count; i++)
        {
            var item = input[i];
            switch (item.Str("type"))
            {
                case "reasoning":
                    if (item.Str("id") is { Length: > 64 })
                    {
                        return "input reasoning id exceeds 64 characters";
                    }

                    if (item.Prop("status") is not null)
                    {
                        return "Unknown parameter: input[].status";
                    }

                    if (item.Arr("content").Count > 0 && body.Str("model") is { } m && !m.StartsWith("deepseek", StringComparison.Ordinal))
                    {
                        return "Invalid input[].content: array too long. Expected an array with maximum length 0";
                    }

                    break;
                case "function_call":
                {
                    var callId = item.Str("call_id");
                    var answered = input.Skip(i + 1).Any(x => x.Str("type") == "function_call_output" && x.Str("call_id") == callId);
                    if (!answered)
                    {
                        return $"No tool output found for function call {callId}";
                    }

                    break;
                }
            }
        }

        return null;
    }
}

/// <summary>把所有协议的单元测试里常用的「完整请求 + 路由 + 假上游」装配在一起。</summary>
public sealed class Harness
{
    public FakeClock Clock { get; } = new();

    public FakeDelayer Delayer { get; }

    public FakeUpstream Upstream { get; } = new();

    public TestConfig Config { get; } = new();

    public MemoryCredentialStore Store { get; } = new();

    public List<UsageAttempt> Usage { get; } = [];

    public Routing.LlmRouter Router { get; private set; } = null!;

    public CredentialResolver Credentials { get; private set; } = null!;

    public Harness()
    {
        Delayer = new FakeDelayer(Clock);
    }

    public Harness Build(
        Routing.LlmRouterOptions? options = null,
        Pricing.IPriceBook? prices = null,
        AccountPool? accounts = null,
        Routing.IImageResolver? images = null,
        Routing.ITransportDecorator? transport = null,
        IOAuthRegistry? oauth = null,
        Routing.IUsageSink? usage = null)
    {
        var http = new HttpClientUpstream(new HttpClient(Upstream));
        Credentials = new CredentialResolver(Store, oauth ?? new OAuthRegistry([]), http, Clock);
        Router = new Routing.LlmRouter(
            Config, http, Credentials, Clock, new SequentialIds(), Delayer, options ?? new Routing.LlmRouterOptions(), prices,
            usage ?? new CollectingUsage(Usage), accounts, images, transport);
        return this;
    }

    public async Task<List<LlmEvent>> RunAsync(LlmRequest request, string alias = "smart", string? providerId = null, string workspace = "ws_test", CancellationToken ct = default)
    {
        var events = await Decoding.CollectAsync(Router.StreamAsync(new ModelRef(providerId, alias), request, new RequestContext(workspace), ct));
        Assert.Empty(Upstream.Violations);
        return events;
    }

    private sealed class CollectingUsage(List<UsageAttempt> list) : Routing.IUsageSink
    {
        public void OnAttempt(UsageAttempt attempt)
        {
            lock (list)
            {
                list.Add(attempt);
            }
        }
    }
}
