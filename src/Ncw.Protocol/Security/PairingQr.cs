using System.Globalization;
using System.Text;

namespace Ncw.Protocol.Security;

/// <summary>
/// 扫码配对的二维码内容(docs/design/09 §3「扫码配对」):
/// <c>nextcowork://pair?v=1&amp;d=&lt;deviceId&gt;&amp;f=&lt;设备公钥指纹&gt;&amp;a=&lt;地址列表&gt;&amp;t=&lt;一次性密钥&gt;&amp;e=&lt;过期时间&gt;</c>。
/// <c>f</c>、<c>t</c> 用 base64url;<c>a</c> 是逗号分隔的 <c>host:port</c>(IPv6 用方括号);<c>e</c> 是 Unix 毫秒。
/// </summary>
public sealed record PairingQr(string DeviceId, byte[] Fingerprint, string[] Addresses, byte[] Token, long ExpiresAt)
{
    public const int Version = 1;

    /// <summary>一次性密钥的长度(128 位)。</summary>
    public const int TokenLength = 16;

    public string ToUri()
    {
        var builder = new StringBuilder("nextcowork://pair?v=");
        builder.Append(Version.ToString(CultureInfo.InvariantCulture))
            .Append("&d=").Append(Uri.EscapeDataString(DeviceId))
            .Append("&f=").Append(Base64Url(Fingerprint))
            .Append("&a=").Append(Uri.EscapeDataString(string.Join(',', Addresses)))
            .Append("&t=").Append(Base64Url(Token))
            .Append("&e=").Append(ExpiresAt.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    /// <summary>解析二维码内容;任何不合法(版本不对、缺字段、密钥长度不对、deviceId 与指纹不符)都返回 false。</summary>
    public static bool TryParse(string uri, out PairingQr qr)
    {
        qr = null!;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
            || !string.Equals(parsed.Scheme, "nextcowork", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parsed.Host, "pair", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in parsed.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=', StringComparison.Ordinal);
            if (index <= 0)
            {
                return false;
            }

            fields[pair[..index]] = Uri.UnescapeDataString(pair[(index + 1)..]);
        }

        if (!fields.TryGetValue("v", out var v) || v != Version.ToString(CultureInfo.InvariantCulture)
            || !fields.TryGetValue("d", out var deviceId) || !fields.TryGetValue("f", out var f)
            || !fields.TryGetValue("a", out var a) || !fields.TryGetValue("t", out var t)
            || !fields.TryGetValue("e", out var e) || !long.TryParse(e, NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
        {
            return false;
        }

        if (!TryFromBase64Url(f, out var fingerprint) || fingerprint.Length != 32
            || !TryFromBase64Url(t, out var token) || token.Length != TokenLength
            || PeerIdentity.DeviceIdOf(fingerprint) != deviceId)
        {
            return false;
        }

        var addresses = a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (addresses.Length == 0 || addresses.Any(x => !TrySplitAddress(x, out _, out _)))
        {
            return false;
        }

        qr = new PairingQr(deviceId, fingerprint, addresses, token, expires);
        return true;
    }

    /// <summary>把 <c>host:port</c>(IPv6 为 <c>[::1]:7810</c>)拆开。</summary>
    public static bool TrySplitAddress(string address, out string host, out int port)
    {
        host = "";
        port = 0;
        var index = address.LastIndexOf(':');
        if (index <= 0 || !int.TryParse(address.AsSpan(index + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)
        {
            return false;
        }

        host = address[..index].Trim('[', ']');
        return host.Length > 0;
    }

    /// <summary>把地址格式化成 <c>host:port</c>(IPv6 加方括号)。</summary>
    public static string FormatAddress(string host, int port) =>
        host.Contains(':', StringComparison.Ordinal) ? $"[{host}]:{port}" : $"{host}:{port}";

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryFromBase64Url(string text, out byte[] data)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            data = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            data = [];
            return false;
        }
    }
}
