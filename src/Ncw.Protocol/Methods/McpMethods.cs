using PolyType;

namespace Ncw.Protocol.Methods;

public static partial class Capabilities
{
    /// <summary>MCP:<c>mcp.*</c> 和 <c>mcp</c> 主题(列表 / 状态 / 资源 / 提示 member 起;配置、密钥、测试连接、重连 owner——这些会在设备上起进程或连网)。</summary>
    public const string Mcp = "mcp";
}

/// <summary>
/// MCP 服务器的方法表与主题(docs/design/09 §8.4,功能盘点 10 的 <c>mcp</c> 模块:配置、测试连接、密钥)。每个方法声明种类(command 必带幂等键)、所需角色、所需能力、允许的承载。
/// <list type="bullet">
/// <item><b>读</b>(<c>mcp.list</c> / <c>mcp.resources.list</c> / <c>mcp.prompts.list</c>):member 起——配置里有启动命令和地址(没有任何密钥),viewer 不给。</item>
/// <item><b>写</b>(<c>mcp.upsert</c> / <c>remove</c> / <c>setEnabled</c> / <c>reconnect</c> / <c>testConnection</c> / <c>setSecrets</c> / <c>getSecretsInfo</c>):owner。stdio 服务器会在设备上启动 <c>command</c>,
/// 与终端 / Hooks 同一等级的授权;远程服务器会让设备连网。<c>mcp.testConnection</c> 真的会起进程 / 连网,所以是 command(带幂等键)。</item>
/// </list>
/// ★ <b>密钥不上线</b>:配置里只有环境变量 / 请求头的<b>键名</b>(<see cref="McpServerConfigDto.EnvNames"/> / <see cref="McpServerConfigDto.HeaderNames"/>);值在 Agent 的 NCK1 凭据库里,
/// 只经 <c>mcp.setSecrets</c> 的<b>请求</b>单向写入(控制端 → Agent,整张 map 一次写完,方向和 <c>providers.credential.set</c> 一样),响应、主题、事件、错误里一概没有;
/// <c>mcp.getSecretsInfo</c> 只回「有哪些键、各自是否已设置」。连接错误只带稳定 key + 结构化参数(<see cref="McpServerStatusDto.ErrorKey"/>),不带服务器给的错误原文(路径、命令输出);
/// 脱敏后的英文诊断只在 owner 的 <see cref="McpTestResultDto.Detail"/> 里。业务失败的稳定 key 在 <c>error.details</c>(<see cref="ProviderErrorDto"/>:<c>mcp.*</c>)。
/// <b>状态是全量快照</b>:<c>mcp</c> 主题的首帧、<see cref="McpEvents.Changed"/> 与 <c>mcp.list</c> 是同一个类型 <see cref="McpTopicState"/>,带单调 <c>Rev</c>(AGENTS.md §3 规则 5)。
/// </summary>
public static class McpMethods
{
    /// <summary>全量状态(与主题首帧同一个类型)。</summary>
    public static readonly MethodDef<Unit, McpTopicState> List = Q<Unit, McpTopicState>("mcp.list", Role.Member);

    /// <summary>新增或修改一台服务器:校验 → 写库 → 先断后连(改完必须重连,不沿用旧连接)→ 返回<b>第一次连接尝试结束后</b>的状态;失败时退避重连继续在后台。</summary>
    public static readonly MethodDef<McpUpsertParams, McpServerStatusDto> Upsert = C<McpUpsertParams, McpServerStatusDto>("mcp.upsert", Role.Owner);

    /// <summary>删服务器:先断开(工具下线),再删配置与密钥。幂等:没有这台也不报错(<see cref="McpRemoveResult.Removed"/> = false)。</summary>
    public static readonly MethodDef<McpIdParams, McpRemoveResult> Remove = C<McpIdParams, McpRemoveResult>("mcp.remove", Role.Owner);

    /// <summary>启用 / 停用(停用只断开,不删配置)。</summary>
    public static readonly MethodDef<McpSetEnabledParams, McpServerStatusDto> SetEnabled = C<McpSetEnabledParams, McpServerStatusDto>("mcp.setEnabled", Role.Owner);

    /// <summary>手动重连(退避用尽之后、或用户想重来)。</summary>
    public static readonly MethodDef<McpIdParams, McpServerStatusDto> Reconnect = C<McpIdParams, McpServerStatusDto>("mcp.reconnect", Role.Owner);

    /// <summary>「测试连接」:真连一次、列工具、断开;不留常驻连接、不改状态。失败是结果(<see cref="McpTestResultDto.Ok"/> = false + key),不是错误。可以带一份还没保存的草稿配置。</summary>
    public static readonly MethodDef<McpTestParams, McpTestResultDto> TestConnection = C<McpTestParams, McpTestResultDto>("mcp.testConnection", Role.Owner);

    /// <summary>写密钥:整张 map 一次写完(没声明的键丢弃),写完重连。响应只有键名和「是否已设置」。</summary>
    public static readonly MethodDef<McpSetSecretsParams, McpSecretsInfoDto> SetSecrets = C<McpSetSecretsParams, McpSecretsInfoDto>("mcp.setSecrets", Role.Owner);

    /// <summary>这台服务器的密钥现在存了哪几个键(<b>键名,不是值</b>)。</summary>
    public static readonly MethodDef<McpIdParams, McpSecretsInfoDto> GetSecretsInfo = Q<McpIdParams, McpSecretsInfoDto>("mcp.getSecretsInfo", Role.Owner);

    /// <summary>服务器列出的资源(只读;需要已连接,否则 <c>unavailable</c> + <c>mcp.notConnected</c>)。</summary>
    public static readonly MethodDef<McpIdParams, McpResourcesResult> ResourcesList = Q<McpIdParams, McpResourcesResult>("mcp.resources.list", Role.Member);

    /// <summary>服务器列出的提示(只读;需要已连接)。</summary>
    public static readonly MethodDef<McpIdParams, McpPromptsResult> PromptsList = Q<McpIdParams, McpPromptsResult>("mcp.prompts.list", Role.Member);

    /// <summary>全部服务器的全量状态 + 单调版本号。member 起可订阅(含启动命令 / 地址,不含任何密钥)。</summary>
    public static readonly TopicDef<McpTopicState> Topic = new("mcp", PayloadShapes.GetShape<McpTopicState>(), Role.Member);

    public static readonly IReadOnlyList<MethodDef> All =
    [
        List, Upsert, Remove, SetEnabled, Reconnect, TestConnection, SetSecrets, GetSecretsInfo, ResourcesList, PromptsList,
    ];

    private static MethodDef<TP, TR> Q<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Mcp };

    private static MethodDef<TP, TR> C<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Mcp };
}

public static class McpEvents
{
    /// <summary>状态变了(数据是新的 <see cref="McpTopicState"/>,全量,带单调 <c>Rev</c>;控制端只收不更旧的)。</summary>
    public const string Changed = "mcp.changed";
}

/// <summary>连接状态的取值。</summary>
public static class McpStates
{
    public const string Disabled = "disabled";

    /// <summary>正在连接 / 等待重连。</summary>
    public const string Connecting = "connecting";

    public const string Connected = "connected";

    /// <summary>失败(<see cref="McpServerStatusDto.ErrorKey"/> 说明原因)。设备断开时控制端自己标 unknown,不是这里的取值。</summary>
    public const string Failed = "failed";
}

public static class McpTransports
{
    public const string Stdio = "stdio";

    public const string Sse = "sse";

    public const string StreamableHttp = "streamable-http";
}

/// <summary>
/// 一台 MCP 服务器的配置(2.x <c>McpServerConfig</c> 的形状)。<b>没有任何密钥值</b>,只有键名。
/// stdio 用 <see cref="Command"/> / <see cref="Args"/> / <see cref="EnvNames"/> / <see cref="Cwd"/>;sse / streamable-http 用 <see cref="Url"/> / <see cref="HeaderNames"/>。
/// <see cref="WorkspaceId"/> 非 null = 绑定到这个项目:服务器在项目的执行环境里运行 / 连接,并且只对这个项目的会话可见;null = 全局(在设备本机)。
/// </summary>
/// <param name="Transport"><c>stdio</c> / <c>sse</c> / <c>streamable-http</c>(<see cref="McpTransports"/>)。</param>
public sealed record McpServerConfigDto(
    string Id,
    string Name,
    string Transport,
    bool Enabled = true,
    string? Description = null,
    string? WorkspaceId = null,
    string? Command = null,
    string[]? Args = null,
    string[]? EnvNames = null,
    string? Cwd = null,
    string? Url = null,
    string[]? HeaderNames = null);

/// <summary>没有接入的工具及原因(<see cref="ReasonKey"/> 是稳定的 <c>mcp.tool.*</c> key)。<see cref="Name"/> 是服务器给的(不可信文本,只当数据显示)。</summary>
public sealed record McpRejectedToolDto(string Name, string ReasonKey);

/// <summary>
/// 一行服务器状态 = 配置 + 运行时状态(合成一个:两份各自到达的话,中间那一帧必有一边是旧的)。
/// <see cref="Tools"/> 是已接入的工具名(服务器的原名);<see cref="Rejected"/> 即使连接成功也照实列出没接入的;
/// <see cref="Attempt"/> 是连续失败次数(0 = 没有失败)。时间是 Unix 毫秒。
/// </summary>
public sealed record McpServerStatusDto(
    McpServerConfigDto Config,
    string State,
    string? ErrorKey,
    Dictionary<string, string>? ErrorParams,
    string[] Tools,
    McpRejectedToolDto[] Rejected,
    long? ConnectedAt,
    int Attempt,
    string? ServerName,
    string? ServerVersion);

/// <summary><c>mcp</c> 主题的全量状态(快照 = <see cref="McpEvents.Changed"/> = <c>mcp.list</c>):所有服务器,按 id 排序;<see cref="Rev"/> 单调。</summary>
public sealed record McpTopicState(long Rev, McpServerStatusDto[] Servers);

public sealed record McpIdParams(string Id);

public sealed record McpUpsertParams(McpServerConfigDto Config);

public sealed record McpSetEnabledParams(string Id, bool Enabled);

/// <param name="Config">非 null = 测这份还没保存的草稿(密钥仍取同 id 已存的那份);null = 测库里存的那份。</param>
public sealed record McpTestParams(string Id, McpServerConfigDto? Config = null);

/// <summary>
/// 写密钥。<see cref="Values"/>:键名 → 明文值(<b>方向:控制端 → Agent</b>);整张 map 替换,配置没声明的键丢弃。
/// 响应 / 主题 / 事件 / 错误里永远没有这些值。
/// </summary>
public sealed record McpSetSecretsParams(string Id, Dictionary<string, string> Values);

public sealed record McpRemoveResult(bool Removed);

/// <summary>某个键「有没有值」——只有键名和是否已设置。</summary>
public sealed record McpSecretEntryDto(string Name, bool IsSet);

/// <param name="Kind"><c>env</c>(stdio 的环境变量)/ <c>headers</c>(远程的请求头)。</param>
public sealed record McpSecretsInfoDto(string Id, string Kind, McpSecretEntryDto[] Entries);

/// <summary>
/// 测试连接的结果。<see cref="Detail"/> 是脱敏后的英文诊断(含服务器 stderr 的尾部,可能有路径),<b>只在 owner 的这个结果里</b>,不进主题。
/// </summary>
public sealed record McpTestResultDto(
    bool Ok,
    string[] Tools,
    long ElapsedMs,
    string? ErrorKey,
    Dictionary<string, string>? ErrorParams,
    string? Detail,
    string? ServerName,
    string? ServerVersion,
    string? ProtocolVersion,
    McpRejectedToolDto[] Rejected);

public sealed record McpResourceDto(string Uri, string Name, string? Description, string? MimeType);

public sealed record McpResourcesResult(McpResourceDto[] Resources);

public sealed record McpPromptDto(string Name, string? Description, string[] Arguments);

public sealed record McpPromptsResult(McpPromptDto[] Prompts);

[GenerateShapeFor<McpServerConfigDto>]
[GenerateShapeFor<McpRejectedToolDto>]
[GenerateShapeFor<McpServerStatusDto>]
[GenerateShapeFor<McpTopicState>]
[GenerateShapeFor<McpIdParams>]
[GenerateShapeFor<McpUpsertParams>]
[GenerateShapeFor<McpSetEnabledParams>]
[GenerateShapeFor<McpTestParams>]
[GenerateShapeFor<McpSetSecretsParams>]
[GenerateShapeFor<McpRemoveResult>]
[GenerateShapeFor<McpSecretEntryDto>]
[GenerateShapeFor<McpSecretsInfoDto>]
[GenerateShapeFor<McpTestResultDto>]
[GenerateShapeFor<McpResourceDto>]
[GenerateShapeFor<McpResourcesResult>]
[GenerateShapeFor<McpPromptDto>]
[GenerateShapeFor<McpPromptsResult>]
public sealed partial class PayloadShapes;
