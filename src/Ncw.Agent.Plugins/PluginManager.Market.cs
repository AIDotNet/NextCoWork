namespace Ncw.Agent.Plugins;

public sealed partial class PluginManager
{
    private const int MaxMarketPages = 20;
    private static readonly TimeSpan MarketCacheTtl = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _installSlots = new(2, 2);
    private readonly Dictionary<string, Task<InstalledPlugin>> _inflightInstalls = new(StringComparer.Ordinal);
    private Task<PluginUpdateResult>? _updateAll;
    private (DateTimeOffset At, IReadOnlyList<PluginMarketItem> Items)? _marketCache;

    private IPluginMarketClient Market => _options.Market ?? throw new PluginException(PluginErrorKeys.MarketFailed, "No market client is configured on this device.");

    public Task<PluginMarketPage> MarketListAsync(string? query, string? category, int page, CancellationToken ct = default) =>
        Market.ListAsync(query, category, Math.Max(1, page), 12, _options.AppInfo.AppVersion, ct);

    public Task<IReadOnlyList<string>> MarketCategoriesAsync(CancellationToken ct = default) => Market.CategoriesAsync(ct);

    public Task<PluginMarketDetail> MarketDetailAsync(string slug, CancellationToken ct = default) => Market.DetailAsync(slug, ct);

    /// <summary>
    /// 从市场安装:详情(版本)→ 安装授权(权威摘要,需要账户令牌)→ 下载(≤ 20MB)→ 校验摘要 → 安装。需要账户令牌而没有 → <see cref="PluginErrorKeys.AuthRequired"/>(同 Skills;浏览照常)。
    /// ★ 同一个 slug 的两次安装合流成一次(两次安装交叠时,A 的停掉旧版本会落在 B 读旧决定和装载之间,插件被静默关掉);同时最多跑两个下载(整个包读进内存,20MB × N 并发全都常驻)。
    /// </summary>
    public Task<InstalledPlugin> InstallMarketAsync(string slug, string? version, IProgress<PluginInstallProgress>? progress = null, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // ★ 只合流「还在跑的」:登记项由任务自己在收尾时摘掉(而不是靠 ContinueWith 异步摘),否则紧接着的下一次安装会拿到上一次已经完成的结果
            if (_inflightInstalls.TryGetValue(slug, out var running) && !running.IsCompleted)
            {
                return running;
            }

            var task = TrackInstallAsync(slug, version, progress, ct);
            if (!task.IsCompleted)
            {
                _inflightInstalls[slug] = task;
            }

            return task;
        }
    }

    private async Task<InstalledPlugin> TrackInstallAsync(string slug, string? version, IProgress<PluginInstallProgress>? progress, CancellationToken ct)
    {
        try
        {
            return await RunMarketInstallAsync(slug, version, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _inflightInstalls.Remove(slug);
            }
        }
    }

    private async Task<InstalledPlugin> RunMarketInstallAsync(string slug, string? version, IProgress<PluginInstallProgress>? progress, CancellationToken ct)
    {
        await _installSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            progress?.Report(new PluginInstallProgress("preparing"));
            var detail = await Market.DetailAsync(slug, ct).ConfigureAwait(false);
            var chosen = version ?? detail.Item.Version ?? detail.Versions.FirstOrDefault()?.Version
                ?? throw new PluginException(PluginErrorKeys.NoVersion, "The market has no installable version.");
            var grant = await Market.GrantAsync(slug, chosen, ct).ConfigureAwait(false);
            // ② 版本对得上:请求 1.2.0、服务端给 1.3.0 的摘要而客户端下载 1.2.0,校验必然失败,但失败信息会指向「包损坏」,而真正的原因是两边说的不是同一个版本
            if (grant.Version != chosen)
            {
                throw new PluginException(PluginErrorKeys.DigestMismatch, "The market vouched for a different version than the one requested.");
            }

            var declared = detail.Versions.FirstOrDefault(v => v.Version == chosen)?.FileSize;
            var bytes = await Market.DownloadAsync(slug, chosen, PluginLimits.MaxZipBytes, p =>
                progress?.Report(new PluginInstallProgress("downloading", p.Received, p.Total ?? declared)), ct).ConfigureAwait(false);
            progress?.Report(new PluginInstallProgress("installing"));
            var installed = await InstallZipAsync(bytes, slug, grant.Sha256, ct).ConfigureAwait(false);
            progress?.Report(new PluginInstallProgress("done"));
            return installed;
        }
        catch (PluginException e)
        {
            progress?.Report(new PluginInstallProgress("failed", ErrorKey: e.Key)); // 终态一定要推出去,成功失败都是
            throw;
        }
        finally
        {
            _installSlots.Release();
        }
    }

    // ───────────────────────── 更新 ─────────────────────────

    /// <summary>
    /// 把市场整张表翻完(浏览页只有第一页:市场长到第 21 条的那天开始静默漏报),缓存 10 分钟(缓存的是<b>市场列表</b>这次网络结果,不是算好的更新表:更新完一个插件,计数立刻就该减下去),
    /// 和<b>当前</b>已装的逐个比。市场列表已按本机版本过滤,所以 engines 提高到本机不满足的插件不会出现(提示了也装不上)。
    /// ★ 新增必选能力用 <c>approvedRequired</c> 算(不是 granted:后者是超集;也不是市场报的 <c>permissionEscalated</c>:它是相对上一个市场版本算的)。
    /// </summary>
    public async Task<IReadOnlyList<PluginUpdate>> CheckUpdatesAsync(bool force = false, CancellationToken ct = default)
    {
        IReadOnlyList<PluginMarketItem> market;
        var now = _options.Clock.GetUtcNow();
        if (!force && _marketCache is { } cache && now - cache.At <= MarketCacheTtl)
        {
            market = cache.Items;
        }
        else
        {
            var items = new List<PluginMarketItem>();
            for (var page = 1; page <= MaxMarketPages; page++)
            {
                var result = await Market.ListAsync(null, null, page, 100, _options.AppInfo.AppVersion, ct).ConfigureAwait(false);
                items.AddRange(result.Items);
                // 20 页硬上限是防服务端把 hasNext 永远置 true 的死循环
                if (!result.HasNext || result.Items.Count == 0)
                {
                    break;
                }
            }

            market = items;
            _marketCache = (now, market);
        }

        var byId = market.GroupBy(i => i.PluginId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        List<PluginRecord> installed;
        lock (_gate)
        {
            installed = [.. _records.Values.OrderBy(r => r.Manifest.Id, StringComparer.Ordinal)];
        }

        var updates = new List<PluginUpdate>();
        foreach (var record in installed)
        {
            if (!byId.TryGetValue(record.Manifest.Id, out var item) || !PluginVersions.HasNewerVersion(record.Manifest.Version, item.Version))
            {
                continue;
            }

            updates.Add(new PluginUpdate(
                record.Manifest.Id, item.Slug, record.Manifest.DisplayName, record.Manifest.Version, item.Version!,
                PluginPermissionRules.Added(record.ApprovedRequired, item.Permissions.Where(PluginPermissions.IsValid)), record.Slug is not null));
        }

        return updates;
    }

    /// <summary>
    /// 串行、失败不中断(中途 abort 的话,前面成功的那几个已经落盘了,而返回值里没有它们)。★ 只动<b>市场来源</b>的那些:本地包装的那份可能是用户自己改过的构建。
    /// 同时只跑一个。
    /// </summary>
    public Task<PluginUpdateResult> UpdateAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_updateAll is { IsCompleted: false } running)
            {
                return running;
            }

            var task = TrackUpdateAllAsync(ct);
            _updateAll = task.IsCompleted ? null : task;
            return task;
        }
    }

    private async Task<PluginUpdateResult> TrackUpdateAllAsync(CancellationToken ct)
    {
        try
        {
            return await RunUpdateAllAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _updateAll = null;
            }
        }
    }

    private async Task<PluginUpdateResult> RunUpdateAllAsync(CancellationToken ct)
    {
        var targets = (await CheckUpdatesAsync(force: true, ct).ConfigureAwait(false)).Where(u => u.FromMarket).ToList();
        var updated = new List<string>();
        var failed = new List<PluginUpdateFailure>();
        foreach (var target in targets)
        {
            try
            {
                await InstallMarketAsync(target.Slug, target.LatestVersion, null, ct).ConfigureAwait(false);
                updated.Add(target.PluginId);
            }
            catch (PluginException e)
            {
                failed.Add(new PluginUpdateFailure(target.PluginId, e.Key));
            }
        }

        _marketCache = null; // 更新完之后下一次检查按新状态重新比
        return new PluginUpdateResult(updated, failed);
    }
}
