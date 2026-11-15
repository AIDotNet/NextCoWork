using System.Text.RegularExpressions;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;
using PolyType;
using PolyType.Abstractions;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 目录 / 供应商的线上形状:每个 DTO 往返、方法表声明齐全(种类 / 角色 / 能力 / 承载 / 幂等键)、结构性地保证凭据不进任何结果和主题状态、
/// 代表性载荷的冻结向量(<see cref="GoldenV1CatalogProviders"/>)。
/// </summary>
public class CatalogProvidersDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static readonly ModelPriceDto Price = new("usd", [new PriceTierDto(200_000, new PriceRatesDto(3, 15, 0.3, 3.75)), new PriceTierDto(null, new PriceRatesDto(6, 22.5, 0.6, 7.5, 12))], "models.dev");

    private static readonly CatalogModelDto Model = new(
        "anthropic", "claude-test-0", "Claude Test 0", "claude", 200_000, 64_000,
        new CatalogCapabilitiesDto(true, true, false, true, ["text", "image"], ["text"]), ["low", "high"], Price, "active", "2026-01-01", "2026-02-01", true);

    private static readonly CatalogStatusDto Status = new(7, "remote", 1_700_000_000_000, 1_700_000_100_000, 1_700_021_700_000, 5200, 90, new CatalogErrorDto("catalog.shrunk", "kept", 1_700_000_100_000));

    private static readonly ModelOverrideDto Override = new("anthropic", "claude-test-0", "Mine", 123_000, null, true, null, false, new PriceOverrideDto("cny", 1.5, 7.5, 0.15), false);

    private static readonly ProviderDto Provider = new("gw", "Gateway", "openai-chat", "https://gw.example.com/v1", 50, true, "5m", "{\"anthropic\":{\"cacheTtl\":\"1h\"}}", true, null);

    private static readonly ModelAliasDto Alias = new("sonnet", "gw", "claude-test-0", 200_000, 64_000, "Sonnet", null, 0, null, true, true, ["displayName"], "{\"mode\":\"toggle\"}", "[\"low\"]");

    private static readonly ProviderAccountDto Account = new(
        "a1", "codex", "chatgpt", "Work", 0, true, true, false,
        new AccountLimitDto(1_700_000_500_000, 1_700_000_000_000, "http429", "rate limited"),
        new QuotaDto(new QuotaWindowDto(42.5, 300, 1_700_003_600_000), null, 1_700_000_000_000), 1, 2,
        new CredentialAuthDto("chatgpt", "acc-1", "me@example.com", "plus", 1_700_003_600_000, false, false));

    public static TheoryData<string, string> GoldenCases() => new()
    {
        { "CatalogQueryParams", Hex(new CatalogQueryParams(Text: "claude", ToolCall: true, Limit: 50)) },
        { "CatalogModelDto", Hex(Model) },
        { "CatalogTopicState", Hex(new CatalogTopicState(Status, [Override])) },
        { "ModelOverrideDto", Hex(Override) },
        { "ProvidersUpsertParams", Hex(new ProvidersUpsertParams("gw", "Gateway", "openai-chat", "https://gw.example.com/v1", 50, true, null)) },
        { "ProviderDto", Hex(Provider) },
        { "ModelAliasDto", Hex(Alias) },
        { "ProvidersTopicState", Hex(new ProvidersTopicState(9, [Provider], [Alias], [new ProviderProblemDto("bad", "providers.file.invalidProtocol", "x")], [new ProviderAccountsDto("codex", [Account])])) },
        { "OAuthProgressDto", Hex(new OAuthProgressDto("codex", "waiting", "https://auth.example.com/authorize?state=s", null, null, null, true)) },
        { "CredentialInfoDto", Hex(new CredentialInfoDto(true, "7890", null, null, null)) },
        { "ProviderErrorDto", Hex(new ProviderErrorDto("providers.fetchFailed", new Dictionary<string, string> { ["provider"] = "gw" })) },
        { "ModelOptionDto", Hex(new ModelOptionDto("gw", "Gateway", "sonnet", "Sonnet", "claude-test-0", 200_000, 64_000, true, true, true, true, "effort", ["low", "high"], "openai-chat")) },
        { "ModelResolutionDto", Hex(new ModelResolutionDto(true, "none", "gw", "sonnet", "claude-test-0", 200_000, 64_000, true, "openai-chat", "Sonnet", Price)) },
    };

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void Golden_vectors_v1_catalog_and_providers(string name, string actual) =>
        Assert.Equal(GoldenV1CatalogProviders.Of(name), actual);

    [Fact]
    public void Every_payload_round_trips()
    {
        Assert.Equal(Model, RoundTrip(Model) with { Capabilities = Model.Capabilities, ReasoningEfforts = Model.ReasoningEfforts, Price = Model.Price });
        var back = RoundTrip(Model);
        Assert.Equal(["text", "image"], back.Capabilities.InputModalities);
        Assert.Equal(["low", "high"], back.ReasoningEfforts!);
        Assert.Equal(2, back.Price!.Tiers.Length);
        Assert.Null(back.Price.Tiers[1].UpToInputTokens);
        Assert.Equal(12d, back.Price.Tiers[1].Rates.CacheWrite1h);

        var topic = RoundTrip(new CatalogTopicState(Status, [Override]));
        Assert.Equal("catalog.shrunk", topic.Status.LastError!.Key);
        Assert.Equal(("cny", 1.5, false), (topic.Overrides[0].Price!.Currency, topic.Overrides[0].Price!.Input, topic.Overrides[0].Vision));
        Assert.Null(topic.Overrides[0].MaxOutputTokens); // null 与 0 / false 分得开

        var providers = RoundTrip(new ProvidersTopicState(9, [Provider], [Alias], [], [new ProviderAccountsDto("codex", [Account])]));
        Assert.Equal(9, providers.Version);
        Assert.Equal("me@example.com", providers.Accounts[0].Accounts[0].Auth!.Email);
        Assert.Equal(42.5, providers.Accounts[0].Accounts[0].Quota!.Primary!.UsedPercent);
        Assert.Equal(["displayName"], providers.Aliases[0].CatalogOverrides);

        Assert.Null(RoundTrip(new OAuthTopicState(null)).Current);
        Assert.Equal("waiting", RoundTrip(new OAuthTopicState(new OAuthProgressDto("codex", "waiting", "u"))).Current!.Phase);
        Assert.Equal("apiKey", RoundTrip(new RevealedCredentialDto("apiKey", ApiKey: "k")).Kind);
        Assert.Equal("providers.x", RoundTrip(new ProviderErrorDto("providers.x", [])).Key);
        Assert.Equal(15, RoundTrip(new CatalogSettingsDto(true, 15)).IntervalMinutes);
        Assert.False(RoundTrip(new ModelResolutionDto(false, "unknownAlias", null, null, null, 0, 0, false, null, null, null)).Found);
    }

    [Fact]
    public void Provider_errors_travel_inside_the_error_frame()
    {
        var details = new ProviderErrorDto("providers.fetchFailed", new Dictionary<string, string> { ["provider"] = "gw", ["status"] = "502" });
        var response = new ResponseFrame(7, false, null, new ProtocolError(ErrorCodes.Unavailable, "could not fetch", true, null,
            FrameCodec.EncodePayload(details, PayloadShapes.GetShape<ProviderErrorDto>())));

        var exception = ProtocolException.From(((ResponseFrame)FrameCodec.Decode(FrameCodec.Encode(response))).Error!);

        var decoded = FrameCodec.DecodePayload(exception.Details!, PayloadShapes.GetShape<ProviderErrorDto>());
        Assert.Equal(("providers.fetchFailed", "502"), (decoded.Key, decoded.Params["status"]));
    }

    // ───────────────────────── 方法表 ─────────────────────────

    [Fact]
    public void Every_catalog_method_declares_kind_role_capability_and_transport()
    {
        Assert.Equal(12, CatalogMethods.All.Count);
        Assert.Equal(CatalogMethods.All.Count, CatalogMethods.All.Select(m => m.Name).Distinct().Count());
        foreach (var method in CatalogMethods.All)
        {
            Assert.StartsWith("catalog.", method.Name);
            Assert.Equal(Capabilities.Catalog, method.Capability);
            Assert.Equal(Transports.Any, method.Allowed);
            // 读取 viewer 可;写入(command)owner
            Assert.Equal(method.Kind == MethodKind.Query ? Role.Viewer : Role.Owner, method.MinRole);
        }

        Assert.Equal(MethodKind.Command, CatalogMethods.Refresh.Kind);
        Assert.Equal(Role.Viewer, CatalogMethods.Topic.MinRole);
    }

    [Fact]
    public void Every_providers_method_declares_kind_role_capability_and_transport_and_only_reads_are_open_to_members()
    {
        Assert.Equal(29, ProvidersMethods.All.Count);
        Assert.Equal(ProvidersMethods.All.Count, ProvidersMethods.All.Select(m => m.Name).Distinct().Count());
        MethodDef[] memberReads = [ProvidersMethods.List, ProvidersMethods.ModelsList, ProvidersMethods.Resolve, ProvidersMethods.AccountsList];
        foreach (var method in ProvidersMethods.All)
        {
            Assert.StartsWith("providers.", method.Name);
            Assert.Equal(Capabilities.Providers, method.Capability);
            Assert.Equal(memberReads.Contains(method) ? Role.Member : Role.Owner, method.MinRole);
            Assert.True(memberReads.Contains(method) ? method.Kind == MethodKind.Query : method.MinRole == Role.Owner);
            if (method != ProvidersMethods.CredentialReveal)
            {
                Assert.Equal(Transports.Any, method.Allowed);
            }
        }

        // 明文只给本机通道的 owner
        Assert.Equal((Role.Owner, Transports.Local), (ProvidersMethods.CredentialReveal.MinRole, ProvidersMethods.CredentialReveal.Allowed));
        // 写入类都是 command(必须带幂等键);读 / 探测类是 query
        foreach (var write in new MethodDef[]
                 {
                     ProvidersMethods.Upsert, ProvidersMethods.Remove, ProvidersMethods.CredentialSet, ProvidersMethods.CredentialRemove, ProvidersMethods.ModelsSetAliases,
                     ProvidersMethods.ModelsUpdate, ProvidersMethods.ModelsRename, ProvidersMethods.ModelsRemove, ProvidersMethods.OAuthStart, ProvidersMethods.OAuthSubmitCode,
                     ProvidersMethods.AccountsPause, ProvidersMethods.AccountsReorder,
                 })
        {
            Assert.Equal(MethodKind.Command, write.Kind);
        }

        Assert.Equal(Role.Member, ProvidersMethods.Topic.MinRole);
        Assert.Equal(Role.Owner, ProvidersMethods.OAuthTopic.MinRole); // 授权 URL 带 state
        Assert.NotEqual(Capabilities.Catalog, Capabilities.Providers);
        Assert.NotEqual(Capabilities.Conversation, Capabilities.Catalog);
    }

    // ───────────────────────── 结构性保证:凭据不进结果和主题状态 ─────────────────────────

    private static readonly Regex Secretish = new("secret|password|apikey|refresh|credentialref|bearer|token$", RegexOptions.IgnoreCase);
    // (以 Token 结尾 = 单数的令牌;InputTokens 这类用量计数不算)

    /// <summary>沿着 PolyType 的类型形状走一遍(不用反射:测试项目同样开着裁剪分析),收集所有属性名。</summary>
    private static void Walk(ITypeShape shape, HashSet<Type> seen, List<string> offenders)
    {
        if (!seen.Add(shape.Type))
        {
            return;
        }

        switch (shape)
        {
            case IObjectTypeShape obj:
                foreach (var property in obj.Properties)
                {
                    if (Secretish.IsMatch(property.Name))
                    {
                        offenders.Add($"{obj.Type.Name}.{property.Name}");
                    }

                    Walk(property.PropertyType, seen, offenders);
                }

                break;
            case IEnumerableTypeShape sequence:
                Walk(sequence.ElementType, seen, offenders);
                break;
            case IOptionalTypeShape nullable:
                Walk(nullable.ElementType, seen, offenders);
                break;
            case IDictionaryTypeShape dictionary:
                Walk(dictionary.KeyType, seen, offenders);
                Walk(dictionary.ValueType, seen, offenders);
                break;
        }
    }

    [Fact]
    public void No_result_or_topic_state_type_has_a_field_that_could_carry_a_credential_except_the_reveal_result()
    {
        ITypeShape[] roots =
        [
            PayloadShapes.GetShape<CatalogQueryResult>(), PayloadShapes.GetShape<CatalogProvidersResult>(), PayloadShapes.GetShape<CatalogGetResult>(),
            PayloadShapes.GetShape<CatalogStatusDto>(), PayloadShapes.GetShape<CatalogRefreshResultDto>(), PayloadShapes.GetShape<CatalogTopicState>(),
            PayloadShapes.GetShape<CatalogOverridesResult>(), PayloadShapes.GetShape<CatalogSettingsDto>(),
            PayloadShapes.GetShape<ProvidersTopicState>(), PayloadShapes.GetShape<ModelOptionsResult>(), PayloadShapes.GetShape<ModelResolutionDto>(),
            PayloadShapes.GetShape<AccountsResult>(), PayloadShapes.GetShape<ProviderDto>(), PayloadShapes.GetShape<ProviderTestResultDto>(),
            PayloadShapes.GetShape<CredentialInfoDto>(), PayloadShapes.GetShape<FetchedModelsResult>(), PayloadShapes.GetShape<ModelAliasesResult>(),
            PayloadShapes.GetShape<ModelAliasDto>(), PayloadShapes.GetShape<OAuthTopicState>(), PayloadShapes.GetShape<OAuthProgressDto>(), PayloadShapes.GetShape<ProviderErrorDto>(),
        ];
        var offenders = new List<string>();
        var seen = new HashSet<Type>();
        foreach (var root in roots)
        {
            Walk(root, seen, offenders);
        }

        Assert.Empty(offenders);
        Assert.True(seen.Count > 30); // 确实走过了整棵类型树

        // 对照:明文只在 reveal 的结果里,以及方向是控制端 → Agent 的请求参数里
        var reveal = new List<string>();
        Walk(PayloadShapes.GetShape<RevealedCredentialDto>(), [], reveal);
        Assert.NotEmpty(reveal);
        var set = new List<string>();
        Walk(PayloadShapes.GetShape<CredentialSetParams>(), [], set);
        Assert.Contains("CredentialSetParams.ApiKey", set);
    }
}
