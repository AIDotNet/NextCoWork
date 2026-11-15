using System.Security.Cryptography;
using Ncw.Client.Updates;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Ncw.Desktop.Updates;

/// <summary>
/// Velopack 的更新源包装(docs/design/04 §6「发布清单另外用 Ed25519 签名」):<b>没验过清单签名的东西,Velopack 看不到、也下载不了。</b>
/// <list type="number">
/// <item><see cref="GetReleaseFeed"/>:先读清单与分离签名并验签(<see cref="SignedManifest"/>:信任列表、通道、产品、策略自洽),
/// 验不过就抛 <see cref="UpdateException"/>,Velopack 拿不到 feed。验过之后只放行<b>清单里列出、且 sha256 一致</b>的包(全量 / 增量):
/// feed 里多出来的包(没被发布流程签进清单的)被丢掉;放行的包的 SHA256 被<b>替换成清单里的值</b>,所以 Velopack 自己的校验(含增量重建出来的全量包)校验的是签过名的值。</item>
/// <item>清单声明的版本必须在放行的全量包里(feed 与清单对不上就是 feed 有问题,不静默降级到别的版本)。</item>
/// <item><see cref="DownloadReleaseEntry"/>:只下载放行过的文件;下载完再算一遍 sha256 和清单比,不一致就删掉文件并抛 <see cref="UpdateFailureKind.ChecksumMismatch"/>。</item>
/// </list>
/// 清单里的 <c>platform</c> 只认本机的(同一份清单可以列多个平台)。已知限制:一份旧的、合法签过名的清单被重放(冻结攻击)会让用户停在旧版本,
/// 但不能让它装比清单更新的东西,也不能让它降级(服务层和 Velopack 都不降级)。
/// </summary>
public sealed class SignedFeedSource(IUpdateSource inner, IFeedReader manifestReader, ReleaseKeyRing keys, UpdateChannel channel, string rid) : IUpdateSource
{
    private readonly object _gate = new();
    private Dictionary<string, ManifestArtifact> _endorsed = new(StringComparer.Ordinal);

    /// <summary>最近一次验过签名的清单(没有成功读过为 null)。</summary>
    public ReleaseManifest? LastManifest { get; private set; }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channelName, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        var manifest = await SignedManifest.ReadAsync(manifestReader, keys, channel, CancellationToken.None).ConfigureAwait(false);
        var endorsed = manifest.Artifacts
            .Where(a => string.Equals(a.Platform, rid, StringComparison.Ordinal) && a.Kind is "velopack-full" or "velopack-delta")
            .GroupBy(a => a.FileName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var feed = await inner.GetReleaseFeed(logger, appId, channelName, stagingId, latestLocalRelease).ConfigureAwait(false);
        var accepted = new List<VelopackAsset>();
        foreach (var asset in feed.Assets)
        {
            if (!endorsed.TryGetValue(asset.FileName ?? "", out var artifact))
            {
                logger.Warn($"feed asset {asset.FileName} is not listed in the signed manifest; ignored");
                continue;
            }

            if (!string.IsNullOrEmpty(asset.SHA256) && !string.Equals(asset.SHA256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(UpdateFailureKind.ChecksumMismatch, $"the feed says {asset.FileName} has sha256 {asset.SHA256}, the signed manifest says {artifact.Sha256}");
            }

            // Velopack 自己的校验是区分大小写的字符串比较,它算出来的是大写十六进制;清单里是小写(tools/Ncw.Build 写的):统一成大写再交给它。
            accepted.Add(asset with { SHA256 = artifact.Sha256.ToUpperInvariant() });
        }

        var announced = Velopack.SemanticVersion.TryParse(manifest.Version, out var version) ? version : null;
        if (announced is null || !accepted.Any(a => a.Type == VelopackAssetType.Full && a.Version == announced))
        {
            throw new UpdateException(UpdateFailureKind.FeedInvalid, $"the feed has no full package for {manifest.Version}, the version the signed manifest announces");
        }

        lock (_gate)
        {
            LastManifest = manifest;
            _endorsed = endorsed;
        }

        return feed with { Assets = [.. accepted] };
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        ManifestArtifact? artifact;
        lock (_gate)
        {
            _endorsed.TryGetValue(releaseEntry.FileName ?? "", out artifact);
        }

        if (artifact is null)
        {
            throw new UpdateException(UpdateFailureKind.FeedInvalid, $"{releaseEntry.FileName} is not in the signed manifest; refusing to download it");
        }

        await inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken).ConfigureAwait(false);

        string actual;
        await using (var stream = File.OpenRead(localFile))
        {
            actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancelToken).ConfigureAwait(false));
        }

        if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                File.Delete(localFile);
            }
            catch (IOException)
            {
            }

            throw new UpdateException(UpdateFailureKind.ChecksumMismatch, $"{releaseEntry.FileName}: downloaded sha256 {actual} does not match the signed manifest ({artifact.Sha256})");
        }
    }
}
