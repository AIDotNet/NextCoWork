namespace Ncw.Agent.Hooks;

/// <summary>
/// 运行期钩子表(2.x <c>hook-registry.ts</c>):只活在进程内存里、按会话登记的那一批钩子——不写进设置文件,生命周期跟着会话走,不跟着磁盘走。
/// 和文件里那些是<b>并列</b>关系,不是覆盖:合并后每事件仍受 <see cref="HookLimits.MaxPerEvent"/> 限制;运行期钩子永远排在最后(全局 → 项目 → 运行期)。
/// <para>
/// 3.0 里<b>会话目标不再经由它</b>(目标是 <c>turn_end</c> 闸门上的处理器,Order 500;不再「注册一条内置 Stop prompt 钩子」),所以 2.x 里「目标钩子叠两条」的问题不存在;
/// 这张表保留给将来「由功能自己装一条 Hook」的用途(例如插件宿主、测试),语义与 2.x 一致:按 id upsert(同一条钩子被重新挂上不会越叠越多)。
/// </para>
/// </summary>
public sealed class RuntimeHookTable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<HookDefinition>> _bySession = new(StringComparer.Ordinal);

    public void Register(string sessionId, HookDefinition hook)
    {
        lock (_gate)
        {
            var list = _bySession.TryGetValue(sessionId, out var existing) ? existing : (_bySession[sessionId] = []);
            list.RemoveAll(h => h.Id == hook.Id);
            list.Add(hook);
        }
    }

    public void Remove(string sessionId, string hookId)
    {
        lock (_gate)
        {
            if (_bySession.TryGetValue(sessionId, out var list))
            {
                list.RemoveAll(h => h.Id == hookId);
                if (list.Count == 0)
                {
                    _bySession.Remove(sessionId);
                }
            }
        }
    }

    public IReadOnlyList<HookDefinition> For(string sessionId)
    {
        lock (_gate)
        {
            return _bySession.TryGetValue(sessionId, out var list) ? [.. list] : [];
        }
    }

    /// <summary>清掉一个会话的运行期钩子(会话结束 / 删除时)。</summary>
    public void Clear(string sessionId)
    {
        lock (_gate)
        {
            _bySession.Remove(sessionId);
        }
    }
}
