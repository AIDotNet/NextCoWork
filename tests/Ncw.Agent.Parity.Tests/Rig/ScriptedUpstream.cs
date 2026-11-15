using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ncw.Agent.Llm.Auth;

namespace Ncw.Agent.Parity.Tests.Rig;

/// <summary>
/// 回放用的假上游:<see cref="IUpstreamHttp"/> 的实现,按录制里的 <c>upstream</c> 数组顺序应答每一次 HTTP 请求。
/// 响应体是 2.x 录制的 SSE 原文,<b>一个录制块 = 一次读取</b>:一个块被解码管线整条消费完、下一次读取到来的那一刻,触发「第 k 个请求第 i 个块之后」的用户操作
/// (录制侧用 <c>highWaterMark: 0</c> 的 ReadableStream 做到同一个时机)。
/// </summary>
public sealed class ScriptedUpstream(IReadOnlyList<JsonObject> script, Action<int, int> chunkConsumed) : IUpstreamHttp
{
    private readonly object _gate = new();
    private int _next;

    public List<(Uri Url, JsonNode? Body)> Requests { get; } = [];

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        int index;
        lock (_gate)
        {
            index = _next++;
            Requests.Add((request.RequestUri!, body));
        }

        if (index >= script.Count)
        {
            throw new InvalidOperationException($"The recording has no scripted response for upstream request #{index + 1}.");
        }

        var item = script[index];
        switch (item["kind"]!.GetValue<string>())
        {
            case "network-error":
                throw new HttpRequestException(item["message"]?.GetValue<string>() ?? "fetch failed");
            case "http":
            {
                var response = new HttpResponseMessage((HttpStatusCode)item["status"]!.GetValue<int>())
                {
                    Content = new StringContent(item["body"]!.GetValue<string>(), Encoding.UTF8, item["contentType"]?.GetValue<string>() ?? "application/json"),
                };
                foreach (var (name, value) in item["headers"]?.AsObject() ?? [])
                {
                    response.Headers.TryAddWithoutValidation(name, value!.GetValue<string>());
                }

                return response;
            }

            default:
            {
                var chunks = item["chunks"]!.AsArray().Select(c => Encoding.UTF8.GetBytes(c!.GetValue<string>())).ToList();
                var content = new StreamContent(new ChunkStream(chunks, item["then"]?.GetValue<string>() ?? "end", i => chunkConsumed(index, i)));
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
                return new HttpResponseMessage((HttpStatusCode)(item["status"]?.GetValue<int>() ?? 200)) { Content = content };
            }
        }
    }

    private sealed class ChunkStream(List<byte[]> chunks, string then, Action<int> consumed) : Stream
    {
        private int _chunk = -1;
        private int _offset;
        private int _notified = -1;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_chunk >= 0 && _chunk < chunks.Count && _offset < chunks[_chunk].Length)
            {
                return Deliver(buffer);
            }

            // 上一个块已经整条交出去,现在是「下一次读取」:它之前的块被管线完整消费了。
            if (_chunk >= 0 && _notified < _chunk)
            {
                _notified = _chunk;
                consumed(_chunk);
            }

            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            _chunk++;
            _offset = 0;
            if (_chunk < chunks.Count)
            {
                return Deliver(buffer);
            }

            switch (then)
            {
                case "fail":
                    throw new IOException("The upstream connection was terminated.");
                case "stall":
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                    return 0;
                default:
                    return 0;
            }
        }

        private int Deliver(Memory<byte> buffer)
        {
            var data = chunks[_chunk];
            var n = Math.Min(buffer.Length, data.Length - _offset);
            data.AsMemory(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
