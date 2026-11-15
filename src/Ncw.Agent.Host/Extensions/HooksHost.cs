using Ncw.Agent.Environments;
using Ncw.Agent.Hooks;
using Ncw.Agent.Host.Providers;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Extensions;

/// <summary>
/// Hooks 在 Host 里的装配:把 <see cref="ExtensionHostContext"/> 适配成服务层注入的小接口。
/// <list type="bullet">
/// <item>全局设置文件 = <c>&lt;profile 根&gt;/settings.json</c>(2.x 的 <c>globalSettingsPath(userData)</c>,和 <c>providers.json</c> 同一个根;2.x 数据接管之后原有的全局钩子原样可用),
/// 项目设置 = <c>&lt;项目&gt;/.next-cowork/settings.local.json</c> 的 <c>hooks</c> 段(与权限规则同一份文件,两边各自保留对方的键)。</item>
/// <item>项目落点经 <see cref="ExtensionHostContext.Project"/>:项目不存在 → 服务层当「没有这个项目」;环境暂时不可用(<c>unavailable</c>)原样抛给调用方。</item>
/// </list>
/// </summary>
internal static class HooksHost
{
    public const string GlobalFileName = "settings.json";

    public static HooksService Create(ExtensionHostContext context, IHookEvaluator? evaluator = null)
    {
        var style = context.LocalEnvironment.Facts.PathStyle;
        var global = EnvPath.Parse(Path.Combine(ProviderRuntime.ProfileRootOf(context.DataRoot), GlobalFileName), style);
        return new HooksService(new HooksServiceOptions
        {
            GlobalEnvironment = context.LocalEnvironment,
            GlobalSettingsPath = global,
            ResolveProject = (workspaceId, _) =>
            {
                try
                {
                    var target = context.Project(workspaceId);
                    return ValueTask.FromResult<HookProjectTarget?>(new HookProjectTarget(target.Environment, target.Root));
                }
                catch (ProtocolException e) when (e.Code == ErrorCodes.NotFound)
                {
                    return ValueTask.FromResult<HookProjectTarget?>(null);
                }
            },
            Evaluator = evaluator,
            Clock = context.Clock,
            Log = context.Log,
        });
    }
}
