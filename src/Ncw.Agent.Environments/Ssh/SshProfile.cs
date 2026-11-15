namespace Ncw.Agent.Environments.Ssh;

public enum SshAuthMethod
{
    /// <summary>按系统配置:私钥(显式的或默认的)、键盘交互式、密码——前面的方法被拒绝后才会问密码。</summary>
    Auto,
    Password,
    Key,
    Interactive,
    /// <summary>总是先问密码(和 <see cref="Password"/> 相同,只是名字对齐 2.x 的 <c>ask</c>)。</summary>
    Ask,
}

public enum RemotePlatform
{
    Auto,
    Linux,
    MacOS,
    Windows,
}

public enum SshProxyKind
{
    Http,
    Socks4,
    Socks5,
}

/// <summary>连到第一跳之前先经过的 HTTP / SOCKS 代理(对应 2.x 的 <c>ssh/proxy.ts</c>;SSH.NET 自带握手)。</summary>
public sealed record SshProxy(SshProxyKind Kind, string Host, int Port, string? Username = null, string? Password = null);

/// <summary>连接目标。两种写法对应 2.x 的 <c>SshTarget</c>:引用 ssh_config 里的别名,或手填。</summary>
public abstract record SshTarget
{
    /// <summary>引用 ssh_config 里的 <c>Host</c> 别名。</summary>
    public sealed record ConfigAlias(string Host, string? ConfigFile = null, string? IdentityFile = null) : SshTarget;

    /// <summary>手填的主机、端口、用户名;可选私钥和 <c>ProxyJump</c>(<c>user@host:port,...</c>)。</summary>
    public sealed record Manual(string Host, int Port, string Username, string? IdentityFile = null, string? ProxyJump = null) : SshTarget;
}

/// <summary>
/// SSH 环境的配置(移植自 2.x 的 <c>SshConnectionProfile</c>,去掉了与系统 OpenSSH / askpass 有关的部分)。
/// 凭据(密码、口令)不在这里,由 <see cref="ISshCredentialProvider"/> 提供;持久化由 Agent 的存储层负责。
/// </summary>
public sealed record SshEnvironmentProfile
{
    public required string Name { get; init; }

    public required SshTarget Target { get; init; }

    public SshAuthMethod AuthMethod { get; init; } = SshAuthMethod.Auto;

    public RemotePlatform Platform { get; init; } = RemotePlatform.Auto;

    public SshProxy? Proxy { get; init; }

    /// <summary>建立一条连接(TCP + 密钥交换 + 认证)的超时。不含等待用户回答确认的时间。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>路线上的一跳(最后一跳是目标主机)。所有字段都是最终值,不含占位符。</summary>
/// <param name="HostKeyName">主机指纹钉扎用的名字:默认端口用裸主机名,否则 <c>[host]:port</c>(与 OpenSSH known_hosts 同形);ssh_config 的 <c>HostKeyAlias</c> 优先。</param>
public sealed record SshHop(string HostName, int Port, string User, IReadOnlyList<string> IdentityFiles, bool IdentitiesOnly, string HostKeyName)
{
    public static string KnownHostsName(string host, int port) => port == 22 ? host : $"[{host}]:{port}";
}

/// <summary><c>ProxyJump</c> 里的一项:<c>[user@]host[:port]</c> 或 <c>ssh://[user@]host[:port]</c>。</summary>
public sealed record SshJumpSpec(string? User, string Host, int? Port);

/// <summary>把配置目标解析成「经过哪些跳、每跳怎么连」的纯函数集合。</summary>
public static class SshRouteResolver
{
    private const int MaxChainDepth = 8;

    /// <summary>
    /// 解析 <c>ProxyJump</c> 文本。<c>none</c> 或空返回空列表。格式不合法抛 <see cref="EnvironmentErrorCode.InvalidProfile"/>。
    /// 支持 IPv6 方括号(<c>[::1]:2222</c>)和 <c>ssh://</c> URI 写法。
    /// </summary>
    public static IReadOnlyList<SshJumpSpec> ParseProxyJump(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var result = new List<SshJumpSpec>();
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var item = raw;
            if (item.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                item = item["ssh://".Length..].TrimEnd('/');
            }

            string? user = null;
            var at = item.LastIndexOf('@');
            if (at >= 0)
            {
                user = item[..at];
                item = item[(at + 1)..];
                if (user.Length == 0 || user.Any(char.IsWhiteSpace))
                {
                    throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "ProxyJump user");
                }
            }

            string host;
            int? port = null;
            if (item.StartsWith('['))
            {
                var close = item.IndexOf(']');
                if (close < 0)
                {
                    throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "ProxyJump host");
                }

                host = item[1..close];
                var tail = item[(close + 1)..];
                if (tail.Length > 0)
                {
                    port = ParsePort(tail.StartsWith(':') ? tail[1..] : tail);
                }
            }
            else
            {
                var colon = item.LastIndexOf(':');
                if (colon >= 0 && item.IndexOf(':') == colon)
                {
                    host = item[..colon];
                    port = ParsePort(item[(colon + 1)..]);
                }
                else
                {
                    host = item;
                }
            }

            if (host.Length == 0 || host.StartsWith('-') || host.Any(c => char.IsWhiteSpace(c) || c == '\0'))
            {
                throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "ProxyJump host");
            }

            result.Add(new SshJumpSpec(user, host, port));
        }

        return result;
    }

    /// <summary>
    /// 解析整条路线(跳板机在前,目标在最后)。跳板机的名字也会按 ssh_config 解析(别名、它自己的 ProxyJump 链),
    /// 链上出现环或过深抛 <see cref="EnvironmentErrorCode.InvalidProfile"/>。目标配置了 ProxyCommand 抛 <see cref="EnvironmentErrorCode.Unsupported"/>。
    /// </summary>
    public static IReadOnlyList<SshHop> Resolve(SshTarget target, SshConfig config, string localUser)
    {
        switch (target)
        {
            case SshTarget.ConfigAlias alias:
                ValidateHostText(alias.Host);
                if (alias.IdentityFile?.IndexOfAny(['\0', '\r', '\n']) >= 0)
                {
                    throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "identity file");
                }

                var viaConfig = config.Resolve(alias.Host, null, localUser);
                var hops = new List<SshHop>();
                AppendChain(hops, viaConfig, config, localUser, 0, [alias.Host]);
                var identity = alias.IdentityFile is { Length: > 0 } file ? [file] : viaConfig.IdentityFiles;
                hops.Add(ToHop(viaConfig, viaConfig.User ?? localUser, identity));
                return hops;

            case SshTarget.Manual manual:
                ValidateHostText(manual.Host);
                if (manual.Port is < 1 or > 65535 || string.IsNullOrEmpty(manual.Username) || manual.Username.IndexOfAny(['\0', '\r', '\n']) >= 0)
                {
                    throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "target");
                }

                var chain = new List<SshHop>();
                foreach (var jump in ParseProxyJump(manual.ProxyJump))
                {
                    AppendJump(chain, jump, config, localUser, 0, [manual.Host]);
                }

                var manualIdentity = manual.IdentityFile is { Length: > 0 } f ? (IReadOnlyList<string>)[f] : [];
                chain.Add(new SshHop(manual.Host, manual.Port, manual.Username, manualIdentity, false, SshHop.KnownHostsName(manual.Host, manual.Port)));
                // 手填目标只有一把私钥:没有自己配置的跳板机沿用它(ssh -J 不会这样做,但手填表单里没有地方给每一跳单独指定私钥)
                return [.. chain.Select(h => h.IdentityFiles.Count == 0 ? h with { IdentityFiles = manualIdentity } : h)];

            default:
                throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "target");
        }
    }

    private static void AppendChain(List<SshHop> hops, SshResolvedHost resolved, SshConfig config, string localUser, int depth, HashSet<string> visiting)
    {
        if (resolved.ProxyCommand is not null && resolved.ProxyJump is null)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "ProxyCommand");
        }

        foreach (var jump in ParseProxyJump(resolved.ProxyJump))
        {
            AppendJump(hops, jump, config, localUser, depth, visiting);
        }
    }

    private static void AppendJump(List<SshHop> hops, SshJumpSpec jump, SshConfig config, string localUser, int depth, HashSet<string> visiting)
    {
        if (depth >= MaxChainDepth || !visiting.Add(jump.Host + "\0" + jump.Port))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "ProxyJump chain is too deep or loops");
        }

        // 跳板机名字也走配置:别名、它自己的 ProxyJump 都要尊重;显式写在 ProxyJump 里的 user/port 优先
        var resolved = config.Resolve(jump.Host, jump.User, localUser);
        AppendChain(hops, resolved, config, localUser, depth + 1, visiting);
        var hop = ToHop(resolved with { Port = jump.Port ?? resolved.Port }, jump.User ?? resolved.User ?? localUser, resolved.IdentityFiles);
        hops.Add(hop);
        visiting.Remove(jump.Host + "\0" + jump.Port);
    }

    private static SshHop ToHop(SshResolvedHost resolved, string user, IReadOnlyList<string> identityFiles) =>
        new(resolved.HostName, resolved.Port, user, identityFiles, resolved.IdentitiesOnly,
            resolved.HostKeyAlias ?? SshHop.KnownHostsName(resolved.HostName, resolved.Port));

    private static int ParsePort(string text) =>
        int.TryParse(text, out var port) && port is >= 1 and <= 65535
            ? port
            : throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "ProxyJump port");

    private static void ValidateHostText(string host)
    {
        // 与 2.x 的 sshTargetArgs 同一组规则:不能以 - 开头(选项注入),不含空白和 NUL
        if (string.IsNullOrEmpty(host) || host.StartsWith('-') || host.Any(c => char.IsWhiteSpace(c) || c == '\0'))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "host");
        }
    }
}
