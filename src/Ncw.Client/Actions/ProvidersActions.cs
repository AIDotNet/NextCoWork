using Ncw.Client.Devices;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 供应商相关的用户意图(规则 4:一个意图一个方法)。真源在设备的 <c>ProviderService</c>;配置变化经 <c>providers</c> 主题进入
/// <see cref="Stores.ClientStores.Providers"/>。★ 凭据:<see cref="SetApiKeyAsync"/> 只把 key 送出去、回来的只有摘要;
/// 明文只有 <see cref="RevealCredentialAsync"/>(仅 owner、仅本机)单次取回,调用方用完即弃,不落进任何 Store。
/// OAuth:授权页由控制端打开——界面订阅 Store 里的 <c>DeviceProviders.OAuth</c>,Waiting 阶段带授权 URL / 配对码;
/// <see cref="StartOAuthAsync"/> 等方法在登录结束(成功 / 失败 / 取消)时才返回,回调码经 <see cref="SubmitOAuthCodeAsync"/> 交回。
/// </summary>
public sealed class ProvidersActions(DeviceRegistry devices) : ViewModels.IProviderDiscoveryCommands, ViewModels.IProviderEditorCommands
{
    // ── 供应商 ──

    public Task<ActionResult<ProviderDto>> UpsertAsync(string deviceId, ProvidersUpsertParams provider, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.Upsert, provider, ct, idem: Idem("upsert")));

    public Task<ActionResult<Unit>> RemoveAsync(string deviceId, string providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.Remove, new ProviderRefParams(providerId), ct, idem: Idem("remove")));

    /// <summary>连通性测试(用模型列表端点探一次,不烧 token)。</summary>
    public Task<ActionResult<ProviderTestResultDto>> TestAsync(string deviceId, string providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.Test, new ProviderRefParams(providerId), ct));

    /// <summary>这个「模型 + 供应商」会落在哪条绑定上(和路由同一段规则;选择器据此标出不可用)。</summary>
    public Task<ActionResult<ModelResolutionDto>> ResolveAsync(string deviceId, string alias, string? providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.Resolve, new ProvidersResolveParams(alias, providerId), ct));

    // ── 凭据 ──

    /// <summary>存一把 API Key。设备只回摘要(有没有、后四位)。</summary>
    public Task<ActionResult<CredentialInfoDto>> SetApiKeyAsync(string deviceId, string providerId, string apiKey, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.CredentialSet, new CredentialSetParams(providerId, apiKey), ct, idem: Idem("credential.set")));

    public Task<ActionResult<CredentialInfoDto>> GetCredentialInfoAsync(string deviceId, string providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.CredentialInfo, new ProviderRefParams(providerId), ct));

    /// <summary>显式查看凭据明文(点了「查看」的那一次)。仅 owner、仅本机通道;远程连接得到 <see cref="ActionOutcome.Forbidden"/>。</summary>
    public Task<ActionResult<RevealedCredentialDto>> RevealCredentialAsync(string deviceId, string providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.CredentialReveal, new ProviderRefParams(providerId), ct));

    public Task<ActionResult<Unit>> RemoveCredentialAsync(string deviceId, string providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.CredentialRemove, new ProviderRefParams(providerId), ct, idem: Idem("credential.remove")));

    // ── 模型别名 ──

    /// <summary>从上游拉「这家有哪些模型」(导入弹窗)。</summary>
    public Task<ActionResult<FetchedModelsResult>> FetchModelsAsync(string deviceId, string providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.ModelsFetch, new ProviderRefParams(providerId), ct));

    /// <summary>整表替换这家的模型别名(导入弹窗点「更新列表」)。</summary>
    public Task<ActionResult<ModelAliasesResult>> SetAliasesAsync(string deviceId, string providerId, IReadOnlyList<string> upstreamModels, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.ModelsSetAliases, new SetAliasesParams(providerId, [.. upstreamModels]), ct, idem: Idem("models.setAliases")));

    public Task<ActionResult<ModelAliasDto>> UpdateModelAsync(string deviceId, ModelUpdateParams update, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.ModelsUpdate, update, ct, idem: Idem("models.update")));

    public Task<ActionResult<ModelAliasDto>> RenameModelAsync(string deviceId, string providerId, string alias, string nextAlias, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.ModelsRename, new ModelRenameParams(providerId, alias, nextAlias), ct, idem: Idem("models.rename")));

    public Task<ActionResult<Unit>> RemoveModelAsync(string deviceId, string providerId, string alias, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.ModelsRemove, new ModelRefParams(providerId, alias), ct, idem: Idem("models.remove")));

    // ── 账号池 ──

    public Task<ActionResult<AccountsResult>> RemoveAccountAsync(string deviceId, string providerId, string accountId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.AccountsRemove, new AccountRefParams(providerId, accountId), ct, idem: Idem("accounts.remove")));

    public Task<ActionResult<AccountsResult>> SetAccountEnabledAsync(string deviceId, string providerId, string accountId, bool enabled, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.AccountsSetEnabled, new AccountSetEnabledParams(providerId, accountId, enabled), ct, idem: Idem("accounts.setEnabled")));

    public Task<ActionResult<AccountsResult>> SetAccountLabelAsync(string deviceId, string providerId, string accountId, string label, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.AccountsSetLabel, new AccountSetLabelParams(providerId, accountId, label), ct, idem: Idem("accounts.setLabel")));

    public Task<ActionResult<AccountsResult>> SetCurrentAccountAsync(string deviceId, string providerId, string accountId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.AccountsSetCurrent, new AccountRefParams(providerId, accountId), ct, idem: Idem("accounts.setCurrent")));

    public Task<ActionResult<AccountsResult>> ReorderAccountsAsync(string deviceId, string providerId, IReadOnlyList<string> accountIds, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.AccountsReorder, new AccountsReorderParams(providerId, [.. accountIds]), ct, idem: Idem("accounts.reorder")));

    /// <summary>「立即解除限流」。</summary>
    public Task<ActionResult<AccountsResult>> ClearAccountLimitAsync(string deviceId, string providerId, string accountId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.AccountsClearLimit, new AccountRefParams(providerId, accountId), ct, idem: Idem("accounts.clearLimit")));

    /// <summary>手动停用到某个时刻。</summary>
    public Task<ActionResult<AccountsResult>> PauseAccountAsync(string deviceId, string providerId, string accountId, DateTimeOffset until, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.AccountsPause, new AccountPauseParams(providerId, accountId, until.ToUnixTimeMilliseconds()), ct, idem: Idem("accounts.pause")));

    // ── OAuth ──

    /// <summary>把这家登录成。登录结束才返回;期间的进度(授权 URL、配对码)在 Store 的 <c>OAuth</c> 里。</summary>
    public Task<ActionResult<CredentialInfoDto>> StartOAuthAsync(string deviceId, string providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.OAuthStart, new ProviderRefParams(providerId), ct, idem: Idem("oauth.start")));

    public Task<ActionResult<AccountsResult>> AddAccountAsync(string deviceId, string providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.OAuthAddAccount, new ProviderRefParams(providerId), ct, idem: Idem("oauth.addAccount")));

    public Task<ActionResult<AccountsResult>> ReauthAccountAsync(string deviceId, string providerId, string accountId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.OAuthReauth, new AccountRefParams(providerId, accountId), ct, idem: Idem("oauth.reauth")));

    /// <summary>退出登录(退出的是当前账号)。</summary>
    public Task<ActionResult<CredentialInfoDto>> SignOutAsync(string deviceId, string providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.OAuthSignOut, new ProviderRefParams(providerId), ct, idem: Idem("oauth.signOut")));

    public Task<ActionResult<Unit>> CancelOAuthAsync(string deviceId, string providerId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.OAuthCancel, new ProviderRefParams(providerId), ct, idem: Idem("oauth.cancel")));

    /// <summary>把授权页给的回调码交回设备(<c>NeedsPastedCode</c> 的登录)。</summary>
    public Task<ActionResult<Unit>> SubmitOAuthCodeAsync(string deviceId, string providerId, string code, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProvidersMethods.OAuthSubmitCode, new OAuthSubmitCodeParams(providerId, code), ct, idem: Idem("oauth.submitCode")));

    // ── 自动发现(后加的能力 providers.discovery)──

    /// <summary>设备支持自动发现(老设备不报这个能力就不调)。</summary>
    public bool SupportsDiscovery(string deviceId) =>
        devices.Find(deviceId) is { IsConnected: true } link && link.Features.Contains(Capabilities.ProviderDiscovery);

    /// <summary>在设备上找其他客户端配好的供应商(只读;结果里没有密钥明文)。</summary>
    public Task<ActionResult<DiscoveryScanResult>> ScanDiscoveryAsync(string deviceId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProviderDiscoveryMethods.Scan, Unit.Value, ct));

    /// <summary>导入勾选的候选(设备重新扫一遍,在设备上建供应商、存密钥)。</summary>
    public Task<ActionResult<DiscoveryImportResult>> ImportDiscoveredAsync(string deviceId, IReadOnlyList<string> candidateIds, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProviderDiscoveryMethods.Import, new DiscoveryImportParams([.. candidateIds]), ct, idem: Idem("discovery.import")));

    // ── 内置模版(后加的能力 providers.templates)──

    /// <summary>设备给内置模版(老设备不报这个能力:添加供应商只有「自定义」)。</summary>
    public bool SupportsTemplates(string deviceId) =>
        devices.Find(deviceId) is { IsConnected: true } link && link.Features.Contains(Capabilities.ProviderTemplates);

    public Task<ActionResult<ProviderTemplatesResult>> ListTemplatesAsync(string deviceId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(ProviderTemplatesMethods.List, Unit.Value, ct));

    private static string Idem(string what) => $"providers.{what}:{Guid.NewGuid():N}";

    private async Task<ActionResult<T>> Run<T>(string deviceId, Func<DeviceLink, Task<T>> call)
    {
        if (devices.Find(deviceId) is not { IsConnected: true } link)
        {
            return ActionResult<T>.NotConnected();
        }

        try
        {
            return ActionResult<T>.Success(await call(link).ConfigureAwait(false));
        }
        catch (ProtocolException e)
        {
            return ActionResult<T>.From(e);
        }
    }
}
