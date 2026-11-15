namespace Ncw.Desktop.Sidecar;

/// <summary>守护的时间参数,测试里换成很小的值或配合假时钟。</summary>
public sealed record SidecarTimings
{
    /// <summary>拉起之后等握手成功的上限。AOT 的 Agent 冷启动约 1 秒;首次启动接管 2.x 数据在握手之后进行,不算在这里。</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>等握手时的轮询间隔。</summary>
    public TimeSpan ProbeInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>单次握手的超时。</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>接上已有 Agent 时用的探测超时(没有 Agent 时连接会立刻失败,所以这个值只影响「端点在、但 Agent 卡住」的情形)。</summary>
    public TimeSpan AdoptProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Agent 连续运行这么久才算「稳定」,此前的退出都算连续崩溃,退避递增;稳定之后的退出从头开始计。</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>连续崩溃这么多次之后不再自动重启(<see cref="SidecarIssue.CrashLoop"/>)。</summary>
    public int MaxConsecutiveFailures { get; init; } = 8;

    /// <summary>接上的(不是自己拉起的)Agent 没有进程句柄,每隔这么久握手一次确认它还在。</summary>
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>找不到 Agent 时重新找的间隔(开发时可能还在构建)。</summary>
    public TimeSpan NotFoundRetry { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>被另一个 Agent 占用时重新尝试的间隔。</summary>
    public TimeSpan ConflictRetry { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>关闭 stdin 之后等它自己退出的时间,然后发 SIGTERM。</summary>
    public TimeSpan GracefulStop { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>SIGTERM 之后再等多久,然后强杀。</summary>
    public TimeSpan TerminateWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>退避:第 1 次 1 秒,之后每次翻倍,最多 30 秒。</summary>
    public static TimeSpan Backoff(int consecutiveFailures) =>
        TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 5))));
}
