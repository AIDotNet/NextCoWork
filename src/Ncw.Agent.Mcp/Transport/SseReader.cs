using System.Runtime.CompilerServices;

namespace Ncw.Agent.Mcp;

/// <summary>一个 SSE 事件(<c>event:</c> / <c>data:</c> 多行用 <c>\n</c> 连接 / <c>id:</c>)。</summary>
public sealed record SseEvent(string? Type, string Data, string? Id);

/// <summary>
/// SSE 解析(WHATWG 规则的子集:LF / CRLF 换行、<c>:</c> 注释行、<c>data</c> 多行、<c>event</c> / <c>id</c> / <c>retry</c> 字段,空行派发)。
/// 一个事件的字节数有上限(<c>maxEventBytes</c>),超出就以 <see cref="McpErrorKeys.Protocol"/> 失败——不替服务器无限缓冲。
/// </summary>
public static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, int maxEventBytes, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var chunk = new byte[8192];
        var line = new List<byte>(256);
        var data = new List<string>();
        string? type = null;
        string? id = null;
        var eventBytes = 0;
        var sawCr = false;
        for (;;)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                yield break; // 流结束:没有空行收尾的残余事件按规范丢弃
            }

            for (var i = 0; i < read; i++)
            {
                var b = chunk[i];
                if (sawCr)
                {
                    sawCr = false;
                    if (b == (byte)'\n')
                    {
                        continue; // CRLF 的 LF 已经按 CR 处理过
                    }
                }

                if (b is (byte)'\n' or (byte)'\r')
                {
                    sawCr = b == (byte)'\r';
                    if (line.Count == 0)
                    {
                        if (data.Count > 0 || type is not null)
                        {
                            yield return new SseEvent(type, string.Join('\n', data), id);
                        }

                        data.Clear();
                        type = null;
                        eventBytes = 0;
                        continue;
                    }

                    var text = System.Text.Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(line));
                    line.Clear();
                    if (text[0] == ':')
                    {
                        continue;
                    }

                    var colon = text.IndexOf(':');
                    var field = colon < 0 ? text : text[..colon];
                    var value = colon < 0 ? "" : text[(colon + 1)..];
                    if (value.StartsWith(' '))
                    {
                        value = value[1..];
                    }

                    switch (field)
                    {
                        case "data":
                            data.Add(value);
                            break;
                        case "event":
                            type = value;
                            break;
                        case "id":
                            if (!value.Contains('\0'))
                            {
                                id = value;
                            }

                            break;
                    }

                    continue;
                }

                line.Add(b);
                if (++eventBytes > maxEventBytes)
                {
                    throw McpException.With(McpErrorKeys.Protocol, "An SSE event from the MCP server exceeded the size limit.", ("reason", "eventTooLarge"));
                }
            }
        }
    }
}
