using Ncw.Protocol.Transport;

namespace Ncw.Desktop.Sidecar;

/// <summary>
/// 数据目录与 profile 目录的约定(docs/design/04 §1、§4):
/// <list type="bullet">
/// <item><b>profile 根</b> <c>~/.next-cowork</c>(2.x 的 Electron profile 根;<c>NCW_PROFILE_ROOT</c> 可改)。控制端自己的文件在 <c>client/</c>,Agent 的数据根在它下面。</item>
/// <item><b>数据根</b>:Agent 的库、附件、设置。2.x 数据接管(04 §4)做好之前,桌面端用独立的 <c>agent-dev/</c>,不碰 2.x 的 <c>data/</c>;
/// <c>NCW_AGENT_DATA_ROOT</c> 可改。接管入口(<c>DataGate</c>)在 Agent 里,这里不变。</item>
/// <item><b>run/</b>(数据根下):<c>agent.sock</c>(本机端点)、<c>agent.lock</c>(Agent 的单实例锁)、<c>sidecar.json</c>(桌面端写的:pid、令牌、是否脱离)。</item>
/// <item><b>logs/</b>(数据根下):<c>agent.log</c>(sidecar 的 stdout / stderr,滚动保留一份旧的),给诊断页用(07 §3)。</item>
/// </list>
/// </summary>
public sealed class SidecarPaths(string dataRoot)
{
    public string DataRoot { get; } = Path.GetFullPath(dataRoot);

    public string RunDir => Path.Combine(DataRoot, "run");

    public string StateFile => Path.Combine(RunDir, "sidecar.json");

    public string LogsDir => Path.Combine(DataRoot, "logs");

    public string LogFile => Path.Combine(LogsDir, "agent.log");

    /// <summary>本机通道端点,和 Agent 用同一个函数算,所以两边一致。</summary>
    public string Endpoint => LocalTransport.DefaultEndpoint(DataRoot);

    public static string ProfileRoot(Func<string, string?> env) =>
        env("NCW_PROFILE_ROOT") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".next-cowork");

    public static string DefaultDataRoot(Func<string, string?> env) =>
        env("NCW_AGENT_DATA_ROOT") is { Length: > 0 } custom ? custom : Path.Combine(ProfileRoot(env), "agent-dev");

    public static string DefaultDataRoot() => DefaultDataRoot(Environment.GetEnvironmentVariable);
}
