using System.Runtime.CompilerServices;
using System.Text;

namespace Ncw.Agent.Llm.Sse;

/// <summary>一个 SSE 事件。<see cref="Event"/> 缺省是 <c>message</c>;多行 data 用 <c>\n</c> 连接。</summary>
public sealed record SseEvent(string Event, string Data, string? Id = null);

/// <summary>
/// 增量 SSE 解析器(对应 2.x <c>upstream/sse.ts</c>)。全部难度都在分块边界上:
/// 去掉流开头的 BOM;缓冲区末尾孤立的 <c>\r</c> 保留到下一块(它可能是被切开的 <c>\r\n</c> 的前半,否则会凭空劈出一个空事件)。
/// </summary>
public sealed class SseParser
{
    private string _buffer = "";
    private readonly List<string> _data = [];
    private string _eventName = "";
    private string? _lastId;
    private bool _sawFirstChunk;

    public List<SseEvent> Feed(string chunk)
    {
        // BOM 只可能出现在流的最开头。不削掉的话第一个字段名会变成 "﻿event",整个第一个事件(message_start)被当成未知字段丢掉。
        if (!_sawFirstChunk)
        {
            _sawFirstChunk = true;
            if (chunk.StartsWith('﻿'))
            {
                chunk = chunk[1..];
            }
        }

        _buffer += chunk;
        var output = new List<SseEvent>();
        var start = 0;
        var i = 0;
        while (i < _buffer.Length)
        {
            var c = _buffer[i];
            if (c is not ('\n' or '\r'))
            {
                i++;
                continue;
            }

            // 缓冲区末尾的孤立 \r 不能当行尾处理。
            if (c == '\r' && i == _buffer.Length - 1)
            {
                break;
            }

            var line = _buffer[start..i];
            i += c == '\r' && i + 1 < _buffer.Length && _buffer[i + 1] == '\n' ? 2 : 1;
            start = i;
            var ev = Line(line);
            if (ev is not null)
            {
                output.Add(ev);
            }
        }

        _buffer = _buffer[start..];
        return output;
    }

    /// <summary>流结束时调用。规范说末尾未以空行结束的数据要丢弃,但上游经常这么干(最后的 <c>data: [DONE]</c> 后不带空行),所以交出来。</summary>
    public List<SseEvent> Flush()
    {
        var output = new List<SseEvent>();
        if (_buffer.Length > 0)
        {
            // 末尾孤立的 \r 在这里就是行尾。
            var ev = Line(_buffer.TrimEnd('\r'));
            _buffer = "";
            if (ev is not null)
            {
                output.Add(ev);
            }
        }

        var tail = Dispatch();
        if (tail is not null)
        {
            output.Add(tail);
        }

        return output;
    }

    private SseEvent? Line(string line)
    {
        if (line.Length == 0)
        {
            return Dispatch();
        }

        if (line[0] == ':')
        {
            return null; // 注释行,上游拿它做心跳
        }

        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' '))
        {
            value = value[1..]; // 冒号后恰好一个空格要去掉,多余的是数据
        }

        switch (field)
        {
            case "event":
                _eventName = value;
                break;
            case "data":
                _data.Add(value);
                break;
            case "id":
                if (!value.Contains('\0'))
                {
                    _lastId = value;
                }

                break;
            // retry 不用:重试策略在路由里。
        }

        return null;
    }

    private SseEvent? Dispatch()
    {
        if (_data.Count == 0)
        {
            _eventName = ""; // 只有 event 没有 data 的事件丢弃,但事件名要重置,否则粘到下一个事件上
            return null;
        }

        var ev = new SseEvent(_eventName.Length == 0 ? "message" : _eventName, string.Join('\n', _data), _lastId);
        _data.Clear();
        _eventName = "";
        return ev;
    }
}

public static class SseReader
{
    /// <summary>
    /// 字节流 → SSE 事件流。UTF-8 解码必须是流式的:一个多字节字符(中文 3 字节)会横跨两个 chunk,
    /// 逐块解码会在接缝处产生 U+FFFD。
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream body, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var parser = new SseParser();
        var bytes = new byte[8192];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length) + 4];
        while (true)
        {
            var read = await body.ReadAsync(bytes, ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
            if (count == 0)
            {
                continue;
            }

            foreach (var ev in parser.Feed(new string(chars, 0, count)))
            {
                yield return ev;
            }
        }

        var tailCount = decoder.GetChars([], 0, 0, chars, 0, flush: true);
        if (tailCount > 0)
        {
            foreach (var ev in parser.Feed(new string(chars, 0, tailCount)))
            {
                yield return ev;
            }
        }

        foreach (var ev in parser.Flush())
        {
            yield return ev;
        }
    }

    /// <summary>
    /// 响应体 → 事件流。有的兼容网关对流式请求直接回一整个 <c>application/json</c>:整体当成一个 <c>message</c> 事件,交给解码器。
    /// </summary>
    public static async IAsyncEnumerable<SseEvent> ReadResponseAsync(
        Stream body, string? contentType, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (contentType is not null && contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(body, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            var text = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
            yield return new SseEvent("message", text);
            yield break;
        }

        await foreach (var ev in ReadAsync(body, ct).ConfigureAwait(false))
        {
            yield return ev;
        }
    }
}
