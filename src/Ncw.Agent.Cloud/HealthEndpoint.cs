using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ncw.Agent.Cloud;

/// <summary>
/// 可选的内网健康检查端点(<c>GET /healthz</c>,给沙箱编排的存活 / 就绪探针用)。远程客户端的流量<b>只</b>走中继,这个端点不是控制通道:
/// 它没有认证、不返回任何会话 / 用户数据,只回「活着 / 没在收尾」和设备 id(公开标识)。自带最小 HTTP/1.1(<c>TcpListener</c>,零反射,和开放网关同一个取舍:不拉 ASP.NET 共享框架)。
/// 默认绑回环;沙箱探针从内网来时配置里显式给内网地址。每个连接只读请求行、只回一次、立刻关闭;读超时 3 秒,并发上限 16。
/// </summary>
public sealed class HealthEndpoint : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<(bool Ready, string DeviceId)> _state;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(16, 16);
    private readonly Task _loop;

    private HealthEndpoint(TcpListener listener, Func<(bool Ready, string DeviceId)> state)
    {
        _listener = listener;
        _state = state;
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static HealthEndpoint Start(string? bind, int port, Func<(bool Ready, string DeviceId)> state)
    {
        var listener = new TcpListener(bind is { Length: > 0 } ? IPAddress.Parse(bind) : IPAddress.Loopback, port);
        listener.Start(16);
        return new HealthEndpoint(listener, state);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (!_slots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.ReceiveTimeout = 3000;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var stream = client.GetStream();
                var buffer = new byte[1024];
                var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                var line = Encoding.ASCII.GetString(buffer, 0, Math.Min(read, 128));
                var (ready, deviceId) = _state();
                string status, body;
                if (!line.StartsWith("GET /healthz", StringComparison.Ordinal))
                {
                    status = "404 Not Found";
                    body = "{\"ok\":false}";
                }
                else if (ready)
                {
                    status = "200 OK";
                    body = $"{{\"ok\":true,\"deviceId\":\"{deviceId}\"}}";
                }
                else
                {
                    status = "503 Service Unavailable";
                    body = $"{{\"ok\":false,\"deviceId\":\"{deviceId}\",\"state\":\"preparing\"}}";
                }

                var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}
