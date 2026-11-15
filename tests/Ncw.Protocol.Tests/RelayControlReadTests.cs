using System.Net.WebSockets;
using Ncw.Protocol.Transport;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 中继控制连接的读(<c>RelayTransport.ReceiveControlAsync</c>)。设备注册连接的收尾(<c>RelayRegistration.DisposeAsync</c>)先 <c>ClientWebSocket.Abort()</c> 再等读循环退出:
/// Abort 会连带 Dispose,读循环此后再发起的 <c>ReceiveAsync</c> 抛的是 <see cref="ObjectDisposedException"/>(不是 <see cref="WebSocketException"/>),
/// 以前它一路冒泡成「注册循环崩溃」,<c>DisposeAsync</c> 把这个异常抛给调用方(整套并行时 <c>RelayIntegrationTests.Removing_a_token_…</c> 偶发,见 docs/parity.md)。
/// 对读的一方来说,连接在读之前被本端收尾释放和连接断开是同一件事:返回 null(调用方据此退出或重新注册)。
/// </summary>
public sealed class RelayControlReadTests
{
    [Fact]
    public async Task A_socket_aborted_and_disposed_before_the_read_is_a_closed_connection_not_an_exception()
    {
        using var socket = new ClientWebSocket();
        socket.Abort(); // 收尾的第一步;之后任何一次新的读都落在已释放的套接字上

        Assert.Null(await RelayTransport.ReceiveControlAsync(socket, CancellationToken.None));
    }
}
