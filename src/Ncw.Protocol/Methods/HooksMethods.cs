using PolyType;

namespace Ncw.Protocol.Methods;

public static partial class Capabilities
{
    /// <summary>Hooks:<c>hooks.*</c>(列表 / 诊断 member 起;保存 / 删除 / 启停 / 试运行 owner——钩子命令在设备上由 shell 执行,允许控制端写入命令 = 允许它在设备上执行任意命令)。</summary>
    public const string Hooks = "hooks";
}

/// <summary>
/// Hooks 的方法表(docs/design/09 §8.4,功能盘点 10 的 <c>hooks</c> 模块:列表、诊断、保存、删除、启停、试运行)。每个方法声明种类(command 必带幂等键)、所需角色、所需能力、允许的承载。
/// <list type="bullet">
/// <item><b>读</b>(<c>hooks.list</c> / <c>hooks.diagnostics</c>):member 起——钩子的命令文本、prompt 文本可见,viewer 不给。</item>
/// <item><b>写</b>(<c>hooks.save</c> / <c>delete</c> / <c>setEnabled</c> / <c>test</c>):owner。2.x 的网络面只开放列出与启停,是因为「写入命令 = 在设备上执行任意命令」要单独的确认流程;
/// 3.0 把这条边界放在角色上:<b>只有 owner(本机通道和已经被设备主人提升为 owner 的控制端)能写</b>,member 只读。<c>hooks.test</c> 真的会执行命令,所以同样 owner 并且是 command(带幂等键)。</item>
/// </list>
/// <b>环境变量的值不上线</b>:钩子可以配置环境变量(密钥常常经它传给脚本),<see cref="HookDto"/> 只带变量名(<see cref="HookDto.EnvNames"/>),
/// 值只在 <c>hooks.save</c> / <c>hooks.test</c> 的请求里单向写入(控制端 → Agent);编辑时某个变量的 <see cref="HookEnvVarDto.Value"/> 为 null = 沿用已存的值。
/// 试运行的输出与诊断里已抹掉这些值。业务失败的稳定 key 在 <c>error.details</c>(<see cref="ProviderErrorDto"/>:<c>hooks.error.*</c>)。
/// 钩子配置变化经 <c>extensions</c> 主题的 <c>HooksRev</c> 通知其它控制端重拉。
/// </summary>
public static class HooksMethods
{
    /// <summary>某个项目的钩子:全局层 + 项目层(没有 <see cref="HooksListParams.WorkspaceId"/> 时只有全局)。每次现读磁盘(用户刚手改了文件就来看)。</summary>
    public static readonly MethodDef<HooksListParams, HooksListResult> List =
        Query<HooksListParams, HooksListResult>("hooks.list", Role.Member);

    /// <summary>配置诊断(文件读不懂、有几条被忽略)+ 运行期失败(最近 20 条;钩子失败不阻断运行,这是用户唯一能发现「它一直在报错」的地方)。</summary>
    public static readonly MethodDef<HooksListParams, HooksDiagnosticsResult> Diagnostics =
        Query<HooksListParams, HooksDiagnosticsResult>("hooks.diagnostics", Role.Member);

    /// <summary>新建或更新(按 id upsert)。校验失败 <c>invalid_request</c>(<c>hooks.error.*</c>)、文件读不懂 <c>conflict</c>(拒绝覆盖)、写不进去 <c>unavailable</c>。</summary>
    public static readonly MethodDef<HooksSaveParams, HooksSaveResult> Save =
        Command<HooksSaveParams, HooksSaveResult>("hooks.save", Role.Owner);

    public static readonly MethodDef<HooksDeleteParams, Unit> Delete =
        Command<HooksDeleteParams, Unit>("hooks.delete", Role.Owner);

    /// <summary>启停一条:只翻转开关,不改内容。</summary>
    public static readonly MethodDef<HooksSetEnabledParams, Unit> SetEnabled =
        Command<HooksSetEnabledParams, Unit>("hooks.setEnabled", Role.Owner);

    /// <summary>
    /// 试运行:跑的是此刻的<b>草稿</b>(不读磁盘上那一条)。command 型在项目的执行环境里(没给项目就在设备本机)用示例载荷真跑一次;prompt 型只预览展开结果,不真调模型。
    /// 输出有长度上限,环境变量的值已抹掉。
    /// </summary>
    public static readonly MethodDef<HooksTestParams, HookRunReportDto> Test =
        Command<HooksTestParams, HookRunReportDto>("hooks.test", Role.Owner);

    public static readonly IReadOnlyList<MethodDef> All = [List, Diagnostics, Save, Delete, SetEnabled, Test];

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Hooks };

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Hooks };
}

/// <summary>枚举用的线上字符串。</summary>
public static class HookNames
{
    public const string ScopeGlobal = "global";
    public const string ScopeProject = "project";
    public const string TypeCommand = "command";
    public const string TypePrompt = "prompt";

    public const string OutcomeOk = "ok";
    public const string OutcomeBlocked = "blocked";
    public const string OutcomeTimeout = "timeout";
    public const string OutcomeSpawnFailed = "spawn-failed";

    public const string DecisionAllow = "allow";
    public const string DecisionDeny = "deny";
    public const string DecisionAsk = "ask";

    public const string VerdictMet = "met";
    public const string VerdictNotMet = "not_met";
    public const string VerdictImpossible = "impossible";
    public const string VerdictSkipped = "skipped";
}

/// <param name="WorkspaceId">项目(工作区)id;null = 只看全局层。</param>
public sealed record HooksListParams(string? WorkspaceId = null);

/// <summary>
/// 列表里的一条钩子。<b>不含环境变量的值</b>(只有名字)。
/// </summary>
/// <param name="Scope"><c>global</c> / <c>project</c>。</param>
/// <param name="Event"><c>UserPromptSubmit</c> / <c>PreToolUse</c> / <c>PostToolUse</c> / <c>Notification</c> / <c>Stop</c> / <c>SubagentStop</c>。</param>
/// <param name="Type"><c>command</c> / <c>prompt</c>。</param>
/// <param name="Matcher">权限规则语法;null = 每次触发。</param>
/// <param name="TimeoutMs">毫秒(文件里是秒,线上统一毫秒)。</param>
/// <param name="Command">command 型才有。</param>
/// <param name="Prompt">prompt 型才有。</param>
/// <param name="EnvNames">已配置的环境变量名(值不上线)。</param>
/// <param name="SourcePath">所在的设置文件(设备上的路径,给「在编辑器里打开」用)。</param>
public sealed record HookDto(
    string Id,
    string Scope,
    string Event,
    string Type,
    string? Matcher,
    bool Enabled,
    int TimeoutMs,
    string? Description,
    string? Command,
    string? Prompt,
    string? Model,
    string? ModelProviderId,
    string[] EnvNames,
    string SourcePath);

public sealed record HooksListResult(HookDto[] Hooks);

/// <summary>一条诊断。<see cref="Key"/> 是稳定的 <c>hooks.diagnostic.*</c> key(控制端翻译),<see cref="Message"/> 是英文,给日志和排错;参数里已抹掉环境变量的值。</summary>
public sealed record HookDiagnosticDto(string Path, string Key, string Message, Dictionary<string, string>? Params = null);

public sealed record HooksDiagnosticsResult(HookDiagnosticDto[] Diagnostics);

/// <summary>环境变量一行(控制端 → Agent 的单向写入)。<see cref="Value"/> 为 null = 沿用这条钩子里同名变量已存的值。</summary>
public sealed record HookEnvVarDto(string Name, string? Value);

/// <summary>
/// 要保存 / 试运行的钩子草稿。<see cref="Id"/> 为空 = 新建(设备铸 id)。<see cref="Env"/> 为 null = 不动(新建 = 没有;更新 = 保持原样);非 null = 恰好这些变量。
/// 超时用毫秒,范围 1 ~ 600000;命令 / prompt 各 4096 字符上限。
/// </summary>
public sealed record HookUpsertDto(
    string? Id,
    string Event,
    string Type,
    string? Matcher,
    bool Enabled,
    int TimeoutMs,
    string? Description,
    string? Command,
    string? Prompt,
    string? Model,
    string? ModelProviderId,
    HookEnvVarDto[]? Env);

public sealed record HooksSaveParams(string Scope, string? WorkspaceId, HookUpsertDto Hook);

/// <param name="WarningKey">软警告的稳定 key(目前只有 <c>hooks.warn.weakMatcher</c>:阻断型钩子用了前缀 matcher)。不拦保存,界面出一条黄条。</param>
public sealed record HooksSaveResult(HookDto Hook, string? WarningKey = null);

public sealed record HooksDeleteParams(string Scope, string? WorkspaceId, string Id);

public sealed record HooksSetEnabledParams(string Scope, string? WorkspaceId, string Id, bool Enabled);

/// <param name="Hook">草稿;<see cref="HookUpsertDto.Id"/> 非空时,环境变量里「值为 null」的行用那条已存钩子的值(单向复用,值不回传)。</param>
public sealed record HooksTestParams(string Scope, string? WorkspaceId, HookUpsertDto Hook);

/// <summary>一条钩子跑完的结果(试运行的返回值)。stdout 最多 8KB、stderr 最多 4KB,环境变量的值已抹掉。</summary>
/// <param name="Outcome"><c>ok</c> / <c>blocked</c> / <c>timeout</c> / <c>spawn-failed</c>(<c>ok</c> 不代表退出码是 0)。</param>
/// <param name="Decision"><c>allow</c> / <c>deny</c> / <c>ask</c>。</param>
/// <param name="Preview">prompt 型试运行才有:$ARGUMENTS 展开后的文本(没有真调模型)。</param>
/// <param name="PromptVerdict"><c>met</c> / <c>not_met</c> / <c>impossible</c> / <c>skipped</c>(prompt 型才有)。</param>
public sealed record HookRunReportDto(
    string HookId,
    string Scope,
    int? ExitCode,
    string Stdout,
    string Stderr,
    long DurationMs,
    string Outcome,
    string? Decision = null,
    string? Reason = null,
    string? AdditionalContext = null,
    string? Preview = null,
    string? PromptVerdict = null);

/// <summary>Hooks 的载荷形状(与其它领域同属一个 <c>PayloadShapes</c>)。</summary>
[GenerateShapeFor<HooksListParams>]
[GenerateShapeFor<HooksListResult>]
[GenerateShapeFor<HooksDiagnosticsResult>]
[GenerateShapeFor<HooksSaveParams>]
[GenerateShapeFor<HooksSaveResult>]
[GenerateShapeFor<HooksDeleteParams>]
[GenerateShapeFor<HooksSetEnabledParams>]
[GenerateShapeFor<HooksTestParams>]
[GenerateShapeFor<HookRunReportDto>]
public sealed partial class PayloadShapes;
