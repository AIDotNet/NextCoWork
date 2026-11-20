using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.History;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Sse;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Diagnostics;

/// <summary>调试记录里的一条事件(docs/design/09 <c>providers.debug/&lt;runId&gt;</c> 主题的 <c>Kind</c>)。</summary>
public enum ProbeEventKind
{
    /// <summary>请求发出:<c>Name</c> = URL,<c>Data</c> = 已脱敏的请求正文。</summary>
    Request,

    /// <summary>响应头到了:<c>Name</c> = 状态码,<c>Data</c> = 响应头(一行一个)。</summary>
    Response,

    /// <summary>一个原始 SSE 事件:<c>Name</c> = 事件名,<c>Data</c> = 数据(已脱敏、可能被截断)。</summary>
    Sse,

    Text,
    Thinking,

    /// <summary>一次完整的工具调用:<c>Name</c> = 工具名,<c>Data</c> = 参数 JSON。</summary>
    ToolCall,

    /// <summary>消息结束:<c>Name</c> = 停止原因。</summary>
    End,

    /// <summary>失败:<c>Name</c> = 失败类别,<c>Data</c> = 已脱敏的说明。</summary>
    Error,
}

public sealed record ProbeEvent(int Seq, int ElapsedMs, ProbeEventKind Kind, string? Name, string Data);

public sealed record ProbeHeader(string Name, string Value);

/// <summary>调试失败的类别(界面据此给出修法):鉴权被拒、地址不对(404)、其它 HTTP 状态、连不上、超时、流没收完、流里报错、请求在本地就组不出来。</summary>
public enum ProbeFailureKind
{
    Rejected,
    NotFound,
    Http,
    Network,
    Timeout,
    Incomplete,
    Stream,
    Local,
}

/// <param name="Code">上游错误归一化后的类别(HTTP 失败、流里的错误才有)。</param>
/// <param name="Message">已脱敏的说明。</param>
public sealed record ProbeFailure(ProbeFailureKind Kind, AgentErrorCode? Code, string Message);

/// <summary>
/// 一次调试请求的输入。<paramref name="Binding"/> 决定上游模型名、思考配置、请求补丁(已是这次所选协议下的那一份);
/// <paramref name="Credential"/> 为 null = 不带鉴权头。<paramref name="Provider"/> 只取地址、名字和 Anthropic 缓存生存期,它自己的协议不看。
/// </summary>
public sealed record ProbeRequest(
    ProviderConfig Provider,
    ModelBinding Binding,
    WireProtocol Protocol,
    ProviderCredential? Credential,
    string Prompt,
    int MaxOutputTokens,
    ThinkingLevel Thinking,
    bool WithTool);

/// <summary>一次调试的完整记录。所有文本都已脱敏;<see cref="Events"/> 可能被截断(<see cref="EventsTruncated"/>)。</summary>
public sealed record ProbeResult(
    bool Ok,
    WireProtocol Protocol,
    string Model,
    string Method,
    string Url,
    IReadOnlyList<ProbeHeader> RequestHeaders,
    string RequestBody,
    int? Status,
    IReadOnlyList<ProbeHeader> ResponseHeaders,
    string? ResponseBody,
    IReadOnlyList<ProbeEvent> Events,
    bool EventsTruncated,
    string Text,
    string Thinking,
    IReadOnlyList<string> ToolCalls,
    StopReason? Stop,
    TokenUsage? Usage,
    string? ResponseModel,
    int? FirstTokenMs,
    int? TotalMs,
    ProbeFailure? Failure);

public sealed class ProbeOptions
{
    public string UserAgent { get; init; } = "NextCoWork";

    /// <summary>多久没收到任何字节判超时。比对话的空闲超时短得多:调试的人在盯着看。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>整体上限。</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>最多记多少条事件(SSE + 解码事件);超过的照样解码,只是不再记录。</summary>
    public int MaxEvents { get; init; } = 2000;

    /// <summary>单条事件数据最多保留多少字符。</summary>
    public int MaxEventData { get; init; } = 8 * 1024;

    /// <summary>请求正文 / 错误正文最多保留多少字符。</summary>
    public int MaxBody { get; init; } = 64 * 1024;

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// 供应商调试(docs/design/07「供应商调试」):用选定的协议和模型发<b>一条</b>真实的流式请求,把请求、响应、每个 SSE 事件和解码结果原样记下来。
/// 组请求和对话是同一份代码(<see cref="WireRequests"/>),解码是同一套解码器;和路由不同的是不重试、不换家、不轮换账号、不记健康和用量、401 之后不刷新令牌——
/// 调试要看的就是「这一条请求本身」的结局。永不抛异常(调用方取消除外),结局都在 <see cref="ProbeResult.Failure"/> 里。
/// </summary>
public sealed partial class ProviderProbe(IUpstreamHttp http, ITransportDecorator? transport = null, ProbeOptions? options = null)
{
    private const string ToolName = "get_current_time";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "x-api-key", "api-key", "x-goog-api-key", "cookie", "set-cookie",
    };

    private readonly ITransportDecorator _transport = transport ?? NoTransportDecoration.Instance;
    private readonly ProbeOptions _options = options ?? new ProbeOptions();

    /// <param name="onEvent">每条记录下来的事件(在发请求的线程上同步调用;抛出的异常被吞掉,调试照常进行)。</param>
    public async Task<ProbeResult> RunAsync(ProbeRequest request, Action<ProbeEvent>? onEvent = null, CancellationToken ct = default)
    {
        var run = new Run(request, _options, onEvent);
        var protocol = request.Protocol;
        var url = WireProtocols.JoinUrl(request.Provider.BaseUrl, protocol.PathOf());
        run.Url = run.Scrub(url);

        var context = new RequestContext("provider-debug", RunId: $"debug_{Guid.NewGuid():N}", SessionId: "");
        string payload;
        IReadOnlyDictionary<string, string> fixedHeaders;
        TransportDecoration decoration;
        try
        {
            decoration = request.Credential is { } credential ? _transport.Decorate(request.Provider, protocol, credential, context) : new TransportDecoration();
            if (request.Credential is SignatureCredential && decoration.Signer is null)
            {
                return run.Fail(ProbeFailureKind.Local, AgentErrorCode.Auth, "A signature credential needs a request signer for this provider.");
            }

            var llm = BuildRequest(request);
            var target = HistoryTarget.For(protocol, request.Provider.Id, request.Binding.UpstreamModel, request.Binding.SupportsImages);
            llm = llm with { Messages = HistoryTransform.Prepare(llm.Messages, target) };
            var (body, headers) = WireRequests.EncodeBody(request.Provider, request.Binding, llm, protocol, context, decoration);
            payload = body.ToJsonString();
            fixedHeaders = headers;
        }
        catch (Exception e) when (e is RequestPatchException or ThinkingAdapterException or ArgumentException or InvalidOperationException or JsonException)
        {
            return run.Fail(ProbeFailureKind.Local, AgentErrorCode.Provider, e.Message);
        }

        HttpRequestMessage message;
        try
        {
            message = WireRequests.BuildHttp(url, payload, fixedHeaders, request.Credential, protocol, decoration, _options.UserAgent);
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException or InvalidOperationException or FormatException)
        {
            return run.Fail(ProbeFailureKind.Local, AgentErrorCode.Provider, e.Message);
        }

        using (message)
        {
            run.Url = run.Scrub(message.RequestUri?.ToString() ?? url);
            run.RequestHeaders = run.MaskHeaders(message.Headers, message.Content?.Headers);
            run.RequestBody = run.Truncate(run.Scrub(PrettyJson(payload)), _options.MaxBody);
            run.Emit(ProbeEventKind.Request, run.Url, run.RequestBody);

            using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
            total.CancelAfter(_options.TotalTimeout);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            void ResetIdle() => idle.CancelAfter(_options.IdleTimeout);
            var token = idle.Token;
            try
            {
                ResetIdle();
                using var response = await http.SendAsync(message, token).ConfigureAwait(false);
                run.Status = (int)response.StatusCode;
                run.ResponseHeaders = run.MaskHeaders(response.Headers, response.Content.Headers);
                run.Emit(ProbeEventKind.Response, run.Status.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    string.Join('\n', run.ResponseHeaders.Select(h => $"{h.Name}: {h.Value}")));

                if (!response.IsSuccessStatusCode)
                {
                    ResetIdle();
                    var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    run.ResponseBody = run.Truncate(run.Scrub(text), _options.MaxBody);
                    var error = protocol == WireProtocol.Anthropic
                        ? AnthropicErrors.FromHttp(run.Status.Value, text, request.Provider.CacheTtl, request.Provider.Name)
                        : OpenAiCommon.ToAgentError(run.Status.Value, text);
                    var kind = run.Status.Value switch
                    {
                        401 or 403 => ProbeFailureKind.Rejected,
                        404 => ProbeFailureKind.NotFound,
                        _ => ProbeFailureKind.Http,
                    };
                    return run.Fail(kind, error.Code, error.Message);
                }

                ResetIdle();
                await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var decoder = WireCodecs.CreateDecoder(protocol);
                var batch = new List<LlmEvent>();
                var finished = false;
                await foreach (var sse in SseReader.ReadResponseAsync(stream, response.Content.Headers.ContentType?.MediaType, token).ConfigureAwait(false))
                {
                    ResetIdle();
                    run.Emit(ProbeEventKind.Sse, sse.Event, sse.Data);
                    batch.Clear();
                    finished = decoder.Feed(sse, batch);
                    run.Handle(batch);
                    if (finished)
                    {
                        break;
                    }
                }

                if (!finished)
                {
                    batch.Clear();
                    decoder.End(batch);
                    run.Handle(batch);
                }

                return run.Finish();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                var seconds = total.IsCancellationRequested ? _options.TotalTimeout.TotalSeconds : _options.IdleTimeout.TotalSeconds;
                return run.Fail(ProbeFailureKind.Timeout, AgentErrorCode.Network, $"No response progress within {seconds:0} seconds.");
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                return run.Fail(ProbeFailureKind.Network, AgentErrorCode.Network, $"Failed to connect: {e.Message}");
            }
        }
    }

    private static LlmRequest BuildRequest(ProbeRequest request)
    {
        var message = new Message(new MessageId("debug-user"), Role.User, [new TextPart(request.Prompt)], DateTimeOffset.UnixEpoch);
        IReadOnlyList<ToolInfo> tools = request.WithTool
            ? [new ToolInfo(ToolName, "Returns the current time on the user's device.", ToolSchema())]
            : [];
        return new LlmRequest("", [message], tools, new GenerationOptions(Math.Max(1, request.MaxOutputTokens), request.Thinking));
    }

    private static JsonElement ToolSchema()
    {
        using var document = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        return document.RootElement.Clone();
    }

    /// <summary>请求正文排版成多行(调试面板里好看);解不出来原样返回。</summary>
    private static string PrettyJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                document.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]+-----[\s\S]*?-----END [A-Z ]+-----")]
    private static partial Regex PrivateKeyBlock();

    /// <summary>key 在请求头里的显示形式:<c>••••</c> + 后四位(太短的整个盖住)。</summary>
    public static string Mask(string secret) => secret.Length >= 12 ? "••••" + secret[^4..] : "••••";

    /// <summary>一次调试的可变状态(只在发请求的那条逻辑流上用,不并发)。</summary>
    private sealed class Run(ProbeRequest request, ProbeOptions options, Action<ProbeEvent>? onEvent)
    {
        private readonly long _started = options.Clock.GetTimestamp();
        private readonly List<ProbeEvent> _events = [];
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _thinking = new();
        private readonly List<string> _toolCalls = [];
        private readonly Dictionary<string, (string Name, StringBuilder Args)> _openCalls = new(StringComparer.Ordinal);
        private readonly string[] _secrets = SecretsOf(request.Credential);
        private bool _truncated;
        private StopReason? _stop;
        private TokenUsage? _usage;
        private string? _responseModel;
        private int? _firstToken;
        private ProbeFailure? _failure;

        public string Url { get; set; } = "";

        public IReadOnlyList<ProbeHeader> RequestHeaders { get; set; } = [];

        public string RequestBody { get; set; } = "";

        public int? Status { get; set; }

        public IReadOnlyList<ProbeHeader> ResponseHeaders { get; set; } = [];

        public string? ResponseBody { get; set; }

        private int Elapsed => Math.Max(0, (int)options.Clock.GetElapsedTime(_started).TotalMilliseconds);

        public void Emit(ProbeEventKind kind, string? name, string data)
        {
            if (_events.Count >= options.MaxEvents)
            {
                _truncated = true;
                return;
            }

            var ev = new ProbeEvent(_events.Count + 1, Elapsed, kind, name is null ? null : Scrub(name), Truncate(Scrub(data), options.MaxEventData));
            _events.Add(ev);
            try
            {
                onEvent?.Invoke(ev);
            }
            catch (Exception)
            {
                // 进度回调(推给控制端)出问题不影响调试本身:权威结果是返回值。
            }
        }

        public void Handle(List<LlmEvent> batch)
        {
            foreach (var ev in batch)
            {
                switch (ev)
                {
                    case MessageStart start:
                        _responseModel = start.Model;
                        break;
                    case TextDelta text:
                        _firstToken ??= Elapsed;
                        _text.Append(text.Text);
                        Emit(ProbeEventKind.Text, null, text.Text);
                        break;
                    case ThinkingDelta thinking:
                        _firstToken ??= Elapsed;
                        _thinking.Append(thinking.Text);
                        Emit(ProbeEventKind.Thinking, null, thinking.Text);
                        break;
                    case ToolCallStart call:
                        _firstToken ??= Elapsed;
                        _openCalls[call.CallId] = (call.Name, new StringBuilder());
                        break;
                    case ToolCallDelta delta when _openCalls.TryGetValue(delta.CallId, out var open):
                        open.Args.Append(delta.ArgsDelta);
                        break;
                    case ToolCallEnd end when _openCalls.Remove(end.CallId, out var done):
                        var args = done.Args.Length == 0 ? "{}" : done.Args.ToString();
                        _toolCalls.Add($"{done.Name}({args})");
                        Emit(ProbeEventKind.ToolCall, done.Name, args);
                        break;
                    case MessageEnd end:
                        _stop = end.Stop;
                        _usage = end.Usage;
                        Emit(ProbeEventKind.End, end.Stop.ToString(), $"input={end.Usage.InputTokens} output={end.Usage.OutputTokens}");
                        break;
                    case StreamError error when _failure is null:
                        _failure = new ProbeFailure(ProbeFailureKind.Stream, error.Error.Code, Scrub(error.Error.Message));
                        Emit(ProbeEventKind.Error, nameof(ProbeFailureKind.Stream), error.Error.Message);
                        break;
                }
            }
        }

        public ProbeResult Fail(ProbeFailureKind kind, AgentErrorCode? code, string message)
        {
            _failure ??= new ProbeFailure(kind, code, Scrub(message));
            Emit(ProbeEventKind.Error, kind.ToString(), message);
            return Result();
        }

        public ProbeResult Finish()
        {
            if (_failure is null && _stop is null)
            {
                _failure = new ProbeFailure(ProbeFailureKind.Incomplete, AgentErrorCode.Network, "The upstream connection closed before the response completed.");
                Emit(ProbeEventKind.Error, nameof(ProbeFailureKind.Incomplete), _failure.Message);
            }

            return Result();
        }

        private ProbeResult Result() => new(
            _failure is null && _stop is not null,
            request.Protocol,
            request.Binding.UpstreamModel,
            "POST",
            Url,
            RequestHeaders,
            RequestBody,
            Status,
            ResponseHeaders,
            ResponseBody,
            [.. _events],
            _truncated,
            Scrub(_text.ToString()),
            Scrub(_thinking.ToString()),
            [.. _toolCalls.Select(Scrub)],
            _stop,
            _usage,
            _responseModel,
            _firstToken,
            Status is null && _failure is not null ? null : Elapsed,
            _failure);

        /// <summary>请求头 / 响应头:key 显示成 <c>••••后四位</c>;敏感头的值没认出是哪把 key(签名、cookie)也整个盖住只留后四位。</summary>
        public IReadOnlyList<ProbeHeader> MaskHeaders(HttpHeaders headers, HttpHeaders? content)
        {
            var list = new List<ProbeHeader>();
            foreach (var (name, values) in content is null ? headers : headers.Concat(content))
            {
                var value = string.Join(", ", values);
                var masked = value;
                foreach (var secret in _secrets)
                {
                    masked = masked.Replace(secret, Mask(secret), StringComparison.Ordinal);
                }

                if (masked == value && SensitiveHeaders.Contains(name) && value.Length > 0)
                {
                    var space = value.IndexOf(' ', StringComparison.Ordinal);
                    masked = space > 0 && space < 16 ? value[..(space + 1)] + Mask(value[(space + 1)..]) : Mask(value);
                }

                list.Add(new ProbeHeader(name.ToLowerInvariant(), PrivateKeyBlock().Replace(masked, "[redacted key]")));
            }

            return list;
        }

        /// <summary>文本里出现的 key / token 抹成 <c>[redacted]</c>(上游的错误正文可能原样回显),私钥块抹成 <c>[redacted key]</c>。</summary>
        public string Scrub(string text)
        {
            foreach (var secret in _secrets)
            {
                text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }

            return PrivateKeyBlock().Replace(text, "[redacted key]");
        }

        public string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

        private static string[] SecretsOf(ProviderCredential? credential) => (credential switch
        {
            ApiKeyCredential k => new[] { k.ApiKey },
            OAuthCredential o => [o.AccessToken, o.RefreshToken],
            SignatureCredential s => [s.SecretKey, s.SessionToken ?? ""],
            _ => [],
        }).Where(s => s.Length >= 4).OrderByDescending(s => s.Length).ToArray();
    }
}
