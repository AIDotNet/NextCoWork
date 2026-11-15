namespace Ncw.Core.Domain;

/// <summary>附件的寻址(2.x 的 <c>shared/domain/attachment.ts</c>):<see cref="Scope"/> 是 <c>session</c> / <c>theme</c> / <c>export</c>;session / export 必须有 <see cref="OwnerId"/>,theme 没有主人。<see cref="FileName"/> 是 ULID + 扩展名,<b>不是原始文件名</b>。</summary>
public sealed record AttachmentLocator(string Scope, string? OwnerId, string FileName);

/// <summary>
/// 附件的类型与寻址:上传落盘、<c>ncw://</c> 引用、清理三处共用的<b>唯一</b>词汇表(AGENTS.md §3 规则 3:同一个事实只有一个纯函数)。
/// <para>
/// <b>为什么路径拼接与解析必须是纯函数</b>:<see cref="ParseUrl"/> 是引用解析的安全边界的一半。解析 URL 和读文件写在同一个函数里,边界就只能靠端到端测试去验——
/// 而端到端测试跑不了 <c>%2e%2e%2f</c> 的几十种变体。拆成纯函数之后,逃逸校验是一个可以被穷举断言的表达式。
/// </para>
/// <para>
/// <b>磁盘文件名为什么是 ULID 而不是原始文件名</b>:用户提供的文件名绝不进路径——路径注入(<c>../../.ssh/id_rsa</c>)、重名覆盖(同一会话传两次 <c>截图.png</c>,
/// 前一条消息引用的图变成另一张)、跨平台(Windows 保留名、大小写不敏感、非法字符集)。原始名存进 <c>display_name</c> 给界面显示,磁盘上不认它。
/// </para>
/// </summary>
public static class Attachments
{
    public const string Session = "session";
    public const string Theme = "theme";
    public const string Export = "export";

    public const string Scheme = "ncw";
    public const string Host = "attachments";

    /// <summary>单个附件上限。更大的数据没有理由塞进一次对话输入(协议帧 4 MiB,所以走分块上传)。</summary>
    public const long MaxBytes = 32 * 1024 * 1024;

    /// <summary>草稿附件的回收宽限期:只回收「既是 draft 又超期」的。宁可多占几 MB,也不能让用户挑好图去倒杯水回来图没了。</summary>
    public static readonly TimeSpan DraftTtl = TimeSpan.FromDays(7);

    private static readonly Dictionary<string, string> ScopeDirs = new(StringComparer.Ordinal) { [Session] = "sessions", [Theme] = "themes", [Export] = "exports" };
    private static readonly Dictionary<string, string> DirScopes = new(StringComparer.Ordinal) { ["sessions"] = Session, ["themes"] = Theme, ["exports"] = Export };

    private static bool NeedsOwner(string scope) => scope != Theme;

    /// <summary>单个路径段是否安全:挡的不只是 <c>..</c>——空段(<c>a//b</c> 多一层)、<c>.</c>、分隔符(凭空多一级目录)、NUL(C 层截断)都不行。</summary>
    public static bool IsSafeSegment(string seg) =>
        seg.Length > 0 && seg != "." && seg != ".." && !seg.Contains('/') && !seg.Contains('\\') && !seg.Contains('\0');

    /// <summary>locator → 相对于附件根的路径,<b>只产出正斜杠</b>。非法 locator 返回 null(调用点都在需要「拒绝」的位置)。</summary>
    public static string? RelativePath(AttachmentLocator loc)
    {
        if (!ScopeDirs.TryGetValue(loc.Scope, out var dir) || !IsSafeSegment(loc.FileName))
        {
            return null;
        }

        if (NeedsOwner(loc.Scope))
        {
            return loc.OwnerId is { } owner && IsSafeSegment(owner) ? $"{dir}/{owner}/{loc.FileName}" : null;
        }

        // 无主 scope 带了 ownerId 是调用方搞错了,不静默忽略
        return loc.OwnerId is null ? $"{dir}/{loc.FileName}" : null;
    }

    /// <summary>每段单独编码(整串编码会把 <c>/</c> 也编掉,毁掉路径结构)。</summary>
    public static string? BuildUrl(AttachmentLocator loc) =>
        RelativePath(loc) is { } rel ? $"{Scheme}://{Host}/{string.Join('/', rel.Split('/').Select(Uri.EscapeDataString))}" : null;

    /// <summary>
    /// <c>ncw://</c> URL → locator。<b>非法一律返回 null</b>,没有第二种失败表达。
    /// ★ 解码在校验之前是必须的(<c>%2e%2e%2f</c> 解码出来就是 <c>../</c>,只在解码前检查等于没检查),解码后必须重新逐段校验——这两步的顺序是这个函数存在的全部意义。
    /// </summary>
    public static AttachmentLocator? ParseUrl(string raw)
    {
        // 直接解析原串,不经 Uri:Uri 会把畸形的 %zz 悄悄规范成 %25zz,「拒绝畸形转义」就成了摆设。
        var prefix = $"{Scheme}://{Host}/";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = raw[(prefix.Length - 1)..];
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        // 畸形百分号转义(% 后面不是两位十六进制)一律拒绝,不做尽力而为的修复(.NET 的 Unescape 会原样放过它们,而 JS 的 decodeURIComponent 会抛)。
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] == '%' && (i + 2 >= path.Length || !char.IsAsciiHexDigit(path[i + 1]) || !char.IsAsciiHexDigit(path[i + 2])))
            {
                return null;
            }
        }

        var decoded = Uri.UnescapeDataString(path);
        var segs = decoded.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length is < 2 or > 3 || !segs.All(IsSafeSegment) || !DirScopes.TryGetValue(segs[0], out var scope))
        {
            return null;
        }

        if (segs.Length == 3)
        {
            return NeedsOwner(scope) ? new AttachmentLocator(scope, segs[1], segs[2]) : null;
        }

        return NeedsOwner(scope) ? null : new AttachmentLocator(scope, null, segs[1]);
    }

    // ───────────────────────── MIME ─────────────────────────

    private static readonly Dictionary<string, string> ExtByMime = new(StringComparer.Ordinal)
    {
        ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/gif"] = ".gif", ["image/webp"] = ".webp", ["image/svg+xml"] = ".svg", ["application/pdf"] = ".pdf",
        ["text/plain"] = ".txt", ["text/markdown"] = ".md", ["application/json"] = ".json", ["application/zip"] = ".zip",
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["video/quicktime"] = ".mov", ["video/x-matroska"] = ".mkv",
    };

    private static readonly Dictionary<string, string> MimeByExt = new(StringComparer.Ordinal)
    {
        ["png"] = "image/png", ["jpg"] = "image/jpeg", ["jpeg"] = "image/jpeg", ["jfif"] = "image/jpeg", ["gif"] = "image/gif", ["webp"] = "image/webp", ["svg"] = "image/svg+xml",
        ["pdf"] = "application/pdf", ["txt"] = "text/plain", ["md"] = "text/markdown", ["json"] = "application/json", ["zip"] = "application/zip",
        ["mp4"] = "video/mp4", ["m4v"] = "video/mp4", ["webm"] = "video/webm", ["mov"] = "video/quicktime", ["mkv"] = "video/x-matroska",
    };

    /// <summary>上传与发送共用的图片白名单;MIME 别名(<c>image/jpg</c>、<c>image/x-png</c>……)不能让预览可见的图片在发送时被拒。</summary>
    public static string? NormalizeImageMime(string mime) => (mime.Split(';', 2)[0].Trim().ToLowerInvariant()) switch
    {
        "image/png" or "image/x-png" => "image/png",
        "image/jpeg" or "image/jpg" or "image/pjpeg" => "image/jpeg",
        "image/gif" => "image/gif",
        "image/webp" => "image/webp",
        _ => null,
    };

    /// <summary>只识别容器签名(不解码整张图);字节可纠正不可靠的扩展名与 MIME。</summary>
    public static string? ImageMimeOfBytes(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> png = [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.StartsWith(png))
        {
            return "image/png";
        }

        if (bytes.Length >= 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255)
        {
            return "image/jpeg";
        }

        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        return bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8) ? "image/webp" : null;
    }

    /// <summary>★ 未知 mime 落到 <c>.bin</c> 而不是没有扩展名:无扩展名的文件推不出类型,浏览器把它当下载而不是内联——mime 表的一个空缺会变成一个显示 bug。</summary>
    public static string ExtOfMime(string mime) => ExtByMime.GetValueOrDefault(NormalizeImageMime(mime) ?? mime.ToLowerInvariant(), ".bin");

    public static string MimeOfExt(string pathOrName)
    {
        var i = pathOrName.LastIndexOf('.');
        return i < 0 ? "application/octet-stream" : MimeByExt.GetValueOrDefault(pathOrName[(i + 1)..].ToLowerInvariant(), "application/octet-stream");
    }

    public static bool IsImageMime(string mime) => mime.Trim().StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public static bool IsVideoMime(string mime) => mime.Trim().StartsWith("video/", StringComparison.OrdinalIgnoreCase);
}
