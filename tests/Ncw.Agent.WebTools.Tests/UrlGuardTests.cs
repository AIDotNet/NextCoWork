using Ncw.Agent.WebTools.Net;

namespace Ncw.Agent.WebTools.Tests;

/// <summary>URL 字面量筛查(移植自 2.x <c>ssrf.test.ts</c> 的 <c>ssrfRisk</c> 全部用例:协议、凭证、主机名、URL 规范化;<c>allowPrivateAddresses</c> 在 3.0 只是测试用的受控策略)。</summary>
public sealed class UrlGuardTests
{
    private static string? Risk(string url, WebPolicy? policy = null) => UrlGuard.Risk(new Uri(url), policy);

    // ───────────────────────── 协议 ─────────────────────────

    [Fact]
    public void File_is_refused_and_points_to_Read()
    {
        var risk = Risk("file:///etc/passwd");
        Assert.NotNull(risk);
        Assert.Contains("Read", risk);
    }

    [Theory]
    [InlineData("ftp://example.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<h1>x</h1>")]
    [InlineData("gopher://example.com/")]
    [InlineData("ws://example.com/")]
    [InlineData("mailto:a@b.c")]
    public void Other_protocols_are_refused(string url) => Assert.NotNull(Risk(url));

    [Fact]
    public void Http_and_https_pass() => Assert.All(new[] { "http://example.com/", "https://example.com/a?b=c#d", "https://example.com:8443/" }, url => Assert.Null(Risk(url)));

    // ───────────────────────── 凭证 ─────────────────────────

    [Theory]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("https://user@example.com/")]
    [InlineData("https://:pass@example.com/")]
    public void Credentials_in_the_url_are_refused_because_they_are_an_outbound_channel_the_model_could_use(string url) =>
        Assert.Contains("username or password", Risk(url));

    [Fact]
    public void The_refusal_never_echoes_the_password() => Assert.DoesNotContain("hunter2", Risk("https://alice:hunter2@example.com/"));

    [Fact]
    public void Credentials_are_refused_even_when_private_addresses_are_allowed()
    {
        var policy = new WebPolicy { AllowPrivateAddresses = true };
        Assert.NotNull(Risk("http://user:pass@127.0.0.1/", policy));
        Assert.NotNull(Risk("ftp://user:pass@127.0.0.1/x", policy)); // 协议先拒
    }

    // ───────────────────────── 主机名 ─────────────────────────

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://LOCALHOST:3000/")]
    [InlineData("http://localhost./")] // 末尾的点
    [InlineData("http://foo.localhost/")]
    [InlineData("http://a.b.localhost./")]
    [InlineData("http://printer.local/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")]
    [InlineData("http://corp.intranet/")]
    [InlineData("http://router.home.arpa/")]
    [InlineData("http://nas.lan/")]
    [InlineData("http://box.localdomain/")]
    [InlineData("http://router/")] // ★ 单标签主机名只可能靠内网 DNS 搜索域解析
    [InlineData("http://nas/admin")]
    [InlineData("http://1.2.3.4.5/")] // 末段全是数字,没有任何合法的顶级域长这样
    public void Local_names_are_refused(string url) => Assert.NotNull(Risk(url));

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/iam/")]
    [InlineData("http://100.100.100.200/latest/meta-data/")]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fd00:ec2::254]/latest/meta-data/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    [InlineData("http://[::ffff:169.254.169.254]/")]
    [InlineData("http://[64:ff9b::7f00:1]/")]
    [InlineData("http://[2002:7f00:1::1]/")]
    public void Private_addresses_are_refused(string url) => Assert.NotNull(Risk(url));

    [Theory]
    // ★ URL 解析会(或我们自己会)把各种 IPv4 花式写法规范化成同一个地址,字面量筛查对它们全部有效
    [InlineData("http://2130706433/")] // 十进制整数 = 127.0.0.1
    [InlineData("http://0x7f000001/")] // 十六进制整数
    [InlineData("http://0177.0.0.1/")] // 八进制
    [InlineData("http://0x7f.0.0.1/")]
    [InlineData("http://127.1/")]
    [InlineData("http://127.0.1/")]
    [InlineData("http://017700000001/")]
    [InlineData("http://2852039166/")] // 169.254.169.254
    [InlineData("http://0251.0376.0251.0376/")]
    [InlineData("http://0/")] // 0.0.0.0
    [InlineData("http://3232235777/")] // 192.168.1.1
    public void Every_ipv4_spelling_is_normalised_and_refused(string url) => Assert.NotNull(Risk(url));

    [Theory]
    [InlineData("http://１２７.０.０.１/")] // 全角数字(NFKC 后是 127.0.0.1)
    [InlineData("http://127。0。0。1/")] // 全角句号
    public void Unicode_lookalikes_do_not_slip_through_as_names(string url)
    {
        // 不论 Uri 把它规范化成地址(被字面量判断拦下),还是留作名字(没有合法 TLD / 解析不出来):都不能被当成「公网域名」放行到连接这一步之前不受任何检查。
        // 连接时的解析结果校验(PinnedConnector)是第二道;这里要求至少一道成立:要么被拒,要么 Uri 压根不接受。
        Uri? uri;
        try
        {
            uri = new Uri(url);
        }
        catch (UriFormatException)
        {
            return;
        }

        var risk = UrlGuard.Risk(uri);
        if (risk is null)
        {
            // 没被字面量筛查拦下:它必须是「作为名字走解析 + 连接时校验」的东西,而不是已经成了环回字面量
            Assert.False(AddressPolicy.TryParseLiteral(uri.IdnHost, out var ip) && AddressPolicy.IsBlocked(ip));
        }
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://www.example.co.uk/a")]
    [InlineData("https://sub.domain.example.org:8443/x")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://[2606:4700:4700::1111]/")]
    [InlineData("http://134744072/")] // 8.8.8.8 的整数写法,公网,不拦
    [InlineData("https://localhost.example.com/")] // 只是包含 localhost 字样的公网域名
    [InlineData("https://internal.example.com/")]
    [InlineData("https://xn--fsq.example/")] // IDN
    public void Public_hosts_pass(string url) => Assert.Null(Risk(url));

    [Fact]
    public void The_port_does_not_change_the_verdict()
    {
        Assert.Null(Risk("https://example.com:1/"));
        Assert.Null(Risk("https://example.com:65535/"));
        Assert.NotNull(Risk("http://127.0.0.1:1/"));
    }

    [Fact]
    public void Refusing_a_local_host_says_why_and_offers_a_way_out()
    {
        var risk = Risk("http://localhost:3000/");
        Assert.Contains("loopback or private-network", risk);
        Assert.Contains("ask the user", risk);
    }

    // ───────────────────────── 受控策略 ─────────────────────────

    [Fact]
    public void The_default_policy_never_allows_private_addresses() => Assert.False(WebPolicy.Default.AllowPrivateAddresses);

    [Fact]
    public void The_test_only_policy_lets_local_hosts_through_but_not_protocols()
    {
        var policy = new WebPolicy { AllowPrivateAddresses = true };
        Assert.Null(Risk("http://127.0.0.1:8080/", policy));
        Assert.Null(Risk("http://localhost/", policy));
        Assert.NotNull(Risk("file:///etc/passwd", policy));
        Assert.NotNull(Risk("ftp://127.0.0.1/", policy));
    }
}
