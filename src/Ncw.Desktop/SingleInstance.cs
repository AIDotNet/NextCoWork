using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Ncw.Desktop;

/// <summary>
/// 单实例转发(docs/design/07 §2 第 4 步):应用已经在运行时,系统(Windows / Linux)会再启动一个新进程并把 <c>nextcowork://…</c> 当作启动参数交给它;
/// 新进程把参数转给已经运行的主实例,然后自己退出。
/// <list type="bullet">
/// <item><b>谁是主实例</b>:独占打开客户端目录下的锁文件(<c>FileShare.None</c>;进程崩溃 / 被杀时由操作系统释放,不会留下过期的锁)。两个进程同时启动也只有一个拿得到。</item>
/// <item><b>转发通道</b>:当前用户专属的命名管道(<see cref="PipeOptions.CurrentUserOnly"/>),其他用户连不上;管道名由客户端目录派生,所以不同的客户端目录(开发、测试隔离)互不干扰。</item>
/// <item>收到的内容<b>不可信</b>:只收第一行(UTF-8)、有长度上限、读取有期限(只连不发的对端挂不住监听),这里不做任何解释,交给链接解析器按白名单处理。</item>
/// </list>
/// </summary>
public sealed class SingleInstance : IAsyncDisposable, IDisposable
{
    /// <summary>一条消息的字节上限(<c>nextcowork://</c> 链接最长 4096 个字符,留出余量)。</summary>
    public const int MaxMessageBytes = 16 * 1024;

    /// <summary>消息的前缀:转发一个链接 / 只要求主窗口回到前台。</summary>
    public const string LinkPrefix = "link ";

    public const string ActivateMessage = "activate";

    private readonly CancellationTokenSource _cts = new();
    private readonly string _pipe;
    private readonly Action<string> _onMessage;
    private readonly FileStream _lock;
    private Task _loop = Task.CompletedTask;

    private SingleInstance(string pipe, FileStream @lock, Action<string> onMessage)
    {
        _pipe = pipe;
        _lock = @lock;
        _onMessage = onMessage;
    }

    /// <summary>由客户端目录(和用户名)派生的管道名。</summary>
    public static string PipeName(string clientDirectory)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + "\0" + Path.GetFullPath(clientDirectory)));
        return "ncw-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }

    /// <summary>主实例的锁文件(在客户端目录里)。</summary>
    public static string LockPath(string clientDirectory) => Path.Combine(clientDirectory, "instance.lock");

    /// <summary>成为主实例并开始监听。已经有主实例(锁被占着)返回 null。</summary>
    public static SingleInstance? TryBecomePrimary(string pipeName, string lockPath, Action<string> onMessage)
    {
        FileStream held;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(lockPath))!);
            held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null; // 已有主实例占着锁
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        var instance = new SingleInstance(pipeName, held, onMessage);
        instance._loop = Task.Run(instance.RunAsync);
        return instance;
    }

    /// <summary>把一条消息发给主实例。连不上(没有主实例,或它正忙到超时)返回 false。</summary>
    public static bool TrySend(string pipeName, string message, TimeSpan timeout)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length > MaxMessageBytes)
        {
            return false;
        }

        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            client.Write(bytes);
            client.WriteByte((byte)'\n');
            client.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private NamedPipeServerStream Create() =>
        new(_pipe, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task RunAsync()
    {
        var current = Create();
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await current.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                var connected = current;
                current = Create(); // 先造好下一个监听再处理这一个:连续到达的转发不会撞上「没有监听者」的空档
                _ = Task.Run(() => HandleAsync(connected));
            }
        }
        catch (OperationCanceledException)
        {
            // 退出
        }
        finally
        {
            await current.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream connection)
    {
        try
        {
            if (await ReadLineAsync(connection, _cts.Token).ConfigureAwait(false) is { Length: > 0 } message)
            {
                _onMessage(message);
            }
        }
        catch (Exception e) when (e is IOException or DecoderFallbackException or OperationCanceledException)
        {
            // 对端中途断开 / 发来的不是 UTF-8 / 正在退出:丢弃这一条
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>读到换行或流结束为止,最多 <see cref="MaxMessageBytes"/> 字节;超过就丢弃整条(返回 null)。</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxMessageBytes + 1];
        var length = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5)); // 只连不发的对端不能把处理挂住
        try
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
                if (Array.IndexOf(buffer, (byte)'\n', 0, length) >= 0)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        var end = Array.IndexOf(buffer, (byte)'\n', 0, length);
        if (length > MaxMessageBytes && (end < 0 || end > MaxMessageBytes))
        {
            return null;
        }

        return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, end < 0 ? length : end).Trim();
    }

    /// <summary>进程退出时:停止监听并释放锁(不等监听循环收尾;进程马上就没了)。</summary>
    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已经 DisposeAsync 过
        }

        _lock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 已停
        }

        _lock.Dispose();
        _cts.Dispose();
    }
}
