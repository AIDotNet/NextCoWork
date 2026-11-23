using System.Net;
using System.Text;
using Ncw.Agent.Catalog;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Tests.Support;
using Ncw.Agent.Kernel.Loop;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 登录后的托管 NextCoWork 供应商经协议的端到端:委托令牌交付 → Agent 用它同步平台模型(Claude 系固定 Anthropic 协议)→ 撤销 → 整家消失;
/// 令牌从不上线;手动改它的任何入口都被拒。上游(平台 <c>/v1/models</c>)是脚本化的假 HTTP。
/// </summary>
public sealed class ManagedProviderHostTests : IAsyncLifetime
{
    private const string Token = "DELEGATED-MODEL-TOKEN-5d2b8e";
    private const string ModelsUrl = "nextco.work/v1/models";

    private const string PlatformModels = """{"data":[{"id":"claude-opus-4-5"},{"id":"gpt-5"},{"id":"claude-sonnet-4-5"}]}""";

    private readonly FakeUpstream _up = new();
    private readonly ConversationRig _rig;
    private DeviceClient _owner = null!;

    public ManagedProviderHostTests()
    {
        _rig = new ConversationRig(_ => new AgentServices
        {
            Http = _up,
            CatalogDelayer = new NeverDelay(),
            CatalogStore = new MemoryCatalogStore(),
            SessionTitles = false,
            Delayer = new NoDelay(),
            Loop = new AgentLoopOptions { ResumeDelays = [] },
        });
    }

    public async ValueTask InitializeAsync()
    {
        await _rig.InitializeAsync();
        _owner = await _rig.ConnectAsync("owner");
        await _rig.AddProjectAsync(_owner); // 等数据就绪
    }

    public ValueTask DisposeAsync() => _rig.DisposeAsync();

    private Task<AccountStatusDto> AttachAsync(DeviceClient client, string accountId = "u1") =>
        client.CallAsync(AccountMethods.AttachDelegatedToken,
            new AccountAttachParams(Token, DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds(), accountId, "t1"), idem: Guid.NewGuid().ToString("N"));

    private static bool HasManaged(ProvidersTopicState s) => s.Providers.Any(p => p.Id == "nextcowork");

    [Fact]
    public async Task Delivering_the_token_syncs_the_platform_models_with_claude_forced_onto_anthropic_and_revoking_removes_them()
    {
        var (owner, tap) = await _rig.ConnectTappedAsync("recorded-owner");
        var topic = await Watch<ProvidersTopicState>.OpenAsync(owner, ProvidersMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "the providers snapshot");
        Assert.False(HasManaged(topic.Snapshot));
        _up.On(ModelsUrl, () => Replies.Json(PlatformModels));

        await AttachAsync(owner);
        await topic.WaitAsync(() => topic.Decoded<ProvidersTopicState>(ProvidersEvents.Changed).Any(HasManaged), "providers.changed with the built-in provider");

        var state = topic.Decoded<ProvidersTopicState>(ProvidersEvents.Changed).Last(HasManaged);
        var provider = state.Providers.Single(p => p.Id == "nextcowork");
        Assert.Equal(("NextCoWork", "openai-responses", "https://nextco.work/v1", true), (provider.Name, provider.Protocol, provider.BaseUrl, provider.HasCredential));
        var aliases = state.Aliases.Where(a => a.ProviderId == "nextcowork").ToDictionary(a => a.Alias);
        Assert.Equal(["claude-opus-4-5", "claude-sonnet-4-5", "gpt-5"], aliases.Keys.Order().ToArray());
        Assert.Equal("anthropic", aliases["claude-opus-4-5"].ProtocolOverride);
        Assert.Equal("anthropic", aliases["claude-sonnet-4-5"].ProtocolOverride);
        Assert.Null(aliases["gpt-5"].ProtocolOverride);
        Assert.Equal("Bearer " + Token, _up.To(ModelsUrl).Single().Headers["authorization"]);

        // 选择器和解析看到的有效协议也一样。
        var options = (await owner.CallAsync(ProvidersMethods.ModelsList, new ModelsListParams())).Options.Where(o => o.ProviderId == "nextcowork").ToDictionary(o => o.Alias);
        Assert.Equal("anthropic", options["claude-opus-4-5"].Protocol);
        Assert.Equal("openai-responses", options["gpt-5"].Protocol);

        // 令牌从不上线:设备发来的每个字节里都没有它。
        Assert.DoesNotContain(Token, Encoding.Latin1.GetString(tap.Transcript), StringComparison.Ordinal);

        await owner.CallAsync(AccountMethods.DetachDelegatedToken, Unit.Value, idem: Guid.NewGuid().ToString("N"));
        await topic.WaitAsync(() => !HasManaged(topic.Decoded<ProvidersTopicState>(ProvidersEvents.Changed).Last()), "providers.changed without the built-in provider");
        Assert.DoesNotContain((await owner.CallAsync(ProvidersMethods.ModelsList, new ModelsListParams())).Options, o => o.ProviderId == "nextcowork");
    }

    [Fact]
    public async Task The_built_in_provider_cannot_be_edited_from_a_controller()
    {
        _up.On(ModelsUrl, () => Replies.Json(PlatformModels));
        var topic = await Watch<ProvidersTopicState>.OpenAsync(_owner, ProvidersMethods.Topic);
        await AttachAsync(_owner);
        await topic.WaitAsync(() => topic.Decoded<ProvidersTopicState>(ProvidersEvents.Changed).Any(HasManaged), "the built-in provider");

        static string KeyOf(ProtocolException e) => FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key;
        var edits = new Func<Task>[]
        {
            () => _owner.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("nextcowork", ["gpt-5"]), idem: Guid.NewGuid().ToString("N")),
            () => _owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("nextcowork", "sk-mine"), idem: Guid.NewGuid().ToString("N")),
            () => _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("nextcowork", "Mine", "openai-chat", "https://evil.example.com/v1"), idem: Guid.NewGuid().ToString("N")),
        };
        foreach (var edit in edits)
        {
            Assert.Equal("providers.managedProvider", KeyOf(await Assert.ThrowsAsync<ProtocolException>(edit)));
        }
    }

    [Fact]
    public async Task A_failed_first_sync_does_not_leave_a_half_provider()
    {
        _up.On(ModelsUrl, () => Replies.Json("no", status: System.Net.HttpStatusCode.Unauthorized));
        var topic = await Watch<ProvidersTopicState>.OpenAsync(_owner, ProvidersMethods.Topic);
        await topic.WaitAsync(() => topic.HasSnapshot, "the providers snapshot");

        await AttachAsync(_owner);
        await Watch<ProvidersTopicState>.UntilAsync(() => _up.To(ModelsUrl).Count >= 1, "the sync request");

        Assert.False(HasManaged(topic.Snapshot));
        Assert.DoesNotContain(topic.Decoded<ProvidersTopicState>(ProvidersEvents.Changed), HasManaged);
    }

    [Fact]
    public async Task Welcome_declares_provider_media_capability_and_attaching_syncs_all_four_built_in_providers_while_templates_include_volcengine_media()
    {
        Assert.Contains(Capabilities.ProviderMedia, _owner.Welcome.Features);

        var templates = (await _owner.CallAsync(ProviderTemplatesMethods.List, Unit.Value)).Templates;
        Assert.Contains(templates, t => t is { Id: "volcengine-image", Kind: "image", Protocol: "volcengine-images" });
        Assert.Contains(templates, t => t is { Id: "volcengine-video", Kind: "video", Protocol: "volcengine-videos" });

        _up.On(ModelsUrl, () => Replies.Json("""
            {"data":[
              {"id":"claude-sonnet-4-5"},
              {"id":"doubao-seedream-4-5-251128"},
              {"id":"doubao-seedance-1-5-pro-251215"},
              {"id":"whisper-large-v3"},
              {"id":"text-embedding-3-large"}
            ]}
            """));
        var topic = await Watch<ProvidersTopicState>.OpenAsync(_owner, ProvidersMethods.Topic);
        await AttachAsync(_owner);
        await topic.WaitAsync(() => topic.Decoded<ProvidersTopicState>(ProvidersEvents.Changed).Any(HasManaged), "all four built-in providers");

        var state = await _owner.CallAsync(ProvidersMethods.List, Unit.Value);
        var byId = state.Providers.ToDictionary(p => p.Id);
        Assert.Equal(("text", "openai-responses"), (byId["nextcowork"].Kind, byId["nextcowork"].Protocol));
        Assert.Equal(("image", "openai-images"), (byId["nextcowork-image"].Kind, byId["nextcowork-image"].Protocol));
        Assert.Equal(("video", "openai-videos"), (byId["nextcowork-video"].Kind, byId["nextcowork-video"].Protocol));
        Assert.Equal(("audio", "openai-audio"), (byId["nextcowork-audio"].Kind, byId["nextcowork-audio"].Protocol));

        // ChatOnly=true 只返回文本聊天模型;ChatOnly=false 返回全部四种类型
        var chatOnly = (await _owner.CallAsync(ProvidersMethods.ModelsList, new ModelsListParams(ChatOnly: true))).Options;
        Assert.Equal(["claude-sonnet-4-5"], chatOnly.Select(o => o.Alias).ToArray());
        var allOptions = (await _owner.CallAsync(ProvidersMethods.ModelsList, new ModelsListParams(ChatOnly: false))).Options;
        Assert.Equal(4, allOptions.Length);
        Assert.Contains(allOptions, o => o is { ProviderId: "nextcowork-image", Alias: "doubao-seedream-4-5-251128", Kind: "image", IsChatModel: false });
        Assert.Contains(allOptions, o => o is { ProviderId: "nextcowork-video", Alias: "doubao-seedance-1-5-pro-251215", Kind: "video", IsChatModel: false });
        Assert.Contains(allOptions, o => o is { ProviderId: "nextcowork-audio", Alias: "whisper-large-v3", Kind: "audio", IsChatModel: false });

        // 任何内置媒体供应商都不允许手动改
        foreach (var managedId in new[] { "nextcowork-image", "nextcowork-video", "nextcowork-audio" })
        {
            var err = await Assert.ThrowsAsync<ProtocolException>(() =>
                _owner.CallAsync(ProvidersMethods.Remove, new ProviderRefParams(managedId), idem: Guid.NewGuid().ToString("N")));
            Assert.Equal("providers.managedProvider", FrameCodec.DecodePayload(err.Details!, PayloadShapes.GetShape<ProviderErrorDto>()).Key);
        }

        // 自定义火山图片供应商经协议创建并按类型过滤拉取模型
        _up.On("ark.cn-beijing.volces.com/api/v3/models", () => Replies.Json("""{"data":[{"id":"doubao-seed-2-0-pro"},{"id":"doubao-seedream-4-5-251128"},{"id":"doubao-seedance-1-5-pro-251215"}]}"""));
        var saved = await _owner.CallAsync(
            ProvidersMethods.Upsert,
            new ProvidersUpsertParams("my-ark-img", "火山生图", "volcengine-images", "https://ark.cn-beijing.volces.com/api/v3", Kind: "image"),
            idem: Guid.NewGuid().ToString("N"));
        Assert.Equal(("image", "volcengine-images"), (saved.Kind, saved.Protocol));
        var fetched = await _owner.CallAsync(ProvidersMethods.ModelsFetch, new ProviderRefParams("my-ark-img"));
        Assert.Equal(["doubao-seedream-4-5-251128"], fetched.Models.Select(m => m.Id).ToArray());

        await _owner.CallAsync(AccountMethods.DetachDelegatedToken, Unit.Value, idem: Guid.NewGuid().ToString("N"));
        var afterDetach = await _owner.CallAsync(ProvidersMethods.List, Unit.Value);
        Assert.Equal(["my-ark-img"], afterDetach.Providers.Select(p => p.Id).ToArray());
    }

    [Fact]
    public async Task Running_a_managed_claude_model_uses_bearer_without_x_api_key_while_custom_anthropic_keeps_x_api_key()
    {
        const string AnthropicSse =
            "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"model\":\"claude-opus-4-5\",\"usage\":{\"input_tokens\":10}}}\n\n"
            + "event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}\n\n"
            + "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi from claude\"}}\n\n"
            + "event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n"
            + "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":4}}\n\n"
            + "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n";
        const string ResponsesSse =
            "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"hi from gpt\"}\n\n"
            + "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"gpt-5\",\"status\":\"completed\",\"usage\":{\"input_tokens\":10,\"output_tokens\":4}}}\n\n"
            + "data: [DONE]\n\n";

        var project = (await _owner.CallAsync(DeviceMethods.ProjectsList, Unit.Value)).Projects.Single();
        var topic = await Watch<ProvidersTopicState>.OpenAsync(_owner, ProvidersMethods.Topic);
        _up.On(ModelsUrl, () => Replies.Json(PlatformModels));
        _up.On("nextco.work/v1/messages", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AnthropicSse, Encoding.UTF8, "text/event-stream") });
        _up.On("nextco.work/v1/responses", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponsesSse, Encoding.UTF8, "text/event-stream") });
        _up.On("api.anthropic.example/v1/messages", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AnthropicSse, Encoding.UTF8, "text/event-stream") });

        await AttachAsync(_owner);
        await topic.WaitAsync(() => topic.Decoded<ProvidersTopicState>(ProvidersEvents.Changed).Any(HasManaged), "providers.changed with the built-in provider");

        // 1. 托管供应商的 Claude 模型走 Anthropic /v1/messages,但鉴权必须是 Authorization: Bearer 且不能带 x-api-key(平台网关有 x-api-key 就判 401)。
        var claudeSession = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: "claude-opus-4-5"));
        var claudeStart = await _owner.CallAsync(ConversationMethods.RunsStart,
            new RunsStartParams(Guid.NewGuid().ToString("N"), claudeSession.Id, Guid.NewGuid().ToString("N"),
                [new InputPartDto(InputPartKinds.Text, "hello")], new SendOptionsDto("claude-opus-4-5", ModelProviderId: "nextcowork", PermissionMode: "ask")));
        var claudeRun = await RunWatch.OpenAsync(_owner, claudeStart.RunId);
        await claudeRun.SettledAsync();

        Assert.Equal("done", claudeRun.Of<RunEndEventDto>().Single().Status);
        var claudeReq = Assert.Single(_up.To("nextco.work/v1/messages"));
        Assert.Equal("Bearer " + Token, claudeReq.Headers["authorization"]);
        Assert.False(claudeReq.Headers.ContainsKey("x-api-key"));
        Assert.Equal("2023-06-01", claudeReq.Headers["anthropic-version"]);
        Assert.StartsWith("NextCoWork/", claudeReq.Headers["user-agent"], StringComparison.Ordinal);

        // 2. 托管供应商的非 Claude 模型走 Responses,Authorization 不能被装饰器重复追加。
        var gptSession = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: "gpt-5"));
        var gptStart = await _owner.CallAsync(ConversationMethods.RunsStart,
            new RunsStartParams(Guid.NewGuid().ToString("N"), gptSession.Id, Guid.NewGuid().ToString("N"),
                [new InputPartDto(InputPartKinds.Text, "hello")], new SendOptionsDto("gpt-5", ModelProviderId: "nextcowork", PermissionMode: "ask")));
        var gptRun = await RunWatch.OpenAsync(_owner, gptStart.RunId);
        await gptRun.SettledAsync();

        Assert.Equal("done", gptRun.Of<RunEndEventDto>().Single().Status);
        var gptReq = Assert.Single(_up.To("nextco.work/v1/responses"));
        Assert.Equal("Bearer " + Token, gptReq.Headers["authorization"]);
        Assert.False(gptReq.Headers.ContainsKey("x-api-key"));
        Assert.StartsWith("NextCoWork/", gptReq.Headers["user-agent"], StringComparison.Ordinal);

        // 3. 用户自己配的 Anthropic 供应商零回归:仍发 x-api-key,不发 Authorization。
        await _owner.CallAsync(ProvidersMethods.Upsert, new ProvidersUpsertParams("my-ant", "My Anthropic", "anthropic", "https://api.anthropic.example"), idem: Guid.NewGuid().ToString("N"));
        await _owner.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams("my-ant", "sk-ant-custom"), idem: Guid.NewGuid().ToString("N"));
        await _owner.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams("my-ant", ["claude-opus-4-5"]), idem: Guid.NewGuid().ToString("N"));
        var customSession = await _owner.CallAsync(ConversationMethods.SessionsCreate, new SessionsCreateParams(project.WorkspaceId, Model: "claude-opus-4-5"));
        var customStart = await _owner.CallAsync(ConversationMethods.RunsStart,
            new RunsStartParams(Guid.NewGuid().ToString("N"), customSession.Id, Guid.NewGuid().ToString("N"),
                [new InputPartDto(InputPartKinds.Text, "hello")], new SendOptionsDto("claude-opus-4-5", ModelProviderId: "my-ant", PermissionMode: "ask")));
        var customRun = await RunWatch.OpenAsync(_owner, customStart.RunId);
        await customRun.SettledAsync();

        Assert.Equal("done", customRun.Of<RunEndEventDto>().Single().Status);
        var customReq = Assert.Single(_up.To("api.anthropic.example/v1/messages"));
        Assert.Equal("sk-ant-custom", customReq.Headers["x-api-key"]);
        Assert.False(customReq.Headers.ContainsKey("authorization"));
    }
}
