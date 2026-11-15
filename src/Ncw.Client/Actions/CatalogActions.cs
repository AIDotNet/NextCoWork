using Ncw.Client.Devices;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 模型目录相关的用户意图(AGENTS.md §3 规则 4:一个意图一个方法)。目录的真源在设备的 <c>ModelCatalogService</c>,这里只经协议读写;
/// 状态变化(目录更新、覆盖变了)经 <c>catalog</c> 主题进入 <see cref="Stores.ClientStores.Catalog"/>,这里不直接改 Store。
/// 目录本身很大,<see cref="QueryAsync"/> 按需拉、结果直接交给调用方(界面的纯 UI 状态),不进 Store。
/// </summary>
public sealed class CatalogActions(DeviceRegistry devices)
{
    /// <summary>按条件查目录里的模型(分页)。viewer 可用。</summary>
    public Task<ActionResult<CatalogQueryResult>> QueryAsync(string deviceId, CatalogQueryParams query, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(CatalogMethods.Query, query, ct));

    /// <summary>取一个模型的详情。viewer 可用。</summary>
    public Task<ActionResult<CatalogGetResult>> GetModelAsync(string deviceId, string catalogProviderId, string modelId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(CatalogMethods.Get, new CatalogGetParams(catalogProviderId, modelId), ct));

    /// <summary>「立即更新」:马上去 models.dev 检查一次。结局(更新了 / 没变 / 被拒 / 失败)在结果里;目录变了会经主题推到 Store。</summary>
    public Task<ActionResult<CatalogRefreshResultDto>> RefreshAsync(string deviceId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(CatalogMethods.Refresh, Unit.Value, ct, idem: Idem("refresh")));

    /// <summary>改名 / 自定义窗口 / 能力 / 价格(内容全空 = 撤销这条覆盖)。</summary>
    public Task<ActionResult<CatalogTopicState>> SetOverrideAsync(string deviceId, ModelOverrideDto item, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(CatalogMethods.OverrideSet, item, ct, idem: Idem("override.set")));

    /// <summary>撤销覆盖(恢复同步数据)。</summary>
    public Task<ActionResult<CatalogTopicState>> RemoveOverrideAsync(string deviceId, string? providerId, string modelId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(CatalogMethods.OverrideRemove, new CatalogModelRefParams(providerId, modelId), ct, idem: Idem("override.remove")));

    /// <summary>从目录里隐藏一个模型。</summary>
    public Task<ActionResult<CatalogTopicState>> DeleteModelAsync(string deviceId, string? providerId, string modelId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(CatalogMethods.ModelDelete, new CatalogModelRefParams(providerId, modelId), ct, idem: Idem("model.delete")));

    public Task<ActionResult<CatalogTopicState>> RestoreModelAsync(string deviceId, string? providerId, string modelId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(CatalogMethods.ModelRestore, new CatalogModelRefParams(providerId, modelId), ct, idem: Idem("model.restore")));

    public Task<ActionResult<CatalogSettingsDto>> GetSettingsAsync(string deviceId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(CatalogMethods.SettingsGet, Unit.Value, ct));

    /// <summary>自动更新开关与间隔。</summary>
    public Task<ActionResult<CatalogSettingsDto>> SetSettingsAsync(string deviceId, CatalogSettingsDto settings, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(CatalogMethods.SettingsSet, settings, ct, idem: Idem("settings.set")));

    private static string Idem(string what) => $"catalog.{what}:{Guid.NewGuid():N}";

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
