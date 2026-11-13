using System.Buffers.Binary;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

public class FrameTests
{
    public static TheoryData<Frame> AllFrames() =>
    [
        new HelloFrame(1, 1, "c1", "3.0.0", "desktop", ["a"], 16, "tok", null),
        new WelcomeFrame(1, "dev", "3.0.0", "owner", [], "resume", 123),
        new RejectFrame(ErrorCodes.Unauthorized, "no"),
        new RequestFrame(7, "projects.list", [0x80], null),
        new ResponseFrame(7, false, null, new ProtocolError(ErrorCodes.NotFound, "x", false, 5)),
        new CancelFrame(7),
        new SubscribeFrame(8, "device", 41),
        new UnsubscribeFrame("device"),
        new SnapshotFrame("device", 3, [1, 2, 3]),
        new EventFrame("device", 4, "projects.changed", [4]),
        new ResyncFrame("device", "trimmed"),
        new PingFrame(1),
        new PongFrame(1),
    ];

    [Theory]
    [MemberData(nameof(AllFrames))]
    public void Every_frame_round_trips(Frame frame)
    {
        var decoded = FrameCodec.Decode(FrameCodec.Encode(frame));

        Assert.Equal(frame.GetType(), decoded.GetType());
        Assert.Equal(FrameCodec.Encode(frame), FrameCodec.Encode(decoded));
    }

    /// <summary>
    /// 黄金向量:按协议版本冻结线上格式(docs/design/09 §12)。这条测试失败说明线上格式变了 ——
    /// 那必须升协议版本或加能力开关,不能直接改向量。
    /// </summary>
    [Fact]
    public void Golden_vectors_v1()
    {
        Assert.Equal(GoldenV1.Ping, Convert.ToHexStringLower(FrameCodec.Encode(new PingFrame(1))));
        Assert.Equal(GoldenV1.Request, Convert.ToHexStringLower(FrameCodec.Encode(new RequestFrame(7, "projects.list", [0x80], "k"))));
    }

    [Fact]
    public void Payloads_round_trip_with_generated_shapes()
    {
        var def = DeviceMethods.ProjectsAdd;
        var bytes = FrameCodec.EncodePayload(new ProjectsAddParams("/tmp/x", null), def.ParamsShape);

        Assert.Equal(new ProjectsAddParams("/tmp/x", null), FrameCodec.DecodePayload(bytes, def.ParamsShape));
    }

    [Fact]
    public async Task Stream_reads_back_what_was_written()
    {
        var buffer = new MemoryStream();
        await using (var writer = new FrameStream(new NonDisposingStream(buffer)))
        {
            await writer.WriteAsync(new PingFrame(5));
            await writer.WriteAsync(new PongFrame(6));
        }

        buffer.Position = 0;
        await using var reader = new FrameStream(buffer);
        Assert.Equal(new PingFrame(5), await reader.ReadAsync());
        Assert.Equal(new PongFrame(6), await reader.ReadAsync());
        Assert.Null(await reader.ReadAsync()); // 正常结束
    }

    [Fact]
    public async Task Oversized_or_truncated_frames_fail_instead_of_continuing()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, ProtocolInfo.MaxFrameBytes + 1);
        await using var oversized = new FrameStream(new MemoryStream(header));
        await Assert.ThrowsAsync<ProtocolException>(async () => await oversized.ReadAsync());

        BinaryPrimitives.WriteUInt32BigEndian(header, 10);
        await using var truncated = new FrameStream(new MemoryStream([.. header, 1, 2]));
        await Assert.ThrowsAsync<ProtocolException>(async () => await truncated.ReadAsync());
    }

    private sealed class NonDisposingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { }
    }
}
