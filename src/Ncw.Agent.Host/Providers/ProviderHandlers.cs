using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ncw.Agent.Catalog;
using Ncw.Agent.Llm.Media;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Providers;

/// <summary>
/// <c>catalog.*</c> 与 <c>providers.*</c> 的方法实现(方法表在 <see cref="CatalogMethods"/> / <see cref="ProvidersMethods"/>)。
/// 角色、承载、幂等键由路由统一检查,这里只做业务:校验参数 → 调 Catalog 层 → 映射成 DTO。设备数据没就绪时 <paramref name="runtime"/> 抛可重试的 <c>unavailable</c>。
/// 失败一律是 <see cref="ProtocolException"/>:Catalog 层的 <see cref="ProviderException"/> 带着稳定 key 放进 <c>details</c>(<see cref="ProviderErrorDto"/>),其它异常路由收成 <c>internal</c>,不外泄细节。
/// </summary>
internal static class ProviderHandlers
{
    private const int DefaultQueryLimit = 500;

    private const int MaxQueryLimit = 2000;

    public static void Register(MethodRouter router, Func<ProviderRuntime> runtime)
    {
        router.DeclareCapability(Capabilities.ProviderMedia);

        // ── catalog ──
        router.Handle(CatalogMethods.Query, (p, _, _) => Sync(() => Query(runtime(), p)));
        router.Handle(CatalogMethods.Providers, (_, _, _) => Sync(() => CatalogProviders(runtime())));
        router.Handle(CatalogMethods.Get, (p, _, _) => Sync(() => CatalogGet(runtime(), p)));
        router.Handle(CatalogMethods.Status, (_, _, _) => Sync(() => ProviderMapping.ToDto(runtime().Catalog.Status)));
        router.Handle(CatalogMethods.Refresh, async (_, _, ct) => ProviderMapping.ToDto(await runtime().Catalog.RefreshAsync(ct).ConfigureAwait(false)));
        router.Handle(CatalogMethods.OverrideSet, (p, _, _) => Sync(() => Mutate(runtime(), c => c.SetOverride(ProviderMapping.FromDto(p)))));
        router.Handle(CatalogMethods.OverrideRemove, (p, _, _) => Sync(() => Mutate(runtime(), c => c.RemoveOverride(p.ProviderId, Require(p.ModelId)))));
        router.Handle(CatalogMethods.ModelDelete, (p, _, _) => Sync(() => Mutate(runtime(), c => c.DeleteModel(p.ProviderId, Require(p.ModelId)))));
        router.Handle(CatalogMethods.ModelRestore, (p, _, _) => Sync(() => Mutate(runtime(), c => c.RestoreModel(p.ProviderId, Require(p.ModelId)))));
        router.Handle(CatalogMethods.OverridesList, (_, _, _) => Sync(() => new CatalogOverridesResult([.. runtime().Catalog.Overrides.Select(ProviderMapping.ToDto)])));
        router.Handle(CatalogMethods.SettingsGet, (_, _, _) => Sync(() => runtime().CatalogSettings()));
        router.Handle(CatalogMethods.SettingsSet, async (p, _, _) => await runtime().SetCatalogSettingsAsync(p).ConfigureAwait(false));

        // ── providers:读 ──
        router.Handle(ProvidersMethods.List, (_, _, _) => Guard(() => runtime().ProvidersState()));
        router.Handle(ProvidersMethods.ModelsList, (p, _, _) => Guard(() => ModelsList(runtime(), p)));
        router.Handle(ProvidersMethods.Resolve, (p, _, _) => Guard(() => ProviderMapping.ToDto(runtime().Config.Resolve(new ModelSelection(p.Alias, p.ProviderId)))));
        router.Handle(ProvidersMethods.AccountsList, (p, _, ct) => GuardAsync(async () => Accounts(await runtime().Providers.ListAccountsAsync(p.ProviderId, ct).ConfigureAwait(false))));

        // ── providers:供应商 ──
        router.Handle(ProvidersMethods.Upsert, (p, _, _) => Guard(() => Upsert(runtime(), p)));
        router.Handle(ProvidersMethods.Remove, (p, _, _) => Guard(() =>
        {
            runtime().Providers.Remove(p.ProviderId);
            runtime().PublishProviders();
            return Unit.Value;
        }));
        router.Handle(ProvidersMethods.Test, (p, _, ct) => GuardAsync(async () =>
        {
            var result = ProviderMapping.ToDto(await runtime().Providers.TestAsync(p.ProviderId, ct).ConfigureAwait(false));
            return result with { Detail = Scrub(result.Detail, await SecretsOfAsync(runtime(), p.ProviderId, ct).ConfigureAwait(false)) };
        }));

        // ── providers:凭据 ──
        router.Handle(ProvidersMethods.CredentialSet, (p, _, ct) => GuardAsync(async () =>
        {
            var info = await runtime().Providers.SetApiKeyAsync(p.ProviderId, p.ApiKey, ct).ConfigureAwait(false);
            runtime().PublishProviders();
            return ProviderMapping.ToDto(info);
        }));
        router.Handle(ProvidersMethods.CredentialInfo, (p, _, ct) => GuardAsync(async () => ProviderMapping.ToDto(await runtime().Providers.GetCredentialInfoAsync(p.ProviderId, ct).ConfigureAwait(false))));
        router.Handle(ProvidersMethods.CredentialReveal, (p, _, ct) => GuardAsync(async () => ProviderMapping.Reveal(await runtime().Providers.RevealCredentialAsync(p.ProviderId, ct).ConfigureAwait(false))));
        router.Handle(ProvidersMethods.CredentialRemove, (p, _, ct) => GuardAsync(async () =>
        {
            await runtime().Providers.RemoveCredentialAsync(p.ProviderId, ct).ConfigureAwait(false);
            runtime().PublishProviders();
            return Unit.Value;
        }));

        // ── providers:模型别名 ──
        router.Handle(ProvidersMethods.ModelsFetch, (p, _, ct) => GuardAsync(async () =>
        {
            try
            {
                return new FetchedModelsResult([.. (await runtime().Providers.FetchModelsAsync(p.ProviderId, ct).ConfigureAwait(false)).Select(m => new FetchedModelDto(m.Id, m.DisplayName))]);
            }
            catch (ProviderException e)
            {
                // 上游的错误正文可能回显我们发过去的凭据:诊断里抹掉。
                var secrets = await SecretsOfAsync(runtime(), p.ProviderId, ct).ConfigureAwait(false);
                throw new ProviderException(e.Key, Scrub(e.Message, secrets)!, e.Parameters.ToDictionary(kv => kv.Key, kv => Scrub(kv.Value, secrets)!));
            }
        }));
        router.Handle(ProvidersMethods.ModelsSetAliases, (p, _, _) => Guard(() => new ModelAliasesResult([.. runtime().Providers.SetAliases(p.ProviderId, p.UpstreamModels).Select(ProviderMapping.ToDto)])));
        router.Handle(ProvidersMethods.ModelsUpdate, (p, _, _) => Guard(() => ProviderMapping.ToDto(runtime().Providers.UpdateAlias(ToUpdate(p)))));
        router.Handle(ProvidersMethods.ModelsRename, (p, _, _) => Guard(() => ProviderMapping.ToDto(runtime().Providers.RenameAlias(p.ProviderId, p.Alias, p.NextAlias))));
        router.Handle(ProvidersMethods.ModelsRemove, (p, _, _) => Guard(() =>
        {
            runtime().Providers.RemoveAlias(p.ProviderId, p.Alias);
            return Unit.Value;
        }));

        // ── providers:账号池 ──
        router.Handle(ProvidersMethods.AccountsRemove, (p, _, ct) => GuardAsync(async () => Accounts(await runtime().Providers.RemoveAccountAsync(p.ProviderId, p.AccountId, ct).ConfigureAwait(false))));
        router.Handle(ProvidersMethods.AccountsSetEnabled, (p, _, ct) => GuardAsync(async () => Accounts(await runtime().Providers.SetAccountEnabledAsync(p.ProviderId, p.AccountId, p.Enabled, ct).ConfigureAwait(false))));
        router.Handle(ProvidersMethods.AccountsSetLabel, (p, _, ct) => GuardAsync(async () => Accounts(await runtime().Providers.SetAccountLabelAsync(p.ProviderId, p.AccountId, p.Label, ct).ConfigureAwait(false))));
        router.Handle(ProvidersMethods.AccountsSetCurrent, (p, _, ct) => GuardAsync(async () => Accounts(await runtime().Providers.SetCurrentAccountAsync(p.ProviderId, p.AccountId, ct).ConfigureAwait(false))));
        router.Handle(ProvidersMethods.AccountsReorder, (p, _, ct) => GuardAsync(async () => Accounts(await runtime().Providers.ReorderAccountsAsync(p.ProviderId, p.AccountIds, ct).ConfigureAwait(false))));
        router.Handle(ProvidersMethods.AccountsClearLimit, (p, _, ct) => GuardAsync(async () => Accounts(await runtime().Providers.ClearAccountLimitAsync(p.ProviderId, p.AccountId, ct).ConfigureAwait(false))));
        router.Handle(ProvidersMethods.AccountsPause, (p, _, ct) => GuardAsync(async () =>
            Accounts(await runtime().Providers.PauseAccountUntilAsync(p.ProviderId, p.AccountId, DateTimeOffset.FromUnixTimeMilliseconds(p.UntilMs), ct).ConfigureAwait(false))));

        // ── providers:OAuth(start / addAccount / reauth 在登录结束时才返回,进度走 providers.oauth 主题) ──
        router.Handle(ProvidersMethods.OAuthStart, (p, _, ct) => GuardAsync(async () =>
        {
            try
            {
                var info = await runtime().Providers.StartOAuthAsync(p.ProviderId, ct).ConfigureAwait(false);
                runtime().OAuthSucceeded(p.ProviderId);
                return ProviderMapping.ToDto(info);
            }
            finally
            {
                runtime().PublishProviders();
            }
        }));
        router.Handle(ProvidersMethods.OAuthAddAccount, (p, _, ct) => GuardAsync(async () =>
        {
            try
            {
                var accounts = await runtime().Providers.AddOrReauthAccountAsync(p.ProviderId, null, ct).ConfigureAwait(false);
                runtime().OAuthSucceeded(p.ProviderId);
                return Accounts(accounts);
            }
            finally
            {
                runtime().PublishProviders();
            }
        }));
        router.Handle(ProvidersMethods.OAuthReauth, (p, _, ct) => GuardAsync(async () =>
        {
            try
            {
                var accounts = await runtime().Providers.AddOrReauthAccountAsync(p.ProviderId, p.AccountId, ct).ConfigureAwait(false);
                runtime().OAuthSucceeded(p.ProviderId);
                return Accounts(accounts);
            }
            finally
            {
                runtime().PublishProviders();
            }
        }));
        router.Handle(ProvidersMethods.OAuthSignOut, (p, _, ct) => GuardAsync(async () =>
        {
            var info = await runtime().Providers.SignOutAsync(p.ProviderId, ct).ConfigureAwait(false);
            runtime().PublishProviders();
            return ProviderMapping.ToDto(info);
        }));
        router.Handle(ProvidersMethods.OAuthCancel, (p, _, _) => Guard(() =>
        {
            runtime().Providers.CancelOAuth(p.ProviderId);
            return Unit.Value;
        }));
        router.Handle(ProvidersMethods.OAuthSubmitCode, (p, _, _) => Guard(() =>
        {
            runtime().Providers.SubmitOAuthCode(p.ProviderId, p.Code);
            return Unit.Value;
        }));

        // ── providers.discovery(后加的能力):在这台设备上找其他客户端的配置;明文 key 只在设备上,DTO 只带后四位 ──
        router.Handle(ProviderDiscoveryMethods.Scan, (_, _, ct) => GuardAsync(async () =>
            new DiscoveryScanResult([.. (await runtime().Discovery.ScanAsync(ct).ConfigureAwait(false)).Select(ProviderMapping.ToDto)])));
        router.Handle(ProviderDiscoveryMethods.Import, (p, _, ct) => GuardAsync(async () =>
        {
            var outcomes = await runtime().Discovery.ImportAsync(p.Ids, ct).ConfigureAwait(false);
            runtime().PublishProviders();
            return new DiscoveryImportResult([.. outcomes.Select(o => new DiscoveryImportItemDto(o.CandidateId, o.ProviderId, o.ErrorKey))]);
        }));

        // ── providers.templates(后加的能力):添加供应商的内置模版(静态数据,和自动发现共用已知服务表) ──
        router.Handle(ProviderTemplatesMethods.List, (_, _, _) => Sync(() => Templates));

        // ── providers.debug(后加的能力):调试弹窗。草稿 key 只用于这一次请求;结果与进度里的凭据已脱敏 ──
        router.Handle(ProviderDebugMethods.Models, (p, _, ct) => GuardAsync(async () =>
            ProviderMapping.ToDto(await runtime().Providers.ListModelsAsync(ToTarget(p.Target), ct).ConfigureAwait(false))));
        router.Handle(ProviderDebugMethods.Run, (p, _, ct) => GuardAsync(async () =>
        {
            if (!ProviderDebugRuns.IsValidRunId(p.RunId))
            {
                throw new ProtocolException(ErrorCodes.InvalidRequest, "The debug run id must be 1-64 letters, digits, '-' or '_'.");
            }

            var protocol = ProviderProtocols.Parse(p.Protocol) ?? throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown protocol '{p.Protocol}'.");
            var request = new DebugRequest(ToTarget(p.Target), protocol, p.Model, p.Prompt, p.MaxOutputTokens, ProviderMapping.ParseDebugThinking(p.Thinking), p.WithTool);
            var rt = runtime();
            rt.DebugRuns.Start(p.RunId);
            try
            {
                var result = await rt.Providers.DebugAsync(request, ev => rt.DebugRuns.Add(p.RunId, ProviderMapping.ToDto(ev)), ct).ConfigureAwait(false);
                return ProviderMapping.ToDto(p.RunId, result);
            }
            finally
            {
                rt.DebugRuns.Finish(p.RunId);
            }
        }));
    }

    private static DebugTarget ToTarget(ProviderDebugTargetDto target)
    {
        ProviderProtocol? protocol = null;
        if (target.Protocol is { } name)
        {
            protocol = ProviderProtocols.Parse(name) ?? throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown protocol '{name}'.");
        }

        ProviderKind? kind = null;
        if (target.Kind is { } kindName)
        {
            kind = ProviderKindExtensions.ParseKind(kindName) ?? throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown provider kind '{kindName}'.");
        }

        if (protocol is { } p && kind is { } k && p.KindOf() != k)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, $"Protocol '{p.NameOf()}' does not belong to kind '{k.NameOf()}'.");
        }

        if (target.ProviderId is null && string.IsNullOrWhiteSpace(target.BaseUrl))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "A debug target needs a saved provider id or an API address.");
        }

        return new DebugTarget(target.ProviderId, string.IsNullOrWhiteSpace(target.BaseUrl) ? null : target.BaseUrl, protocol, target.ApiKey, kind);
    }

    private static readonly ProviderTemplatesResult Templates = new([.. Ncw.Agent.Catalog.Templates.ProviderTemplates.All.Select(ProviderMapping.ToDto)]);

    // ───────────────────────── catalog ─────────────────────────

    private static CatalogQueryResult Query(ProviderRuntime rt, CatalogQueryParams p)
    {
        var snapshot = rt.Catalog.Current;
        var all = snapshot.Query(new CatalogQuery(p.ProviderId, p.ModelId, p.Text, p.ToolCall, p.Reasoning, p.Vision, p.ChatOnly, p.MinContextWindow));
        var limit = p.Limit <= 0 ? DefaultQueryLimit : Math.Min(p.Limit, MaxQueryLimit);
        return new CatalogQueryResult(snapshot.Version, all.Count, [.. all.Skip(Math.Max(0, p.Offset)).Take(limit).Select(ProviderMapping.ToDto)]);
    }

    private static CatalogProvidersResult CatalogProviders(ProviderRuntime rt)
    {
        var snapshot = rt.Catalog.Current;
        return new CatalogProvidersResult(snapshot.Version, [.. snapshot.Providers.Select(ProviderMapping.ToDto)]);
    }

    private static CatalogGetResult CatalogGet(ProviderRuntime rt, CatalogGetParams p)
    {
        var snapshot = rt.Catalog.Current;
        return new CatalogGetResult(snapshot.Version, snapshot.Get(p.ProviderId, p.ModelId) is { } model ? ProviderMapping.ToDto(model) : null);
    }

    private static CatalogTopicState Mutate(ProviderRuntime rt, Func<ModelCatalogService, CatalogSnapshot> change)
    {
        change(rt.Catalog);
        return rt.CatalogState();
    }

    private static string Require(string modelId) =>
        string.IsNullOrWhiteSpace(modelId) ? throw new ProtocolException(ErrorCodes.InvalidRequest, "A model id is required.") : modelId;

    // ───────────────────────── providers ─────────────────────────

    private static ModelOptionsResult ModelsList(ProviderRuntime rt, ModelsListParams p) =>
        new([.. rt.Config.Options().Where(o => !p.ChatOnly || o.IsChatModel).Select(ProviderMapping.ToDto)]);

    private static ProviderDto Upsert(ProviderRuntime rt, ProvidersUpsertParams p)
    {
        var protocol = ProviderProtocols.Parse(p.Protocol)
            ?? throw new ProviderException(ProviderErrorKeys.InvalidProtocol, $"Unknown protocol '{p.Protocol}'.");
        var kind = p.Kind is null
            ? protocol.KindOf()
            : ProviderKindExtensions.ParseKind(p.Kind)
                ?? throw new ProviderException(ProviderErrorKeys.InvalidKind, $"Unknown provider kind '{p.Kind}'.");
        var saved = rt.Providers.Upsert(new ProviderRecord(p.Id, p.Name, protocol, p.BaseUrl, p.Priority ?? ProviderRecord.DefaultPriority, p.Enabled ?? true, kind)
        {
            ProtocolOptions = ParseObject(p.ProtocolOptionsJson, nameof(p.ProtocolOptionsJson)),
        });
        return rt.ProviderOf(saved);
    }

    private static AccountsResult Accounts(IReadOnlyList<ProviderAccountView> views) => new([.. views.Select(ProviderMapping.ToDto)]);

    private static AliasUpdate ToUpdate(ModelUpdateParams p)
    {
        ProviderProtocol? protocol = null;
        if (p.ProtocolOverride is { } name)
        {
            protocol = ProviderProtocols.Parse(name) ?? throw new ProviderException(ProviderErrorKeys.InvalidProtocol, $"Unknown protocol '{name}'.");
        }

        return new AliasUpdate(
            p.ProviderId, p.Alias, p.DisplayName, p.ContextWindow, p.MaxOutputTokens, p.Enabled, p.Priority, protocol, p.ClearProtocolOverride, p.Vision,
            ParseObject(p.ThinkingConfigJson, nameof(p.ThinkingConfigJson)), ParseArray(p.ReasoningEffortsJson, nameof(p.ReasoningEffortsJson)),
            ParseObject(p.RequestAdapterJson, nameof(p.RequestAdapterJson)));
    }

    private static JsonObject? ParseObject(string? json, string field) => json is null ? null : ParseNode(json, field) as JsonObject
        ?? throw new ProtocolException(ErrorCodes.InvalidRequest, $"{field} must be a JSON object.");

    private static JsonArray? ParseArray(string? json, string field) => json is null ? null : ParseNode(json, field) as JsonArray
        ?? throw new ProtocolException(ErrorCodes.InvalidRequest, $"{field} must be a JSON array.");

    private static JsonNode? ParseNode(string json, string field)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, $"{field} is not valid JSON.");
        }
    }

    // ───────────────────────── 诊断里抹掉凭据 ─────────────────────────

    /// <summary>
    /// 这家当前凭据里的秘密串。上游的错误正文可能原样回显我们发过去的 key(或 token),而诊断文本是要发给控制端的:
    /// 凭据永不出现在任何 DTO 里,所以发之前把这些串抹掉。读不出凭据(没配 / 主密钥坏)就没有可抹的。
    /// </summary>
    private static async Task<string[]> SecretsOfAsync(ProviderRuntime rt, string providerId, CancellationToken ct)
    {
        try
        {
            return await rt.Providers.RevealCredentialAsync(providerId, ct).ConfigureAwait(false) switch
            {
                RevealedApiKey k => [k.ApiKey],
                RevealedOAuth o => [o.AccessToken, o.RefreshToken],
                RevealedSignature sig => [sig.SecretKey, sig.SessionToken ?? ""],
                _ => [],
            };
        }
        catch (Exception e) when (e is ProviderException or CryptographicException)
        {
            return [];
        }
    }

    private static string? Scrub(string? text, string[] secrets)
    {
        if (text is null)
        {
            return null;
        }

        foreach (var secret in secrets)
        {
            if (secret.Length >= 4)
            {
                text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        return text;
    }

    // ───────────────────────── 错误转换 ─────────────────────────

    private static ValueTask<T> Sync<T>(Func<T> work) => Guard(work);

    private static ValueTask<T> Guard<T>(Func<T> work)
    {
        try
        {
            return ValueTask.FromResult(work());
        }
        catch (Exception e) when (ToProtocolError(e) is { } converted)
        {
            throw converted;
        }
    }

    private static async ValueTask<T> GuardAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception e) when (ToProtocolError(e) is { } converted)
        {
            throw converted;
        }
    }

    /// <summary>认识的业务 / 存储失败转成协议错误;其它(含 <see cref="ProtocolException"/> 本身、取消)返回 null,原样往上抛。</summary>
    private static ProtocolException? ToProtocolError(Exception e) => e switch
    {
        ProtocolException => null,
        ProviderException provider => ProviderMapping.ToProtocol(provider),
        // 主密钥丢了 / 损坏:凭据读不出来。这是设备上的问题,不是请求的问题(也不能把异常细节发给控制端)。
        CryptographicException => new ProtocolException(ErrorCodes.Unavailable, "The credential store could not be read.", retryable: false),
        StorageException s => new ProtocolException(ErrorCodes.Unavailable, s.Message, retryable: true),
        _ => null,
    };
}
