using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog;

/// <summary>
/// 一个上游供应商(2.x <c>UpstreamProvider</c>)。<see cref="CredentialRef"/> 永远由 <see cref="Id"/> 派生(<c>provider:&lt;id&gt;</c>),
/// 不接受调用方给的值:它是安全相关的元数据,收下就等于允许「供应商 A 指向供应商 B 的密钥」。
/// <see cref="ProtocolOptions"/> / <see cref="VideoGeneration"/> 是 3.0 暂不解释、但必须原样保留的 2.x 字段(视频生成属于后续版本)。
/// </summary>
public sealed record ProviderRecord(
    string Id,
    string Name,
    WireProtocol Protocol,
    string BaseUrl,
    int Priority = ProviderRecord.DefaultPriority,
    bool Enabled = true)
{
    public const int DefaultPriority = 50;

    public JsonObject? ProtocolOptions { get; init; }

    public JsonObject? VideoGeneration { get; init; }

    public string CredentialRef => ProviderRefs.ForProvider(Id);

    /// <summary>Anthropic 提示词缓存的生存期:缺失、旧值 <c>off</c> 和未知值都落到强制的 5 分钟,显式的 <c>1h</c> 保留(2.x <c>normalizeAnthropicCacheTtl</c>)。</summary>
    public AnthropicCacheTtl CacheTtl =>
        ProtocolOptions?["anthropic"] is JsonObject anthropic && anthropic["cacheTtl"] is JsonValue v && v.TryGetValue<string>(out var ttl) && ttl == "1h"
            ? AnthropicCacheTtl.OneHour
            : AnthropicCacheTtl.FiveMinutes;
}

/// <summary>凭据 ref 的派生规则(沿用 2.x,接管数据后原来的密文仍然对得上)。</summary>
public static class ProviderRefs
{
    public const string Prefix = "provider:";

    /// <summary>托管的 NextCoWork 供应商:永远在库里,不进 providers.json,也不接受手动改凭据。</summary>
    public const string ManagedProviderId = "nextcowork";

    public static string ForProvider(string providerId) => Prefix + providerId;

    public static string ForAccount(string providerId, string accountId) => CredentialRefs.ForAccount(providerId, accountId);

    /// <summary>
    /// 从账号 ref 反查 (providerId, accountId);认不出(包括旧槽 <c>provider:&lt;id&gt;</c>)返回 null。
    /// 用第一个 <c>#</c> 切:id 本身不含 <c>#</c>,万一将来放宽了,至少 providerId 那一半是对的。
    /// </summary>
    public static (string ProviderId, string AccountId)? ParseAccountRef(string reference)
    {
        if (!reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = reference[Prefix.Length..];
        var hash = rest.IndexOf('#');
        return hash <= 0 || hash == rest.Length - 1 ? null : (rest[..hash], rest[(hash + 1)..]);
    }

    /// <summary>
    /// 文件模式下归 providers.json 管的凭据 ref 对应的供应商 id;账号密文(含 <c>#</c>)和托管供应商不归文件管,返回 null。
    /// 否则账号 ref 的写入会落进暂存区再也不落盘,读回来恒为空——「设置页显示已登录,一发消息就说没配密钥」。
    /// </summary>
    public static string? FileOwnedProviderId(string reference)
    {
        if (!reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = reference[Prefix.Length..];
        return id.Length > 0 && !id.Contains('#') && id != ManagedProviderId ? id : null;
    }

    /// <summary>供应商 id 的约束:非空、无 NUL(它是物理 ref 里的分隔符)、无斜杠(<c>providerId/alias</c> 复合键靠第一个斜杠切)、无空白。</summary>
    public static bool IsValidProviderId(string id) =>
        !string.IsNullOrWhiteSpace(id) && !id.Contains('\0') && !id.Contains('/') && !id.Any(char.IsWhiteSpace);
}

/// <summary>
/// 供应商下的一个模型别名绑定(2.x <c>ModelAlias</c>;主键 <c>(ProviderId, Alias)</c>,同一别名可以挂在多家上——这正是别名表存在的理由)。
/// 不解释的字段(能力表、思考配置、请求适配、来源、视频绑定、<c>catalogOverrides</c>)放在 <see cref="Extra"/> 里原样保留。
/// </summary>
public sealed record ModelAliasRecord(string Alias, string ProviderId, string UpstreamModel)
{
    public const int DefaultContextWindow = 200_000;

    public const int DefaultMaxOutputTokens = 8192;

    public int ContextWindow { get; init; } = DefaultContextWindow;

    public int MaxOutputTokens { get; init; } = DefaultMaxOutputTokens;

    public string? DisplayName { get; init; }

    public WireProtocol? ProtocolOverride { get; init; }

    public int? Priority { get; init; }

    public string? Modality { get; init; }

    public bool? Enabled { get; init; }

    public JsonObject Extra { get; init; } = new();

    public bool IsEnabled => Enabled != false;

    /// <summary>能力表里的图片输入。缺失按 2.x「导入新模型」的保守缺省(true:填 false 会让图片被静默换成占位文字)。</summary>
    public bool SupportsImages => Capability("visionInput") ?? Capability("vision") ?? true;

    public bool? Capability(string name) =>
        Extra["capabilities"] is JsonObject caps && caps[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}

public static class ProviderErrorKeys
{
    public const string UnknownProvider = "providers.unknownProvider";
    public const string InvalidId = "providers.invalidId";
    public const string InvalidBaseUrl = "providers.invalidBaseUrl";
    public const string InvalidName = "providers.invalidName";
    public const string InvalidOptions = "providers.invalidOptions";
    public const string ManagedProvider = "providers.managedProvider";
    public const string FileBroken = "providers.fileBroken";
    public const string UnknownAlias = "providers.unknownAlias";
    public const string AliasExists = "providers.aliasExists";
    public const string EmptyKey = "providers.emptyKey";
    public const string NoCredential = "providers.noCredential";
    public const string NoOAuth = "providers.noOAuth";
    public const string UnknownAccount = "providers.unknownAccount";
    public const string FetchFailed = "providers.fetchFailed";
    public const string FetchTimeout = "providers.fetchTimeout";
    public const string FetchRejected = "providers.fetchRejected";
    public const string FetchNotFound = "providers.fetchNotFound";
    public const string FetchEmpty = "providers.fetchEmpty";
    public const string NoLoginInProgress = "providers.noLoginInProgress";
    public const string LoginFailed = "providers.loginFailed";
    public const string LoginCancelled = "providers.loginCancelled";
}

/// <summary>供应商服务的业务错误:只带稳定的 <see cref="Key"/>、英文诊断和结构化参数,界面文字由控制端翻译。</summary>
public sealed class ProviderException(string key, string message, IReadOnlyDictionary<string, string>? parameters = null) : Exception(message)
{
    public string Key { get; } = key;

    public IReadOnlyDictionary<string, string> Parameters { get; } = parameters ?? new Dictionary<string, string>();
}
