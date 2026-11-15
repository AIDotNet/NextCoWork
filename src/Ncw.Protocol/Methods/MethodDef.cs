using PolyType;

namespace Ncw.Protocol.Methods;

/// <summary>方法种类(docs/design/09 §8.1)。</summary>
public enum MethodKind
{
    /// <summary>只读,可以安全重试。</summary>
    Query,
    /// <summary>有副作用,必须带幂等键。</summary>
    Command,
}

/// <summary>控制端角色(docs/design/09 §4)。数值越大权限越高。</summary>
public enum Role
{
    /// <summary>远程连上但还没配对:只能调用 <c>pair.*</c>,不能订阅。</summary>
    Unpaired = -1,
    Viewer = 0,
    Member = 1,
    Owner = 2,
}

/// <summary>允许调用的承载。</summary>
[Flags]
public enum Transports
{
    Local = 1,
    Remote = 2,
    Any = Local | Remote,
}

/// <summary>
/// 能力名(docs/design/09 §7、§8.4)。方法表里每个方法声明它属于哪个能力:缺省(null)是协议 v1 的基础能力,设备一定支持;
/// 之后新增的领域各有一个能力名,设备在 <c>welcome.features</c> 里列出自己支持的,控制端据此决定能调哪些方法。
/// </summary>
public static partial class Capabilities
{
    /// <summary>对话链路:<c>sessions.*</c>、<c>runs.*</c>、<c>interactions.*</c>、<c>queue.*</c>、<c>tools.*</c>、<c>context.*</c> 和 <c>run/&lt;id&gt;</c>、<c>session/&lt;id&gt;</c>、<c>interactions</c> 主题。</summary>
    public const string Conversation = "conversation";

    /// <summary>模型目录:<c>catalog.*</c> 和 <c>catalog</c> 主题(读取 viewer 可,写入 owner)。</summary>
    public const string Catalog = "catalog";

    /// <summary>供应商:<c>providers.*</c> 和 <c>providers</c> / <c>providers.oauth</c> 主题(只读 member 可,写入 owner)。</summary>
    public const string Providers = "providers";

    /// <summary>子代理:<c>subagents.*</c>(定义的列表 / 诊断 / 启停、手动汇报);<c>subagent_*</c> 事件在 <c>run/&lt;id&gt;</c> 主题里(能力 <c>conversation</c>)。</summary>
    public const string Subagents = "subagents";

    /// <summary>计划 / 目标:<c>goal.*</c>、<c>runs.executePlan</c> 和 <c>goal</c> 主题(读取 viewer 可,设定 / 清除 / 执行计划 member 起)。</summary>
    public const string Planning = "planning";
}

/// <summary>方法表里的一行:名字、种类、所需角色、所需能力、允许的承载,以及参数 / 结果的类型形状(源生成)。</summary>
public abstract record MethodDef(string Name, MethodKind Kind, Role MinRole, Transports Allowed)
{
    /// <summary>所需能力(<see cref="Capabilities"/>);null = 基础能力。</summary>
    public string? Capability { get; init; }

    /// <summary>参数 / 结果的非泛型类型形状(开放网关据此从方法表导出 OpenAPI,零反射)。</summary>
    public abstract PolyType.ITypeShape ParamsTypeShape { get; }

    public abstract PolyType.ITypeShape ResultTypeShape { get; }
}

public sealed record MethodDef<TParams, TResult>(
    string Name,
    MethodKind Kind,
    Role MinRole,
    Transports Allowed,
    ITypeShape<TParams> ParamsShape,
    ITypeShape<TResult> ResultShape) : MethodDef(Name, Kind, MinRole, Allowed)
{
    public override PolyType.ITypeShape ParamsTypeShape => ParamsShape;

    public override PolyType.ITypeShape ResultTypeShape => ResultShape;
}

/// <summary>主题:快照状态的类型形状、订阅所需角色和允许的承载。增量事件的数据另按事件类型解码。</summary>
public sealed record TopicDef<TState>(string Name, ITypeShape<TState> StateShape, Role MinRole = Role.Viewer, Transports Allowed = Transports.Any);
