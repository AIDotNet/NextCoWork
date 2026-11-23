using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Media;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog;

/// <summary>
/// 托管的 NextCoWork 供应商(文本 <c>nextcowork</c>、图片 <c>nextcowork-image</c>、视频 <c>nextcowork-video</c>、音频 <c>nextcowork-audio</c>)的固定部分:
/// 身份、出厂协议、以及文本通道「哪些模型强制走 Anthropic 协议」的规则。
/// <para>
/// 四家内置供应商<b>都不进 providers.json</b>,也没有手填的密钥:登录 NextCoWork 后由 <see cref="ProviderService.SyncManagedAsync"/>
/// 用账户委托令牌拉 <c>/v1/models</c>,按类型过滤后分别同步到四家内存供应商;退出登录(委托令牌撤销)就整体消失。
/// 身份字段(名字、地址、凭据 ref)归登录流程,不接受任何地方改——访问令牌是发往平台域名的凭据,地址一旦能被改走就等于把它送去别处。
/// </para>
/// <para>
/// 协议规则只有 <see cref="ProtocolFor"/> 这一处:文本通道里的 Claude 系在平台网关上走 Anthropic 线形(<c>/v1/messages</c>),
/// 否则继承供应商的 Responses,避免思考档位和提示词缓存语义在兼容层里静默丢失。
/// </para>
/// </summary>
public static class ManagedProvider
{
    public const string Name = "NextCoWork";

    /// <summary>平台网关地址。带 <c>/v1</c>:<see cref="WireProtocols.JoinUrl"/> 对 Anthropic 的 <c>/v1/messages</c> 会去重,其余路径直接接上。</summary>
    public const string DefaultBaseUrl = "https://nextco.work/v1";

    /// <summary>文本通道出厂协议:平台网关主推 Responses(带完整的 <c>reasoning</c> 往返),chat/completions 只是兼容层。</summary>
    public const WireProtocol DefaultProtocol = WireProtocol.OpenAiResponses;

    /// <summary>排在所有用户自己配的供应商前面(越小越靠前)。</summary>
    public const int Priority = 1;

    /// <summary>每种类型对应的平台出厂协议。</summary>
    public static ProviderProtocol DefaultProtocolFor(ProviderKind kind) => kind switch
    {
        ProviderKind.Text => ProviderProtocol.OpenAiResponses,
        ProviderKind.Image => ProviderProtocol.OpenAiImages,
        ProviderKind.Video => ProviderProtocol.OpenAiVideos,
        ProviderKind.Audio => ProviderProtocol.OpenAiAudio,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static ProviderRecord Record(string? baseUrl = null) => Record(ProviderKind.Text, baseUrl);

    public static ProviderRecord Record(ProviderKind kind, string? baseUrl = null) =>
        new(ProviderRefs.ManagedIdFor(kind), Name, DefaultProtocolFor(kind), baseUrl ?? DefaultBaseUrl, Priority, Enabled: true, Kind: kind);

    /// <summary>登录 NextCoWork 后自动提供的四家内置供应商(文本、图片、视频、音频)。</summary>
    public static IReadOnlyList<ProviderRecord> Records(string? baseUrl = null) =>
    [
        Record(ProviderKind.Text, baseUrl),
        Record(ProviderKind.Image, baseUrl),
        Record(ProviderKind.Video, baseUrl),
        Record(ProviderKind.Audio, baseUrl),
    ];

    /// <summary>
    /// 这个上游模型 id 在托管文本供应商下被强制使用的协议;不强制(继承供应商的出厂协议)为 null。
    /// 认 Claude 系:<c>claude-opus-4-5</c>、<c>anthropic/claude-sonnet-4</c>、<c>us.anthropic.claude-3-5-haiku-v1:0</c>,不分大小写。
    /// </summary>
    public static WireProtocol? ProtocolFor(string upstreamModel) => IsClaude(upstreamModel) ? WireProtocol.Anthropic : null;

    public static bool IsClaude(string upstreamModel)
    {
        // 网关前缀(openrouter 式的 vendor/model)只看最后一段;Bedrock 式的 <region>.anthropic.claude-… 点分前缀用「.claude」认。
        var slash = upstreamModel.LastIndexOf('/');
        var segment = slash < 0 ? upstreamModel : upstreamModel[(slash + 1)..];
        return segment.StartsWith("claude", StringComparison.OrdinalIgnoreCase) || segment.Contains(".claude", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 托管供应商的凭据来源:<c>provider:nextcowork*</c>(文本 / 图片 / 视频 / 音频四家内置供应商)读到的永远是账户委托令牌
/// (<paramref name="token"/>,只在内存里,过期 / 没交付 = null),不落盘、不读库、不接受写入(<see cref="ProviderService"/> 已经在入口拒绝手动改;
/// 这里是第二道:任何写 / 删都是空操作,库里不会留下令牌)。其余 ref 全部原样交给 <paramref name="inner"/>。
/// </summary>
public sealed class ManagedCredentialVault(ICredentialVault inner, Func<string?> token) : ICredentialVault
{
    private static readonly HashSet<string> ManagedRefs =
        ProviderRefs.ManagedProviderIds.Select(ProviderRefs.ForProvider).ToHashSet(StringComparer.Ordinal);

    public ValueTask<string?> GetAsync(string credentialRef, CancellationToken ct) =>
        ManagedRefs.Contains(credentialRef) ? ValueTask.FromResult(token()) : inner.GetAsync(credentialRef, ct);

    public ValueTask SetAsync(string credentialRef, string value, CancellationToken ct) =>
        ManagedRefs.Contains(credentialRef) ? ValueTask.CompletedTask : inner.SetAsync(credentialRef, value, ct);

    public ValueTask RemoveAsync(string credentialRef, CancellationToken ct) =>
        ManagedRefs.Contains(credentialRef) ? ValueTask.CompletedTask : inner.RemoveAsync(credentialRef, ct);

    public void Remove(string credentialRef)
    {
        if (!ManagedRefs.Contains(credentialRef))
        {
            inner.Remove(credentialRef);
        }
    }
}

/// <summary>
/// 托管 NextCoWork 供应商的传输装饰(移植自 2.x <c>kernel/upstream/transport.ts</c> 的 <c>platformLoginAuth</c>):
/// 托管供应商(<c>nextcowork*</c>)的凭据是账户登录 / 委托令牌(JWT),不是平台 API Key。
/// 平台网关按请求头分流:<c>x-api-key</c> 去查 API Key 表,<c>Authorization: Bearer</c> 才验登录态;
/// 实测只要 <c>x-api-key</c> 存在且非空就会落进 API Key 分支返回 401,即使同时带了合法的 Bearer 也一样。
/// 因此对托管供应商必须写 <c>Authorization: Bearer &lt;token&gt;</c> 并删掉 <c>x-api-key</c>(Claude 系强制走 Anthropic 协议时编码器默认发 <c>x-api-key</c>);
/// 其余供应商与非 API Key 形态凭据原样透传。
/// </summary>
public sealed class ManagedProviderTransport(ITransportDecorator? inner = null) : ITransportDecorator
{
    public static readonly ManagedProviderTransport Instance = new();

    private static readonly IReadOnlyList<string> DropXApiKey = ["x-api-key"];

    public TransportDecoration Decorate(ProviderConfig provider, WireProtocol protocol, ProviderCredential credential, RequestContext context)
    {
        var baseDecoration = inner?.Decorate(provider, protocol, credential, context) ?? new TransportDecoration();
        if (credential is not ApiKeyCredential key || !ProviderRefs.IsManagedProviderId(provider.Id) || baseDecoration.Signer is not null)
        {
            return baseDecoration;
        }

        var headers = baseDecoration.Headers is { Count: > 0 } existing
            ? new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        headers["authorization"] = "Bearer " + key.ApiKey;

        IReadOnlyList<string> dropHeaders = baseDecoration.DropHeaders is { Count: > 0 } existingDrops
            ? existingDrops.Contains("x-api-key", StringComparer.OrdinalIgnoreCase) ? existingDrops : [.. existingDrops, "x-api-key"]
            : DropXApiKey;

        return baseDecoration with { Headers = headers, DropHeaders = dropHeaders };
    }
}
