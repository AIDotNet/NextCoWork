using System.Net;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Diagnostics;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Tests.Support;
using Ncw.Agent.Llm.Wire;
using static Ncw.Agent.Llm.Tests.Support.Decoding;

namespace Ncw.Agent.Llm.Tests;

/// <summary>
/// 供应商调试(docs/design/07「供应商调试」):一条真实请求,组请求与对话同一份代码(<see cref="WireRequests"/>),
/// 结果里有脱敏后的请求、响应、每个 SSE 事件和解码结果;失败归类;取消照常抛。假上游像真实 API 一样严格校验请求形状。
/// </summary>
public sealed class ProviderProbeTests
{
    private const string Key = "sk-test-0123456789abcd";

    private static readonly ProviderConfig Provider = new("p", "Probe", "https://api.example.com", WireProtocol.OpenAiChat, 1, true, "provider:p");

    private static (ProviderProbe Probe, FakeUpstream Upstream) Make(ProbeOptions? options = null)
    {
        var upstream = new FakeUpstream();
        return (new ProviderProbe(new HttpClientUpstream(new HttpClient(upstream)), options: options), upstream);
    }

    private static ProbeRequest Request(WireProtocol protocol, string model = "model-x", ProviderCredential? credential = null, bool withTool = false, string? baseUrl = null) =>
        new(Provider with { BaseUrl = baseUrl ?? (protocol == WireProtocol.Anthropic ? "https://api.example.com" : "https://api.example.com/v1") },
            new ModelBinding(model, "p", model, 200_000, 8192),
            protocol,
            credential ?? new ApiKeyCredential(Key),
            "Reply with OK",
            64,
            ThinkingLevel.Auto,
            withTool);

    private static HttpResponseMessage AnthropicOk() => Reply.Sse(TypedFrames(
        ("message_start", """{"type":"message_start","message":{"model":"claude-real","usage":{"input_tokens":5}}}"""),
        ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"OK"}}"""),
        ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
        ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}"""),
        ("message_stop", """{"type":"message_stop"}""")));

    private static HttpResponseMessage ChatOk(string text = "OK") => Reply.Sse(Frames(
        "{\"model\":\"model-x\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"}}]}",
        """{"model":"model-x","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
        """{"model":"model-x","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":1}}""",
        "[DONE]"));

    [Fact]
    public async Task An_anthropic_probe_records_the_request_every_sse_event_and_the_decoded_reply()
    {
        var (probe, upstream) = Make();
        upstream.Enqueue(AnthropicOk);
        var live = new List<ProbeEvent>();

        var result = await probe.RunAsync(Request(WireProtocol.Anthropic, "claude-x"), live.Add);

        Assert.Empty(upstream.Violations);
        Assert.True(result.Ok, result.Failure?.Message);
        Assert.Null(result.Failure);
        var seen = Assert.Single(upstream.Requests);
        Assert.Equal("https://api.example.com/v1/messages", seen.Url.ToString());
        Assert.Equal(Key, seen.Header("x-api-key"));
        Assert.Equal("https://api.example.com/v1/messages", result.Url);
        Assert.Equal(200, result.Status);
        Assert.Equal("OK", result.Text);
        Assert.Equal(StopReason.EndTurn, result.Stop);
        Assert.Equal(5, result.Usage!.InputTokens);
        Assert.Equal("claude-real", result.ResponseModel);
        Assert.NotNull(result.FirstTokenMs);

        // 请求里的 key 显示成 ••••后四位,正文里没有 key
        Assert.Equal("••••abcd", result.RequestHeaders.Single(h => h.Name == "x-api-key").Value);
        Assert.Contains("\"claude-x\"", result.RequestBody);
        Assert.DoesNotContain(Key, result.RequestBody);

        // 事件:请求 → 响应 → 每个 SSE → 解码出的文字 → 结束;实时回调和结果里是同一份
        Assert.Equal(ProbeEventKind.Request, result.Events[0].Kind);
        Assert.Equal(ProbeEventKind.Response, result.Events[1].Kind);
        Assert.Equal("200", result.Events[1].Name);
        Assert.Equal(6, result.Events.Count(e => e.Kind == ProbeEventKind.Sse));
        Assert.Contains(result.Events, e => e is { Kind: ProbeEventKind.Sse, Name: "content_block_delta" });
        Assert.Contains(result.Events, e => e is { Kind: ProbeEventKind.Text, Data: "OK" });
        Assert.Equal(ProbeEventKind.End, result.Events[^1].Kind);
        Assert.Equal(result.Events, live);
        Assert.Equal(Enumerable.Range(1, result.Events.Count), result.Events.Select(e => e.Seq));
    }

    [Fact]
    public async Task The_chosen_protocol_decides_the_path_and_the_auth_header_not_the_providers_own()
    {
        var (probe, upstream) = Make();
        upstream.Enqueue(() => ChatOk()).Enqueue(() => Reply.Sample("openai-responses-reasoning-tool.sse"));

        var chat = await probe.RunAsync(Request(WireProtocol.OpenAiChat));
        var responses = await probe.RunAsync(Request(WireProtocol.OpenAiResponses));

        Assert.Empty(upstream.Violations);
        Assert.True(chat.Ok, chat.Failure?.Message);
        Assert.Equal("https://api.example.com/v1/chat/completions", upstream.Requests[0].Url.ToString());
        Assert.Equal($"Bearer {Key}", upstream.Requests[0].Header("authorization"));
        Assert.Equal("Bearer ••••abcd", chat.RequestHeaders.Single(h => h.Name == "authorization").Value);
        Assert.True(responses.Ok, responses.Failure?.Message);
        Assert.Equal("https://api.example.com/v1/responses", upstream.Requests[1].Url.ToString());
        Assert.NotEmpty(responses.ToolCalls);
    }

    [Fact]
    public async Task With_a_tool_the_request_carries_a_tool_definition()
    {
        var (probe, upstream) = Make();
        upstream.Enqueue(() => ChatOk());
        var result = await probe.RunAsync(Request(WireProtocol.OpenAiChat, withTool: true));
        Assert.True(result.Ok, result.Failure?.Message);
        Assert.Equal("get_current_time", upstream.Requests[0].Json.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Without_a_credential_no_auth_header_is_sent()
    {
        var (probe, upstream) = Make();
        var result = await probe.RunAsync(Request(WireProtocol.OpenAiChat) with { Credential = null });

        // 严格的假上游拒绝没鉴权的请求(真实 API 也会):调试要看到的正是这个 400
        Assert.Null(upstream.Requests[0].Header("authorization"));
        Assert.Equal(400, result.Status);
        Assert.Equal(ProbeFailureKind.Http, result.Failure!.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProbeFailureKind.Rejected)]
    [InlineData(HttpStatusCode.Forbidden, ProbeFailureKind.Rejected)]
    [InlineData(HttpStatusCode.NotFound, ProbeFailureKind.NotFound)]
    [InlineData(HttpStatusCode.BadGateway, ProbeFailureKind.Http)]
    public async Task Http_failures_are_classified_and_the_echoed_key_is_scrubbed(HttpStatusCode status, ProbeFailureKind kind)
    {
        var (probe, upstream) = Make();
        upstream.Enqueue(() => Reply.Status(status, $"bad key {Key}"));

        var result = await probe.RunAsync(Request(WireProtocol.OpenAiChat));

        Assert.False(result.Ok);
        Assert.Equal(kind, result.Failure!.Kind);
        Assert.Equal((int)status, result.Status);
        Assert.Contains("[redacted]", result.ResponseBody);
        Assert.DoesNotContain(Key, result.ResponseBody);
        Assert.DoesNotContain(Key, result.Failure.Message);
        Assert.All(result.Events, e => Assert.DoesNotContain(Key, e.Data));
        Assert.Equal(ProbeEventKind.Error, result.Events[^1].Kind);
    }

    [Fact]
    public async Task A_connection_failure_has_no_status_and_no_secrets()
    {
        var (probe, upstream) = Make();
        upstream.Enqueue(_ => throw new HttpRequestException("refused -----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----"));

        var result = await probe.RunAsync(Request(WireProtocol.OpenAiChat));

        Assert.Equal(ProbeFailureKind.Network, result.Failure!.Kind);
        Assert.Null(result.Status);
        Assert.DoesNotContain("PRIVATE KEY", result.Failure.Message);
    }

    [Fact]
    public async Task A_stream_that_ends_without_a_stop_is_incomplete()
    {
        var (probe, upstream) = Make();
        upstream.Enqueue(() => Reply.Sse(Frames("""{"model":"model-x","choices":[{"index":0,"delta":{"content":"O"}}]}""")));

        var result = await probe.RunAsync(Request(WireProtocol.OpenAiChat));

        Assert.False(result.Ok);
        Assert.Contains(result.Failure!.Kind, new[] { ProbeFailureKind.Incomplete, ProbeFailureKind.Stream });
        Assert.Equal("O", result.Text);
    }

    [Fact]
    public async Task No_progress_within_the_idle_timeout_is_a_timeout_and_cancelling_is_a_cancellation()
    {
        var (probe, upstream) = Make(new ProbeOptions { IdleTimeout = TimeSpan.FromMilliseconds(150) });
        upstream.Enqueue(() => Reply.SseThenStall(Frames("""{"model":"model-x","choices":[{"index":0,"delta":{"content":"O"}}]}""")));
        var stalled = await probe.RunAsync(Request(WireProtocol.OpenAiChat));
        Assert.Equal(ProbeFailureKind.Timeout, stalled.Failure!.Kind);

        upstream.Enqueue(() => Reply.SseThenStall(""));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.RunAsync(Request(WireProtocol.OpenAiChat) with { }, ct: cts.Token));
    }

    [Fact]
    public async Task Only_the_first_events_are_kept_when_there_are_too_many()
    {
        var (probe, upstream) = Make(new ProbeOptions { MaxEvents = 4 });
        upstream.Enqueue(() => ChatOk("a long reply"));
        var result = await probe.RunAsync(Request(WireProtocol.OpenAiChat));
        Assert.True(result.Ok, result.Failure?.Message);
        Assert.Equal(4, result.Events.Count);
        Assert.True(result.EventsTruncated);
        Assert.Equal("a long reply", result.Text);
    }

    [Fact]
    public void A_key_is_masked_to_its_last_four_and_a_short_one_entirely()
    {
        Assert.Equal("••••abcd", ProviderProbe.Mask(Key));
        Assert.Equal("••••", ProviderProbe.Mask("short"));
    }

    private sealed class MediaStubHttp(Func<HttpRequestMessage, HttpResponseMessage> responder) : IUpstreamHttp
    {
        public List<string> Urls { get; } = [];

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(responder(request));
        }
    }

    [Fact]
    public async Task Media_probes_hit_volcengine_image_and_video_endpoints_and_scrub_credentials()
    {
        var stub = new MediaStubHttp(req => req.RequestUri!.AbsolutePath.EndsWith("/images/generations", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"url":"https://tos.example/probe.png"}]}""", System.Text.Encoding.UTF8, "application/json"),
            }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"error\":{\"message\":\"invalid key " + Key + "\"}}", System.Text.Encoding.UTF8, "application/json"),
            });
        var probe = new ProviderProbe(stub);

        var img = await probe.RunMediaAsync(new MediaProbeRequest(
            "https://ark.cn-beijing.volces.com/api/v3",
            Ncw.Agent.Llm.Media.ProviderProtocol.VolcengineImages,
            "doubao-seedream-4-5-251128",
            new ApiKeyCredential(Key),
            "a tiny bonsai"));
        Assert.True(img.Ok, img.Failure?.Message);
        Assert.Equal("https://ark.cn-beijing.volces.com/api/v3/images/generations", img.Url);
        Assert.Equal("Bearer ••••abcd", img.RequestHeaders.Single(h => h.Name == "authorization").Value);
        Assert.Contains("https://tos.example/probe.png", img.Text, StringComparison.Ordinal);

        var vid = await probe.RunMediaAsync(new MediaProbeRequest(
            "https://ark.cn-beijing.volces.com/api/v3",
            Ncw.Agent.Llm.Media.ProviderProtocol.VolcengineVideos,
            "doubao-seedance-1-5-pro-251215",
            new ApiKeyCredential(Key),
            "ocean waves"));
        Assert.False(vid.Ok);
        Assert.Equal("https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks", vid.Url);
        Assert.Equal(ProbeFailureKind.Rejected, vid.Failure!.Kind);
        Assert.DoesNotContain(Key, vid.ResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, vid.Failure.Message, StringComparison.Ordinal);
    }
}
