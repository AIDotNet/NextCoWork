namespace Ncw.Core.Domain;

/// <summary>
/// 「设备」这个维度要不要出现在界面上(docs/design/03 §11.7)。大多数人只有桌面内置的那一台本机设备,没有 SSH 环境:
/// 这时到处写「本机」「设备 · 已连接 · 所有者」、放一个只有一项的设备切换器,都是多设备架构给单机用户的概念成本。
/// 规则(纯函数,设置页、项目面板、模型页共用这一份):
/// <list type="bullet">
/// <item>有不止一台设备(含已配对的远程设备、云端),或者有任何非本机设备 → 显示;</item>
/// <item>有项目在 SSH 环境里 → 显示(执行环境要看得出来);</item>
/// <item>当前设备不是「已连接」(连接中 / 断开 / 未知)→ 显示状态(规则 7:状态未知必须让人看到);</item>
/// <item>本控制端在这台设备上不是 owner → 显示角色(只读、不能改的原因要看得到)。</item>
/// </list>
/// 审批卡片、工具行上的执行环境标识不受这里影响(AGENTS.md §5 要求它们一直带着)。
/// </summary>
public static class DeviceDimension
{
    public static bool Shows(int deviceCount, bool anyNonLocalDevice, bool anySshEnvironment) =>
        deviceCount > 1 || anyNonLocalDevice || anySshEnvironment;

    /// <summary>设置页页头那一排「设备 · 连接状态 · 角色」要不要画。</summary>
    public static bool ShowsScopeDetails(bool showsDevices, DeviceConnection connection, string? role) =>
        showsDevices || connection != DeviceConnection.Connected || (role is not null && role != "owner");
}
