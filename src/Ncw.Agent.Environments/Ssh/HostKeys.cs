namespace Ncw.Agent.Environments.Ssh;

/// <summary>服务器出示的主机密钥。<see cref="FingerprintSha256"/> 是 <c>ssh-keygen -lf</c> 那种形式:SHA256 的 Base64,不带 <c>=</c> 填充,也不带 <c>SHA256:</c> 前缀。</summary>
public sealed record HostKeyInfo(string HostName, string Algorithm, string FingerprintSha256)
{
    /// <summary>界面展示用的完整写法(<c>SHA256:xxxx</c>)。</summary>
    public string Display => "SHA256:" + FingerprintSha256;
}

/// <summary>已钉扎的主机密钥(一个主机可以按算法钉扎多把)。</summary>
public sealed record PinnedHostKey(string Algorithm, string FingerprintSha256);

public enum HostKeyVerdict
{
    /// <summary>和钉扎的一致。</summary>
    Trusted,

    /// <summary>这台主机(或这个算法)没有钉扎过:首次出现,需要用户确认。</summary>
    Unknown,

    /// <summary>同一算法的指纹和钉扎的不一致:可能是中间人,也可能是服务器重装。直接拒绝,不给「继续」。</summary>
    Changed,
}

/// <summary>
/// 主机指纹钉扎的判定(纯函数)。语义对齐 OpenSSH 的 known_hosts 严格模式,并与 2.x 内置客户端的 <c>verifyHostKey</c> 一致:
/// 没钉扎 → 首次使用要确认;一致 → 放行(不再弹窗);不同 → fail closed,绝不静默覆盖旧指纹。
/// </summary>
public static class HostKeyPinning
{
    public static HostKeyVerdict Evaluate(IReadOnlyList<PinnedHostKey> pinned, HostKeyInfo presented)
    {
        var sameAlgorithm = pinned.Where(p => string.Equals(p.Algorithm, presented.Algorithm, StringComparison.Ordinal)).ToList();
        if (sameAlgorithm.Count == 0)
        {
            // 钉扎过别的算法、这次换了算法出示:不能当作「已信任」,也不是「指纹变了」;当作新密钥,让用户确认
            return HostKeyVerdict.Unknown;
        }

        return sameAlgorithm.Any(p => string.Equals(p.FingerprintSha256, presented.FingerprintSha256, StringComparison.Ordinal))
            ? HostKeyVerdict.Trusted
            : HostKeyVerdict.Changed;
    }

    /// <summary>把已钉扎的算法排到偏好列表前面,避免服务器同时有多种主机密钥时,因为协商出另一种算法而误报「指纹变了」。</summary>
    public static IReadOnlyList<string> PreferPinned(IReadOnlyList<string> clientOrder, IReadOnlyList<PinnedHostKey> pinned)
    {
        var pinnedAlgorithms = pinned.Select(p => p.Algorithm).ToHashSet(StringComparer.Ordinal);
        return [.. clientOrder.Where(pinnedAlgorithms.Contains), .. clientOrder.Where(a => !pinnedAlgorithms.Contains(a))];
    }

    /// <summary>SSH.NET 给的指纹可能带 <c>SHA256:</c> 前缀和 <c>=</c> 填充,统一成钉扎用的写法。</summary>
    public static string NormalizeFingerprint(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["SHA256:".Length..];
        }

        return text.TrimEnd('=');
    }
}

/// <summary>每台设备上已知主机的存储。按「主机名」(见 <see cref="SshHop.HostKeyName"/>)索引,不按环境 id——同一台主机从不同环境配置连上来是同一个指纹。</summary>
public interface IHostKeyStore
{
    ValueTask<IReadOnlyList<PinnedHostKey>> GetAsync(string hostName, CancellationToken ct);

    ValueTask PinAsync(string hostName, PinnedHostKey key, CancellationToken ct);

    /// <summary>用户明确选择「信任新指纹」时先移除旧钉扎。</summary>
    ValueTask RemoveAsync(string hostName, CancellationToken ct);
}

/// <summary>能列出全部已知主机的存储(设置页的「已知主机」)。<see cref="IHostKeyStore"/> 本身只按主机名取,所以枚举是可选的另一个接口。</summary>
public interface IHostKeyCatalog
{
    ValueTask<IReadOnlyList<(string HostName, PinnedHostKey Key)>> ListAsync(CancellationToken ct);
}

/// <summary>进程内存实现(测试和没有持久化存储时用)。线程安全。</summary>
public sealed class InMemoryHostKeyStore : IHostKeyStore, IHostKeyCatalog
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<PinnedHostKey>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask<IReadOnlyList<PinnedHostKey>> GetAsync(string hostName, CancellationToken ct)
    {
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<PinnedHostKey>>(_keys.TryGetValue(hostName, out var list) ? [.. list] : []);
        }
    }

    public ValueTask PinAsync(string hostName, PinnedHostKey key, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_keys.TryGetValue(hostName, out var list))
            {
                _keys[hostName] = list = [];
            }

            list.RemoveAll(p => p.Algorithm == key.Algorithm);
            list.Add(key);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string hostName, CancellationToken ct)
    {
        lock (_lock)
        {
            _keys.Remove(hostName);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<(string HostName, PinnedHostKey Key)>> ListAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<(string, PinnedHostKey)>>([.. _keys.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).SelectMany(kv => kv.Value.Select(k => (kv.Key, k)))]);
        }
    }
}

/// <summary>
/// 主机指纹的交互回调(06 §3「主机指纹」)。首次连接时以「交互」的形式让用户确认(和审批走同一套机制,任何已连接的控制端都能回答);
/// 指纹变化时<b>不</b>调用 <see cref="ConfirmNewHostAsync"/>,只通知 <see cref="NotifyChangedAsync"/>,连接直接失败。
/// </summary>
public interface IHostKeyCallback
{
    /// <summary>首次出现的主机密钥。返回 <c>true</c> 才会钉扎并继续;取消、超时、没有控制端在线都必须是 <c>false</c>(没问到不能当成同意)。</summary>
    ValueTask<bool> ConfirmNewHostAsync(HostKeyInfo presented, CancellationToken ct);

    /// <summary>指纹和钉扎的不一致。只是通知(让界面给出处理入口),不影响「拒绝连接」这个结果。</summary>
    ValueTask NotifyChangedAsync(HostKeyInfo presented, IReadOnlyList<PinnedHostKey> pinned, CancellationToken ct);
}

/// <summary>什么都不确认的回调:所有首次出现的主机都被拒绝(fail closed)。没有交互通道时的默认值。</summary>
public sealed class RejectingHostKeyCallback : IHostKeyCallback
{
    public static RejectingHostKeyCallback Instance { get; } = new();

    public ValueTask<bool> ConfirmNewHostAsync(HostKeyInfo presented, CancellationToken ct) => ValueTask.FromResult(false);

    public ValueTask NotifyChangedAsync(HostKeyInfo presented, IReadOnlyList<PinnedHostKey> pinned, CancellationToken ct) => ValueTask.CompletedTask;
}
