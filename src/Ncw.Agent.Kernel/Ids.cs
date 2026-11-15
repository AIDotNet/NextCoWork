namespace Ncw.Agent.Kernel;

/// <summary>一次 Run 的标识(02-loop §1)。在循环层之上的各层(会话、编排)也用它。</summary>
public readonly record struct RunId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// 执行环境的标识(设备 + 环境),只是一个标签:审批和工具卡片必须带上它(AGENTS.md §5)。
/// 真正的 <c>IExecutionEnvironment</c> 由会话层放进它派生的 <see cref="Tools.ToolContext"/> 里。
/// </summary>
public sealed record EnvironmentTag(string DeviceId, string EnvironmentId);
