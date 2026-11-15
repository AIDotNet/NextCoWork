using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ncw.Protocol.Orchestration;

// 云端 Agent 与编排服务之间的「编排控制通道」(docs/design/02 A6、05 §6 B6–B9)。
// ★ 这不是客户端协议:控制端(桌面 / 手机)永远不会看到这些类型,也不会经设备协议(L1–L4)收发它们。
//   这里只有 Agent 进程和 nextco.work 编排服务之间的 HTTPS + JSON 约定,放在 Ncw.Protocol 里只因为「协议定义只有一个来源」(AGENTS.md §4)。
// ★ 命名:仓库里已经有 `Ncw.Protocol.Methods.OrchestrationMethods`(子代理 / 目标的「编排」),那是对话编排,和这里的「云端编排服务」无关。
// ★ 线上格式由 `GoldenOrchestration` 冻结;改格式必须升 `OrchestrationProtocol.Version` 或加可选字段。

/// <summary>通道约定的版本与常量。</summary>
public static class OrchestrationProtocol
{
    /// <summary>约定的版本;不兼容的改动才升,新增可选字段不升。</summary>
    public const int Version = 1;

    /// <summary>指令签名的域分隔前缀(同一把编排密钥给「指令」和「身份绑定」签名时互不通用)。</summary>
    public const string CommandContext = "ncw-orch-command/1";

    /// <summary>身份绑定(编排服务对「这台设备属于这个账户」的声明)的签名域前缀。</summary>
    public const string BindingContext = "ncw-orch-binding/1";

    /// <summary>设备向编排服务注册身份时,设备私钥签名的域前缀(证明持有私钥)。</summary>
    public const string RegisterContext = "ncw-cloud-register/1";

    /// <summary>一条指令(整个信封)的体积上限。</summary>
    public const int MaxEnvelopeBytes = 256 * 1024;

    /// <summary>一次拉取最多返回的指令数。</summary>
    public const int MaxCommandsPerPoll = 64;

    /// <summary>指令签发时间与设备时钟允许的偏差(设备时钟在休眠唤醒后可能不准)。</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>HTTP 路径(相对编排服务的基础地址;设备 id 放在头 <c>X-Ncw-Device</c> 里,令牌放在 <c>Authorization: Bearer</c> 里)。</summary>
    public static class Paths
    {
        public const string Hello = "/v1/agent/hello";
        public const string Commands = "/v1/agent/commands";
        public const string Ack = "/v1/agent/ack";
        public const string Usage = "/v1/agent/usage";
        public const string Lifecycle = "/v1/agent/lifecycle";
    }

    public const string DeviceHeader = "X-Ncw-Device";
}

/// <summary>指令类型(<see cref="CommandBody.Type"/>)。</summary>
public static class CommandTypes
{
    /// <summary>下发(或覆盖)一条信任记录:一个控制端的证书指纹、角色和路由令牌哈希。</summary>
    public const string TrustPut = "trust.put";

    /// <summary>吊销一个控制端:立即断开它的所有连接并作废它的路由令牌。</summary>
    public const string TrustRevoke = "trust.revoke";

    /// <summary>整体替换信任记录(唤醒后对账用):不在列表里的控制端被吊销。</summary>
    public const string TrustReplace = "trust.replace";

    /// <summary>设置配额(整体替换)。</summary>
    public const string QuotaSet = "quota.set";

    /// <summary>请求休眠(<see cref="SleepRequest"/>)。</summary>
    public const string SleepRequest = "sleep.request";

    /// <summary>请求升级(<see cref="UpgradeRequest"/>):没有运行中的 Run 时持久化状态并退出,由编排服务换版本。</summary>
    public const string UpgradeRequest = "upgrade.request";

    /// <summary>更新账户身份令牌(令牌轮换)。</summary>
    public const string AccountToken = "account.token";

    public static bool IsKnown(string? type) =>
        type is TrustPut or TrustRevoke or TrustReplace or QuotaSet or SleepRequest or UpgradeRequest or AccountToken;
}

/// <summary>休眠请求的模式。</summary>
public static class SleepModes
{
    /// <summary>只在空闲时休眠:有运行中的 Run、连接的控制端就拒绝(<c>busy</c>),编排服务稍后重试或等 Agent 自己判定空闲。</summary>
    public const string WhenIdle = "whenIdle";

    /// <summary>立即休眠:中止运行中的 Run(记为被休眠打断,唤醒后可见)、断开控制端。</summary>
    public const string Now = "now";
}

/// <summary>升级请求的模式。</summary>
public static class UpgradeModes
{
    /// <summary>等没有运行中的 Run 再退出(设计:只在没有运行中 Run 的时候换版本)。</summary>
    public const string WhenIdle = "whenIdle";

    /// <summary>立即(与 <see cref="SleepModes.Now"/> 同样的中止语义)。</summary>
    public const string Now = "now";
}

/// <summary>指令的回执状态。</summary>
public static class AckStatuses
{
    public const string Applied = "applied";

    /// <summary>验签失败、过期、序号重放、不认识的类型……(<see cref="CommandAck.Code"/> 说明原因)。</summary>
    public const string Rejected = "rejected";

    /// <summary>已经应用过(序号 ≤ 已应用的最大序号):编排服务据此停止重发,不是错误。</summary>
    public const string Duplicate = "duplicate";

    /// <summary>暂时做不了(例如休眠请求时有 Run 在跑),稍后可重试;序号<b>不</b>被消耗。</summary>
    public const string Busy = "busy";
}

/// <summary>验证失败的原因(<see cref="CommandAck.Code"/>)。</summary>
public static class RejectCodes
{
    public const string BadFormat = "bad_format";
    public const string UnknownKey = "unknown_key";
    public const string BadSignature = "bad_signature";
    public const string WrongDevice = "wrong_device";
    public const string Expired = "expired";
    public const string NotYetValid = "not_yet_valid";
    public const string Replayed = "replayed";
    public const string UnknownType = "unknown_type";
    public const string Invalid = "invalid";
}

/// <summary>
/// 签名信封(指令和身份绑定共用):<see cref="Payload"/> 是 JSON 字节的 base64,<see cref="Sig"/> 是编排密钥(Ed25519)对
/// <c>域前缀 ‖ 0x00 ‖ payload 字节</c> 的签名。签的是字节而不是「规范化的 JSON」,所以两端不需要约定字段顺序或空白。
/// </summary>
public sealed record SignedEnvelope(int V, string Kid, string Payload, string Sig);

/// <summary>一条信任记录(控制端把证书公钥注册到账户 API 后,编排服务生成它)。</summary>
/// <param name="Fingerprint">控制端证书公钥(SPKI)的 SHA-256,小写十六进制(和设备上 <c>controller_trust.fingerprint</c> 同一个形式)。</param>
/// <param name="Name">给人看的名字(≤ 64 字符)。</param>
/// <param name="Role"><c>owner</c> / <c>member</c> / <c>viewer</c>。</param>
/// <param name="RouteTokenHash">中继路由令牌的哈希(<c>RelayProtocol.HashToken</c>);令牌本身只由编排服务交给控制端,设备和中继只有哈希。</param>
public sealed record TrustEntry(string Fingerprint, string Name, string Role, string RouteTokenHash);

/// <summary>
/// 配额(02 A6、05 B9)。每个字段 null = 不限。日窗口 / 月窗口按 <b>UTC</b> 自然日 / 自然月计(待和编排服务对齐,见 05 §6 的假设清单)。
/// </summary>
/// <param name="DailyRuns">每个 UTC 日最多发起的顶层 Run 数。</param>
/// <param name="MonthlyRuns">每个 UTC 月最多发起的顶层 Run 数。</param>
/// <param name="DailyTokens">每个 UTC 日最多的 token 数(输入 + 输出 + 缓存读写,取自用量表)。</param>
/// <param name="MonthlyTokens">每个 UTC 月最多的 token 数。</param>
/// <param name="MaxConcurrentRuns">同时运行的顶层 Run 数上限(02 A6「并发 Run 的上限」)。</param>
/// <param name="MonthlyComputeSeconds">每个 UTC 月的计算时长(Agent 醒着的秒数,02 A6「计算时长」)。</param>
/// <param name="StorageBytes">数据根 + 工作区的存储上限(02 A6「存储」);超限后拒绝新 Run(「用尽后转为只读」)。</param>
/// <param name="Frozen">账户被冻结(欠费 / 违规 / 灰度白名单被移除):拒绝一切新 Run。</param>
public sealed record QuotaLimits(
    long? DailyRuns = null,
    long? MonthlyRuns = null,
    long? DailyTokens = null,
    long? MonthlyTokens = null,
    int? MaxConcurrentRuns = null,
    long? MonthlyComputeSeconds = null,
    long? StorageBytes = null,
    bool Frozen = false);

public sealed record SleepRequest(string Mode, string? Reason = null);

public sealed record UpgradeRequest(string Version, string Mode);

/// <summary>账户身份令牌(模型通道 / 配置同步用的委托令牌)。轮换时由编排服务经 <see cref="CommandTypes.AccountToken"/> 下发。</summary>
public sealed record AccountTokenGrant(string Token, long ExpiresAt, string AccountId, string? TeamId = null);

/// <summary>
/// 指令的明文(签名之后才解析)。一个平铺类型,<see cref="Type"/> 决定用哪个字段;没用到的字段不出现在线上(省略 null)。
/// <list type="bullet">
/// <item><see cref="DeviceId"/>:指令发给哪台设备——设备拒绝别的设备的指令(同一把编排密钥签的、发给别人的指令不能拿来重放)。</item>
/// <item><see cref="Seq"/>:这台设备的指令序号,严格递增;设备持久化已应用的最大序号,<c>seq ≤ 已应用</c> 一律拒绝(防重放)。</item>
/// <item><see cref="IssuedAt"/> / <see cref="ExpiresAt"/>:Unix 毫秒;过期的指令拒绝(防止休眠很久的设备醒来后应用陈旧指令)。</item>
/// </list>
/// </summary>
public sealed record CommandBody(string Type)
{
    public int V { get; init; } = OrchestrationProtocol.Version;

    public string DeviceId { get; init; } = "";

    public long Seq { get; init; }

    public long IssuedAt { get; init; }

    public long ExpiresAt { get; init; }

    public TrustEntry? Trust { get; init; }

    public string? Fingerprint { get; init; }

    public TrustEntry[]? Trusts { get; init; }

    public QuotaLimits? Quota { get; init; }

    public SleepRequest? Sleep { get; init; }

    public UpgradeRequest? Upgrade { get; init; }

    public AccountTokenGrant? Account { get; init; }
}

/// <summary>
/// 身份绑定(签名后下发给设备,控制端也能经账户 API 拿到同一份):编排服务声明「这台设备(指纹)属于这个账户、经这个中继到达」。
/// 设备据此知道自己属于谁;指纹和设备自己的不一致就拒绝启动(挂错了卷)。
/// </summary>
public sealed record DeviceBinding(string DeviceId, string Fingerprint, string AccountId, string RelayUrl, long IssuedAt, string? TeamId = null);

/// <summary>设备向编排服务打招呼(每次启动 / 唤醒一次;第一次也是身份注册)。</summary>
/// <param name="PublicKey">设备证书公钥(SPKI DER)的 base64。</param>
/// <param name="Signature">设备私钥对 <c>RegisterContext ‖ 0 ‖ deviceId ‖ 0 ‖ issuedAt(十进制)‖ 0 ‖ bootId</c> 的签名(ECDSA P-256 / SHA-256,IEEE P1363,base64)。</param>
/// <param name="BootId">这次启动的随机 id(编排服务据此区分「同一次启动的重试」和「新的一次启动」)。</param>
/// <param name="Resumed">这次启动是不是从休眠里醒来(数据根里有休眠快照)。</param>
/// <param name="LastSeq">设备已应用的最大指令序号(编排服务据此决定重发哪些)。</param>
public sealed record AgentHello(
    int V, string DeviceId, string PublicKey, string Signature, long IssuedAt, string AgentVersion, string BootId, bool Resumed, long LastSeq);

/// <param name="Binding">身份绑定(签名信封,载荷是 <see cref="DeviceBinding"/>)。</param>
/// <param name="Commands">待处理的指令(按序号升序)。</param>
/// <param name="ServerTime">编排服务的时间(Unix 毫秒,只用来诊断时钟偏差)。</param>
public sealed record HelloResponse(SignedEnvelope? Binding, SignedEnvelope[] Commands, long ServerTime);

public sealed record CommandsResponse(SignedEnvelope[] Commands);

/// <summary>指令回执(Agent → 编排服务)。</summary>
public sealed record CommandAck(long Seq, string Status, string? Code = null);

/// <summary>
/// 一批用量(Agent → 编排服务)。<b>增量</b>:自上一批被确认以来的变化。<see cref="BatchId"/> = <c>&lt;deviceId&gt;:&lt;Seq&gt;</c>,
/// 重试时原样重发,编排服务按它去重(幂等);Agent 只有收到成功确认后才推进序号。
/// </summary>
public sealed record UsageBatch(
    int V,
    string BatchId,
    string DeviceId,
    long Seq,
    long From,
    long To,
    long Runs,
    long InputTokens,
    long OutputTokens,
    long CacheTokens,
    long ComputeSeconds,
    long? StorageBytes = null);

/// <summary>生命周期通知的状态。</summary>
public static class LifecycleStates
{
    /// <summary>Agent 已收尾,可以停掉计算资源(编排服务收到后可立即回收沙箱)。</summary>
    public const string Sleeping = "sleeping";

    /// <summary>Agent 已收尾并请求换版本。</summary>
    public const string Upgrading = "upgrading";

    /// <summary>Agent 起来了(含从休眠中醒来)。</summary>
    public const string Awake = "awake";
}

/// <summary>休眠前收尾的结果(给编排服务和唤醒后的控制端看)。</summary>
/// <param name="InterruptedRuns">被休眠打断的 Run 数(只有 <see cref="SleepModes.Now"/> 才可能非零)。</param>
/// <param name="KilledShells">被清理的后台 Shell 数。</param>
/// <param name="QueuedMessages">休眠时仍在排队的消息数(唤醒后会续跑)。</param>
public sealed record SleepSummary(int InterruptedRuns, int KilledShells, int QueuedMessages);

/// <summary>生命周期通知(Agent → 编排服务)。<see cref="WakeAt"/> 是 Agent 希望被唤醒的时间(最近一个定时任务的触发时间减去提前量);null = 只在控制端连接时唤醒。</summary>
public sealed record LifecycleNotice(int V, string DeviceId, string State, string Reason, long At, long? WakeAt = null, SleepSummary? Summary = null);

/// <summary>编排服务对 usage / lifecycle / ack 的回执。<see cref="Duplicate"/> = 已经收过(幂等命中,Agent 当作成功)。</summary>
public sealed record AckResponse(bool Ok, bool Duplicate = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SignedEnvelope))]
[JsonSerializable(typeof(CommandBody))]
[JsonSerializable(typeof(DeviceBinding))]
[JsonSerializable(typeof(AgentHello))]
[JsonSerializable(typeof(HelloResponse))]
[JsonSerializable(typeof(CommandsResponse))]
[JsonSerializable(typeof(CommandAck))]
[JsonSerializable(typeof(UsageBatch))]
[JsonSerializable(typeof(LifecycleNotice))]
[JsonSerializable(typeof(AckResponse))]
public sealed partial class OrchestrationJson : JsonSerializerContext;

/// <summary>编解码助手(JSON 源生成,零反射)。解不出来返回 null,不抛。</summary>
public static class OrchestrationWire
{
    public static byte[] Encode<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) => JsonSerializer.SerializeToUtf8Bytes(value, info);

    public static T? Decode<T>(ReadOnlySpan<byte> utf8, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(utf8, info);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static byte[] Encode(CommandBody body) => Encode(body, OrchestrationJson.Default.CommandBody);

    public static CommandBody? DecodeCommand(ReadOnlySpan<byte> utf8) => Decode(utf8, OrchestrationJson.Default.CommandBody);
}
