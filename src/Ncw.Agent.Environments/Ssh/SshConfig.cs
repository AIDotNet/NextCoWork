using System.Text;

namespace Ncw.Agent.Environments.Ssh;

/// <summary>读 ssh_config 及其 <c>Include</c> 文件的文件系统抽象(设备上的 <c>~/.ssh/config</c>;单测里是内存实现)。</summary>
public interface ISshConfigFiles
{
    /// <summary>用户 home,用来展开 <c>~</c> 和相对的 <c>Include</c>(相对 <c>~/.ssh</c>)。</summary>
    string Home { get; }

    string? ReadAllText(string path);

    /// <summary>展开 <c>Include</c> 里的通配符(只支持文件名部分的 <c>*</c> / <c>?</c>),按名字排序。</summary>
    IEnumerable<string> Glob(string pattern);
}

/// <summary>设备本地磁盘上的实现(SSH 环境读自己的配置,属于执行环境实现自身,允许直接用 System.IO)。</summary>
public sealed class PhysicalSshConfigFiles(string? home = null) : ISshConfigFiles
{
    public string Home { get; } = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string? ReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IEnumerable<string> Glob(string pattern)
    {
        var directory = Path.GetDirectoryName(pattern);
        var filePattern = Path.GetFileName(pattern);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory, filePattern).Order(StringComparer.Ordinal).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>ssh_config 里解析出来的一个条目:关键字(小写)和值(已按引号拆成词)。</summary>
internal sealed record SshConfigEntry(string Keyword, IReadOnlyList<string> Values);

/// <summary>一个条件块:<c>Host</c> 或 <c>Match</c> 之后的所有条目(第一个块是全局的,没有条件)。</summary>
internal sealed record SshConfigBlock(SshConfigCondition Condition, List<SshConfigEntry> Entries);

internal abstract record SshConfigCondition
{
    public sealed record Always : SshConfigCondition;

    public sealed record HostPatterns(IReadOnlyList<string> Patterns) : SshConfigCondition;

    /// <summary>
    /// <c>Match</c> 的条件。<paramref name="Supported"/> 为假表示用了我们不支持的条件(<c>exec</c>、<c>canonical</c>、<c>localnetwork</c> 等),
    /// 此时整块忽略并给出警告,而不是猜一个结果。
    /// </summary>
    public sealed record MatchCriteria(IReadOnlyList<(string Key, IReadOnlyList<string> Patterns)> Criteria, bool Supported) : SshConfigCondition;
}

/// <summary>解析出的最终主机配置(已按「第一个值生效」合并)。纯数据,不含任何界面文字。</summary>
public sealed record SshResolvedHost
{
    public required string Alias { get; init; }

    public required string HostName { get; init; }

    public string? User { get; init; }

    public int Port { get; init; } = 22;

    public IReadOnlyList<string> IdentityFiles { get; init; } = [];

    public bool IdentitiesOnly { get; init; }

    /// <summary>原始 ProxyJump 文本(<c>none</c> 已折算成 <c>null</c>)。</summary>
    public string? ProxyJump { get; init; }

    /// <summary>配置了 ProxyCommand。本实现不执行任意命令作为传输,遇到时由调用方报「不支持」。</summary>
    public string? ProxyCommand { get; init; }

    public string? HostKeyAlias { get; init; }

    public bool IdentityAgentDisabled { get; init; }

    public int? ConnectTimeoutSeconds { get; init; }

    public int? ServerAliveIntervalSeconds { get; init; }

    public int? ServerAliveCountMax { get; init; }

    public bool? PasswordAuthentication { get; init; }

    public bool? PubkeyAuthentication { get; init; }

    public bool? KbdInteractiveAuthentication { get; init; }

    /// <summary>解析时遇到的不支持项(例如 <c>Match exec</c>),给导入界面提示用。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// <c>~/.ssh/config</c> 解析(06 §3):Host、HostName、User、Port、IdentityFile、ProxyJump、IdentitiesOnly,
/// 以及常用的 HostKeyAlias、IdentityAgent、ConnectTimeout、ServerAliveInterval/CountMax、各认证方式开关。
/// 语义对齐 OpenSSH:关键字大小写不敏感,<c>Keyword value</c> 与 <c>Keyword=value</c> 都认,第一个取得的值生效,
/// <c>IdentityFile</c> 累加,<c>Host</c> 支持 <c>*</c> / <c>?</c> / <c>!</c> 取反,<c>Include</c> 相对 <c>~/.ssh</c>,
/// IdentityFile 里的 <c>~</c>、<c>%d %h %n %p %r %u %%</c> 会展开。<c>Match</c> 支持 <c>all</c> / <c>host</c> / <c>originalhost</c> / <c>user</c>。
/// </summary>
public sealed class SshConfig
{
    private const int MaxIncludeDepth = 16;
    private readonly List<SshConfigBlock> _blocks = [new(new SshConfigCondition.Always(), [])];
    private readonly string _home;

    private SshConfig(string home) => _home = home;

    /// <summary>空配置(没有 <c>~/.ssh/config</c> 时用)。</summary>
    public static SshConfig Empty(string home = "") => new(home);

    /// <summary>从设备上的配置文件读取并解析(含 Include)。文件不存在返回空配置。</summary>
    public static SshConfig Load(ISshConfigFiles files, string? path = null)
    {
        var config = new SshConfig(files.Home);
        var configPath = path ?? Path.Combine(files.Home, ".ssh", "config");
        if (files.ReadAllText(configPath) is { } text)
        {
            config.Add(text, files, 0, Path.Combine(files.Home, ".ssh"));
        }

        return config;
    }

    /// <summary>解析一段配置文本(单测用)。<paramref name="files"/> 为 <c>null</c> 时 <c>Include</c> 被忽略。</summary>
    public static SshConfig Parse(string text, ISshConfigFiles? files = null, string home = "/home/user")
    {
        var config = new SshConfig(files?.Home ?? home);
        config.Add(text, files, 0, Path.Combine(config._home, ".ssh"));
        return config;
    }

    /// <summary>
    /// 列出可导入的具体主机别名(<c>Host</c> 行里不含通配符、也不是取反的那些),按出现顺序、去重。
    /// </summary>
    public IReadOnlyList<string> ListHosts()
    {
        var result = new List<string>();
        foreach (var block in _blocks)
        {
            if (block.Condition is SshConfigCondition.HostPatterns hosts)
            {
                foreach (var pattern in hosts.Patterns)
                {
                    if (!pattern.StartsWith('!') && pattern.IndexOfAny(['*', '?']) < 0 && !result.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(pattern);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 解析 <paramref name="host"/>(用户在命令行/界面里写的那个名字)的最终配置。
    /// <paramref name="requestedUser"/> 是显式指定的用户名(优先于配置里的 <c>User</c>),<paramref name="localUser"/> 用来展开 <c>%u</c>。
    /// </summary>
    public SshResolvedHost Resolve(string host, string? requestedUser = null, string localUser = "")
    {
        var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var identityFiles = new List<string>();
        var warnings = new List<string>();
        var currentUser = requestedUser;
        string? hostName = null;

        foreach (var block in _blocks)
        {
            if (!Matches(block.Condition, host, currentUser ?? "", warnings))
            {
                continue;
            }

            foreach (var entry in block.Entries)
            {
                if (entry.Keyword == "identityfile")
                {
                    identityFiles.AddRange(entry.Values);
                    continue;
                }

                if (values.TryAdd(entry.Keyword, entry.Values))
                {
                    if (entry.Keyword == "user" && currentUser is null && entry.Values.Count > 0)
                    {
                        currentUser = entry.Values[0];
                    }

                    if (entry.Keyword == "hostname" && entry.Values.Count > 0)
                    {
                        hostName = entry.Values[0];
                    }
                }
            }
        }

        string? Single(string key) => values.TryGetValue(key, out var v) && v.Count > 0 ? v[0] : null;
        bool? Flag(string key) => Single(key)?.ToLowerInvariant() switch { "yes" or "true" => true, "no" or "false" => false, _ => null };
        int? Number(string key) => int.TryParse(Single(key), out var n) && n > 0 ? n : null;

        var resolvedHost = ExpandHostName(hostName ?? host, host);
        var port = Number("port") is { } p && p <= 65535 ? p : 22;
        var user = requestedUser ?? Single("user");
        var proxyJump = Single("proxyjump") is { } jump && !jump.Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Join(',', values["proxyjump"]) : null;
        var expanded = identityFiles.Select(f => ExpandIdentity(f, resolvedHost, host, port, user ?? "", localUser)).ToList();

        return new SshResolvedHost
        {
            Alias = host,
            HostName = resolvedHost,
            User = user,
            Port = port,
            IdentityFiles = expanded,
            IdentitiesOnly = Flag("identitiesonly") ?? false,
            ProxyJump = proxyJump,
            ProxyCommand = Single("proxycommand") is { } cmd && !cmd.Equals("none", StringComparison.OrdinalIgnoreCase) ? cmd : null,
            HostKeyAlias = Single("hostkeyalias"),
            IdentityAgentDisabled = Single("identityagent")?.Equals("none", StringComparison.OrdinalIgnoreCase) == true,
            ConnectTimeoutSeconds = Number("connecttimeout"),
            ServerAliveIntervalSeconds = Number("serveraliveinterval"),
            ServerAliveCountMax = Number("serveralivecountmax"),
            PasswordAuthentication = Flag("passwordauthentication"),
            PubkeyAuthentication = Flag("pubkeyauthentication"),
            KbdInteractiveAuthentication = Flag("kbdinteractiveauthentication"),
            Warnings = warnings.Distinct().ToList(),
        };
    }

    // ---- 解析 ----

    private void Add(string text, ISshConfigFiles? files, int depth, string includeBase)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var split = SplitKeyword(line);
            if (split is null)
            {
                continue;
            }

            var (keyword, rest) = split.Value;
            var values = Tokenize(rest);
            switch (keyword)
            {
                case "host":
                    _blocks.Add(new SshConfigBlock(new SshConfigCondition.HostPatterns(values), []));
                    break;
                case "match":
                    _blocks.Add(new SshConfigBlock(ParseMatch(values), []));
                    break;
                case "include":
                    if (files is not null && depth < MaxIncludeDepth)
                    {
                        foreach (var pattern in values)
                        {
                            foreach (var path in files.Glob(ResolveIncludePath(pattern, includeBase)))
                            {
                                if (files.ReadAllText(path) is { } included)
                                {
                                    Add(included, files, depth + 1, includeBase);
                                }
                            }
                        }
                    }

                    break;
                default:
                    if (values.Count > 0)
                    {
                        _blocks[^1].Entries.Add(new SshConfigEntry(keyword, values));
                    }

                    break;
            }
        }
    }

    private string ResolveIncludePath(string pattern, string includeBase)
    {
        if (pattern.StartsWith("~/", StringComparison.Ordinal))
        {
            return Path.Combine(_home, pattern[2..]);
        }

        return Path.IsPathRooted(pattern) ? pattern : Path.Combine(includeBase, pattern);
    }

    private static (string Keyword, string Remainder)? SplitKeyword(string line)
    {
        var i = 0;
        while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '=')
        {
            i++;
        }

        if (i == 0)
        {
            return null;
        }

        var keyword = line[..i].ToLowerInvariant();
        while (i < line.Length && (char.IsWhiteSpace(line[i]) || line[i] == '='))
        {
            // 只吞掉关键字后面的一个 '=':值里的 '=' 要保留
            if (line[i] == '=')
            {
                i++;
                break;
            }

            i++;
        }

        while (i < line.Length && char.IsWhiteSpace(line[i]))
        {
            i++;
        }

        return (keyword, line[i..]);
    }

    /// <summary>按空白拆词,支持双引号;空白后的 <c>#</c> 开始注释。</summary>
    internal static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var hasToken = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuote)
            {
                if (c == '"')
                {
                    inQuote = false;
                }
                else if (c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
                {
                    current.Append(text[++i]);
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                inQuote = true;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else if (c == '#' && !hasToken)
            {
                break;
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static SshConfigCondition ParseMatch(List<string> tokens)
    {
        if (tokens.Count == 1 && tokens[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return new SshConfigCondition.Always();
        }

        var criteria = new List<(string, IReadOnlyList<string>)>();
        var supported = true;
        for (var i = 0; i < tokens.Count; i++)
        {
            var key = tokens[i].ToLowerInvariant();
            if (key == "all")
            {
                continue;
            }

            if (key is "host" or "originalhost" or "user" or "localuser")
            {
                if (i + 1 >= tokens.Count)
                {
                    supported = false;
                    break;
                }

                criteria.Add((key, tokens[++i].Split(',')));
            }
            else
            {
                // exec、canonical、final、localnetwork、tagged……:不执行任意命令,也不猜
                supported = false;
                if (key is "exec" or "tagged" or "localnetwork" or "sessiontype" && i + 1 < tokens.Count)
                {
                    i++;
                }
            }
        }

        return new SshConfigCondition.MatchCriteria(criteria, supported);
    }

    // ---- 匹配 ----

    private static bool Matches(SshConfigCondition condition, string host, string user, List<string> warnings)
    {
        switch (condition)
        {
            case SshConfigCondition.Always:
                return true;
            case SshConfigCondition.HostPatterns hosts:
                return MatchPatternList(hosts.Patterns, host);
            case SshConfigCondition.MatchCriteria match:
                if (!match.Supported)
                {
                    warnings.Add("unsupported-match");
                    return false;
                }

                foreach (var (key, patterns) in match.Criteria)
                {
                    var subject = key switch
                    {
                        "host" or "originalhost" => host,
                        "user" => user,
                        _ => Environment.UserName,
                    };
                    if (!MatchPatternList(patterns, subject))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>OpenSSH 的模式列表语义:至少一个正向模式匹配,且没有任何取反模式匹配。</summary>
    internal static bool MatchPatternList(IReadOnlyList<string> patterns, string subject)
    {
        var positive = false;
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                if (Glob(pattern[1..], subject))
                {
                    return false;
                }
            }
            else if (Glob(pattern, subject))
            {
                positive = true;
            }
        }

        return positive;
    }

    /// <summary>只支持 <c>*</c> 和 <c>?</c> 的通配,不区分大小写,线性双指针(不用正则)。</summary>
    internal static bool Glob(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])) && pattern[p] != '*')
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    // ---- 展开 ----

    private static string ExpandHostName(string value, string original)
    {
        // HostName 里的 %h 指它自己(OpenSSH:HostName 里 %h 展开为命令行上的名字)
        return value.Replace("%h", original, StringComparison.Ordinal).Replace("%%", "%", StringComparison.Ordinal);
    }

    private string ExpandIdentity(string value, string hostName, string original, int port, string user, string localUser)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }

            var c = value[++i];
            sb.Append(c switch
            {
                '%' => "%",
                'd' => _home,
                'h' => hostName,
                'n' => original,
                'p' => port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                'r' => user,
                'u' => localUser,
                _ => "%" + c,
            });
        }

        var expanded = sb.ToString();
        if (expanded == "~")
        {
            return _home;
        }

        return expanded.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(_home, expanded[2..]) : expanded;
    }
}
