using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Protocol.Orchestration;
using Ncw.Protocol.Transport;

namespace Ncw.Agent.Cloud;

/// <summary>退出码(进程管理方 = 编排服务据此判断要不要重启 / 报警)。0 = 正常结束(含休眠、升级请求);2 = 命令行参数错误(沿用 local 模式)。</summary>
public static class CloudExitCodes
{
    public const int Ok = 0;

    /// <summary>同一数据根已有另一个 Agent(和 local 模式同一个值,EX_TEMPFAIL)。</summary>
    public const int AlreadyRunning = 75;

    /// <summary>启动配置缺失或不合法(EX_CONFIG)。重启也不会好,编排服务应当报警而不是重试。</summary>
    public const int Config = 78;

    /// <summary>依赖不可用(EX_UNAVAILABLE):KMS 连不上 / 拒绝解开密钥。可以退避后重启。</summary>
    public const int Dependency = 69;

    /// <summary>
    /// 身份不符(挂错了卷):数据根里的设备身份和编排服务期望的不一致(EX_DATAERR)。重启也不会好。
    /// 本机模式也用它表示「数据根里有数据却没有可用的主密钥」(<see cref="LocalStartupCodes"/>):同样是「这份数据和手上的密钥对不上」,重启也不会好。
    /// </summary>
    public const int Identity = 65;
}

/// <summary>启动失败。<see cref="Code"/> 是稳定的结构化 key(给编排服务和日志用),<see cref="Field"/> 只是字段名——<b>不含任何值</b>(值可能是密钥)。</summary>
public sealed class CloudStartupException(string code, string? field, int exitCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;

    public string? Field { get; } = field;

    public int ExitCode { get; } = exitCode;
}

/// <summary>启动失败的稳定 key。</summary>
public static class CloudStartupCodes
{
    public const string ConfigMissing = "cloud.config.missing";
    public const string ConfigInvalid = "cloud.config.invalid";
    public const string ConfigUnreadable = "cloud.config.unreadable";
    public const string ConfigInsecure = "cloud.config.insecure";
    public const string SecretMissing = "cloud.secret.missing";
    public const string KmsUnavailable = "cloud.kms.unavailable";
    public const string KmsRejected = "cloud.kms.rejected";
    public const string KeyMissing = "cloud.kms.keyMissing";
    public const string IdentityMismatch = "cloud.identity.mismatch";
    public const string BindingMismatch = "cloud.identity.bindingMismatch";
}

/// <summary>
/// 本机模式(<c>--mode local</c>)启动失败的稳定 key,和云端同一个形态:<c>ncw-agent startup-failed &lt;key&gt; &lt;字段名&gt;</c>(只有 key 和字段名,绝无值),退出码 <see cref="CloudExitCodes.Identity"/>。
/// 字段名是主密钥的文件名(<c>credential.key</c>),不是它的内容。桌面端的守护按这个 key 映射成给用户的原因(<c>master-key-missing</c> / <c>master-key-unreadable</c>)。
/// </summary>
public static class LocalStartupCodes
{
    /// <summary>数据根里已经有数据 / 加密凭据(<c>device.json</c> 里的设备私钥、<c>credentials</c> 表里的 NCK1 密文),但 <c>credential.key</c> 不存在。绝不生成新密钥。</summary>
    public const string MasterKeyMissing = "agent.masterKey.missing";

    /// <summary><c>credential.key</c> 存在,但读不了(权限 / 不是普通文件)、已损坏(长度不对),或者解不开这份数据里的密文(不是这份数据的密钥)。</summary>
    public const string MasterKeyUnreadable = "agent.masterKey.unreadable";
}

/// <summary>出网策略(启动配置里的一段,见 <see cref="HostAllowlistEgress"/>)。</summary>
public sealed record CloudEgressFile(string[]? AllowedHosts = null, string[]? EnforceFor = null);

public sealed record CloudKmsFile(string Provider, string Context, string? KeyId = null, string? Endpoint = null);

public sealed record CloudIdleFile(int? IdleMinutes = null, int? CheckSeconds = null, int? WakeLeadSeconds = null, int? MinSleepMinutes = null);

public sealed record CloudHealthFile(int Port, string? Bind = null);

public sealed record OrchestratorKeyFile(string Kid, string Key);

/// <summary>
/// 启动配置文件(JSON,编排服务写进沙箱、Agent 启动时读;<b>不含任何密钥</b>——账户身份令牌走环境变量 / 密钥文件)。
/// 不认识的字段直接报错(拼写错了就该启动失败,而不是悄悄用默认值)。
/// </summary>
public sealed record CloudConfigFile(
    string? DataRoot = null,
    string? WorkspacesRoot = null,
    string? DeviceName = null,
    string? OrchestrationUrl = null,
    string? RelayUrl = null,
    OrchestratorKeyFile[]? OrchestratorKeys = null,
    string? ExpectedDeviceId = null,
    string? AccountId = null,
    long? AccountTokenExpiresAt = null,
    CloudKmsFile? Kms = null,
    CloudIdleFile? Idle = null,
    int? UsageReportSeconds = null,
    CloudHealthFile? Health = null,
    CloudEgressFile? Egress = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(CloudConfigFile))]
internal sealed partial class CloudConfigJson : JsonSerializerContext;

/// <summary>校验后的启动配置(不可变;<see cref="ToString"/> 不含任何值,日志里随便打)。</summary>
public sealed class CloudConfig
{
    public required string DataRoot { get; init; }

    public required string WorkspacesRoot { get; init; }

    public string? DeviceName { get; init; }

    public required Uri OrchestrationUrl { get; init; }

    public required Uri RelayUrl { get; init; }

    public required OrchestratorKeys Keys { get; init; }

    public string? ExpectedDeviceId { get; init; }

    public string? AccountId { get; init; }

    public required CloudKmsFile Kms { get; init; }

    public TimeSpan IdleAfter { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan IdleCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>唤醒提前量:Agent 请求编排服务在「下一个定时任务触发时间 − 提前量」唤醒它(冷启动目标 10 秒,留足余量)。</summary>
    public TimeSpan WakeLead { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>下一个定时任务离现在不到这么久就不休眠(休眠再唤醒不划算)。</summary>
    public TimeSpan MinSleep { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan UsageReportInterval { get; init; } = TimeSpan.FromSeconds(60);

    public CloudHealthFile? Health { get; init; }

    public CloudEgressFile? Egress { get; init; }

    /// <summary>账户身份令牌(秘密)。只经 Host 的委托账户(<c>DelegatedAccount</c>)和编排通道的 Authorization 头使用,永远不进日志 / 诊断。</summary>
    public required string AccountToken { get; init; }

    /// <summary>令牌到期时间(Unix 毫秒);没配置 = 启动后 1 小时(编排服务会经 <c>account.token</c> 指令轮换)。</summary>
    public long? AccountTokenExpiresAt { get; init; }

    public override string ToString() => $"CloudConfig(dataRoot={DataRoot}, orchestration={OrchestrationUrl.Host}, relay={RelayUrl.Host}, keys={Keys.Count})";
}

/// <summary>
/// 启动配置的装载与校验。来源(都不是命令行参数,所以不会出现在进程列表里):
/// <list type="bullet">
/// <item><c>NCW_CLOUD_CONFIG</c>:配置文件路径(必需)。编排服务把它挂进沙箱;文件不能被「组 / 其它用户」写。</item>
/// <item><c>NCW_ACCOUNT_TOKEN</c> 或 <c>NCW_ACCOUNT_TOKEN_FILE</c>:账户身份令牌(必需;二选一,文件形式给密钥挂载用)。</item>
/// <item><c>NCW_ORCH_KEYS</c>:编排验证公钥 <c>kid:base64,kid:base64</c>(可选;给了就覆盖配置文件里的)。</item>
/// </list>
/// 失败一律是 <see cref="CloudStartupException"/>:稳定 key + 字段名,<b>不含值</b>。
/// </summary>
public static class CloudConfigLoader
{
    public const string ConfigEnv = "NCW_CLOUD_CONFIG";
    public const string TokenEnv = "NCW_ACCOUNT_TOKEN";
    public const string TokenFileEnv = "NCW_ACCOUNT_TOKEN_FILE";
    public const string KeysEnv = "NCW_ORCH_KEYS";

    public static CloudConfig Load(Func<string, string?> env, Func<string, string> readFile, Func<string, bool>? isGroupOrOtherWritable = null)
    {
        var path = env(ConfigEnv);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Fail(CloudStartupCodes.ConfigMissing, ConfigEnv);
        }

        isGroupOrOtherWritable ??= IsLooselyWritable;
        string text;
        try
        {
            if (isGroupOrOtherWritable(path))
            {
                throw Fail(CloudStartupCodes.ConfigInsecure, ConfigEnv);
            }

            text = readFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Fail(CloudStartupCodes.ConfigUnreadable, ConfigEnv);
        }

        CloudConfigFile file;
        try
        {
            file = JsonSerializer.Deserialize(text, CloudConfigJson.Default.CloudConfigFile) ?? throw Fail(CloudStartupCodes.ConfigInvalid, "config");
        }
        catch (JsonException e)
        {
            // JsonException.Path 只是字段路径(不含值);Message 可能含值的片段,所以只取 Path
            throw Fail(CloudStartupCodes.ConfigInvalid, e.Path ?? "config");
        }

        var token = ReadToken(env, readFile);
        return Build(file, token, env(KeysEnv));
    }

    /// <summary>从进程环境和真实文件系统装载。</summary>
    public static CloudConfig LoadFromProcess() =>
        Load(Environment.GetEnvironmentVariable, static p => File.ReadAllText(p));

    private static string ReadToken(Func<string, string?> env, Func<string, string> readFile)
    {
        var inline = env(TokenEnv);
        var file = env(TokenFileEnv);
        string? token = inline;
        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(file))
        {
            try
            {
                token = readFile(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw Fail(CloudStartupCodes.SecretMissing, TokenFileEnv);
            }
        }

        token = token?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            throw Fail(CloudStartupCodes.SecretMissing, TokenEnv);
        }

        // 令牌进 HTTP 头:不能含空白 / 控制字符,也别大得离谱
        if (token.Length > 8192 || token.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            throw Fail(CloudStartupCodes.ConfigInvalid, TokenEnv);
        }

        return token;
    }

    public static CloudConfig Build(CloudConfigFile file, string accountToken, string? keysOverride)
    {
        var dataRoot = RequireAbsolutePath(file.DataRoot, "dataRoot");
        var workspaces = RequireAbsolutePath(file.WorkspacesRoot, "workspacesRoot");

        var orchestration = RequireUrl(file.OrchestrationUrl, "orchestrationUrl");
        if (orchestration.Scheme != Uri.UriSchemeHttps && !(orchestration.Scheme == Uri.UriSchemeHttp && IsLoopback(orchestration.Host)))
        {
            // 账户令牌在这条连接上(Authorization 头):必须 TLS;http 只允许回环(开发 / 测试)
            throw Fail(CloudStartupCodes.ConfigInvalid, "orchestrationUrl");
        }

        if (string.IsNullOrWhiteSpace(file.RelayUrl) || !RelayAddress.TryNormalize(file.RelayUrl, out var relay))
        {
            throw Fail(CloudStartupCodes.ConfigInvalid, "relayUrl");
        }

        OrchestratorKeys keys;
        if (!string.IsNullOrWhiteSpace(keysOverride))
        {
            if (!OrchestratorKeys.TryParse(keysOverride, out keys))
            {
                throw Fail(CloudStartupCodes.ConfigInvalid, KeysEnv);
            }
        }
        else
        {
            var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var item in file.OrchestratorKeys ?? [])
            {
                var bytes = new byte[32];
                if (string.IsNullOrWhiteSpace(item.Kid) || !Convert.TryFromBase64String(item.Key ?? "", bytes, out var written) || written != 32)
                {
                    throw Fail(CloudStartupCodes.ConfigInvalid, "orchestratorKeys");
                }

                map[item.Kid] = bytes;
            }

            if (map.Count == 0)
            {
                throw Fail(CloudStartupCodes.ConfigMissing, "orchestratorKeys");
            }

            keys = new OrchestratorKeys(map);
        }

        if (file.ExpectedDeviceId is { } expected && !RelayProtocol.IsValidDeviceId(expected))
        {
            throw Fail(CloudStartupCodes.ConfigInvalid, "expectedDeviceId");
        }

        if (file.Kms is not { Provider.Length: > 0, Context.Length: > 0 } kms)
        {
            throw Fail(CloudStartupCodes.ConfigMissing, "kms");
        }

        var idle = file.Idle ?? new CloudIdleFile();
        var config = new CloudConfig
        {
            DataRoot = dataRoot,
            WorkspacesRoot = workspaces,
            DeviceName = file.DeviceName is { Length: > 0 } name ? name[..Math.Min(name.Length, 64)] : null,
            OrchestrationUrl = orchestration,
            RelayUrl = relay,
            Keys = keys,
            ExpectedDeviceId = file.ExpectedDeviceId,
            AccountId = file.AccountId,
            Kms = kms,
            IdleAfter = Minutes(idle.IdleMinutes, 15, 1, 24 * 60, "idle.idleMinutes"),
            IdleCheckInterval = Seconds(idle.CheckSeconds, 30, 1, 3600, "idle.checkSeconds"),
            WakeLead = Seconds(idle.WakeLeadSeconds, 120, 0, 3600, "idle.wakeLeadSeconds"),
            MinSleep = Minutes(idle.MinSleepMinutes, 10, 0, 24 * 60, "idle.minSleepMinutes"),
            UsageReportInterval = Seconds(file.UsageReportSeconds, 60, 5, 3600, "usageReportSeconds"),
            Health = file.Health is { } health ? ValidateHealth(health) : null,
            Egress = file.Egress,
            AccountToken = accountToken,
            AccountTokenExpiresAt = file.AccountTokenExpiresAt,
        };
        return config;
    }

    private static CloudHealthFile ValidateHealth(CloudHealthFile health)
    {
        if (health.Port is < 1 or > 65535 || (health.Bind is { } bind && !IPAddress.TryParse(bind, out _)))
        {
            throw Fail(CloudStartupCodes.ConfigInvalid, "health");
        }

        return health;
    }

    private static string RequireAbsolutePath(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Fail(CloudStartupCodes.ConfigMissing, field);
        }

        if (!Path.IsPathRooted(value) || value.Contains('\0'))
        {
            throw Fail(CloudStartupCodes.ConfigInvalid, field);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private static Uri RequireUrl(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Fail(CloudStartupCodes.ConfigMissing, field);
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || string.IsNullOrEmpty(uri.Host))
        {
            throw Fail(CloudStartupCodes.ConfigInvalid, field);
        }

        return new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/" }.Uri; // 结尾带 /,相对路径才能正确拼接
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static TimeSpan Minutes(int? value, int fallback, int min, int max, string field) => TimeSpan.FromMinutes(Ranged(value, fallback, min, max, field));

    private static TimeSpan Seconds(int? value, int fallback, int min, int max, string field) => TimeSpan.FromSeconds(Ranged(value, fallback, min, max, field));

    private static int Ranged(int? value, int fallback, int min, int max, string field)
    {
        var v = value ?? fallback;
        return v < min || v > max ? throw Fail(CloudStartupCodes.ConfigInvalid, field) : v;
    }

    private static CloudStartupException Fail(string code, string field) =>
        new(code, field, CloudExitCodes.Config, $"{code}: {field}");

    private static bool IsLooselyWritable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return false;
        }

        var mode = File.GetUnixFileMode(path);
        return (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0;
    }
}
