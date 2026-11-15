using Ncw.Agent.Environments;
using Ncw.Agent.Skills;
using Ncw.Agent.Skills.Install;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Extensions;

/// <summary>
/// <c>skills.*</c> 的方法实现(方法表在 <see cref="SkillsMethods"/>)。角色、承载、幂等键由路由统一检查,这里只做业务:校验参数 → 调服务层 → 映射成 DTO。
/// 失败一律是 <see cref="ProtocolException"/>:服务层的 <see cref="SkillException"/> 带着稳定 key 放进 <c>details</c>(<see cref="ProviderErrorDto"/>),环境连不上是可重试的 <c>unavailable</c>,
/// 其它异常路由收成 <c>internal</c>,不外泄细节。所有变更方法返回变更之后的完整清单。
/// </summary>
internal static class SkillsHandlers
{
    public static void Register(MethodRouter router, Func<SkillsService> service)
    {
        router.Handle(SkillsMethods.List, (p, _, ct) => GuardAsync(() => ListAsync(service(), p.WorkspaceId, ct)));
        router.Handle(SkillsMethods.Diagnostics, (p, _, ct) => GuardAsync(async () =>
            new SkillsDiagnosticsResult([.. (await service().DiagnosticsAsync(p.WorkspaceId, ct).ConfigureAwait(false)).Select(ToDto)])));
        router.Handle(SkillsMethods.SetGlobalEnabled, (p, _, ct) => GuardAsync(async () =>
        {
            service().SetGlobalEnabled(Require(p.SkillId), p.Enabled);
            return await ListAsync(service(), p.WorkspaceId, ct).ConfigureAwait(false);
        }));
        router.Handle(SkillsMethods.SetWorkspaceActive, (p, _, ct) => GuardAsync(async () =>
        {
            await service().SetWorkspaceActiveAsync(Require(p.SkillId), Require(p.WorkspaceId), p.Active, ct).ConfigureAwait(false);
            return await ListAsync(service(), p.WorkspaceId, ct).ConfigureAwait(false);
        }));
        router.Handle(SkillsMethods.InstallZip, (p, _, ct) => GuardAsync(async () =>
        {
            if (p.Zip.Length == 0 || p.Zip.Length > SkillsLimits.MaxZipBytes)
            {
                throw new SkillException(SkillErrorKeys.PackageTooLarge, "The ZIP is empty or larger than a request can carry.");
            }

            var installed = await service().InstallZipAsync(p.Zip, ScopeOf(p.Scope), p.WorkspaceId, p.FileName, ct: ct).ConfigureAwait(false);
            return await InstalledAsync(service(), installed, p.WorkspaceId, ct).ConfigureAwait(false);
        }));
        router.Handle(SkillsMethods.InstallMarket, (p, _, ct) => GuardAsync(async () =>
        {
            var installed = await service().InstallMarketAsync(Require(p.Slug), p.Version, ScopeOf(p.Scope), p.WorkspaceId, ct: ct).ConfigureAwait(false);
            return await InstalledAsync(service(), installed, p.WorkspaceId, ct).ConfigureAwait(false);
        }));
        router.Handle(SkillsMethods.Uninstall, (p, _, ct) => GuardAsync(async () =>
        {
            await service().UninstallAsync(Require(p.SkillId), p.Scope is null ? null : ScopeOf(p.Scope), p.WorkspaceId, ct).ConfigureAwait(false);
            return await ListAsync(service(), p.WorkspaceId, ct).ConfigureAwait(false);
        }));
        router.Handle(SkillsMethods.MarketList, (p, _, ct) => GuardAsync(async () =>
            new SkillsMarketListResult([.. (await service().MarketListAsync(p.Query, p.Category, ct).ConfigureAwait(false)).Select(ToDto)])));
        router.Handle(SkillsMethods.MarketCategories, (_, _, ct) => GuardAsync(async () =>
            new SkillsMarketCategoriesResult([.. await service().MarketCategoriesAsync(ct).ConfigureAwait(false)])));
        router.Handle(SkillsMethods.MarketDetail, (p, _, ct) => GuardAsync(async () =>
        {
            var detail = await service().MarketDetailAsync(Require(p.Slug), ct).ConfigureAwait(false);
            return new SkillMarketDetailDto(ToDto(detail.Item), [.. detail.Versions.Select(v => new SkillMarketVersionDto(v.Version, v.Changelog, v.Sha256, v.FileSize))]);
        }));
    }

    private static async Task<SkillsListResult> ListAsync(SkillsService service, string? workspaceId, CancellationToken ct) =>
        new([.. (await service.ListAsync(workspaceId, ct).ConfigureAwait(false)).Select(ToDto)]);

    private static async Task<SkillsInstallResult> InstalledAsync(SkillsService service, SkillListItem installed, string? workspaceId, CancellationToken ct) =>
        new(ToDto(installed), (await ListAsync(service, workspaceId, ct).ConfigureAwait(false)).Skills);

    private static string Require(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new ProtocolException(ErrorCodes.InvalidRequest, "A required argument is empty.") : value;

    private static SkillInstallScope ScopeOf(string scope) => scope switch
    {
        SkillScopes.Global => SkillInstallScope.Global,
        SkillScopes.Project => SkillInstallScope.Project,
        _ => throw new ProtocolException(ErrorCodes.InvalidRequest, $"Unknown install scope '{scope}'."),
    };

    // ───────────────────────── 映射 ─────────────────────────

    internal static SkillInfoDto ToDto(SkillListItem i) => new(
        i.Id, i.Name, i.Description, i.Category, KindName(i.SourceKind), ScopeName(i.Scope), i.PluginId, i.GlobalEnabled, i.ActiveInWorkspace, i.ClientAssetsOnly,
        i.Version, i.Sha256, i.SourcePath, i.UsageCount, i.LastUsedAt);

    internal static SkillDiagnosticDto ToDto(SkillDiagnostic d) =>
        new(d.Path, d.Key, d.Message, d.Params is null ? null : new Dictionary<string, string>(d.Params));

    internal static SkillMarketItemDto ToDto(SkillMarketItem i) =>
        new(i.Slug, i.Name, i.DisplayName, i.Description, i.Category, i.IconUrl, i.Author, i.Version, i.Sha256, i.FileSize, i.DownloadCount, i.TriggerCount);

    private static string KindName(SkillSourceKind kind) => kind switch
    {
        SkillSourceKind.Builtin => SkillSourceKinds.Builtin,
        SkillSourceKind.Zip => SkillSourceKinds.Zip,
        SkillSourceKind.Git => SkillSourceKinds.Git,
        SkillSourceKind.Plugin => SkillSourceKinds.Plugin,
        _ => SkillSourceKinds.Folder,
    };

    private static string ScopeName(SkillScope scope) => scope switch
    {
        SkillScope.Project => SkillScopes.Project,
        SkillScope.Plugin => SkillScopes.Plugin,
        _ => SkillScopes.Global,
    };

    // ───────────────────────── 错误 ─────────────────────────

    private static async ValueTask<T> GuardAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception e) when (ToProtocol(e) is { } converted)
        {
            throw converted;
        }
    }

    /// <summary>认识的业务 / 环境失败转成协议错误;其它(含 <see cref="ProtocolException"/> 本身、取消)返回 null,原样往上抛。</summary>
    internal static ProtocolException? ToProtocol(Exception e) => e switch
    {
        ProtocolException => null,
        SkillException skill => ExtensionErrors.Coded(CodeOf(skill.Key), skill.Message, skill.Key, skill.Parameters, retryable: skill.Key == SkillErrorKeys.NetworkFailed),
        // 项目环境(SSH)连不上不是「没有 Skill」:可重试的 unavailable
        EnvironmentException env when env.Code is EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.StaleGeneration =>
            new ProtocolException(ErrorCodes.Unavailable, "The project environment is not connected.", retryable: true),
        EnvironmentException env when env.Code == EnvironmentErrorCode.PermissionDenied =>
            ExtensionErrors.Coded(ErrorCodes.Unavailable, "The install location is not writable.", SkillErrorKeys.DiskPermission),
        _ => null,
    };

    private static string CodeOf(string key) => key switch
    {
        SkillErrorKeys.NotFound or SkillErrorKeys.WorkspaceNotFound or SkillErrorKeys.VersionUnavailable => ErrorCodes.NotFound,
        SkillErrorKeys.AuthRequired or SkillErrorKeys.ScopeRequired => ErrorCodes.Unauthorized,
        SkillErrorKeys.NetworkFailed or SkillErrorKeys.DiskPermission or SkillErrorKeys.DiskFull or SkillErrorKeys.NotLoaded or SkillErrorKeys.OperationFailed => ErrorCodes.Unavailable,
        _ => ErrorCodes.InvalidRequest,
    };
}
