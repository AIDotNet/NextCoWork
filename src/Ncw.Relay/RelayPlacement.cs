using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Ncw.Relay;

/// <summary>设备归哪个中继实例。</summary>
/// <param name="IsLocal">归本实例。</param>
/// <param name="OwnerUrl">不归本实例时,归属实例的对外地址(放进 <c>421</c> 响应的 <c>X-Ncw-Relay-Owner</c> 头)。</param>
public readonly record struct Placement(bool IsLocal, string? OwnerUrl = null);

/// <summary>
/// 按 deviceId 粘性路由的接口(09 §9「无状态,按 deviceId 粘性路由,可以水平扩展」)。
/// 一台设备的注册连接和它所有控制端的会话必须落在同一个实例上(拼接发生在内存里),所以负载均衡器要按 URL 查询串里的 <c>d=</c> 做一致性哈希;
/// 万一没有做到,实例用这个接口发现「这台设备不归我」,返回 421 和归属实例的地址,让客户端 / 负载均衡器重试,而不是静默地对不上。
/// </summary>
public interface IRelayPlacement
{
    Placement Locate(string deviceId);
}

/// <summary>单实例:全部在本机。</summary>
public sealed class SingleInstancePlacement : IRelayPlacement
{
    public static readonly SingleInstancePlacement Instance = new();

    public Placement Locate(string deviceId) => new(true);
}

/// <summary>
/// 多实例的 rendezvous(最高随机权重)哈希:每个实例对 deviceId 打分,分最高的拥有它。
/// 增减实例时只有被增减的那个实例上的设备迁移(设备重连时重新注册,中继本身无状态),不会整体洗牌。纯函数,所有实例用同一份实例列表得到同样的结果。
/// </summary>
public sealed class RendezvousPlacement : IRelayPlacement
{
    private readonly string _self;
    private readonly string[] _instances;

    /// <param name="self">本实例的对外地址(必须在 <paramref name="instances"/> 里)。</param>
    /// <param name="instances">全部实例的对外地址。</param>
    public RendezvousPlacement(string self, IEnumerable<string> instances)
    {
        _self = self;
        _instances = [.. instances];
        if (_instances.Length == 0 || !_instances.Contains(self, StringComparer.Ordinal))
        {
            throw new ArgumentException("The instance list must contain this instance", nameof(instances));
        }
    }

    public Placement Locate(string deviceId)
    {
        var best = _instances[0];
        var bestScore = Score(best, deviceId);
        foreach (var instance in _instances.Skip(1))
        {
            var score = Score(instance, deviceId);
            if (score > bestScore || (score == bestScore && string.CompareOrdinal(instance, best) > 0))
            {
                best = instance;
                bestScore = score;
            }
        }

        return string.Equals(best, _self, StringComparison.Ordinal) ? new Placement(true) : new Placement(false, best);
    }

    private static ulong Score(string instance, string deviceId) =>
        BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(instance + "\n" + deviceId)));
}
