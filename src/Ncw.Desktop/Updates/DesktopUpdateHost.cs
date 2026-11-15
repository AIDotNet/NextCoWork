using Ncw.Client.Updates;

namespace Ncw.Desktop.Updates;

/// <summary>
/// 桌面端的更新重启宿主(docs/design/04 §3「sidecar 和客户端一起更新」):先让 ncw-agent 优雅收尾(关 stdin → SIGTERM → 强杀,
/// 不管「关窗后继续运行」开没开),再走应用自己的退出流程。数据库迁移不在这里:新版本的 Agent 启动时自己做(04 §4 步骤 6)。
/// 更新器只等本进程退出 60 秒,所以退出有兜底:请求退出后 <paramref name="exitFallback"/> 内进程还没走,就硬退出(此时 sidecar 已经收尾,没有数据风险)。
/// </summary>
public sealed class DesktopUpdateHost(Func<LocalAgentSidecar?> sidecar, Action requestExit, TimeProvider? clock = null, TimeSpan? exitFallback = null,
    Action<int>? hardExit = null, Action<string>? log = null) : IUpdateHost
{
    private ITimer? _fallback;

    public async Task PrepareForRestartAsync(CancellationToken ct)
    {
        if (sidecar() is { } agent)
        {
            log?.Invoke("update: stopping the local agent before the restart");
            await agent.StopForUpdateAsync().ConfigureAwait(false);
        }
    }

    public void RequestExit()
    {
        var wait = exitFallback ?? TimeSpan.FromSeconds(20);
        _fallback = (clock ?? TimeProvider.System).CreateTimer(_ =>
        {
            log?.Invoke("update: the app did not exit in time; exiting now so the updater can take over");
            (hardExit ?? Environment.Exit)(0);
        }, null, wait, Timeout.InfiniteTimeSpan);
        requestExit();
    }
}
