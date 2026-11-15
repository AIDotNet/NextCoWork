using Ncw.Agent.Environments;

namespace Ncw.Agent.Mcp;

/// <summary>服务器配置的存放处(沿用 2.x 的库里 <c>mcp_servers</c> 表;Host 实现)。读不懂的行跳过,不抛。</summary>
public interface IMcpConfigStore
{
    IReadOnlyList<McpServerConfig> List();

    McpServerConfig? Get(string id);

    void Put(McpServerConfig config);

    bool Remove(string id);
}

/// <summary>密钥库(NCK1;Host 实现)。<b>明文只经它进出,绝不进任何 DTO / 事件 / 日志。</b>读不出来(主密钥丢了)抛异常。</summary>
public interface IMcpSecretStore
{
    string? Get(string reference);

    void Set(string reference, string value);

    bool Remove(string reference);
}

/// <summary>一台服务器在哪里跑:执行环境 + 根目录(项目绑定的服务器是项目根;全局的是 null)。</summary>
public sealed record McpRuntimeTarget(IExecutionEnvironment Environment, EnvPath? Root);

/// <summary>服务器配置 → 运行环境。项目已不存在 / 环境不可用抛 <see cref="McpException"/>(<see cref="McpErrorKeys.ProjectMissing"/> / <see cref="McpErrorKeys.EnvironmentUnavailable"/>)。</summary>
public interface IMcpEnvironments
{
    ValueTask<McpRuntimeTarget> ResolveAsync(McpServerConfig config, CancellationToken ct);
}

/// <summary>时间 / 体积 / 重连的参数(测试里调小)。</summary>
public sealed class McpOptions
{
    /// <summary>连接 + 初始化握手的墙钟预算。卡住的服务器不该让设置页一直转圈。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary><c>tools/list</c> 的预算(握手过了但列不出工具,也是一次失败)。</summary>
    public TimeSpan ListToolsTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 一次工具调用的墙钟上限。比连接的 30 秒宽——工具本来就可能慢(跑测试、建 PR);「慢」和「死」之间那条线是 60 秒(2.x ★)。
    /// </summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>失败 / 断线后的重连退避。用尽仍失败就停在 failed,等手动重连。</summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
        [.. new[] { 1, 2, 4, 8, 16, 32, 60, 60 }.Select(s => TimeSpan.FromSeconds(s))];

    /// <summary>一台服务器最多接入多少个工具(超出的拒绝并记原因)。</summary>
    public int MaxToolsPerServer { get; init; } = 500;

    /// <summary><c>tools/list</c> 最多翻几页(防止无限分页)。</summary>
    public int MaxListPages { get; init; } = 50;

    /// <summary>一个工具的 inputSchema 的最大字节数。</summary>
    public int MaxSchemaBytes { get; init; } = 64 * 1024;

    /// <summary>一次工具结果交给模型的文字上限(字符)。循环层另有 64K 的统一截断,这里留在它之下。</summary>
    public int MaxResultChars { get; init; } = 60_000;

    /// <summary>传输层一条消息(一行 / 一个 SSE 事件 / 一个 JSON 响应体)的最大字节数。</summary>
    public int MaxMessageBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>服务器 stderr 保留的尾部字节数(诊断用)。</summary>
    public int StderrTailBytes { get; init; } = 4 * 1024;

    /// <summary>关 stdin 之后留给服务器自行退出的时间,超过就杀进程树。</summary>
    public TimeSpan GracefulExit { get; init; } = TimeSpan.FromMilliseconds(500);
}
