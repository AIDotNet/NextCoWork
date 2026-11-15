namespace Ncw.Agent.Browser;

/// <summary>
/// 浏览器工具能打开哪些地址。返回 <c>null</c> = 放行,否则是给模型看的拒绝说明。
/// 默认实现只做协议白名单和「不带凭据」;装配方可以换成更严的(例如接 <c>Ncw.Agent.WebTools</c> 的 UrlGuard 拦内网)。
/// </summary>
public interface IBrowserUrlPolicy
{
    string? Check(Uri url);
}

/// <summary>
/// 默认策略(沿用 2.x 的取舍):
/// <list type="bullet">
/// <item><c>http</c> / <c>https</c> / <c>about:blank</c> 放行;<c>file://</c> 放行(2.x 2026-09-22 的需求:本地生成的 HTML 报告要能直接看);其余协议(<c>javascript:</c>、<c>data:</c>、<c>ftp:</c>、<c>chrome:</c>……)拒绝;</item>
/// <item>URL 内嵌 <c>user:pass@</c> 拒绝(模型不该有能力把一份凭据塞进自己发起的请求);对 <c>file://</c> 同样拒绝;</item>
/// <item><b>不拦环回 / 内网地址</b>:浏览器工具的主要用途之一就是看本机的开发服务器(2.x 2026-09-20 的需求)。风险:被投毒的页面内容可以诱导模型打开本机没鉴权的调试端口或云元数据端点,
/// 页面内容随后经快照 / 截图回到上下文。这和 <c>WebFetch</c> 不同(那边拒绝内网);要收紧就换策略,不要改这里。</item>
/// </list>
/// 这道闸只管模型给出的那个 URL;页面自己发起的跳转与子资源请求不在它的范围内(浏览器内部的事)。
/// </summary>
public sealed class DefaultBrowserUrlPolicy : IBrowserUrlPolicy
{
    public static DefaultBrowserUrlPolicy Instance { get; } = new();

    public string? Check(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            return "Give a complete address, e.g. https://example.com/page.";
        }

        var scheme = url.Scheme;
        var allowed = scheme is "http" or "https" or "file" || (scheme == "about" && url.AbsoluteUri == "about:blank");
        if (!allowed)
        {
            return $"Only http, https and file URLs can be opened in the browser, not \"{scheme}:\".";
        }

        if (!string.IsNullOrEmpty(url.UserInfo))
        {
            return "The URL must not carry a username or password. Remove the \"user:pass@\" part and try again.";
        }

        return null;
    }
}
