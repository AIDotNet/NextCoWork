using System.Net;
using System.Text;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Media;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Tests;

/// <summary>托管的 NextCoWork 供应商:登录后同步平台模型,Claude 系强制 Anthropic 协议,令牌只在内存里,写入口全部拒绝。</summary>
public sealed class ManagedProviderTests : IDisposable
{
    private const string Models = """{"object":"list","data":[{"id":"claude-opus-4-5"},{"id":"gpt-5"},{"id":"anthropic/claude-sonnet-4"},{"id":"deepseek-chat"}]}""";

    private readonly ProviderFixture _fx = new();
    private readonly MemoryCredentialVault _inner = new();
    private string? _token = "tok-1";
    private ProviderService? _service;
    private ProviderConfigSource? _resolver;

    private ProviderService Service => _service ??= new ProviderService(
        _fx.File, new ManagedCredentialVault(_inner, () => _token), _fx.Accounts, _fx.Http, _fx.Catalog, _fx.Clock);

    private ProviderConfigSource Resolver => _resolver ??= new ProviderConfigSource(Service, _fx.Catalog);

    public void Dispose() => _fx.Dispose();

    // ── 协议规则(唯一一处) ──

    [Theory]
    [InlineData("claude-opus-4-5", true)]
    [InlineData("Claude-Sonnet-4", true)]
    [InlineData("anthropic/claude-sonnet-4", true)]
    [InlineData("openrouter/anthropic/claude-3.5-haiku", true)]
    [InlineData("us.anthropic.claude-3-5-haiku-20241022-v1:0", true)]
    [InlineData("gpt-5", false)]
    [InlineData("deepseek-chat", false)]
    [InlineData("gemini-3-pro", false)]
    [InlineData("not-claude", false)]
    [InlineData("", false)]
    public void Only_claude_models_are_forced_onto_the_anthropic_protocol(string model, bool claude)
    {
        Assert.Equal(claude, ManagedProvider.IsClaude(model));
        Assert.Equal(claude ? WireProtocol.Anthropic : null, ManagedProvider.ProtocolFor(model));
    }

    // ── 同步 ──

    [Fact]
    public async Task Syncing_adds_the_built_in_provider_first_and_forces_claude_models_onto_anthropic()
    {
        _fx.AddProvider("mine", priority: 50);
        _fx.Http.Ok(Models);

        var count = await Service.SyncManagedAsync();

        Assert.Equal(4, count);
        Assert.Equal(
            ["nextcowork", "nextcowork-image", "nextcowork-video", "nextcowork-audio", "mine"],
            Service.Snapshot.Providers.Select(p => p.Id).ToArray());
        var provider = Service.Provider("nextcowork")!;
        Assert.Equal(("NextCoWork", ProviderProtocol.OpenAiResponses, "https://nextco.work/v1", ManagedProvider.Priority), (provider.Name, provider.Protocol, provider.BaseUrl, provider.Priority));

        Assert.Equal(WireProtocol.Anthropic, Resolver.Resolve(new ModelSelection("claude-opus-4-5")).Protocol);
        Assert.Equal(WireProtocol.Anthropic, Resolver.Resolve(new ModelSelection("anthropic/claude-sonnet-4", "nextcowork")).Protocol);
        Assert.Equal(WireProtocol.OpenAiResponses, Resolver.Resolve(new ModelSelection("gpt-5")).Protocol);
        Assert.Equal(WireProtocol.OpenAiResponses, Resolver.Resolve(new ModelSelection("deepseek-chat")).Protocol);
        Assert.All(Resolver.Options().Where(o => o.ProviderId == "nextcowork" && ManagedProvider.IsClaude(o.UpstreamModel)), o => Assert.Equal(ProviderProtocol.Anthropic, o.Protocol));
    }

    [Fact]
    public async Task The_model_list_is_fetched_from_the_platform_with_the_delegated_token()
    {
        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();

        var seen = _fx.Http.Requests.Single();
        Assert.Equal("https://nextco.work/v1/models", seen.Url);
        Assert.Equal("Bearer tok-1", seen.Header("authorization"));
    }

    [Fact]
    public async Task A_claude_request_goes_to_the_anthropic_messages_endpoint_under_the_same_base()
    {
        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();

        var resolved = Resolver.Resolve(new ModelSelection("claude-opus-4-5"));
        // 路由用的就是这一对 (base, 协议):/v1 去重,Anthropic 打到 /v1/messages,Responses 打到 /responses。
        Assert.Equal("https://nextco.work/v1/messages", WireProtocols.JoinUrl(resolved.Provider!.BaseUrl, resolved.Protocol!.Value.PathOf()));
        Assert.Equal("https://nextco.work/v1/responses", WireProtocols.JoinUrl(resolved.Provider.BaseUrl, Resolver.Resolve(new ModelSelection("gpt-5")).Protocol!.Value.PathOf()));
    }

    [Fact]
    public async Task Without_a_token_nothing_is_requested_and_no_provider_appears()
    {
        _token = null;
        var error = await Assert.ThrowsAsync<ProviderException>(() => Service.SyncManagedAsync());

        Assert.Equal(ProviderErrorKeys.NoCredential, error.Key);
        Assert.Empty(_fx.Http.Requests);
        Assert.False(Service.HasManaged);
        Assert.Empty(Service.Providers);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_previous_models_so_the_picker_does_not_go_empty()
    {
        _fx.Http.Ok(Models).Status(System.Net.HttpStatusCode.InternalServerError, "boom");
        await Service.SyncManagedAsync();

        await Assert.ThrowsAsync<ProviderException>(() => Service.SyncManagedAsync());

        Assert.Equal(4, Service.Aliases("nextcowork").Count);
    }

    [Fact]
    public async Task Signing_out_while_the_fetch_is_in_flight_does_not_bring_the_provider_back()
    {
        _fx.Http.Reply((_, _) =>
        {
            _token = null; // 请求发出去之后、结果回来之前:退出登录
            return Task.FromResult(FakeHttp.Json(System.Net.HttpStatusCode.OK, Models));
        });

        var error = await Assert.ThrowsAsync<ProviderException>(() => Service.SyncManagedAsync());

        Assert.Equal(ProviderErrorKeys.NoCredential, error.Key);
        Assert.False(Service.HasManaged);
    }

    [Fact]
    public async Task Clearing_removes_the_provider_and_its_models_and_announces_it_once()
    {
        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();
        var announced = 0;
        Service.Changed += _ => announced++;

        Service.ClearManaged();
        Service.ClearManaged(); // 已经没有了:不再广播

        Assert.Equal(1, announced);
        Assert.Empty(Service.Providers);
        Assert.Empty(Service.Aliases());
        Assert.False(Resolver.Resolve(new ModelSelection("claude-opus-4-5")).Found);
    }

    [Fact]
    public async Task A_resync_replaces_the_whole_list_with_what_the_platform_says()
    {
        _fx.Http.Ok(Models).Ok("""{"data":[{"id":"claude-haiku-4-5"}]}""");
        await Service.SyncManagedAsync();
        await Service.SyncManagedAsync();

        Assert.Equal(["claude-haiku-4-5"], Service.Aliases("nextcowork").Select(a => a.Alias).ToArray());
    }

    // ── 不可编辑、不泄露 ──

    [Fact]
    public async Task Every_write_path_refuses_the_managed_provider()
    {
        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();

        static void Refused(Action action) => Assert.Equal(ProviderErrorKeys.ManagedProvider, Assert.Throws<ProviderException>(action).Key);
        Refused(() => Service.SetAliases("nextcowork", ["x"]));
        Refused(() => Service.RemoveAlias("nextcowork", "gpt-5"));
        Refused(() => Service.RenameAlias("nextcowork", "gpt-5", "g"));
        Refused(() => Service.UpdateAlias(new AliasUpdate("nextcowork", "claude-opus-4-5", ClearProtocolOverride: true)));
        Refused(() => Service.Remove("nextcowork"));
        Refused(() => Service.Upsert(new ProviderRecord("nextcowork", "Mine", WireProtocol.OpenAiChat, "https://evil.example.com")));
        Assert.Equal(ProviderErrorKeys.ManagedProvider, (await Assert.ThrowsAsync<ProviderException>(() => Service.SetApiKeyAsync("nextcowork", "k"))).Key);
        Assert.Equal(ProviderErrorKeys.ManagedProvider, (await Assert.ThrowsAsync<ProviderException>(() => Service.RemoveCredentialAsync("nextcowork"))).Key);
        Assert.Equal(ProviderErrorKeys.ManagedProvider, (await Assert.ThrowsAsync<ProviderException>(() => Service.RevealCredentialAsync("nextcowork"))).Key);

        // 协议覆盖没被任何一条路径改掉。
        Assert.Equal(ProviderProtocol.Anthropic, Service.Aliases("nextcowork").Single(a => a.Alias == "claude-opus-4-5").ProtocolOverride);
        Assert.Empty(_fx.File.ListAliases());
    }

    [Fact]
    public async Task The_credential_summary_only_says_whether_a_token_is_there_never_its_characters()
    {
        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();

        var info = await Service.GetCredentialInfoAsync("nextcowork");
        Assert.True(info.HasKey);
        Assert.Null(info.Last4);

        _token = null;
        Assert.False((await Service.GetCredentialInfoAsync("nextcowork")).HasKey);
    }

    // ── 凭据来源 ──

    [Fact]
    public async Task The_managed_credential_ref_reads_the_token_and_never_writes_it_anywhere()
    {
        var vault = new ManagedCredentialVault(_inner, () => _token);
        var reference = ProviderRefs.ForProvider("nextcowork");

        Assert.Equal("tok-1", await vault.GetAsync(reference, default));
        await vault.SetAsync(reference, "other", default);
        await vault.RemoveAsync(reference, default);
        vault.Remove(reference);
        Assert.Empty(_inner.Values);

        _token = null;
        Assert.Null(await vault.GetAsync(reference, default));
    }

    [Fact]
    public async Task Other_refs_pass_through_to_the_real_vault()
    {
        var vault = new ManagedCredentialVault(_inner, () => _token);
        await vault.SetAsync("provider:gw", "sk-1", default);
        Assert.Equal("sk-1", await vault.GetAsync("provider:gw", default));
        vault.Remove("provider:gw");
        Assert.Null(await vault.GetAsync("provider:gw", default));
    }

    [Fact]
    public async Task Syncing_splits_platform_models_into_all_four_built_in_providers_and_clears_them_together()
    {
        const string MixedPlatformModels = """
            {"object":"list","data":[
              {"id":"claude-opus-4-5"},
              {"id":"gpt-5"},
              {"id":"gpt-image-1.5"},
              {"id":"doubao-seedream-4-5-251128"},
              {"id":"sora-2"},
              {"id":"doubao-seedance-1-5-pro-251215"},
              {"id":"gpt-4o-mini-tts"},
              {"id":"whisper-large-v3"},
              {"id":"text-embedding-3-large"}
            ]}
            """;
        _fx.Http.Ok(MixedPlatformModels);

        var total = await Service.SyncManagedAsync();

        Assert.Equal(8, total); // text-embedding-3-large 被过滤掉
        Assert.Equal(["claude-opus-4-5", "gpt-5"], Service.Aliases("nextcowork").Select(a => a.Alias).ToArray());
        Assert.Equal(["gpt-image-1.5", "doubao-seedream-4-5-251128"], Service.Aliases("nextcowork-image").Select(a => a.Alias).ToArray());
        Assert.Equal(["sora-2", "doubao-seedance-1-5-pro-251215"], Service.Aliases("nextcowork-video").Select(a => a.Alias).ToArray());
        Assert.Equal(["gpt-4o-mini-tts", "whisper-large-v3"], Service.Aliases("nextcowork-audio").Select(a => a.Alias).ToArray());

        Assert.Equal((ProviderKind.Image, ProviderProtocol.OpenAiImages), (Service.Provider("nextcowork-image")!.Kind, Service.Provider("nextcowork-image")!.Protocol));
        Assert.Equal((ProviderKind.Video, ProviderProtocol.OpenAiVideos), (Service.Provider("nextcowork-video")!.Kind, Service.Provider("nextcowork-video")!.Protocol));
        Assert.Equal((ProviderKind.Audio, ProviderProtocol.OpenAiAudio), (Service.Provider("nextcowork-audio")!.Kind, Service.Provider("nextcowork-audio")!.Protocol));

        // 四个内置供应商共用委托令牌,且全部拒绝手动写入
        var vault = new ManagedCredentialVault(_inner, () => _token);
        foreach (var managedId in ProviderRefs.ManagedProviderIds)
        {
            Assert.Equal("tok-1", await vault.GetAsync(ProviderRefs.ForProvider(managedId), default));
            Assert.Equal(ProviderErrorKeys.ManagedProvider, Assert.Throws<ProviderException>(() => Service.Remove(managedId)).Key);
            Assert.Equal(ProviderErrorKeys.ManagedProvider, (await Assert.ThrowsAsync<ProviderException>(() => Service.SetApiKeyAsync(managedId, "k"))).Key);
        }

        Assert.NotNull(Resolver.ResolveMedia(new ModelSelection("gpt-image-1.5"), ProviderKind.Image));
        Assert.NotNull(Resolver.ResolveMedia(new ModelSelection("sora-2"), ProviderKind.Video));
        Assert.NotNull(Resolver.ResolveMedia(new ModelSelection("whisper-large-v3"), ProviderKind.Audio));

        Service.ClearManaged();
        Assert.Empty(Service.Providers);
        Assert.Empty(Service.Aliases());
    }

    [Fact]
    public async Task Seedance_video_with_sound_and_omni_chat_models_reporting_audio_output_capabilities_are_not_misclassified_as_audio()
    {
        // 复刻 NextCoWork 真实 /v1/models 负载:
        // - Seedance 2.0 / 2.5 有声视频模型返回 capabilities: ["VisionInput", "AudioInput", "AudioOutput"](平台枚举无 VideoOutput);
        // - Seedream 图片模型返回 capabilities: ["ImageGeneration", "VisionInput"];
        // - 多模态对话模型带 FunctionCalling + AudioOutput;
        // - 纯语音合成模型 seed-tts-2.0 返回 capabilities: ["AudioOutput"]。
        const string RealGatewayPayload = """
            {"object":"list","data":[
              {"id":"qwen3-omni-flash","capabilities":["Reasoning","VisionInput","AudioInput","AudioOutput","FunctionCalling","WebSearch","PromptCache"]},
              {"id":"doubao-seedream-4-5-251128","capabilities":["ImageGeneration","VisionInput"]},
              {"id":"doubao-seedance-2-0-260128","capabilities":["VisionInput","AudioInput","AudioOutput"]},
              {"id":"doubao-seedance-2-0-fast-260128","capabilities":["VisionInput","AudioInput","AudioOutput"]},
              {"id":"doubao-seedance-2-0-mini-260615","capabilities":["VisionInput","AudioInput","AudioOutput"]},
              {"id":"doubao-seedance-2-5-260628","capabilities":["VisionInput","AudioInput","AudioOutput"]},
              {"id":"doubao-seedance-2-5-flash-261115","capabilities":["VisionInput","AudioInput","AudioOutput"]},
              {"id":"seed-tts-2.0","capabilities":["AudioOutput"]}
            ]}
            """;
        _fx.Http.Ok(RealGatewayPayload);

        var total = await Service.SyncManagedAsync();

        Assert.Equal(8, total);
        Assert.Equal(["qwen3-omni-flash"], Service.Aliases("nextcowork").Select(a => a.Alias).ToArray());
        Assert.Equal(["doubao-seedream-4-5-251128"], Service.Aliases("nextcowork-image").Select(a => a.Alias).ToArray());
        Assert.Equal(
            [
                "doubao-seedance-2-0-260128",
                "doubao-seedance-2-0-fast-260128",
                "doubao-seedance-2-0-mini-260615",
                "doubao-seedance-2-5-260628",
                "doubao-seedance-2-5-flash-261115",
            ],
            Service.Aliases("nextcowork-video").Select(a => a.Alias).ToArray());
        Assert.Equal(["seed-tts-2.0"], Service.Aliases("nextcowork-audio").Select(a => a.Alias).ToArray());
    }

    // ── 平台登录态鉴权(2.x transport.ts platformLoginAuth) ──

    [Fact]
    public void Managed_provider_transport_authenticates_with_bearer_and_drops_x_api_key_while_leaving_other_providers_untouched()
    {
        var managed = new ProviderConfig("nextcowork", "NextCoWork", ManagedProvider.DefaultBaseUrl, WireProtocol.Anthropic, 1, true, "provider:nextcowork");
        var decoration = ManagedProviderTransport.Instance.Decorate(managed, WireProtocol.Anthropic, new ApiKeyCredential("jwt-1"), new RequestContext("w"));

        Assert.Equal("Bearer jwt-1", decoration.Headers!["authorization"]);
        Assert.Contains("x-api-key", decoration.DropHeaders!);

        var custom = new ProviderConfig("anthropic", "Anthropic", "https://api.anthropic.com", WireProtocol.Anthropic, 50, true, "provider:anthropic");
        var untouched = ManagedProviderTransport.Instance.Decorate(custom, WireProtocol.Anthropic, new ApiKeyCredential("sk-ant"), new RequestContext("w"));

        Assert.Null(untouched.Headers);
        Assert.Null(untouched.DropHeaders);
    }

    [Fact]
    public async Task Debugging_managed_claude_and_listing_models_use_bearer_without_x_api_key()
    {
        const string AnthropicSse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-opus-4-5\",\"usage\":{\"input_tokens\":3}}}\n\n"
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}\n\n"
            + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
            + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":1}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        const string ResponsesSse =
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"OK\"}\n\n"
            + "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"gpt-5\",\"status\":\"completed\",\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}}\n\n"
            + "data: [DONE]\n\n";

        _fx.Http.Ok(Models);
        await Service.SyncManagedAsync();
        _fx.Http.Requests.Clear();

        _fx.Http.Reply((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AnthropicSse, Encoding.UTF8, "text/event-stream") }))
            .Reply((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponsesSse, Encoding.UTF8, "text/event-stream") }));

        var claude = await Service.DebugAsync(new DebugRequest(
            new DebugTarget("nextcowork"), ProviderProtocol.Anthropic, "claude-opus-4-5", "Reply OK", 64, ThinkingLevel.Auto, false));
        var gpt = await Service.DebugAsync(new DebugRequest(
            new DebugTarget("nextcowork"), ProviderProtocol.OpenAiResponses, "gpt-5", "Reply OK", 64, ThinkingLevel.Auto, false));

        Assert.True(claude.Ok, claude.Failure?.Message);
        Assert.Equal("https://nextco.work/v1/messages", _fx.Http.Requests[0].Url);
        Assert.Equal("Bearer tok-1", _fx.Http.Requests[0].Header("authorization"));
        Assert.Null(_fx.Http.Requests[0].Header("x-api-key"));
        Assert.Equal("2023-06-01", _fx.Http.Requests[0].Header("anthropic-version"));

        Assert.True(gpt.Ok, gpt.Failure?.Message);
        Assert.Equal("https://nextco.work/v1/responses", _fx.Http.Requests[1].Url);
        Assert.Equal("Bearer tok-1", _fx.Http.Requests[1].Header("authorization"));
        Assert.Null(_fx.Http.Requests[1].Header("x-api-key"));

        _fx.Http.Requests.Clear();
        _fx.Http.Ok(Models).Ok(Models);
        var listing = await Service.ListModelsAsync(new DebugTarget("nextcowork"));

        Assert.Equal(2, listing.Attempts.Count);
        Assert.All(_fx.Http.Requests, r =>
        {
            Assert.Equal("Bearer tok-1", r.Header("authorization"));
            Assert.Null(r.Header("x-api-key"));
        });
    }
}
