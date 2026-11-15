namespace Ncw.Agent.Mcp;

/// <summary>一台服务器的连接状态(三态 + 禁用)。</summary>
public enum McpState
{
    Disabled,

    /// <summary>正在连接 / 等待重连。</summary>
    Connecting,

    Connected,

    /// <summary>连接失败(<see cref="McpServerStatus.ErrorKey"/> 说明原因);退避重连用尽或还在等下一次重试。</summary>
    Failed,
}

/// <summary>
/// 列表行要显示的一切 = 配置 + 运行时状态。合成一个而不是让界面拿两份自己 join:两份会各自到达,中间那一帧必然有一边是旧的
/// (表现是「刚加的服务器显示成未连接,一秒后才跳成已连接」)。<b>不含任何密钥值,也不含服务器给的错误原文</b>(只有稳定 key 和结构化参数)。
/// </summary>
/// <param name="Tools">已接入的工具名(服务器的原名)。</param>
/// <param name="Rejected">没有接入的工具及原因——即使连接成功也照实显示(不写的话用户看到「已连接 · 3 个工具」而服务器文档写着 5 个,差在哪里没有任何地方说得出来)。</param>
/// <param name="Attempt">当前连续失败的次数(0 = 没有失败)。</param>
public sealed record McpServerStatus(
    McpServerConfig Config,
    McpState State,
    string? ErrorKey,
    IReadOnlyDictionary<string, string> ErrorParams,
    IReadOnlyList<string> Tools,
    IReadOnlyList<McpRejectedTool> Rejected,
    DateTimeOffset? ConnectedAt,
    int Attempt,
    string? ServerName,
    string? ServerVersion);

/// <summary>全量状态 + 单调版本号(<c>mcp</c> 主题:首帧和增量是同一个类型,AGENTS.md §3 规则 5)。</summary>
public sealed record McpSnapshot(long Rev, IReadOnlyList<McpServerStatus> Servers)
{
    public static McpSnapshot Empty { get; } = new(0, []);
}

/// <summary>「测试连接」的结果。<see cref="Detail"/> 是脱敏后的英文诊断(含服务器 stderr 的尾部),只给 owner 看,不进主题。</summary>
public sealed record McpTestResult(
    bool Ok,
    IReadOnlyList<string> Tools,
    TimeSpan Elapsed,
    string? ErrorKey,
    IReadOnlyDictionary<string, string> ErrorParams,
    string? Detail,
    string? ServerName,
    string? ServerVersion,
    string? ProtocolVersion,
    IReadOnlyList<McpRejectedTool> Rejected);

/// <summary>某个键「有没有值」——<b>只有键名和是否已设置,绝不回值</b>。</summary>
public sealed record McpSecretState(string Name, bool IsSet);

public sealed record McpSecretsInfo(string Id, string Kind, IReadOnlyList<McpSecretState> Entries);
