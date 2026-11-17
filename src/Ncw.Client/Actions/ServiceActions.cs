using Ncw.Client.Devices;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>M4 杂项服务各个 Actions 共用的调用壳:设备没连上 → <c>Unavailable</c>;协议错误 → 结构化结局(界面按 key 自己生成文字,Ncw.Client 不产出界面文字)。</summary>
internal static class ServiceCall
{
    public static string Idem(string area, string what) => $"{area}.{what}:{Guid.NewGuid():N}";

    public static async Task<ActionResult<T>> RunAsync<T>(DeviceRegistry devices, string deviceId, Func<DeviceLink, Task<T>> call)
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

/// <summary>
/// 设备 / 项目设置的用户意图(规则 4:一个意图一个方法)。真源在设备的 <c>SettingsService</c>;改完经 <c>settings</c> 主题进 <see cref="Stores.ClientStores.Settings"/>,这里不直接改 Store。
/// 「本应用」作用域的设置(外观、快捷键、通知)是 <c>UiPrefs</c>,不经设备。补丁是深合并:界面只传改了的那一项,所以连点两个同块的开关不会互相覆盖。
/// 设备 / 项目设置只有 owner 能写(改权限模式不是 member 的事,09 §4);代理密码只允许本机通道。
/// </summary>
public sealed class SettingsActions(DeviceRegistry devices) : ISettingsCommands
{
    public Task<ActionResult<DeviceSettingsState>> UpdateAsync(string deviceId, DeviceSettingsPatchDto patch, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SettingsMethods.Update, new SettingsUpdateParams(patch), ct, idem: ServiceCall.Idem("settings", "update")));

    public Task<ActionResult<ProjectSettingsDto>> GetProjectAsync(string deviceId, string workspaceId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SettingsMethods.ProjectGet, new ProjectSettingsRefParams(workspaceId), ct));

    public Task<ActionResult<ProjectSettingsDto>> UpdateProjectAsync(string deviceId, ProjectSettingsPatchDto patch, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SettingsMethods.ProjectUpdate, patch, ct, idem: ServiceCall.Idem("settings", "project.update")));

    /// <summary>设置 / 清掉(空串)代理密码。密码只送出去,回来的状态里只有「有没有」。</summary>
    public Task<ActionResult<DeviceSettingsState>> SetProxyPasswordAsync(string deviceId, string password, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SettingsMethods.ProxySetPassword, new ProxyPasswordParams(password), ct, idem: ServiceCall.Idem("settings", "proxy.setPassword")));
}

/// <summary>
/// 用量查询(只读,viewer 起)。数据来自设备上的库,不进 Store:界面把结果当纯 UI 状态用(统计页自己的状态)。
/// 费用的 <c>CostMicros</c> 为 null = 未知,绝不是 0;不同币种分开,绝不相加。
/// </summary>
public sealed class UsageActions(DeviceRegistry devices) : IUsageCommands
{
    public Task<ActionResult<UsageSummaryDto>> GetSummaryAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(UsageMethods.GetSummary, window, ct));

    public Task<ActionResult<UsageAttemptsResult>> GetSessionAttemptsAsync(string deviceId, string sessionId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(UsageMethods.GetSessionAttempts, new UsageSessionRefParams(sessionId), ct));

    public Task<ActionResult<UsageDimensionStatsResult>> GetProviderStatsAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(UsageMethods.GetProviderStats, window, ct));

    public Task<ActionResult<UsageDimensionStatsResult>> GetModelStatsAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(UsageMethods.GetModelStats, window, ct));

    public Task<ActionResult<UsageDailySeriesResult>> GetDailySeriesAsync(string deviceId, UsageWindowDto window, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(UsageMethods.GetDailySeries, window, ct));

    public Task<ActionResult<UsageActivityStatsDto>> GetActivityStatsAsync(string deviceId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(UsageMethods.GetActivityStats, Unit.Value, ct));
}

/// <summary>
/// 定时任务的用户意图。真源在设备的 <c>ScheduledTaskService</c> / <c>Scheduler</c>;任务和运行(含「运行中」)经 <c>schedules</c> 主题进 <see cref="Stores.ClientStores.Schedules"/>,
/// 这里不直接改 Store。定时任务无人值守地以 <c>full</c> 档位运行,所以写入(创建 / 修改 / 启停 / 立即运行 / 删除)只有 owner;更早的运行记录用 <see cref="ListRunsAsync"/> 按需翻页。
/// </summary>
public sealed class ScheduleActions(DeviceRegistry devices) : ISchedulesCommands
{
    public Task<ActionResult<ScheduledTaskDto>> CreateAsync(string deviceId, ScheduledTaskInputDto input, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SchedulesMethods.Create, input, ct, idem: ServiceCall.Idem("schedules", "create")));

    public Task<ActionResult<ScheduledTaskDto>> UpdateAsync(string deviceId, string taskId, ScheduledTaskPatchDto patch, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SchedulesMethods.Update, new ScheduleUpdateParams(taskId, patch), ct, idem: ServiceCall.Idem("schedules", "update")));

    public Task<ActionResult<Unit>> DeleteAsync(string deviceId, string taskId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SchedulesMethods.Delete, new ScheduleRefParams(taskId), ct, idem: ServiceCall.Idem("schedules", "delete")));

    public Task<ActionResult<ScheduledTaskDto>> SetEnabledAsync(string deviceId, string taskId, bool enabled, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SchedulesMethods.SetEnabled, new ScheduleSetEnabledParams(taskId, enabled), ct, idem: ServiceCall.Idem("schedules", "setEnabled")));

    /// <summary>立即运行一次:返回刚落库的 <c>queued</c> 记录,进度看 Store 里的活动运行。上一次还没跑完 → <see cref="ActionOutcome.Conflict"/>。</summary>
    public Task<ActionResult<ScheduledRunDto>> RunNowAsync(string deviceId, string taskId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SchedulesMethods.RunNow, new ScheduleRefParams(taskId), ct, idem: ServiceCall.Idem("schedules", "runNow")));

    public Task<ActionResult<ScheduledRunsResult>> ListRunsAsync(string deviceId, ScheduledRunsQuery query, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SchedulesMethods.ListRuns, query, ct));

    public Task<ActionResult<ScheduledRunDto>> GetRunAsync(string deviceId, string runId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SchedulesMethods.GetRun, new ScheduleRefParams(runId), ct));

    public Task<ActionResult<Unit>> DeleteRunAsync(string deviceId, string runId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SchedulesMethods.DeleteRun, new ScheduleRefParams(runId), ct, idem: ServiceCall.Idem("schedules", "deleteRun")));
}

/// <summary>
/// 存储维护的用户意图。统计 / 备份状态 member 起;整理、备份、回收附件、导出 owner(远程也行);
/// <b>危险操作</b>(恢复备份、按年龄清理、清空历史、清除本地数据、应用导入)owner + 仅本机通道 + 二次确认:先调 <c>Preview*</c> 拿到一次性 <c>ConfirmToken</c>,界面向用户展示预览数字,
/// 用户确认后把令牌交给对应的执行方法(令牌绑定到这一次操作,10 分钟过期,一次性)。远程控制端调危险操作得到 <see cref="ActionOutcome.Forbidden"/>。
/// 导出 / 导入的文件传输是分块的(单块 ≤ 1 MiB):<see cref="ExportToAsync"/> / <see cref="ImportFromAsync"/> 封装了循环,文件对话框在界面层。
/// </summary>
public sealed class StorageActions(DeviceRegistry devices) : IStorageCommands
{
    public const int ChunkBytes = 512 * 1024;

    public Task<ActionResult<StorageStatsDto>> GetStatsAsync(string deviceId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.GetStats, Unit.Value, ct));

    public Task<ActionResult<StorageStatsDto>> VacuumAsync(string deviceId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.Vacuum, Unit.Value, ct, idem: ServiceCall.Idem("storage", "vacuum")));

    public Task<ActionResult<BackupStatusDto>> GetBackupStatusAsync(string deviceId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.BackupStatus, Unit.Value, ct));

    public Task<ActionResult<BackupStatusDto>> CreateBackupAsync(string deviceId, bool manual = true, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.BackupCreate, new BackupCreateParams(manual), ct, idem: ServiceCall.Idem("storage", "backup.create")));

    /// <summary>验证一份备份(设备上的路径)并拿恢复的确认令牌。仅本机 owner。</summary>
    public Task<ActionResult<RestorePreviewDto>> PreviewRestoreAsync(string deviceId, string path, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.BackupRestorePreview, new RestorePreviewParams(path), ct));

    /// <summary>恢复(危险)。用预览给的令牌;仅本机 owner。</summary>
    public Task<ActionResult<BackupStatusDto>> RestoreBackupAsync(string deviceId, string path, string confirmToken, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.BackupRestore, new RestoreParams(path, confirmToken), ct, idem: ServiceCall.Idem("storage", "backup.restore")));

    public Task<ActionResult<CleanupPreviewDto>> PreviewCleanupAsync(string deviceId, string kind, string? age = null, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.CleanupPreview, new CleanupPreviewParams(kind, age), ct));

    public Task<ActionResult<CleanupResultDto>> CleanupAttachmentsAsync(string deviceId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.CleanupAttachments, Unit.Value, ct, idem: ServiceCall.Idem("storage", "cleanup.attachments")));

    /// <summary>按年龄清理 / 清空历史 / 清除本地数据(危险)。仅本机 owner。</summary>
    public Task<ActionResult<CleanupResultDto>> ExecuteCleanupAsync(string deviceId, string kind, string? age, string confirmToken, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.CleanupExecute, new CleanupExecuteParams(kind, age, confirmToken), ct, idem: ServiceCall.Idem("storage", "cleanup.execute")));

    /// <summary>把设备上的数据导出到一个流(调用方是文件对话框选的文件)。分块读,读完丢弃设备上的临时文件。</summary>
    public Task<ActionResult<ExportInfoDto>> ExportToAsync(string deviceId, Stream destination, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, async link =>
        {
            var info = await link.CallAsync(StorageMethods.ExportCreate, Unit.Value, ct, idem: ServiceCall.Idem("storage", "export.create")).ConfigureAwait(false);
            try
            {
                long offset = 0;
                while (true)
                {
                    var chunk = await link.CallAsync(StorageMethods.ExportRead, new ExportReadParams(info.ExportId, offset, ChunkBytes), ct).ConfigureAwait(false);
                    await destination.WriteAsync(chunk.Data, ct).ConfigureAwait(false);
                    offset += chunk.Data.Length;
                    if (chunk.Eof)
                    {
                        return info;
                    }
                }
            }
            finally
            {
                await link.CallAsync(StorageMethods.ExportDiscard, new ExportRefParams(info.ExportId), CancellationToken.None, idem: ServiceCall.Idem("storage", "export.discard")).ConfigureAwait(false);
            }
        });

    /// <summary>把一个导入文件分块上传到设备,返回预览(带确认令牌)。仅本机 owner。随后调 <see cref="ApplyImportAsync"/> 或 <see cref="DiscardImportAsync"/>。</summary>
    public Task<ActionResult<ImportPreviewDto>> ImportFromAsync(string deviceId, Stream source, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, async link =>
        {
            string? uploadId = null;
            long offset = 0;
            var buffer = new byte[ChunkBytes];
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                var upload = await link.CallAsync(StorageMethods.ImportWrite, new ImportWriteParams(uploadId, offset, buffer[..read]), ct, idem: ServiceCall.Idem("storage", "import.write")).ConfigureAwait(false);
                uploadId = upload.UploadId;
                offset = upload.Received;
            }

            return uploadId is null
                ? throw new ProtocolException(ErrorCodes.InvalidRequest, "The import file is empty.")
                : await link.CallAsync(StorageMethods.ImportPreview, new ImportRefParams(uploadId), ct).ConfigureAwait(false);
        });

    public Task<ActionResult<ImportResultDto>> ApplyImportAsync(string deviceId, string uploadId, string confirmToken, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.ImportApply, new ImportApplyParams(uploadId, confirmToken), ct, idem: ServiceCall.Idem("storage", "import.apply")));

    public Task<ActionResult<Unit>> DiscardImportAsync(string deviceId, string uploadId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(StorageMethods.ImportDiscard, new ImportRefParams(uploadId), ct, idem: ServiceCall.Idem("storage", "import.discard")));
}

/// <summary>全局会话搜索(只读,viewer 起)。结果是搜索页自己的状态,不进 Store。查询按短语处理:用户输入里的运算符不会破坏语法。</summary>
public sealed class SearchActions(DeviceRegistry devices)
{
    public Task<ActionResult<SearchConversationsResult>> SearchConversationsAsync(string deviceId, string query, string? workspaceId = null, int limit = 50, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(SearchMethods.Conversations, new SearchConversationsParams(query, workspaceId, limit), ct));
}

/// <summary>
/// 附件的用户意图。上传是分块的(协议帧 4 MiB、单个附件 32 MiB):<see cref="UploadAsync"/> 封装 begin / chunk / commit 循环,失败(包括中途断线)时尽力 abort 掉设备上的暂存。
/// 返回的 <see cref="AttachmentDto.Url"/> 是 <c>ncw://</c> 引用,控制端用 <see cref="DownloadAsync"/> 按引用分块取字节来显示(远程控制端没有设备的文件系统)。
/// 选文件的对话框、拖拽、粘贴是界面层的事;非图片直接给 <c>file</c> 块的路径,不走上传。
/// </summary>
public sealed class AttachmentActions(DeviceRegistry devices) : IAttachmentUploader
{
    public const int ChunkBytes = 512 * 1024;

    public Task<ActionResult<AttachmentDto>> UploadAsync(string deviceId, string sessionId, string displayName, string mime, byte[] bytes, CancellationToken ct = default) =>
        UploadAsync(deviceId, sessionId, displayName, mime, bytes, null, ct);

    /// <summary>同上,每个分块送达后报告已上传的字节数(上传进度条用;回调可能在任何线程)。</summary>
    public Task<ActionResult<AttachmentDto>> UploadAsync(string deviceId, string sessionId, string displayName, string mime, byte[] bytes, IProgress<long>? progress, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, async link =>
        {
            var begin = await link.CallAsync(AttachmentsMethods.UploadBegin, new AttachmentsUploadBeginParams(sessionId, displayName, mime, bytes.Length), ct, idem: ServiceCall.Idem("attachments", "upload.begin")).ConfigureAwait(false);
            try
            {
                for (var offset = 0; offset < bytes.Length; offset += ChunkBytes)
                {
                    var chunk = bytes[offset..Math.Min(bytes.Length, offset + ChunkBytes)];
                    await link.CallAsync(AttachmentsMethods.UploadChunk, new AttachmentsUploadChunkParams(begin.UploadId, offset, chunk), ct, idem: ServiceCall.Idem("attachments", "upload.chunk")).ConfigureAwait(false);
                    progress?.Report(offset + chunk.Length);
                }

                return await link.CallAsync(AttachmentsMethods.UploadCommit, new AttachmentsUploadRefParams(begin.UploadId), ct, idem: ServiceCall.Idem("attachments", "upload.commit")).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await link.CallAsync(AttachmentsMethods.UploadAbort, new AttachmentsUploadRefParams(begin.UploadId), CancellationToken.None, idem: ServiceCall.Idem("attachments", "upload.abort")).ConfigureAwait(false);
                }
                catch (ProtocolException)
                {
                    // 设备上的暂存 10 分钟后自己过期
                }

                throw;
            }
        });

    public Task<ActionResult<Unit>> RemoveAsync(string deviceId, string attachmentId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(AttachmentsMethods.Remove, new AttachmentRefParams(attachmentId), ct, idem: ServiceCall.Idem("attachments", "remove")));

    /// <summary>重启后恢复输入框里的待发附件。</summary>
    public Task<ActionResult<AttachmentsListResult>> ListDraftsAsync(string deviceId, string sessionId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(AttachmentsMethods.ListBySession, new AttachmentsListParams(sessionId), ct));

    /// <summary>按 <c>ncw://</c> 引用把附件分块读进流。返回 MIME 类型。</summary>
    public Task<ActionResult<string>> DownloadAsync(string deviceId, string url, Stream destination, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, async link =>
        {
            long offset = 0;
            while (true)
            {
                var chunk = await link.CallAsync(AttachmentsMethods.Read, new AttachmentsReadParams(url, offset, ChunkBytes), ct).ConfigureAwait(false);
                await destination.WriteAsync(chunk.Data, ct).ConfigureAwait(false);
                offset += chunk.Data.Length;
                if (chunk.Eof)
                {
                    return chunk.Mime;
                }
            }
        });
}

/// <summary>
/// 输入框补全(建议下一步 / 历史指令 / 打字续写)。★ <b>一切失败都是「没有建议」</b>:补全是便利不是功能,这里把设备没连上、没有权限、设备拒绝、被取消都收成空串 / 空列表,
/// 输入框上不会因为它弹任何错误。请求 id 由这里生成(用于合流 / 取消)。
/// </summary>
public sealed class ComposerActions(DeviceRegistry devices)
{
    public async Task<string> SuggestNextAsync(string deviceId, string workspaceId, string sessionId, long triggerKey, string model, string? modelProviderId, string? requestId = null, CancellationToken ct = default)
    {
        var result = await ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(
            ComposerMethods.SuggestNext, new ComposerSuggestParams(requestId ?? NewId(), workspaceId, sessionId, triggerKey, model, modelProviderId), ct)).ConfigureAwait(false);
        return result.IsOk ? result.Value!.Text : "";
    }

    public async Task<string[]> HistoryAsync(string deviceId, string workspaceId, CancellationToken ct = default)
    {
        var result = await ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(ComposerMethods.History, new ComposerHistoryParams(workspaceId), ct)).ConfigureAwait(false);
        return result.IsOk ? result.Value!.Prompts : [];
    }

    public async Task<string> CompleteAsync(string deviceId, string workspaceId, string? sessionId, string draft, ComposerContextDto context, string? requestId = null, CancellationToken ct = default)
    {
        // ★ 草稿像带着密钥就不发:控制端先判一次(同一条规则,Core 之外的纯文本规则在设备的 ComposerText;这里只做最便宜的空 / 超长检查,密钥判定设备还会再判)
        if (draft.Length is 0 or > 2000)
        {
            return "";
        }

        var result = await ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(
            ComposerMethods.Complete, new ComposerCompleteParams(requestId ?? NewId(), workspaceId, sessionId, draft, context), ct)).ConfigureAwait(false);
        return result.IsOk ? result.Value!.Text : "";
    }

    /// <summary>取消一个在途的建议 / 续写(尽力而为,失败不报)。</summary>
    public async Task CancelAsync(string deviceId, string requestId)
    {
        _ = await ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(ComposerMethods.Cancel, new ComposerCancelParams(requestId), CancellationToken.None, idem: ServiceCall.Idem("composer", "cancel"))).ConfigureAwait(false);
    }

    public static string NewId() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// 开放网关的管理(开关、API 密钥、地址、健康)。开关与密钥只有本机 owner(设备拒绝其它承载,这里不重复判断);密钥只在 <see cref="GetKeyAsync"/> / <see cref="RotateKeyAsync"/> 的返回值里出现,
/// 调用方只能显示、不能存进任何 Store(密钥不是状态)。
/// </summary>
public sealed class GatewayActions(DeviceRegistry devices) : IGatewayCommands
{
    public Task<ActionResult<GatewayStatusDto>> GetStatusAsync(string deviceId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(GatewayMethods.GetStatus, Unit.Value, ct));

    public Task<ActionResult<GatewayStatusDto>> SetEnabledAsync(string deviceId, bool enabled, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(GatewayMethods.SetEnabled, new GatewaySetEnabledParams(enabled), ct, idem: ServiceCall.Idem("gateway", "setEnabled")));

    /// <summary><paramref name="role"/> 是 <c>viewer</c> / <c>member</c>。</summary>
    public Task<ActionResult<GatewayKeyDto>> GetKeyAsync(string deviceId, string role, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(GatewayMethods.GetKey, new GatewayKeyParams(role), ct));

    public Task<ActionResult<GatewayKeyDto>> RotateKeyAsync(string deviceId, string role, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(GatewayMethods.RotateKey, new GatewayKeyParams(role), ct, idem: ServiceCall.Idem("gateway", "rotateKey")));

    public Task<ActionResult<GatewayAddressesResult>> GetAddressesAsync(string deviceId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(GatewayMethods.GetAddresses, Unit.Value, ct));

    /// <summary><paramref name="providerId"/> 为 null = 重置全部供应商的健康(冷却、失败计数、限流闸门)。</summary>
    public Task<ActionResult<GatewayStatusDto>> ResetHealthAsync(string deviceId, string? providerId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(GatewayMethods.ResetHealth, new GatewayResetHealthParams(providerId), ct, idem: ServiceCall.Idem("gateway", "resetHealth")));

    public Task<ActionResult<GatewayOpenApiResult>> GetOpenApiAsync(string deviceId, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(GatewayMethods.OpenApi, Unit.Value, ct));
}

/// <summary>上传托盘依赖的附件意图(<see cref="AttachmentActions"/> 的子集):单测里换成假实现,不用起设备连接就能测进度 / 失败 / 取消。</summary>
public interface IAttachmentUploader
{
    Task<ActionResult<AttachmentDto>> UploadAsync(string deviceId, string sessionId, string displayName, string mime, byte[] bytes, IProgress<long>? progress, CancellationToken ct = default);

    Task<ActionResult<Unit>> RemoveAsync(string deviceId, string attachmentId, CancellationToken ct = default);
}
