using System.Net;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Pricing;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Tests.Support;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;
using static Ncw.Agent.Llm.Tests.Support.Decoding;

namespace Ncw.Agent.Llm.Tests;

/// <summary>路由与重试(01-llm §5):用假上游端到端验证。假上游像真实 API 一样严格校验每个请求。</summary>
public sealed class RouterTests
{
    private const string AnthropicOk = "anthropic-thinking-tool.sse";

    private static Harness TwoProviders(bool failover = true)
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up").Add("b", WireProtocol.OpenAiChat, 2, "gpt-b-up");
        h.Config.Failover = failover;
        h.Store.Put("provider:a", "sk-a").Put("provider:b", "sk-b");
        return h.Build();
    }

    private static Harness OnlyA(Action<Harness>? setup = null, LlmRouterOptions? options = null)
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up");
        h.Store.Put("provider:a", "sk-a");
        setup?.Invoke(h);
        return h.Build(options);
    }

    private static HttpResponseMessage AnthropicText(string text = "ok") => Reply.Sse(TypedFrames(
        ("message_start", """{"type":"message_start","message":{"model":"claude-a-up","usage":{"input_tokens":1}}}"""),
        ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
        ("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"" + text + "\"}}"),
        ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
        ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}"""),
        ("message_stop", """{"type":"message_stop"}""")));

    private static HttpResponseMessage ChatText(string text = "ok") => Reply.Sse(Frames(
        "{\"model\":\"gpt-b-up\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"" + text + "\"}}]}",
        """{"model":"gpt-b-up","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}""",
        """{"model":"gpt-b-up","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":1}}""",
        "[DONE]"));

    private static HttpResponseMessage ServerError(string message = "boom") => Reply.Status(HttpStatusCode.InternalServerError, message);

    private static StreamError LastError(IEnumerable<LlmEvent> events) => Assert.IsType<StreamError>(events.Last());

    private static T[] Of<T>(IEnumerable<LlmEvent> events) where T : LlmEvent => events.OfType<T>().ToArray();

    // ───────────────────────── 正常路径 ─────────────────────────

    [Fact]
    public async Task One_provider_streams_end_to_end_and_the_message_start_says_who_answered()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => Reply.Sample(AnthropicOk));

        var events = await h.RunAsync(Msg.Request("hi", "SYS", [Msg.Tool()]));

        var start = Assert.IsType<MessageStart>(events[0]);
        Assert.Equal(("a", "anthropic", "claude-a-up"), (start.ProviderId, start.Api, start.UpstreamModel));
        Assert.Equal("claude-sonnet-5-20260101", start.Model); // 回包里的真实模型名仍保留,用于显示
        var end = Assert.IsType<MessageEnd>(events[^1]);
        Assert.Equal(StopReason.ToolUse, end.Stop);
        Assert.NotNull(end.LatencyMs);
        var message = Assemble(events).ToMessage(new MessageId("m"), DateTimeOffset.UnixEpoch);
        Assert.Equal(new Provenance("anthropic", "a", "claude-a-up"), message.Source);
    }

    [Fact]
    public async Task The_request_carries_the_upstream_model_not_the_alias_and_anthropic_credentials_identity_and_caching()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request("hi", "SYS"), workspace: "ws_42");

        var request = Assert.Single(h.Upstream.Requests);
        Assert.Equal("https://a.example.com/v1/messages", request.Url.ToString());
        Assert.Equal("claude-a-up", request.Json.Str("model")); // alias 是 "smart"
        Assert.Equal("sk-a", request.Header("x-api-key"));
        Assert.Equal("2023-06-01", request.Header("anthropic-version"));
        Assert.Equal("text/event-stream", request.Header("accept"));
        Assert.Equal("NextCoWork", request.Header("user-agent"));
        Assert.Equal("ws_42", request.Json.Obj("metadata").Str("user_id"));
        Assert.NotNull(request.Json.Obj("cache_control"));
    }

    [Fact]
    public async Task Chat_and_responses_providers_work_through_the_same_router()
    {
        var h = new Harness();
        h.Config.Add("chat", WireProtocol.OpenAiChat, 1, "gpt-4o", alias: "c", baseUrl: "https://api.example.com/v1")
            .Add("resp", WireProtocol.OpenAiResponses, 1, "gpt-5", alias: "r", baseUrl: "https://api.example.com/v1");
        h.Store.Put("provider:chat", "sk-c").Put("provider:resp", "sk-r");
        h.Build();
        h.Upstream.Enqueue(() => ChatText("hello")).Enqueue(() => Reply.Sample("openai-responses-reasoning-tool.sse"));

        var chat = await h.RunAsync(Msg.Request("hi"), "c");
        var responses = await h.RunAsync(Msg.Request("hi"), "r");

        Assert.Equal("https://api.example.com/v1/chat/completions", h.Upstream.Requests[0].Url.ToString());
        Assert.Equal("Bearer sk-c", h.Upstream.Requests[0].Header("authorization"));
        Assert.Equal("https://api.example.com/v1/responses", h.Upstream.Requests[1].Url.ToString());
        Assert.Equal(StopReason.EndTurn, Assert.IsType<MessageEnd>(chat[^1]).Stop);
        Assert.Equal(StopReason.ToolUse, Assert.IsType<MessageEnd>(responses[^1]).Stop);
    }

    [Fact]
    public async Task A_compatible_provider_that_answers_json_instead_of_sse_is_accepted()
    {
        var h = new Harness();
        h.Config.Add("chat", WireProtocol.OpenAiChat, 1, "m", alias: "c");
        h.Store.Put("provider:chat", "k");
        h.Build();
        h.Upstream.Enqueue(() => Reply.Json(HttpStatusCode.OK,
            """{"model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"whole"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}"""));

        var events = await h.RunAsync(Msg.Request("hi"), "c");

        Assert.Contains(new TextDelta(0, "whole"), events);
        Assert.IsType<MessageEnd>(events[^1]);
    }

    [Fact]
    public async Task Stop_reason_length_arrives_as_max_tokens_with_the_truncated_tool_call_flagged()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => Reply.Sample("anthropic-max-tokens.sse"));

        var events = await h.RunAsync(Msg.Request("write"));

        Assert.Equal(StopReason.MaxTokens, Assert.IsType<MessageEnd>(events[^1]).Stop);
        Assert.NotNull(Assert.Single(Assemble(events).Parts().OfType<ToolCallPart>()).InputError);
    }

    // ───────────────────────── 计价 ─────────────────────────

    private static readonly IPriceBook Prices = new StaticPriceBook([
        new ModelPricing(null, "claude-a-up", Currency.Usd, [new PriceTier(null, new TokenRates(3, 15, 0.3m, 3.75m, 6m))]),
        new ModelPricing(null, "the-response-model-name", Currency.Usd, [new PriceTier(null, new TokenRates(999, 999))]),
    ]);

    [Fact]
    public async Task Message_end_carries_the_cost_of_this_request_priced_by_the_aliass_upstream_model()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up");
        h.Store.Put("provider:a", "k");
        h.Build(prices: Prices);
        h.Upstream.Enqueue(() => Reply.Sample(AnthropicOk));

        var events = await h.RunAsync(Msg.Request("hi"));

        // 412×3 + 87×15 + 3000×0.3 + (1000-400)×3.75 + 400×6;不是回包里的模型名("claude-sonnet-5-20260101")。
        var end = Assert.IsType<MessageEnd>(events[^1]);
        Assert.Equal(CostInfo.Of(412 * 3 + 87 * 15 + 900 + 2250 + 2400, Currency.Usd), end.Cost);
        var attempt = Assert.Single(h.Usage);
        Assert.Equal(end.Cost, attempt.Cost); // 落库那条和聊天页是同一个数
    }

    [Fact]
    public async Task A_model_without_a_price_is_unknown_cost_and_no_price_book_means_not_metered()
    {
        var unpriced = OnlyA().Also(h => h.Upstream.Enqueue(() => AnthropicText()));
        var priced = new Harness();
        priced.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up");
        priced.Store.Put("provider:a", "k");
        priced.Build(prices: new StaticPriceBook([]));
        priced.Upstream.Enqueue(() => AnthropicText());

        var notMetered = Assert.IsType<MessageEnd>((await unpriced.RunAsync(Msg.Request("hi")))[^1]);
        var unknown = Assert.IsType<MessageEnd>((await priced.RunAsync(Msg.Request("hi")))[^1]);

        Assert.Equal(CostState.NotMetered, notMetered.Cost.State);
        Assert.Equal(CostState.Unknown, unknown.Cost.State); // 查不到价:整轮锁定为「未知」
    }

    [Fact]
    public async Task A_price_book_that_throws_costs_the_amount_only_never_the_stream()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up");
        h.Store.Put("provider:a", "k");
        h.Build(prices: new ThrowingPrices());
        h.Upstream.Enqueue(() => AnthropicText());

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(CostState.Unknown, Assert.IsType<MessageEnd>(events[^1]).Cost.State);
        Assert.Contains(events, e => e is TextDelta);
    }

    private sealed class ThrowingPrices : IPriceBook
    {
        public ModelPricing? Find(string? providerId, string modelId, DateTimeOffset at) => throw new InvalidOperationException("price db offline");
    }

    // ───────────────────────── 首字节边界 ─────────────────────────

    [Fact]
    public async Task A_5xx_before_any_content_is_retried_and_the_retry_is_visible()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => ServerError("overloaded")).Enqueue(() => AnthropicText("second try"));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(2, h.Upstream.Requests.Count);
        var retry = Assert.Single(Of<ProviderRetry>(events));
        Assert.Equal((1, "overloaded"), (retry.Attempt, retry.Reason));
        Assert.Equal(4000, retry.DelayMs);
        Assert.IsType<MessageEnd>(events[^1]);
    }

    [Fact]
    public async Task After_content_has_reached_the_consumer_there_is_no_retry_and_no_switch()
    {
        // 已经吐了内容再换供应商重来,会出现重复输出和错位的工具调用:之后的失败是硬错误,由用户决定重发。
        var h = TwoProviders();
        var prefix = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"half an answer"}}"""));
        h.Upstream.Enqueue(() => Reply.SseThenFail(prefix, new IOException("connection reset")));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Single(h.Upstream.Requests); // 没有重试,也没有换到 b
        Assert.Contains(new TextDelta(0, "half an answer"), events); // 已收到的内容保留
        Assert.Empty(Of<ProviderRetry>(events));
        Assert.Empty(Of<ProviderSwitch>(events));
        Assert.Equal(AgentErrorCode.Network, LastError(events).Error.Code);
    }

    [Fact]
    public async Task A_stream_that_dies_after_only_message_start_is_still_retried_because_message_start_is_not_content()
    {
        var h = OnlyA();
        var onlyStart = TypedFrames(("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""));
        h.Upstream.Enqueue(() => Reply.SseThenFail(onlyStart, new IOException("reset"))).Enqueue(() => AnthropicText("retried"));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(2, h.Upstream.Requests.Count);
        Assert.Single(Of<TextDelta>(events)); // 没有重复内容
        Assert.IsType<MessageEnd>(events[^1]);
    }

    [Fact]
    public async Task An_in_stream_error_after_content_ends_the_stream_without_a_retry()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => Reply.Sse(TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}}"""),
            ("error", """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""))));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Single(h.Upstream.Requests);
        Assert.Equal(AgentErrorCode.Provider, LastError(events).Error.Code);
    }

    [Fact]
    public async Task An_empty_decoded_response_is_not_a_success_and_is_retried()
    {
        var h = new Harness();
        h.Config.Add("chat", WireProtocol.OpenAiChat, 1, "m", alias: "c");
        h.Store.Put("provider:chat", "k");
        h.Build();
        h.Upstream.Enqueue(() => Reply.Sse("")).Enqueue(() => ChatText());

        var events = await h.RunAsync(Msg.Request("hi"), "c");

        Assert.Equal(2, h.Upstream.Requests.Count);
        Assert.IsType<MessageEnd>(events[^1]);
    }

    // ───────────────────────── 次数与退避 ─────────────────────────

    [Fact]
    public async Task Provider_errors_are_tried_three_times_with_second_scale_backoff_then_the_next_provider_takes_over()
    {
        var h = TwoProviders();
        h.Upstream.Enqueue(() => ServerError()).Enqueue(() => ServerError()).Enqueue(() => ServerError()).Enqueue(() => ChatText("from b"));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(["a.example.com", "a.example.com", "a.example.com", "b.example.com"], h.Upstream.Requests.Select(r => r.Host));
        Assert.Equal([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], h.Delayer.Delays); // 4s×2ⁿ,不是 500ms 那张表
        var switched = Assert.Single(Of<ProviderSwitch>(events));
        Assert.Equal(("A", "B"), (switched.From, switched.To));
        Assert.Contains(new TextDelta(0, "from b"), events);
    }

    [Fact]
    public async Task Network_errors_get_six_attempts_with_millisecond_backoff()
    {
        var h = OnlyA();
        for (var i = 0; i < 6; i++)
        {
            h.Upstream.Enqueue(_ => throw new HttpRequestException("dns failure"));
        }

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(6, h.Upstream.Requests.Count);
        Assert.Equal([500, 1000, 2000, 4000, 8000], h.Delayer.Delays.Select(d => (int)d.TotalMilliseconds));
        Assert.Equal(AgentErrorCode.Network, LastError(events).Error.Code);
    }

    [Fact]
    public async Task Rate_limits_without_retry_after_back_off_on_the_seconds_scale_and_end_as_rate_limit()
    {
        var h = OnlyA();
        for (var i = 0; i < 3; i++)
        {
            h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "slow down"));
        }

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(3, h.Upstream.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], h.Delayer.Delays);
        Assert.Equal(AgentErrorCode.RateLimit, LastError(events).Error.Code);
    }

    [Fact]
    public async Task Retry_after_is_obeyed_including_zero_which_means_immediately()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "x", ("retry-after", "2")))
            .Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "x", ("retry-after", "0")))
            .Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request("hi"));

        // 上游给了 Retry-After 就只听它的,不套下限:0 是合法的「立刻再来」,不能被抬成 4 秒。
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.Zero], h.Delayer.Delays);
    }

    [Fact]
    public async Task Backoff_is_capped_at_sixty_seconds()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "x", ("retry-after", "3600"))).Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request("hi"));

        Assert.Equal([TimeSpan.FromSeconds(60)], h.Delayer.Delays);
    }

    [Theory]
    [InlineData("5", 5000)]
    [InlineData("0", 0)]
    [InlineData("100000", 60_000)]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    public void Retry_after_parses_seconds_and_http_dates(string raw, int? expected)
    {
        Assert.Equal(expected, LlmRouter.ParseRetryAfter(raw, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Retry_after_accepts_an_http_date()
    {
        var now = new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(30_000, LlmRouter.ParseRetryAfter("Mon, 07 Sep 2026 12:00:30 GMT", now));
        Assert.Equal(0, LlmRouter.ParseRetryAfter("Mon, 07 Sep 2026 11:00:00 GMT", now));
    }

    [Fact]
    public async Task A_non_retryable_error_goes_straight_to_the_next_provider_without_retrying_in_place()
    {
        var h = TwoProviders();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.BadRequest, "invalid field")).Enqueue(() => ChatText());

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(["a.example.com", "b.example.com"], h.Upstream.Requests.Select(r => r.Host));
        Assert.Empty(h.Delayer.Delays);
        Assert.IsType<MessageEnd>(events[^1]);
    }

    [Fact]
    public async Task Cache_unsupported_ends_the_request_without_retrying_or_switching()
    {
        var h = TwoProviders();
        h.Upstream.Enqueue(() => Reply.Json(HttpStatusCode.BadRequest, """{"error":{"type":"invalid_request_error","message":"cache_control is not supported"}}"""));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Single(h.Upstream.Requests); // 配置不匹配,不是供应商不健康
        Assert.Empty(Of<ProviderSwitch>(events));
        Assert.Equal(AgentErrorCode.CacheUnsupported, LastError(events).Error.Code);
        Assert.Equal(LlmErrorKeys.CacheUnsupported, LastError(events).Error.MessageKey);
    }

    [Fact]
    public async Task An_auth_error_is_reported_in_preference_to_a_later_network_error()
    {
        // auth 有明确的行动(去设置页填密钥),而一个 network 错误只会让用户干瞪眼重试。
        var h = TwoProviders();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.Unauthorized, "bad key"))
            .Enqueue(() => ServerError()).Enqueue(() => ServerError()).Enqueue(() => ServerError());

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(AgentErrorCode.Auth, LastError(events).Error.Code);
        Assert.True(LastError(events).Error.IsFatal);
    }

    [Fact]
    public async Task A_provider_without_a_credential_is_skipped_without_sending_anything()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up").Add("b", WireProtocol.OpenAiChat, 2, "gpt-b-up");
        h.Store.Put("provider:b", "sk-b"); // a 没配密钥
        h.Build();
        h.Upstream.Enqueue(() => ChatText());

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(["b.example.com"], h.Upstream.Requests.Select(r => r.Host));
        Assert.Equal("A", Assert.Single(Of<ProviderSwitch>(events)).From);
        Assert.IsType<MessageEnd>(events[^1]);
    }

    [Fact]
    public async Task When_every_candidate_fails_the_last_error_is_given()
    {
        var h = TwoProviders();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.BadRequest, "a is broken")).Enqueue(() => Reply.Status(HttpStatusCode.BadRequest, "b is broken"));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal("b is broken", LastError(events).Error.Message);
    }

    // ───────────────────────── 候选集 ─────────────────────────

    [Fact]
    public async Task Candidates_are_ordered_by_provider_priority_not_by_insertion_order()
    {
        var h = new Harness();
        h.Config.Add("late", WireProtocol.OpenAiChat, 9, "m").Add("early", WireProtocol.OpenAiChat, 1, "m");
        h.Store.Put("provider:late", "k").Put("provider:early", "k");
        h.Build();
        h.Upstream.Enqueue(() => ChatText());

        await h.RunAsync(Msg.Request("hi"));

        Assert.Equal("early.example.com", Assert.Single(h.Upstream.Requests).Host);
    }

    [Fact]
    public async Task A_pinned_provider_is_a_hard_constraint_it_never_falls_back_or_emits_a_switch()
    {
        var h = TwoProviders();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.BadRequest, "b rejects"));

        var events = await h.RunAsync(Msg.Request("hi"), providerId: "b");

        Assert.Equal(["b.example.com"], h.Upstream.Requests.Select(r => r.Host));
        Assert.Empty(Of<ProviderSwitch>(events));
        Assert.Equal("b rejects", LastError(events).Error.Message);
    }

    [Fact]
    public async Task A_pinned_provider_that_was_deleted_or_disabled_or_lost_the_model_gets_an_explicit_no_fallback_error()
    {
        var h = TwoProviders();

        var missing = LastError(await h.RunAsync(Msg.Request("hi"), providerId: "gone"));
        h.Config.ProviderList[1] = h.Config.ProviderList[1] with { Enabled = false };
        var disabled = LastError(await h.RunAsync(Msg.Request("hi"), providerId: "b"));
        h.Config.BindingList.RemoveAll(b => b.ProviderId == "a");
        var lostModel = LastError(await h.RunAsync(Msg.Request("hi"), providerId: "a"));

        Assert.Equal(LlmErrorKeys.PinnedProviderMissing, missing.Error.MessageKey);
        Assert.Equal(LlmErrorKeys.PinnedProviderDisabled, disabled.Error.MessageKey);
        Assert.Equal(LlmErrorKeys.PinnedModelMissing, lostModel.Error.MessageKey);
        Assert.All([missing, disabled, lostModel], e => Assert.Equal(AgentErrorCode.NoHealthyProvider, e.Error.Code));
        Assert.Empty(h.Upstream.Requests);
    }

    [Fact]
    public async Task An_unknown_alias_or_an_alias_with_no_enabled_provider_ends_the_run_without_any_http()
    {
        var h = TwoProviders();

        var unknown = LastError(await h.RunAsync(Msg.Request("hi"), "nope"));
        for (var i = 0; i < h.Config.ProviderList.Count; i++)
        {
            h.Config.ProviderList[i] = h.Config.ProviderList[i] with { Enabled = false };
        }

        var noneEnabled = LastError(await h.RunAsync(Msg.Request("hi")));

        Assert.Equal(LlmErrorKeys.UnknownAlias, unknown.Error.MessageKey);
        Assert.Equal(LlmErrorKeys.NoEnabledProvider, noneEnabled.Error.MessageKey);
        Assert.True(unknown.Error.IsFatal);
        Assert.Empty(h.Upstream.Requests);
    }

    [Fact]
    public async Task With_failover_off_only_the_highest_priority_provider_is_tried()
    {
        var h = TwoProviders(failover: false);
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.BadRequest, "a rejects"));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(["a.example.com"], h.Upstream.Requests.Select(r => r.Host));
        Assert.Empty(Of<ProviderSwitch>(events));
    }

    [Fact]
    public void Resolve_binding_answers_which_binding_a_selection_points_at()
    {
        var h = TwoProviders();

        Assert.Equal("claude-a-up", h.Router.ResolveBinding(ModelRef.Unpinned("smart"))!.UpstreamModel);
        Assert.Equal("gpt-b-up", h.Router.ResolveBinding(new ModelRef("b", "smart"))!.UpstreamModel);
        Assert.Null(h.Router.ResolveBinding(new ModelRef("zzz", "smart")));
    }

    // ───────────────────────── 健康度 ─────────────────────────

    [Fact]
    public async Task Three_consecutive_failures_cool_a_provider_down_for_thirty_seconds_then_it_returns()
    {
        var h = TwoProviders();
        h.Upstream.Enqueue(() => ServerError()).Enqueue(() => ServerError()).Enqueue(() => ServerError()).Enqueue(() => ChatText());

        await h.RunAsync(Msg.Request("hi"));
        var health = Assert.Single(h.Router.Health(), x => x.ProviderId == "a");
        Assert.False(health.Healthy);
        Assert.Equal(3, health.ConsecutiveFailures);
        Assert.NotNull(health.CooldownUntil);

        // 冷却期内:a 被排除,直接走 b。
        var before = h.Upstream.Requests.Count;
        h.Upstream.Enqueue(() => ChatText());
        await h.RunAsync(Msg.Request("again"));
        Assert.All(h.Upstream.Requests.Skip(before).Select(r => r.Host), host => Assert.Equal("b.example.com", host));

        // 冷却到期:a 重新纳入候选,成功后健康度恢复。
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        h.Upstream.Enqueue(() => AnthropicText());
        await h.RunAsync(Msg.Request("later"));
        Assert.Equal("a.example.com", h.Upstream.Requests[^1].Host);
        var recovered = Assert.Single(h.Router.Health(), x => x.ProviderId == "a");
        Assert.True(recovered.Healthy);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.CooldownUntil);
    }

    [Fact]
    public async Task When_every_provider_is_cooling_the_error_says_so_and_is_retryable()
    {
        var h = OnlyA();
        for (var i = 0; i < 3; i++)
        {
            h.Upstream.Enqueue(() => ServerError());
        }

        await h.RunAsync(Msg.Request("hi"));
        var events = await h.RunAsync(Msg.Request("again"));

        var error = LastError(events).Error;
        Assert.Equal(LlmErrorKeys.AllProvidersCooling, error.MessageKey);
        Assert.True(error.Retryable);
        Assert.Equal(3, h.Upstream.Requests.Count);
    }

    [Fact]
    public async Task A_pinned_provider_is_never_filtered_out_by_cooldown()
    {
        var h = OnlyA();
        for (var i = 0; i < 3; i++)
        {
            h.Upstream.Enqueue(() => ServerError());
        }

        await h.RunAsync(Msg.Request("hi"));
        h.Upstream.Enqueue(() => AnthropicText());
        var events = await h.RunAsync(Msg.Request("again"), providerId: "a");

        Assert.IsType<MessageEnd>(events[^1]); // 锁死之后无处可绕,过滤掉唯一那条只会让用户收到「都在冷却中」
    }

    // ───────────────────────── 进程级限流闸门 ─────────────────────────

    [Fact]
    public async Task A_stream_that_hits_a_429_makes_other_streams_wait_before_sending_and_the_wait_costs_no_attempt()
    {
        var h = OnlyA(options: new LlmRouterOptions { UnhealthyAfter = 99 }); // 不让健康度冷却抢在闸门前面
        for (var i = 0; i < 3; i++)
        {
            h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "quota window", ("retry-after", "30")));
        }

        await h.RunAsync(Msg.Request("first stream"));
        h.Upstream.Enqueue(() => AnthropicText());
        var delaysBefore = h.Delayer.Delays.Count;

        var second = await h.RunAsync(Msg.Request("second stream"));

        var wait = Assert.Single(Of<ProviderRetry>(second));
        Assert.Equal((1, 30_000, "quota window"), (wait.Attempt, wait.DelayMs, wait.Reason)); // 复用 ProviderRetry,等的原因是上游限流
        Assert.Equal(TimeSpan.FromSeconds(30), h.Delayer.Delays[delaysBefore]);
        Assert.Equal(4, h.Upstream.Requests.Count);
        Assert.IsType<MessageEnd>(second[^1]); // 等待不消耗尝试次数:这条流还有完整的 3 次机会
    }

    [Fact]
    public async Task Resetting_health_clears_both_the_cooldown_and_the_rate_limit_gate()
    {
        var h = OnlyA(options: new LlmRouterOptions { UnhealthyAfter = 1 });
        for (var i = 0; i < 3; i++)
        {
            h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "x", ("retry-after", "30")));
        }

        await h.RunAsync(Msg.Request("one"));
        Assert.Equal(LlmErrorKeys.AllProvidersCooling, LastError(await h.RunAsync(Msg.Request("blocked"))).Error.MessageKey);

        h.Router.ResetHealth("a");
        h.Upstream.Enqueue(() => AnthropicText());
        var events = await h.RunAsync(Msg.Request("fresh"));

        Assert.Empty(Of<ProviderRetry>(events)); // 用户点「重置健康状态」就是「当它没挂过,现在就重试」:不该安静地睡满剩下的退避
        Assert.IsType<MessageEnd>(events[^1]);
    }

    [Fact]
    public async Task A_success_clears_the_gate_so_later_streams_do_not_wait_for_nothing()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "x", ("retry-after", "10"))).Enqueue(() => AnthropicText());
        await h.RunAsync(Msg.Request("one"));
        var delaysAfterFirst = h.Delayer.Delays.Count;

        h.Upstream.Enqueue(() => AnthropicText());
        await h.RunAsync(Msg.Request("two"));

        Assert.Equal(delaysAfterFirst, h.Delayer.Delays.Count); // 成功 = 配额回来了,后面的流不必再白等
    }

    // ───────────────────────── 空闲超时 ─────────────────────────

    [Fact]
    public async Task A_stalled_stream_times_out_with_a_timeout_error_that_is_not_retried()
    {
        var h = OnlyA(options: new LlmRouterOptions { IdleTimeout = () => TimeSpan.FromMilliseconds(250) });
        var onlyStart = TypedFrames(("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""));
        h.Upstream.Enqueue(() => Reply.SseThenStall(onlyStart));

        var events = await h.RunAsync(Msg.Request("hi"));

        var error = LastError(events).Error;
        Assert.Equal(LlmErrorKeys.UpstreamTimeout, error.MessageKey);
        Assert.Equal(AgentErrorCode.Network, error.Code);
        Assert.False(error.Retryable);
        Assert.Single(h.Upstream.Requests);
    }

    [Fact]
    public void The_idle_timeout_defaults_to_600_seconds_and_the_setting_is_clamped_to_60_through_3600()
    {
        Assert.Equal(TimeSpan.FromSeconds(600), new LlmRouterOptions().IdleTimeout());
        var setting = 5;
        var timeout = LlmRouterOptions.IdleTimeoutFromSettings(() => setting);

        Assert.Equal(TimeSpan.FromSeconds(60), timeout());
        setting = 900;
        Assert.Equal(TimeSpan.FromSeconds(900), timeout()); // 每次请求时重新求值:改完立刻生效
        setting = 999_999;
        Assert.Equal(TimeSpan.FromSeconds(3600), timeout());
    }

    [Fact]
    public async Task Progress_resets_the_idle_deadline_so_there_is_no_total_run_limit()
    {
        var h = OnlyA(options: new LlmRouterOptions { IdleTimeout = () => TimeSpan.FromMilliseconds(1000) });
        var chunks = new List<string>
        {
            TypedFrames(("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
                ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""")),
        };
        for (var i = 0; i < 6; i++)
        {
            chunks.Add(TypedFrames(("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"" + i + "\"}}")));
        }

        chunks.Add(TypedFrames(
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":6}}"""),
            ("message_stop", """{"type":"message_stop"}""")));
        h.Upstream.Enqueue(() => Reply.SsePaced(chunks, TimeSpan.FromMilliseconds(250))); // 总长 ~2s,远超 1s,但每个间隔都没超(余量 4 倍,扛得住慢 CI 的定时器抖动)

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.IsType<MessageEnd>(events[^1]);
        Assert.Equal("012345", string.Concat(Of<TextDelta>(events).Select(t => t.Text)));
    }

    // ───────────────────────── 401 与 OAuth ─────────────────────────

    private static OAuthProviderSpec Spec() => new(
        "chatgpt", "https://auth.example.com/token", "cid",
        (json, now) => new OAuthIdentity(json.Str("access_token")!, json.Str("refresh_token"), now + 3_600_000, "acct"));

    private static string OauthJson(long expiresAt, string access = "A1") =>
        CredentialFormat.Serialize(new OAuthCredential("chatgpt", access, "R1", expiresAt, "acct"));

    private static Harness OauthProvider(Action<Harness>? setup = null)
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up");
        h.Store.Put("provider:a", OauthJson(h.Clock.Ms + 3_600_000));
        setup?.Invoke(h);
        return h.Build(oauth: new OAuthRegistry([Spec()]));
    }

    [Fact]
    public async Task A_401_on_an_oauth_credential_forces_exactly_one_refresh_and_resends_with_the_new_token()
    {
        var h = OauthProvider();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.Unauthorized, "token revoked"))
            .Enqueue(() => Reply.Json(HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2"}"""))
            .Enqueue(() => AnthropicText());

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(3, h.Upstream.Requests.Count);
        Assert.Equal("A1", h.Upstream.Requests[0].Header("x-api-key"));
        Assert.Equal("https://auth.example.com/token", h.Upstream.Requests[1].Url.ToString());
        Assert.Equal("A2", h.Upstream.Requests[2].Header("x-api-key"));
        Assert.Equal("A2", ((OAuthCredential)CredentialFormat.Parse(h.Store.Peek("provider:a"))!).AccessToken); // 新凭据先写回
        Assert.IsType<MessageEnd>(events[^1]);
        Assert.Empty(Of<ProviderRetry>(events)); // 不交给外层重试循环
    }

    [Fact]
    public async Task A_second_401_after_the_refresh_is_an_auth_error_and_there_is_no_refresh_loop()
    {
        var h = OauthProvider();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.Unauthorized, "no"))
            .Enqueue(() => Reply.Json(HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2"}"""))
            .Enqueue(() => Reply.Status(HttpStatusCode.Unauthorized, "still no"));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(3, h.Upstream.Requests.Count);
        Assert.Equal(AgentErrorCode.Auth, LastError(events).Error.Code);
    }

    [Fact]
    public async Task A_401_on_a_plain_api_key_is_never_refreshed()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.Unauthorized, "bad key"));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Single(h.Upstream.Requests);
        Assert.Equal(AgentErrorCode.Auth, LastError(events).Error.Code);
    }

    [Fact]
    public async Task An_oauth_credential_close_to_expiry_is_refreshed_before_the_request_is_sent()
    {
        var h = OauthProvider(x => x.Store.Put("provider:a", OauthJson(x.Clock.Ms + 10_000)));
        h.Upstream.Enqueue(() => Reply.Json(HttpStatusCode.OK, """{"access_token":"A9","refresh_token":"R9"}""")).Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request("hi"));

        Assert.Equal("https://auth.example.com/token", h.Upstream.Requests[0].Url.ToString());
        Assert.Equal("A9", h.Upstream.Requests[1].Header("x-api-key"));
    }

    // ───────────────────────── 签名凭据 ─────────────────────────

    private sealed class CountingSigner : IRequestSigner
    {
        public List<string> Paths { get; } = [];

        public SignedRequest Sign(string method, string path)
        {
            Paths.Add(path);
            return new SignedRequest(
                new Dictionary<string, string> { ["x-signature"] = $"sig-{Paths.Count}" },
                new Dictionary<string, string> { ["ts"] = $"t{Paths.Count}" });
        }
    }

    private sealed class SigningTransport(CountingSigner signer) : ITransportDecorator
    {
        public TransportDecoration Decorate(ProviderConfig provider, WireProtocol protocol, ProviderCredential credential, RequestContext context) => new(Signer: signer);
    }

    [Fact]
    public async Task A_signed_request_is_signed_on_the_final_url_path_with_no_credential_in_any_header_or_query()
    {
        var signer = new CountingSigner();
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up", baseUrl: "https://a.example.com/gateway");
        h.Store.Put("provider:a", "-----BEGIN PRIVATE KEY-----SECRETPEM");
        h.Build(transport: new SigningTransport(signer));
        h.Upstream.Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request("hi"));

        var request = Assert.Single(h.Upstream.Requests);
        Assert.Equal(["/gateway/v1/messages"], signer.Paths); // 最终 URL 的 pathname,不是编码器的相对路径
        Assert.Equal("sig-1", request.Header("x-signature"));
        Assert.Equal("?ts=t1", request.Url.Query);
        Assert.Null(request.Header("x-api-key"));
        Assert.Null(request.Header("authorization"));
        Assert.DoesNotContain("SECRETPEM", string.Join("|", request.Headers.Values) + request.Url);
    }

    [Fact]
    public async Task Every_send_is_signed_afresh_including_the_resend_after_a_401()
    {
        var signer = new CountingSigner();
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up");
        h.Store.Put("provider:a", OauthJson(h.Clock.Ms + 3_600_000));
        h.Build(transport: new SigningTransport(signer), oauth: new OAuthRegistry([Spec()]));
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.Unauthorized, "expired signature"))
            .Enqueue(() => Reply.Json(HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2"}"""))
            .Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(2, signer.Paths.Count);
        Assert.Equal("sig-1", h.Upstream.Requests[0].Header("x-signature"));
        Assert.Equal("sig-2", h.Upstream.Requests[2].Header("x-signature")); // 时间戳式签名复用旧值等于没重试
        Assert.Null(h.Upstream.Requests[2].Header("x-api-key"));
    }

    [Fact]
    public async Task A_signature_credential_without_a_signer_is_an_auth_error_and_nothing_is_sent()
    {
        var h = OnlyA(x => x.Store.Put("provider:a", CredentialFormat.Serialize(new SignatureCredential(SignatureScheme.AwsSigV4, "AKIA", "SECRET"))));

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Empty(h.Upstream.Requests);
        Assert.Equal(AgentErrorCode.Auth, LastError(events).Error.Code);
    }

    private sealed class BearerAuthTransport : ITransportDecorator
    {
        public TransportDecoration Decorate(ProviderConfig provider, WireProtocol protocol, ProviderCredential credential, RequestContext context) =>
            credential is ApiKeyCredential key
                ? new TransportDecoration(
                    Headers: new Dictionary<string, string> { ["authorization"] = "Bearer " + key.ApiKey, ["user-agent"] = "CustomAgent/1.0" },
                    DropHeaders: ["x-api-key"])
                : new TransportDecoration();
    }

    [Fact]
    public async Task Transport_decoration_replaces_existing_headers_without_duplicating_them_and_drops_x_api_key()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up", alias: "claude")
            .Add("b", WireProtocol.OpenAiChat, 1, "gpt-b-up", alias: "gpt");
        h.Store.Put("provider:a", "jwt-a").Put("provider:b", "jwt-b");
        h.Build(transport: new BearerAuthTransport());
        h.Upstream.Enqueue(() => AnthropicText()).Enqueue(() => ChatText());

        await h.RunAsync(Msg.Request("hi"), "claude");
        await h.RunAsync(Msg.Request("hi"), "gpt");

        var anthropic = h.Upstream.Requests[0];
        Assert.Equal("Bearer jwt-a", anthropic.Header("authorization"));
        Assert.Null(anthropic.Header("x-api-key"));
        Assert.Equal("CustomAgent/1.0", anthropic.Header("user-agent"));

        var openai = h.Upstream.Requests[1];
        Assert.Equal("Bearer jwt-b", openai.Header("authorization"));
        Assert.Null(openai.Header("x-api-key"));
        Assert.Equal("CustomAgent/1.0", openai.Header("user-agent"));
    }

    // ───────────────────────── 账户池 ─────────────────────────

    private static (Harness H, FakePort Port) WithAccounts(int accounts = 2)
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up");
        h.Store.Put("provider:a", "legacy-slot-key");
        var port = new FakePort { Clock = () => h.Clock.Ms };
        for (var i = 1; i <= accounts; i++)
        {
            port.Accounts.Add(new ProviderAccount($"acc{i}", "a", "chatgpt", i));
            h.Store.Put($"provider:a#acc{i}", $"key-{i}");
        }

        h.Build(accounts: new AccountPool(port));
        return (h, port);
    }

    [Fact]
    public async Task A_rate_limited_account_hands_over_to_the_next_one_without_spending_a_retry_or_sleeping()
    {
        var (h, port) = WithAccounts();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "account 1 quota", ("retry-after", "30"))).Enqueue(() => AnthropicText());

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(["key-1", "key-2"], h.Upstream.Requests.Select(r => r.Header("x-api-key")));
        Assert.Empty(h.Delayer.Delays);
        Assert.Empty(Of<ProviderRetry>(events));
        Assert.NotNull(port.Accounts[0].Limit); // 落闸(落库)
        Assert.IsType<MessageEnd>(events[^1]);
    }

    [Fact]
    public async Task Three_accounts_can_all_be_reached_even_though_only_three_attempts_are_allowed()
    {
        var (h, _) = WithAccounts(3);
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "q1", ("retry-after", "60")))
            .Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "q2", ("retry-after", "60")))
            .Enqueue(() => AnthropicText());

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(["key-1", "key-2", "key-3"], h.Upstream.Requests.Select(r => r.Header("x-api-key")));
        Assert.IsType<MessageEnd>(events[^1]);
    }

    [Fact]
    public async Task When_every_account_is_limited_the_error_gives_the_earliest_recovery_and_never_uses_the_legacy_credential_slot()
    {
        var (h, port) = WithAccounts();
        var recovery = h.Clock.Ms + 90_000;
        for (var i = 0; i < port.Accounts.Count; i++)
        {
            port.Accounts[i] = port.Accounts[i] with { Limit = new AccountLimit(recovery + i * 1000, h.Clock.Ms, AccountLimitSource.Http429, "x") };
        }

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Empty(h.Upstream.Requests); // 绝不退回旧槽:那里装的是当前账号的镜像,用它发请求等于绕过刚立起来的闸门
        var error = LastError(events).Error;
        Assert.Equal(AgentErrorCode.RateLimit, error.Code);
        Assert.Equal(LlmErrorKeys.AllAccountsRateLimited, error.MessageKey);
        Assert.Equal(recovery.ToString(), error.MessageParams!["recoveryAt"]);
        Assert.Equal(90_000, error.RetryAfterMs);
        Assert.True(error.Retryable);
    }

    [Fact]
    public async Task When_all_accounts_are_disabled_or_need_a_new_login_the_error_is_not_retryable()
    {
        var (h, port) = WithAccounts();
        port.Accounts[0] = port.Accounts[0] with { Enabled = false };
        port.Accounts[1] = port.Accounts[1] with { NeedsReauth = true };

        var error = LastError(await h.RunAsync(Msg.Request("hi"))).Error;

        Assert.Equal(LlmErrorKeys.AllAccountsUnusable, error.MessageKey);
        Assert.False(error.Retryable);
    }

    [Fact]
    public async Task With_rotation_off_a_429_neither_switches_accounts_nor_loops_forever()
    {
        // 「换号不消耗重试次数」+「轮换关掉时 Select 恒返回当前账号」曾经是一个死循环(Run 卡住、CPU 打满、一条错误都不输出)。
        var (h, port) = WithAccounts();
        port.Rotation = false;
        for (var i = 0; i < 3; i++)
        {
            h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.TooManyRequests, "q", ("retry-after", "1")));
        }

        var events = await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(3, h.Upstream.Requests.Count);
        Assert.All(h.Upstream.Requests, r => Assert.Equal("key-1", r.Header("x-api-key")));
        Assert.Equal(AgentErrorCode.RateLimit, LastError(events).Error.Code);
    }

    [Fact]
    public async Task Account_switching_is_bounded_at_eight_so_a_misbehaving_pool_cannot_spin_forever()
    {
        var (h, _) = WithAccounts(20);
        h.Upstream.Fallback = _ => Task.FromResult(Reply.Status(HttpStatusCode.TooManyRequests, "everything is limited", ("retry-after", "60")));

        var events = await h.RunAsync(Msg.Request("hi"));

        // 8 次换号不消耗尝试次数,之后仍按「每个供应商最多 3 次」收尾:8 + 3 = 11 个请求。
        Assert.Equal(11, h.Upstream.Requests.Count);
        Assert.Equal(AgentErrorCode.RateLimit, LastError(events).Error.Code);
    }

    [Fact]
    public async Task A_success_with_an_account_clears_its_rate_limit()
    {
        var (h, port) = WithAccounts(1);
        port.Accounts[0] = port.Accounts[0] with { Limit = new AccountLimit(h.Clock.Ms - 1, 0, AccountLimitSource.Http429, "expired") };
        h.Upstream.Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request("hi"));

        Assert.Null(port.Accounts[0].Limit);
    }

    // ───────────────────────── 用量 ─────────────────────────

    [Fact]
    public async Task Every_http_attempt_gets_its_own_usage_record_including_failed_ones()
    {
        var h = TwoProviders();
        h.Upstream.Enqueue(() => ServerError("flaky")).Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request("hi"));

        Assert.Equal(2, h.Usage.Count);
        var failed = h.Usage[0];
        Assert.False(failed.Ok);
        Assert.Equal((1, "a", 500, AgentErrorCode.Provider), (failed.Attempt, failed.ProviderId, failed.HttpStatus, failed.ErrorKind));
        Assert.Equal("flaky", failed.ErrorMessage);
        var ok = h.Usage[1];
        Assert.True(ok.Ok);
        Assert.Equal((2, "smart", "claude-a-up"), (ok.Attempt, ok.Alias, ok.UpstreamModel));
        Assert.Equal(StopReason.EndTurn, ok.Stop);
        Assert.Equal(new TokenUsage(1, 2), ok.Usage);
        Assert.Equal(failed.RunId, ok.RunId); // 同一次逻辑请求内稳定
        Assert.Equal("ws_test", ok.WorkspaceId);
        // 时钟和 ID 都是注入的:假上游测试里可以逐字断言。
        Assert.Equal("run_id0001", failed.RunId);
        Assert.Equal(["id0002", "id0003"], h.Usage.Select(u => u.Id));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(h.Clock.Ms), ok.At);
    }

    [Fact]
    public async Task A_usage_sink_that_throws_never_fails_the_request()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up");
        h.Store.Put("provider:a", "k");
        h.Build(usage: new ThrowingUsage());
        h.Upstream.Enqueue(() => AnthropicText());

        Assert.IsType<MessageEnd>((await h.RunAsync(Msg.Request("hi")))[^1]);
    }

    private sealed class ThrowingUsage : IUsageSink
    {
        public void OnAttempt(UsageAttempt attempt) => throw new InvalidOperationException("db locked");
    }

    // ───────────────────────── 取消与永不抛异常 ─────────────────────────

    [Fact]
    public async Task Cancellation_ends_the_stream_with_an_aborted_event_instead_of_throwing()
    {
        var h = OnlyA();
        var prefix = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"typing"}}"""));
        h.Upstream.Enqueue(() => Reply.SseThenStall(prefix));
        using var cts = new CancellationTokenSource();
        var events = new List<LlmEvent>();

        await foreach (var ev in h.Router.StreamAsync(ModelRef.Unpinned("smart"), Msg.Request("hi"), new RequestContext("ws"), cts.Token))
        {
            events.Add(ev);
            if (ev is TextDelta)
            {
                await cts.CancelAsync();
            }
        }

        var error = LastError(events).Error;
        Assert.Equal(AgentErrorCode.Aborted, error.Code);
        Assert.False(error.Retryable);
        Assert.Contains(new TextDelta(0, "typing"), events); // 已收到的内容保留
        Assert.Single(h.Upstream.Requests); // 不重试
        Assert.Equal(StopReason.Aborted, Assemble(events).ToMessage(new MessageId("m"), DateTimeOffset.UnixEpoch).Stop);
    }

    [Fact]
    public async Task A_consumer_that_stops_early_releases_the_upstream_connection()
    {
        var h = OnlyA();
        var prefix = TypedFrames(
            ("message_start", """{"type":"message_start","message":{"model":"m","usage":{"input_tokens":1}}}"""),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"x"}}"""));
        h.Upstream.Enqueue(() => Reply.SseThenStall(prefix));

        await foreach (var ev in h.Router.StreamAsync(ModelRef.Unpinned("smart"), Msg.Request("hi"), new RequestContext("ws")))
        {
            if (ev is TextDelta)
            {
                break;
            }
        }

        // 走到这里没有挂起,就说明提前退出时生产者已经收尾(上游流被取消,不会留着一个吊着的连接)。
        Assert.Single(h.Upstream.Requests);
    }

    // ───────────────────────── 本地校验与请求管线 ─────────────────────────

    [Fact]
    public async Task An_invalid_tool_name_is_rejected_before_anything_is_sent()
    {
        var h = OnlyA();

        var error = LastError(await h.RunAsync(Msg.Request("hi", tools: [Msg.Tool("mcp.server:tool")]))).Error;

        Assert.Equal(LlmErrorKeys.InvalidToolName, error.MessageKey);
        Assert.Equal("mcp.server:tool", error.MessageParams!["name"]);
        Assert.Empty(h.Upstream.Requests);
    }

    [Fact]
    public async Task The_workspace_id_must_be_1_to_512_characters_counted_in_code_points()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => AnthropicText());

        var empty = LastError(await h.RunAsync(Msg.Request("hi"), workspace: " "));
        var tooLong = LastError(await h.RunAsync(Msg.Request("hi"), workspace: new string('w', 513)));
        var astral = await h.RunAsync(Msg.Request("hi"), workspace: string.Concat(Enumerable.Repeat("🙂", 512))); // 1024 个 UTF-16 单元,但只有 512 个字符

        Assert.Equal(LlmErrorKeys.InvalidWorkspaceId, empty.Error.MessageKey);
        Assert.Equal(LlmErrorKeys.InvalidWorkspaceId, tooLong.Error.MessageKey);
        Assert.IsType<MessageEnd>(astral[^1]);
        Assert.Single(h.Upstream.Requests);
    }

    [Fact]
    public async Task An_invalid_request_patch_fails_before_any_http_and_is_not_retried()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up", configure: b =>
            b.With(x => x with { Patches = [new PatchRule(PatchOp.Replace, "/model", Jsn.Parse("\"evil\""))] }));
        h.Store.Put("provider:a", "k");
        h.Build();

        var error = LastError(await h.RunAsync(Msg.Request("hi"))).Error;

        Assert.Equal(LlmErrorKeys.InvalidRequestPatch, error.MessageKey);
        Assert.False(error.Retryable);
        Assert.Empty(h.Upstream.Requests);
    }

    [Fact]
    public async Task A_model_patch_cannot_override_the_anthropic_identity_or_remove_caching()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up", ttl: AnthropicCacheTtl.OneHour, configure: b => b.With(x => x with
        {
            Patches =
            [
                new PatchRule(PatchOp.Replace, "/metadata", Jsn.Parse("""{"user_id":"someone-else","relay":"x"}""")),
                new PatchRule(PatchOp.Remove, "/cache_control"),
                new PatchRule(PatchOp.Add, "/temperature", Jsn.Parse("0.25")),
            ],
        }));
        h.Store.Put("provider:a", "k");
        h.Build();
        h.Upstream.Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request("hi", "SYS"), workspace: "ws_real");

        var body = Assert.Single(h.Upstream.Requests).Json;
        Assert.Equal("ws_real", body.Obj("metadata").Str("user_id"));
        Assert.Equal("x", body.Obj("metadata").Str("relay")); // 无害的中转专用键保留
        Assert.Equal("1h", body.Obj("cache_control").Str("ttl")); // TTL 以供应商为准
        Assert.Equal("1h", body.Arr("system")[0].Obj("cache_control").Str("ttl"));
        Assert.Equal(0.25, body.Num("temperature")); // 普通参数的补丁照常生效
    }

    [Fact]
    public async Task Thinking_level_is_adapted_per_provider_binding_and_an_explicit_off_is_sent()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up", configure: b => b.With(x => x with { Thinking = new ThinkingConfig(ThinkingMode.Budget) }));
        h.Store.Put("provider:a", "k");
        h.Build();
        h.Upstream.Enqueue(() => AnthropicText()).Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request([Msg.User("hi")], thinking: ThinkingLevel.High, maxOutput: 32_000));
        await h.RunAsync(Msg.Request([Msg.User("hi")], thinking: ThinkingLevel.Off, maxOutput: 32_000));

        Assert.Equal(21_333, h.Upstream.Requests[0].Json.Obj("thinking").Count("budget_tokens"));
        Assert.Equal("disabled", h.Upstream.Requests[1].Json.Obj("thinking").Str("type")); // 显式关闭必须发出关闭参数
    }

    [Fact]
    public async Task Claude_5_5_gets_adaptive_thinking_even_when_the_catalog_declared_a_budget()
    {
        // issue #202:目录对一手 Anthropic 推理模型推出 Budget 声明,Opus 5.5 收到 budget_tokens / disabled 都会 400。
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-opus-5-5", configure: b => b.With(x => x with { Thinking = new ThinkingConfig(ThinkingMode.Budget, DefaultEnabled: false) }));
        h.Store.Put("provider:a", "k");
        h.Build();
        h.Upstream.Enqueue(() => AnthropicText()).Enqueue(() => AnthropicText()).Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request([Msg.User("hi")], thinking: ThinkingLevel.High, maxOutput: 32_000));
        await h.RunAsync(Msg.Request([Msg.User("hi")], thinking: ThinkingLevel.Auto, maxOutput: 32_000));
        await h.RunAsync(Msg.Request([Msg.User("hi")], thinking: ThinkingLevel.Off, maxOutput: 32_000));

        var high = h.Upstream.Requests[0].Json;
        Assert.Equal("adaptive", high.Obj("thinking").Str("type"));
        Assert.Equal("high", high.Obj("output_config").Str("effort"));
        Assert.False(h.Upstream.Requests[1].Json.TryGetProperty("thinking", out _)); // Auto + 目录默认关:什么都不发,用模型默认
        Assert.False(h.Upstream.Requests[2].Json.TryGetProperty("thinking", out _)); // 关不掉:不发 disabled
        Assert.Equal("low", h.Upstream.Requests[2].Json.Obj("output_config").Str("effort"));
    }

    [Fact]
    public async Task A_legacy_binding_without_a_thinking_declaration_still_honours_an_explicit_off_on_anthropic()
    {
        var h = OnlyA();
        h.Upstream.Enqueue(() => AnthropicText());

        await h.RunAsync(Msg.Request("hi", thinking: ThinkingLevel.Off));

        Assert.Equal("disabled", Assert.Single(h.Upstream.Requests).Json.Obj("thinking").Str("type"));
    }

    [Fact]
    public async Task A_model_that_cannot_turn_thinking_off_fails_locally_without_a_request()
    {
        var h = new Harness();
        h.Config.Add("c", WireProtocol.OpenAiChat, 1, "o-model", configure: b => b.With(x => x with
        {
            Thinking = new ThinkingConfig(ThinkingMode.Effort),
            ReasoningEfforts = [ReasoningEffort.Low, ReasoningEffort.High],
        }));
        h.Store.Put("provider:c", "k");
        h.Build();

        var error = LastError(await h.RunAsync(Msg.Request("hi", thinking: ThinkingLevel.Off))).Error;

        Assert.Equal(LlmErrorKeys.ThinkingUnsupported, error.MessageKey);
        Assert.Equal("thinking.cannotDisable", error.MessageParams!["code"]);
        Assert.Empty(h.Upstream.Requests);
    }

    // ───────────────────────── 图片引用 ─────────────────────────

    private sealed class FakeImages : IImageResolver
    {
        public ValueTask<string> ResolveAsync(string dataRef, string mime, RequestContext context, CancellationToken ct) =>
            dataRef.Contains("missing", StringComparison.Ordinal)
                ? throw new ImageInputException(AgentError.Create(AgentErrorCode.Provider, "attachment missing", false, messageKey: LlmErrorKeys.ImageInput))
                : ValueTask.FromResult($"data:{mime};base64,UkVTT0xWRUQ=");
    }

    [Fact]
    public async Task Image_references_are_resolved_only_on_the_outgoing_copy()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up");
        h.Store.Put("provider:a", "k");
        h.Build(images: new FakeImages());
        h.Upstream.Enqueue(() => AnthropicText());
        var original = Msg.User(new TextPart("look"), new ImagePart("image/png", "ncw://attachments/sessions/s1/IMG.png"));

        await h.RunAsync(Msg.Request([original]));

        var block = Assert.Single(h.Upstream.Requests).Json.Arr("messages")[0].Arr("content")[1];
        Assert.Equal("UkVTT0xWRUQ=", block.Obj("source").Str("data"));
        Assert.Equal("ncw://attachments/sessions/s1/IMG.png", Assert.IsType<ImagePart>(original.Parts[1]).DataRef); // 存储里的消息永远保持引用
    }

    [Fact]
    public async Task An_unresolvable_image_fails_the_request_locally()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up");
        h.Store.Put("provider:a", "k");
        h.Build(images: new FakeImages());

        var error = LastError(await h.RunAsync(Msg.Request([Msg.User(new ImagePart("image/png", "ncw://missing.png"))]))).Error;

        Assert.Equal(LlmErrorKeys.ImageInput, error.MessageKey);
        Assert.Empty(h.Upstream.Requests);
    }

    [Fact]
    public async Task Responses_replaces_unresolvable_legacy_path_images_and_resolves_case_insensitive_ncw_refs()
    {
        var h = new Harness();
        h.Config.Add("openai", WireProtocol.OpenAiResponses, 1, "gpt-5");
        h.Store.Put("provider:openai", "k");
        h.Build(images: new FakeImages());
        h.Upstream.Enqueue(() => Reply.Sample("openai-responses-reasoning-tool.sse"));
        var history = new[]
        {
            Msg.User(new TextPart("from 2.x"), new ImagePart("image/png", "/Users/old/screenshot.png")),
            Msg.Assistant(new TextPart("ok")),
            Msg.User(new TextPart("current"), new ImagePart("image/jpg", "NCW://attachments/sessions/s1/IMG.jpg")),
        };

        var events = await h.RunAsync(Msg.Request(history));

        Assert.Contains(events, e => e is MessageEnd);
        var input = Assert.Single(h.Upstream.Requests).Json.Arr("input");
        var firstContent = input[0].Arr("content");
        Assert.Equal("input_text", firstContent[1].Str("type"));
        Assert.Contains("image omitted", firstContent[1].Str("text"));
        var secondContent = input[2].Arr("content");
        Assert.Equal("input_image", secondContent[1].Str("type"));
        Assert.Equal("data:image/jpeg;base64,UkVTT0xWRUQ=", secondContent[1].Str("image_url"));
    }

    // ───────────────────────── 跨模型切换(端到端) ─────────────────────────

    [Fact]
    public async Task Switching_to_a_different_model_drops_thinking_but_the_same_model_replays_it_with_its_signature()
    {
        var h = new Harness();
        h.Config.Add("a", WireProtocol.Anthropic, 1, "claude-a-up", alias: "x").Add("b", WireProtocol.Anthropic, 1, "claude-b-up", alias: "x");
        h.Store.Put("provider:a", "k").Put("provider:b", "k");
        h.Build();
        h.Upstream.Enqueue(() => AnthropicText()).Enqueue(() => AnthropicText());
        var history = new[]
        {
            Msg.User("q"),
            Msg.Assistant(new Provenance("anthropic", "a", "claude-a-up"),
                new ThinkingPart("deep thought", Msg.Opaque("anthropic", """{"signature":"SIG"}""")), new TextPart("answer")),
            Msg.User("follow up"),
        };

        await h.RunAsync(Msg.Request(history), "x", providerId: "a");
        await h.RunAsync(Msg.Request(history), "x", providerId: "b");

        var sameModel = h.Upstream.Requests[0].Json.Arr("messages")[1].Arr("content");
        var otherModel = h.Upstream.Requests[1].Json.Arr("messages")[1].Arr("content");
        Assert.Equal(["thinking", "text"], sameModel.Select(b => b.Str("type")));
        Assert.Equal("SIG", sameModel[0].Str("signature"));
        Assert.Equal(["text"], otherModel.Select(b => b.Str("type"))); // 来源不同:丢弃思考块
        Assert.NotNull(history[1].Parts.OfType<ThinkingPart>().SingleOrDefault()); // 只读转换,存储不变
    }

    [Fact]
    public async Task Failing_over_to_another_provider_re_encodes_the_history_for_that_provider()
    {
        var h = TwoProviders();
        h.Upstream.Enqueue(() => Reply.Status(HttpStatusCode.BadRequest, "a rejects")).Enqueue(() => ChatText());
        var history = new[]
        {
            Msg.User("q"),
            Msg.Assistant(new Provenance("anthropic", "a", "claude-a-up"),
                new ThinkingPart("deep", Msg.Opaque("anthropic", """{"signature":"SIG"}""")), Msg.Call("toolu_1", "Read", """{"path":"x"}""")),
            Msg.ToolResults(("toolu_1", "contents", false)),
        };

        await h.RunAsync(Msg.Request(history));

        var chat = h.Upstream.Requests[1].Json;
        var roles = chat.Arr("messages").Select(m => m.Str("role")).ToList();
        Assert.Equal(["system", "user", "assistant", "tool"], roles);
        Assert.DoesNotContain("SIG", h.Upstream.Requests[1].Body);
    }
}

internal static class HarnessExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
