using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog;

/// <summary>
/// 托管的 NextCoWork 供应商(<see cref="ProviderRefs.ManagedProviderId"/>,2.x <c>CLIENT_PROVIDER_ID</c>)的固定部分:身份、出厂协议、以及
/// 「哪些模型强制走 Anthropic 协议」的规则。
/// <para>
/// 这家供应商<b>不进 providers.json</b>,也没有手填的密钥:登录之后由 <see cref="ProviderService.SyncManagedAsync"/> 用账户委托令牌拉 <c>/v1/models</c>,
/// 同步成内存里的别名;退出登录(委托令牌撤销)就整家消失。身份字段(名字、地址、凭据 ref)归登录流程,不接受任何地方改——
/// 访问令牌是发往平台域名的凭据,地址一旦能被改走就等于把它送去别处。
/// </para>
/// <para>
/// 协议规则只有 <see cref="ProtocolFor"/> 这一处:Claude 系在平台网关上走 Anthropic 线形(<c>/v1/messages</c>),否则继承供应商的 Responses,
/// Claude 的思考档位(<c>thinking.budget_tokens</c>)和提示词缓存语义在兼容层里会被丢掉,而且零报错(2.x 登录同步的事故)。
/// </para>
/// </summary>
public static class ManagedProvider
{
    public const string Name = "NextCoWork";

    /// <summary>平台网关地址。带 <c>/v1</c>:<see cref="WireProtocols.JoinUrl"/> 对 Anthropic 的 <c>/v1/messages</c> 会去重,Responses 的 <c>/responses</c> 直接接上。</summary>
    public const string DefaultBaseUrl = "https://nextco.work/v1";

    /// <summary>出厂协议:平台网关主推 Responses(带完整的 <c>reasoning</c> 往返),chat/completions 只是兼容层。</summary>
    public const WireProtocol DefaultProtocol = WireProtocol.OpenAiResponses;

    /// <summary>排在所有用户自己配的供应商前面(越小越靠前)。</summary>
    public const int Priority = 1;

    public static ProviderRecord Record(string? baseUrl = null) =>
        new(ProviderRefs.ManagedProviderId, Name, DefaultProtocol, baseUrl ?? DefaultBaseUrl, Priority);

    /// <summary>
    /// 这个上游模型 id 在托管供应商下被强制使用的协议;不强制(继承供应商的出厂协议)为 null。
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
/// 托管供应商的凭据来源:<c>provider:nextcowork</c> 这个 ref 读到的永远是账户委托令牌(<paramref name="token"/>,只在内存里,过期 / 没交付 = null),
/// 不落盘、不读库、不接受写入(<see cref="ProviderService"/> 已经在入口拒绝手动改;这里是第二道:任何写 / 删都是空操作,库里不会留下令牌)。
/// 其余 ref 全部原样交给 <paramref name="inner"/>。
/// </summary>
public sealed class ManagedCredentialVault(ICredentialVault inner, Func<string?> token) : ICredentialVault
{
    private static readonly string ManagedRef = ProviderRefs.ForProvider(ProviderRefs.ManagedProviderId);

    public ValueTask<string?> GetAsync(string credentialRef, CancellationToken ct) =>
        credentialRef == ManagedRef ? ValueTask.FromResult(token()) : inner.GetAsync(credentialRef, ct);

    public ValueTask SetAsync(string credentialRef, string value, CancellationToken ct) =>
        credentialRef == ManagedRef ? ValueTask.CompletedTask : inner.SetAsync(credentialRef, value, ct);

    public ValueTask RemoveAsync(string credentialRef, CancellationToken ct) =>
        credentialRef == ManagedRef ? ValueTask.CompletedTask : inner.RemoveAsync(credentialRef, ct);

    public void Remove(string credentialRef)
    {
        if (credentialRef != ManagedRef)
        {
            inner.Remove(credentialRef);
        }
    }
}
