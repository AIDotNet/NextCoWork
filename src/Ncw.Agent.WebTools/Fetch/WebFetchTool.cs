using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;
using Ncw.Agent.Tools;
using Ncw.Agent.Tools.Schema;
using Ncw.Agent.WebTools.Net;

namespace Ncw.Agent.WebTools.Fetch;

/// <summary>WebFetch 的上限。生产用默认值;测试用它缩小(超时 / 字节数 / 字符数)。</summary>
public sealed record WebFetchLimits
{
    /// <summary>整次抓取(含所有重定向跳与读正文)的墙钟预算。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>最多跟几跳重定向。和浏览器的常规上限一致。</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>下载字节上限:读到这个数就停(流式,不是读完再截),半页内容通常也够用,标记「只读了开头」。</summary>
    public int MaxBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>交给模型的正文字符上限。</summary>
    public int MaxTextChars { get; init; } = 100_000;
}

/// <summary>
/// <c>WebFetch</c>(2.x <c>web.ts</c>)—— 取一个网页回来给模型读。名字和 <c>url</c> / <c>prompt</c> 两个参数对齐 Claude Code。
/// <para>
/// ★ 语义差异在描述第一段说清:CC 的 WebFetch 背后有个小模型按 <c>prompt</c> 读完整页再给答案;我们没有,返回的是<b>页面正文本身</b>。
/// <c>prompt</c> 仍然有用:它被原样放在结果开头,一页几万字符倒进上下文之后模型很容易忘了自己本来要找什么。
/// </para>
/// <para>
/// 安全(03 §13):协议白名单、拒绝带凭据的 URL、<b>拒绝环回 / 内网 / 链路本地 / 元数据地址</b>(2.x 曾按工作区所有者的要求关掉这一段,3.0 不继承);
/// 地址校验分两道——URL 字面量(<see cref="UrlGuard"/>)和连接时的解析 + 钉死 IP(<see cref="PinnedConnector"/>,防 DNS 重绑定);重定向<b>逐跳</b>都重新过两道;
/// 跨主机重定向不默默跟,把新地址交还给模型;content-type 白名单 + 字节上限 + 总超时。
/// </para>
/// 联网这件事不靠 <c>ReadOnly</c> 管,靠权限闸那张表的第 1 行(<c>NetworkSwitchTools</c>,「联网搜索」开关):那一行排在只读放行之前。
/// 抓取从 Agent 所在设备发出,不经项目的执行环境(SSH 项目也一样);Details 仍带项目的执行环境标识,工具卡片与其它工具一致。
/// </summary>
public sealed class WebFetchTool : BuiltinTool
{
    private readonly IWebHttp _http;
    private readonly WebFetchLimits _limits;
    private readonly WebPolicy _policy;

    public WebFetchTool(IWebHttp http, WebFetchLimits? limits = null, WebPolicy? policy = null)
        : base(
            "WebFetch",
            "Fetches the content at a URL and converts it to text for you.\n\n"
            + "- IMPORTANT: this returns THE PAGE ITSELF, not an answer summarized for you. Extracting, judging, and quoting are your job\n"
            + "- Say in prompt what you are looking for; it is placed above the text so you can check yourself against it\n"
            + "- Only http and https are supported. http is upgraded to https automatically\n"
            + "- Only textual content is accepted (HTML, plain text, JSON, XML, …). PDFs, images, and archives are refused\n"
            + "- Text longer than 100k characters is truncated\n"
            + "- HTML is stripped to readable text; table and nested-list structure may be lost\n"
            + "- Loopback, private-network, link-local and cloud-metadata addresses are refused, whether written as an address or reached through a host name\n"
            + "- user:pass@ credentials in the URL are refused\n"
            + "- Pages behind a login cannot be fetched. If you get a login page, do NOT retry — tell the user\n"
            + "- A redirect to a different host is refused and the new address is handed back to you; call again with that address if you want it",
            S.Build(S.Object(
                ["url", "prompt"],
                ("url", S.String("The URL to fetch. Must be a complete http/https address", minLength: 1)),
                ("prompt", S.String("What you are looking for on this page. It is echoed verbatim at the top of the result so you stay on target after reading a long page", minLength: 1, maxLength: 2000)))),
            // ReadOnly:它确实不改变任何东西。联网由权限闸第 1 行管(NeedsNetwork 只是如实声明,工具快照与权限扩展读的是 PermissionGate.NetworkSwitchTools 那张名单)。
            new ToolTraits(ReadOnly: true, Destructive: false, NeedsNetwork: true),
            "Fetch a web page and read it as text",
            ["Use WebFetch for the content of a specific URL; use web_search first when you do not know the address."])
    {
        _http = http;
        _limits = limits ?? new WebFetchLimits();
        _policy = policy ?? WebPolicy.Default;
    }

    public const string UserAgent = "Mozilla/5.0 (compatible; NextCoWork/3.0; +https://nextco.work)";

    protected override async ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct)
    {
        var rt = ctx.Runtime;
        var raw = RequiredString(args, "url").Trim();
        var prompt = RequiredString(args, "prompt");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url))
        {
            return Failed(rt, raw, $"Malformed URL: \"{raw}\". Give a complete address, e.g. https://example.com/page.");
        }

        // 和 CC 一致:http 升级成 https。降级传输里的内容会被中间人改写。
        if (url.Scheme == Uri.UriSchemeHttp)
        {
            url = new UriBuilder(url) { Scheme = Uri.UriSchemeHttps, Port = url.IsDefaultPort ? -1 : url.Port }.Uri;
        }

        if (UrlGuard.Risk(url, _policy) is { } firstRisk)
        {
            return Failed(rt, url.ToString(), firstRisk);
        }

        ctx.Progress.Report(new ToolProgress($"Fetching {url.Host}"));

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_limits.Timeout);
        try
        {
            return await FetchAsync(rt, url, prompt, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw; // 用户点停止:原样抛出,不包成一个普通失败
            }

            return Failed(rt, url.ToString(), $"The request to {url} timed out after {_limits.Timeout.TotalSeconds:0} seconds.");
        }
        catch (SsrfBlockedException e)
        {
            return Failed(rt, url.ToString(), e.Message + " If a local service really is the target, ask the user to open it themselves.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            return Failed(rt, url.ToString(), $"The request to {url} failed: {e.Message}. Check the address or find another source — retrying the same URL will not help.");
        }
    }

    private async Task<ToolResult> FetchAsync(ToolRuntime rt, Uri start, string prompt, CancellationToken ct)
    {
        var current = start;
        HttpResponseMessage? res = null;
        try
        {
            for (var hop = 0; hop <= _limits.MaxRedirects; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,text/plain,application/json;q=0.9");
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                res?.Dispose();
                res = await _http.SendAsync(request, ct).ConfigureAwait(false);

                var status = (int)res.StatusCode;
                if (status is < 300 or >= 400)
                {
                    break;
                }

                if (res.Headers.Location is not { } location)
                {
                    break;
                }

                if (!Uri.TryCreate(current, location, out var next))
                {
                    return Failed(rt, current.ToString(), $"The server returned a redirect address that could not be parsed: \"{location}\".");
                }

                // 每一跳都重新过一遍闸,和第一次一模一样的标准(协议白名单 + 凭证拦截 + 本机 / 内网);连接时还有第二道(解析后校验并钉死 IP)
                if (UrlGuard.Risk(next, _policy) is { } risk)
                {
                    return Failed(rt, current.ToString(), $"The redirect target is not allowed. {risk}");
                }

                // 跨主机重定向停下来问模型,而不是默默跟过去:它会改变「我读到的内容来自哪个域名」,而模型多半按最初那个域名的可信度对待读回来的内容。
                if (!string.Equals(next.IdnHost, current.IdnHost, StringComparison.OrdinalIgnoreCase))
                {
                    return ToolResult.Text(
                        $"This address redirects to a different host: {next}\nNothing was fetched. If that new address is what you want, call WebFetch again with it.",
                        Details(rt, "webFetch", w =>
                        {
                            w.WriteString("url", start.ToString());
                            w.WriteString("redirectTo", next.ToString());
                        }));
                }

                current = next;
            }

            if (res is null)
            {
                return Failed(rt, start.ToString(), "The request produced no response at all.");
            }

            if (!res.IsSuccessStatusCode)
            {
                var code = (int)res.StatusCode;
                var advice = code is 401 or 403
                    ? "This page needs a login or you do not have access. Retrying will not help — tell the user."
                    : code == 404 ? "The address does not exist. Check the URL, or find another source." : "";
                return Failed(rt, current.ToString(), $"HTTP {code} {res.ReasonPhrase} ({current}). {advice}".TrimEnd(), code);
            }

            var contentType = res.Content.Headers.ContentType?.ToString() ?? "";
            if (!HtmlText.IsTextual(contentType))
            {
                return Failed(
                    rt,
                    current.ToString(),
                    $"This address returned \"{(contentType.Length == 0 ? "an unknown type" : contentType)}\", which is not textual content, so it was refused. "
                    + "This tool only reads HTML, plain text, JSON, XML and the like. For a PDF, an image, or an archive, ask the user to download it into the workspace and open it with Read.");
            }

            var (bytes, truncatedBytes) = await BoundedBody.ReadAsync(res.Content, _limits.MaxBytes, ct).ConfigureAwait(false);
            var decoded = Decode(bytes, res.Content.Headers.ContentType);
            var looksHtml = contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || HtmlText.LooksLikeHtml(decoded);
            var text = HtmlText.StripControlChars(looksHtml ? HtmlText.ToText(decoded) : decoded).Trim();

            if (text.Length == 0)
            {
                return ToolResult.Text(
                    $"{current} was fetched successfully but has no readable text. It is most likely rendered entirely by JavaScript, which this tool cannot execute. Find another source.",
                    Details(rt, "webFetch", w => WriteSummary(w, start, current, (int)res.StatusCode, contentType, bytes.Length, truncatedBytes, false)));
            }

            var body = Clip.WithEllipsis(text, _limits.MaxTextChars);
            var truncatedText = body.Length < text.Length;
            var notes = new List<string>();
            if (truncatedText)
            {
                notes.Add("text was too long and has been truncated");
            }

            if (truncatedBytes)
            {
                notes.Add($"the response exceeded {Clip.HumanSize(_limits.MaxBytes)}, so only the start was read");
            }

            var status2 = (int)res.StatusCode;
            var message = $"You are looking for: {prompt}\n\nContent of {current}{(notes.Count > 0 ? $" ({string.Join("; ", notes)})" : "")}:\n\n{body}";
            return ToolResult.Text(message, Details(rt, "webFetch", w => WriteSummary(w, start, current, status2, contentType, bytes.Length, truncatedBytes, truncatedText)))
                with { Truncated = truncatedText || truncatedBytes };
        }
        finally
        {
            res?.Dispose();
        }
    }

    private static void WriteSummary(Utf8JsonWriter w, Uri requested, Uri finalUrl, int status, string contentType, int bytes, bool truncatedBytes, bool truncatedText)
    {
        w.WriteString("url", requested.ToString());
        w.WriteString("finalUrl", finalUrl.ToString());
        w.WriteNumber("status", status);
        w.WriteString("contentType", contentType);
        w.WriteNumber("bytes", bytes);
        w.WriteBoolean("truncatedBytes", truncatedBytes);
        w.WriteBoolean("truncatedText", truncatedText);
    }

    private static ToolResult Failed(ToolRuntime rt, string url, string message, int? status = null) =>
        ToolResult.Error(message, Details(rt, "webFetch", w =>
        {
            w.WriteString("url", url);
            if (status is { } s)
            {
                w.WriteNumber("status", s);
            }
        }));

    private static string Decode(byte[] bytes, MediaTypeHeaderValue? contentType)
    {
        var encoding = Encoding.UTF8;
        if (contentType?.CharSet is { Length: > 0 } charset)
        {
            encoding = Charsets.Find(charset) ?? Encoding.UTF8;
        }

        var text = encoding.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }
}

/// <summary>按上限<b>流式</b>读响应正文:读到上限就停(不把一个 500MB 的响应读完再截),返回是否被截断。</summary>
internal static class BoundedBody
{
    public static async Task<(byte[] Bytes, bool Truncated)> ReadAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (buffer.Length < maxBytes)
        {
            var want = (int)Math.Min(chunk.Length, maxBytes - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return (buffer.ToArray(), false);
            }

            buffer.Write(chunk, 0, read);
        }

        // 到上限了:再读一个字节判断是否还有更多(正好等于上限的响应不算截断)
        var probe = await stream.ReadAsync(chunk.AsMemory(0, 1), ct).ConfigureAwait(false);
        return (buffer.ToArray(), probe > 0);
    }
}

/// <summary>字符集名 → 编码。常见的中日韩编码(gbk / gb2312 / big5 / shift_jis / euc-kr)靠 .NET 共享框架自带的 CodePages 提供者;认不出的退回 UTF-8。</summary>
internal static class Charsets
{
    private static readonly Lazy<bool> Registered = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return true;
    });

    public static Encoding? Find(string name)
    {
        _ = Registered.Value;
        try
        {
            return Encoding.GetEncoding(name.Trim('"', '\'', ' '));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>截断小工具(不切开代理对;截断必须留下明确的标记,静默截断更糟)。</summary>
internal static class Clip
{
    public static string WithEllipsis(string text, int max, string marker = "...")
    {
        if (text.Length <= max)
        {
            return text;
        }

        var keep = Math.Max(0, max - marker.Length);
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return text[..keep] + marker;
    }

    public static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.0} MB",
    };
}
