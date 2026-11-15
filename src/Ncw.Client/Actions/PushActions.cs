using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Actions;

/// <summary>
/// 推送的用户意图(规则 4):把本控制端的推送令牌登记到一台设备(<c>push.register</c>)。设备只存不发,发送由后端推送中继负责。
/// 令牌的取得(权限、APNs / FCM)是宿主的 <see cref="IPushRegistration"/>;什么时候登记到哪些设备由 <c>PushViewModel</c> 决定。
/// </summary>
public sealed class PushActions(DeviceRegistry devices) : IPushCommands
{
    /// <summary>这台设备声明了推送能力,而且本控制端的角色够用(member 起)。</summary>
    public bool Supports(string deviceId) =>
        devices.Find(deviceId) is { IsConnected: true } link && link.Features.Contains(Capabilities.Push);

    public Task<ActionResult<PushRegisterResult>> RegisterAsync(string deviceId, PushPlatform platform, string token, string? appVersion = null, CancellationToken ct = default) =>
        ServiceCall.RunAsync(devices, deviceId, link => link.CallAsync(
            PushMethods.Register,
            new PushRegisterParams(platform == PushPlatform.Apns ? PushPlatforms.Apns : PushPlatforms.Fcm, token, appVersion),
            ct, idem: ServiceCall.Idem("push", "register")));
}

/// <summary>推送登记依赖的意图(<see cref="PushActions"/> 的子集):单测里换成假实现。</summary>
public interface IPushCommands
{
    bool Supports(string deviceId);

    Task<ActionResult<PushRegisterResult>> RegisterAsync(string deviceId, PushPlatform platform, string token, string? appVersion = null, CancellationToken ct = default);
}
