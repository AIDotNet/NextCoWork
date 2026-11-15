using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 插件设置页依赖的意图接口(<see cref="ISkillsCommands"/> 的同一个做法):真实现是 <see cref="PluginsActions"/>,单测里换成脚本化的假实现,
/// 不用起设备连接就能测加载 / 空 / 错误 / 角色禁用 / 二次确认 / authRequired 这些状态机。
/// </summary>
public interface IPluginsCommands
{
    Task<ActionResult<PluginsTopicState>> ListAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<PluginsTopicState>> SetEnabledAsync(string deviceId, string pluginId, bool enabled, CancellationToken ct = default);

    Task<ActionResult<PluginsTopicState>> UninstallAsync(string deviceId, string pluginId, CancellationToken ct = default);

    Task<ActionResult<PluginsInstallResult>> InstallPackageAsync(string deviceId, byte[] zip, string? fileName, CancellationToken ct = default);

    Task<ActionResult<PluginsInstallResult>> InstallMarketAsync(string deviceId, string slug, string? version, CancellationToken ct = default);

    Task<ActionResult<PluginsTopicState>> GrantAsync(string deviceId, string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default);

    Task<ActionResult<PluginsTopicState>> RevokeAsync(string deviceId, string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default);

    Task<ActionResult<PluginsActivityResult>> ActivityAsync(string deviceId, string? pluginId, CancellationToken ct = default);

    Task<ActionResult<Unit>> RunCommandAsync(string deviceId, string pluginId, string commandId, string? workspaceId = null, string? argsJson = null, CancellationToken ct = default);

    Task<ActionResult<PluginsCardActionResult>> CardActionAsync(string deviceId, string pluginId, string callId, string actionId, string? valueJson = null, CancellationToken ct = default);

    Task<ActionResult<Unit>> ReplyInteractionAsync(string deviceId, string requestId, bool? confirmed = null, string? text = null, string? pickedId = null, CancellationToken ct = default);

    Task<ActionResult<PluginsMarketListResult>> MarketListAsync(string deviceId, string? query, string? category, int page = 1, CancellationToken ct = default);

    Task<ActionResult<PluginsMarketCategoriesResult>> MarketCategoriesAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<PluginMarketDetailDto>> MarketDetailAsync(string deviceId, string slug, CancellationToken ct = default);

    Task<ActionResult<PluginsUpdatesResult>> CheckUpdatesAsync(string deviceId, bool force = false, CancellationToken ct = default);

    Task<ActionResult<PluginsUpdateAllResult>> UpdateAllAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<PluginsConfigurationResult>> GetConfigurationAsync(string deviceId, string pluginId, CancellationToken ct = default);

    Task<ActionResult<PluginsConfigurationResult>> SetConfigurationAsync(string deviceId, string pluginId, string key, PluginConfigValueDto? value, CancellationToken ct = default);
}

/// <summary>没有设备连接可用时(没接线的外壳、预览)的实现:一律是「设备不可用」。</summary>
public sealed class UnavailablePluginsCommands : IPluginsCommands
{
    public static readonly UnavailablePluginsCommands Instance = new();

    private static Task<ActionResult<T>> Down<T>() => Task.FromResult(ActionResult<T>.NotConnectedResult());

    public Task<ActionResult<PluginsTopicState>> ListAsync(string deviceId, CancellationToken ct = default) => Down<PluginsTopicState>();

    public Task<ActionResult<PluginsTopicState>> SetEnabledAsync(string deviceId, string pluginId, bool enabled, CancellationToken ct = default) => Down<PluginsTopicState>();

    public Task<ActionResult<PluginsTopicState>> UninstallAsync(string deviceId, string pluginId, CancellationToken ct = default) => Down<PluginsTopicState>();

    public Task<ActionResult<PluginsInstallResult>> InstallPackageAsync(string deviceId, byte[] zip, string? fileName, CancellationToken ct = default) => Down<PluginsInstallResult>();

    public Task<ActionResult<PluginsInstallResult>> InstallMarketAsync(string deviceId, string slug, string? version, CancellationToken ct = default) => Down<PluginsInstallResult>();

    public Task<ActionResult<PluginsTopicState>> GrantAsync(string deviceId, string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default) => Down<PluginsTopicState>();

    public Task<ActionResult<PluginsTopicState>> RevokeAsync(string deviceId, string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default) => Down<PluginsTopicState>();

    public Task<ActionResult<PluginsActivityResult>> ActivityAsync(string deviceId, string? pluginId, CancellationToken ct = default) => Down<PluginsActivityResult>();

    public Task<ActionResult<Unit>> RunCommandAsync(string deviceId, string pluginId, string commandId, string? workspaceId = null, string? argsJson = null, CancellationToken ct = default) => Down<Unit>();

    public Task<ActionResult<PluginsCardActionResult>> CardActionAsync(string deviceId, string pluginId, string callId, string actionId, string? valueJson = null, CancellationToken ct = default) => Down<PluginsCardActionResult>();

    public Task<ActionResult<Unit>> ReplyInteractionAsync(string deviceId, string requestId, bool? confirmed = null, string? text = null, string? pickedId = null, CancellationToken ct = default) => Down<Unit>();

    public Task<ActionResult<PluginsMarketListResult>> MarketListAsync(string deviceId, string? query, string? category, int page = 1, CancellationToken ct = default) => Down<PluginsMarketListResult>();

    public Task<ActionResult<PluginsMarketCategoriesResult>> MarketCategoriesAsync(string deviceId, CancellationToken ct = default) => Down<PluginsMarketCategoriesResult>();

    public Task<ActionResult<PluginMarketDetailDto>> MarketDetailAsync(string deviceId, string slug, CancellationToken ct = default) => Down<PluginMarketDetailDto>();

    public Task<ActionResult<PluginsUpdatesResult>> CheckUpdatesAsync(string deviceId, bool force = false, CancellationToken ct = default) => Down<PluginsUpdatesResult>();

    public Task<ActionResult<PluginsUpdateAllResult>> UpdateAllAsync(string deviceId, CancellationToken ct = default) => Down<PluginsUpdateAllResult>();

    public Task<ActionResult<PluginsConfigurationResult>> GetConfigurationAsync(string deviceId, string pluginId, CancellationToken ct = default) => Down<PluginsConfigurationResult>();

    public Task<ActionResult<PluginsConfigurationResult>> SetConfigurationAsync(string deviceId, string pluginId, string key, PluginConfigValueDto? value, CancellationToken ct = default) => Down<PluginsConfigurationResult>();
}
