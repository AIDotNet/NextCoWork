using PolyType;

namespace Ncw.Protocol.Methods;

// 执行环境(SSH 多设备执行)领域的能力名(docs/design/06、09 §8.4、10 的 connection 模块)。
public static partial class Capabilities
{
    /// <summary>执行环境:<c>environments.*</c> 和 <c>environments</c> 主题(SSH 环境的配置、连接状态、远程目录浏览)。读取 member 起,写入 owner。</summary>
    public const string Environments = "environments";
}

/// <summary>
/// 执行环境的方法表和主题(docs/design/06 §6,功能盘点 10 的 <c>connection</c> 模块)。能力 <see cref="Capabilities.Environments"/>,承载 本机 + 远程。
/// 真源在设备的 <c>EnvironmentRegistry</c>(配置)与各环境实例(状态);控制端只镜像(规则 1)。
/// <list type="bullet">
/// <item><b>角色</b>:读取(<c>list</c> / <c>status</c> / <c>browse</c>)member 起——环境清单里有内网主机名和用户名,属于设备配置,viewer 不给;
/// <c>connect</c> / <c>disconnect</c> member 起(连接是惰性的,member 本来就能通过项目触发它;<c>disconnect</c> 对在用的环境返回 <c>conflict</c>);
/// 配置的增改删、默认环境、试连接、从 <c>~/.ssh/config</c> 导入、已知主机的查看和移除 owner(它们决定设备会去连谁、信任谁,或在设备上读文件)。</item>
/// <item><b>凭据只写不读</b>:密码、私钥口令、代理密码、私钥文件路径只出现在 <c>create</c> / <c>update</c> / <c>test</c> 的<b>请求</b>里(控制端 → Agent 单向);
/// 响应、主题、事件、错误、日志里只有「有没有」(<c>HasSavedPassword</c> / <c>HasIdentityFile</c> / <c>HasPassword</c>)。已保存的凭据用 NCK1 存在设备上。</item>
/// <item><b>交互</b>:连接过程中的主机指纹确认、密码 / 口令 / 键盘交互式提问走 <c>interactions</c>(<c>ssh_host_key</c> / <c>ssh_credential</c>,见 <see cref="SshHostKeyInteractionDto"/>),
/// 任何已连接的控制端都能回答;指纹和钉扎的不一致时连接直接失败,详情在环境状态的 <see cref="EnvironmentErrorDto.HostKey"/>,不是交互。</item>
/// </list>
/// 业务失败是粗粒度错误码 + <c>error.details</c>(<see cref="ProviderErrorDto"/>,稳定 <c>environments.*</c> key):<c>not_found</c>(没有这个环境)、<c>invalid_request</c>(配置不合法)、
/// <c>conflict</c>(版本过期 / 在用)、<c>unavailable</c>(连不上,可重试)、<c>unsupported</c>(没有 SSH 服务)。
/// </summary>
public static class EnvironmentsMethods
{
    /// <summary>全部环境(含 <c>local</c>),与 <c>environments</c> 主题的状态是同一个类型。</summary>
    public static readonly MethodDef<Unit, EnvironmentsTopicState> List = Query<Unit, EnvironmentsTopicState>("environments.list", Role.Member);

    public static readonly MethodDef<EnvironmentIdParams, EnvironmentDto> Status = Query<EnvironmentIdParams, EnvironmentDto>("environments.status", Role.Member);

    /// <summary>新建 SSH 环境(设备铸 id)。<c>Secrets</c> 里的密码 / 口令存 NCK1,响应里没有。</summary>
    public static readonly MethodDef<EnvironmentCreateParams, EnvironmentDto> Create = Command<EnvironmentCreateParams, EnvironmentDto>("environments.create", Role.Owner);

    /// <summary>
    /// 改配置。<c>ExpectedRevision</c> 对不上 <c>conflict</c>。已连接的实例会被关闭,下次使用时按新配置重连。
    /// 私钥文件路径 / 配置文件路径:null = 保持,空串 = 清除;密码 / 口令:null = 保持,空串 = 清除(同 2.x 的 <c>password</c> 字段)。
    /// </summary>
    public static readonly MethodDef<EnvironmentUpdateParams, EnvironmentDto> Update = Command<EnvironmentUpdateParams, EnvironmentDto>("environments.update", Role.Owner);

    /// <summary>删除环境(连同它保存的凭据)。还有项目绑定在上面 <c>conflict</c>(<c>environments.inUse</c>);<c>local</c> 不能删。</summary>
    public static readonly MethodDef<EnvironmentIdParams, Unit> Remove = Command<EnvironmentIdParams, Unit>("environments.remove", Role.Owner);

    public static readonly MethodDef<EnvironmentIdParams, Unit> SetDefault = Command<EnvironmentIdParams, Unit>("environments.setDefault", Role.Owner);

    /// <summary>
    /// 连接(或失败后重新连接)。<b>等到这次连接尝试结束</b>才返回(结果是结束时的 <see cref="EnvironmentDto"/>:<c>ready</c> 或 <c>failed</c>,失败是结果不是错误);
    /// 等待期间主机指纹确认、凭据提问以交互的形式出现,任何控制端可答。请求被取消(<c>cancel</c> 帧、控制端断开)时连接尝试一并取消,环境回到 <c>idle</c>。
    /// </summary>
    public static readonly MethodDef<EnvironmentIdParams, EnvironmentDto> Connect = Command<EnvironmentIdParams, EnvironmentDto>("environments.connect", Role.Member);

    /// <summary>断开并回收连接(配置保留,下次使用惰性重连)。有 Run / 终端 / 后台 Shell 在用 <c>conflict</c>(<c>environments.busy</c>)。</summary>
    public static readonly MethodDef<EnvironmentIdParams, EnvironmentDto> Disconnect = Command<EnvironmentIdParams, EnvironmentDto>("environments.disconnect", Role.Member);

    /// <summary>
    /// 试连接(可以是还没保存的草稿):真连一次、探测环境事实、测一次往返、断开。<b>结果是结构化的,失败不是错误</b>。
    /// 首次出现的主机指纹照常以交互确认(确认后钉扎,和正式连接是同一个指纹库)。草稿没有保存,所以凭据「记住」不可用。
    /// </summary>
    public static readonly MethodDef<EnvironmentTestParams, EnvironmentTestResult> Test = Command<EnvironmentTestParams, EnvironmentTestResult>("environments.test", Role.Owner);

    /// <summary>浏览环境里的远程目录(只列目录),新建项目选根目录时用。环境没连接会先连接(同 <c>connect</c> 的交互)。<c>Path</c> 缺省 = 远程 home。</summary>
    public static readonly MethodDef<EnvironmentBrowseParams, RemoteDirectoryDto> Browse = Query<EnvironmentBrowseParams, RemoteDirectoryDto>("environments.browse", Role.Member);

    /// <summary>读设备上的 <c>~/.ssh/config</c>(或指定文件),列出可导入的主机别名。不连接任何主机。</summary>
    public static readonly MethodDef<ImportSshConfigParams, SshConfigImportResult> ImportSshConfig = Query<ImportSshConfigParams, SshConfigImportResult>("environments.importSshConfig", Role.Owner);

    /// <summary>已知主机(钉扎的指纹)。</summary>
    public static readonly MethodDef<Unit, KnownHostsResult> KnownHosts = Query<Unit, KnownHostsResult>("environments.knownHosts", Role.Owner);

    /// <summary>忘记一台主机的钉扎(用户明确选择「信任新指纹」时的第一步:之后再连会重新弹确认)。指纹变化的拒绝从不自动覆盖,只有这里能移除。</summary>
    public static readonly MethodDef<ForgetHostKeyParams, Unit> ForgetHostKey = Command<ForgetHostKeyParams, Unit>("environments.forgetHostKey", Role.Owner);

    /// <summary>全部环境的全量状态。<b>首帧、<c>environments.changed</c> 事件、<c>environments.list</c> 是同一个类型</b>(规则 5),<c>Rev</c> 单调(规则 6)。</summary>
    public static readonly TopicDef<EnvironmentsTopicState> Topic = new("environments", PayloadShapes.GetShape<EnvironmentsTopicState>(), Role.Member);

    public static readonly IReadOnlyList<MethodDef> All =
    [
        List, Status, Create, Update, Remove, SetDefault, Connect, Disconnect, Test, Browse, ImportSshConfig, KnownHosts, ForgetHostKey,
    ];

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Environments };

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Environments };
}

/// <summary><c>environments</c> 主题的事件类型。</summary>
public static class EnvironmentsEvents
{
    /// <summary>环境配置或状态变了(数据是新的 <see cref="EnvironmentsTopicState"/>,全量)。</summary>
    public const string Changed = "environments.changed";
}

/// <summary><see cref="EnvironmentDto.Kind"/> 的取值。</summary>
public static class EnvironmentKindNames
{
    public const string Local = "local";
    public const string Ssh = "ssh";
}

/// <summary>
/// <see cref="EnvironmentDto.State"/> 的取值(和 <c>EnvironmentState</c> 一一对应)。控制端断开时标为「未知」是控制端自己的事(规则 7),不在线上。
/// </summary>
public static class EnvironmentStateNames
{
    /// <summary>还没连接(SSH 惰性连接,或空闲回收之后)。</summary>
    public const string Idle = "idle";
    public const string Connecting = "connecting";
    public const string Ready = "ready";
    public const string Reconnecting = "reconnecting";
    public const string Failed = "failed";
}

/// <summary><see cref="EnvironmentErrorDto.Key"/> 与 <c>error.details</c> 里 <c>environments.*</c> key 的取值(控制端翻译)。</summary>
public static class EnvironmentErrorKeys
{
    public const string NotFound = "environments.notFound";
    public const string InvalidProfile = "environments.invalidProfile";
    public const string Conflict = "environments.conflict";
    public const string InUse = "environments.inUse";
    public const string Busy = "environments.busy";
    public const string Disabled = "environments.disabled";
    public const string NotConnected = "environments.notConnected";
    public const string LocalImmutable = "environments.localImmutable";
    public const string NotRemote = "environments.notRemote";
    public const string TargetChangeNeedsConfirm = "environments.targetChangeNeedsConfirm";
    public const string SecretsUnavailable = "environments.secretsUnavailable";
    public const string NoSshServices = "environments.noSshServices";
    public const string Authentication = "environments.authentication";
    public const string HostKeyChanged = "environments.hostKeyChanged";
    public const string HostKeyRejected = "environments.hostKeyRejected";
    public const string Cancelled = "environments.cancelled";
    public const string ConnectionFailed = "environments.connectionFailed";
    public const string Timeout = "environments.timeout";
    public const string Disconnected = "environments.disconnected";
    public const string UnsupportedPlatform = "environments.unsupportedPlatform";
    public const string Unsupported = "environments.unsupported";
    public const string InvalidPath = "environments.invalidPath";
    public const string NotFoundPath = "environments.pathNotFound";
    public const string PermissionDenied = "environments.permissionDenied";
    public const string Internal = "environments.internal";
}

/// <summary>主机指纹和钉扎的不一致(只有指纹和算法,公开信息;不含任何凭据)。</summary>
/// <param name="HostName">钉扎用的主机名(默认端口是裸主机名,否则 <c>[host]:port</c>)。</param>
/// <param name="Presented">服务器这次出示的指纹(SHA256,Base64 不带 <c>=</c> 和前缀,<c>ssh-keygen -l</c> 同款)。</param>
public sealed record HostKeyMismatchDto(string HostName, string Algorithm, string Presented, PinnedKeyDto[] Pinned);

public sealed record PinnedKeyDto(string Algorithm, string Fingerprint);

/// <summary>一次连接失败的原因:稳定 key(<see cref="EnvironmentErrorKeys"/>)+ 指纹不一致时的详情。不含任何凭据、路径或系统消息。</summary>
public sealed record EnvironmentErrorDto(string Key, HostKeyMismatchDto? HostKey = null);

/// <summary>远程主机的环境事实(没有路径:home 目录之类只在设备内部用)。<see cref="Os"/>:<c>linux</c> / <c>macos</c> / <c>windows</c>。</summary>
public sealed record EnvironmentFactsDto(string Os, string Architecture, string Hostname, string Username);

/// <summary>SSH 连接目标。路径只以「有没有」出现(<see cref="HasIdentityFile"/> / <see cref="HasConfigFile"/>)。</summary>
/// <param name="Kind"><c>config</c>(引用 ssh_config 里的 Host 别名)/ <c>manual</c>(手填)。</param>
/// <param name="Host">别名或主机名。</param>
public sealed record SshTargetDto(string Kind, string Host, int? Port, string? Username, bool HasIdentityFile, string? ProxyJump, bool HasConfigFile);

/// <param name="Kind"><c>http</c> / <c>socks4</c> / <c>socks5</c>。</param>
public sealed record SshProxyDto(string Kind, string Host, int Port, string? Username, bool HasPassword);

/// <summary>
/// 一个执行环境。<see cref="Revision"/> 是配置的修订号(改配置才变,连接状态变化不变),<c>update</c> 用它做乐观锁。
/// <see cref="State"/> 是 <see cref="EnvironmentStateNames"/> 之一;<see cref="Error"/> 只在 <c>failed</c> 时有值。
/// </summary>
/// <param name="Generation">每次(重)连成功加一;旧连接代发起的进程、流以「连接中断」结束。</param>
/// <param name="LatencyMs">最近一次探测到的往返延迟(连接成功后测一次);没测过为 null。</param>
/// <param name="AuthMethod"><c>auto</c> / <c>password</c> / <c>key</c> / <c>interactive</c> / <c>ask</c>。</param>
/// <param name="Platform"><c>auto</c> / <c>linux</c> / <c>macos</c> / <c>windows</c>。</param>
public sealed record EnvironmentDto(
    string Id,
    string Name,
    string Kind,
    string State,
    int Generation,
    bool IsDefault,
    bool Enabled,
    long Revision,
    EnvironmentErrorDto? Error = null,
    long? LatencyMs = null,
    SshTargetDto? Target = null,
    string? AuthMethod = null,
    string? Platform = null,
    SshProxyDto? Proxy = null,
    bool HasSavedPassword = false,
    EnvironmentFactsDto? Facts = null);

/// <summary><c>environments</c> 主题的状态:权威全量,<see cref="Rev"/> 单调(设备上每次发布加一)。<c>local</c> 永远在最前。</summary>
public sealed record EnvironmentsTopicState(long Rev, string DefaultId, EnvironmentDto[] Environments);

public sealed record EnvironmentIdParams(string Id);

/// <param name="Port">缺省 22。</param>
/// <param name="IdentityFile">私钥文件路径(设备上的路径;只写不读)。</param>
/// <param name="ConfigFile">别名所在的 ssh_config 文件(缺省 <c>~/.ssh/config</c>;只写不读)。</param>
/// <param name="ProxyJump"><c>user@host:port,…</c>(只对 <c>manual</c> 有意义;<c>config</c> 目标的跳板机来自 ssh_config)。</param>
public sealed record SshTargetInput(string Kind, string Host, int? Port = null, string? Username = null, string? IdentityFile = null, string? ConfigFile = null, string? ProxyJump = null);

/// <param name="Kind"><c>http</c> / <c>socks4</c> / <c>socks5</c>。代理密码走 <see cref="EnvironmentSecretsInput.ProxyPassword"/>。</param>
public sealed record SshProxyInput(string Kind, string Host, int Port, string? Username = null);

/// <summary>环境配置的输入。不含任何凭据(凭据在 <see cref="EnvironmentSecretsInput"/>,单独一块)。</summary>
/// <param name="Name">显示名(1–120 字符)。</param>
/// <param name="AuthMethod">缺省 <c>auto</c>。</param>
/// <param name="Platform">缺省 <c>auto</c>。</param>
/// <param name="Proxy">null = 不经代理(update 时 null 也是「不经代理」:整份配置以输入为准,只有路径和凭据有「null = 保持」的语义)。</param>
public sealed record EnvironmentInput(string Name, SshTargetInput Target, string AuthMethod = "auto", string Platform = "auto", bool Enabled = true, SshProxyInput? Proxy = null);

/// <summary>
/// 凭据(只写不读)。<b>null = 不动已存的,空串 = 清除,非空 = 写入</b>(同 2.x 的 <c>password</c>)。
/// <see cref="KeyPassphrase"/> 是 <see cref="SshTargetInput.IdentityFile"/> 那把私钥的口令。
/// </summary>
public sealed record EnvironmentSecretsInput(string? Password = null, string? KeyPassphrase = null, string? ProxyPassword = null)
{
    /// <summary>★ 自动生成的 <c>ToString()</c> 会打出每个属性:凭据不能经日志、异常消息、断言输出带出去。</summary>
    public override string ToString() => "EnvironmentSecretsInput { redacted }";
}

public sealed record EnvironmentCreateParams(EnvironmentInput Profile, EnvironmentSecretsInput? Secrets = null);

/// <param name="ConfirmTargetChange">改了连接目标(主机 / 端口 / 用户名 / 跳板机 / 平台)而已有项目绑在这个环境上时必须为真(2.x 的 <c>confirmTargetChange</c>):否则 <c>conflict</c>(<c>environments.targetChangeNeedsConfirm</c>)——项目的远程路径会指向另一台机器。</param>
public sealed record EnvironmentUpdateParams(string Id, long ExpectedRevision, EnvironmentInput Profile, EnvironmentSecretsInput? Secrets = null, bool ConfirmTargetChange = false);

/// <param name="ExistingId">非空 = 这份草稿是在改一个已存的环境:路径 / 凭据里的 null 沿用那个环境已存的,试连接也能用它保存的凭据。</param>
public sealed record EnvironmentTestParams(EnvironmentInput Profile, string? ExistingId = null, EnvironmentSecretsInput? Secrets = null);

/// <param name="Ok">连上了。失败时 <see cref="Error"/> 有稳定 key。</param>
public sealed record EnvironmentTestResult(bool Ok, EnvironmentErrorDto? Error = null, long? LatencyMs = null, EnvironmentFactsDto? Facts = null);

public sealed record EnvironmentBrowseParams(string EnvironmentId, string? Path = null);

/// <summary>远程目录里的一项(只列目录)。路径是<b>远程主机上的路径</b>(选项目根目录用的)。</summary>
public sealed record RemoteDirEntryDto(string Name, string Path);

/// <summary>面包屑:从根到当前目录,每一级的名字和路径。</summary>
public sealed record RemoteCrumbDto(string Name, string Path);

/// <param name="Path">规范化(realpath)后的目录。</param>
/// <param name="Parent">上一级;已经是根时为 null。</param>
/// <param name="Roots">可选的根:POSIX 是 <c>/</c>,Windows 是各个盘符。</param>
/// <param name="Truncated">条目太多,只给了前面一部分(2000 条)。</param>
public sealed record RemoteDirectoryDto(string Path, string? Parent, RemoteCrumbDto[] Breadcrumbs, string[] Roots, RemoteDirEntryDto[] Entries, bool Truncated, EnvironmentFactsDto Facts);

/// <param name="ConfigFile">null = 默认的 <c>~/.ssh/config</c>。</param>
public sealed record ImportSshConfigParams(string? ConfigFile = null);

/// <summary>ssh_config 里的一个可导入的主机(已套用配置解析:HostName / User / Port / ProxyJump)。私钥路径只以「有没有」出现。</summary>
public sealed record SshConfigCandidateDto(string Alias, string HostName, int Port, string? Username, bool HasIdentityFile, string? ProxyJump, bool Importable, string[] Warnings);

/// <param name="Warnings">整份配置层面的诊断(不支持的 <c>Match</c> 块等,稳定 key)。</param>
public sealed record SshConfigImportResult(SshConfigCandidateDto[] Candidates, string[] Warnings);

public sealed record KnownHostDto(string HostName, string Algorithm, string Fingerprint);

public sealed record KnownHostsResult(KnownHostDto[] Hosts);

public sealed record ForgetHostKeyParams(string HostName);

[GenerateShapeFor<HostKeyMismatchDto>]
[GenerateShapeFor<PinnedKeyDto>]
[GenerateShapeFor<EnvironmentErrorDto>]
[GenerateShapeFor<EnvironmentFactsDto>]
[GenerateShapeFor<SshTargetDto>]
[GenerateShapeFor<SshProxyDto>]
[GenerateShapeFor<EnvironmentDto>]
[GenerateShapeFor<EnvironmentsTopicState>]
[GenerateShapeFor<EnvironmentIdParams>]
[GenerateShapeFor<SshTargetInput>]
[GenerateShapeFor<SshProxyInput>]
[GenerateShapeFor<EnvironmentInput>]
[GenerateShapeFor<EnvironmentSecretsInput>]
[GenerateShapeFor<EnvironmentCreateParams>]
[GenerateShapeFor<EnvironmentUpdateParams>]
[GenerateShapeFor<EnvironmentTestParams>]
[GenerateShapeFor<EnvironmentTestResult>]
[GenerateShapeFor<EnvironmentBrowseParams>]
[GenerateShapeFor<RemoteDirEntryDto>]
[GenerateShapeFor<RemoteCrumbDto>]
[GenerateShapeFor<RemoteDirectoryDto>]
[GenerateShapeFor<ImportSshConfigParams>]
[GenerateShapeFor<SshConfigCandidateDto>]
[GenerateShapeFor<SshConfigImportResult>]
[GenerateShapeFor<KnownHostDto>]
[GenerateShapeFor<KnownHostsResult>]
[GenerateShapeFor<ForgetHostKeyParams>]
public sealed partial class PayloadShapes;
