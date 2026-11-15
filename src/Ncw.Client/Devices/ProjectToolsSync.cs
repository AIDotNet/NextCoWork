using Ncw.Client.Stores;
using Ncw.Protocol.Session;

namespace Ncw.Client.Devices;

/// <summary>
/// 一条连接上的「工作区工具」镜像同步(<see cref="DeviceLink"/> 每次连上后调 <see cref="AttachAsync"/>,断开时调 <see cref="MarkUnknown"/>):
/// git 概览、改动审查变更集、终端会话表和 <c>terminal/&lt;id&gt;</c> 主题的订阅(重连后按游标续传)。每个领域一个分部文件
/// (<c>ProjectToolsSync.Git.cs</c> / <c>.Review.cs</c> / <c>.Terminals.cs</c>);设备不支持某个能力(老设备)就跳过,不报错。
/// 设备的事实只经这里写入这些 Store(AGENTS.md §3 规则 1,key 带 deviceId)。
/// </summary>
internal sealed partial class ProjectToolsSync
{
    private readonly ClientStores _stores;
    private readonly IUiDispatcher _ui;
    private readonly Func<string> _deviceId;

    public ProjectToolsSync(ClientStores stores, IUiDispatcher ui, Func<string> deviceId)
    {
        _stores = stores;
        _ui = ui;
        _deviceId = deviceId;
    }

    public async Task AttachAsync(DeviceClient client, CancellationToken ct)
    {
        await AttachGitAsync(client, ct).ConfigureAwait(false);
        await AttachReviewAsync(client, ct).ConfigureAwait(false);
        await AttachTerminalsAsync(client, ct).ConfigureAwait(false);
    }

    /// <summary>设备断开:这台设备上的 git 概览、变更集、终端状态都标为未知(规则 7),不把旧值当现状,也不判成结束。</summary>
    public void MarkUnknown()
    {
        MarkGitUnknown();
        MarkReviewUnknown();
        MarkTerminalsUnknown();
    }

    /// <summary>一个顶层 Run 收口了(<c>run_settled</c>):它可能改过文件,取变更集(变更集在 Run 结束时才落库)。</summary>
    public void OnRunSettled(DeviceClient? client, string runId) => OnRunSettledReview(client, runId);

    private partial Task AttachGitAsync(DeviceClient client, CancellationToken ct);

    private partial Task AttachReviewAsync(DeviceClient client, CancellationToken ct);

    private partial Task AttachTerminalsAsync(DeviceClient client, CancellationToken ct);

    private partial void MarkGitUnknown();

    private partial void MarkReviewUnknown();

    private partial void MarkTerminalsUnknown();

    private partial void OnRunSettledReview(DeviceClient? client, string runId);
}
