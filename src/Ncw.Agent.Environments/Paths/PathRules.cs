namespace Ncw.Agent.Environments;

public enum PathStyle
{
    Posix,
    Windows,
}

/// <summary>
/// 纯字符串的路径规则(移植自 2.x <c>environment/paths.ts</c> 里的 node:path posix/win32 用法)。
/// 不碰文件系统,所以在 macOS 上也能按 Windows 规则处理远程 Windows 主机的路径,反之亦然。
/// <list type="bullet">
/// <item>Windows 的输入里,盘符相对路径(<c>D:</c>、<c>D:foo</c>)、单斜杠根(<c>\foo</c>)、设备路径(<c>\\?\</c>、<c>\\.\</c>)一律无效;</item>
/// <item>规范化后的 Windows 路径用 <c>\</c>,<see cref="Relative"/> 的结果一律用 <c>/</c>(给界面显示用);</item>
/// <item>Windows 的比较忽略大小写,POSIX 区分大小写(macOS 的大小写敏感卷上两个目录不能被折叠成一个)。</item>
/// </list>
/// </summary>
public static class PathRules
{
    public static char Separator(PathStyle style) => style == PathStyle.Windows ? '\\' : '/';

    public static StringComparison Comparison(PathStyle style) =>
        style == PathStyle.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>输入是否是合法的路径文本(不含 NUL;Windows 上还要排除几种有歧义的写法)。</summary>
    public static bool IsValidInput(PathStyle style, string? input)
    {
        if (input is null || input.Contains('\0'))
        {
            return false;
        }

        if (style != PathStyle.Windows)
        {
            return true;
        }

        // 盘符相对:D: / D:foo
        if (input.Length >= 2 && IsAsciiLetter(input[0]) && input[1] == ':' && (input.Length == 2 || !IsSeparator(input[2])))
        {
            return false;
        }

        // 单斜杠根:\foo、/foo(UNC 的双斜杠另算)
        if (input.Length >= 1 && IsSeparator(input[0]) && (input.Length == 1 || !IsSeparator(input[1])))
        {
            return false;
        }

        // 设备路径:\\?\、\\.\
        return !(input.Length >= 4 && IsSeparator(input[0]) && IsSeparator(input[1]) && (input[2] is '?' or '.') && IsSeparator(input[3]));
    }

    /// <summary>合法且是绝对路径(Windows:<c>C:\…</c> 或 UNC <c>\\server\share\…</c>)。</summary>
    public static bool IsAbsolute(PathStyle style, string? input)
    {
        if (!IsValidInput(style, input) || input!.Length == 0)
        {
            return false;
        }

        if (style == PathStyle.Posix)
        {
            return input[0] == '/';
        }

        return TryParseWindowsRoot(input, out _);
    }

    /// <summary>规范化:折叠重复分隔符、去掉 <c>.</c>、解析 <c>..</c>(不会越过根)、去掉结尾分隔符。输入无效时抛 <see cref="EnvironmentErrorCode.InvalidPath"/>。</summary>
    public static string Normalize(PathStyle style, string path)
    {
        if (!IsValidInput(style, path))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidPath);
        }

        var (root, rest) = SplitRoot(style, path);
        var sep = Separator(style);
        var stack = new List<string>();
        // POSIX 里反斜杠是普通字符,只有 Windows 把它当分隔符
        char[] separators = style == PathStyle.Windows ? ['/', '\\'] : ['/'];
        foreach (var part in rest.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            Push(stack, part, root.Length > 0);
        }

        var body = string.Join(sep, stack);
        if (root.Length > 0)
        {
            return root + body;
        }

        return body.Length == 0 ? "." : body;
    }

    /// <summary>
    /// 拼接(node 的 <c>path.join</c> 语义):后面的绝对路径不会重置前面的,只是接在后面。要「绝对输入优先」用 <see cref="Resolve"/>。
    /// </summary>
    public static string Join(PathStyle style, params string[] parts)
    {
        var joined = string.Join(Separator(style), parts.Where(static p => p.Length > 0));
        return joined.Length == 0 ? "." : Normalize(style, joined);
    }

    /// <summary>绝对输入直接规范化,相对输入接在 <paramref name="root"/> 后面再规范化。</summary>
    public static string Resolve(PathStyle style, string root, string input) =>
        IsAbsolute(style, input) ? Normalize(style, input) : Normalize(style, root.Length == 0 ? input : root + Separator(style) + input);

    public static string Dirname(PathStyle style, string path)
    {
        var normalized = Normalize(style, path);
        var (root, rest) = SplitRoot(style, normalized);
        var index = rest.LastIndexOf(Separator(style));
        if (index < 0)
        {
            return root.Length > 0 ? root : ".";
        }

        return root + rest[..index];
    }

    public static string Basename(PathStyle style, string path)
    {
        var normalized = Normalize(style, path);
        var (_, rest) = SplitRoot(style, normalized);
        if (rest.Length == 0 || normalized == ".")
        {
            return normalized == "." ? "." : "";
        }

        var index = rest.LastIndexOf(Separator(style));
        return index < 0 ? rest : rest[(index + 1)..];
    }

    public static string Extname(PathStyle style, string path)
    {
        var name = Basename(style, path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[dot..];
    }

    /// <summary>
    /// 从 <paramref name="from"/> 到 <paramref name="to"/> 的相对路径,分隔符统一用 <c>/</c>。两者必须都是绝对路径;
    /// Windows 上盘符(或 UNC 根)不同时没有相对写法,返回规范化后的 <paramref name="to"/>。
    /// </summary>
    public static string Relative(PathStyle style, string from, string to)
    {
        var (fromRoot, fromSegments) = Segments(style, Normalize(style, from));
        var (toRoot, toSegments) = Segments(style, Normalize(style, to));
        if (!string.Equals(fromRoot, toRoot, Comparison(style)))
        {
            return Normalize(style, to);
        }

        var common = 0;
        while (common < fromSegments.Length && common < toSegments.Length
            && string.Equals(fromSegments[common], toSegments[common], Comparison(style)))
        {
            common++;
        }

        var parts = new List<string>();
        for (var i = common; i < fromSegments.Length; i++)
        {
            parts.Add("..");
        }

        parts.AddRange(toSegments.Skip(common));
        return string.Join('/', parts);
    }

    /// <summary><paramref name="target"/> 是否等于或位于 <paramref name="root"/> 之内(纯文本比较,不解析符号链接)。</summary>
    public static bool IsInside(PathStyle style, string root, string target)
    {
        var (rootRoot, rootSegments) = Segments(style, Normalize(style, root));
        var (targetRoot, targetSegments) = Segments(style, Normalize(style, target));
        if (!string.Equals(rootRoot, targetRoot, Comparison(style)) || targetSegments.Length < rootSegments.Length)
        {
            return false;
        }

        for (var i = 0; i < rootSegments.Length; i++)
        {
            if (!string.Equals(rootSegments[i], targetSegments[i], Comparison(style)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>SFTP 协议里的路径写法:Windows 主机上 <c>C:\a\b</c> 写成 <c>/C:/a/b</c>(OpenSSH for Windows 的约定)。POSIX 原样。</summary>
    public static string ToSftpPath(PathStyle style, string path)
    {
        if (style != PathStyle.Windows)
        {
            return path;
        }

        var slashes = path.Replace('\\', '/');
        return path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]) ? "/" + slashes : slashes;
    }

    public static string FromSftpPath(PathStyle style, string path)
    {
        if (style != PathStyle.Windows)
        {
            return path;
        }

        var trimmed = path.Length >= 3 && path[0] == '/' && IsAsciiLetter(path[1]) && path[2] == ':' ? path[1..] : path;
        return Normalize(style, trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed);
    }

    private static void Push(List<string> stack, string part, bool rooted)
    {
        if (part == ".")
        {
            return;
        }

        if (part != "..")
        {
            stack.Add(part);
            return;
        }

        if (stack.Count > 0 && stack[^1] != "..")
        {
            stack.RemoveAt(stack.Count - 1);
        }
        else if (!rooted)
        {
            stack.Add("..");
        }
    }

    private static (string Root, string[] Segments) Segments(PathStyle style, string normalized)
    {
        var (root, rest) = SplitRoot(style, normalized);
        return (root, rest.Length == 0 ? [] : rest.Split(Separator(style)));
    }

    /// <summary>拆出根(<c>/</c>、<c>C:\</c>、<c>\\server\share\</c>)和其余部分。相对路径的根是空串。</summary>
    private static (string Root, string Remainder) SplitRoot(PathStyle style, string path)
    {
        if (style == PathStyle.Posix)
        {
            return path.StartsWith('/') ? ("/", path.TrimStart('/')) : ("", path);
        }

        if (TryParseWindowsRoot(path, out var length))
        {
            var root = path[..length].Replace('/', '\\');
            if (!root.EndsWith('\\'))
            {
                root += "\\";
            }

            return (root, path[length..]);
        }

        return ("", path);
    }

    /// <summary>识别 <c>C:\</c> 或 <c>\\server\share</c> 根,返回根占用的字符数(含其后的一个分隔符)。</summary>
    private static bool TryParseWindowsRoot(string path, out int length)
    {
        length = 0;
        if (path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]))
        {
            length = 3;
            return true;
        }

        if (path.Length >= 5 && IsSeparator(path[0]) && IsSeparator(path[1]) && !IsSeparator(path[2]) && path[2] is not ('?' or '.'))
        {
            var serverEnd = IndexOfSeparator(path, 2);
            if (serverEnd < 0)
            {
                return false;
            }

            var shareStart = serverEnd + 1;
            if (shareStart >= path.Length || IsSeparator(path[shareStart]))
            {
                return false;
            }

            var shareEnd = IndexOfSeparator(path, shareStart);
            length = shareEnd < 0 ? path.Length : shareEnd + 1;
            return true;
        }

        return false;
    }

    private static int IndexOfSeparator(string path, int start)
    {
        for (var i = start; i < path.Length; i++)
        {
            if (IsSeparator(path[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsSeparator(char c) => c is '/' or '\\';

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
