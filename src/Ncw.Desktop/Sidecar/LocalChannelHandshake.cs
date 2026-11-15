using Ncw.Protocol.Session;
using Ncw.Protocol.Transport;

namespace Ncw.Desktop.Sidecar;

/// <summary>真实的就绪握手:本机通道 + 本机令牌(docs/design/09 §5、§7),握手完成就断开,只为拿到 Agent 的版本。</summary>
public sealed class LocalChannelHandshake(string clientId, TimeSpan? timeout = null) : IAgentHandshake
{
    public async Task<AgentHello> ProbeAsync(string endpoint, string token, string clientVersion, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            var stream = await LocalTransport.ConnectAsync(endpoint, limit.Token).ConfigureAwait(false);
            var client = await DeviceClient.ConnectAsync(stream, new ClientHello(clientId + "-probe", clientVersion, LocalToken: token), limit.Token).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
                return new AgentHello(client.Welcome.DeviceId, client.Welcome.DeviceVersion, client.Welcome.Version);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The agent did not answer the handshake in time.");
        }
    }
}
