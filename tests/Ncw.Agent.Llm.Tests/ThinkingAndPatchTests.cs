using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Thinking;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Tests;

/// <summary>思考参数(01-llm §6)和用户请求补丁(§5)。</summary>
public sealed class ThinkingAndPatchTests
{
    private static JsonObject Body(params (string Key, JsonNode? Value)[] fields)
    {
        var body = new JsonObject { ["model"] = "m", ["max_tokens"] = 8192 };
        foreach (var (key, value) in fields)
        {
            body[key] = value;
        }

        return body;
    }

    private static ThinkingAdapterInput Input(
        WireProtocol protocol, ThinkingConfig? config, ThinkingLevel level, string model = "m", int maxOutput = 8192,
        IReadOnlyList<ReasoningEffort>? efforts = null, AdapterPreset preset = AdapterPreset.Auto) =>
        new(protocol, model, config, ThinkingResolver.Resolve(level, config, maxOutput, efforts), maxOutput, preset, efforts);

    // ───────────────────────── 解析 ─────────────────────────

    [Fact]
    public void Unsupported_models_resolve_to_nothing_so_no_field_can_ever_be_emitted()
    {
        Assert.Null(ThinkingResolver.Resolve(ThinkingLevel.High, new ThinkingConfig(ThinkingMode.Unsupported), 8192));
        Assert.Null(ThinkingResolver.Resolve(ThinkingLevel.High, null, 8192));
    }

    [Theory]
    [InlineData(ThinkingLevel.Minimal, 1024)]
    [InlineData(ThinkingLevel.Low, 4096)]
    [InlineData(ThinkingLevel.Medium, 10_000)]
    [InlineData(ThinkingLevel.High, 21_333)]
    [InlineData(ThinkingLevel.XHigh, 32_000)]
    [InlineData(ThinkingLevel.Max, 64_000)]
    public void Budget_levels_map_to_the_documented_budgets_when_there_is_room(ThinkingLevel level, int budget)
    {
        var resolved = ThinkingResolver.Resolve(level, new ThinkingConfig(ThinkingMode.Budget), 200_000)!;

        Assert.True(resolved.Enabled);
        Assert.Equal(budget, resolved.BudgetTokens);
    }

    [Fact]
    public void The_budget_is_capped_at_max_output_minus_1024_and_floored_at_1024()
    {
        var capped = ThinkingResolver.Resolve(ThinkingLevel.Max, new ThinkingConfig(ThinkingMode.Budget), 32_000)!;
        var floored = ThinkingResolver.Resolve(ThinkingLevel.Minimal, new ThinkingConfig(ThinkingMode.Budget), 2048)!;

        Assert.Equal(32_000 - 1024, capped.BudgetTokens);
        Assert.Equal(1024, floored.BudgetTokens);
    }

    [Fact]
    public void When_the_budget_cannot_be_satisfied_thinking_degrades_to_off_instead_of_failing()
    {
        var resolved = ThinkingResolver.Resolve(ThinkingLevel.High, new ThinkingConfig(ThinkingMode.Budget), 1500)!;

        Assert.False(resolved.Enabled);
        Assert.Null(resolved.BudgetTokens);
    }

    [Fact]
    public void Auto_follows_the_model_default_and_always_models_are_always_enabled()
    {
        Assert.False(ThinkingResolver.Resolve(ThinkingLevel.Auto, new ThinkingConfig(ThinkingMode.Toggle, DefaultEnabled: false), 8192)!.Enabled);
        Assert.True(ThinkingResolver.Resolve(ThinkingLevel.Off, new ThinkingConfig(ThinkingMode.Always), 8192)!.Enabled);
        Assert.True(ThinkingResolver.Resolve(ThinkingLevel.Auto, new ThinkingConfig(ThinkingMode.Effort, DefaultEnabled: false), 8192, [ReasoningEffort.Low, ReasoningEffort.High])!.Enabled);
        Assert.False(ThinkingResolver.Resolve(ThinkingLevel.Auto, new ThinkingConfig(ThinkingMode.Effort, DefaultEnabled: false), 8192, [ReasoningEffort.None, ReasoningEffort.Low, ReasoningEffort.High])!.Enabled);
        Assert.Equal(ReasoningEffort.XHigh, ThinkingResolver.Resolve(ThinkingLevel.XHigh, new ThinkingConfig(ThinkingMode.Effort), 8192)!.Effort);
    }

    // ───────────────────────── 显式关闭必须发出关闭参数 ─────────────────────────

    [Fact]
    public void Explicit_off_on_anthropic_sends_thinking_disabled_it_does_not_omit_the_field()
    {
        var config = new ThinkingConfig(ThinkingMode.Toggle);

        var body = ThinkingAdapter.Apply(Body(), Input(WireProtocol.Anthropic, config, ThinkingLevel.Off));

        Assert.Equal("""{"type":"disabled"}""", body["thinking"]!.ToJsonString());
    }

    [Fact]
    public void Explicit_off_on_chat_sends_reasoning_effort_none()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort);

        var body = ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiChat, config, ThinkingLevel.Off, efforts: [ReasoningEffort.None, ReasoningEffort.Low]));

        Assert.Equal("none", body["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void Explicit_off_on_responses_sends_reasoning_effort_none()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort);

        var body = ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiResponses, config, ThinkingLevel.Off, efforts: [ReasoningEffort.None, ReasoningEffort.Low]));

        Assert.Equal("none", body["reasoning"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void A_model_that_cannot_turn_thinking_off_is_an_error_not_a_silent_default()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort);

        var error = Assert.Throws<ThinkingAdapterException>(() =>
            ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiChat, config, ThinkingLevel.Off, efforts: [ReasoningEffort.Low, ReasoningEffort.High])));

        Assert.Equal("thinking.cannotDisable", error.Code);
    }

    [Fact]
    public void An_effort_the_model_does_not_accept_is_rejected()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort);

        Assert.Throws<ThinkingAdapterException>(() =>
            ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiChat, config, ThinkingLevel.XHigh, efforts: [ReasoningEffort.Low])));
    }

    [Fact]
    public void Auto_does_not_add_any_field_for_openai_but_an_explicit_level_does()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort, DefaultEffort: ReasoningEffort.Medium);

        var auto = ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiChat, config, ThinkingLevel.Auto));
        var high = ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiChat, config, ThinkingLevel.High));

        Assert.Equal("medium", auto["reasoning_effort"]!.GetValue<string>()); // 默认档也明确下发,不依赖上游默认
        Assert.Equal("high", high["reasoning_effort"]!.GetValue<string>());
    }

    // ───────────────────────── Anthropic 预算 ─────────────────────────

    [Fact]
    public void Anthropic_budget_thinking_removes_temperature_and_keeps_max_tokens_above_the_budget()
    {
        var config = new ThinkingConfig(ThinkingMode.Budget);
        var body = Body(("temperature", 0.5));

        var adapted = ThinkingAdapter.Apply(body, Input(WireProtocol.Anthropic, config, ThinkingLevel.High, maxOutput: 32_000));

        Assert.Equal(21_333, adapted["thinking"]!["budget_tokens"]!.GetValue<int>());
        Assert.Null(adapted["temperature"]); // thinking 开启时不接受 temperature
        Assert.True(adapted["max_tokens"]!.GetValue<int>() > 21_333);
        Assert.Equal(0.5, body["temperature"]!.GetValue<double>()); // 输入不被修改
    }

    [Fact]
    public void Anthropic_raises_max_tokens_when_the_legacy_budget_would_otherwise_exceed_it()
    {
        var config = new ThinkingConfig(ThinkingMode.Budget);
        var input = new ThinkingAdapterInput(WireProtocol.Anthropic, "m", config, new ResolvedThinking(ThinkingMode.Budget, true, true, BudgetTokens: 5000), 4096);

        var adapted = ThinkingAdapter.Apply(Body(("max_tokens", 4096)), input);

        Assert.Equal(5000 + 4096, adapted["max_tokens"]!.GetValue<int>());
    }

    // ───────────────────────── Claude 自适应思考(issue #202) ─────────────────────────

    [Theory]
    [InlineData("claude-opus-5-5", ClaudeOffMode.Omit)]
    [InlineData("anthropic/claude-opus-5.5", ClaudeOffMode.Omit)]
    [InlineData("claude-fable-5-2", ClaudeOffMode.Omit)]
    [InlineData("claude-sonnet-5-5", ClaudeOffMode.BetweenTools)]
    [InlineData("us.anthropic.claude-sonnet-5", ClaudeOffMode.Disabled)]
    [InlineData("claude-opus-4-7", ClaudeOffMode.Disabled)]
    [InlineData("claude-opus-4-6-20260101", ClaudeOffMode.Disabled)]
    [InlineData("claude-haiku-5-5", ClaudeOffMode.Disabled)]
    public void Adaptive_claude_models_are_recognised_by_name(string model, ClaudeOffMode off)
    {
        Assert.Equal(off, ClaudeThinking.For(model)!.Off);
    }

    [Theory]
    [InlineData("claude-sonnet-4-5")]
    [InlineData("claude-sonnet-4-20250514")]
    [InlineData("claude-haiku-4-5")]
    [InlineData("claude-opus-4-1")]
    [InlineData("claude-3-5-sonnet-latest")]
    [InlineData("gpt-6.1")]
    public void Older_claude_models_keep_the_budget_dialect(string model)
    {
        Assert.Null(ClaudeThinking.For(model));
    }

    [Theory]
    [InlineData(ThinkingMode.Budget)]
    [InlineData(ThinkingMode.Effort)]
    public void Claude_5_5_never_gets_budget_tokens_whatever_the_catalog_declared(ThinkingMode mode)
    {
        var config = new ThinkingConfig(mode);
        var body = Body(("temperature", 0.5));

        var adapted = ThinkingAdapter.Apply(body, Input(WireProtocol.Anthropic, config, ThinkingLevel.High, model: "claude-opus-5-5", maxOutput: 32_000));

        Assert.Equal("adaptive", adapted["thinking"]!["type"]!.GetValue<string>());
        Assert.Null(adapted["thinking"]!["budget_tokens"]);
        Assert.Equal("summarized", adapted["thinking"]!["display"]!.GetValue<string>());
        Assert.Equal("high", adapted["output_config"]!["effort"]!.GetValue<string>());
        Assert.Null(adapted["temperature"]);
    }

    [Fact]
    public void Auto_on_claude_turns_on_adaptive_thinking_without_forcing_an_effort()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort);

        var adapted = ThinkingAdapter.Apply(Body(), Input(WireProtocol.Anthropic, config, ThinkingLevel.Auto, model: "claude-sonnet-5-5"));

        Assert.Equal("adaptive", adapted["thinking"]!["type"]!.GetValue<string>());
        Assert.Null(adapted["output_config"]);
    }

    [Fact]
    public void Off_is_expressed_the_way_each_claude_model_accepts()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort);
        var efforts = new[] { ReasoningEffort.Low, ReasoningEffort.Medium, ReasoningEffort.High, ReasoningEffort.Max };

        var opus55 = ThinkingAdapter.Apply(Body(), Input(WireProtocol.Anthropic, config, ThinkingLevel.Off, model: "claude-opus-5-5", efforts: efforts));
        var sonnet55 = ThinkingAdapter.Apply(Body(), Input(WireProtocol.Anthropic, config, ThinkingLevel.Off, model: "claude-sonnet-5-5", efforts: efforts));
        var opus48 = ThinkingAdapter.Apply(Body(), Input(WireProtocol.Anthropic, config, ThinkingLevel.Off, model: "claude-opus-4-8", efforts: efforts));

        // Opus 5.5 关不掉思考:不发 thinking,降到最低档。
        Assert.Null(opus55["thinking"]);
        Assert.Equal("low", opus55["output_config"]!["effort"]!.GetValue<string>());
        // Sonnet 5.5 只接受 between_tools,且不能带别的字段。
        Assert.Equal("between_tools", sonnet55["thinking"]!["type"]!.GetValue<string>());
        Assert.Single(sonnet55["thinking"]!.AsObject());
        Assert.Null(sonnet55["output_config"]);
        Assert.Equal("disabled", opus48["thinking"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Unsupported_claude_efforts_are_clamped_instead_of_failing()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort);

        var minimal = ThinkingAdapter.Apply(Body(), Input(WireProtocol.Anthropic, config, ThinkingLevel.Minimal, model: "claude-opus-5-5"));
        var xhighOn46 = ThinkingAdapter.Apply(Body(), Input(WireProtocol.Anthropic, config, ThinkingLevel.XHigh, model: "claude-opus-4-6"));

        Assert.Equal("low", minimal["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("high", xhighOn46["output_config"]!["effort"]!.GetValue<string>());
        Assert.Null(xhighOn46["thinking"]!["display"]); // 4.6 默认就是 summarized
    }

    [Fact]
    public void A_saved_patch_cannot_switch_an_explicit_off_back_to_disabled_on_opus_5_5()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort);
        var input = Input(WireProtocol.Anthropic, config, ThinkingLevel.Off, model: "claude-opus-5-5");
        var patched = Body(("thinking", new JsonObject { ["type"] = "disabled" }));

        var guarded = ThinkingAdapter.Enforce(patched, input);

        Assert.Null(guarded["thinking"]);
        Assert.Equal("low", guarded["output_config"]!["effort"]!.GetValue<string>());
    }

    // ───────────────────────── 方言与白名单 ─────────────────────────

    [Fact]
    public void Always_and_unsupported_models_get_every_thinking_field_stripped()
    {
        var dirty = Body(("thinking", new JsonObject { ["type"] = "enabled" }), ("reasoning_effort", "high"), ("enable_thinking", true));

        foreach (var mode in new[] { ThinkingMode.Always, ThinkingMode.Unsupported })
        {
            var adapted = ThinkingAdapter.Apply(dirty, Input(WireProtocol.OpenAiChat, new ThinkingConfig(mode), ThinkingLevel.High));

            Assert.Null(adapted["thinking"]);
            Assert.Null(adapted["reasoning_effort"]);
            Assert.Null(adapted["enable_thinking"]);
        }
    }

    [Fact]
    public void Deepseek_by_name_gets_thinking_type_plus_a_flat_effort_field()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort);

        var on = ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiChat, config, ThinkingLevel.High, "deepseek-v4"));
        var off = ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiChat, new ThinkingConfig(ThinkingMode.Toggle), ThinkingLevel.Off, "deepseek-v4"));

        Assert.Equal("enabled", on["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("high", on["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("disabled", off["thinking"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Standard_wire_hosts_do_not_get_the_vendor_dialect_even_for_a_deepseek_name()
    {
        var config = new ThinkingConfig(ThinkingMode.Effort, StandardWire: true);

        var adapted = ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiChat, config, ThinkingLevel.High, "deepseek-v4"));

        Assert.Null(adapted["thinking"]);
        Assert.Equal("high", adapted["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void Custom_parameter_paths_are_whitelisted()
    {
        var ok = new ThinkingConfig(ThinkingMode.Toggle, ParameterPath: "enable_thinking");
        var bad = new ThinkingConfig(ThinkingMode.Toggle, ParameterPath: "model");

        Assert.True(ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiChat, ok, ThinkingLevel.Medium, preset: AdapterPreset.Custom))["enable_thinking"]!.GetValue<bool>());
        Assert.Throws<ThinkingAdapterException>(() => ThinkingAdapter.Apply(Body(), Input(WireProtocol.OpenAiChat, bad, ThinkingLevel.Medium, preset: AdapterPreset.Custom)));
    }

    [Fact]
    public void Replay_dialect_is_text_required_only_for_deepseek_names_and_standard_wire_hosts_opt_out()
    {
        Assert.Equal(ReasoningReplay.TextRequired, ReasoningReplayPolicy.For(null, "deepseek-v4"));
        Assert.Equal(ReasoningReplay.OpaqueOnly, ReasoningReplayPolicy.For(null, "gpt-5"));
        Assert.Equal(ReasoningReplay.OpaqueOnly, ReasoningReplayPolicy.For(new ThinkingConfig(ThinkingMode.Effort, StandardWire: true), "deepseek-v4"));
        Assert.Equal(ReasoningReplay.TextRequired, ReasoningReplayPolicy.For(new ThinkingConfig(ThinkingMode.Effort, ReasoningReplay: ReasoningReplay.TextRequired), "gpt-5"));
    }

    // ───────────────────────── 补丁之后的最后一道闸 ─────────────────────────

    [Fact]
    public void A_patch_cannot_smuggle_thinking_fields_back_into_an_unsupported_model()
    {
        var config = new ThinkingConfig(ThinkingMode.Unsupported);
        var input = Input(WireProtocol.OpenAiChat, config, ThinkingLevel.High);
        var patched = RequestPatches.Apply(Body(), [new PatchRule(PatchOp.Add, "/extra_body", Jsn.Parse("""{"thinking":{"type":"enabled"},"keep":1}"""))]);

        var guarded = ThinkingAdapter.Enforce(patched, input);

        Assert.Null(guarded["extra_body"]!["thinking"]);
        Assert.Equal(1, guarded["extra_body"]!["keep"]!.GetValue<int>());
    }

    [Fact]
    public void An_explicit_off_beats_a_saved_patch_that_turns_thinking_on()
    {
        var config = new ThinkingConfig(ThinkingMode.Toggle);
        var input = Input(WireProtocol.Anthropic, config, ThinkingLevel.Off);
        var patched = RequestPatches.Apply(Body(), [new PatchRule(PatchOp.Add, "/thinking", Jsn.Parse("""{"type":"enabled","budget_tokens":9999}"""))]);

        var guarded = ThinkingAdapter.Enforce(patched, input);

        Assert.Equal("""{"type":"disabled"}""", guarded["thinking"]!.ToJsonString());
    }

    // ───────────────────────── 请求补丁 ─────────────────────────

    [Theory]
    [InlineData("/model")]
    [InlineData("/messages/0/content")]
    [InlineData("/stream")]
    [InlineData("/tools/0")]
    [InlineData("/headers/authorization")]
    [InlineData("/metadata/api_key")]
    [InlineData("/metadata/Authorization")]
    [InlineData("/extra_body/__proto__")]
    [InlineData("/unknown_param")]
    [InlineData("")]
    [InlineData("temperature")]
    public void Patches_may_not_touch_model_messages_tools_stream_or_credentials(string path)
    {
        var patch = new PatchRule(PatchOp.Add, path, Jsn.Parse("1"));

        Assert.NotNull(RequestPatches.Validate([patch]));
        Assert.Throws<RequestPatchException>(() => RequestPatches.Apply(Body(), [patch]));
    }

    [Fact]
    public void A_forbidden_key_inside_a_replaced_container_is_rejected_too()
    {
        var patch = new PatchRule(PatchOp.Replace, "/metadata", Jsn.Parse("""{"x":{"api_key":"leak"}}"""));

        Assert.NotNull(RequestPatches.Validate([patch]));
    }

    [Fact]
    public void Allowed_patches_apply_add_replace_and_remove_atomically()
    {
        var body = Body(("temperature", 1.0), ("stop", new JsonArray((JsonNode?)"a", (JsonNode?)"c")));
        var patches = new[]
        {
            new PatchRule(PatchOp.Replace, "/temperature", Jsn.Parse("0.2")),
            new PatchRule(PatchOp.Add, "/top_p", Jsn.Parse("0.9")),
            new PatchRule(PatchOp.Add, "/stop/1", Jsn.Parse("\"b\"")),
            new PatchRule(PatchOp.Remove, "/stop/2"),
            new PatchRule(PatchOp.Add, "/extra_body", Jsn.Parse("""{"top_k":5}""")),
        };

        var patched = RequestPatches.Apply(body, patches);

        Assert.Equal(0.2, patched["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, patched["top_p"]!.GetValue<double>());
        Assert.Equal(["a", "b"], ((JsonArray)patched["stop"]!).Select(n => n!.GetValue<string>()));
        Assert.Equal(1.0, body["temperature"]!.GetValue<double>()); // 原请求体不变
    }

    [Fact]
    public void A_failing_patch_leaves_the_original_body_untouched()
    {
        var body = Body(("temperature", 1.0));

        Assert.Throws<RequestPatchException>(() => RequestPatches.Apply(body, [
            new PatchRule(PatchOp.Replace, "/temperature", Jsn.Parse("0.1")),
            new PatchRule(PatchOp.Remove, "/top_p"), // 目标不存在
        ]));

        Assert.Equal(1.0, body["temperature"]!.GetValue<double>());
    }

    [Fact]
    public void Json_pointer_escapes_are_decoded_and_bad_escapes_rejected()
    {
        Assert.NotNull(RequestPatches.Validate([new PatchRule(PatchOp.Add, "/metadata/a~2b", Jsn.Parse("1"))]));
        Assert.Null(RequestPatches.Validate([new PatchRule(PatchOp.Add, "/metadata/a~1b", Jsn.Parse("1"))]));
    }

    [Fact]
    public void A_patch_missing_its_value_is_invalid()
    {
        Assert.NotNull(RequestPatches.Validate([new PatchRule(PatchOp.Add, "/temperature")]));
    }
}
