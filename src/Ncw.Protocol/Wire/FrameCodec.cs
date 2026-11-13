using System.Buffers.Binary;
using Nerdbank.MessagePack;
using PolyType;

namespace Ncw.Protocol.Wire;

/// <summary>帧与载荷的 MessagePack 编解码。只用源生成的类型形状,兼容 NativeAOT。</summary>
public static class FrameCodec
{
    private static readonly MessagePackSerializer Serializer = new();

    public static byte[] Encode(Frame frame) => Serializer.Serialize<Frame, FrameShapes>(frame);

    public static Frame Decode(ReadOnlyMemory<byte> bytes) =>
        Serializer.Deserialize<Frame, FrameShapes>(bytes)
        ?? throw new ProtocolException(ErrorCodes.InvalidRequest, "Empty frame");

    /// <summary>编码方法参数、结果、主题状态。</summary>
    public static byte[] EncodePayload<T>(T value, ITypeShape<T> shape) => Serializer.Serialize(value, shape);

    public static T DecodePayload<T>(ReadOnlyMemory<byte> bytes, ITypeShape<T> shape) =>
        Serializer.Deserialize(bytes, shape)!;
}

[GenerateShapeFor<Frame>]
internal sealed partial class FrameShapes;

/// <summary>
/// L2:在任意双向字节流上读写帧,4 字节大端长度前缀。写入串行化;读取只能由一个循环调用。
/// 帧超过上限、长度非法时抛出,调用方应断开连接(不能静默继续)。
/// </summary>
public sealed class FrameStream(Stream stream, int maxFrameBytes = ProtocolInfo.MaxFrameBytes) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _header = new byte[4];

    public Stream Inner => stream;

    public async ValueTask WriteAsync(Frame frame, CancellationToken ct = default)
    {
        var body = FrameCodec.Encode(frame);
        if (body.Length > maxFrameBytes)
        {
            throw new ProtocolException(ErrorCodes.TooLarge, $"Frame of {body.Length} bytes exceeds {maxFrameBytes}");
        }

        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>读下一帧;对端正常关闭时返回 null。</summary>
    public async ValueTask<Frame?> ReadAsync(CancellationToken ct = default)
    {
        if (!await ReadExactlyOrEndAsync(_header, ct).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(_header);
        if (length == 0 || length > maxFrameBytes)
        {
            throw new ProtocolException(ErrorCodes.TooLarge, $"Invalid frame length {length}");
        }

        var body = new byte[length];
        if (!await ReadExactlyOrEndAsync(body, ct).ConfigureAwait(false))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "Connection closed in the middle of a frame");
        }

        return FrameCodec.Decode(body);
    }

    private async ValueTask<bool> ReadExactlyOrEndAsync(byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return read == 0 ? false : throw new ProtocolException(ErrorCodes.InvalidRequest, "Truncated frame");
            }

            read += n;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
