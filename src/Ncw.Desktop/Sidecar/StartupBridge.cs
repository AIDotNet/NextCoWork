using Ncw.Client.Services;
using Ncw.Core.Domain;

namespace Ncw.Desktop.Sidecar;

/// <summary>
/// 本机 Agent 守护 → 启动事实(docs/design/03 §10)。守护的状态机(<see cref="SidecarStatus"/>)是真源;这里只做结构化的投影:
/// 阶段枚举对阶段枚举,问题枚举对稳定的 kebab-case key(界面按 key 翻译)。守护线程上调用,写入经 <see cref="StartupSignals"/> 切到 UI 线程。
/// </summary>
public static class StartupBridge
{
    public static StartupSidecar Map(SidecarStatus status) => new(
        status.Phase switch
        {
            SidecarPhase.Starting => StartupSidecarPhase.Starting,
            SidecarPhase.Running => StartupSidecarPhase.Running,
            SidecarPhase.Restarting => StartupSidecarPhase.Restarting,
            SidecarPhase.Failed => StartupSidecarPhase.Failed,
            _ => StartupSidecarPhase.Stopped,
        },
        IssueKey(status.Issue),
        status.RestartCount,
        status.RetryIn,
        status.Detail);

    public static string? IssueKey(SidecarIssue issue) => issue switch
    {
        SidecarIssue.AgentNotFound => "agent-not-found",
        SidecarIssue.LaunchFailed => "launch-failed",
        SidecarIssue.AlreadyRunningElsewhere => "already-running-elsewhere",
        SidecarIssue.AgentOutdated => "agent-outdated",
        SidecarIssue.ClientOutdated => "client-outdated",
        SidecarIssue.CrashLoop => "crash-loop",
        SidecarIssue.MasterKeyMissing => "master-key-missing",
        SidecarIssue.MasterKeyUnreadable => "master-key-unreadable",
        _ => null,
    };

    /// <summary>把守护的信号接到启动事实:状态变化 → <c>Sidecar</c>;握手成功(<see cref="SidecarPhase.Running"/>)→ <see cref="StartupMilestone.AgentReady"/>;进程拉起 → <see cref="StartupMilestone.AgentSpawned"/>。</summary>
    public static void Attach(LocalAgentSidecar sidecar, StartupSignals signals)
    {
        sidecar.Spawned += () => signals.Milestone(StartupMilestone.AgentSpawned);
        sidecar.StatusChanged += status =>
        {
            signals.Sidecar(Map(status));
            if (status.Phase == SidecarPhase.Running)
            {
                signals.Milestone(StartupMilestone.AgentReady);
            }
        };
    }
}

/// <summary>启动加载界面的桌面宿主能力:重试(守护从失败 / 退避等待中立刻再来一次)和诊断(守护状态 + agent.log 的尾部)。</summary>
public sealed class SidecarStartupHost(LocalAgentSidecar sidecar, int logTailLines = 30) : IStartupHost
{
    public void Retry()
    {
        // 守护在运行 / 启动中时重试没有意义,而且会留下一个多余的信号(之后一次真正的失败会被它抢先放行)
        if (sidecar.Status.Phase is SidecarPhase.Failed or SidecarPhase.Restarting)
        {
            sidecar.Retry();
        }
    }

    public string? Diagnostics()
    {
        var status = sidecar.Status;
        var text = $"data root: {sidecar.DataRoot}\n";
        text += $"sidecar status: {status.Phase} issue={status.Issue} version={status.AgentVersion ?? "-"} restarts={status.RestartCount}{(status.Detail is null ? "" : " detail=" + status.Detail)}\n";
        try
        {
            var log = new SidecarPaths(sidecar.DataRoot).LogFile;
            if (File.Exists(log))
            {
                var lines = File.ReadAllLines(log);
                text += $"agent.log (last {Math.Min(logTailLines, lines.Length)} lines):\n" + string.Join('\n', lines[Math.Max(0, lines.Length - logTailLines)..]) + "\n";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            text += "agent.log: unreadable\n";
        }

        return text;
    }
}
