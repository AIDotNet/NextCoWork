using System.Globalization;

namespace Ncw.Relay;

/// <summary>
/// 从环境变量读部署配置(服务端部署物,没有命令行参数):
/// <c>NCW_RELAY_URLS</c>(分号分隔,默认 <c>http://0.0.0.0:8080</c>)、<c>NCW_RELAY_CERT</c> / <c>NCW_RELAY_CERT_KEY</c>(PEM,让 Kestrel 自己终止 TLS)、
/// <c>NCW_RELAY_IDLE_SECONDS</c>、<c>NCW_RELAY_MAX_SESSIONS_PER_DEVICE</c>、<c>NCW_RELAY_MAX_MB_PER_SEC</c>(每条会话)、
/// <c>NCW_RELAY_TRUST_FORWARDED_FOR</c>(<c>true</c> = 在可信反向代理后面)、<c>NCW_RELAY_METRICS_PUBLIC</c>、
/// <c>NCW_RELAY_SELF</c> + <c>NCW_RELAY_INSTANCES</c>(分号分隔的全部实例对外地址:多实例按 deviceId 粘性路由)。
/// 不认识的值直接报错,不静默回退(部署配置错了要立刻知道)。
/// </summary>
public static class RelayConfig
{
    public static RelayOptions FromEnvironment(Func<string, string?> get)
    {
        var defaults = new RelayOptions();
        var self = get("NCW_RELAY_SELF");
        var instances = get("NCW_RELAY_INSTANCES");
        IRelayPlacement placement = SingleInstancePlacement.Instance;
        if (!string.IsNullOrWhiteSpace(instances))
        {
            placement = new RendezvousPlacement(self ?? throw new FormatException("NCW_RELAY_INSTANCES needs NCW_RELAY_SELF"), instances.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var urls = get("NCW_RELAY_URLS");
        return new RelayOptions
        {
            Urls = string.IsNullOrWhiteSpace(urls) ? defaults.Urls : urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            CertificatePath = get("NCW_RELAY_CERT"),
            CertificateKeyPath = get("NCW_RELAY_CERT_KEY"),
            IdleTimeout = Seconds(get, "NCW_RELAY_IDLE_SECONDS", defaults.IdleTimeout),
            MaxSessionsPerDevice = Int(get, "NCW_RELAY_MAX_SESSIONS_PER_DEVICE", defaults.MaxSessionsPerDevice),
            MaxBytesPerSecondPerSession = Int(get, "NCW_RELAY_MAX_MB_PER_SEC", (int)(defaults.MaxBytesPerSecondPerSession / (1024 * 1024))) * 1024L * 1024,
            TrustForwardedFor = Bool(get, "NCW_RELAY_TRUST_FORWARDED_FOR"),
            MetricsPublic = Bool(get, "NCW_RELAY_METRICS_PUBLIC"),
            Placement = placement,
        };
    }

    private static int Int(Func<string, string?> get, string name, int fallback) =>
        get(name) is { Length: > 0 } text
            ? int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new FormatException($"{name} must be a non-negative integer")
            : fallback;

    private static TimeSpan Seconds(Func<string, string?> get, string name, TimeSpan fallback) =>
        get(name) is { Length: > 0 } ? TimeSpan.FromSeconds(Int(get, name, 0)) : fallback;

    private static bool Bool(Func<string, string?> get, string name) =>
        get(name) switch
        {
            null or "" or "false" or "0" => false,
            "true" or "1" => true,
            _ => throw new FormatException($"{name} must be true or false"),
        };
}
