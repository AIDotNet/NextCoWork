using Ncw.Desktop.Sidecar;
using Ncw.Protocol.Transport;

namespace Ncw.Desktop.Tests;

public class SemanticVersionTests
{
    [Theory]
    [InlineData("3.0.0", "3.0.0", 0)]
    [InlineData("3.0.0-alpha.1", "3.0.0-alpha.2", -1)]
    [InlineData("3.0.0-alpha.2", "3.0.0-alpha.10", -1)] // 数字标识按数值比
    [InlineData("3.0.0-alpha", "3.0.0-alpha.1", -1)]
    [InlineData("3.0.0-alpha.1", "3.0.0-beta", -1)]
    [InlineData("3.0.0-rc.1", "3.0.0", -1)] // 有预发布标识的比没有的小
    [InlineData("3.0.1", "3.0.0", 1)]
    [InlineData("3.1.0", "3.0.9", 1)]
    [InlineData("4.0.0", "3.9.9", 1)]
    [InlineData("3.0.0+build5", "3.0.0+build9", 0)] // 构建元数据不参与
    [InlineData("v3.0.0", "3.0.0", 0)]
    [InlineData("3.0.0-1", "3.0.0-alpha", -1)] // 数字标识 < 字母标识
    public void Compares_by_semver_precedence(string a, string b, int expected)
    {
        Assert.True(SemanticVersion.TryParse(a, out var va));
        Assert.True(SemanticVersion.TryParse(b, out var vb));
        Assert.Equal(expected, Math.Sign(va.CompareTo(vb)));
        Assert.Equal(-expected, Math.Sign(vb.CompareTo(va)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("3")]
    [InlineData("3.x.0")]
    [InlineData("3.0.0-")]
    [InlineData("3.0.0-a..b")]
    public void Rejects_unparseable(string text) => Assert.False(SemanticVersion.TryParse(text, out _));
}

public class VersionPolicyTests
{
    [Theory]
    [InlineData("3.0.0", "3.0.0", VersionVerdict.Compatible)]
    [InlineData("3.0.0-alpha.1", "3.0.0-alpha.1", VersionVerdict.Compatible)]
    [InlineData("3.0.1", "3.0.0", VersionVerdict.AgentOlder)]
    [InlineData("3.0.0", "3.0.0-rc.1", VersionVerdict.AgentOlder)]
    [InlineData("3.0.0", "3.0.1", VersionVerdict.AgentNewer)]
    [InlineData("3.0.0-alpha.1", "3.0.0-alpha.2", VersionVerdict.AgentNewer)]
    [InlineData("3.0.0", "garbage", VersionVerdict.Compatible)] // 看不懂就不拦,协议握手本身有 N / N-1 检查
    public void Verdicts(string desktop, string agent, VersionVerdict expected) =>
        Assert.Equal(expected, SidecarVersionPolicy.Evaluate(desktop, agent));

    [Fact]
    public void The_desktop_and_agent_versions_come_from_the_same_property()
    {
        // 桌面端用 AssemblyInformationalVersion(去掉 +构建元数据)和 AgentHost.Version 同一个取法。
        var version = SidecarVersionPolicy.OfAssembly(typeof(LocalAgentSidecar).Assembly);
        Assert.True(SemanticVersion.TryParse(version, out _), version);
        Assert.DoesNotContain('+', version);
    }
}

public class TimingsTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 30)]
    [InlineData(20, 30)]
    public void Backoff_doubles_and_caps_at_thirty_seconds(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SidecarTimings.Backoff(failures));
}

public class AgentLocatorTests
{
    [Fact]
    public void Finds_the_sidecar_in_the_agent_directory_next_to_the_desktop_executable()
    {
        var found = AgentLocator.Locate("/app/Contents/MacOS", _ => null, windows: false, p => p == Path.Combine("/app/Contents/MacOS", "agent", "ncw-agent"));
        Assert.Equal(Path.Combine("/app/Contents/MacOS", "agent", "ncw-agent"), found);
        Assert.EndsWith("ncw-agent.exe", AgentLocator.Locate("C:\\app", _ => null, windows: true, _ => true));
    }

    [Fact]
    public void The_environment_override_wins_but_only_if_the_file_exists()
    {
        string? Env(string n) => n == "NCW_AGENT_PATH" ? "/dev/agent" : null;
        Assert.Equal("/dev/agent", AgentLocator.Locate("/app", Env, false, _ => true));
        Assert.Equal(Path.Combine("/app", "agent", "ncw-agent"), AgentLocator.Locate("/app", Env, false, p => p == Path.Combine("/app", "agent", "ncw-agent")));
        Assert.Null(AgentLocator.Locate("/app", _ => null, false, _ => false));
    }

    [Fact]
    public void Layout_matches_what_the_build_tool_bundles()
    {
        // tools/Ncw.Build 的 BundleLayout:<桌面可执行文件目录>/agent/ncw-agent(macOS:Contents/MacOS/agent/)。两边各自写死同一个约定,这里钉住字面值。
        Assert.Equal("ncw-agent", AgentLocator.ExecutableName(false));
        Assert.Equal("ncw-agent.exe", AgentLocator.ExecutableName(true));
        Assert.Equal(Path.Combine("NextCoWork Agent.app", "Contents", "MacOS", "NextCoWork Agent"), AgentLocator.MacBundledExecutable);
    }

    [Fact]
    public void On_macOS_the_nested_agent_app_wins_over_the_bare_executable()
    {
        var bundled = Path.Combine("/app/Contents/MacOS", "agent", "NextCoWork Agent.app", "Contents", "MacOS", "NextCoWork Agent");
        Assert.Equal(bundled, AgentLocator.Locate("/app/Contents/MacOS", _ => null, windows: false, _ => true));
        // Windows 没有嵌套包
        Assert.EndsWith("ncw-agent.exe", AgentLocator.Locate("C:\\app", _ => null, windows: true, _ => true));
    }

    [Theory]
    [InlineData("ncw-agent", true)]
    [InlineData("NextCoWork Agent", true)]
    [InlineData("NextCoWork", false)]
    [InlineData("zsh", false)]
    public void Only_agent_process_names_are_attached(string name, bool expected) =>
        Assert.Equal(expected, AgentLocator.IsAgentProcessName(name));
}

public class PathsTests
{
    [Fact]
    public void Data_run_and_log_locations()
    {
        using var dir = new TempDir();
        var p = new SidecarPaths(dir.Path);
        Assert.Equal(Path.Combine(dir.Path, "run", "sidecar.json"), p.StateFile);
        Assert.Equal(Path.Combine(dir.Path, "logs", "agent.log"), p.LogFile);
        Assert.Equal(LocalTransport.DefaultEndpoint(p.DataRoot), p.Endpoint); // 和 Agent 用同一个函数
    }

    [Fact]
    public void Default_data_root_is_the_2x_data_directory_in_release_builds_and_agent_dev_in_dev_builds()
    {
        string? None(string _) => null;
        // 开发版用独立的 agent-dev,不碰真实的 2.x 数据
        Assert.EndsWith(Path.Combine(".next-cowork", "agent-dev"), SidecarPaths.DefaultDataRoot(None, development: true));
        // ★ 发行版必须是 2.x 的数据根 ~/.next-cowork/data:3.0.0 用的是 agent-dev,所以从来没接上过 2.x 的数据
        Assert.EndsWith(Path.Combine(".next-cowork", "data"), SidecarPaths.DefaultDataRoot(None, development: false));
        Assert.Equal(Path.Combine("/prof", "data"), SidecarPaths.DefaultDataRoot(n => n == "NCW_PROFILE_ROOT" ? "/prof" : null, development: false));
        Assert.Equal("/x/data", SidecarPaths.DefaultDataRoot(n => n == "NCW_AGENT_DATA_ROOT" ? "/x/data" : null));
        Assert.Equal(Path.Combine("/prof", "agent-dev"), SidecarPaths.DefaultDataRoot(n => n == "NCW_PROFILE_ROOT" ? "/prof" : null, development: true));
        Assert.EndsWith(".next-cowork", SidecarPaths.ProfileRoot(None));
    }
}

public class StateAndSettingsTests
{
    [Fact]
    public void State_file_round_trips_is_private_and_tolerates_garbage()
    {
        using var dir = new TempDir();
        var file = new SidecarStateFile(Path.Combine(dir.Path, "run", "sidecar.json"));
        Assert.Null(file.Read());
        var state = new SidecarState(123, "tok", true, "3.0.0", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        file.Write(state);
        Assert.Equal(state, file.Read());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(dir.Path, "run", "sidecar.json")));
        }

        File.WriteAllText(Path.Combine(dir.Path, "run", "sidecar.json"), "{ not json");
        Assert.Null(file.Read());
        file.Delete();
        file.Delete(); // 重复删除不抛
    }

    [Fact]
    public void Keep_agent_running_defaults_to_off_reads_the_file_and_can_be_forced_by_environment()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "desktop.json");
        var settings = new DesktopSettings(path, _ => null);
        Assert.False(settings.KeepAgentRunningAfterExit);
        settings.SetKeepAgentRunningAfterExit(true);
        Assert.True(settings.KeepAgentRunningAfterExit);
        settings.SetKeepAgentRunningAfterExit(false);
        Assert.False(settings.KeepAgentRunningAfterExit);

        Assert.True(new DesktopSettings(path, n => n == "NCW_KEEP_AGENT" ? "1" : null).KeepAgentRunningAfterExit);
        File.WriteAllText(path, "garbage");
        Assert.False(settings.KeepAgentRunningAfterExit);
    }

    [Fact]
    public void Log_file_rolls_over_keeping_one_previous_file()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "logs", "agent.log");
        var log = new FileSidecarLog(path, maxBytes: 100);
        for (var i = 0; i < 20; i++)
        {
            log.Write("line " + i);
        }

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.False(File.Exists(path + ".2"));
        Assert.Contains("line 19", File.ReadAllText(path));
    }
}
