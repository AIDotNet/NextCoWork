using System.Security.Cryptography;
using System.Text;

namespace Ncw.Agent.Workspace.Files;

/// <summary>文件内容分类(移植自 2.x <c>workspace-file-content.ts</c>):文本 / 图片 / 二进制,以及修订号。纯函数,零 IO。</summary>
internal static class FileClassifier
{
    private static readonly Dictionary<string, string> ImageMime = new(StringComparer.Ordinal)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp",
        [".avif"] = "image/avif", [".bmp"] = "image/bmp", [".ico"] = "image/x-icon", [".svg"] = "image/svg+xml",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.Ordinal)
    {
        ".pdf", ".zip", ".gz", ".tgz", ".bz2", ".xz", ".7z", ".rar", ".tar", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods",
        ".exe", ".dll", ".so", ".dylib", ".bin", ".wasm", ".class", ".pyc", ".sqlite", ".sqlite3", ".db", ".mp3", ".wav", ".ogg", ".flac",
        ".mp4", ".mov", ".webm", ".avi", ".woff", ".woff2", ".ttf", ".otf", ".heic", ".tif", ".tiff", ".psd", ".dmg", ".iso",
    };

    /// <summary>严格 UTF-8:非法字节 / 孤立代理项抛异常(不静默换成替换字符)。不吞 BOM:<c>GetString</c> 把它保留为 U+FEFF。</summary>
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string? ImageMimeOf(string extension) => ImageMime.GetValueOrDefault(extension);

    /// <summary>这个扩展名的读取上限:图片 16 MiB,其它 2 MiB。</summary>
    public static int ReadLimit(string extension) => ImageMime.ContainsKey(extension) ? FileLimits.ImageMax : FileLimits.TextMax;

    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>没读内容的大文件的修订号。大小 + 修改时间(100ns 刻度):外部改写会让它变。</summary>
    public static string LargeRevision(long size, DateTimeOffset modified) => $"large:{size}:{modified.UtcTicks}";

    public static string? DecodeText(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>编码成 UTF-8;含孤立代理项这类编不回去的内容返回 null(保存时拒绝,不把 U+FFFD 写进用户文件)。</summary>
    public static byte[]? EncodeText(string text)
    {
        try
        {
            return Strict.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>控制字符(NUL 等)= 二进制文本。放行 TAB / LF / FF / CR。</summary>
    public static bool IsBinaryText(string content)
    {
        foreach (var c in content)
        {
            if (c <= 8 || c == 11 || (c >= 14 && c <= 31) || c == 127)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>已读到的字节 → 分类结果。<paramref name="bytes"/> 为 null = 超限没读(只有大小和修改时间)。</summary>
    public static FileContent Classify(string path, string extension, byte[]? bytes, long size, DateTimeOffset modified)
    {
        if (bytes is null)
        {
            return new FileContent(FileKind.Binary, path, size, LargeRevision(size, modified), Reason: BinaryReason.TooLarge);
        }

        var revision = Hash(bytes);
        if (ImageMime.TryGetValue(extension, out var mime))
        {
            return new FileContent(FileKind.Image, path, size, revision, Mime: mime, Data: bytes);
        }

        if (BinaryExtensions.Contains(extension))
        {
            return new FileContent(FileKind.Binary, path, size, revision, Reason: BinaryReason.Unsupported);
        }

        var utf16 = bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));
        var text = utf16 ? null : DecodeText(bytes);
        if (text is null)
        {
            return new FileContent(FileKind.Binary, path, size, revision, Reason: BinaryReason.Encoding);
        }

        return IsBinaryText(text)
            ? new FileContent(FileKind.Binary, path, size, revision, Reason: BinaryReason.Unsupported)
            : new FileContent(FileKind.Text, path, size, revision, Text: text);
    }
}
