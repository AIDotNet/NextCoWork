using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>主题的三种形态(固定 / 前缀 / 来源)、发送缓冲上限、握手里的能力声明、错误里的 details。真实的本机承载。</summary>
public sealed class TopicTests : IAsyncLifetime
{
    private const string Token = "local-token";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwt-{Guid.NewGuid():N}"[..16]);
    private readonly CancellationTokenSource _cts = new();
    private readonly MethodRouter _router = new();
    private readonly TopicHub _topics = new();
    private readonly FakeSource _source = new();
    private string _endpoint = "";
    private Task _server = Task.CompletedTask;
    private GatedStream? _gated;

    /// <summary>服务端写出的字节可以被拦住,用来造出一个「慢消费者」。</summary>
    private sealed class GatedStream(Stream inner) : Stream
    {
        private volatile TaskCompletionSource? _gate;

        public void Block() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate?.TrySetResult();

        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 来源主题:<c>src/&lt;name&gt;</c>。不带游标给快照(seq = 10);游标 ≥ 10 只补发之后的(10 + n 条);之后实时帧由测试调 <see cref="Push"/> 发出。
    /// </summary>
    private sealed class FakeSource : ITopicSource
    {
        private ITopicWriter? _writer;

        public string Prefix => "src/";

        public Role MinRole => Role.Member;

        public Transports Allowed => Transports.Any;

        public int Opened { get; private set; }

        public int Disposed { get; private set; }

        public IDisposable? Open(string topic, long? cursor, ITopicWriter writer)
        {
            if (topic == "src/missing")
            {
                return null;
            }

            if (topic == "src/boom")
            {
                throw new ProtocolException(ErrorCodes.Unavailable, "not ready", retryable: true);
            }

            Opened++;
            _writer = writer;
            if (cursor is { } c)
            {
                writer.Write(new EventFrame(topic, c + 1, "replayed", [1]));
            }
            else
            {
                writer.Write(new SnapshotFrame(topic, 10, FrameCodec.EncodePayload(new InteractionsDto(10, []), PayloadShapes.GetShape<InteractionsDto>())));
            }

            return new Disposer(this);
        }

        public bool Push(string topic, long seq, int bytes = 8) => _writer!.TryWrite(new EventFrame(topic, seq, "live", new byte[bytes]));

        private sealed class Disposer(FakeSource source) : IDisposable
        {
            public void Dispose() => source.Disposed++;
        }
    }

    public ValueTask InitializeAsync()
    {
        _endpoint = LocalTransport.DefaultEndpoint(_root);
        _router.Handle(ConversationMethods.SessionsRename, (p, _, _) => throw new ProtocolException(
            ErrorCodes.Conflict, "changed", false, FrameCodec.EncodePayload(new InteractionConflictDto("staleVersion", null, 3), PayloadShapes.GetShape<InteractionConflictDto>())));
        _router.Handle(ConversationMethods.ToolsList, (_, _, _) => ValueTask.FromResult(new ToolsListResult([])));
        _router.Handle(DeviceMethods.ProjectsList, (_, _, _) => ValueTask.FromResult(new ProjectsListResult([], 0)));
        _topics.Register(ConversationMethods.InteractionsTopic, () => new InteractionsDto(0, []));
        _topics.RegisterPrefix("thing/", PayloadShapes.GetShape<InteractionsDto>(), name => name == "thing/a" ? new InteractionsDto(1, []) : null);
        _topics.RegisterSource(_source);

        var options = new ServerOptions("dev-1", "3.0.0-test", Transports.Local,
            (hello, _) => hello.LocalToken == Token ? Role.Owner : hello.LocalToken == "viewer" ? Role.Viewer : null);
        _server = Task.Run(async () =>
        {
            await foreach (var stream in LocalTransport.ListenAsync(_endpoint, _cts.Token))
            {
                _gated = new GatedStream(stream);
                _ = new ServerSession(new FrameStream(_gated), _router, _topics, options).RunAsync(_cts.Token);
            }
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _gated?.Release();
        await _cts.CancelAsync();
        try { await _server; } catch (OperationCanceledException) { }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<DeviceClient> Connect(string token = Token)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var stream = await LocalTransport.ConnectAsync(_endpoint, _cts.Token);
                return await DeviceClient.ConnectAsync(stream, new ClientHello("test-client", "3.0.0", LocalToken: token), _cts.Token);
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException && attempt < 50)
            {
                await Task.Delay(50);
            }
        }
    }

    private static TopicDef<InteractionsDto> Def(string name) => new(name, PayloadShapes.GetShape<InteractionsDto>());

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, $"Timed out: {what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Welcome_lists_the_capabilities_of_the_registered_methods()
    {
        await using var client = await Connect();

        // 注册了对话方法 → welcome 里有 conversation;projects.list 是基础能力(没有能力名),不出现
        Assert.Equal([Capabilities.Conversation], client.Welcome.Features);
    }

    [Fact]
    public async Task A_methods_conflict_details_reach_the_client_intact()
    {
        await using var client = await Connect();

        var e = await Assert.ThrowsAsync<ProtocolException>(() => client.CallAsync(ConversationMethods.SessionsRename, new SessionsRenameParams("s", "t")));

        Assert.Equal(ErrorCodes.Conflict, e.Code);
        var details = FrameCodec.DecodePayload(e.Details!, PayloadShapes.GetShape<InteractionConflictDto>());
        Assert.Equal(("staleVersion", 3L), (details.Reason, details.CurrentVersion));
    }

    [Fact]
    public async Task Prefix_topics_exist_only_while_subscribed_publishing_to_nobody_is_harmless_and_a_new_incarnation_never_matches_an_old_cursor()
    {
        await using var client = await Connect();
        long firstSeq = -1;
        var events = new List<EventFrame>();
        var sub = await client.SubscribeAsync(Def("thing/a"), (_, seq) => firstSeq = seq, e => { lock (events) events.Add(e); });
        await Until(() => firstSeq >= 0, "snapshot");

        _topics.Publish("thing/a", "x", new InteractionsDto(2, []), PayloadShapes.GetShape<InteractionsDto>());
        await Until(() => { lock (events) return events.Count == 1; }, "event");
        Assert.Equal(firstSeq + 1, events[0].Seq);

        var unknown = await Assert.ThrowsAsync<ProtocolException>(() => client.SubscribeAsync(Def("thing/zzz"), (_, _) => { }, _ => { }));
        Assert.Equal(ErrorCodes.NotFound, unknown.Code);

        // 退订后主题不存在:发布是空操作(不抛);固定主题没注册才是编程错误
        await sub.DisposeAsync();
        await Until(() => !_topics.HasSubscribers("thing/a"), "unsubscribed");
        _topics.Publish("thing/a", "x", new InteractionsDto(3, []), PayloadShapes.GetShape<InteractionsDto>());
        Assert.Throws<KeyNotFoundException>(() => _topics.Publish("nope", "x", new InteractionsDto(3, []), PayloadShapes.GetShape<InteractionsDto>()));

        // 再订阅:新的主题实例,序号基数更大,所以拿着旧游标来也只会得到快照,不会被误认为续得上
        long secondSnapshotSeq = -1;
        await using var again = await client.SubscribeAsync(Def("thing/a"), (_, seq) => secondSnapshotSeq = seq, _ => { }, cursor: firstSeq + 1);
        await Until(() => secondSnapshotSeq >= 0, "fresh snapshot instead of a replay");
        Assert.True(secondSnapshotSeq > firstSeq + 1);
    }

    [Fact]
    public async Task A_source_topic_writes_its_own_initial_frames_then_live_ones_resumes_by_its_own_cursor_and_is_closed_on_unsubscribe()
    {
        await using var client = await Connect();
        var frames = new List<string>();
        var sub = await client.SubscribeAsync(Def("src/a"), (_, seq) => { lock (frames) frames.Add($"snapshot:{seq}"); }, e => { lock (frames) frames.Add($"{e.Type}:{e.Seq}"); });
        await Until(() => { lock (frames) return frames.Count == 1; }, "snapshot");
        Assert.True(_source.Push("src/a", 11));
        Assert.True(_source.Push("src/a", 12));
        await Until(() => { lock (frames) return frames.Count == 3; }, "live frames");
        Assert.Equal(["snapshot:10", "live:11", "live:12"], frames);

        await sub.DisposeAsync();
        await Until(() => _source.Disposed == 1, "source disposed on unsubscribe");

        // 带游标重订阅:来源只补发之后的帧(没有快照),客户端的序号去重从游标起算
        frames.Clear();
        await using var resumed = await client.SubscribeAsync(Def("src/a"), (_, seq) => { lock (frames) frames.Add($"snapshot:{seq}"); }, e => { lock (frames) frames.Add($"{e.Type}:{e.Seq}"); }, cursor: 12);
        await Until(() => { lock (frames) return frames.Count == 1; }, "replay");
        Assert.Equal(["replayed:13"], frames);
    }

    [Fact]
    public async Task A_source_can_refuse_and_roles_and_errors_are_enforced_before_it_opens()
    {
        await using var viewer = await Connect("viewer");
        await using var owner = await Connect();

        Assert.Equal(ErrorCodes.Forbidden, (await Assert.ThrowsAsync<ProtocolException>(() => viewer.SubscribeAsync(Def("src/a"), (_, _) => { }, _ => { }))).Code);
        Assert.Equal(0, _source.Opened); // 角色不够:来源根本没被打开
        Assert.Equal(ErrorCodes.NotFound, (await Assert.ThrowsAsync<ProtocolException>(() => owner.SubscribeAsync(Def("src/missing"), (_, _) => { }, _ => { }))).Code);
        var boom = await Assert.ThrowsAsync<ProtocolException>(() => owner.SubscribeAsync(Def("src/boom"), (_, _) => { }, _ => { }));
        Assert.Equal((ErrorCodes.Unavailable, true), (boom.Code, boom.Retryable)); // 来源的失败带着可重试标志原样给到控制端
    }

    [Fact]
    public async Task A_slow_connection_makes_live_writes_refuse_once_the_send_buffer_is_full_but_initial_frames_still_go_out()
    {
        await using var client = await Connect();
        var received = 0;
        await using var sub = await client.SubscribeAsync(Def("src/a"), (_, _) => { }, _ => Interlocked.Increment(ref received));
        await Until(() => true, "subscribed");

        _gated!.Block(); // 对端「不读」:服务端写出去的字节被拦住,发送缓冲只增不减
        var accepted = 0;
        var seq = 11;
        while (_source.Push("src/a", seq++, bytes: 1024 * 1024))
        {
            accepted++;
            Assert.True(accepted < 64, "the buffer limit never kicked in");
        }

        // 8 MiB 上限:大约 8 帧(1 MiB 一帧)之后拒绝;调用方据此丢增量或发 resync
        Assert.InRange(accepted, 7, 10);
        _gated.Release();
        await Until(() => Volatile.Read(ref received) >= accepted, "the buffered frames drain once the reader catches up");
        Assert.True(_source.Push("src/a", seq++)); // 缓冲回落后又能写了
    }
}
