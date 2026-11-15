using System.Net;
using Ncw.Agent.WebTools.Net;

namespace Ncw.Agent.WebTools.Tests;

/// <summary>地址判断(移植自 2.x <c>ssrf.test.ts</c> 的 <c>isPrivateAddress</c> 全部用例,并补 3.0 的:更宽的 IPv4 范围、IPv6 内嵌 IPv4 的各种形式、<c>inet_aton</c> 传统写法)。</summary>
public sealed class AddressPolicyTests
{
    [Theory]
    // 环回 / 0.0.0.0 / 私网 / 运营商级 NAT / 链路本地(云元数据)/ 协议专用 / 基准测试 / 文档 / 组播与保留
    [InlineData("0.0.0.0")]
    [InlineData("0.1.2.3")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("100.64.0.1")]
    [InlineData("100.100.100.200")] // 阿里云元数据
    [InlineData("100.127.255.255")]
    [InlineData("169.254.169.254")] // ★ AWS / GCP / Azure 元数据
    [InlineData("169.254.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.1")]
    [InlineData("192.168.0.1")]
    [InlineData("192.168.255.255")]
    [InlineData("198.18.0.1")]
    [InlineData("198.19.255.255")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.250")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    public void Blocks_ipv4(string ip) => Assert.True(AddressPolicy.IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("100.63.255.255")] // 100.64/10 之外
    [InlineData("100.128.0.1")]
    [InlineData("172.15.255.255")] // 172.16/12 之外
    [InlineData("172.32.0.1")]
    [InlineData("169.253.1.1")]
    [InlineData("169.255.1.1")]
    [InlineData("192.0.1.1")] // 192.0.0/24 是 /24,不是 /16
    [InlineData("192.169.0.1")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.1")]
    [InlineData("11.0.0.1")]
    [InlineData("223.255.255.255")]
    public void Allows_public_ipv4(string ip) => Assert.False(AddressPolicy.IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("::2")] // ::/96 兼容地址
    [InlineData("fc00::1")]
    [InlineData("fd00:ec2::254")] // AWS IPv6 元数据
    [InlineData("fdff:ffff::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001::1")] // Teredo
    [InlineData("100::1")]
    [InlineData("::ffff:127.0.0.1")] // IPv4 映射
    [InlineData("::ffff:7f00:1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::7f00:1")] // NAT64 内嵌 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")] // NAT64 内嵌 169.254.169.254
    [InlineData("64:ff9b:1::1")] // 本地用途 NAT64
    [InlineData("2002:7f00:1::1")] // 6to4 内嵌 127.0.0.1
    [InlineData("2002:a9fe:a9fe::1")] // 6to4 内嵌 169.254.169.254
    [InlineData("2002:0a00:0001::")] // 6to4 内嵌 10.0.0.1
    public void Blocks_ipv6(string ip) => Assert.True(AddressPolicy.IsBlocked(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("::ffff:8.8.8.8")] // 映射的公网地址放行
    [InlineData("64:ff9b::808:808")] // NAT64 内嵌 8.8.8.8
    [InlineData("2002:0808:0808::1")] // 6to4 内嵌 8.8.8.8
    [InlineData("2a00:1450:4001::1")]
    public void Allows_public_ipv6(string ip) => Assert.False(AddressPolicy.IsBlocked(IPAddress.Parse(ip)), ip);

    // ───────────────────────── 字面量解析 ─────────────────────────

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("2130706433", "127.0.0.1")] // 十进制整数
    [InlineData("0x7f000001", "127.0.0.1")] // 十六进制整数
    [InlineData("017700000001", "127.0.0.1")] // 八进制整数
    [InlineData("0177.0.0.1", "127.0.0.1")] // 八进制点分
    [InlineData("0x7f.0.0.1", "127.0.0.1")] // 十六进制点分
    [InlineData("0x7f.1", "127.0.0.1")] // 两段缩写
    [InlineData("127.1", "127.0.0.1")]
    [InlineData("127.0.1", "127.0.0.1")]
    [InlineData("0", "0.0.0.0")]
    [InlineData("0x0.0x0.0x0.0x0", "0.0.0.0")]
    [InlineData("3232235777", "192.168.1.1")]
    [InlineData("2852039166", "169.254.169.254")] // 元数据的整数写法
    [InlineData("0251.0376.0251.0376", "169.254.169.254")] // 元数据的八进制写法
    [InlineData("127.0.0.1.", "127.0.0.1")] // 末尾的点
    [InlineData("[::1]", "::1")]
    [InlineData("::1", "::1")]
    [InlineData("[::ffff:127.0.0.1]", "::ffff:127.0.0.1")]
    [InlineData("[fe80::1%25eth0]", "fe80::1")] // 区域 id 被去掉
    [InlineData("[fe80::1%eth0]", "fe80::1")]
    [InlineData("134744072", "8.8.8.8")] // 公网地址的整数写法也认得(只是不拦)
    public void Parses_every_literal_spelling_the_system_resolver_would_accept(string host, string expected)
    {
        Assert.True(AddressPolicy.TryParseLiteral(host, out var ip), host);
        Assert.Equal(IPAddress.Parse(expected), ip);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("localhost")]
    [InlineData("")]
    [InlineData("1.2.3.4.5")]
    [InlineData("256.1.1.1")]
    [InlineData("1.2.3.256")]
    [InlineData("08.0.0.1")] // 八进制里没有 8
    [InlineData("0xg.0.0.1")]
    [InlineData("4294967296")] // 超出 32 位
    [InlineData("1..2")]
    [InlineData("::gggg")]
    [InlineData("[example.com]")]
    [InlineData("a.b.c.d")]
    public void Names_and_malformed_text_are_not_literals(string host) => Assert.False(AddressPolicy.TryParseLiteral(host, out _), host);

    [Fact]
    public void Every_private_spelling_of_loopback_and_metadata_ends_up_blocked()
    {
        string[] spellings = ["2130706433", "0177.0.0.1", "0x7f.0.0.1", "127.1", "0x7f000001", "017700000001", "2852039166", "0251.0376.0251.0376", "[::1]", "[::ffff:127.0.0.1]", "[::ffff:7f00:1]", "[0:0:0:0:0:0:0:1]", "[::]", "0"];
        foreach (var spelling in spellings)
        {
            Assert.True(AddressPolicy.TryParseLiteral(spelling, out var ip), spelling);
            Assert.True(AddressPolicy.IsBlocked(ip), spelling);
        }
    }
}
