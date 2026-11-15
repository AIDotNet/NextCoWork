using System.Security.Cryptography;
using Ncw.Agent.Environments;
using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Environments;

/// <summary>
/// <c>environments.*</c> 的方法实现(方法表在 <see cref="EnvironmentsMethods"/>)。角色、承载、幂等键由路由统一检查,这里只做业务:校验 → 调 <see cref="EnvironmentService"/> → 映射。
/// 失败一律是 <see cref="ProtocolException"/>:环境层的 <see cref="EnvironmentException"/> 按粗粒度错误码映射、稳定 <c>environments.*</c> key 放进 <c>details</c>
/// (<see cref="ProviderErrorDto"/>),消息是固定短句(主机名、路径、服务器文本都不外泄);其它异常由路由收成 <c>internal</c>。设备数据没就绪时 <paramref name="service"/> 抛可重试的 <c>unavailable</c>。
/// </summary>
internal static class EnvironmentsHandlers
{
    public static void Register(MethodRouter router, Func<EnvironmentService> service)
    {
        router.Handle(EnvironmentsMethods.List, (_, _, _) => Guard(() => service().List()));
        router.Handle(EnvironmentsMethods.Status, (p, _, _) => Guard(() => service().Dto(p.Id)));
        router.Handle(EnvironmentsMethods.Create, (p, _, _) => GuardAsync(() => service().CreateAsync(p)));
        router.Handle(EnvironmentsMethods.Update, (p, _, _) => GuardAsync(() => service().UpdateAsync(p)));
        router.Handle(EnvironmentsMethods.Remove, (p, _, _) => GuardAsync(async () =>
        {
            await service().RemoveAsync(p.Id).ConfigureAwait(false);
            return Unit.Value;
        }));
        router.Handle(EnvironmentsMethods.SetDefault, (p, _, _) => Guard(() =>
        {
            service().SetDefault(p.Id);
            return Unit.Value;
        }));
        router.Handle(EnvironmentsMethods.Connect, (p, _, ct) => GuardAsync(() => service().ConnectAsync(p.Id, ct)));
        router.Handle(EnvironmentsMethods.Disconnect, (p, _, _) => GuardAsync(() => service().DisconnectAsync(p.Id)));
        router.Handle(EnvironmentsMethods.Test, (p, _, ct) => GuardAsync(() => service().TestAsync(p, ct)));
        router.Handle(EnvironmentsMethods.Browse, (p, ctx, ct) => GuardAsync(() => service().BrowseAsync(p, ctx.Role >= Role.Member ? ConnectMode.Interactive : ConnectMode.None, ct)));
        router.Handle(EnvironmentsMethods.ImportSshConfig, (p, _, _) => Guard(() => service().ImportSshConfig(p)));
        router.Handle(EnvironmentsMethods.KnownHosts, (_, _, ct) => GuardAsync(() => service().KnownHostsAsync(ct)));
        router.Handle(EnvironmentsMethods.ForgetHostKey, (p, _, ct) => GuardAsync(async () =>
        {
            await service().ForgetHostKeyAsync(p.HostName, ct).ConfigureAwait(false);
            return Unit.Value;
        }));
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

    /// <summary>认识的环境 / 存储失败转成协议错误;其它(含 <see cref="ProtocolException"/> 本身、取消)返回 null,原样往上抛。</summary>
    private static ProtocolException? ToProtocolError(Exception e) => e switch
    {
        ProtocolException => null,
        EnvironmentException env => EnvironmentErrors.ToProtocol(env),
        // 主密钥丢了 / 损坏:凭据读不出来。这是设备上的问题,不是请求的问题(也不能把异常细节发给控制端)。
        CryptographicException => EnvironmentErrors.Coded(ErrorCodes.Unavailable, "The credential store could not be read.", EnvironmentErrorKeys.SecretsUnavailable),
        StorageException s => new ProtocolException(ErrorCodes.Unavailable, s.Message, retryable: true),
        Microsoft.Data.Sqlite.SqliteException s => new ProtocolException(ErrorCodes.Unavailable, s.Message, retryable: true),
        _ => null,
    };
}
