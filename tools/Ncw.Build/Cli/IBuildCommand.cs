using Ncw.Build.Core;

namespace Ncw.Build.Cli;

internal interface IBuildCommand
{
    CommandSpec Spec { get; }

    /// <summary>返回进程退出码(0 = 成功)。可预期的失败抛 <see cref="BuildException"/>。</summary>
    Task<int> RunAsync(BuildContext ctx, ParsedArgs args, CancellationToken ct);
}
