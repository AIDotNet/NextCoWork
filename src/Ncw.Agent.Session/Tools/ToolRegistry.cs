using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Session;

/// <summary>一个已注册的工具:稳定 id、来源、分配的外部名(模型看到的名字)。</summary>
public sealed record RegisteredTool(string InternalId, string Source, string ExternalName, IAgentTool Tool, bool Disabled);

/// <summary>注册结局。</summary>
public enum ToolRegistration
{
    /// <summary>新增。</summary>
    Added,

    /// <summary>同 id、同来源:替换(MCP 重连依赖这一点)。</summary>
    Replaced,

    /// <summary>同 id、别的来源已经占着:丢弃后来者(任何来源都不能替换内置工具,03 §1)。</summary>
    DroppedConflict,
}

/// <summary>
/// 统一的工具注册表(03 §1 的「注册表」):内置、一方扩展、插件、MCP 的工具都登记在这里,每回合由会话层取一次快照(<see cref="IToolSource"/>)。
/// <list type="bullet">
/// <item><b>按 <see cref="IAgentTool.Info"/>.InternalId 存储</b>;同 id 同来源 = 替换(位置不变),同 id 别的来源 = 丢弃后来者(内置先注册,所以谁也替不掉 <c>Bash</c>);</item>
/// <item><b>按来源批量注销</b>(<see cref="UnregisterSource"/>):MCP 服务器断开 / 关掉时它的工具整批消失;</item>
/// <item><b>外部名</b>只含 <c>[a-zA-Z0-9_-]</c>,超长(&gt; 64)或与别的工具冲突时变成 <c>前缀_&lt;fnv1a32(内部 id)&gt;</c>;映射<b>永不回收、永不改变</b>(同一个内部 id 在进程生命周期里永远是同一个外部名,
/// 否则同一个会话里先后两个回合模型看到的名字会变);</item>
/// <item><b>禁用</b>(<see cref="SetDisabled"/>):不进快照,但仍然列在 <see cref="List"/> 里(设置页要显示「已关」);</item>
/// <item><b>快照只删不加</b>:<see cref="Snapshot"/> 按 <see cref="IAgentTool.IsEnabled"/> 过滤;子代理 / 模式 / 联网开关的收窄由会话层的 <c>RunDraft.ToolFilter</c> 在它之上再做。正在执行的调用持有自己的工具引用,注销后不重新查找。</item>
/// </list>
/// 线程安全:读(快照、列表)不加锁,写时复制。
/// </summary>
public sealed class ToolRegistry : IToolSource
{
    /// <summary>内置工具的来源名。</summary>
    public const string BuiltinSource = "builtin";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);      // internalId → 外部名,永不回收
    private readonly HashSet<string> _usedNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);
    private RegisteredTool[] _entries = [];
    private long _version;

    /// <summary>注册表被改过就加一(界面据此知道要不要重拉 <c>tools.list</c>)。</summary>
    public long Version => Interlocked.Read(ref _version);

    public event Action? Changed;

    public ToolRegistry(IEnumerable<IAgentTool>? builtins = null)
    {
        if (builtins is not null)
        {
            foreach (var tool in builtins)
            {
                Register(tool, BuiltinSource);
            }
        }
    }

    public ToolRegistration Register(IAgentTool tool, string source)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentException.ThrowIfNullOrEmpty(source);
        var id = tool.Info.InternalId;
        ToolRegistration result;
        lock (_gate)
        {
            var index = Array.FindIndex(_entries, e => e.InternalId == id);
            if (index >= 0 && _entries[index].Source != source)
            {
                return ToolRegistration.DroppedConflict;
            }

            var entry = new RegisteredTool(id, source, ExternalNameFor(id), tool, _disabled.Contains(id));
            if (index >= 0)
            {
                var copy = (RegisteredTool[])_entries.Clone();
                copy[index] = entry;
                _entries = copy;
                result = ToolRegistration.Replaced;
            }
            else
            {
                _entries = [.. _entries, entry];
                result = ToolRegistration.Added;
            }

            Interlocked.Increment(ref _version);
        }

        Changed?.Invoke();
        return result;
    }

    /// <summary>整批注册一个来源的工具(先注销它原有的,所以重连后少掉的工具会消失)。返回被丢弃的(与别的来源冲突的)内部 id。</summary>
    public IReadOnlyList<string> ReplaceSource(string source, IEnumerable<IAgentTool> tools)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        var dropped = new List<string>();
        lock (_gate)
        {
            _entries = [.. _entries.Where(e => e.Source != source)];
            foreach (var tool in tools)
            {
                var id = tool.Info.InternalId;
                if (_entries.Any(e => e.InternalId == id))
                {
                    dropped.Add(id);
                    continue;
                }

                _entries = [.. _entries, new RegisteredTool(id, source, ExternalNameFor(id), tool, _disabled.Contains(id))];
            }

            Interlocked.Increment(ref _version);
        }

        Changed?.Invoke();
        return dropped;
    }

    /// <summary>注销一个来源的全部工具。返回注销了几个。</summary>
    public int UnregisterSource(string source)
    {
        int removed;
        lock (_gate)
        {
            var before = _entries.Length;
            _entries = [.. _entries.Where(e => e.Source != source)];
            removed = before - _entries.Length;
            if (removed > 0)
            {
                Interlocked.Increment(ref _version);
            }
        }

        if (removed > 0)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    public bool Unregister(string internalId)
    {
        bool removed;
        lock (_gate)
        {
            var before = _entries.Length;
            _entries = [.. _entries.Where(e => e.InternalId != internalId)];
            removed = before != _entries.Length;
            if (removed)
            {
                Interlocked.Increment(ref _version);
            }
        }

        if (removed)
        {
            Changed?.Invoke();
        }

        return removed;
    }

    /// <summary>禁用 / 启用一个工具(按内部 id)。禁用不改外部名,也不要求工具已经注册(先禁用后注册同样生效)。</summary>
    public void SetDisabled(string internalId, bool disabled)
    {
        lock (_gate)
        {
            var changed = disabled ? _disabled.Add(internalId) : _disabled.Remove(internalId);
            if (!changed)
            {
                return;
            }

            _entries = [.. _entries.Select(e => e.InternalId == internalId ? e with { Disabled = disabled } : e)];
            Interlocked.Increment(ref _version);
        }

        Changed?.Invoke();
    }

    /// <summary>所有已注册的工具(含被禁用的),按注册顺序。<c>tools.list</c> 用。</summary>
    public IReadOnlyList<RegisteredTool> List() => _entries;

    public RegisteredTool? Find(string internalId) => Array.Find(_entries, e => e.InternalId == internalId);

    /// <summary>
    /// 每回合取一次的快照来源:没被禁用、<see cref="IAgentTool.IsEnabled"/> 为真的工具。外部名已在注册时分配。
    /// </summary>
    public IReadOnlyList<ToolBinding> Snapshot(ToolContext context)
    {
        var bindings = new List<ToolBinding>();
        foreach (var entry in _entries)
        {
            if (!entry.Disabled && entry.Tool.IsEnabled(context))
            {
                bindings.Add(new ToolBinding(entry.ExternalName, entry.Tool));
            }
        }

        return bindings;
    }

    // ───────────────────────── 命名 ─────────────────────────

    private string ExternalNameFor(string internalId)
    {
        if (_names.TryGetValue(internalId, out var existing))
        {
            return existing;
        }

        var clean = Sanitize(internalId);
        var name = clean;
        if (name.Length == 0 || name.Length > MaxNameLength || _usedNames.Contains(name))
        {
            var suffix = "_" + Fnv1a32(internalId).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            var prefix = clean.Length > MaxNameLength - suffix.Length ? clean[..(MaxNameLength - suffix.Length)] : clean;
            name = (prefix.Length == 0 ? "tool" : prefix) + suffix;
        }

        // 极端情况(同一个哈希前缀撞上):继续加序号,保证唯一
        var candidate = name;
        for (var i = 2; _usedNames.Contains(candidate); i++)
        {
            var tail = $"_{i}";
            candidate = (name.Length + tail.Length > MaxNameLength ? name[..(MaxNameLength - tail.Length)] : name) + tail;
        }

        _usedNames.Add(candidate);
        _names[internalId] = candidate;
        return candidate;
    }

    private const int MaxNameLength = 64;

    private static string Sanitize(string id)
    {
        var chars = new char[id.Length];
        for (var i = 0; i < id.Length; i++)
        {
            var c = id[i];
            chars[i] = char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_';
        }

        return new string(chars);
    }

    /// <summary>FNV-1a 32 位(03 §1 的命名规则)。</summary>
    public static uint Fnv1a32(string text)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 16777619u;
        }

        return hash;
    }

    /// <summary>外部名是否合法(与模型层同一条规则)。</summary>
    public static bool IsValidExternalName(string name) => ToolInfo.IsValidName(name);
}
