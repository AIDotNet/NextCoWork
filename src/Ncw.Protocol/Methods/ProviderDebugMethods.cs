using PolyType;

namespace Ncw.Protocol.Methods;

// 供应商调试(docs/design/07「供应商调试」、09 §8.4)。后加的能力 `providers.debug`:对一个调试对象(已保存的供应商,和 / 或「添加 / 编辑供应商」表单里的草稿)
// 用选定的协议和模型发一条真实的流式请求,把请求、响应、每个 SSE 事件和解码结果原样带回来。
// ★ 凭据:草稿 key 只出现在请求参数里(方向控制端 → Agent,和 providers.credential.set 一样),只用于这一次请求、不落盘;
// 结果和进度里请求头的 key 一律是 `••••后四位`,正文 / 响应 / 事件 / 说明里出现的 key 抹成 `[redacted]`。

/// <summary>
/// 调试对象。<see cref="ProviderId"/> 给了 = 已保存的供应商(地址、协议、凭据默认取它的);草稿字段给了就压过已保存的。
/// 没有 <see cref="ProviderId"/> 时 <see cref="BaseUrl"/> 必填(新建、还没保存的供应商)。
/// </summary>
/// <param name="Protocol">草稿的协议(见 <c>ProviderDto.Protocol</c>);决定拉模型列表时先试哪一族以及媒体协议类型。</param>
/// <param name="ApiKey">草稿里刚填的 key;空 = 用已存的那把(有的话)。</param>
/// <param name="Kind">草稿的供应商类型(<c>text</c> / <c>image</c> / <c>video</c> / <c>audio</c>);缺省时由协议或已保存供应商推断。</param>
public sealed record ProviderDebugTargetDto(string? ProviderId, string? BaseUrl = null, string? Protocol = null, string? ApiKey = null, string? Kind = null);

public sealed record ProviderDebugModelsParams(ProviderDebugTargetDto Target);

/// <summary>一族列表端点的一次尝试。</summary>
/// <param name="Family"><c>anthropic</c>(<c>/v1/models</c> + <c>x-api-key</c>)或 <c>openai</c>(<c>/models</c> + Bearer)。</param>
/// <param name="ErrorKey">失败的 <c>providers.*</c> key(<c>fetchRejected</c> / <c>fetchNotFound</c> / <c>fetchFailed</c> / <c>fetchTimeout</c> / <c>fetchEmpty</c>);成功为 null。</param>
/// <param name="Detail">失败的一句话说明(已脱敏)。</param>
public sealed record DebugListAttemptDto(string Family, string Url, bool Ok, int? Status, int Count, string? ErrorKey, string? Detail);

/// <param name="Families">哪几族的列表端点报了它(<c>anthropic</c> / <c>openai</c>)。</param>
public sealed record DebugModelDto(string Id, string? DisplayName, string[] Families);

/// <summary>调试弹窗的模型候选:每一族的尝试 + 合并去重后的模型(先这家协议那一族)。都拉不到时模型为空(不是错误)。</summary>
public sealed record ProviderDebugModelsResult(DebugListAttemptDto[] Attempts, DebugModelDto[] Models);

public static class DebugThinking
{
    public const string Auto = "auto";
    public const string Off = "off";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    public static readonly IReadOnlyList<string> All = [Auto, Off, Low, Medium, High];
}

/// <summary><c>providers.debug.run</c> 的参数。</summary>
/// <param name="RunId">控制端生成的 id;进度在 <c>providers.debug/&lt;RunId&gt;</c> 主题上(先订阅再调用)。</param>
/// <param name="Protocol">这一次用哪种协议(和供应商自己的协议无关)。</param>
/// <param name="Model">上游模型名,或这家已有的别名(那就用别名的思考配置和请求补丁)。</param>
/// <param name="Thinking"><see cref="DebugThinking"/> 之一。</param>
/// <param name="WithTool">带一个无参数的工具定义(只验证端点接不接受工具)。</param>
public sealed record ProviderDebugRunParams(
    string RunId,
    ProviderDebugTargetDto Target,
    string Protocol,
    string Model,
    string Prompt,
    int MaxOutputTokens,
    string Thinking,
    bool WithTool);

public static class DebugEventKinds
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Sse = "sse";
    public const string Text = "text";
    public const string Thinking = "thinking";
    public const string ToolCall = "toolCall";
    public const string End = "end";
    public const string Error = "error";
}

/// <summary>调试记录里的一条事件。</summary>
/// <param name="ElapsedMs">相对请求开始的毫秒。</param>
/// <param name="Kind"><see cref="DebugEventKinds"/> 之一(开放的字符串,控制端认不出就原样显示)。</param>
/// <param name="Name">请求 = URL;响应 = 状态码;SSE = 事件名;工具调用 = 工具名;结束 = 停止原因;错误 = 失败类别。</param>
/// <param name="Data">已脱敏,可能被截断。</param>
public sealed record ProviderDebugEventDto(int Seq, int ElapsedMs, string Kind, string? Name, string Data);

public sealed record DebugHeaderDto(string Name, string Value);

public sealed record DebugUsageDto(long Input, long Output, long? CacheRead, long? CacheWrite, long? Reasoning);

public static class DebugFailures
{
    /// <summary>401 / 403。</summary>
    public const string Rejected = "rejected";

    /// <summary>404(地址或协议不对)。</summary>
    public const string NotFound = "notFound";

    /// <summary>其它 HTTP 状态。</summary>
    public const string Http = "http";

    public const string Network = "network";
    public const string Timeout = "timeout";

    /// <summary>流没收完就断了。</summary>
    public const string Incomplete = "incomplete";

    /// <summary>200 之后流里报了错。</summary>
    public const string Stream = "stream";

    /// <summary>请求在设备上就组不出来(请求补丁、思考参数、签名)。</summary>
    public const string Local = "local";
}

/// <summary>
/// 一次调试的完整记录(<c>providers.debug.run</c> 的返回值,权威结果)。所有文本都已脱敏。
/// <see cref="Ok"/> = HTTP 2xx 且收到了完整的一条消息;否则 <see cref="Failure"/> 是 <see cref="DebugFailures"/> 之一,<see cref="Detail"/> 是说明。
/// </summary>
/// <param name="Url">实际请求的 URL(签名式鉴权会带 query)。</param>
/// <param name="Status">没连上为 null。</param>
/// <param name="ResponseBody">非 2xx 时的响应正文(已截断);成功时为 null(内容在事件里)。</param>
/// <param name="ErrorCode">上游错误归一化后的类别(<c>auth</c> / <c>rateLimit</c> / <c>contextLength</c> / <c>network</c> / <c>provider</c>……)。</param>
public sealed record ProviderDebugResultDto(
    string RunId,
    bool Ok,
    string Protocol,
    string Model,
    string Method,
    string Url,
    DebugHeaderDto[] RequestHeaders,
    string RequestBody,
    int? Status,
    DebugHeaderDto[] ResponseHeaders,
    string? ResponseBody,
    ProviderDebugEventDto[] Events,
    bool EventsTruncated,
    string Text,
    string Thinking,
    string[] ToolCalls,
    string? StopReason,
    DebugUsageDto? Usage,
    string? ResponseModel,
    int? FirstTokenMs,
    int? TotalMs,
    string? Failure,
    string? ErrorCode,
    string? Detail);

/// <summary><c>providers.debug/&lt;runId&gt;</c> 的快照:到此刻为止的全部事件。没开始 / 已回收的 runId 是空快照(所以可以先订阅再发起)。</summary>
public sealed record ProviderDebugTopicState(string RunId, bool Running, ProviderDebugEventDto[] Events);

public static class ProviderDebugEvents
{
    /// <summary>一条新事件。数据是 <see cref="ProviderDebugEventDto"/>。</summary>
    public const string Event = "providers.debug.event";
}

/// <summary>供应商调试的方法表(能力 <see cref="Capabilities.ProviderDebug"/>,承载 本机 + 远程,只有 owner:它用这家的凭据真的发请求)。</summary>
public static class ProviderDebugMethods
{
    /// <summary>调试弹窗的模型候选(两族列表端点都试)。</summary>
    public static readonly MethodDef<ProviderDebugModelsParams, ProviderDebugModelsResult> Models =
        new("providers.debug.models", MethodKind.Query, Role.Owner, Transports.Any, PayloadShapes.GetShape<ProviderDebugModelsParams>(), PayloadShapes.GetShape<ProviderDebugModelsResult>())
        {
            Capability = Capabilities.ProviderDebug,
        };

    /// <summary>发一条调试请求,跑完才返回完整记录;取消这次调用 = 掐断上游连接。</summary>
    public static readonly MethodDef<ProviderDebugRunParams, ProviderDebugResultDto> Run =
        new("providers.debug.run", MethodKind.Command, Role.Owner, Transports.Any, PayloadShapes.GetShape<ProviderDebugRunParams>(), PayloadShapes.GetShape<ProviderDebugResultDto>())
        {
            Capability = Capabilities.ProviderDebug,
        };

    public static readonly IReadOnlyList<MethodDef> All = [Models, Run];

    /// <summary><c>providers.debug/&lt;runId&gt;</c> 的前缀。</summary>
    public const string TopicPrefix = "providers.debug/";

    /// <summary>一次调试的进度主题(owner)。</summary>
    public static TopicDef<ProviderDebugTopicState> Topic(string runId) => new(TopicPrefix + runId, PayloadShapes.GetShape<ProviderDebugTopicState>(), Role.Owner);
}

public static partial class Capabilities
{
    /// <summary>供应商调试:<c>providers.debug.*</c>(后加的能力,设备不报:「测试」退回 <c>providers.test</c>)。</summary>
    public const string ProviderDebug = "providers.debug";
}

[GenerateShapeFor<ProviderDebugTargetDto>]
[GenerateShapeFor<ProviderDebugModelsParams>]
[GenerateShapeFor<DebugListAttemptDto>]
[GenerateShapeFor<DebugModelDto>]
[GenerateShapeFor<ProviderDebugModelsResult>]
[GenerateShapeFor<ProviderDebugRunParams>]
[GenerateShapeFor<ProviderDebugEventDto>]
[GenerateShapeFor<DebugHeaderDto>]
[GenerateShapeFor<DebugUsageDto>]
[GenerateShapeFor<ProviderDebugResultDto>]
[GenerateShapeFor<ProviderDebugTopicState>]
public sealed partial class PayloadShapes;
