using System.Text.RegularExpressions;
using System.Text.Json;
using Ncw.Core.Ids;

namespace Ncw.Agent.Llm;

/// <summary>统一入参 <c>off / minimal / low / medium / high / xhigh / max</c>(01-llm §6),<c>Auto</c> = 交给模型自己决定(2.x 的缺省档)。</summary>
public enum ThinkingLevel
{
    Auto,
    Off,
    Minimal,
    Low,
    Medium,
    High,
    XHigh,
    Max,
}

/// <summary>工具名只用 externalName 上线,必须匹配 <c>^[a-zA-Z0-9_-]{1,64}$</c>,同一会话内稳定。</summary>
public sealed record ToolInfo(string ExternalName, string Description, JsonElement InputSchema)
{
    public static bool IsValidName(string name) => ToolNames.IsMatch(name);
}

internal static partial class ToolNames
{
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex Pattern();

    public static bool IsMatch(string name) => Pattern().IsMatch(name);
}

public sealed record GenerationOptions(
    int MaxOutputTokens,
    ThinkingLevel Thinking = ThinkingLevel.Auto,
    double? Temperature = null,
    IReadOnlyList<string>? StopSequences = null);

public sealed record LlmRequest(
    string System,
    IReadOnlyList<Message> Messages,
    IReadOnlyList<ToolInfo> Tools,
    GenerationOptions Options);

/// <summary>要哪个模型。<see cref="ProviderId"/> 非空 = 用户显式固定了供应商(硬约束,不跨家兜底)。<see cref="Alias"/> 是别名,不是上游真实模型名。</summary>
public sealed record ModelRef(string? ProviderId, string Alias)
{
    public static ModelRef Unpinned(string alias) => new(null, alias);
}

/// <summary>一次逻辑请求的运行上下文:Anthropic 的 metadata.user_id 取工作区 id,RunId 在重试和换供应商之间保持稳定。</summary>
public sealed record RequestContext(string WorkspaceId, string? RunId = null, string? SessionId = null);

public interface IIdGenerator
{
    string NewId();
}

/// <summary>ULID,和 2.x 记录混排。</summary>
public sealed class UlidIdGenerator(TimeProvider? clock = null) : IIdGenerator
{
    public string NewId() => Ulid.New(clock);
}
