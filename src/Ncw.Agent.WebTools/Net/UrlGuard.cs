namespace Ncw.Agent.WebTools.Net;

/// <summary>
/// 请求之前的 URL 字面量筛查(2.x <c>ssrf.ts</c> 的 <c>ssrfRisk</c>):协议白名单、拒绝带用户名密码的 URL、拒绝本机 / 内网的主机写法。
/// 有风险就返回给模型看的说明,没有返回 <c>null</c>。
/// <para>
/// 这只是第一道:它只看字面量,看不见 <c>public.example</c> 解析成 <c>127.0.0.1</c>。第二道是 <see cref="PinnedConnector"/>——在<b>连接的那一刻</b>解析、校验、
/// 把连接钉死在校验过的 IP 上(防 DNS 重绑定:校验和连接之间没有第二次解析);重定向的每一跳都重新过这两道。
/// </para>
/// </summary>
public static class UrlGuard
{
    /// <summary>只在本机 / 内网有意义的域名后缀(和 2.x 同一份,另加 <c>.localdomain</c>、<c>.corp</c>、<c>.home</c>)。</summary>
    private static readonly string[] LocalSuffixes = [".localhost", ".local", ".internal", ".intranet", ".home.arpa", ".lan", ".localdomain", ".corp", ".home"];

    public static string? Risk(Uri url, WebPolicy? policy = null)
    {
        if (url.Scheme is not ("http" or "https"))
        {
            return $"Only http and https are supported, not \"{url.Scheme}:\". To read a local file use Read — do not reach for file:// style addresses.";
        }

        // ★ URL 里带用户名密码的话,凭证会随请求发出去。模型不该有能力把一份凭证塞进一次它自己发起的请求里——那是一条现成的外发通道。回显里不带密码本身。
        if (url.UserInfo.Length > 0)
        {
            return "The URL must not carry a username or password. Remove the \"user:pass@\" part and try again.";
        }

        if (policy?.AllowPrivateAddresses == true)
        {
            return null;
        }

        var host = (url.IdnHost.Length > 0 ? url.IdnHost : url.Host).ToLowerInvariant().TrimEnd('.');
        if (host.Length == 0)
        {
            return RefuseLocal(url.Host);
        }

        if (AddressPolicy.TryParseLiteral(host, out var ip))
        {
            return AddressPolicy.IsBlocked(ip) ? RefuseLocal(url.Host) : null;
        }

        if (host.StartsWith('['))
        {
            return RefuseLocal(url.Host); // 写成 [..] 却不是合法 IPv6:不放行
        }

        if (host == "localhost" || LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)))
        {
            return RefuseLocal(url.Host);
        }

        // ★ 单标签主机名(不含点的,像 router、nas)只可能靠内网 DNS 搜索域解析出来,一定指向内网设备
        if (!host.Contains('.'))
        {
            return RefuseLocal(url.Host);
        }

        // 末段全是数字(1.2.3.4.5 之类解析不成地址的写法):没有任何合法的顶级域长这样,也是各种解析器行为不一的花招
        var tld = host[(host.LastIndexOf('.') + 1)..];
        if (tld.All(char.IsAsciiDigit))
        {
            return RefuseLocal(url.Host);
        }

        return null;
    }

    private static string RefuseLocal(string host) =>
        $"Refusing to reach \"{host}\": this is a loopback or private-network address, and network tools may only reach the public internet. "
        + "Addresses like this usually sit in front of unauthenticated debug ports, internal devices, or a cloud provider's instance metadata endpoint — "
        + "not something a model-initiated request should touch. If a local service really is the target, ask the user to open it themselves, or to explicitly ask you to use Bash for it.";
}
