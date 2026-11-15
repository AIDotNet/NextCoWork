using System.Text.Json;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Extensions.Permissions;

/// <summary>交给 PreToolUse 钩子 / 插件拦截器的一次调用。</summary>
public sealed record PreToolContext(PermissionRunState Run, ToolInvocation Call, JsonElement Input);

/// <summary>
/// PreToolUse 钩子的接入口(03 §3 判定链的第 3–6 步)。Hooks 扩展实现它,<b>不要</b>自己在 <c>before_tool</c> 上再注册一个处理器:
/// 钩子的 deny 必须压得过 allow 规则、钩子的 ask 必须压过档位放行、钩子的 allow 要能放行一个本来要问的调用——这些都发生在链的中间,
/// 而 <c>before_tool</c> 的组合语义(第一个拒绝即短路、放行需要所有人不反对)表达不了「钩子 ask 压过规则 allow」。所以权限扩展在链里<b>调用</b>它们。
/// 钩子失败(异常、超时、起不来)= 弃权并记诊断(Hook 的失败放行,03 §5);只有权限扩展自己的异常才按拒绝处理。
/// </summary>
public interface IPreToolVerdictSource
{
    ValueTask<HookVerdict> EvaluateAsync(PreToolContext context, CancellationToken ct);
}

/// <summary>
/// 插件工具拦截器的表态:<b>只有拒绝和询问,没有放行</b>——类型上就不存在「插件把审批弹窗关掉」这条路(让第三方插件关掉审批,等于把权限链的最后一道门交给它)。
/// </summary>
public readonly record struct InterceptorVerdict(string? Deny = null, bool Ask = false)
{
    public static InterceptorVerdict None => default;
}

/// <summary>插件拦截器的接入口。超时 = 弃权(一个卡住的插件不该把所有工具调用堵死)。</summary>
public interface IToolInterceptor
{
    ValueTask<InterceptorVerdict> InterceptAsync(PreToolContext context, CancellationToken ct);
}
