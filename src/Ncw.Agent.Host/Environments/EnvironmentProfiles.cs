using System.Text.Json.Nodes;
using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;
using Ncw.Protocol.Methods;

namespace Ncw.Agent.Host.Environments;

/// <summary>经过校验的环境配置输入(<see cref="EnvironmentInput"/> 的内部形式)。路径字段:null = 保持已存的,空串 = 清除。</summary>
internal sealed record ValidatedProfile(
    string Name,
    bool Enabled,
    string TargetKind,
    string Host,
    int? Port,
    string? Username,
    string? IdentityFile,
    string? ConfigFile,
    string? ProxyJump,
    SshAuthMethod AuthMethod,
    RemotePlatform Platform,
    SshProxyInput? Proxy);

/// <summary>
/// 环境配置的校验、持久化形状(2.x 的 <c>connection_profiles</c> JSON)与运行时 <see cref="SshEnvironmentProfile"/> 之间的转换。
/// <b>逐字段构造</b>(不展开输入):凭据天然进不了这一行(同 2.x 的 <c>upsertConnection</c>)。3.0 不认识的键(2.x 以后加的)在更新时原样保留。
/// </summary>
internal static class EnvironmentProfiles
{
    public const int MaxNameLength = 120;

    public const int MaxHostLength = 255;

    public const int MaxPathLength = 4096;

    public const int MaxSecretLength = 8192;

    /// <summary>校验输入;不合法抛 <see cref="EnvironmentException"/>(<see cref="EnvironmentErrorCode.InvalidProfile"/>)。</summary>
    public static ValidatedProfile Validate(EnvironmentInput input)
    {
        if (input?.Target is not { } target)
        {
            throw Invalid();
        }

        var name = input.Name?.Trim() ?? "";
        Require(name.Length is > 0 and <= MaxNameLength && !HasControl(name));
        Require(target.Kind is "config" or "manual");
        var host = target.Host?.Trim() ?? "";
        Require(host.Length is > 0 and <= MaxHostLength && !host.StartsWith('-') && !host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)));
        string? username = null;
        int? port = null;
        if (target.Kind == "manual")
        {
            port = target.Port ?? 22;
            Require(port is >= 1 and <= 65535);
            username = target.Username?.Trim() ?? "";
            Require(username.Length is > 0 and <= 128 && !username.Any(char.IsControl) && !username.Any(char.IsWhiteSpace));
        }
        else
        {
            // 别名的端口 / 用户名来自 ssh_config,表单里给了也不采信(避免界面显示的和实际连的不一致)
            Require(target.Port is null && string.IsNullOrEmpty(target.Username));
        }

        ValidatePath(target.IdentityFile);
        ValidatePath(target.ConfigFile);
        Require(target.Kind == "manual" || string.IsNullOrEmpty(target.ProxyJump));
        if (!string.IsNullOrWhiteSpace(target.ProxyJump))
        {
            Require(target.ProxyJump.Length <= 1024 && !target.ProxyJump.Any(char.IsControl));
            _ = SshRouteResolver.ParseProxyJump(target.ProxyJump); // 格式不合法抛 InvalidProfile
        }

        var auth = ParseAuth(input.AuthMethod ?? "auto") ?? throw Invalid();
        var platform = ParsePlatform(input.Platform ?? "auto") ?? throw Invalid();
        if (input.Proxy is { } proxy)
        {
            Require(proxy.Kind is "http" or "socks4" or "socks5");
            Require(proxy.Host is { Length: > 0 and <= MaxHostLength } && !proxy.Host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) && !proxy.Host.StartsWith('-'));
            Require(proxy.Port is >= 1 and <= 65535);
            Require(proxy.Username is null || (proxy.Username.Length <= 128 && !proxy.Username.Any(char.IsControl)));
        }

        return new ValidatedProfile(name, input.Enabled, target.Kind, host, port, username, target.IdentityFile, target.ConfigFile,
            string.IsNullOrWhiteSpace(target.ProxyJump) ? null : target.ProxyJump.Trim(), auth, platform, input.Proxy);
    }

    public static void ValidateSecrets(EnvironmentSecretsInput? secrets)
    {
        if (secrets is null)
        {
            return;
        }

        foreach (var value in new[] { secrets.Password, secrets.KeyPassphrase, secrets.ProxyPassword })
        {
            Require(value is null || (value.Length <= MaxSecretLength && !value.Contains('\0')));
        }
    }

    /// <summary>生成要写进 <c>connection_profiles</c> 的 JSON。<paramref name="previous"/> 非空 = 更新:未知键保留,路径的 null 沿用已存的。</summary>
    public static JsonObject ToJson(string id, ValidatedProfile v, JsonObject? previous, long now, long revision)
    {
        var previousTarget = previous?["target"] as JsonObject;
        string? Path(string? input, string key) => input is null ? previousTarget?[key]?.GetValue<string>() : input.Length == 0 ? null : input;
        var target = new JsonObject { ["kind"] = v.TargetKind, ["host"] = v.Host };
        if (v.TargetKind == "manual")
        {
            target["port"] = v.Port;
            target["username"] = v.Username;
        }
        else
        {
            if (Path(v.ConfigFile, "configFile") is { } configFile)
            {
                target["configFile"] = configFile;
            }
        }

        if (Path(v.IdentityFile, "identityFile") is { } identity)
        {
            target["identityFile"] = identity;
        }

        if (v.TargetKind == "manual" && v.ProxyJump is { } jump)
        {
            target["proxyJump"] = jump;
        }

        var json = previous is null ? new JsonObject() : (JsonObject)previous.DeepClone();
        json["id"] = id;
        json["kind"] = "ssh";
        json["name"] = v.Name;
        json["target"] = target;
        json["authMethod"] = AuthName(v.AuthMethod);
        json["platform"] = PlatformName(v.Platform);
        json["enabled"] = v.Enabled;
        json["revision"] = revision;
        json["createdAt"] = previous?["createdAt"]?.GetValue<long>() ?? now;
        json["updatedAt"] = now;
        if (v.Proxy is { } proxy)
        {
            var node = new JsonObject { ["kind"] = proxy.Kind, ["host"] = proxy.Host, ["port"] = proxy.Port };
            if (!string.IsNullOrEmpty(proxy.Username))
            {
                node["username"] = proxy.Username;
            }

            json["proxy"] = node;
        }
        else
        {
            json.Remove("proxy");
        }

        return json;
    }

    /// <summary>持久化的 JSON → 运行时配置。<paramref name="proxyPassword"/> 来自凭据库(不在 JSON 里)。</summary>
    public static SshEnvironmentProfile ToProfile(ConnectionView view, string? proxyPassword)
    {
        SshTarget target = view.TargetKind == "manual"
            ? new SshTarget.Manual(view.Host, view.Port ?? 22, view.Username ?? "", view.IdentityFile, view.ProxyJump)
            : new SshTarget.ConfigAlias(view.Host, view.ConfigFile, view.IdentityFile);
        return new SshEnvironmentProfile
        {
            Name = view.Name,
            Target = target,
            AuthMethod = view.AuthMethod,
            Platform = view.Platform,
            Proxy = view.Proxy is { } p
                ? new SshProxy(p.Kind switch { "http" => SshProxyKind.Http, "socks4" => SshProxyKind.Socks4, _ => SshProxyKind.Socks5 }, p.Host, p.Port, p.Username, proxyPassword)
                : null,
        };
    }

    public static string AuthName(SshAuthMethod method) => method switch
    {
        SshAuthMethod.Password => "password",
        SshAuthMethod.Key => "key",
        SshAuthMethod.Interactive => "interactive",
        SshAuthMethod.Ask => "ask",
        _ => "auto",
    };

    public static SshAuthMethod? ParseAuth(string value) => value switch
    {
        "auto" => SshAuthMethod.Auto,
        "password" => SshAuthMethod.Password,
        "key" => SshAuthMethod.Key,
        "interactive" => SshAuthMethod.Interactive,
        "ask" => SshAuthMethod.Ask,
        _ => null,
    };

    /// <summary>线上写 <c>macos</c>,持久化沿用 2.x 的 <c>darwin</c> / <c>win32</c>。</summary>
    public static string PlatformName(RemotePlatform platform) => platform switch
    {
        RemotePlatform.Linux => "linux",
        RemotePlatform.MacOS => "darwin",
        RemotePlatform.Windows => "win32",
        _ => "auto",
    };

    public static string WirePlatformName(RemotePlatform platform) => platform switch
    {
        RemotePlatform.Linux => "linux",
        RemotePlatform.MacOS => "macos",
        RemotePlatform.Windows => "windows",
        _ => "auto",
    };

    public static RemotePlatform? ParsePlatform(string value) => value switch
    {
        "auto" => RemotePlatform.Auto,
        "linux" => RemotePlatform.Linux,
        "macos" or "darwin" => RemotePlatform.MacOS,
        "windows" or "win32" => RemotePlatform.Windows,
        _ => null,
    };

    private static void ValidatePath(string? path) =>
        Require(path is null || (path.Length <= MaxPathLength && !path.Contains('\0') && !path.Contains('\r') && !path.Contains('\n')));

    private static bool HasControl(string text) => text.Any(char.IsControl);

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw Invalid();
        }
    }

    private static EnvironmentException Invalid() => new(EnvironmentErrorCode.InvalidProfile);
}

/// <summary>一条已存连接配置的结构化视图(从 JSON 读出;读不懂的字段按 2.x 的缺省值)。</summary>
internal sealed record ConnectionView(
    string Id,
    string Name,
    bool Enabled,
    long Revision,
    string TargetKind,
    string Host,
    int? Port,
    string? Username,
    string? IdentityFile,
    string? ConfigFile,
    string? ProxyJump,
    SshAuthMethod AuthMethod,
    RemotePlatform Platform,
    SshProxyInput? Proxy)
{
    public static ConnectionView? From(Ncw.Agent.Storage.ConnectionRecord record)
    {
        try
        {
            var target = record.Json["target"] as JsonObject;
            var kind = target?["kind"]?.GetValue<string>() ?? "manual";
            var host = target?["host"]?.GetValue<string>();
            if (kind is not ("config" or "manual") || string.IsNullOrEmpty(host))
            {
                return null;
            }

            var proxyNode = record.Json["proxy"] as JsonObject;
            var proxy = proxyNode is not null && proxyNode["kind"]?.GetValue<string>() is { } proxyKind && proxyNode["host"]?.GetValue<string>() is { } proxyHost
                ? new SshProxyInput(proxyKind, proxyHost, proxyNode["port"]?.GetValue<int>() ?? 0, proxyNode["username"]?.GetValue<string>())
                : null;
            return new ConnectionView(
                record.Id,
                record.Name,
                record.Enabled,
                record.Revision,
                kind,
                host,
                kind == "manual" ? target?["port"]?.GetValue<int>() ?? 22 : null,
                kind == "manual" ? target?["username"]?.GetValue<string>() : null,
                target?["identityFile"]?.GetValue<string>(),
                target?["configFile"]?.GetValue<string>(),
                target?["proxyJump"]?.GetValue<string>(),
                // 2.x 的老配置没有 authMethod:「保持系统 OpenSSH 的认证配置」= 自动
                EnvironmentProfiles.ParseAuth(record.Json["authMethod"]?.GetValue<string>() ?? "auto") ?? SshAuthMethod.Auto,
                EnvironmentProfiles.ParsePlatform(record.Json["platform"]?.GetValue<string>() ?? "auto") ?? RemotePlatform.Auto,
                proxy);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
