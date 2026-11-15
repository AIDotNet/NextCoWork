using Ncw.Client.Devices;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 插件相关的用户意图(规则 4:一个意图一个方法)。真源在设备的 <c>PluginManager</c>;控制端按 <c>deviceId</c> 镜像全量状态(<see cref="PluginsMirror"/>,<c>plugins</c> 主题 + 每个变更方法返回的完整状态,
/// 同一个类型、同一个折叠函数,所以谁先到都不会写出旧状态)。
/// 写入类(装 / 启停 / 授权 / 卸载 / 改配置 / 更新)需要 owner(在设备上装并运行第三方代码、决定它能碰什么);读取、市场浏览、运行命令、卡片动作、回答插件的提问需要 member。
/// 失败的结构化原因在 <see cref="ActionResult{T}.ErrorKey"/>(<c>plugins.*</c>),界面自己生成文字;市场安装没有账户令牌 = <c>plugins.authRequired</c>。
/// 插件的界面类能力(自定义编辑器、网页应用、React 视图)3.0 不做,这里没有对应的意图。<c>pickPackage</c>(文件选择框)是客户端本地的 UI,这里只收字节。
/// </summary>
public sealed class PluginsActions(DeviceRegistry devices, Store<PluginsMirror> mirror) : IPluginsCommands
{
    /// <summary>主动拉一份全量状态并合进镜像(平时靠 <c>plugins</c> 主题;订阅前 / 对账时用)。member。</summary>
    public async Task<ActionResult<PluginsTopicState>> ListAsync(string deviceId, CancellationToken ct = default) =>
        Merge(deviceId, await Run(deviceId, link => link.CallAsync(PluginsMethods.List, Unit.Value, ct)).ConfigureAwait(false));

    /// <summary>启用 / 禁用(owner)。必选能力没批 = 状态 <c>pending-approval</c>,装载失败的不能启用(<c>plugins.notRunnable</c>)。</summary>
    public async Task<ActionResult<PluginsTopicState>> SetEnabledAsync(string deviceId, string pluginId, bool enabled, CancellationToken ct = default) =>
        Merge(deviceId, await Run(deviceId, link => link.CallAsync(PluginsMethods.SetEnabled, new PluginsSetEnabledParams(pluginId, enabled), ct, idem: Idem("setEnabled"))).ConfigureAwait(false));

    /// <summary>卸载(owner):设备上先下线再删包、状态、配置、私有存储、密钥。</summary>
    public async Task<ActionResult<PluginsTopicState>> UninstallAsync(string deviceId, string pluginId, CancellationToken ct = default) =>
        Merge(deviceId, await Run(deviceId, link => link.CallAsync(PluginsMethods.Uninstall, new PluginsUninstallParams(pluginId), ct, idem: Idem("uninstall"))).ConfigureAwait(false));

    /// <summary>
    /// 从 zip 字节安装(owner)。zip 超过一个请求能带的大小(<see cref="PluginsLimits.MaxZipBytes"/>)时<b>不发请求</b>,直接得到 <c>plugins.packageTooLarge</c>(更大的包走市场安装,设备自己下载)。
    /// 新装的插件是禁用的、没有授权:界面让用户逐条勾选能力(<see cref="GrantAsync"/>)再启用。
    /// </summary>
    public async Task<ActionResult<PluginsInstallResult>> InstallPackageAsync(string deviceId, byte[] zip, string? fileName, CancellationToken ct = default)
    {
        if (zip.Length == 0 || zip.Length > PluginsLimits.MaxZipBytes)
        {
            return new ActionResult<PluginsInstallResult>(ActionOutcome.Invalid, ErrorKey: "plugins.packageTooLarge", Detail: "The ZIP is empty or larger than a request can carry.");
        }

        return MergeInstall(deviceId, await Run(deviceId, link => link.CallAsync(PluginsMethods.InstallPackage, new PluginsInstallPackageParams(zip, fileName), ct, idem: Idem("installPackage"))).ConfigureAwait(false));
    }

    /// <summary>从市场安装(owner,设备自己下载,≤ 20MB)。没有账户令牌 = <c>plugins.authRequired</c>。长调用,没有进度主题:结果带终态与 key。</summary>
    public async Task<ActionResult<PluginsInstallResult>> InstallMarketAsync(string deviceId, string slug, string? version, CancellationToken ct = default) =>
        MergeInstall(deviceId, await Run(deviceId, link => link.CallAsync(PluginsMethods.InstallMarket, new PluginsInstallMarketParams(slug, version), ct, idem: Idem("installMarket"))).ConfigureAwait(false));

    /// <summary>授予能力(owner)。越过清单上界的被设备静默丢弃。</summary>
    public async Task<ActionResult<PluginsTopicState>> GrantAsync(string deviceId, string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default) =>
        Merge(deviceId, await Run(deviceId, link => link.CallAsync(PluginsMethods.GrantPermissions, new PluginsPermissionsParams(pluginId, [.. permissions]), ct, idem: Idem("grant"))).ConfigureAwait(false));

    /// <summary>撤销能力(owner)。撤掉必选能力 = 插件停下来;对正在跑的插件立刻生效。</summary>
    public async Task<ActionResult<PluginsTopicState>> RevokeAsync(string deviceId, string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default) =>
        Merge(deviceId, await Run(deviceId, link => link.CallAsync(PluginsMethods.RevokePermissions, new PluginsPermissionsParams(pluginId, [.. permissions]), ct, idem: Idem("revoke"))).ConfigureAwait(false));

    /// <summary>最近的宿主调用(最近的在前,不含参数原文;member)。结果交给界面,不进镜像。</summary>
    public Task<ActionResult<PluginsActivityResult>> ActivityAsync(string deviceId, string? pluginId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.Activity, new PluginsActivityParams(pluginId), ct));

    /// <summary>
    /// 执行插件贡献的命令(member;菜单 / 命令面板 / 斜杠命令)。<paramref name="workspaceId"/> = 菜单在哪个项目里点的(命令里的文件 / 命令类能力以它为根);
    /// <paramref name="argsJson"/> 是附加参数(JSON 对象文本)。失败:插件自己 throw = <c>plugins.commandFailed</c>,超预算 = <c>plugins.faulted</c>。
    /// </summary>
    public Task<ActionResult<Unit>> RunCommandAsync(string deviceId, string pluginId, string commandId, string? workspaceId = null, string? argsJson = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.RunCommand, new PluginsRunCommandParams(pluginId, commandId, argsJson, workspaceId), ct, idem: Idem("runCommand")));

    /// <summary>用户点了实时工具卡片上的按钮(member)。即发即忘:送不到(工具已结束)是 <see cref="PluginsCardActionResult.Delivered"/> = false,不是错误。</summary>
    public Task<ActionResult<PluginsCardActionResult>> CardActionAsync(string deviceId, string pluginId, string callId, string actionId, string? valueJson = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.CardAction, new PluginsCardActionParams(pluginId, callId, actionId, valueJson), ct, idem: Idem("cardAction")));

    /// <summary>
    /// 回答插件的一个提问(member)。取消 = 三个字段都缺省。回答之后设备推送新状态(提问从 <see cref="DevicePlugins.Interactions"/> 里消失);
    /// 请求已经不存在(超时 / 被撤销 / 别的控制端先回答)是 <c>plugins.interactionNotFound</c>——竞态,不是故障。
    /// </summary>
    public Task<ActionResult<Unit>> ReplyInteractionAsync(string deviceId, string requestId, bool? confirmed = null, string? text = null, string? pickedId = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.InteractionReply, new PluginsInteractionReplyParams(requestId, confirmed, text, pickedId), ct, idem: Idem("interactionReply")));

    /// <summary>市场目录(设备代为联网,member 起)。结果交给界面,不进镜像。</summary>
    public Task<ActionResult<PluginsMarketListResult>> MarketListAsync(string deviceId, string? query, string? category, int page = 1, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.MarketList, new PluginsMarketListParams(query, category, page), ct));

    public Task<ActionResult<PluginsMarketCategoriesResult>> MarketCategoriesAsync(string deviceId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.MarketCategories, Unit.Value, ct));

    public Task<ActionResult<PluginMarketDetailDto>> MarketDetailAsync(string deviceId, string slug, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.MarketDetail, new PluginsMarketDetailParams(slug), ct));

    /// <summary>已装插件有没有新版(把市场整张表翻完,设备缓存 10 分钟;member)。新增的必选能力逐条列在每条里。</summary>
    public Task<ActionResult<PluginsUpdatesResult>> CheckUpdatesAsync(string deviceId, bool force = false, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.CheckUpdates, new PluginsCheckUpdatesParams(force), ct));

    /// <summary>更新所有市场来源的插件(owner,串行,失败不中断)。更新后的状态经 <c>plugins</c> 主题到达镜像。</summary>
    public Task<ActionResult<PluginsUpdateAllResult>> UpdateAllAsync(string deviceId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.UpdateAll, Unit.Value, ct, idem: Idem("updateAll")));

    /// <summary>用户在详情页里拨的那些开关合上清单里的默认值(member)。</summary>
    public Task<ActionResult<PluginsConfigurationResult>> GetConfigurationAsync(string deviceId, string pluginId, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.GetConfiguration, new PluginsConfigurationParams(pluginId), ct));

    /// <summary>改一项配置(owner;<paramref name="value"/> 为 null = 恢复默认)。类型对不上 / 键不在清单里是 <c>plugins.configurationInvalid</c>。</summary>
    public Task<ActionResult<PluginsConfigurationResult>> SetConfigurationAsync(string deviceId, string pluginId, string key, PluginConfigValueDto? value, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(PluginsMethods.SetConfiguration, new PluginsSetConfigurationParams(pluginId, key, value), ct, idem: Idem("setConfiguration")));

    private ActionResult<PluginsTopicState> Merge(string deviceId, ActionResult<PluginsTopicState> result)
    {
        if (result.IsOk)
        {
            var state = result.Value!;
            Post(deviceId, () => mirror.Update(m => PluginsReducer.ApplyTopic(m, deviceId, state)));
        }

        return result;
    }

    private ActionResult<PluginsInstallResult> MergeInstall(string deviceId, ActionResult<PluginsInstallResult> result)
    {
        if (result.IsOk)
        {
            var state = result.Value!.State;
            Post(deviceId, () => mirror.Update(m => PluginsReducer.ApplyTopic(m, deviceId, state)));
        }

        return result;
    }

    /// <summary>Store 只在 UI 线程上读写:经这台设备连接的调度器投递。</summary>
    private void Post(string deviceId, Action change) => devices.Find(deviceId)?.PostToUi(change);

    private static string Idem(string what) => $"plugins.{what}:{Guid.NewGuid():N}";

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
