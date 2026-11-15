using PolyType;

namespace Ncw.Protocol.Methods;

// 开放网关的管理方法(docs/design/09 §11、10 的 `gateway` 模块 → `gateway.*`)。网关本身是同一张方法表的 HTTP 映射(`POST /api/<method>`、SSE `GET /api/topics/<topic>`),见 `Ncw.Agent.Gateway`;
// 这里是它的开关、密钥、地址和健康。★ 默认关闭;和「允许远程连接」是两个独立的开关;**开关与密钥只有本机 owner 能动**(远程 owner 不能替设备打开一个 HTTP 端口,也看不到密钥)。
// API 密钥绑定角色(viewer / member),密钥只经 `gateway.getKey`(owner + 仅本机通道)单次取回,不进状态、不进事件、不进设置。

public sealed record GatewayHealthDto(string ProviderId, bool Healthy, double Score, int ConsecutiveFailures, long? CooldownUntil, int? LastLatencyMs, string? LastError, long LastCheckedAt);

/// <param name="Port">实际监听的端口;没打开或打开失败为 null。</param>
/// <param name="Host">监听地址:<c>127.0.0.1</c>(仅本机)或 <c>0.0.0.0</c>(允许局域网)。</param>
/// <param name="Error">打开失败的原因代码(<c>gateway.portInUse</c> 等);正常为 null。</param>
public sealed record GatewayStatusDto(bool Enabled, int? Port, string Host, bool AllowRemote, bool Failover, GatewayHealthDto[] Health, string? Error);

/// <param name="Role"><c>viewer</c> / <c>member</c>。</param>
public sealed record GatewayKeyParams(string Role);

public sealed record GatewayKeyDto(string Role, string Key);

public sealed record GatewayAddressesResult(string[] Addresses);

public sealed record GatewaySetEnabledParams(bool Enabled);

/// <summary><see cref="ProviderId"/> 为空 = 重置全部供应商的健康(冷却、失败计数、限流闸门)。</summary>
public sealed record GatewayResetHealthParams(string? ProviderId);

/// <summary>OpenAPI 3.1 文档(JSON 文本),由方法表自动导出。</summary>
public sealed record GatewayOpenApiResult(string Document);

/// <summary>网关管理方法表(能力 <see cref="Capabilities.Gateway"/>)。</summary>
public static class GatewayMethods
{
    public static readonly MethodDef<Unit, GatewayStatusDto> GetStatus =
        MethodTable.Query<Unit, GatewayStatusDto>("gateway.getStatus", Role.Owner, Capabilities.Gateway);

    /// <summary>取某个角色的 API 密钥(没有就生成并存进 NCK1)。仅本机 owner。</summary>
    public static readonly MethodDef<GatewayKeyParams, GatewayKeyDto> GetKey =
        MethodTable.Query<GatewayKeyParams, GatewayKeyDto>("gateway.getKey", Role.Owner, Capabilities.Gateway, Transports.Local);

    /// <summary>换一把新密钥(旧的立刻失效)。仅本机 owner。</summary>
    public static readonly MethodDef<GatewayKeyParams, GatewayKeyDto> RotateKey =
        MethodTable.Command<GatewayKeyParams, GatewayKeyDto>("gateway.rotateKey", Role.Owner, Capabilities.Gateway, Transports.Local);

    /// <summary>真实可访问的地址(<c>http://127.0.0.1:19836</c>;允许局域网时含本机的局域网地址;<c>0.0.0.0</c> 是绑定地址,不是可访问地址)。</summary>
    public static readonly MethodDef<Unit, GatewayAddressesResult> GetAddresses =
        MethodTable.Query<Unit, GatewayAddressesResult>("gateway.getAddresses", Role.Owner, Capabilities.Gateway);

    /// <summary>打开 / 关闭网关(写设置 <c>gateway.enabled</c> 并立即生效)。仅本机 owner。</summary>
    public static readonly MethodDef<GatewaySetEnabledParams, GatewayStatusDto> SetEnabled =
        MethodTable.Command<GatewaySetEnabledParams, GatewayStatusDto>("gateway.setEnabled", Role.Owner, Capabilities.Gateway, Transports.Local);

    public static readonly MethodDef<GatewayResetHealthParams, GatewayStatusDto> ResetHealth =
        MethodTable.Command<GatewayResetHealthParams, GatewayStatusDto>("gateway.resetHealth", Role.Owner, Capabilities.Gateway);

    public static readonly MethodDef<Unit, GatewayOpenApiResult> OpenApi =
        MethodTable.Query<Unit, GatewayOpenApiResult>("gateway.openApi", Role.Viewer, Capabilities.Gateway);

    public static readonly IReadOnlyList<MethodDef> All = [GetStatus, GetKey, RotateKey, GetAddresses, SetEnabled, ResetHealth, OpenApi];
}

[GenerateShapeFor<GatewayHealthDto>]
[GenerateShapeFor<GatewayStatusDto>]
[GenerateShapeFor<GatewayKeyParams>]
[GenerateShapeFor<GatewayKeyDto>]
[GenerateShapeFor<GatewayAddressesResult>]
[GenerateShapeFor<GatewaySetEnabledParams>]
[GenerateShapeFor<GatewayResetHealthParams>]
[GenerateShapeFor<GatewayOpenApiResult>]
public sealed partial class PayloadShapes;
