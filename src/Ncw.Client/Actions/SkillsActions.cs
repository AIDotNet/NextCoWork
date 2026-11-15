using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// Skills 相关的用户意图(AGENTS.md §3 规则 4:一个意图一个方法)。Skill 的真源是设备上的目录和设置;控制端按 <c>(deviceId, workspaceId)</c> 镜像清单(<see cref="SkillsMirror"/>)。
/// 读取和<b>每一次变更的返回值</b>(设备返回变更之后的完整清单)都合进镜像;全局层的变更(全局启停、全局安装 / 卸载)影响这台设备上持有的每一份清单,所以变更之后重拉其余的。
/// 其它控制端的改动靠 <c>extensions</c> 主题的 <c>SkillsRev</c> 变化触发 <see cref="RefreshHeldAsync"/>。
/// 写入类(全局启停 / 安装 / 卸载)需要 owner,选装 member,viewer 得到 <see cref="ActionOutcome.Forbidden"/>。市场浏览的结果直接交给界面(纯 UI 状态),不进镜像。
/// <c>pickZip</c>(文件选择框)是客户端本地的 UI,这里只收字节。
/// </summary>
public sealed class SkillsActions(DeviceRegistry devices, Store<SkillsMirror> mirror) : ISkillsCommands
{
    private long _seq;

    /// <summary>某个项目看到的 Skill 清单(<paramref name="workspaceId"/> 为 null 只有全局层,不谈「在这个工作区激活」),合进镜像。viewer 可用。</summary>
    public async Task<ActionResult<SkillsListResult>> ListAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(SkillsMethods.List, new SkillsScopeParams(workspaceId), ct)).ConfigureAwait(false);
        Merge(deviceId, workspaceId, result, seq);
        return result;
    }

    /// <summary>加载诊断(哪个目录为什么被跳过,带稳定 key),合进镜像。viewer 可用。</summary>
    public async Task<ActionResult<SkillsDiagnosticsResult>> DiagnosticsAsync(string deviceId, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(SkillsMethods.Diagnostics, new SkillsScopeParams(workspaceId), ct)).ConfigureAwait(false);
        if (result.IsOk)
        {
            Post(deviceId, () => mirror.Update(m => SkillsReducer.ApplyDiagnostics(m, deviceId, workspaceId, result.Value!, seq)));
        }

        return result;
    }

    /// <summary>全局启停一条(设备级偏好,owner)。<paramref name="workspaceId"/> 只用来算返回的清单。</summary>
    public async Task<ActionResult<SkillsListResult>> SetGlobalEnabledAsync(string deviceId, string skillId, bool enabled, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(SkillsMethods.SetGlobalEnabled, new SkillsSetGlobalEnabledParams(skillId, enabled, workspaceId), ct, idem: Idem("setGlobalEnabled"))).ConfigureAwait(false);
        await AfterChangeAsync(deviceId, workspaceId, result, seq, global: true, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 在一个工作区里选装 / 取消选装一条(member)。设备上有权威闸门:依赖设备本机资源、在 SSH 工作区里跑不了的 Skill 不许启用
    /// (<see cref="ActionOutcome.Invalid"/> + <c>skills.clientAssetsUnavailable</c>),关掉始终可行。
    /// </summary>
    public async Task<ActionResult<SkillsListResult>> SetWorkspaceActiveAsync(string deviceId, string workspaceId, string skillId, bool active, CancellationToken ct = default)
    {
        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(SkillsMethods.SetWorkspaceActive, new SkillsSetWorkspaceActiveParams(skillId, workspaceId, active), ct, idem: Idem("setWorkspaceActive"))).ConfigureAwait(false);
        Merge(deviceId, workspaceId, result, seq);
        return result;
    }

    /// <summary>
    /// 从 zip 字节安装(owner)。zip 超过一个请求能带的大小(<see cref="SkillsLimits.MaxZipBytes"/>)时<b>不发请求</b>,直接得到 <c>skills.packageTooLarge</c>
    /// (更大的包走市场安装,设备自己下载)。项目作用域要 <paramref name="workspaceId"/>。
    /// </summary>
    public async Task<ActionResult<SkillsInstallResult>> InstallZipAsync(string deviceId, byte[] zip, string? fileName, string scope, string? workspaceId = null, CancellationToken ct = default)
    {
        if (zip.Length == 0 || zip.Length > SkillsLimits.MaxZipBytes)
        {
            return new ActionResult<SkillsInstallResult>(ActionOutcome.Invalid, ErrorKey: "skills.packageTooLarge", Detail: "The ZIP is empty or larger than a request can carry.");
        }

        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(SkillsMethods.InstallZip, new SkillsInstallZipParams(zip, fileName, scope, workspaceId), ct, idem: Idem("installZip"))).ConfigureAwait(false);
        await AfterInstallAsync(deviceId, workspaceId, scope, result, seq, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// 从市场安装(owner,设备自己下载,≤ 20MB)。设备需要账户令牌才能向市场要安装授权;没有时 <see cref="ActionResult{T}.ErrorKey"/> = <c>skills.authRequired</c>。
    /// 这是一个长调用(下载 + 解压),没有进度主题:结果带终态与 key。
    /// </summary>
    public async Task<ActionResult<SkillsInstallResult>> InstallMarketAsync(string deviceId, string slug, string? version, string scope, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(SkillsMethods.InstallMarket, new SkillsInstallMarketParams(slug, version, scope, workspaceId), ct, idem: Idem("installMarket"))).ConfigureAwait(false);
        await AfterInstallAsync(deviceId, workspaceId, scope, result, seq, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>卸载(owner)。<paramref name="scope"/> 为 null = 按这条 Skill 实际所在的层;插件带来的 Skill 没有卸载入口(设备拒绝)。</summary>
    public async Task<ActionResult<SkillsListResult>> UninstallAsync(string deviceId, string skillId, string? scope = null, string? workspaceId = null, CancellationToken ct = default)
    {
        var seq = Next();
        var result = await Run(deviceId, link => link.CallAsync(SkillsMethods.Uninstall, new SkillsUninstallParams(skillId, scope, workspaceId), ct, idem: Idem("uninstall"))).ConfigureAwait(false);
        await AfterChangeAsync(deviceId, workspaceId, result, seq, global: scope != SkillScopes.Project, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>市场目录(设备代为联网,member 起)。结果交给界面,不进镜像。</summary>
    public Task<ActionResult<SkillsMarketListResult>> MarketListAsync(string deviceId, string? query, string? category, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(SkillsMethods.MarketList, new SkillsMarketListParams(query, category), ct));

    public Task<ActionResult<SkillsMarketCategoriesResult>> MarketCategoriesAsync(string deviceId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(SkillsMethods.MarketCategories, Unit.Value, ct));

    public Task<ActionResult<SkillMarketDetailDto>> MarketDetailAsync(string deviceId, string slug, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(SkillsMethods.MarketDetail, new SkillsMarketDetailParams(slug), ct));

    /// <summary>这台设备上镜像里持有的每一份清单都重拉一遍(<c>extensions</c> 主题的 <c>SkillsRev</c> 变了 / 设备重连之后调)。失败的不影响其它。</summary>
    public async Task RefreshHeldAsync(string deviceId, CancellationToken ct = default)
    {
        foreach (var workspaceId in mirror.State.WorkspacesOf(deviceId))
        {
            _ = await ListAsync(deviceId, workspaceId, ct).ConfigureAwait(false);
        }
    }

    private async Task AfterInstallAsync(string deviceId, string? workspaceId, string scope, ActionResult<SkillsInstallResult> result, long seq, CancellationToken ct)
    {
        if (!result.IsOk)
        {
            return;
        }

        var list = new SkillsListResult(result.Value!.Skills);
        Post(deviceId, () => mirror.Update(m => SkillsReducer.ApplyList(m, deviceId, workspaceId, list, seq)));
        if (scope != SkillScopes.Project)
        {
            await RefreshHeldAsync(deviceId, ct).ConfigureAwait(false);
        }
    }

    private async Task AfterChangeAsync(string deviceId, string? workspaceId, ActionResult<SkillsListResult> result, long seq, bool global, CancellationToken ct)
    {
        Merge(deviceId, workspaceId, result, seq);
        if (result.IsOk && global)
        {
            // 全局层的改动影响这台设备上持有的每一份清单:刷新失败不改写入本身的结局,下次打开 / 下次修订号变化时再取
            await RefreshHeldAsync(deviceId, ct).ConfigureAwait(false);
        }
    }

    private void Merge(string deviceId, string? workspaceId, ActionResult<SkillsListResult> result, long seq)
    {
        if (result.IsOk)
        {
            Post(deviceId, () => mirror.Update(m => SkillsReducer.ApplyList(m, deviceId, workspaceId, result.Value!, seq)));
        }
    }

    private long Next() => Interlocked.Increment(ref _seq);

    /// <summary>Store 只在 UI 线程上读写:经这台设备连接的调度器投递。</summary>
    private void Post(string deviceId, Action change) => devices.Find(deviceId)?.PostToUi(change);

    private static string Idem(string what) => $"skills.{what}:{Guid.NewGuid():N}";

    private async Task<ActionResult<T>> Run<T>(string deviceId, Func<DeviceLink, Task<T>> call)
    {
        if (devices.Find(deviceId) is not { IsConnected: true } link)
        {
            return ActionResult<T>.NotConnected();
        }

        try
        {
            return ActionResult<T>.Success(await call(link).ConfigureAwait(false));
        }
        catch (ProtocolException e)
        {
            return ActionResult<T>.From(e);
        }
    }
}
