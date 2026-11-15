namespace Ncw.Agent.Environments;

public sealed record FsWriteStreamOptions(long MaxBytes, bool Exclusive = false, int? UnixMode = null, bool CreateParents = false);

/// <summary>建在 <see cref="IEnvironmentFs"/> 基本操作之上的便捷方法,本机和 SSH 共用同一份实现(行为一致)。</summary>
public static class EnvironmentFsExtensions
{
    public const long DefaultMaxReadBytes = 32L * 1024 * 1024;

    public static async ValueTask<bool> ExistsAsync(this IEnvironmentFs fs, EnvPath path, CancellationToken ct = default)
    {
        try
        {
            await fs.LStatAsync(path, ct).ConfigureAwait(false);
            return true;
        }
        catch (EnvironmentException e) when (e.Code is EnvironmentErrorCode.NotFound or EnvironmentErrorCode.NotDirectory)
        {
            return false;
        }
    }

    /// <summary>读整个文件;超过 <paramref name="maxBytes"/> 抛 <see cref="EnvironmentErrorCode.TooLarge"/>(最多多读 1 字节来判定,不会把大文件读进内存)。</summary>
    public static async ValueTask<byte[]> ReadBytesAsync(this IEnvironmentFs fs, EnvPath path, long maxBytes = DefaultMaxReadBytes, CancellationToken ct = default)
    {
        var stream = await fs.OpenReadAsync(path, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            for (;;)
            {
                var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > maxBytes)
                {
                    throw new EnvironmentException(EnvironmentErrorCode.TooLarge, path.Value);
                }

                buffer.Write(chunk, 0, read);
            }
        }
    }

    /// <summary>只读前 <paramref name="count"/> 个字节(二进制嗅探用),文件更短就返回实际读到的。</summary>
    public static async ValueTask<byte[]> ReadHeadAsync(this IEnvironmentFs fs, EnvPath path, int count, CancellationToken ct = default)
    {
        var stream = await fs.OpenReadAsync(path, ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[count];
            var total = 0;
            while (total < count)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total == count ? buffer : buffer[..total];
        }
    }

    public static async ValueTask<string> ReadTextAsync(this IEnvironmentFs fs, EnvPath path, long maxBytes = DefaultMaxReadBytes, CancellationToken ct = default) =>
        System.Text.Encoding.UTF8.GetString(await fs.ReadBytesAsync(path, maxBytes, ct).ConfigureAwait(false));

    /// <summary>直接覆盖写(不原子)。要「写一半不留半截文件」用 <see cref="WriteStreamAsync"/>。</summary>
    public static async ValueTask WriteBytesAsync(this IEnvironmentFs fs, EnvPath path, ReadOnlyMemory<byte> bytes, FsWriteOptions? options = null, CancellationToken ct = default)
    {
        var stream = await fs.OpenWriteAsync(path, options ?? new FsWriteOptions(), ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    public static ValueTask WriteTextAsync(this IEnvironmentFs fs, EnvPath path, string text, FsWriteOptions? options = null, CancellationToken ct = default) =>
        fs.WriteBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(text), options, ct);

    /// <summary>
    /// 把一个流写进文件,全程有上限、失败即清理(移植自 2.x <c>writeStream</c>):
    /// 先写同目录下的临时文件,成功才改名到目标;超过 <see cref="FsWriteStreamOptions.MaxBytes"/> 当场中止并删掉临时文件;
    /// 失败不碰已有的旧文件。<see cref="FsWriteStreamOptions.Exclusive"/> 时由改名这一步原子判定目标是否已存在。返回写入的字节数。
    /// </summary>
    public static async ValueTask<long> WriteStreamAsync(this IEnvironmentFs fs, EnvPath path, Stream source, FsWriteStreamOptions options, CancellationToken ct = default)
    {
        var temp = path.Parent.Join($".{path.FileName}.ncw-{Guid.NewGuid():N}.tmp");
        long total = 0;
        try
        {
            var target = await fs.OpenWriteAsync(temp, new FsWriteOptions(Exclusive: true, options.UnixMode, options.CreateParents), ct).ConfigureAwait(false);
            await using (target.ConfigureAwait(false))
            {
                var chunk = new byte[81920];
                for (;;)
                {
                    var read = await source.ReadAsync(chunk, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                    if (total > options.MaxBytes)
                    {
                        throw new EnvironmentException(EnvironmentErrorCode.TooLarge, path.Value);
                    }

                    await target.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }

            await fs.RenameAsync(temp, path, replace: !options.Exclusive, ct).ConfigureAwait(false);
            return total;
        }
        catch
        {
            try
            {
                await fs.DeleteFileAsync(temp, CancellationToken.None).ConfigureAwait(false);
            }
            catch (EnvironmentException)
            {
                // 清理失败不能盖住真正的错误
            }

            throw;
        }
    }

    /// <summary>
    /// 不支持服务端复制的环境用的通用复制:读流写流。目标已存在则失败;源超过 <paramref name="maxBytes"/> 抛 TooLarge,不留半截文件。
    /// </summary>
    public static async ValueTask<long> CopyFileViaStreamsAsync(this IEnvironmentFs fs, EnvPath source, EnvPath destination, long maxBytes, CancellationToken ct = default)
    {
        var info = await fs.StatAsync(source, ct).ConfigureAwait(false);
        if (info.IsDirectory)
        {
            throw new EnvironmentException(EnvironmentErrorCode.IsDirectory, source.Value);
        }

        if (!info.IsFile || info.Size > maxBytes)
        {
            throw new EnvironmentException(info.IsFile ? EnvironmentErrorCode.TooLarge : EnvironmentErrorCode.Unsupported, source.Value);
        }

        var input = await fs.OpenReadAsync(source, ct).ConfigureAwait(false);
        await using (input.ConfigureAwait(false))
        {
            return await fs.WriteStreamAsync(destination, input, new FsWriteStreamOptions(maxBytes, Exclusive: true, info.UnixMode == 0 ? null : info.UnixMode), ct).ConfigureAwait(false);
        }
    }
}
