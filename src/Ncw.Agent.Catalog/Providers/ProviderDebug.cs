using System.Text.Json;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Diagnostics;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog;

/// <summary>
/// 调试对象(docs/design/07「供应商调试」):已保存的供应商(<paramref name="ProviderId"/>),和 / 或「添加 / 编辑供应商」表单里的草稿。
/// 草稿字段给了就压过已保存的;<paramref name="ApiKey"/> 只用于这一次请求,不落盘。没有供应商 id 时地址必填。
/// </summary>
public sealed record DebugTarget(string? ProviderId, string? BaseUrl = null, WireProtocol? Protocol = null, string? ApiKey = null);

/// <summary>一族模型列表端点的一次尝试。<see cref="Family"/> 是 <see cref="WireProtocol.Anthropic"/>(<c>/v1/models</c>)或 <see cref="WireProtocol.OpenAiChat"/>(OpenAI 族的 <c>/models</c>)。</summary>
public sealed record ModelListAttempt(WireProtocol Family, string Url, bool Ok, int? Status, int Count, string? ErrorKey, string? Detail);

/// <summary>合并后的一个模型,带「哪几族的列表端点报了它」。</summary>
public sealed record ListedModel(string Id, string? DisplayName, IReadOnlyList<WireProtocol> Families);

public sealed record ModelListing(IReadOnlyList<ModelListAttempt> Attempts, IReadOnlyList<ListedModel> Models);

/// <summary>一次调试请求:对 <see cref="Target"/> 用 <see cref="Protocol"/> 发一条消息给 <see cref="Model"/>(上游模型名,或这家已有的别名)。</summary>
public sealed record DebugRequest(DebugTarget Target, WireProtocol Protocol, string Model, string Prompt, int MaxOutputTokens, ThinkingLevel Thinking, bool WithTool);

public sealed partial class ProviderService
{
    /// <summary>调试请求最多要多少输出 token(防止手滑填一个天文数字烧钱)。</summary>
    public const int MaxDebugOutputTokens = 64_000;

    /// <summary>
    /// 调试弹窗的模型候选:两族列表端点都试一遍(先这家协议那一族),合并去重,每一族的结果都给出来。都拉不到不是错误:模型为空,控制端让用户手填。
    /// 失败说明里的凭据已抹掉。
    /// </summary>
    public async Task<ModelListing> ListModelsAsync(DebugTarget target, CancellationToken ct = default)
    {
        var (provider, credential) = await ResolveTargetAsync(target, ct).ConfigureAwait(false);
        return await ListModelsAsync(provider, credential, bothFamilies: true, ct).ConfigureAwait(false);
    }

    /// <summary>对调试对象发一条真实的流式请求(<see cref="ProviderProbe"/>),返回完整记录。永不抛业务异常(调试对象本身不合法、调用方取消除外)。</summary>
    public async Task<ProbeResult> DebugAsync(DebugRequest request, Action<ProbeEvent>? onEvent = null, CancellationToken ct = default)
    {
        var model = request.Model.Trim();
        if (model.Length == 0)
        {
            throw new ProviderException(ProviderErrorKeys.DebugModelRequired, "Pick or type a model to send the request to.");
        }

        var (provider, credential) = await ResolveTargetAsync(request.Target, ct).ConfigureAwait(false);
        var binding = DebugBinding(provider, model, request.Protocol, request.Target.ProviderId is not null);
        var config = new ProviderConfig(provider.Id, provider.Name, provider.BaseUrl, request.Protocol, provider.Priority, true, provider.CredentialRef, provider.CacheTtl);
        var probe = new ProviderProbe(_http, _transport, new ProbeOptions { UserAgent = _options.UserAgent, Clock = _clock });
        var prompt = string.IsNullOrWhiteSpace(request.Prompt) ? "Reply with the two letters OK." : request.Prompt;
        var maxTokens = Math.Clamp(request.MaxOutputTokens, 1, MaxDebugOutputTokens);
        return await probe.RunAsync(new ProbeRequest(config, binding, request.Protocol, credential, prompt, maxTokens, request.Thinking, request.WithTool), onEvent, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 调试用哪条绑定:模型是这家已有别名(按别名或上游模型名认)时用它的思考配置、档位和请求补丁,只把协议换成所选的(思考配置随协议重新推导);
    /// 否则当成一个没配过的上游模型,按目录推导。草稿(没保存的供应商)不查目录:还不知道它是哪家。
    /// </summary>
    private ModelBinding DebugBinding(ProviderRecord provider, string model, WireProtocol protocol, bool saved)
    {
        var alias = saved
            ? Aliases(provider.Id).FirstOrDefault(a => a.Alias == model) ?? Aliases(provider.Id).FirstOrDefault(a => a.UpstreamModel == model)
            : null;
        var record = (alias ?? new ModelAliasRecord(model, provider.Id, model)) with { ProtocolOverride = protocol };
        var info = saved ? _catalog.Describe(provider.Id, record.UpstreamModel) : null;
        return ModelBindingResolver.Resolve(record, provider, info) with { Enabled = true };
    }

    /// <summary>调试对象 → (这一次用的供应商形状, 凭据)。草稿的 key 压过已存的;都没有 = 不带鉴权头。</summary>
    private async Task<(ProviderRecord Provider, ProviderCredential? Credential)> ResolveTargetAsync(DebugTarget target, CancellationToken ct)
    {
        var saved = target.ProviderId is { } id ? Require(id) : null;
        var baseUrl = target.BaseUrl is { } draftUrl ? NormalizeBaseUrl(draftUrl)
            : saved?.BaseUrl ?? throw new ProviderException(ProviderErrorKeys.InvalidBaseUrl, "An API address is required.");
        var protocol = target.Protocol ?? saved?.Protocol ?? WireProtocol.OpenAiChat;
        var provider = (saved ?? new ProviderRecord(DraftProviderId, "Draft", protocol, baseUrl)) with { BaseUrl = baseUrl, Protocol = protocol };
        var credential = !string.IsNullOrWhiteSpace(target.ApiKey) ? new ApiKeyCredential(target.ApiKey.Trim())
            : saved is not null ? await ResolveCredentialAsync(saved, ct).ConfigureAwait(false)
            : null;
        return (provider, credential);
    }

    /// <summary>草稿在请求里用的供应商 id(只出现在 Anthropic metadata 之外的地方:传输装饰按 id 认不出它,就不做任何装饰)。</summary>
    private const string DraftProviderId = "draft";

    /// <summary>
    /// 拉模型列表的核心。<paramref name="bothFamilies"/> 为 false(导入)时先试这家协议那一族,失败或为空才试另一族;为 true(调试)时两族并行都试。
    /// 合并去重保持「先这家协议那一族、族内上游顺序」。调用方取消照常抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    private async Task<ModelListing> ListModelsAsync(ProviderRecord provider, ProviderCredential? credential, bool bothFamilies, CancellationToken ct)
    {
        var primary = provider.Protocol == WireProtocol.Anthropic ? WireProtocol.Anthropic : WireProtocol.OpenAiChat;
        var secondary = primary == WireProtocol.Anthropic ? WireProtocol.OpenAiChat : WireProtocol.Anthropic;
        var secrets = SecretStrings(credential);

        List<(ModelListAttempt Attempt, IReadOnlyList<FetchedModel> Models)> results;
        if (bothFamilies)
        {
            var both = await Task.WhenAll(TryListAsync(provider, credential, primary, secrets, ct), TryListAsync(provider, credential, secondary, secrets, ct)).ConfigureAwait(false);
            results = [.. both];
        }
        else
        {
            results = [await TryListAsync(provider, credential, primary, secrets, ct).ConfigureAwait(false)];
            if (results[0].Models.Count == 0)
            {
                results.Add(await TryListAsync(provider, credential, secondary, secrets, ct).ConfigureAwait(false));
            }
        }

        var merged = new List<ListedModel>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (attempt, models) in results)
        {
            foreach (var model in models)
            {
                if (index.TryGetValue(model.Id, out var at))
                {
                    var existing = merged[at];
                    merged[at] = existing with { DisplayName = existing.DisplayName ?? model.DisplayName, Families = [.. existing.Families, attempt.Family] };
                    continue;
                }

                index[model.Id] = merged.Count;
                merged.Add(new ListedModel(model.Id, model.DisplayName, [attempt.Family]));
            }
        }

        return new ModelListing([.. results.Select(r => r.Attempt)], merged);
    }

    /// <summary>一族列表端点试一次,永不抛业务异常(结局在 attempt 里)。</summary>
    private async Task<(ModelListAttempt Attempt, IReadOnlyList<FetchedModel> Models)> TryListAsync(
        ProviderRecord provider, ProviderCredential? credential, WireProtocol family, string[] secrets, CancellationToken ct)
    {
        var config = new ProviderConfig(provider.Id, provider.Name, provider.BaseUrl, family, provider.Priority, provider.Enabled, provider.CredentialRef, provider.CacheTtl);
        ModelList.ListRequest request;
        try
        {
            request = ModelList.RequestFor(config, family, credential, _transport, new RequestContext("model-list"));
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException or InvalidOperationException)
        {
            return (new ModelListAttempt(family, provider.BaseUrl, false, null, 0, ProviderErrorKeys.FetchFailed, Scrub(e.Message, secrets)), []);
        }

        var url = Scrub(request.Url, secrets);
        try
        {
            using var response = await SendListAsync(request, provider, ct).ConfigureAwait(false);
            var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = ModelList.Classify((int)response.StatusCode, body);
                return (new ModelListAttempt(family, url, false, failure.Status, 0, failure.Key, Scrub(failure.Detail, secrets)), []);
            }

            JsonElement payload;
            try
            {
                payload = JsonSerializer.Deserialize(body, ModelsDevElementContext.Default.JsonElement);
            }
            catch (JsonException)
            {
                payload = default;
            }

            var models = ModelList.Parse(family, payload);
            return models.Count == 0
                ? (new ModelListAttempt(family, url, false, (int)response.StatusCode, 0, ProviderErrorKeys.FetchEmpty, null), [])
                : (new ModelListAttempt(family, url, true, (int)response.StatusCode, models.Count, null, null), models);
        }
        catch (ProviderException e)
        {
            var status = e.Parameters.TryGetValue("status", out var s) && int.TryParse(s, out var parsed) ? parsed : (int?)null;
            return (new ModelListAttempt(family, url, false, status, 0, e.Key, Scrub(e.Parameters.GetValueOrDefault("detail"), secrets)), []);
        }
    }

    /// <summary>凭据里的秘密串(上游的错误正文可能原样回显我们发过去的 key)。</summary>
    private static string[] SecretStrings(ProviderCredential? credential) => (credential switch
    {
        ApiKeyCredential k => new[] { k.ApiKey },
        OAuthCredential o => [o.AccessToken, o.RefreshToken],
        SignatureCredential s => [s.SecretKey, s.SessionToken ?? ""],
        _ => [],
    }).Where(s => s.Length >= 4).OrderByDescending(s => s.Length).ToArray();

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(text))]
    private static string? Scrub(string? text, string[] secrets)
    {
        if (text is null)
        {
            return null;
        }

        foreach (var secret in secrets)
        {
            text = text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        }

        return ModelList.RedactSecrets(text);
    }
}
