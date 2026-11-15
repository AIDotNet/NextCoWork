namespace Ncw.Client.Accounts;

/// <summary>
/// 从系统收到的 <c>nextcowork://</c> 链接(docs/design/07 §2「跳转链接路由」)。只有白名单里的路由才会被解析出来,其他一律是 null(忽略)。
/// 链接来自系统和别的程序,<b>不可信</b>:解析器只做结构检查并取出参数,任何带副作用的动作(登录换码、配对)都要在界面上由用户确认或由 state 校验兜底。
/// </summary>
public abstract record NcwLink;

/// <summary><c>nextcowork://auth/callback?code=…&amp;state=…</c>(或 <c>?error=…&amp;state=…</c>):登录回调。</summary>
public sealed record AuthCallbackLink(string? Code, string? State, string? Error, string? ErrorDescription) : NcwLink;

/// <summary><c>nextcowork://pair?invite=…</c>:扫配对二维码之后跳进来。只带邀请内容;配对必须由用户在界面上确认,不会自动执行。</summary>
public sealed record PairLink(string Invite) : NcwLink;

/// <summary><c>nextcowork://open?…</c>:打开项目或会话(后续版本)。当前只解析、不执行。</summary>
public sealed record OpenLink(IReadOnlyDictionary<string, string> Query) : NcwLink;

/// <summary>
/// 跳转链接解析(纯函数)。严格白名单:scheme 必须正好是 <c>nextcowork</c>,不能带用户信息 / 端口 / 片段,路由只有 <c>auth/callback</c>、<c>pair</c>、<c>open</c>;
/// 总长度、参数个数、单个参数长度都有上限;重复的参数名、控制字符、非法的百分号编码一律拒绝(返回 null)。
/// </summary>
public static class NcwLinkParser
{
    public const string Scheme = "nextcowork";

    public const int MaxLength = 4096;

    public const int MaxParameters = 16;

    public const int MaxValueLength = 2048;

    public static NcwLink? Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength || raw.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return null;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Scheme, StringComparison.Ordinal)
            || uri.UserInfo.Length > 0 || !uri.IsDefaultPort || uri.Fragment.Length > 0 || raw.Contains('#', StringComparison.Ordinal)
            || uri.Host.Length == 0)
        {
            return null;
        }

        // 用原始字符串里的查询部分:Uri.Query 会把非法的 % 悄悄改成 %25,那样就看不出畸形编码了
        var question = raw.IndexOf('?', StringComparison.Ordinal);
        var query = ParseQuery(question < 0 ? "" : raw[question..]);
        if (query is null)
        {
            return null;
        }

        var path = uri.AbsolutePath;
        switch (uri.Host.ToLowerInvariant())
        {
            case "auth" when path == "/callback":
                query.TryGetValue("code", out var code);
                query.TryGetValue("state", out var state);
                query.TryGetValue("error", out var error);
                query.TryGetValue("error_description", out var description);
                // 要么是出错(带 error),要么是成功(code 和 state 都要有);别的形状不是授权回调
                if (error is { Length: > 0 } || (code is { Length: > 0 } && state is { Length: > 0 }))
                {
                    return new AuthCallbackLink(Empty(code), Empty(state), Empty(error), Empty(description));
                }

                return null;
            case "pair" when path is "" or "/":
                return query.TryGetValue("invite", out var invite) && invite.Length > 0 ? new PairLink(invite) : null;
            case "open" when path is "" or "/":
                return new OpenLink(query);
            default:
                return null;
        }
    }

    private static bool WellEncoded(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '%' && (i + 2 >= text.Length || !char.IsAsciiHexDigit(text[i + 1]) || !char.IsAsciiHexDigit(text[i + 2])))
            {
                return false;
            }
        }

        return true;
    }

    private static string? Empty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static Dictionary<string, string>? ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.Length <= 1)
        {
            return result;
        }

        foreach (var pair in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Count >= MaxParameters)
            {
                return null;
            }

            var separator = pair.IndexOf('=', StringComparison.Ordinal);
            var rawName = separator < 0 ? pair : pair[..separator];
            var rawValue = separator < 0 ? "" : pair[(separator + 1)..];
            if (!WellEncoded(rawName) || !WellEncoded(rawValue))
            {
                return null; // 不完整 / 非法的百分号编码(.NET 会原样放过 %zz,这里要拒绝)
            }

            string name, value;
            try
            {
                name = Uri.UnescapeDataString(rawName.Replace('+', ' '));
                value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return null;
            }

            if (name.Length == 0 || value.Length > MaxValueLength || name.Any(char.IsControl) || value.Any(char.IsControl) || !result.TryAdd(name, value))
            {
                return null; // 空名字、过长、控制字符、重复的参数名(参数污染)
            }
        }

        return result;
    }
}
