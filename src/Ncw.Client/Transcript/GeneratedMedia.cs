using System.Text.Json;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.Transcript;

/// <summary>生成媒体工具的种类。</summary>
public enum GeneratedMediaKind
{
    Image,
    Video,
    Audio,
}

/// <summary>来自工具回执的附件引用,仅保留可由设备认证读取的会话附件。</summary>
public sealed record GeneratedMedia(string Url, string Mime)
{
    public static GeneratedMediaKind? KindOf(string toolName) => toolName switch
    {
        "generate_image" => GeneratedMediaKind.Image,
        "generate_video" => GeneratedMediaKind.Video,
        "generate_audio" => GeneratedMediaKind.Audio,
        _ => null,
    };

    internal static GeneratedMedia? Read(string? details, GeneratedMediaKind kind)
    {
        if (details is null) return null;

        var media = kind switch
        {
            GeneratedMediaKind.Image => "image",
            GeneratedMediaKind.Video => "video",
            GeneratedMediaKind.Audio => "audio",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        try
        {
            using var doc = JsonDocument.Parse(details);
            var root = doc.RootElement;
            if (SubagentReceipts.Str(root, "kind") != "media-generation" || SubagentReceipts.Str(root, "mediaKind") != media
                || SubagentReceipts.Str(root, "mime") is not { } mime || !mime.StartsWith(media + "/", StringComparison.OrdinalIgnoreCase)
                || SubagentReceipts.Str(root, "url") is not { } url || Attachments.ParseUrl(url) is not { Scope: Attachments.Session } locator
                || Attachments.BuildUrl(locator) != url)
            {
                return null;
            }

            return new GeneratedMedia(url, mime);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
