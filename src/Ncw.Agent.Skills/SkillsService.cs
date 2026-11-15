using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Loop;
using Ncw.Agent.Skills.Install;
using Ncw.Core.Domain;

namespace Ncw.Agent.Skills;

/// <summary>设置页 / 扩展面板里那张清单的一行(2.x 的 <c>SkillListItem</c>)。<see cref="SourcePath"/> 是 Skill 目录(执行环境里的路径)。</summary>
public sealed record SkillListItem(
    string Id,
    string Name,
    string Description,
    string Category,
    SkillSourceKind SourceKind,
    SkillScope Scope,
    string? PluginId,
    bool GlobalEnabled,
    bool ActiveInWorkspace,
    bool ClientAssetsOnly,
    string? Version,
    string? Sha256,
    string SourcePath,
    int UsageCount,
    long LastUsedAt);

/// <summary>安装进度(市场安装 / zip 安装共用):<c>preparing</c> → <c>downloading</c> → <c>installing</c> → <c>done</c> / <c>failed</c>。</summary>
public sealed record SkillInstallProgress(string Phase, long? Received = null, long? Total = null, string? ErrorKey = null);

/// <summary>
/// Skills 域的服务(2.x 的 <c>ipc/skills.ts</c> 去掉 electron 之后):磁盘上的目录和界面上那张清单之间的接线员。
/// 扫描与校验在 <see cref="SkillLoader"/>,开关在 <see cref="ISkillStateStore"/>,这里做三件事:触发扫描、把两份状态合成一份下发、写完之后通知(<see cref="Changed"/>)。
/// <para>
/// <b>两个开关是两件不同的事</b>:<c>globalEnabled</c> = 用户在设置页整个关掉了这条(存被关掉的那些);<c>activeInWorkspace</c> = 这个工作区选装了哪几条(空清单 = 全都要)。
/// 合起来才是「这一轮下发哪几条」,合并规则只有 <see cref="SkillCatalog.IsActive"/> 一份。
/// </para>
/// ★ <see cref="ListAsync"/> <b>每次都重扫</b>:用户刚往 skills/ 里拖了一个目录就来看这个列表,「关掉设置页再打开」并不会重扫——那样他会以为装失败了。
/// </summary>
public sealed class SkillsService(
    ISkillLocations locations,
    ISkillStateStore state,
    ISkillUsage? usage = null,
    ISkillMarketClient? market = null,
    ILoopDiagnostics? log = null)
{
    private readonly ILoopDiagnostics _log = log ?? NullLoopDiagnostics.Instance;

    public ISkillUsage Usage { get; } = usage ?? NullSkillUsage.Instance;

    /// <summary>安装 / 卸载 / 开关变了(Host 据此把「扩展」主题的修订号加一,别的控制端重拉)。</summary>
    public event Action? Changed;

    public SkillsExtension CreateExtension(string workspaceId) => new(this, workspaceId);

    // ───────────────────────── 扫描 ─────────────────────────

    public async Task<SkillScanResult> ScanAsync(string? workspaceId, CancellationToken ct = default) =>
        await SkillLoader.ScanAsync(await locations.ScanInputAsync(workspaceId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>每个 Run 开始时调:扫 → 合并 → 快照。</summary>
    public async Task<SkillSnapshot> SnapshotAsync(string workspaceId, CancellationToken ct = default)
    {
        var scan = await ScanAsync(workspaceId, ct).ConfigureAwait(false);
        foreach (var diagnostic in scan.Diagnostics)
        {
            _log.Warn($"[skill] {diagnostic.Path}: {diagnostic.Message}");
        }

        var catalog = new SkillCatalog(scan.Skills, scan.Diagnostics);
        var disabled = state.DisabledGlobally();
        var selection = state.Selection(workspaceId);
        return new SkillSnapshot(
            [.. catalog.Skills.Where(s => SkillCatalog.IsActive(s, disabled, selection))],
            new HashSet<string>(catalog.Skills.Where(s => s.ClientAssetsOnly && !disabled.Contains(s.Id)).Select(s => s.Name), StringComparer.Ordinal));
    }

    // ───────────────────────── 清单 ─────────────────────────

    public async Task<IReadOnlyList<SkillListItem>> ListAsync(string? workspaceId, CancellationToken ct = default)
    {
        var scan = await ScanAsync(workspaceId, ct).ConfigureAwait(false);
        return Items(new SkillCatalog(scan.Skills, scan.Diagnostics), workspaceId);
    }

    public async Task<IReadOnlyList<SkillDiagnostic>> DiagnosticsAsync(string? workspaceId, CancellationToken ct = default) =>
        (await ScanAsync(workspaceId, ct).ConfigureAwait(false)).Diagnostics;

    private IReadOnlyList<SkillListItem> Items(SkillCatalog catalog, string? workspaceId)
    {
        var disabled = state.DisabledGlobally();
        var selection = workspaceId is null ? null : state.Selection(workspaceId);
        var stats = Usage.Usage(workspaceId);
        return
        [
            .. catalog.Skills.Select(s =>
            {
                var stat = stats.GetValueOrDefault(s.Id);
                return new SkillListItem(
                    s.Id, s.Name, s.Description, s.Category, s.Source.Kind, s.Scope, s.Source.PluginId,
                    !disabled.Contains(s.Id),
                    // 空清单 = 全都要,所以此时每一条显示的都是「已启用」;没指定工作区(全局设置页)不谈「在这个工作区激活」
                    SkillSelection.IsActiveInWorkspace(s.Id, s.ClientAssetsOnly, selection?.Ids, selection?.Explicit ?? false),
                    s.ClientAssetsOnly, s.Source.Version, s.Source.Sha256, s.Directory, stat?.Count ?? 0, stat?.LastUsedAt ?? 0);
            }),
        ];
    }

    // ───────────────────────── 开关 ─────────────────────────

    public void SetGlobalEnabled(string skillId, bool enabled)
    {
        state.SetGlobalEnabled(skillId, enabled);
        Changed?.Invoke();
    }

    /// <summary>
    /// 在某个工作区里选装 / 取消选装一条。
    /// ★ 空清单 = 全都要这个语义带来一处必须显式处理的不对称:空清单时把某一条关掉,不能只是「从空清单里删掉它」(空操作);得先把当前所有 Skill 的 id 铺开再去掉这一条,
    /// 即把隐式的「全都要」物化成显式清单。代价是这之后新装的 Skill 在这个工作区默认不生效——对的:用户一旦手动选装过,「我选的就是我要的」比「悄悄给你加一条」更符合预期。
    /// ★ 权威闸门在这里:依赖设备本机资源的 Skill 在 SSH 工作区里跑不了,不拦的话 <c>activeSkillIds</c> 里会留下一条永远不会生效的 id,界面显示「已启用」,模型那边却被静默排除。
    /// </summary>
    public async Task SetWorkspaceActiveAsync(string skillId, string workspaceId, bool active, CancellationToken ct = default)
    {
        var scan = await ScanAsync(workspaceId, ct).ConfigureAwait(false);
        var current = state.Selection(workspaceId)
            ?? throw new SkillException(SkillErrorKeys.WorkspaceNotFound, $"There is no workspace \"{workspaceId}\".");
        var skill = scan.Skills.FirstOrDefault(s => s.Id == skillId);
        if (active && skill is null)
        {
            throw new SkillException(SkillErrorKeys.NotFound, $"There is no Skill \"{skillId}\".");
        }

        if (active && skill!.ClientAssetsOnly)
        {
            throw new SkillException(SkillErrorKeys.ClientAssetsUnavailable, "This Skill needs files that exist only on the device that stores it.");
        }

        var next = SkillSelection.NextActiveIds(current.Ids, current.Explicit, [.. scan.Skills.Select(s => s.Id)], skillId, active);
        state.SetSelection(workspaceId, next, explicitSelection: true);
        Changed?.Invoke();
    }

    // ───────────────────────── 安装 / 卸载 ─────────────────────────

    /// <summary>从 zip 字节安装(文件选择框是客户端本地的,这里只收字节)。大小上限由调用方按承载决定(协议帧 4MB → <see cref="SkillLimits.MaxWireZipBytes"/>)。</summary>
    public async Task<SkillListItem> InstallZipAsync(
        ReadOnlyMemory<byte> zip, SkillInstallScope scope, string? workspaceId, string? fileName = null, IProgress<SkillInstallProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new SkillInstallProgress("installing"));
        try
        {
            var installed = await InstallPackageAsync(zip, scope, workspaceId, null, fileName, ct).ConfigureAwait(false);
            var item = await FoundAsync(installed, workspaceId, ct).ConfigureAwait(false);
            progress?.Report(new SkillInstallProgress("done"));
            Changed?.Invoke();
            return item;
        }
        catch (SkillException e)
        {
            // 终态一定要推出去,成功失败都是(别的控制端手上只有事件)
            progress?.Report(new SkillInstallProgress("failed", ErrorKey: e.Key));
            throw;
        }
    }

    /// <summary>
    /// 从市场安装:详情(版本)→ 安装授权(权威摘要,需要账户令牌)→ 下载(≤ 20MB,带节流的进度)→ 校验摘要 → 安装。
    /// 授权、问详情、要摘要加起来常常比下载还久而且报不出百分比,所以这一档的进度没有 <c>Total</c>(界面据此画滚动斜纹,而不是一条停在 0% 的条子)。
    /// </summary>
    public async Task<SkillListItem> InstallMarketAsync(
        string slug, string? version, SkillInstallScope scope, string? workspaceId, IProgress<SkillInstallProgress>? progress = null, CancellationToken ct = default)
    {
        var client = market ?? throw new SkillException(SkillErrorKeys.NetworkFailed, "No market client is configured on this device.");
        try
        {
            progress?.Report(new SkillInstallProgress("preparing"));
            var detail = await client.DetailAsync(slug, ct).ConfigureAwait(false);
            var chosen = version ?? detail.Item.Version ?? detail.Versions.FirstOrDefault()?.Version
                ?? throw new SkillException(SkillErrorKeys.NoVersion, "The market has no installable version.");
            var grant = await client.GrantAsync(slug, chosen, ct).ConfigureAwait(false);
            var declared = detail.Versions.FirstOrDefault(v => v.Version == chosen)?.FileSize;
            var bytes = await client.DownloadAsync(slug, chosen, SkillLimits.MaxZipBytes, p =>
                progress?.Report(new SkillInstallProgress("downloading", p.Received, p.Total ?? declared)), ct).ConfigureAwait(false);
            progress?.Report(new SkillInstallProgress("installing"));
            var installed = await InstallPackageAsync(bytes, scope, workspaceId, grant.Sha256, $"skill-v{chosen}.zip", ct).ConfigureAwait(false);
            var item = await FoundAsync(installed, workspaceId, ct).ConfigureAwait(false);
            progress?.Report(new SkillInstallProgress("done"));
            Changed?.Invoke();
            return item;
        }
        catch (SkillException e)
        {
            progress?.Report(new SkillInstallProgress("failed", ErrorKey: e.Key));
            throw;
        }
    }

    private async Task<InstalledSkill> InstallPackageAsync(
        ReadOnlyMemory<byte> zip, SkillInstallScope scope, string? workspaceId, string? expectedSha256, string? fileName, CancellationToken ct)
    {
        var target = await locations.InstallRootAsync(scope, workspaceId, ct).ConfigureAwait(false);
        return await SkillZipInstaller.InstallAsync(zip, target.Environment, target.Root, expectedSha256, fileName, ct).ConfigureAwait(false);
    }

    private async Task<SkillListItem> FoundAsync(InstalledSkill installed, string? workspaceId, CancellationToken ct) =>
        (await ListAsync(workspaceId, ct).ConfigureAwait(false)).FirstOrDefault(i => i.Name == installed.Name)
        ?? throw new SkillException(SkillErrorKeys.NotLoaded, "The Skill could not be loaded after installing.");

    /// <summary>
    /// 卸载。只删安装根(全局 <c>skills/</c> 或项目 <c>.next-cowork/skills/</c>)之内的目录:解析真实路径后做包含判断,根本身和根之外的一律拒绝;插件带来的没有卸载入口(文件在插件包里,删掉就破坏了包的完整性)。
    /// </summary>
    public async Task UninstallAsync(string skillId, SkillInstallScope? scope, string? workspaceId, CancellationToken ct = default)
    {
        var item = (await ListAsync(workspaceId, ct).ConfigureAwait(false)).FirstOrDefault(i => i.Id == skillId)
            ?? throw new SkillException(SkillErrorKeys.NotFound, $"There is no Skill \"{skillId}\".");
        if (item.Scope == SkillScope.Plugin)
        {
            throw new SkillException(SkillErrorKeys.InvalidLocation, "A plugin's Skill is removed by disabling or uninstalling the plugin.");
        }

        var effective = scope ?? (item.Scope == SkillScope.Project ? SkillInstallScope.Project : SkillInstallScope.Global);
        var location = await locations.InstallRootAsync(effective, workspaceId, ct).ConfigureAwait(false);
        var fs = location.Environment.Fs;
        EnvPath directory;
        EnvPath root;
        try
        {
            directory = await fs.RealPathAsync(EnvPath.Parse(item.SourcePath, location.Environment.Facts.PathStyle), ct).ConfigureAwait(false);
            root = await fs.RealPathAsync(location.Root, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.NotFound)
        {
            throw new SkillException(SkillErrorKeys.InvalidLocation, "The Skill's install location was not found.", null, e);
        }

        // 根本身、根之外的一律拒绝(realpath 之后比,所以软链指到外面的删不到外面)
        if (directory.Value == root.Value || !directory.IsWithin(root))
        {
            throw new SkillException(SkillErrorKeys.InvalidLocation, "The Skill's install location is not inside the skills directory.");
        }

        try
        {
            await fs.DeleteDirectoryAsync(directory, recursive: true, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.PermissionDenied)
        {
            throw new SkillException(SkillErrorKeys.DiskPermission, "The install location is not writable.", null, e);
        }

        Changed?.Invoke();
    }

    // ───────────────────────── 市场 ─────────────────────────

    private ISkillMarketClient Market => market ?? throw new SkillException(SkillErrorKeys.NetworkFailed, "No market client is configured on this device.");

    public Task<IReadOnlyList<SkillMarketItem>> MarketListAsync(string? query, string? category, CancellationToken ct = default) =>
        Market.ListAsync(query, category, ct);

    public Task<IReadOnlyList<string>> MarketCategoriesAsync(CancellationToken ct = default) => Market.CategoriesAsync(ct);

    public Task<SkillMarketDetail> MarketDetailAsync(string slug, CancellationToken ct = default) => Market.DetailAsync(slug, ct);
}
