using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Extensions;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Skills;

namespace Ncw.Agent.Plugins;

/// <summary>
/// 插件提供的 Skill(<see cref="ISkillPluginSource"/>,Skills 子代理已留好的接口)。★ 每次扫描都重新问、不缓存:用户禁用 / 卸载了插件,下一个 Run 模型就不该再看见它带来的 Skill。
/// 只有<b>清单里声明了</b>的 <c>skills/&lt;name&gt;</c> 才算(包里多放一个目录不能自动生效);Skill 是静态数据,不需要运行时,所以 <c>idle</c> / <c>active</c> 的启用插件都提供,
/// 装载失败 / 待批准 / 故障 / 禁用的不提供(一个被判不可信的插件不该继续往模型的目录里塞东西)。插件包永远在设备本机。
/// </summary>
internal sealed class PluginSkillSource(PluginManager manager) : ISkillPluginSource
{
    public IReadOnlyList<PluginSkillRoot> Current() => manager.SkillRoots();
}

/// <summary>
/// 插件的工具拦截器(<see cref="IToolInterceptor"/>,交给权限扩展:<c>PermissionExtension(interceptors: …)</c>)。<b>只能收紧</b>:接入口类型上没有放行(<see cref="InterceptorVerdict"/> 只有拒绝和询问),
/// 插件不能关掉审批;超时 / 失败 = 弃权(只记诊断)。需要 <c>agent.intercept</c> 能力。
/// </summary>
internal sealed class PluginToolInterceptor(PluginManager manager) : IToolInterceptor
{
    public async ValueTask<InterceptorVerdict> InterceptAsync(PreToolContext context, CancellationToken ct)
    {
        var (deny, ask) = await manager.InterceptAsync(context.Call.Tool.Info.InternalId, context.Input, ct).ConfigureAwait(false);
        return new InterceptorVerdict(deny, ask);
    }
}

/// <summary>
/// 插件宿主的会话扩展(<c>plugins</c>,<see cref="Order"/> 700,每个会话对象一个实例):<c>before_run</c> 收集插件的上下文提供者给本轮的一小段,进头块(静态、保住缓存前缀)。
/// 插件扩展只能订阅白名单里的事件,不能注册 <c>before_provider_request</c> 这类高权限扩展点(08.06 §1)。
/// </summary>
public sealed class PluginSessionExtension(PluginManager manager) : IAgentExtension
{
    public const string ExtensionId = "plugins";

    public string Id => ExtensionId;

    public int Order => 700;

    public void Register(IExtensionRegistry registry) =>
        registry.Mutate(SessionExtensionPoints.BeforeRun, async (draft, ctx, ct) =>
        {
            foreach (var section in await manager.CollectContextAsync(ctx.SessionId, ct).ConfigureAwait(false))
            {
                draft.HeadSections.Add(section);
            }
        });
}

public sealed partial class PluginManager
{
    /// <summary>插件提供的 Skill 目录(见 <see cref="SkillSource"/>)。</summary>
    public ISkillPluginSource SkillSource { get; }

    /// <summary>插件的工具拦截器(交给 <c>PermissionExtension</c>)。</summary>
    public IToolInterceptor Interceptor { get; }

    /// <summary>每个会话一个的会话扩展(上下文提供者)。</summary>
    public PluginSessionExtension CreateExtension() => new(this);

    internal IReadOnlyList<PluginSkillRoot> SkillRoots()
    {
        lock (_gate)
        {
            return [.. _records.Values
                .Where(r => r.Enabled && r.Status is PluginStatus.Idle or PluginStatus.Activating or PluginStatus.Active)
                .OrderBy(r => r.Manifest.Id, StringComparer.Ordinal)
                .SelectMany(r => r.Manifest.Contributes.Skills.Select(path => new PluginSkillRoot(r.Manifest.Id, _env, r.Directory.Join(path.Split('/')))))];
        }
    }
}
