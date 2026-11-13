using System.Net.Sockets;
using Ncw.Client.Devices;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>添加设备的结果。界面按它生成文字(Ncw.Client 不产出界面文字)。</summary>
public enum PairOutcome
{
    Paired,

    /// <summary>地址格式不对(需要 IP:端口 或主机名:端口)。</summary>
    InvalidAddress,

    /// <summary>配对码格式不对(6 位数字)。</summary>
    InvalidCode,

    /// <summary>连不上(地址不对、设备没开远程、防火墙)。</summary>
    Unreachable,

    /// <summary>对方不是 NextCoWork 设备,或者 TLS 握手失败。</summary>
    NotADevice,

    /// <summary>配对码不对。</summary>
    WrongCode,

    /// <summary>设备没能证明自己(可能被中间人转接)。</summary>
    DeviceNotVerified,

    /// <summary>设备的远程开关关了。</summary>
    RemoteDisabled,

    /// <summary>这个平台还不能安全保存本控制端的密钥。</summary>
    KeyStoreUnavailable,
}

/// <summary>
/// 设备相关的用户意图(AGENTS.md §3 规则 4):添加 / 移除远程设备,以及本机设备的远程开关、配对码、吊销控制端。
/// </summary>
public sealed class DeviceActions(DeviceRegistry devices)
{
    /// <summary>用 <c>地址:端口</c> 和配对码添加一台设备。</summary>
    public async Task<PairOutcome> PairAsync(string address, string code, CancellationToken ct = default)
    {
        if (!TryParseAddress(address, out var host, out var port))
        {
            return PairOutcome.InvalidAddress;
        }

        var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 6)
        {
            return PairOutcome.InvalidCode;
        }

        if (devices.Remote is not { } remote)
        {
            return PairOutcome.KeyStoreUnavailable;
        }

        try
        {
            await remote.PairAsync(host, port, digits, ct).ConfigureAwait(false);
            return PairOutcome.Paired;
        }
        catch (PairingException e)
        {
            return e.Failure switch
            {
                PairingFailure.WrongCode => PairOutcome.WrongCode,
                PairingFailure.RemoteDisabled => PairOutcome.RemoteDisabled,
                _ => PairOutcome.DeviceNotVerified,
            };
        }
        catch (SecureChannelException)
        {
            return PairOutcome.NotADevice;
        }
        catch (ProtocolException)
        {
            return PairOutcome.NotADevice;
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return PairOutcome.Unreachable;
        }
        catch (PlatformNotSupportedException)
        {
            return PairOutcome.KeyStoreUnavailable;
        }
    }

    /// <summary>忘掉一台远程设备。</summary>
    public Task RemoveAsync(string deviceId) => devices.Remote?.RemoveAsync(deviceId) ?? Task.CompletedTask;

    /// <summary>打开 / 关闭本机设备的远程连接。结果经 remote 主题进入 Store。</summary>
    public Task SetRemoteEnabledAsync(bool enabled) => CallLocal(DeviceMethods.RemoteSetEnabled, new RemoteSetEnabledParams(enabled));

    /// <summary>换一个配对码。</summary>
    public Task RotatePairingCodeAsync() => CallLocal(DeviceMethods.RemoteRotateCode, Unit.Value);

    /// <summary>吊销一个控制端:设备删除它的信任记录并立即断开它。</summary>
    public Task RevokeControllerAsync(string fingerprint) => CallLocal(DeviceMethods.TrustRevoke, new TrustRevokeParams(fingerprint));

    private async Task CallLocal<TParams, TResult>(MethodDef<TParams, TResult> method, TParams parameters)
    {
        if (devices.Local is not { IsConnected: true } local)
        {
            return;
        }

        try
        {
            await local.CallAsync(method, parameters).ConfigureAwait(false);
        }
        catch (ProtocolException)
        {
            // 结果(或失败原因)以 remote 主题为准,界面照 Store 显示。
        }
    }

    /// <summary>解析 <c>192.168.1.23:7810</c>、<c>[fe80::1]:7810</c>、<c>studio.local:7810</c>;不写端口时用默认端口。</summary>
    public static bool TryParseAddress(string text, out string host, out int port)
    {
        host = "";
        port = Protocol.Transport.LanTransport.DefaultPort;
        var value = text.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            host = value[1..close];
            var rest = value[(close + 1)..];
            return ValidHost(host) && (rest.Length == 0 || (rest.StartsWith(':') && TryPort(rest[1..], out port)));
        }

        var colon = value.LastIndexOf(':');
        if (colon >= 0 && value.IndexOf(':') == colon)
        {
            host = value[..colon];
            return ValidHost(host) && TryPort(value[(colon + 1)..], out port);
        }

        host = value; // 没有端口,或者是不带方括号的 IPv6
        return ValidHost(host);
    }

    private static bool ValidHost(string host) => host.Length > 0 && Uri.CheckHostName(host) != UriHostNameType.Unknown;

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) && port is > 0 and < 65536;
}
