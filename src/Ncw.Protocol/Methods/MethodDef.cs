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

/// <summary>方法表里的一行:名字、种类、所需角色、允许的承载,以及参数 / 结果的类型形状(源生成)。</summary>
public abstract record MethodDef(string Name, MethodKind Kind, Role MinRole, Transports Allowed);

public sealed record MethodDef<TParams, TResult>(
    string Name,
    MethodKind Kind,
    Role MinRole,
    Transports Allowed,
    ITypeShape<TParams> ParamsShape,
    ITypeShape<TResult> ResultShape) : MethodDef(Name, Kind, MinRole, Allowed);

/// <summary>主题:快照状态的类型形状。增量事件的数据另按事件类型解码。</summary>
public sealed record TopicDef<TState>(string Name, ITypeShape<TState> StateShape);
