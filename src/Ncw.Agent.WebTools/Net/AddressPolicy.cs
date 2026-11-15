using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Ncw.Agent.WebTools.Net;

/// <summary>
/// 「这个地址是不是不该让模型发起的请求去碰」—— 纯函数,零 IO,所以能把边界铺满(2.x <c>ssrf.ts</c> 的 <c>isPrivateAddress</c>,3.0 收紧并加上 DNS 之后的二次校验)。
/// <para>
/// 防的是:云厂商实例元数据(<c>169.254.169.254</c>、<c>fd00:ec2::254</c>、阿里云 <c>100.100.100.200</c>)、本机没有鉴权的调试端口 / 管理面板、家里和公司内网的设备。
/// 这个 URL 不一定是用户给的:它可以来自被投毒的网页、Skill 正文、MCP 工具的返回值,所以不能指望「模型不会去请求奇怪的地址」。
/// </para>
/// 阻断的范围比 2.x 更宽(3.0 默认不放行任何内网;2.x 曾按工作区所有者的要求关掉过这道筛查,3.0 的设计文档不继承那次豁免,见 03 §13):
/// 0/8、10/8、100.64/10、127/8、169.254/16、172.16/12、192.0.0/24、192.0.2/24、192.168/16、198.18/15、198.51.100/24、203.0.113/24、224/4 与 240/4;
/// IPv6 的 <c>::</c>、<c>::1</c>、<c>::/96</c>(兼容地址)、<c>fc00::/7</c>、<c>fe80::/10</c>、<c>fec0::/10</c>、<c>ff00::/8</c>、<c>2001:db8::/32</c>、<c>100::/64</c>、Teredo <c>2001::/32</c>,
/// 以及内嵌 IPv4 的形式(<c>::ffff:a.b.c.d</c> 映射、NAT64 <c>64:ff9b::/96</c> 与本地用途 <c>64:ff9b:1::/48</c>、6to4 <c>2002::/16</c>)按内嵌的那个 IPv4 判。
/// </summary>
public static class AddressPolicy
{
    public static bool IsBlocked(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsBlockedV4(address.GetAddressBytes());
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return true; // 不认识的地址族:不放行
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return IsBlockedV4(address.MapToIPv4().GetAddressBytes());
        }

        Span<byte> b = stackalloc byte[16];
        address.TryWriteBytes(b, out _);
        var first = (b[0] << 8) | b[1];

        if (IsZero(b[..12]))
        {
            return true; // ::、::1、::/96(IPv4 兼容地址,已废弃,没有任何合法用途)
        }

        // NAT64 64:ff9b::/96:内嵌 IPv4 在最后四字节
        if (first == 0x0064 && b[2] == 0xff && b[3] == 0x9b)
        {
            if (b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && IsZero(b[8..12]))
            {
                return IsBlockedV4(b[12..16]);
            }

            if (b[4] == 0 && b[5] == 1)
            {
                return true; // 64:ff9b:1::/48 本地用途
            }
        }

        if (first == 0x2002)
        {
            return IsBlockedV4(b[2..6]); // 6to4:内嵌 IPv4 在字节 2..5
        }

        if ((first & 0xfe00) == 0xfc00)
        {
            return true; // fc00::/7 唯一本地
        }

        if ((first & 0xffc0) == 0xfe80)
        {
            return true; // fe80::/10 链路本地
        }

        if ((first & 0xffc0) == 0xfec0)
        {
            return true; // fec0::/10 站点本地(已废弃)
        }

        if ((first & 0xff00) == 0xff00)
        {
            return true; // ff00::/8 组播
        }

        if (first == 0x2001 && b[2] == 0x0d && b[3] == 0xb8)
        {
            return true; // 2001:db8::/32 文档
        }

        if (first == 0x2001 && b[2] == 0 && b[3] == 0)
        {
            return true; // 2001::/32 Teredo
        }

        if (first == 0x0100 && IsZero(b[2..8]))
        {
            return true; // 100::/64 黑洞
        }

        return false;
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes)
    {
        foreach (var x in bytes)
        {
            if (x != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBlockedV4(ReadOnlySpan<byte> v)
    {
        int a = v[0], b = v[1], c = v[2];
        return a == 0                                  // 0.0.0.0/8
            || a == 10                                 // 私网
            || a == 127                                // 环回
            || (a == 100 && b is >= 64 and <= 127)     // 100.64/10 运营商级 NAT(含阿里云元数据 100.100.100.200)
            || (a == 169 && b == 254)                  // ★ 链路本地 —— 云元数据 169.254.169.254
            || (a == 172 && b is >= 16 and <= 31)      // 私网
            || (a == 192 && b == 0 && c is 0 or 2)     // 192.0.0/24 协议专用(★ 是 /24,不是 /16)、192.0.2/24 文档
            || (a == 192 && b == 168)                  // 私网
            || (a == 198 && b is 18 or 19)             // 基准测试
            || (a == 198 && b == 51 && c == 100)       // 文档
            || (a == 203 && b == 0 && c == 113)        // 文档
            || a >= 224;                               // 组播 + 保留(含 255.255.255.255)
    }

    /// <summary>
    /// 主机名字符串是不是一个 IP 字面量,包括各种花式写法:点分十进制、<c>[::1]</c>、带区域 id 的 IPv6,以及 <c>inet_aton</c> 的传统写法
    /// (整数 <c>2130706433</c>、八进制 <c>0177.0.0.1</c>、十六进制 <c>0x7f.1</c>、缩写 <c>127.1</c>)。
    /// 系统解析器(<c>getaddrinfo</c>)对这些写法也会当成地址,所以必须按同样的宽容度认出来。不是字面量返回 false(那是个域名,要解析之后再校验)。
    /// </summary>
    public static bool TryParseLiteral(string host, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var bare = host;
        if (bare.Length >= 2 && bare[0] == '[' && bare[^1] == ']')
        {
            bare = bare[1..^1];
        }

        if (bare.Contains(':'))
        {
            var zone = bare.IndexOf('%');
            if (zone >= 0)
            {
                bare = bare[..zone];
            }

            if (IPAddress.TryParse(bare, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6)
            {
                address = v6;
                return true;
            }

            return false;
        }

        if (TryParseLegacyV4(bare, out var v4))
        {
            address = v4;
            return true;
        }

        return false;
    }

    private static bool TryParseLegacyV4(string text, out IPAddress address)
    {
        address = IPAddress.None;
        var trimmed = text.TrimEnd('.'); // "127.0.0.1." 是同一个地址
        var parts = trimmed.Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }

        var numbers = new ulong[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParsePart(parts[i], out numbers[i]))
            {
                return false;
            }
        }

        // 最后一段填满剩下的字节;前面的每段是单字节
        ulong value = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (numbers[i] > 255)
            {
                return false;
            }

            value |= numbers[i] << (8 * (3 - i));
        }

        var remainingBytes = 4 - (parts.Length - 1);
        var limit = remainingBytes == 4 ? uint.MaxValue : (1UL << (8 * remainingBytes)) - 1;
        if (numbers[^1] > limit)
        {
            return false;
        }

        value |= numbers[^1];
        address = new IPAddress([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
        return true;
    }

    private static bool TryParsePart(string part, out ulong value)
    {
        value = 0;
        if (part.Length == 0)
        {
            return false;
        }

        var hex = false;
        var digits = part;
        if (part.Length > 2 && part[0] == '0' && (part[1] is 'x' or 'X'))
        {
            hex = true;
            digits = part[2..];
        }
        else if (part.Length > 1 && part[0] == '0')
        {
            // 八进制:有 8 / 9 就不是合法写法
            ulong acc = 0;
            foreach (var ch in part)
            {
                if (ch is < '0' or > '7')
                {
                    return false;
                }

                acc = (acc * 8) + (ulong)(ch - '0');
                if (acc > uint.MaxValue)
                {
                    return false;
                }
            }

            value = acc;
            return true;
        }

        foreach (var ch in digits)
        {
            if (hex ? !char.IsAsciiHexDigit(ch) : !char.IsAsciiDigit(ch))
            {
                return false;
            }
        }

        return ulong.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= uint.MaxValue;
    }
}
