using Ncw.Agent.Environments;
using Ncw.Agent.Extensions.Permissions;
using Ncw.Agent.Kernel.Loop;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>
/// 子代理注册表(2.x <c>agent/registry.ts</c>)——进程内「这个工作区现在有哪些子代理可派」。可整体替换、按名字取、带诊断。
/// <para>
/// <b>出厂就非空</b>:构造时先装上内建的(<see cref="BuiltinAgents"/>),于是「至少有一个子代理可派」在任何时刻都成立,包括第一次扫描发生之前——
/// <c>Task</c> 工具的 description 是按这份清单拼的,不预置的话,第一份 description 里会写着「当前没有任何可用的子代理」。
/// </para>
/// 没有「选装清单」:子代理不进系统提示词,只在模型<b>主动</b>派活时才被用到,多一个可选的子代理的成本就是 <c>Task</c> description 里多一行。
/// </summary>
public sealed class AgentRegistry
{
    private sealed record State(IReadOnlyList<AgentDefinition> Agents, IReadOnlyList<AgentDiagnostic> Diagnostics, long Version);

    private State _state = new(Sort(BuiltinAgents.All), [], 0);

    /// <summary>每次 <see cref="Replace"/> 加一。<c>Task</c> 工具据此判断要不要重拼 description(拼一份字符串的代价可以忽略,但缓存稳定的前提是内容没变就别变)。</summary>
    public long Version => Volatile.Read(ref _state).Version;

    public void Replace(IEnumerable<AgentDefinition> agents, IReadOnlyList<AgentDiagnostic> diagnostics)
    {
        var sorted = Sort([.. agents]);
        State current;
        State next;
        do
        {
            current = Volatile.Read(ref _state);
            next = new State(sorted, diagnostics, current.Version + 1);
        }
        while (Interlocked.CompareExchange(ref _state, next, current) != current);
    }

    public IReadOnlyList<AgentDefinition> List() => Volatile.Read(ref _state).Agents;

    /// <summary>按名字取;认不出返回 null。★ 绝不回落到 <c>general-purpose</c>。</summary>
    public AgentDefinition? Get(string name) => List().FirstOrDefault(a => a.Name == name);

    public IReadOnlyList<string> Names() => [.. List().Select(a => a.Name)];

    public IReadOnlyList<AgentDiagnostic> Diagnostics() => Volatile.Read(ref _state).Diagnostics;

    /// <summary>
    /// 内建的排在最前(内建之间按 <see cref="BuiltinAgents.All"/> 的书写顺序),其余按名字。排序本身是<b>必需</b>的:这个顺序会原样变成 <c>Task</c> 工具 description 里那份清单的顺序,
    /// 而目录遍历的顺序在不同平台上不一样——那会让工具定义无谓地抖动,进而让上游的提示词缓存失效。内建优先则是给模型看的:<c>general-purpose</c> 是它在拿不准时该选的那个。
    /// ★ 内建之间<b>不能</b>按名字排:三条是 <c>code-*</c> 开头,字母序会把 <c>general-purpose</c> 推到第四行。
    /// ★ 用户用同名文件覆盖一条内建之后,<see cref="AgentDefinition.Source"/> 就不是内建了,于是它按名字排进后半段——那已经是用户自己的子代理,不该再占着「模型该优先看的那几行」。
    /// </summary>
    private static List<AgentDefinition> Sort(IReadOnlyList<AgentDefinition> list)
    {
        var builtinOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < BuiltinAgents.All.Count; i++)
        {
            builtinOrder[BuiltinAgents.All[i].Name] = i;
        }

        var sorted = new List<AgentDefinition>(list);
        sorted.Sort((a, b) =>
        {
            var ab = a.Source.Kind == AgentSourceKind.Builtin ? 0 : 1;
            var bb = b.Source.Kind == AgentSourceKind.Builtin ? 0 : 1;
            if (ab != bb)
            {
                return ab.CompareTo(bb);
            }

            if (ab == 1)
            {
                return string.CompareOrdinal(a.Name, b.Name);
            }

            return builtinOrder.GetValueOrDefault(a.Name, int.MaxValue).CompareTo(builtinOrder.GetValueOrDefault(b.Name, int.MaxValue));
        });
        return sorted;
    }
}

/// <summary>「哪些子代理被用户关掉了」的存放处(2.x 的 <c>agents.disabled</c> 配置项)。Host 用配置表实现;内核不认识 kv。</summary>
public interface IAgentSwitchStore
{
    IReadOnlySet<string> Disabled();

    void SetEnabled(string name, bool enabled);
}

public sealed class MemoryAgentSwitchStore : IAgentSwitchStore
{
    private readonly object _gate = new();
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);

    public IReadOnlySet<string> Disabled()
    {
        lock (_gate)
        {
            return new HashSet<string>(_disabled, StringComparer.Ordinal);
        }
    }

    public void SetEnabled(string name, bool enabled)
    {
        lock (_gate)
        {
            if (enabled)
            {
                _disabled.Remove(name);
            }
            else
            {
                _disabled.Add(name);
            }
        }
    }
}

/// <summary>扩展面板里那张清单的一行(2.x 的 <c>AgentListItem</c>)。<see cref="Source"/> 对内建为空串。</summary>
public sealed record AgentListItem(
    string Name,
    string Description,
    AgentSourceKind Scope,
    string Source,
    IReadOnlyList<string>? Tools,
    string? Model,
    PermissionMode? PermissionMode,
    string? Color,
    bool Enabled);

/// <summary>给一个工作区造扫描输入(Host 实现:全局目录在设备本机、项目目录在项目的执行环境里)。</summary>
public interface IAgentScanSource
{
    ValueTask<AgentScanInput> InputForAsync(string workspaceId, CancellationToken ct);
}

/// <summary>
/// 子代理目录服务(2.x 的 <c>runtime.refreshAgents</c> + <c>ipc/agents.ts</c>):<b>每个工作区一份</b>注册表(A 工作区扫出来的子代理不能出现在 B 的工具描述里);
/// <see cref="RefreshAsync"/> 扫描 → 滤掉用户关掉的 → 整体换进那个工作区的注册表;<see cref="ListAsync"/> / <see cref="DiagnosticsAsync"/> 只读(扩展面板用,<b>不碰注册表</b>:
/// 打开一次管理面板就换一次注册表,会让正在跑的 Run 的工具表在中途换掉);<see cref="SetEnabled"/> 启停。
/// <para>
/// 过滤放在这一层而不是 <see cref="AgentLoader"/> 里:「磁盘上有这个子代理吗」和「我想不想用它」是两个问题,扫描器只回答前一个(面板要列出被关掉的那些,靠的就是扫描器不过滤)。
/// 不滤的话「禁用」只改了界面上一个开关的样子,模型照样能通过 <c>Task</c> 派遣它。
/// </para>
/// </summary>
public sealed class AgentCatalog(IAgentScanSource scan, IAgentSwitchStore switches, ILoopDiagnostics? log = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentRegistry> _registries = new(StringComparer.Ordinal);
    private readonly ILoopDiagnostics _log = log ?? NullLoopDiagnostics.Instance;

    /// <summary>启停变了(扩展面板据此刷新)。</summary>
    public event Action? Changed;

    /// <summary>这个工作区的注册表。新建的桶出厂就带内建那几条。</summary>
    public AgentRegistry Registry(string workspaceId)
    {
        lock (_gate)
        {
            if (!_registries.TryGetValue(workspaceId, out var registry))
            {
                _registries[workspaceId] = registry = new AgentRegistry();
            }

            return registry;
        }
    }

    /// <summary>重扫、过滤、整体换进这个工作区的注册表。每次 Run 开始前调一次(用户新加的文件不该「要重启才生效」,而没有任何地方会提示他)。</summary>
    public async Task<IReadOnlyList<AgentDefinition>> RefreshAsync(string workspaceId, CancellationToken ct = default)
    {
        var result = await AgentLoader.ScanAsync(await scan.InputForAsync(workspaceId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        foreach (var diagnostic in result.Diagnostics)
        {
            _log.Warn($"[agent] {diagnostic.Path}: {diagnostic.Message}");
        }

        var disabled = switches.Disabled();
        var enabled = result.Agents.Where(a => !disabled.Contains(a.Name)).ToList();
        Registry(workspaceId).Replace(enabled, result.Diagnostics);
        return Registry(workspaceId).List();
    }

    /// <summary>磁盘上有哪些(含被关掉的,<see cref="AgentListItem.Enabled"/> 标出来)。</summary>
    public async Task<IReadOnlyList<AgentListItem>> ListAsync(string workspaceId, CancellationToken ct = default)
    {
        var result = await AgentLoader.ScanAsync(await scan.InputForAsync(workspaceId, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        var disabled = switches.Disabled();
        return
        [
            .. result.Agents.Select(a => new AgentListItem(
                a.Name, a.Description, a.Source.Kind, a.Source.Kind == AgentSourceKind.Builtin ? "" : a.Source.Path,
                a.Tools, a.Model, a.PermissionMode, a.Color, !disabled.Contains(a.Name))),
        ];
    }

    public async Task<IReadOnlyList<AgentDiagnostic>> DiagnosticsAsync(string workspaceId, CancellationToken ct = default) =>
        (await AgentLoader.ScanAsync(await scan.InputForAsync(workspaceId, ct).ConfigureAwait(false), ct).ConfigureAwait(false)).Diagnostics;

    public void SetEnabled(string name, bool enabled)
    {
        switches.SetEnabled(name, enabled);
        Changed?.Invoke();
    }

    /// <summary>工作区被移除时调。</summary>
    public void Drop(string workspaceId)
    {
        lock (_gate)
        {
            _registries.Remove(workspaceId);
        }
    }

    /// <summary>全部丢掉(换账户时必须调)。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _registries.Clear();
        }
    }
}
