using Ncw.Protocol.Methods;

namespace Ncw.Client.Actions;

// 设置 / 管理页的 ViewModel 依赖的意图接口(IFilesCommands 的同一个做法):真实现是对应的 *Actions,
// 单测里换成脚本化的假实现,不用起设备连接就能测加载 / 空 / 错误 / 角色禁用 / 二次确认这些状态机。

/// <summary>设备 / 项目设置(<see cref="SettingsActions"/>)。</summary>
public interface ISettingsCommands
{
    Task<ActionResult<DeviceSettingsState>> UpdateAsync(string deviceId, DeviceSettingsPatchDto patch, CancellationToken ct = default);

    Task<ActionResult<ProjectSettingsDto>> GetProjectAsync(string deviceId, string workspaceId, CancellationToken ct = default);

    Task<ActionResult<ProjectSettingsDto>> UpdateProjectAsync(string deviceId, ProjectSettingsPatchDto patch, CancellationToken ct = default);

    Task<ActionResult<DeviceSettingsState>> SetProxyPasswordAsync(string deviceId, string password, CancellationToken ct = default);
}

/// <summary>Skills(<see cref="SkillsActions"/>)。</summary>
public interface ISkillsCommands
{
    Task<ActionResult<SkillsListResult>> ListAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default);

    Task<ActionResult<SkillsDiagnosticsResult>> DiagnosticsAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default);

    Task<ActionResult<SkillsListResult>> SetGlobalEnabledAsync(string deviceId, string skillId, bool enabled, string? workspaceId = null, CancellationToken ct = default);

    Task<ActionResult<SkillsListResult>> SetWorkspaceActiveAsync(string deviceId, string workspaceId, string skillId, bool active, CancellationToken ct = default);

    Task<ActionResult<SkillsInstallResult>> InstallZipAsync(string deviceId, byte[] zip, string? fileName, string scope, string? workspaceId = null, CancellationToken ct = default);

    Task<ActionResult<SkillsInstallResult>> InstallMarketAsync(string deviceId, string slug, string? version, string scope, string? workspaceId = null, CancellationToken ct = default);

    Task<ActionResult<SkillsListResult>> UninstallAsync(string deviceId, string skillId, string? scope = null, string? workspaceId = null, CancellationToken ct = default);

    Task<ActionResult<SkillsMarketListResult>> MarketListAsync(string deviceId, string? query, string? category, CancellationToken ct = default);

    Task<ActionResult<SkillsMarketCategoriesResult>> MarketCategoriesAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<SkillMarketDetailDto>> MarketDetailAsync(string deviceId, string slug, CancellationToken ct = default);
}

/// <summary>斜杠命令的管理(<see cref="CommandsActions"/> 的管理子集)。</summary>
public interface ICommandsAdminCommands
{
    Task<ActionResult<CommandsListAllResult>> ListAllAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default);

    Task<ActionResult<CommandsDiagnosticsResult>> DiagnosticsAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default);

    Task<ActionResult<CommandsListAllResult>> SetEnabledAsync(string deviceId, string name, bool enabled, string? workspaceId = null, CancellationToken ct = default);
}

/// <summary>Hooks(<see cref="HooksActions"/>)。</summary>
public interface IHooksCommands
{
    Task<ActionResult<HooksListResult>> ListAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default);

    Task<ActionResult<HooksDiagnosticsResult>> DiagnosticsAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default);

    Task<ActionResult<HooksSaveResult>> SaveAsync(string deviceId, string scope, string? workspaceId, HookUpsertDto hook, CancellationToken ct = default);

    Task<ActionResult<Unit>> DeleteAsync(string deviceId, string scope, string? workspaceId, string id, CancellationToken ct = default);

    Task<ActionResult<Unit>> SetEnabledAsync(string deviceId, string scope, string? workspaceId, string id, bool enabled, CancellationToken ct = default);

    Task<ActionResult<HookRunReportDto>> TestAsync(string deviceId, string scope, string? workspaceId, HookUpsertDto draft, CancellationToken ct = default);
}

/// <summary>MCP(<see cref="McpActions"/>)。状态经 <c>mcp</c> 主题进 Store;这里的返回值只用来报告失败和一次性结果。</summary>
public interface IMcpCommands
{
    Task<ActionResult<McpTopicState>> ListAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<McpServerStatusDto>> UpsertAsync(string deviceId, McpServerConfigDto config, CancellationToken ct = default);

    Task<ActionResult<McpRemoveResult>> RemoveAsync(string deviceId, string serverId, CancellationToken ct = default);

    Task<ActionResult<McpServerStatusDto>> SetEnabledAsync(string deviceId, string serverId, bool enabled, CancellationToken ct = default);

    Task<ActionResult<McpServerStatusDto>> ReconnectAsync(string deviceId, string serverId, CancellationToken ct = default);

    Task<ActionResult<McpTestResultDto>> TestConnectionAsync(string deviceId, string serverId, McpServerConfigDto? draft = null, CancellationToken ct = default);

    Task<ActionResult<McpSecretsInfoDto>> SetSecretsAsync(string deviceId, string serverId, IReadOnlyDictionary<string, string> values, CancellationToken ct = default);

    Task<ActionResult<McpSecretsInfoDto>> GetSecretsInfoAsync(string deviceId, string serverId, CancellationToken ct = default);

    Task<ActionResult<McpResourcesResult>> ListResourcesAsync(string deviceId, string serverId, CancellationToken ct = default);

    Task<ActionResult<McpPromptsResult>> ListPromptsAsync(string deviceId, string serverId, CancellationToken ct = default);
}

/// <summary>用量查询(<see cref="UsageActions"/>)。</summary>
public interface IUsageCommands
{
    Task<ActionResult<UsageSummaryDto>> GetSummaryAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default);

    Task<ActionResult<UsageDimensionStatsResult>> GetProviderStatsAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default);

    Task<ActionResult<UsageDimensionStatsResult>> GetModelStatsAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default);

    Task<ActionResult<UsageDailySeriesResult>> GetDailySeriesAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default);
}

/// <summary>存储维护(<see cref="StorageActions"/>)。</summary>
public interface IStorageCommands
{
    Task<ActionResult<StorageStatsDto>> GetStatsAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<StorageStatsDto>> VacuumAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<BackupStatusDto>> GetBackupStatusAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<BackupStatusDto>> CreateBackupAsync(string deviceId, bool manual = true, CancellationToken ct = default);

    Task<ActionResult<RestorePreviewDto>> PreviewRestoreAsync(string deviceId, string path, CancellationToken ct = default);

    Task<ActionResult<BackupStatusDto>> RestoreBackupAsync(string deviceId, string path, string confirmToken, CancellationToken ct = default);

    Task<ActionResult<CleanupPreviewDto>> PreviewCleanupAsync(string deviceId, string kind, string? age = null, CancellationToken ct = default);

    Task<ActionResult<CleanupResultDto>> CleanupAttachmentsAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<CleanupResultDto>> ExecuteCleanupAsync(string deviceId, string kind, string? age, string confirmToken, CancellationToken ct = default);

    Task<ActionResult<ExportInfoDto>> ExportToAsync(string deviceId, Stream destination, CancellationToken ct = default);

    Task<ActionResult<ImportPreviewDto>> ImportFromAsync(string deviceId, Stream source, CancellationToken ct = default);

    Task<ActionResult<ImportResultDto>> ApplyImportAsync(string deviceId, string uploadId, string confirmToken, CancellationToken ct = default);

    Task<ActionResult<Unit>> DiscardImportAsync(string deviceId, string uploadId, CancellationToken ct = default);
}

/// <summary>开放网关(<see cref="GatewayActions"/>)。</summary>
public interface IGatewayCommands
{
    Task<ActionResult<GatewayStatusDto>> GetStatusAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<GatewayStatusDto>> SetEnabledAsync(string deviceId, bool enabled, CancellationToken ct = default);

    Task<ActionResult<GatewayKeyDto>> GetKeyAsync(string deviceId, string role, CancellationToken ct = default);

    Task<ActionResult<GatewayKeyDto>> RotateKeyAsync(string deviceId, string role, CancellationToken ct = default);

    Task<ActionResult<GatewayAddressesResult>> GetAddressesAsync(string deviceId, CancellationToken ct = default);

    Task<ActionResult<GatewayStatusDto>> ResetHealthAsync(string deviceId, string? providerId, CancellationToken ct = default);
}

/// <summary>定时任务(<see cref="ScheduleActions"/>)。任务与运行经 <c>schedules</c> 主题进 Store。</summary>
public interface ISchedulesCommands
{
    Task<ActionResult<ScheduledTaskDto>> CreateAsync(string deviceId, ScheduledTaskInputDto input, CancellationToken ct = default);

    Task<ActionResult<ScheduledTaskDto>> UpdateAsync(string deviceId, string taskId, ScheduledTaskPatchDto patch, CancellationToken ct = default);

    Task<ActionResult<Unit>> DeleteAsync(string deviceId, string taskId, CancellationToken ct = default);

    Task<ActionResult<ScheduledTaskDto>> SetEnabledAsync(string deviceId, string taskId, bool enabled, CancellationToken ct = default);

    Task<ActionResult<ScheduledRunDto>> RunNowAsync(string deviceId, string taskId, CancellationToken ct = default);

    Task<ActionResult<ScheduledRunsResult>> ListRunsAsync(string deviceId, ScheduledRunsQuery query, CancellationToken ct = default);

    Task<ActionResult<ScheduledRunDto>> GetRunAsync(string deviceId, string runId, CancellationToken ct = default);

    Task<ActionResult<Unit>> DeleteRunAsync(string deviceId, string runId, CancellationToken ct = default);
}
