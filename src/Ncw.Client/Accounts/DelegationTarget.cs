using Ncw.Client.Devices;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Accounts;

/// <summary>
/// 把委托令牌交给设备的出口(07 §2 第 7 步)。只有<b>本机通道</b>的设备(桌面内置的 Agent)才有:远程设备、云端 Agent 的账户身份另有来源(02 A6)。
/// 真实现 <see cref="DeviceDelegationTarget"/> 走 <c>account.attachDelegatedToken</c>;测试里换成假的。
/// </summary>
public interface IDelegationTarget
{
    /// <summary>当前能交付的设备 id(本机设备且已连接)。</summary>
    IReadOnlyList<string> ConnectedTargets();

    /// <summary>交付(替换)委托令牌。设备拒绝或掉线抛异常;成功返回。令牌不进异常消息。</summary>
    Task AttachAsync(string deviceId, string token, DateTimeOffset expiresAt, string accountId, string? teamId, CancellationToken ct);

    /// <summary>撤销。幂等。</summary>
    Task DetachAsync(string deviceId, CancellationToken ct);
}

/// <summary>没有设备可交付(没有本机 Agent:预览、纯手机端)。</summary>
public sealed class NoDelegationTarget : IDelegationTarget
{
    public static readonly NoDelegationTarget Instance = new();

    public IReadOnlyList<string> ConnectedTargets() => [];

    public Task AttachAsync(string deviceId, string token, DateTimeOffset expiresAt, string accountId, string? teamId, CancellationToken ct) => Task.CompletedTask;

    public Task DetachAsync(string deviceId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>经设备连接交付:<c>account.attachDelegatedToken</c> / <c>detachDelegatedToken</c>(owner、仅本机通道,command 带幂等键)。</summary>
public sealed class DeviceDelegationTarget(DeviceRegistry devices) : IDelegationTarget
{
    public IReadOnlyList<string> ConnectedTargets() =>
        devices.Local is { IsConnected: true } local && local.Features.Contains(Capabilities.Account) ? [local.DeviceId] : [];

    public async Task AttachAsync(string deviceId, string token, DateTimeOffset expiresAt, string accountId, string? teamId, CancellationToken ct)
    {
        var link = Local(deviceId);
        await link.CallAsync(AccountMethods.AttachDelegatedToken, new AccountAttachParams(token, expiresAt.ToUnixTimeMilliseconds(), accountId, teamId), ct,
            idem: $"account.attach:{Guid.NewGuid():N}").ConfigureAwait(false);
    }

    public async Task DetachAsync(string deviceId, CancellationToken ct)
    {
        var link = Local(deviceId);
        await link.CallAsync(AccountMethods.DetachDelegatedToken, Unit.Value, ct, idem: $"account.detach:{Guid.NewGuid():N}").ConfigureAwait(false);
    }

    private DeviceLink Local(string deviceId) =>
        devices.Local is { IsConnected: true } local && local.DeviceId == deviceId ? local : throw new InvalidOperationException("The local device is not connected.");
}
