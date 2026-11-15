using System.Text.Json;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Tools;

/// <summary>
/// 工具特征(03 §1)。<c>ReadOnly / Destructive / NeedsNetwork</c> 必须显式声明,没有默认值
/// (2.x ★:默认值会让下一个联网工具悄悄绕过开关)。<see cref="ConcurrencySafe"/> 为 false 的工具使整批顺序执行。
/// </summary>
public sealed record ToolTraits(bool ReadOnly, bool Destructive, bool NeedsNetwork, bool ConcurrencySafe = true);

/// <summary>工具的静态描述。名字是稳定的内部 id;上线用的外部名由注册表分配(见 <see cref="ToolBinding"/>)。</summary>
public sealed record ToolSpec(
    string InternalId,
    string Description,
    JsonElement InputSchema,
    ToolTraits Traits,
    string? PromptSnippet = null,
    IReadOnlyList<string>? PromptGuidelines = null);

/// <summary>参数校验与宽松转换(如 <c>"5"</c> → <c>5</c>)的结果:要么是转换后的参数,要么是给模型看的错误说明。</summary>
public readonly record struct ToolArgumentCheck(JsonElement Input, string? Error = null)
{
    public bool IsValid => Error is null;

    public static ToolArgumentCheck Valid(JsonElement input) => new(input);

    public static ToolArgumentCheck Invalid(string error) => new(default, error);
}

public interface IAgentTool
{
    ToolSpec Info { get; }

    bool IsEnabled(ToolContext ctx) => true;

    /// <summary>
    /// 预检的第一步:校验参数并做宽松转换。默认原样放行。
    /// Loop 在调用 <c>BeforeTool</c>(权限)之前调它,失败变成工具错误而不是异常。
    /// </summary>
    ToolArgumentCheck PrepareArguments(JsonElement input) => ToolArgumentCheck.Valid(input);

    /// <summary>
    /// 执行。<b>中止异常(<see cref="OperationCanceledException"/>,且取消令牌已触发)永远原样传播</b>,不要包装成失败结果(R15)。
    /// 其它异常由 Loop 收敛成工具错误,Run 不挂。
    /// </summary>
    ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct);
}

/// <summary>一次工具调用。<see cref="Name"/> 是外部名(模型看到的名字),<see cref="Input"/> 是预检之后的参数(可能被改写)。</summary>
public sealed record ToolInvocation(string CallId, string Name, IAgentTool Tool, JsonElement Input);

/// <summary>
/// 工具结果(R5):<see cref="Content"/> 给模型(文本和图片),<see cref="Details"/> 只给界面(diff、卡片、结构化数据,永远不发给模型)。
/// <see cref="Terminate"/>:本批所有结果都 Terminate(且都不是错误)时结束 Run(对应 2.x 的 <c>stopRun</c>)。
/// </summary>
public sealed record ToolResult(
    IReadOnlyList<Part> Content,
    JsonElement? Details = null,
    bool IsError = false,
    bool Terminate = false,
    bool Truncated = false,
    long? OriginalBytes = null,
    SubagentReceipt? Subagent = null)
{
    public static ToolResult Text(string text, JsonElement? details = null, bool terminate = false) =>
        new([new TextPart(text)], details, Terminate: terminate);

    public static ToolResult Error(string text, JsonElement? details = null) =>
        new([new TextPart(text)], details, IsError: true);
}

/// <summary>工具进度。易失:只发事件,不进记录。</summary>
public sealed record ToolProgress(string? Message = null, double? Fraction = null, JsonElement? Details = null);

/// <summary>
/// 工具上下文。能力不可用时直接不提供(属性为 null),不给一个一调就抛异常的桩(2.x ★)。
/// 不是 sealed:会话层派生一个带执行环境句柄、权限档位快照、<c>SpawnSubagent</c>、<c>Interact</c> 等能力的子类型,
/// 通过 <c>LoopHooks.EnrichToolContext</c> 换掉默认值。
/// </summary>
public record ToolContext(
    RunId RunId,
    string CallId,
    string SessionId,
    int Depth,
    EnvironmentTag? Environment,
    IProgress<ToolProgress> Progress,
    FileMutationQueue FileMutations);

/// <summary>工具名绑定:外部名 → 工具。同一回合的快照既用来告诉模型有哪些工具,也用来按名字找回工具(2.x ★)。</summary>
public sealed record ToolBinding(string ExternalName, IAgentTool Tool);

/// <summary>一个回合的工具快照。不可变;正在执行的调用持有自己的工具引用,之后注册表怎么变都不影响它。</summary>
public sealed class ToolSnapshot
{
    private readonly Dictionary<string, IAgentTool> _byName;

    public static ToolSnapshot Empty { get; } = new([]);

    public ToolSnapshot(IEnumerable<ToolBinding> bindings)
    {
        _byName = new Dictionary<string, IAgentTool>(StringComparer.Ordinal);
        var model = new List<ToolInfo>();
        foreach (var binding in bindings)
        {
            if (!ToolInfo.IsValidName(binding.ExternalName))
            {
                throw new ArgumentException($"Tool name '{binding.ExternalName}' does not match ^[a-zA-Z0-9_-]{{1,64}}$.", nameof(bindings));
            }

            if (!_byName.TryAdd(binding.ExternalName, binding.Tool))
            {
                throw new ArgumentException($"Duplicate tool name '{binding.ExternalName}'.", nameof(bindings));
            }

            model.Add(new ToolInfo(binding.ExternalName, binding.Tool.Info.Description, binding.Tool.Info.InputSchema));
        }

        ModelTools = model;
    }

    /// <summary>发给模型的工具列表(与 <see cref="Find"/> 同源)。</summary>
    public IReadOnlyList<ToolInfo> ModelTools { get; }

    public IReadOnlyList<string> Names => ModelTools.Select(t => t.ExternalName).ToList();

    public IAgentTool? Find(string externalName) => _byName.GetValueOrDefault(externalName);
}

/// <summary>
/// 同一个文件的修改串行(02 §4,取自 pi)。调用方先把路径解析成<b>真实路径</b>(符号链接、大小写)再当 key;
/// 不同 key 之间互不阻塞。工具在并行批里写文件时,把读-改-写整段放进 <see cref="RunAsync{T}"/>。
/// </summary>
public sealed class FileMutationQueue(IEqualityComparer<string>? comparer = null)
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);

        public int Users;
    }

    private readonly Dictionary<string, Entry> _entries = new(comparer ?? StringComparer.Ordinal);

    /// <summary>当前有任务在排队或执行的 key 数(空闲后立即回收,不泄漏)。</summary>
    public int ActiveKeys
    {
        get
        {
            lock (_entries)
            {
                return _entries.Count;
            }
        }
    }

    public async ValueTask<T> RunAsync<T>(string key, Func<CancellationToken, ValueTask<T>> work, CancellationToken ct)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await work(ct).ConfigureAwait(false);
            }
            finally
            {
                entry.Gate.Release();
            }
        }
        finally
        {
            lock (_entries)
            {
                if (--entry.Users == 0)
                {
                    _entries.Remove(key);
                }
            }
        }
    }

    public async ValueTask RunAsync(string key, Func<CancellationToken, ValueTask> work, CancellationToken ct) =>
        await RunAsync<bool>(key, async token =>
        {
            await work(token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);
}
