using System.Text;
using System.Text.Json;
using Ncw.Agent.Llm.Auth;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Llm.Sse;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Llm.Tests.Support;

/// <summary>可手动拨动的时钟;计时器仍用系统的(空闲超时用真实的短时间)。</summary>
public sealed class FakeClock(long startMs = 1_800_000_000_000) : TimeProvider
{
    private long _ms = startMs;

    public long Ms => Interlocked.Read(ref _ms);

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Ms);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ms, (long)by.TotalMilliseconds);
}

/// <summary>记录每次退避睡眠、并让假时钟前进,不真的睡。</summary>
public sealed class FakeDelayer(FakeClock clock) : IDelayer
{
    private readonly List<TimeSpan> _delays = [];

    public IReadOnlyList<TimeSpan> Delays
    {
        get
        {
            lock (_delays)
            {
                return _delays.ToList();
            }
        }
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_delays)
        {
            _delays.Add(delay);
        }

        clock.Advance(delay);
        return Task.CompletedTask;
    }
}

public sealed class SequentialIds : IIdGenerator
{
    private int _n;

    public string NewId() => $"id{Interlocked.Increment(ref _n):D4}";
}

public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _values = [];

    public List<(string Ref, string Value)> Writes { get; } = [];

    public MemoryCredentialStore Put(string credentialRef, string value)
    {
        _values[credentialRef] = value;
        return this;
    }

    public string? Peek(string credentialRef) => _values.GetValueOrDefault(credentialRef);

    public ValueTask<string?> GetAsync(string credentialRef, CancellationToken ct) => ValueTask.FromResult(_values.GetValueOrDefault(credentialRef));

    public ValueTask SetAsync(string credentialRef, string value, CancellationToken ct)
    {
        _values[credentialRef] = value;
        Writes.Add((credentialRef, value));
        return ValueTask.CompletedTask;
    }
}

public sealed class TestConfig : IProviderConfigSource
{
    public List<ProviderConfig> ProviderList { get; } = [];

    public List<ModelBinding> BindingList { get; } = [];

    public bool Failover { get; set; } = true;

    public IReadOnlyList<ProviderConfig> Providers => ProviderList;

    public IReadOnlyList<ModelBinding> Bindings => BindingList;

    public bool FailoverEnabled => Failover;

    public TestConfig Add(string id, WireProtocol protocol, int priority, string upstreamModel, string alias = "smart", string? baseUrl = null,
        AnthropicCacheTtl ttl = AnthropicCacheTtl.FiveMinutes, bool images = true, Action<ModelBindingBuilder>? configure = null)
    {
        ProviderList.Add(new ProviderConfig(id, id.ToUpperInvariant(), baseUrl ?? $"https://{id}.example.com", protocol, priority, true, $"provider:{id}", ttl));
        var builder = new ModelBindingBuilder(new ModelBinding(alias, id, upstreamModel, 200_000, 32_000, SupportsImages: images));
        configure?.Invoke(builder);
        BindingList.Add(builder.Binding);
        return this;
    }
}

public sealed class ModelBindingBuilder(ModelBinding binding)
{
    public ModelBinding Binding { get; private set; } = binding;

    public ModelBindingBuilder With(Func<ModelBinding, ModelBinding> change)
    {
        Binding = change(Binding);
        return this;
    }
}

public static class Samples
{
    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    public static string Text(string name) => Encoding.UTF8.GetString(Bytes(name));
}

/// <summary>每次 Read 最多返回 N 字节,用来验证解码器对任意分块边界(包括多字节字符中间)的鲁棒性。</summary>
public sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
{
    private int _pos;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var n = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _pos);
        data.AsSpan(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public static class Decoding
{
    public static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var item in source)
        {
            list.Add(item);
        }

        return list;
    }

    public static Task<List<LlmEvent>> DecodeAsync(WireProtocol protocol, byte[] data, int chunkSize = 7) =>
        CollectAsync(WireCodecs.DecodeAsync(protocol, SseReader.ReadAsync(new ChunkedStream(data, chunkSize))));

    public static Task<List<LlmEvent>> DecodeTextAsync(WireProtocol protocol, string sse, int chunkSize = 64) =>
        DecodeAsync(protocol, Encoding.UTF8.GetBytes(sse), chunkSize);

    /// <summary>把若干 JSON 值拼成 <c>data:</c> 帧的 SSE 文本。</summary>
    public static string Frames(params string[] json) => string.Concat(json.Select(j => $"data: {j}\n\n"));

    public static string TypedFrames(params (string Event, string Json)[] frames) =>
        string.Concat(frames.Select(f => $"event: {f.Event}\ndata: {f.Json}\n\n"));

    public static StreamAssembler Assemble(IEnumerable<LlmEvent> events)
    {
        var assembler = new StreamAssembler();
        foreach (var ev in events)
        {
            assembler.Apply(ev);
        }

        return assembler;
    }
}

public static class Msg
{
    private static int _n;

    private static MessageId NextId() => new($"m{Interlocked.Increment(ref _n)}");

    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public static Message User(params Part[] parts) => new(NextId(), Role.User, parts, At);

    public static Message User(string text) => User(new TextPart(text));

    public static Message Assistant(Provenance? source, params Part[] parts) => new(NextId(), Role.Assistant, parts, At, Source: source);

    public static Message Assistant(params Part[] parts) => Assistant(null, parts);

    public static Message ToolResults(params (string CallId, string Content, bool IsError)[] results) =>
        User(results.Select(r => (Part)new ToolResultPart(r.CallId, new ToolOutput(r.Content), r.IsError)).ToArray());

    public static ToolCallPart Call(string id, string name, string json = "{}") => new(id, name, Jsn.Parse(json));

    public static ToolInfo Tool(string name = "Read") =>
        new(name, "Read a file", Jsn.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""));

    public static LlmRequest Request(IReadOnlyList<Message> messages, string system = "You are helpful.", IReadOnlyList<ToolInfo>? tools = null,
        ThinkingLevel thinking = ThinkingLevel.Auto, int maxOutput = 8192) =>
        new(system, messages, tools ?? [], new GenerationOptions(maxOutput, thinking));

    public static LlmRequest Request(string text, string system = "You are helpful.", IReadOnlyList<ToolInfo>? tools = null,
        ThinkingLevel thinking = ThinkingLevel.Auto) =>
        Request([User(text)], system, tools, thinking);

    public static ProtocolOpaque Opaque(string protocol, string json) => new(protocol, Jsn.Parse(json));
}
