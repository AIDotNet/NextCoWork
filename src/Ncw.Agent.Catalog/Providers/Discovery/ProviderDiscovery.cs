using System.Security.Cryptography;
using System.Text;
using Ncw.Agent.Llm.Media;
using Ncw.Agent.Llm.Wire;

namespace Ncw.Agent.Catalog.Discovery;

/// <summary>扫描结果里的一个候选(去重、对过现有供应商之后)。<see cref="ApiKey"/> 是明文,只在设备上用,映射成协议 DTO 时只留后四位。</summary>
public sealed record DiscoveryCandidate(
    string Id,
    string Source,
    string Origin,
    IReadOnlyList<string> AlsoSeenIn,
    string SuggestedId,
    string Name,
    WireProtocol Protocol,
    string BaseUrl,
    string? ApiKey,
    IReadOnlyList<string> Models,
    string Status,
    string? ExistingProviderId)
{
    public string? KeyLast4 => ApiKey is { Length: >= 4 } key ? key[^4..] : null;

    /// <summary>不要把明文带进日志 / 异常消息。</summary>
    public override string ToString() => $"DiscoveryCandidate {{ Id = {Id}, Source = {Source}, BaseUrl = {BaseUrl}, Status = {Status} }}";
}

/// <summary>一条候选的导入结局。</summary>
public sealed record DiscoveryImportOutcome(string CandidateId, string? ProviderId, string? ErrorKey);

/// <summary>
/// 供应商自动发现与导入(docs/design/07 §3「供应商自动发现与导入」)。在设备上读其他客户端的配置(<see cref="DiscoveryReaders"/>),
/// 同一地址 + 同一把 key 合成一条,和现有供应商比出 <c>new</c> / <c>imported</c> / <c>conflict</c>;导入时重新扫一遍、按 id 找回候选,
/// 在设备上建供应商、存密钥、建别名——密钥明文从头到尾不出设备。只读别人的文件,从不写。
/// </summary>
public sealed class ProviderDiscovery(ProviderService providers, IDiscoveryHost host)
{
    public const string StatusNew = "new";
    public const string StatusImported = "imported";
    public const string StatusConflict = "conflict";
    public const string ErrorGone = "providers.discovery.gone";
    public const string ErrorAlreadyImported = "providers.discovery.alreadyImported";

    public async Task<IReadOnlyList<DiscoveryCandidate>> ScanAsync(CancellationToken ct = default)
    {
        var raw = DiscoveryReaders.ReadAll(host);
        var merged = Merge(raw);

        // 现有文本供应商的地址和 key(只在这里比较,不外传;媒体供应商允许共用同一网关和 key)
        var existing = new List<(string Id, string BaseUrl, string? Key)>();
        foreach (var p in providers.Snapshot.Providers.Where(p => p.Kind == ProviderKind.Text && !ProviderRefs.IsManagedProviderId(p.Id)))
        {
            existing.Add((p.Id, Normalize(p.BaseUrl), await KeyOfAsync(p.Id, ct).ConfigureAwait(false)));
        }

        var ids = existing.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        return [.. merged.Select(c =>
        {
            var sameUrl = existing.Where(e => e.BaseUrl == Normalize(c.BaseUrl)).ToList();
            if (sameUrl.FirstOrDefault(e => e.Key == c.ApiKey) is { Id: not null } same)
            {
                return c with { Status = StatusImported, ExistingProviderId = same.Id };
            }

            if (sameUrl.Count > 0)
            {
                return c with { Status = StatusConflict, ExistingProviderId = sameUrl[0].Id };
            }

            return ids.Contains(c.SuggestedId) ? c with { Status = StatusConflict, ExistingProviderId = c.SuggestedId } : c;
        })];
    }

    /// <summary>按候选 id 导入。逐条给结局,一条失败不影响别的。</summary>
    public async Task<IReadOnlyList<DiscoveryImportOutcome>> ImportAsync(IReadOnlyList<string> candidateIds, CancellationToken ct = default)
    {
        var scan = await ScanAsync(ct).ConfigureAwait(false);
        var taken = providers.Snapshot.Providers.Select(p => p.Id).Concat(ProviderRefs.ManagedProviderIds).ToHashSet(StringComparer.Ordinal);
        var outcomes = new List<DiscoveryImportOutcome>();
        foreach (var id in candidateIds.Distinct(StringComparer.Ordinal))
        {
            if (scan.FirstOrDefault(c => c.Id == id) is not { } candidate)
            {
                outcomes.Add(new(id, null, ErrorGone));
                continue;
            }

            if (candidate.Status == StatusImported)
            {
                outcomes.Add(new(id, null, ErrorAlreadyImported));
                continue;
            }

            var providerId = Unique(candidate.SuggestedId, taken);
            try
            {
                providers.Upsert(new ProviderRecord(providerId, candidate.Name, candidate.Protocol, candidate.BaseUrl));
                taken.Add(providerId);
                if (candidate.ApiKey is { } key)
                {
                    await providers.SetApiKeyAsync(providerId, key, ct).ConfigureAwait(false);
                }

                if (candidate.Models.Count > 0)
                {
                    providers.SetAliases(providerId, candidate.Models);
                }

                outcomes.Add(new(id, providerId, null));
            }
            catch (ProviderException e)
            {
                outcomes.Add(new(id, null, e.Key));
            }
        }

        return outcomes;
    }

    /// <summary>同一地址 + 同一把 key 合成一条:保留来源顺序靠前的那个,模型取并集,其余来源记进「也见于」。</summary>
    internal static List<DiscoveryCandidate> Merge(IReadOnlyList<RawCandidate> raw)
    {
        var result = new List<DiscoveryCandidate>();
        var ordered = raw.Select((c, i) => (c, i)).OrderBy(x => Array.IndexOf(DiscoveryReaders.Order, x.c.Source)).ThenBy(x => x.i).Select(x => x.c);
        foreach (var group in ordered.GroupBy(c => (Normalize(c.BaseUrl), c.ApiKey ?? "")))
        {
            var first = group.First();
            var (suggestedId, name) = KnownServices.Identify(first.BaseUrl, first.Protocol, first.Name);
            var models = group.SelectMany(c => c.Models).Distinct(StringComparer.Ordinal).ToList();
            var also = group.Skip(1).Select(c => c.Source).Where(s => s != first.Source).Distinct(StringComparer.Ordinal).ToList();
            result.Add(new DiscoveryCandidate(IdOf(first), first.Source, first.Origin, also, suggestedId, name, first.Protocol, first.BaseUrl.Trim(),
                first.ApiKey, models, StatusNew, null));
        }

        return result;
    }

    /// <summary>
    /// 候选 id:来源 + 出处 + 地址 + key 的摘要(同一份配置每次扫描都一样,配置一改就变——导入时候选「没了」就是这个意思)。
    /// 只取 SHA-256 的前 8 字节,而且混着别的字段:从 id 推不出 key。
    /// </summary>
    private static string IdOf(RawCandidate c) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{c.Source}\n{c.Origin}\n{Normalize(c.BaseUrl)}\n{c.ApiKey}")))[..16];

    internal static string Normalize(string baseUrl) => baseUrl.Trim().TrimEnd('/').ToLowerInvariant();

    private static string Unique(string id, HashSet<string> taken)
    {
        if (!taken.Contains(id))
        {
            return id;
        }

        for (var n = 2; ; n++)
        {
            var next = $"{id}-{n}";
            if (!taken.Contains(next))
            {
                return next;
            }
        }
    }

    private async Task<string?> KeyOfAsync(string providerId, CancellationToken ct)
    {
        try
        {
            return await providers.RevealCredentialAsync(providerId, ct).ConfigureAwait(false) is RevealedApiKey key ? key.ApiKey : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null; // 没 key / 凭据库读不了:当作「对不上」,不影响扫描
        }
    }
}
