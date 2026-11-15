using Ncw.Agent.Services.Schedules;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Services;

/// <summary>
/// 设置 / 用量 / 定时任务的方法实现(方法表在 <see cref="SettingsMethods"/> / <see cref="UsageMethods"/> / <see cref="SchedulesMethods"/>)。
/// 角色、承载、幂等键由路由统一检查,这里只做业务:校验参数 → 调服务层 → 映射成 DTO;业务失败经 <see cref="ServiceMapping"/> 转成带稳定 key 的协议错误。
/// 设备数据没就绪时 <paramref name="services"/> 抛可重试的 <c>unavailable</c>。
/// </summary>
internal static class ServiceHandlers
{
    public static void Register(MethodRouter router, Func<DeviceServices> services)
    {
        // settings
        router.Handle(SettingsMethods.Get, (_, _, _) => ServiceMapping.Guard(() => services().SettingsState()));
        router.Handle(SettingsMethods.Update, (p, ctx, _) => ServiceMapping.Guard(() =>
        {
            // 网关的开关 / 监听范围 / 端口和「允许远程连接」一样只有本机能动:远程 owner 不能替设备打开一个 HTTP 端口。
            if (p.Patch.Gateway is not null && ctx.Transport != Transports.Local)
            {
                throw ServiceMapping.Error(ErrorCodes.Forbidden, "settings.localOnly", "The gateway settings can only be changed from this device.", false, ("field", "gateway"));
            }

            var s = services();
            s.Settings.Update(ServiceMapping.FromDto(p.Patch));
            return s.SettingsState();
        }));
        router.Handle(SettingsMethods.ProjectGet, (p, _, _) => ServiceMapping.Guard(() => ServiceMapping.ToDto(RequireProject(services(), p.WorkspaceId))));
        router.Handle(SettingsMethods.ProjectUpdate, (p, _, _) => ServiceMapping.Guard(() =>
            ServiceMapping.ToDto(services().Projects.Update(p.WorkspaceId, ServiceMapping.FromDto(p)) ?? throw NotFoundProject(p.WorkspaceId))));
        router.Handle(SettingsMethods.ProxySetPassword, (p, _, _) => ServiceMapping.Guard(() =>
        {
            if (p.Password.Length > 1024)
            {
                throw ServiceMapping.Invalid("settings.invalid.tooLong", "The proxy password is too long.", ("field", "proxy.password"));
            }

            return services().SetProxyPassword(p.Password);
        }));

        // usage
        router.Handle(UsageMethods.GetSummary, (p, _, _) => ServiceMapping.Guard(() => ServiceMapping.ToDto(services().Usage.Summary(Window(p)))));
        router.Handle(UsageMethods.GetRequestLogs, (p, _, _) => ServiceMapping.Guard(() => RequestLogs(services(), p)));
        router.Handle(UsageMethods.GetSessionAttempts, (p, _, _) => ServiceMapping.Guard(() =>
            new UsageAttemptsResult([.. services().Usage.SessionAttempts(p.SessionId).Select(ServiceMapping.ToDto)])));
        router.Handle(UsageMethods.GetProviderStats, (p, _, _) => ServiceMapping.Guard(() =>
            new UsageDimensionStatsResult([.. services().Usage.ProviderStats(Window(p)).Select(ServiceMapping.ToDto)])));
        router.Handle(UsageMethods.GetModelStats, (p, _, _) => ServiceMapping.Guard(() =>
            new UsageDimensionStatsResult([.. services().Usage.ModelStats(Window(p)).Select(ServiceMapping.ToDto)])));
        router.Handle(UsageMethods.GetDailySeries, (p, _, _) => ServiceMapping.Guard(() => DailySeries(services(), p)));
        router.Handle(UsageMethods.GetActivityStats, (_, _, _) => ServiceMapping.Guard(() => Activity(services())));

        // schedules
        router.Handle(SchedulesMethods.ListTasks, (p, _, _) => ServiceMapping.Guard(() =>
            new ScheduledTasksResult([.. services().Schedules.List(p.WorkspaceId).Select(ServiceMapping.ToDto)])));
        router.Handle(SchedulesMethods.GetTask, (p, _, _) => ServiceMapping.Guard(() => ServiceMapping.ToDto(RequireTask(services(), p.Id))));
        router.Handle(SchedulesMethods.Create, (p, _, _) => ServiceMapping.Guard(() => ServiceMapping.ToDto(services().Schedules.Create(ServiceMapping.FromDto(p)))));
        router.Handle(SchedulesMethods.Update, (p, _, _) => ServiceMapping.Guard(() => ServiceMapping.ToDto(services().Schedules.Update(p.Id, ServiceMapping.FromDto(p.Patch)))));
        router.Handle(SchedulesMethods.Delete, (p, _, _) => ServiceMapping.Guard(() =>
        {
            // 幂等:已经没有了不是错误(重发、两个控制端同时删)。
            services().Schedules.Delete(p.Id);
            return Unit.Value;
        }));
        router.Handle(SchedulesMethods.SetEnabled, (p, _, _) => ServiceMapping.Guard(() => ServiceMapping.ToDto(services().Schedules.SetEnabled(p.Id, p.Enabled))));
        router.Handle(SchedulesMethods.RunNow, (p, _, _) => ServiceMapping.Guard(() => ServiceMapping.ToDto(services().Scheduler.RunNow(p.Id))));
        router.Handle(SchedulesMethods.ListRuns, (p, _, _) => ServiceMapping.Guard(() => ListRuns(services(), p)));
        router.Handle(SchedulesMethods.GetRun, (p, _, _) => ServiceMapping.Guard(() =>
            ServiceMapping.ToDto(services().ScheduleRepository.GetRun(p.Id) ?? throw ServiceMapping.Error(ErrorCodes.NotFound, "schedules.runNotFound", $"No scheduled run '{p.Id}'."))));
        router.Handle(SchedulesMethods.DeleteRun, (p, _, _) => ServiceMapping.Guard(() =>
        {
            services().Scheduler.DeleteRun(p.Id);
            return Unit.Value;
        }));
    }

    // ───────────────────────── 设置 ─────────────────────────

    private static Ncw.Agent.Services.Settings.ProjectSettings RequireProject(DeviceServices s, string workspaceId) =>
        s.Projects.Get(workspaceId) ?? throw NotFoundProject(workspaceId);

    private static ProtocolException NotFoundProject(string workspaceId) =>
        ServiceMapping.Error(ErrorCodes.NotFound, "settings.projectNotFound", $"Unknown project '{workspaceId}'.");

    // ───────────────────────── 用量 ─────────────────────────

    private static Ncw.Agent.Storage.UsageWindow Window(UsageWindowDto w)
    {
        if (w.From is { } from && from > w.To)
        {
            throw ServiceMapping.Invalid("usage.invalid.window", "The window's start is after its end.");
        }

        return ServiceMapping.FromDto(w);
    }

    private static UsageLogsPage RequestLogs(DeviceServices s, UsageLogsQuery q)
    {
        if (q.Status is not (null or "all" or "success" or "failed"))
        {
            throw ServiceMapping.Invalid("usage.invalid.status", $"Unknown status filter '{q.Status}'.");
        }

        var page = s.Usage.Logs(new Ncw.Agent.Storage.UsageLogsQuery(Window(new UsageWindowDto(q.From, q.To)), q.Query, q.Status, q.Offset, q.Limit));
        return new UsageLogsPage([.. page.Items.Select(ServiceMapping.ToDto)], page.Total, page.Offset, page.Limit);
    }

    /// <summary>查询前先刷一次汇总(幂等):刚聊完就打开统计页也看得到新数字。</summary>
    private static UsageDailySeriesResult DailySeries(DeviceServices s, UsageWindowDto w)
    {
        var window = Window(w);
        s.Rollup.Refresh(s.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        return new UsageDailySeriesResult([.. s.Rollup.Series(window).Select(ServiceMapping.ToDto)]);
    }

    private static UsageActivityStatsDto Activity(DeviceServices s)
    {
        var now = s.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        s.Rollup.Refresh(now);
        return ServiceMapping.ToDto(s.Rollup.Activity(now));
    }

    // ───────────────────────── 定时任务 ─────────────────────────

    private static ScheduledTask RequireTask(DeviceServices s, string id) =>
        s.Schedules.Get(id) ?? throw ServiceMapping.Error(ErrorCodes.NotFound, "schedules.notFound", $"No scheduled task with id \"{id}\".");

    private static ScheduledRunsResult ListRuns(DeviceServices s, ScheduledRunsQuery q)
    {
        var limit = Math.Clamp(q.Limit, 1, 200);
        var rows = s.ScheduleRepository.ListRuns(q.TaskId, limit + 1, q.BeforeScheduledAt, q.BeforeId);
        return new ScheduledRunsResult([.. rows.Take(limit).Select(ServiceMapping.ToDto)], rows.Count > limit);
    }
}
