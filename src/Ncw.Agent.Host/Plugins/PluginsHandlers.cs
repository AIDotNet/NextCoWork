using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Host.Extensions;
using Ncw.Agent.Plugins;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Plugins;

/// <summary>
/// <c>plugins.*</c> 的方法实现(方法表在 <see cref="PluginsMethods"/>)。角色、承载、幂等键由路由统一检查,这里只做业务:校验参数 → 调插件管理器 → 映射成 DTO。
/// 失败一律是 <see cref="ProtocolException"/>:插件域的 <see cref="PluginException"/> 带着稳定 key 放进 <c>details</c>(<see cref="ProviderErrorDto"/>),环境连不上是可重试的 <c>unavailable</c>,
/// 其它异常路由收成 <c>internal</c>,不外泄细节。所有变更方法返回变更之后的完整状态;<see cref="PluginsTopicState.Rev"/> 单调。
/// </summary>
internal static class PluginsHandlers
{
    public static void Register(MethodRouter router, Func<PluginManager> manager)
    {
        PluginsTopicState State() => PluginsMapping.ToDto(manager().Snapshot(), manager().ApiVersion);

        router.Handle(PluginsMethods.List, (_, _, _) => Guard(State));
        router.Handle(PluginsMethods.SetEnabled, (p, _, ct) => GuardAsync(async () =>
        {
            await manager().SetEnabledAsync(Require(p.PluginId), p.Enabled, ct).ConfigureAwait(false);
            return State();
        }));
        router.Handle(PluginsMethods.Uninstall, (p, _, ct) => GuardAsync(async () =>
        {
            await manager().UninstallAsync(Require(p.PluginId), ct).ConfigureAwait(false);
            return State();
        }));
        router.Handle(PluginsMethods.InstallPackage, (p, _, ct) => GuardAsync(async () =>
        {
            if (p.Zip.Length == 0 || p.Zip.Length > PluginsLimits.MaxZipBytes)
            {
                throw new PluginException(PluginErrorKeys.PackageTooLarge, "The ZIP is empty or larger than a request can carry.");
            }

            var installed = await manager().InstallZipAsync(p.Zip, null, null, ct).ConfigureAwait(false);
            return new PluginsInstallResult(PluginsMapping.ToDto(installed), State());
        }));
        router.Handle(PluginsMethods.GrantPermissions, (p, _, ct) => GuardAsync(async () =>
        {
            await manager().GrantAsync(Require(p.PluginId), Permissions(p.Permissions), ct).ConfigureAwait(false);
            return State();
        }));
        router.Handle(PluginsMethods.RevokePermissions, (p, _, ct) => GuardAsync(async () =>
        {
            await manager().RevokeAsync(Require(p.PluginId), Permissions(p.Permissions), ct).ConfigureAwait(false);
            return State();
        }));
        router.Handle(PluginsMethods.Activity, (p, _, _) => Guard(() =>
            new PluginsActivityResult([.. manager().Activity(string.IsNullOrEmpty(p.PluginId) ? null : p.PluginId).Select(PluginsMapping.ToDto)])));
        router.Handle(PluginsMethods.RunCommand, (p, _, ct) => GuardAsync(async () =>
        {
            await manager().RunCommandAsync(Require(p.PluginId), Require(p.CommandId), p.ArgsJson, string.IsNullOrEmpty(p.WorkspaceId) ? null : p.WorkspaceId, ct).ConfigureAwait(false);
            return Unit.Value;
        }));
        router.Handle(PluginsMethods.CardAction, (p, _, _) => Guard(() =>
        {
            System.Text.Json.JsonElement? value = null;
            if (!string.IsNullOrEmpty(p.ValueJson) && p.ValueJson.Length <= 16 * 1024)
            {
                try
                {
                    value = System.Text.Json.JsonDocument.Parse(p.ValueJson).RootElement.Clone();
                }
                catch (System.Text.Json.JsonException)
                {
                    throw new ProtocolException(ErrorCodes.InvalidRequest, "The action value is not valid JSON.");
                }
            }

            return new PluginsCardActionResult(manager().DeliverCardAction(Require(p.PluginId), Require(p.CallId), Require(p.ActionId), value));
        }));
        router.Handle(PluginsMethods.InteractionReply, (p, _, _) => Guard(() =>
        {
            manager().ReplyInteraction(Require(p.RequestId), new PluginInteractionReply(p.Confirmed, p.Text, p.PickedId));
            return Unit.Value;
        }));
        router.Handle(PluginsMethods.MarketList, (p, _, ct) => GuardAsync(async () =>
        {
            var page = await manager().MarketListAsync(p.Query, p.Category, p.Page, ct).ConfigureAwait(false);
            return new PluginsMarketListResult([.. page.Items.Select(PluginsMapping.ToDto)], page.HasNext, page.Page);
        }));
        router.Handle(PluginsMethods.MarketCategories, (_, _, ct) => GuardAsync(async () =>
            new PluginsMarketCategoriesResult([.. await manager().MarketCategoriesAsync(ct).ConfigureAwait(false)])));
        router.Handle(PluginsMethods.MarketDetail, (p, _, ct) => GuardAsync(async () =>
            PluginsMapping.ToDto(await manager().MarketDetailAsync(Require(p.Slug), ct).ConfigureAwait(false))));
        router.Handle(PluginsMethods.InstallMarket, (p, _, ct) => GuardAsync(async () =>
        {
            var installed = await manager().InstallMarketAsync(Require(p.Slug), p.Version, null, ct).ConfigureAwait(false);
            return new PluginsInstallResult(PluginsMapping.ToDto(installed), State());
        }));
        router.Handle(PluginsMethods.CheckUpdates, (p, _, ct) => GuardAsync(async () =>
            new PluginsUpdatesResult([.. (await manager().CheckUpdatesAsync(p.Force, ct).ConfigureAwait(false)).Select(PluginsMapping.ToDto)])));
        router.Handle(PluginsMethods.UpdateAll, (_, _, ct) => GuardAsync(async () =>
        {
            var result = await manager().UpdateAllAsync(ct).ConfigureAwait(false);
            return new PluginsUpdateAllResult([.. result.Updated], [.. result.Failed.Select(f => new PluginUpdateFailureDto(f.PluginId, f.ErrorKey))]);
        }));
        router.Handle(PluginsMethods.GetConfiguration, (p, _, _) => Guard(() =>
        {
            var id = Require(p.PluginId);
            return PluginsMapping.ToConfigurationDto(id, manager().GetConfiguration(id));
        }));
        router.Handle(PluginsMethods.SetConfiguration, (p, _, _) => Guard(() =>
        {
            var id = Require(p.PluginId);
            return PluginsMapping.ToConfigurationDto(id, manager().SetConfiguration(id, Require(p.Key), PluginsMapping.FromDto(p.Value)));
        }));
    }

    private static string Require(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new ProtocolException(ErrorCodes.InvalidRequest, "A required argument is empty.") : value;

    private static IReadOnlyList<string> Permissions(string[] permissions)
    {
        if (permissions.Length > 64 || permissions.Any(p => !PluginPermissions.IsValid(p)))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "Unknown permission.");
        }

        return permissions;
    }

    private static ValueTask<T> Guard<T>(Func<T> work)
    {
        try
        {
            return ValueTask.FromResult(work());
        }
        catch (Exception e) when (ToProtocol(e) is { } converted)
        {
            throw converted;
        }
    }

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
        PluginException plugin => ExtensionErrors.Coded(CodeOf(plugin.Key), plugin.Message, plugin.Key, plugin.Parameters, retryable: plugin.Key == PluginErrorKeys.MarketFailed),
        EnvironmentException env when env.Code is EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.StaleGeneration =>
            new ProtocolException(ErrorCodes.Unavailable, "The project environment is not connected.", retryable: true),
        EnvironmentException env when env.Code == EnvironmentErrorCode.PermissionDenied =>
            ExtensionErrors.Coded(ErrorCodes.Unavailable, "The install location is not writable.", PluginErrorKeys.DiskPermission),
        StorageException s => new ProtocolException(ErrorCodes.Unavailable, s.Message, retryable: true),
        Microsoft.Data.Sqlite.SqliteException s => new ProtocolException(ErrorCodes.Unavailable, s.Message, retryable: true),
        _ => null,
    };

    private static string CodeOf(string key) => key switch
    {
        PluginErrorKeys.NotFound or PluginErrorKeys.InteractionNotFound or PluginErrorKeys.VersionUnavailable => ErrorCodes.NotFound,
        PluginErrorKeys.AuthRequired or PluginErrorKeys.ScopeRequired => ErrorCodes.Unauthorized,
        PluginErrorKeys.MarketFailed or PluginErrorKeys.DiskPermission or PluginErrorKeys.NotRunnable or PluginErrorKeys.Faulted or PluginErrorKeys.NotLoaded or PluginErrorKeys.OperationFailed => ErrorCodes.Unavailable,
        PluginErrorKeys.CommandFailed => ErrorCodes.Internal,
        PluginErrorKeys.PackageTooLarge => ErrorCodes.TooLarge,
        _ => ErrorCodes.InvalidRequest,
    };
}
