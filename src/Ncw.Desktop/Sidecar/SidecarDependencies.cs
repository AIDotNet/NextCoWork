namespace Ncw.Desktop.Sidecar;

/// <summary>守护用到的一切外部东西。生产用 <see cref="Production"/>,测试换成假的(假进程、假握手、假时钟)。</summary>
public sealed record SidecarDependencies(
    IProcessLauncher Launcher,
    IAgentHandshake Handshake,
    ISidecarPolicy Policy,
    TimeProvider Clock,
    ISidecarLog Log,
    Func<string?> Locate,
    SidecarTimings Timings,
    string DesktopVersion,
    int DesktopPid)
{
    public static SidecarDependencies Production(string dataRoot, string clientDirectory, System.Reflection.Assembly desktopAssembly) =>
        new(
            new SystemProcessLauncher(),
            new LocalChannelHandshake("desktop-" + Environment.MachineName),
            new DesktopSettings(Path.Combine(clientDirectory, "desktop.json"), Environment.GetEnvironmentVariable),
            TimeProvider.System,
            new FileSidecarLog(new SidecarPaths(dataRoot).LogFile),
            AgentLocator.Locate,
            new SidecarTimings(),
            SidecarVersionPolicy.OfAssembly(desktopAssembly),
            Environment.ProcessId);
}
