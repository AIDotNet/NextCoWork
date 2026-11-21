using System.Text.Json;

namespace Ncw.Agent.Hooks;

/// <summary>
/// 钩子事件(2.x <c>shared/domain/hook.ts</c>)。事件表是由内核的真实时机<b>倒推</b>出来的:CC 有而这里没有的(<c>SessionStart</c> / <c>PreCompact</c> / <c>SessionEnd</c>)
/// 没有一个能稳定落钩子的对应时刻——挂一个「有时触发有时不触发」的事件,比没有这个事件坏得多。
/// </summary>
public static class HookEvents
{
    public const string UserPromptSubmit = "UserPromptSubmit";
    public const string PreToolUse = "PreToolUse";
    public const string PostToolUse = "PostToolUse";
    public const string Notification = "Notification";
    public const string PreCompact = "PreCompact";
    public const string Stop = "Stop";
    public const string SubagentStart = "SubagentStart";
    public const string SubagentStop = "SubagentStop";

    /// <summary>固定顺序(列表、写文件都按它排,不按文件里键的书写顺序)。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        UserPromptSubmit,
        PreToolUse,
        PostToolUse,
        Notification,
        PreCompact,
        Stop,
        SubagentStart,
        SubagentStop,
    ];

    /// <summary>只有这两个事件手里有「工具 + 入参」,别的事件写了 matcher 也无从匹配(被忽略,而不是让这条钩子整个失效)。</summary>
    public static IReadOnlyList<string> ToolScoped { get; } = [PreToolUse, PostToolUse];

    /// <summary>
    /// 能阻断后续动作的事件。<b><c>Stop</c> 的「阻断」方向相反</b>:其余是「拦住,不让它发生」,Stop 拦的是<b>收尾</b>——拦住的结果是这一轮继续跑。
    /// </summary>
    public static IReadOnlyList<string> Blocking { get; } = [UserPromptSubmit, PreToolUse, PreCompact, Stop];

    public static bool IsValid(string? value) => value is not null && All.Contains(value, StringComparer.Ordinal);
}

public enum HookScope
{
    Global,
    Project,
}

public enum HookKind
{
    /// <summary>本机命令(在<b>项目的执行环境</b>里经 shell 执行)。</summary>
    Command,

    /// <summary>一次旁路模型判定(<c>Stop</c> 上可阻断)。</summary>
    Prompt,
}

/// <summary>尺寸 / 超时的常量(2.x 的值)。</summary>
public static class HookLimits
{
    public const int DefaultTimeoutMs = 60_000;

    /// <summary>PreToolUse 的默认超时单独压低:它的 await 落在每一次工具调用的关键路径上。</summary>
    public const int PreToolDefaultTimeoutMs = 10_000;

    public const int MaxTimeoutMs = 600_000;

    /// <summary>prompt 型等的是一次模型请求,不是本机脚本:独立于命令钩子那条 60 秒。</summary>
    public const int PromptDefaultTimeoutMs = 30_000;

    /// <summary>每个事件下最多几条(合并后的总数;线性扫,每一条都要 fork 一个进程)。</summary>
    public const int MaxPerEvent = 50;

    public const int CommandMax = 4 * 1024;

    public const int PromptMax = 4 * 1024;

    /// <summary>hook 的 stdout 会进模型上下文,必须比工具输出的 64K 小一个量级。</summary>
    public const int StdoutMax = 8 * 1024;

    public const int StderrMax = 4 * 1024;

    public const int DescriptionMax = 256;

    /// <summary>环境变量:个数、名字、值的上限。</summary>
    public const int EnvMaxCount = 32;

    public const int EnvValueMax = 4 * 1024;

    public static int DefaultTimeout(string hookEvent, HookKind kind) =>
        kind == HookKind.Prompt ? PromptDefaultTimeoutMs : hookEvent == HookEvents.PreToolUse ? PreToolDefaultTimeoutMs : DefaultTimeoutMs;
}

/// <summary>
/// 一条钩子。<b>环境变量(<see cref="Env"/>)的值是用户配置,可能是密钥</b>:它只在 Agent 里(配置文件 → 子进程环境),永远不进协议 DTO 和诊断。
/// </summary>
public sealed record HookDefinition
{
    /// <summary>稳定 id(ULID)。「点这一行的开关」「删掉这一行」都需要一个跨读写稳定的键;靠数组下标的话,手改文件之后开关会点到别人身上。</summary>
    public required string Id { get; init; }

    public required string Event { get; init; }

    public HookKind Kind { get; init; } = HookKind.Command;

    /// <summary>权限规则语法(<c>Bash</c> / <c>Write(src/*.ts)</c>)。缺省 / 空 = 这个事件的每一次触发。阻断型钩子应写裸工具名,见 <see cref="HookValidation.WeakMatcher"/>。</summary>
    public string? Matcher { get; init; }

    public bool Enabled { get; init; } = true;

    public int TimeoutMs { get; init; } = HookLimits.DefaultTimeoutMs;

    public string? Description { get; init; }

    /// <summary>走 shell 的一整条命令,不是 argv。<see cref="Kind"/> = Command 才有。</summary>
    public string? Command { get; init; }

    /// <summary>追加 / 覆盖的环境变量(值不上线)。Command 才有。</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = EmptyEnv;

    public string? Prompt { get; init; }

    /// <summary>空 / 缺省 = 回落到本次 run 的模型。与 <see cref="ModelProviderId"/> 成对(单独一个供应商配不出绑定,会被丢掉)。</summary>
    public string? Model { get; init; }

    public string? ModelProviderId { get; init; }

    public static IReadOnlyDictionary<string, string> EmptyEnv { get; } = new Dictionary<string, string>(0);
}

/// <summary>列表里的一条:定义 + 它所在的层 + 来源文件(运行期钩子没有来源文件,是空串)。</summary>
public sealed record HookItem(HookDefinition Hook, HookScope Scope, string SourcePath);

/// <summary>钩子失败 / 配置问题的稳定 key(控制端据此翻译;<see cref="HookDiagnostic.Message"/> 是英文,给日志和排错)。</summary>
public static class HookDiagnosticKeys
{
    public const string Invalid = "hooks.diagnostic.invalid";
    public const string Unreadable = "hooks.diagnostic.unreadable";
    public const string Timeout = "hooks.diagnostic.timeout";
    public const string Command = "hooks.diagnostic.command";
    public const string Evaluator = "hooks.diagnostic.evaluator";
    public const string SpawnFailed = "hooks.diagnostic.spawnFailed";
}

/// <param name="Params">结构化参数(hook 标签、秒数、退出码、细节)。<b>已抹掉该钩子环境变量里的值</b>。</param>
public sealed record HookDiagnostic(string Path, string Key, string Message, IReadOnlyDictionary<string, string>? Params = null);

public enum HookOutcome
{
    /// <summary>不代表退出码是 0:非 0 但不是 2 的也归这里(不阻断,只记诊断)。</summary>
    Ok,
    Blocked,
    Timeout,
    SpawnFailed,
}

public enum HookDecision
{
    Allow,
    Deny,
    Ask,
}

public enum PromptVerdict
{
    Met,
    NotMet,
    Impossible,
    Skipped,
}

/// <summary>一条钩子跑完的结果(<c>hooks.test</c> 的返回值也是它)。</summary>
public sealed record HookRunReport(
    string HookId,
    HookScope Scope,
    int? ExitCode,
    string Stdout,
    string Stderr,
    long DurationMs,
    HookOutcome Outcome,
    HookDecision? Decision = null,
    string? Reason = null,
    string? AdditionalContext = null,
    string? Preview = null,
    PromptVerdict? PromptVerdict = null,
    System.Text.Json.JsonElement? UpdatedInput = null)
{
    /// <summary>阻断 = 退出码 2 / 结构化 deny / prompt 判未达成。</summary>
    public bool Blocks => Outcome == HookOutcome.Blocked || Decision == HookDecision.Deny;
}

/// <summary>钩子配置 / 保存的业务失败。<see cref="Key"/> 是稳定 key,<see cref="Params"/> 不含任何密钥。</summary>
public sealed class HookException(string key, string message, IReadOnlyDictionary<string, string>? parameters = null) : Exception(message)
{
    public string Key { get; } = key;

    public IReadOnlyDictionary<string, string> Params { get; } = parameters ?? new Dictionary<string, string>(0);
}

public static class HookErrorKeys
{
    public const string Invalid = "hooks.error.invalid";
    public const string NotFound = "hooks.error.notFound";
    public const string FileUnreadable = "hooks.error.fileUnreadable";
    public const string WriteFailed = "hooks.error.writeFailed";
    public const string NoWorkspace = "hooks.error.noWorkspace";
    public const string Full = "hooks.error.full";

    public const string EmptyCommand = "hooks.error.emptyCommand";
    public const string EmptyPrompt = "hooks.error.emptyPrompt";
    public const string BadMatcher = "hooks.error.badMatcher";
    public const string BadTimeout = "hooks.error.badTimeout";
    public const string TimeoutTooLong = "hooks.error.timeoutTooLong";
    public const string BadEvent = "hooks.error.badEvent";
    public const string BadEnv = "hooks.error.badEnv";
    public const string TooLong = "hooks.error.tooLong";
}

/// <summary>Json 助手:跳过注释 / 尾逗号(配置文件是给人手改的)。</summary>
internal static class HookJson
{
    public static readonly JsonDocumentOptions Document = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
}
