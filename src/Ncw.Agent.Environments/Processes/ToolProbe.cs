namespace Ncw.Agent.Environments;

/// <summary>
/// 探测环境里有哪些工具可用(06 §3「工具降级」:搜索优先用 <c>rg</c>,没有就退回 <c>grep</c> / <c>find</c>)。
/// 本机和 SSH 共用同一份实现:都是通过 <see cref="IExecutionEnvironment.OpenProcessAsync"/> 跑一条探测命令。
/// </summary>
public static class ToolProbe
{
    private const string PosixScript =
        "for t in git rg grep find; do command -v \"$t\" >/dev/null 2>&1 && printf 'tool:%s\\n' \"$t\"; done; "
        + "v=$(git --version 2>/dev/null); [ -n \"$v\" ] && printf 'gitversion:%s\\n' \"$v\"; true";

    private const string PowerShellScript =
        "foreach ($t in 'git','rg','grep') { if (Get-Command $t -ErrorAction SilentlyContinue) { \"tool:$t\" } }; "
        + "$v = & git --version 2>$null; if ($v) { \"gitversion:$v\" }";

    public static async Task<EnvironmentFacts> ProbeAsync(IExecutionEnvironment environment, EnvironmentFacts facts, CancellationToken ct)
    {
        var script = facts.ShellDialect == ShellDialect.PowerShell || facts.Os == EnvironmentOs.Windows ? PowerShellScript : PosixScript;
        var result = await environment.RunAsync(
            CommandSpec.Shell(script),
            new RunOptions { Timeout = TimeSpan.FromSeconds(15), OutputPolicy = OutputPolicies.Head(16 * 1024) },
            ct).ConfigureAwait(false);

        // 探测失败(超时、连接中断)不是错误:保持原有的事实,调用方当作「不知道有哪些工具」
        if (result.Outcome != ProcessOutcome.Exited)
        {
            return facts;
        }

        return Parse(facts, result.Stdout.ToText());
    }

    /// <summary>解析探测输出(纯函数,单测直接喂文本)。</summary>
    public static EnvironmentFacts Parse(EnvironmentFacts facts, string output)
    {
        var tools = new HashSet<string>(StringComparer.Ordinal);
        string? gitVersion = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("tool:", StringComparison.Ordinal))
            {
                tools.Add(line["tool:".Length..]);
            }
            else if (line.StartsWith("gitversion:", StringComparison.Ordinal))
            {
                var text = line["gitversion:".Length..].Trim();
                gitVersion = text.StartsWith("git version ", StringComparison.Ordinal) ? text["git version ".Length..].Trim() : text;
            }
        }

        return facts with { Tools = tools, GitVersion = gitVersion };
    }
}
