using System.Text.RegularExpressions;

namespace Ncw.Agent.Plugins;

/// <summary>参数门(2.x <c>main/plugin/capabilities.ts</c>):<b>纯函数</b>,不碰文件系统、不碰网络。路径的词法边界在这里判,realpath 归一(挡软链)在调用点经 <c>PathGuard</c> 做。</summary>
public static partial class HostGuards
{
    public const int MaxNetRedirects = 5;
    public const int MaxNetHeaderCount = 64;
    public const int MaxNetHeaderValueBytes = 4096;

    [GeneratedRegex(@"^[A-Za-z0-9_.:@/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StorageKeyPattern();

    [GeneratedRegex(@"[;&|`$><\n]", RegexOptions.CultureInvariant)]
    private static partial Regex ShellMeta();

    [GeneratedRegex(@"^[a-zA-Z]:", RegexOptions.CultureInvariant)]
    private static partial Regex Drive();

    [GeneratedRegex(@"\.(exe|cmd|bat|ps1)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WinExe();

    /// <summary>
    /// 工作区内的相对路径的词法检查。入参<b>只接受相对路径</b>:接受绝对路径意味着每次调用都要判断「这个绝对路径是不是恰好在工作区里」,
    /// 而那个判断在软链、大小写不敏感文件系统、UNC 路径上各有各的坑。落点是否跑出根外由调用方经 realpath 守卫判。
    /// </summary>
    public static GuardResult<string> NarrowRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return GuardResult<string>.Deny("path is required");
        }

        if (path.Length > 1024)
        {
            return GuardResult<string>.Deny("path is too long");
        }

        if (path.Contains('\0'))
        {
            return GuardResult<string>.Deny("path contains a NUL byte");
        }

        if (path.StartsWith('/') || path.StartsWith('\\') || Drive().IsMatch(path))
        {
            return GuardResult<string>.Deny("path must be relative to the workspace root");
        }

        return GuardResult<string>.Ok(path);
    }

    /// <summary>
    /// 命令白名单:只匹配 <c>argv[0]</c>,而且只按<b>基名</b>(<c>/usr/bin/cargo</c> 与 <c>cargo</c> 是同一个意图)。不拼 shell 串,也不接受 shell 元字符:
    /// 让「这条命令到底会跑什么」在审批里可读。
    /// </summary>
    public static GuardResult<(string Command, string[] Args)> NarrowCommand(IReadOnlyList<string> allowed, string? command, IReadOnlyList<string>? args)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return GuardResult<(string, string[])>.Deny("command is required");
        }

        if (command.Contains('\0'))
        {
            return GuardResult<(string, string[])>.Deny("command contains a NUL byte");
        }

        // ★ shell 元字符先于白名单判:否则 "echo; rm -rf /" 会以一句「不在白名单」的名义被拒,而真正的原因被藏起来
        if (ShellMeta().IsMatch(command))
        {
            return GuardResult<(string, string[])>.Deny("command must not contain shell metacharacters");
        }

        args ??= [];
        if (args.Any(a => a is null || a.Contains('\0')))
        {
            return GuardResult<(string, string[])>.Deny("args must be an array of strings");
        }

        if (args.Count > 64)
        {
            return GuardResult<(string, string[])>.Deny("too many arguments");
        }

        var normalized = command.Replace('\\', '/');
        var basename = normalized[(normalized.LastIndexOf('/') + 1)..];
        var stem = WinExe().Replace(basename, "");
        if (allowed.Count == 0)
        {
            return GuardResult<(string, string[])>.Deny("the manifest declares no command allow-list");
        }

        if (!allowed.Contains(stem, StringComparer.Ordinal))
        {
            return GuardResult<(string, string[])>.Deny($"\"{stem}\" is not in the manifest command allow-list");
        }

        return GuardResult<(string, string[])>.Ok((stem, [.. args]));
    }

    /// <summary><c>net.fetch</c> 的 URL 门:只允许 https、不带凭据、不是内网 / 环回字面量、命中 <c>hostPermissions</c>。</summary>
    public static GuardResult<Uri> NarrowFetchUrl(IReadOnlyList<string> hostPermissions, string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return GuardResult<Uri>.Deny("url is required");
        }

        if (url.Length > 2048)
        {
            return GuardResult<Uri>.Deny("url is too long");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return GuardResult<Uri>.Deny("url is not a valid URL");
        }

        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            return GuardResult<Uri>.Deny("only https:// is allowed");
        }

        if (parsed.UserInfo.Length > 0)
        {
            return GuardResult<Uri>.Deny("credentials in the URL are not allowed");
        }

        if (IsPrivateHost(parsed.IdnHost))
        {
            return GuardResult<Uri>.Deny("private and loopback addresses are blocked");
        }

        return MatchesHostPermission(hostPermissions, parsed)
            ? GuardResult<Uri>.Ok(parsed)
            : GuardResult<Uri>.Deny("url does not match any entry in hostPermissions");
    }

    /// <summary>一个具体 URL 命中 <c>hostPermissions</c> 里的某一条吗(主机精确匹配、路径前缀 / 精确)。</summary>
    public static bool MatchesHostPermission(IReadOnlyList<string> patterns, Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (!PluginManifestParser.IsHostPattern(pattern))
            {
                continue;
            }

            var rest = pattern["https://".Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            var host = slash < 0 ? rest : rest[..slash];
            var path = slash < 0 ? "/*" : rest[slash..];
            if (!string.Equals(url.Authority, host, StringComparison.OrdinalIgnoreCase) && !(url.IsDefaultPort && string.Equals(url.Host, host, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (path is "/*" or "*")
            {
                return true;
            }

            if (path.EndsWith('*') ? url.AbsolutePath.StartsWith(path[..^1], StringComparison.Ordinal) : url.AbsolutePath == path)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 内网 / 环回字面量一律挡掉:<c>hostPermissions</c> 是作者写的,而作者可以写 <c>https://169.254.169.254/*</c>。
    /// ★ IPv6 的唯一本地判定必须带冒号(<c>fc2.com</c> 这类正常域名以 fc/fd 开头)。★ 只挡字面量:域名解析到内网(DNS 重绑定)这一层在这里判不了。
    /// </summary>
    public static bool IsPrivateHost(string hostname)
    {
        var host = hostname.ToLowerInvariant().Trim('[', ']');
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal))
        {
            return true;
        }

        if (host is "::1" or "::" or "0.0.0.0")
        {
            return true;
        }

        if (host.Contains(':'))
        {
            return IPv6Local().IsMatch(host);
        }

        var parts = host.Split('.');
        if (parts.Length != 4 || !parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit)))
        {
            return false;
        }

        var a = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var b = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        return a is 10 or 127 or 0 || (a == 169 && b == 254) || (a == 172 && b is >= 16 and <= 31) || (a == 192 && b == 168);
    }

    [GeneratedRegex(@"^(f[cd][0-9a-f]{2}:|fe[89ab])", RegexOptions.CultureInvariant)]
    private static partial Regex IPv6Local();

    /// <summary>kv 的 key:形状与长度(强制前缀在调用点加)。</summary>
    public static GuardResult<string> NarrowStorageKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return GuardResult<string>.Deny("key is required");
        }

        if (key.Length > 256)
        {
            return GuardResult<string>.Deny("key is too long");
        }

        return StorageKeyPattern().IsMatch(key) ? GuardResult<string>.Ok(key) : GuardResult<string>.Deny("key contains unsupported characters");
    }

    /// <summary>secrets 的引用。前缀由<b>宿主</b>拼:让插件自己拼前缀等于让它有机会写成 <c>plugin:other.plugin:token</c>,读走别人的密钥。</summary>
    public static string SecretReference(string pluginId, string key) => $"plugin:{pluginId}:{key}";

    /// <summary>注入上下文的强制包裹 + 截断:模型必须能看出这一段来自插件,而不是来自用户或系统提示词。</summary>
    public static string WrapContext(string pluginId, string text, int limit)
    {
        var clean = new string([.. text.Where(c => !(c <= '\u0008' || c is '\u000B' or '\u000C' || (c >= '\u000E' && c <= '\u001F')))]).Trim();
        if (clean.Length == 0)
        {
            return "";
        }

        var body = clean.Length > limit ? clean[..limit] + "…" : clean;
        return $"<plugin-context source=\"{pluginId}\">\n{body}\n</plugin-context>";
    }
}

public readonly record struct GuardResult<T>(bool Allowed, T? Value, string? Reason)
{
    public static GuardResult<T> Ok(T value) => new(true, value, null);

    public static GuardResult<T> Deny(string reason) => new(false, default, reason);
}
