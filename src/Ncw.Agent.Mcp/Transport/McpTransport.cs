namespace Ncw.Agent.Mcp;

/// <summary>
/// 一条到 MCP 服务器的消息通道:每条消息是一个完整的 JSON-RPC 消息(UTF-8 JSON)。
/// 对 HTTP 传输来说「请求—响应」是 POST 的往返,但响应仍经 <see cref="Received"/> 回到会话层(和 stdio 一样按 id 关联,所以乱序、夹带通知都不影响)。
/// </summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>收到一条消息(<see cref="StartAsync"/> 之前设好)。回调里抛异常由传输吞掉并记诊断。</summary>
    Func<ReadOnlyMemory<byte>, ValueTask>? Received { get; set; }

    Task StartAsync(CancellationToken ct);

    /// <summary>发一条消息。发不出去抛 <see cref="McpException"/>(带稳定 key)。</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);

    /// <summary>传输关闭时完成:正常关闭(我们自己关 / 对端干净地结束)= 正常完成;异常关闭(进程崩溃、会话过期、流断了)= 以 <see cref="McpException"/> 失败。</summary>
    Task Completion { get; }

    /// <summary>握手完成(<c>initialize</c> 之后)。Streamable HTTP 据此记下协商好的协议版本头并打开服务器事件流。</summary>
    void OnInitialized(string protocolVersion);

    /// <summary>诊断文字(stdio:服务器 stderr 的尾部)。<b>可能含密钥值或路径</b>:出站前过 <see cref="McpScrubber"/>,且不进协议。</summary>
    string? Diagnostics { get; }
}
