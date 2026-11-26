using System.Buffers.Text;
using Ncw.Core.Domain;

namespace Ncw.Agent.Llm.Wire;

/// <summary>
/// 编码器侧的 <c>data:</c> 图片地址校验与规范化。
/// <para>
/// <c>ncw://</c> 附件引用由路由在发出去的副本上解析(<see cref="Routing.LlmRouter"/>),编码器只接受有效的光栅图片
/// <c>data:image/(png|jpeg|gif|webp);base64,…</c>。历史里残留的 2.x 本地路径、未解析的 <c>ncw://</c>、空或畸形的
/// <c>data:</c> 绝不原样放进 <c>image_url</c>(OpenAI Responses / Chat 会直接 400:<c>Expected a valid URL</c>,并卡死整段会话)。
/// </para>
/// </summary>
internal static class DataUrls
{
    /// <summary>图片引用在编码期无法作为有效 data URL 上行时的占位(给模型看,不是界面文字)。</summary>
    public const string UnavailableImagePlaceholder = "[image omitted: image data is unavailable]";

    private const string Base64Marker = ";base64,";

    /// <summary>
    /// 校验并解析光栅图片 <c>data:</c> URL。MIME 归一化到白名单(<c>image/png</c> / <c>image/jpeg</c> / <c>image/gif</c> / <c>image/webp</c>),
    /// 剥掉 base64 里的空白字符并校验其合法且非空。
    /// </summary>
    public static bool TryParseImage(string? dataRef, out string mime, out string base64)
    {
        mime = "";
        base64 = "";
        if (string.IsNullOrWhiteSpace(dataRef))
        {
            return false;
        }

        var span = dataRef.AsSpan().Trim();
        if (!span.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var markerIndex = span.IndexOf(Base64Marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 5)
        {
            return false;
        }

        var normalizedMime = Attachments.NormalizeImageMime(span[5..markerIndex].ToString());
        if (normalizedMime is null)
        {
            return false;
        }

        var payload = span[(markerIndex + Base64Marker.Length)..];
        if (payload.IsEmpty)
        {
            return false;
        }

        string clean;
        if (ContainsAsciiWhitespace(payload))
        {
            clean = StripAsciiWhitespace(payload);
            if (clean.Length == 0)
            {
                return false;
            }
        }
        else
        {
            clean = payload.ToString();
        }

        if (!Base64.IsValid(clean.AsSpan(), out var decodedLength) || decodedLength <= 0)
        {
            return false;
        }

        mime = normalizedMime;
        base64 = clean;
        return true;
    }

    /// <summary>规范化为标准 <c>data:{mime};base64,{base64}</c>(无空白、MIME 规范),非法返回 false。</summary>
    public static bool TryNormalizeImageUrl(string? dataRef, out string url)
    {
        if (TryParseImage(dataRef, out var mime, out var base64))
        {
            url = $"data:{mime};base64,{base64}";
            return true;
        }

        url = "";
        return false;
    }

    private static bool ContainsAsciiWhitespace(ReadOnlySpan<char> value)
    {
        foreach (var ch in value)
        {
            if (ch is ' ' or '\t' or '\r' or '\n')
            {
                return true;
            }
        }

        return false;
    }

    private static string StripAsciiWhitespace(ReadOnlySpan<char> value)
    {
        var count = 0;
        foreach (var ch in value)
        {
            if (ch is not (' ' or '\t' or '\r' or '\n'))
            {
                count++;
            }
        }

        if (count == 0)
        {
            return "";
        }

        return string.Create(count, value, static (dst, src) =>
        {
            var i = 0;
            foreach (var ch in src)
            {
                if (ch is not (' ' or '\t' or '\r' or '\n'))
                {
                    dst[i++] = ch;
                }
            }
        });
    }
}
