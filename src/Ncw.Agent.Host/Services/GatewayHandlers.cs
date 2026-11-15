using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;

namespace Ncw.Agent.Host.Services;

/// <summary>开放网关管理方法的实现(方法表在 <see cref="GatewayMethods"/>)。开关与密钥只有本机 owner(路由统一检查承载);密钥明文只经 <c>gateway.getKey</c> / <c>gateway.rotateKey</c> 单次返回。</summary>
internal static class GatewayHandlers
{
    public static void Register(MethodRouter router, Func<DeviceServices> services)
    {
        router.Handle(GatewayMethods.GetStatus, (_, _, _) => ServiceMapping.Guard(() => services().Gateway.Status()));
        router.Handle(GatewayMethods.GetKey, (p, _, _) => ServiceMapping.Guard(() => new GatewayKeyDto(p.Role, services().Gateway.GetKey(p.Role))));
        router.Handle(GatewayMethods.RotateKey, (p, _, _) => ServiceMapping.Guard(() => new GatewayKeyDto(p.Role, services().Gateway.RotateKey(p.Role))));
        router.Handle(GatewayMethods.GetAddresses, (_, _, _) => ServiceMapping.Guard(() => new GatewayAddressesResult(services().Gateway.Addresses())));
        router.Handle(GatewayMethods.SetEnabled, async (p, _, _) =>
        {
            var s = services();
            s.Settings.Update(new Ncw.Agent.Services.Settings.DeviceSettingsPatch { Gateway = new Ncw.Agent.Services.Settings.GatewaySettingsPatch(Enabled: p.Enabled) });
            await s.Gateway.ReconcileAsync().ConfigureAwait(false); // 立即生效,结果里就是新的状态
            return s.Gateway.Status();
        });
        router.Handle(GatewayMethods.ResetHealth, (p, _, _) => ServiceMapping.Guard(() => services().Gateway.ResetHealth(p.ProviderId)));
        router.Handle(GatewayMethods.OpenApi, (_, _, _) => ServiceMapping.Guard(() => new GatewayOpenApiResult(services().Gateway.OpenApi())));
    }
}
