using System.Security.Cryptography;
using Ncw.Agent.Mcp;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Extensions;

/// <summary>
/// <c>mcp.*</c> 的方法实现(方法表在 <see cref="McpMethods"/>)。角色、承载、幂等键由路由统一检查,这里只做业务:校验 → 调服务层 → 映射成 DTO。
/// 失败一律是 <see cref="ProtocolException"/>:服务层的 <see cref="McpException"/> 带着稳定 key 放进 <c>details</c>(<see cref="ProviderErrorDto"/>),
/// 其它异常路由收成 <c>internal</c>,不外泄细节。设备数据没就绪时 <paramref name="service"/> 抛可重试的 <c>unavailable</c>。
/// </summary>
internal static class McpHandlers
{
    public static void Register(MethodRouter router, Func<McpService> service)
    {
        router.Handle(McpMethods.List, (_, _, _) => Guard(() => McpMapping.ToDto(service().Snapshot())));
        router.Handle(McpMethods.Upsert, (p, _, ct) => GuardAsync(async () => McpMapping.ToDto(await service().UpsertAsync(McpMapping.FromDto(p.Config), ct).ConfigureAwait(false))));
        router.Handle(McpMethods.Remove, (p, _, ct) => GuardAsync(async () => new McpRemoveResult(await service().RemoveAsync(p.Id, ct).ConfigureAwait(false))));
        router.Handle(McpMethods.SetEnabled, (p, _, ct) => GuardAsync(async () => McpMapping.ToDto(await service().SetEnabledAsync(p.Id, p.Enabled, ct).ConfigureAwait(false))));
        router.Handle(McpMethods.Reconnect, (p, _, ct) => GuardAsync(async () => McpMapping.ToDto(await service().ReconnectAsync(p.Id, ct).ConfigureAwait(false))));
        router.Handle(McpMethods.TestConnection, (p, _, ct) => GuardAsync(async () =>
            McpMapping.ToDto(await service().TestConnectionAsync(p.Id, p.Config is null ? null : McpMapping.FromDto(p.Config), ct).ConfigureAwait(false))));
        router.Handle(McpMethods.SetSecrets, (p, _, ct) => GuardAsync(async () =>
            McpMapping.ToDto(await service().SetSecretsAsync(p.Id, p.Values, ct).ConfigureAwait(false))));
        router.Handle(McpMethods.GetSecretsInfo, (p, _, _) => Guard(() => McpMapping.ToDto(service().SecretsInfo(p.Id))));
        router.Handle(McpMethods.ResourcesList, (p, _, ct) => GuardAsync(async () =>
            new McpResourcesResult([.. (await service().ListResourcesAsync(p.Id, ct).ConfigureAwait(false)).Select(r => new McpResourceDto(r.Uri, r.Name, r.Description, r.MimeType))])));
        router.Handle(McpMethods.PromptsList, (p, _, ct) => GuardAsync(async () =>
            new McpPromptsResult([.. (await service().ListPromptsAsync(p.Id, ct).ConfigureAwait(false)).Select(r => new McpPromptDto(r.Name, r.Description, [.. r.Arguments]))])));
    }

    private static ValueTask<T> Guard<T>(Func<T> work)
    {
        try
        {
            return ValueTask.FromResult(work());
        }
        catch (Exception e) when (ToProtocolError(e) is { } converted)
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
        catch (Exception e) when (ToProtocolError(e) is { } converted)
        {
            throw converted;
        }
    }

    /// <summary>认识的业务 / 存储失败转成协议错误;其它(含 <see cref="ProtocolException"/> 本身、取消)返回 null,原样往上抛。</summary>
    private static ProtocolException? ToProtocolError(Exception e) => e switch
    {
        ProtocolException => null,
        McpException mcp => ToProtocol(mcp),
        // 主密钥丢了 / 损坏:凭据读不出来。这是设备上的问题,不是请求的问题(也不能把异常细节发给控制端)。
        CryptographicException => new ProtocolException(ErrorCodes.Unavailable, "The credential store could not be read.", retryable: false),
        StorageException s => new ProtocolException(ErrorCodes.Unavailable, s.Message, retryable: true),
        Microsoft.Data.Sqlite.SqliteException s => new ProtocolException(ErrorCodes.Unavailable, s.Message, retryable: true),
        _ => null,
    };

    /// <summary>
    /// 业务错误 → 协议错误:粗粒度的错误码给路由 / 通用处理,稳定 key + 参数放在 <c>details</c>。<b>消息是固定的短句</b>(服务器给的文本、路径、命令输出都不外泄)。
    /// </summary>
    private static ProtocolException ToProtocol(McpException e)
    {
        var (code, message) = e.Key switch
        {
            McpErrorKeys.NotFound => (ErrorCodes.NotFound, "The MCP server does not exist."),
            McpErrorKeys.NotConnected => (ErrorCodes.Unavailable, "The MCP server is not connected."),
            McpErrorKeys.Unsupported => (ErrorCodes.InvalidRequest, "The MCP request is not supported."),
            McpErrorKeys.WorkspaceInvalid => (ErrorCodes.NotFound, "The project does not exist."),
            var key when key.StartsWith("mcp.connect.", StringComparison.Ordinal) => (ErrorCodes.Unavailable, "The MCP server request failed."),
            _ => (ErrorCodes.InvalidRequest, "The MCP server configuration was rejected."),
        };
        return ExtensionErrors.Coded(code, message, e.Key, e.Parameters, retryable: code == ErrorCodes.Unavailable);
    }
}
