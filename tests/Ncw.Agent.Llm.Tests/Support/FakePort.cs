using Ncw.Agent.Llm.Auth;

namespace Ncw.Agent.Llm.Tests.Support;

public sealed class FakePort : IAccountPoolPort
{
    public List<ProviderAccount> Accounts { get; } = [];

    public long Now { get; set; } = 1_000_000;

    public bool Rotation { get; set; } = true;

    public List<string> Changes { get; } = [];

    public IReadOnlyList<ProviderAccount> List(string providerId) => Accounts.Where(a => a.ProviderId == providerId).ToList();

    public void SetLimit(string accountId, AccountLimit? limit)
    {
        var i = Accounts.FindIndex(a => a.Id == accountId);
        Accounts[i] = Accounts[i] with { Limit = limit };
    }

    public void SetQuota(string accountId, QuotaSnapshot quota)
    {
        var i = Accounts.FindIndex(a => a.Id == accountId);
        Accounts[i] = Accounts[i] with { Quota = quota };
    }

    /// <summary>设了就用它(路由测试里和假时钟绑在一起),否则用 <see cref="Now"/>。</summary>
    public Func<long>? Clock { get; set; }

    public long NowMs() => Clock?.Invoke() ?? Now;

    public bool RotationEnabled() => Rotation;

    public void OnChanged(string providerId) => Changes.Add(providerId);
}
