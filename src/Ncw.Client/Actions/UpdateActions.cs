using Ncw.Client.Updates;

namespace Ncw.Client.Actions;

/// <summary>
/// 自更新的用户意图(规则 4:同一个意图只有一个方法;设置页的「关于」、主界面的阻断提示、将来的托盘菜单都调这里):
/// 检查更新、下载、立即重启并更新、换通道。没有更新服务(手机、预览、测试)时全部是空操作,<see cref="IsAvailable"/> 为 false。
/// 事实在 <see cref="Stores.ClientStores.Updates"/>,这里不返回状态。
/// </summary>
public sealed class UpdateActions(IUpdateService? service)
{
    public bool IsAvailable => service is not null;

    public Task CheckAsync(CancellationToken ct = default) => service?.CheckAsync(ct) ?? Task.CompletedTask;

    public Task DownloadAsync(CancellationToken ct = default) => service?.DownloadAsync(ct) ?? Task.CompletedTask;

    /// <summary>true = 更新器已就位,应用正在退出。</summary>
    public Task<bool> RestartAndApplyAsync(CancellationToken ct = default) => service?.RestartAndApplyAsync(ct) ?? Task.FromResult(false);

    public Task<bool> SetChannelAsync(UpdateChannel channel, CancellationToken ct = default) => service?.SetChannelAsync(channel, ct) ?? Task.FromResult(false);
}
