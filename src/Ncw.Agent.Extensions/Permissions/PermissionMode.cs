namespace Ncw.Agent.Extensions.Permissions;

/// <summary>
/// 权限档位(03 §3,2.x <c>shared/agent/permission.ts</c>):<c>ask</c>(都问)、<c>auto</c>(只有破坏性操作问)、<c>full</c>(不问,但联网仍受开关控制)。
/// 运行中可以改(<see cref="PermissionRunState.Mode"/> 是实时值),工具上下文里拿的是 Run 开始时的快照。
/// 「计划模式」不是档位:它是一道写入围栏(<see cref="PermissionRunState.PlanFence"/>),和档位正交。
/// </summary>
public enum PermissionMode
{
    Ask = 0,
    Auto = 1,
    Full = 2,
}

public static class PermissionModes
{
    public static IReadOnlyList<PermissionMode> All { get; } = [PermissionMode.Ask, PermissionMode.Auto, PermissionMode.Full];

    /// <summary>
    /// 子代理不继承 <c>full</c>:档位取 min(父档位, 子代理配置档位)(2.x 方案 §4.9)。
    /// 否则一个被投毒的 MCP 工具描述可以诱导主 Agent 派一个子 Agent 去做它自己不被允许做的事——这是真实的提权路径。
    /// </summary>
    public static PermissionMode Min(PermissionMode a, PermissionMode b) => (PermissionMode)Math.Min((int)a, (int)b);

    /// <summary>线上写法(和 2.x 一致:<c>ask</c> / <c>auto</c> / <c>full</c>)。</summary>
    public static string ToWire(this PermissionMode mode) => mode switch
    {
        PermissionMode.Ask => "ask",
        PermissionMode.Auto => "auto",
        PermissionMode.Full => "full",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    public static bool TryParse(string? wire, out PermissionMode mode)
    {
        switch (wire)
        {
            case "ask":
                mode = PermissionMode.Ask;
                return true;
            case "auto":
                mode = PermissionMode.Auto;
                return true;
            case "full":
                mode = PermissionMode.Full;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}
