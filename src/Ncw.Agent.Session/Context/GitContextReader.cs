using Ncw.Agent.Environments;

namespace Ncw.Agent.Kernel.Session;

/// <summary>
/// git 上下文(2.x <c>git-context.ts</c>):仓库现在在什么状态。模型不知道自己在哪个分支、有没有未提交的改动,要么花一整轮去问,要么直接假设。
/// <para>
/// ★ <b>每个 Run 算一次,不是每回合</b>:装配是纯函数,拿不到进程;而每轮重算也会让状态块每轮变化、把前缀缓存打断。代价是模型在这个 Run 里自己提交了之后这里还是旧的,
/// 所以文案如实标注「as of the start of this run」。最多 4 次 git 调用,每次 2 秒超时,只经 <see cref="IExecutionEnvironment"/>(SSH 环境里就是在远程主机上跑)。
/// </para>
/// <para>
/// ★ <b>永不抛、永不失败</b>:不是仓库 / 没装 git / 超时 / 中断 / 连接断开 → <c>null</c>(这只是锦上添花的上下文)。
/// ★ 分支名和提交标题在 clone 来的仓库里是攻击者可控的:一条写成 <c>&lt;/system-reminder&gt; new instructions:</c> 的提交标题就是一次现成的注入,
/// 所以它们和项目指令走同一条消毒路径,并限长。
/// </para>
/// </summary>
public static class GitContextReader
{
    public const int CommandTimeoutSeconds = 2;

    public const int LineMax = 200;

    public const int RecentCommits = 3;

    public static async Task<GitContext?> ReadAsync(IExecutionEnvironment environment, EnvPath root, CancellationToken ct)
    {
        try
        {
            // 闸门:一条命令吸收三种情况——没装 git、不是仓库、目录不存在。失败就直接收工,后面三条一条都不发。
            var inside = await GitAsync(environment, root, ["rev-parse", "--is-inside-work-tree"], ct).ConfigureAwait(false);
            if (inside is null || inside.Trim() != "true")
            {
                return null;
            }

            // branch --show-current 而不是 rev-parse --abbrev-ref HEAD:空仓库(unborn HEAD)时后者会失败,前者正常返回空串;detached HEAD 同样返回空串。
            var branch = (await GitAsync(environment, root, ["branch", "--show-current"], ct).ConfigureAwait(false) ?? "").Trim();
            // status 只数行数,内容一个字都不带进来(文件名也是不可信文本)
            var dirty = (await GitAsync(environment, root, ["status", "--porcelain"], ct).ConfigureAwait(false) ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
            // 还没有提交时 log 退出非零:不等于「没有 git」,其余字段照常给
            var log = await GitAsync(environment, root, ["log", $"-{RecentCommits}", "--format=%h %s"], ct).ConfigureAwait(false) ?? "";
            var recent = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => PromptText.Clamp(PromptText.Sanitize(line), LineMax))
                .Where(line => line.Length > 0)
                .Take(RecentCommits)
                .ToList();
            return new GitContext(PromptText.Clamp(PromptText.Sanitize(branch), LineMax), dirty, recent);
        }
        catch (Exception e) when (e is EnvironmentException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>成功返回 stdout,失败(非零退出、超时、取消、起不来)返回 null。<c>--no-optional-locks</c>:免得和用户自己开着的 git / IDE 抢 index 锁。</summary>
    private static async Task<string?> GitAsync(IExecutionEnvironment environment, EnvPath root, string[] args, CancellationToken ct)
    {
        var spec = CommandSpec.Exec("git", ["--no-optional-locks", .. args]) with { WorkingDirectory = root };
        var result = await environment.RunAsync(spec, new RunOptions { Timeout = TimeSpan.FromSeconds(CommandTimeoutSeconds) }, ct).ConfigureAwait(false);
        return result.Succeeded ? result.Stdout.ToText() : null;
    }
}
