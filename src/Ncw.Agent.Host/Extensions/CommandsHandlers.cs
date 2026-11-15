using Ncw.Agent.Environments;
using Ncw.Agent.Skills.Commands;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Extensions;

/// <summary>
/// <c>commands.*</c> 的方法实现(方法表在 <see cref="CommandsMethods"/>)。命令只是提示词模板:展开在客户端完成,设备只负责「有哪些、哪些被关了」。每次现扫。
/// 项目环境(SSH)连不上是可重试的 <c>unavailable</c>,不是「没有命令」。
/// </summary>
internal static class CommandsHandlers
{
    public static void Register(MethodRouter router, Func<CommandsService> service)
    {
        router.Handle(CommandsMethods.List, (p, _, ct) => GuardAsync(async () =>
            new CommandsListResult([.. (await service().ListAsync(p.WorkspaceId, ct).ConfigureAwait(false)).Select(c =>
                new CommandDto(c.Name, c.Description, c.Prompt, ScopeName(c.Scope), c.Source, c.ArgumentHint))])));
        router.Handle(CommandsMethods.ListAll, (p, _, ct) => GuardAsync(() => ListAllAsync(service(), p.WorkspaceId, ct)));
        router.Handle(CommandsMethods.Diagnostics, (p, _, ct) => GuardAsync(async () =>
            new CommandsDiagnosticsResult([.. (await service().DiagnosticsAsync(p.WorkspaceId, ct).ConfigureAwait(false)).Select(d =>
                new CommandDiagnosticDto(d.Path, d.Key, d.Message, d.Params is null ? null : new Dictionary<string, string>(d.Params)))])));
        router.Handle(CommandsMethods.SetEnabled, (p, _, ct) => GuardAsync(async () =>
        {
            // 名字会原样进 kv:和命令文件名同一条规则,免得一个调用方往设备配置里塞任意文本
            if (!CommandExpansion.IsValidName(p.Name))
            {
                throw new ProtocolException(ErrorCodes.InvalidRequest, $"'{p.Name}' is not a valid command name.");
            }

            service().SetEnabled(p.Name, p.Enabled);
            return await ListAllAsync(service(), p.WorkspaceId, ct).ConfigureAwait(false);
        }));
    }

    private static async Task<CommandsListAllResult> ListAllAsync(CommandsService service, string? workspaceId, CancellationToken ct) =>
        new([.. (await service.ListAllAsync(workspaceId, ct).ConfigureAwait(false)).Select(c =>
            new CommandItemDto(c.Name, c.Description, ScopeName(c.Scope), c.Source, c.ArgumentHint, c.Enabled))]);

    private static string ScopeName(CommandScope scope) => scope switch
    {
        CommandScope.Builtin => CommandScopes.Builtin,
        CommandScope.Project => CommandScopes.Project,
        CommandScope.Extension => CommandScopes.Extension,
        _ => CommandScopes.Global,
    };

    private static async ValueTask<T> GuardAsync<T>(Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.StaleGeneration)
        {
            throw new ProtocolException(ErrorCodes.Unavailable, "The project environment is not connected.", retryable: true);
        }
    }
}
