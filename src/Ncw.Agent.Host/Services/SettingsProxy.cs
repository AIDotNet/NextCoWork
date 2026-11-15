using System.Net;
using System.Security.Cryptography;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;

namespace Ncw.Agent.Host.Services;

/// <summary>代理密码的存放(NCK1,凭据表里的 <c>proxy:password</c>,2.x 同名):不进 settings JSON、不进任何 DTO。</summary>
internal sealed class ProxyPasswordStore(CredentialStore credentials)
{
    public const string Reference = "proxy:password";

    public string? Get()
    {
        try
        {
            return credentials.Get(Reference) is { Length: > 0 } password ? password : null;
        }
        catch (Exception e) when (e is CryptographicException or Microsoft.Data.Sqlite.SqliteException or StorageException)
        {
            return null;
        }
    }

    public bool Has => Get() is not null;

    /// <summary>空串 = 清掉。</summary>
    public void Set(string password)
    {
        if (password.Length == 0)
        {
            credentials.Remove(Reference);
        }
        else
        {
            credentials.Set(Reference, password);
        }
    }
}

/// <summary>
/// 把设置页的代理设置(<c>settings.proxy</c> + NCK1 密码)落到全应用的出站请求上(2.x 的 <c>main/net/proxy.ts</c>):模型请求、目录同步、模型列表拉取、OAuth 刷新共用 Host 的那一个 <see cref="HttpClient"/>,
/// 所以它们一并跟着走。<b>每次请求现读</b>(改完设置立即对下一条请求生效;连接池按代理分池,旧连接自然淘汰)。
/// <list type="bullet">
/// <item>没启用:直连。<c>system</c> 模式:跟随系统代理(<see cref="HttpClient.DefaultProxy"/>)。<c>manual</c>:<c>scheme://host:port</c>(含 socks4 / socks5),带用户名 / 密码时用 NCK1 里的密码。</item>
/// <item><b>直连白名单</b>:内置的回环与私有网段(本机网关、局域网服务,走代理反而会坏)+ 用户填的(一行一条,支持 <c>host</c>、<c>*.domain</c>、<c>CIDR</c>)。内置那张表不存进设置:存进去的话用户删一条,下次启动又被塞回来。</item>
/// </list>
/// </summary>
internal sealed class SettingsProxy(SettingsService settings, ProxyPasswordStore password) : IWebProxy
{
    private readonly Lock _gate = new();
    private (string Key, ProxySettings Settings, string? Password)? _cache;

    /// <summary>内置直连:回环与本机、私有网段、链路本地(RFC 1918 等)。国内服务的域名白名单不内置:走不走代理由用户决定。</summary>
    public static bool IsBuiltinDirect(string host, IPAddress? address)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (address is null)
        {
            return !host.Contains('.'); // 没有点的主机名(局域网内的单标签名)
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.GetAddressBytes()[0] is 0xfc or 0xfd))
        {
            return true;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        return false;
    }

    /// <summary>用户白名单里有没有这个主机(<c>host</c> 精确、<c>*.domain</c> 后缀、<c>a.b.c.d/n</c> 网段)。</summary>
    public static bool MatchesBypass(string bypass, string host, IPAddress? address)
    {
        foreach (var raw in bypass.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if (raw.Contains('/') && address is not null && IPNetwork.TryParse(raw, out var network))
            {
                if (network.Contains(address))
                {
                    return true;
                }

                continue;
            }

            if (raw.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = raw[1..]; // ".domain"
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || host.Equals(raw[2..], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (raw.Equals(host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private (ProxySettings Settings, string? Password) Current()
    {
        var s = settings.Current().Proxy;
        // 密码只在开了认证时读(没认证的代理每个请求都去解一次 NCK1 没有意义);设置没变就复用。
        var key = $"{s.Enabled}|{s.Mode}|{s.Scheme}|{s.Host}|{s.Port}|{s.AuthEnabled}|{s.AuthUser}|{s.Bypass}";
        lock (_gate)
        {
            if (_cache is { } cached && cached.Key == key && DateTime.UtcNow.Ticks - _cacheAt < TimeSpan.TicksPerSecond * 5)
            {
                return (cached.Settings, cached.Password);
            }

            var pwd = s.Enabled && s.Mode == "manual" && s.AuthEnabled ? password.Get() : null;
            _cache = (key, s, pwd);
            _cacheAt = DateTime.UtcNow.Ticks;
            return (s, pwd);
        }
    }

    private long _cacheAt;

    /// <summary>密码或设置刚改过:丢掉缓存。</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    public ICredentials? Credentials
    {
        get => Current() is { Settings: { Enabled: true, Mode: "manual", AuthEnabled: true } s, Password: { } pwd } ? new NetworkCredential(s.AuthUser, pwd) : null;
        set { }
    }

    public Uri? GetProxy(Uri destination)
    {
        var (s, _) = Current();
        if (!s.Enabled)
        {
            return null;
        }

        if (s.Mode == "system")
        {
            return HttpClient.DefaultProxy is { } system && !system.IsBypassed(destination) ? system.GetProxy(destination) : null;
        }

        if (s.Host.Length == 0 || s.Port == 0 || IsBypassed(destination))
        {
            return null;
        }

        return new UriBuilder(s.Scheme, s.Host, s.Port).Uri;
    }

    public bool IsBypassed(Uri host)
    {
        var (s, _) = Current();
        if (!s.Enabled)
        {
            return true;
        }

        if (s.Mode == "system")
        {
            return HttpClient.DefaultProxy is not { } system || system.IsBypassed(host);
        }

        if (s.Host.Length == 0 || s.Port == 0)
        {
            return true;
        }

        var name = host.DnsSafeHost;
        _ = IPAddress.TryParse(name, out var address);
        return IsBuiltinDirect(name, address) || MatchesBypass(s.Bypass, name, address);
    }
}
