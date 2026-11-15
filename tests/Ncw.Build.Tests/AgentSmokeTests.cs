using Ncw.Build.Core;
using Ncw.Build.Verify;

namespace Ncw.Build.Tests;

/// <summary>
/// 真实冒烟:启动一个真的 ncw-agent 进程。用 <c>NCW_AGENT_PATH</c>(例如 AOT 发布产物);没有就找本仓库刚构建出来的
/// <c>src/Ncw.Agent/bin/*/net10.0/ncw-agent</c>。都没有、或设置了 <c>NCW_SKIP_AGENT_SMOKE=1</c> 时跳过(并说明原因)。
/// </summary>
public class AgentSmokeTests
{
    internal static string? FindAgent()
    {
        if (Environment.GetEnvironmentVariable("NCW_AGENT_PATH") is { Length: > 0 } custom && File.Exists(custom))
        {
            return custom;
        }

        var name = OperatingSystem.IsWindows() ? "ncw-agent.exe" : "ncw-agent";
        var bin = Path.Combine(TestKit.RepoRoot(), "src", "Ncw.Agent", "bin");
        return Directory.Exists(bin)
            ? Directory.EnumerateFiles(bin, name, SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}publish{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                // NativeAOT 编译留在 bin/<配置>/<rid>/native/ 下的是中间产物:旁边没有 e_sqlite3 之类的原生库,启动就 DllNotFoundException。
                // 刚做完一次 AOT 发布,它就是「最新修改」的那个——必须排除,否则本机上 publish 之后再跑测试就会挑中它。
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}native{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
    }

    [Fact]
    public async Task A_real_agent_starts_handshakes_rejects_a_second_instance_and_shuts_down_cleanly()
    {
        if (Environment.GetEnvironmentVariable("NCW_SKIP_AGENT_SMOKE") == "1")
        {
            Assert.Skip("NCW_SKIP_AGENT_SMOKE=1");
        }

        var agent = FindAgent();
        if (agent is null)
        {
            Assert.Skip("no ncw-agent binary: build src/Ncw.Agent first (dotnet build NextCoWork.slnx) or set NCW_AGENT_PATH");
        }

        var log = new MemoryBuildLog();
        var result = await AgentSmoke.RunAsync(agent, null, log, TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(result.AgentVersion), string.Join("\n", log.Lines));
        Assert.Equal(AgentSmoke.AlreadyRunningExitCode, result.SecondInstanceExitCode);
        Assert.Equal(0, result.ShutdownExitCode);
    }
}
