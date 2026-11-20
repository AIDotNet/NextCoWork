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
}
