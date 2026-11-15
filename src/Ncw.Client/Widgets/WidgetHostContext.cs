using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;

namespace Ncw.Client.Widgets;

/// <summary>
/// widget 卡片需要的外部能力的打包(由 <c>ShellViewModel</c> 装配,转录里的 <see cref="ViewModels.WidgetRowViewModel"/> 拿它造桥):意图(<see cref="WidgetActions"/>)、镜像(<see cref="Stores"/>)、
/// 外链打开(<see cref="IUrlLauncher"/>)、确认框(<see cref="IWidgetConfirmations"/>,界面层按当前语言实现)。launcher / 确认框用 <c>Func</c> 取:界面在装配之后才把真实现换进来。
/// </summary>
public sealed class WidgetHostContext(ClientStores stores, WidgetActions actions, Func<IUrlLauncher> launcher, Func<IWidgetConfirmations> confirmations, TimeProvider? clock = null)
{
    public ClientStores Stores { get; } = stores;

    public WidgetActions Actions { get; } = actions;

    public TimeProvider Clock { get; } = clock ?? TimeProvider.System;

    public IWidgetConfirmations Confirmations => confirmations();

    public IUrlLauncher Launcher => launcher();

    /// <summary>为一张卡造桥。<paramref name="post"/> 是把消息 JSON 送进页面的出口(控制层包成 <c>InvokeScript</c>);<paramref name="context"/> 每次现读这张卡此刻的事实。</summary>
    public WidgetBridge CreateBridge(WidgetRef card, Func<WidgetBridgeContext> context, Action<string> post) =>
        new(card, Actions, new DeferredConfirmations(this), () => Launcher, context, post, Clock);

    /// <summary>确认框在调用时才取(界面可能在桥造好之后才换上真实现)。</summary>
    private sealed class DeferredConfirmations(WidgetHostContext owner) : IWidgetConfirmations
    {
        public Task<bool> ConfirmOpenUrlAsync(string url) => owner.Confirmations.ConfirmOpenUrlAsync(url);

        public Task<bool> ConfirmActionAsync(WidgetActionPrompt prompt) => owner.Confirmations.ConfirmActionAsync(prompt);
    }
}
