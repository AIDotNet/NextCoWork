using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel;
using Ncw.Agent.Kernel.Tools;

namespace Ncw.Agent.Extensions.Interactions;

/// <summary>交互的标识(闸门分配,ULID 风格)。</summary>
public readonly record struct InteractionId(string Value)
{
    public override string ToString() => Value;
}

/// <summary>
/// 执行环境标识(设备 + 环境)——审批和工具卡片必须带上它(AGENTS.md §5)。同一台远程主机从「本机」和从「云端」打开是两个不同的环境。
/// 字段和工具结果 Details 里的 <c>environment</c> 对象一致:<c>{deviceId, environment, name, type}</c>。
/// </summary>
public sealed record ExecutionEnvironmentRef(string DeviceId, string Environment, string Name, string Type)
{
    public static ExecutionEnvironmentRef From(IExecutionEnvironment environment) => new(
        environment.Id.DeviceId,
        environment.Id.Environment,
        environment.DisplayName,
        environment.Kind == EnvironmentKind.Local ? "local" : "ssh");

    public EnvironmentTag ToTag() => new(DeviceId, Environment);
}

/// <summary>「记住本次选择」的作用域(03 §3):会话(进程内,会话结束即忘)/ 项目(<c>settings.local.json</c>)/ 永久(全局,所有项目)。</summary>
public enum RememberScope
{
    Session,
    Project,
    Global,
}

[Flags]
public enum RememberScopes
{
    None = 0,
    Session = 1,
    Project = 2,
    Global = 4,
    All = Session | Project | Global,
}

public static class RememberScopeExtensions
{
    public static RememberScopes ToFlag(this RememberScope scope) => scope switch
    {
        RememberScope.Session => RememberScopes.Session,
        RememberScope.Project => RememberScopes.Project,
        RememberScope.Global => RememberScopes.Global,
        _ => RememberScopes.None,
    };
}

/// <summary>Bash 命令的粗略风险分级(<see cref="Permissions.BashCommand"/>)。只是事实描述,不是策略:它不会放行任何命令。</summary>
public enum CommandRisk
{
    /// <summary>认不出来:按 Bash 的特征(非只读、破坏性)处理。</summary>
    Unknown,

    /// <summary>保守地认定只读(白名单命令、没有重定向和命令替换)。</summary>
    ReadOnly,

    /// <summary>已知的危险模式(递归强删、提权、格式化磁盘、强推、<c>curl | sh</c> 等)。</summary>
    Dangerous,
}

// ───────────────────────── 请求 ─────────────────────────

/// <summary>
/// 一个挂起的交互(03 §4)。<see cref="Id"/> / <see cref="RunId"/> / <see cref="CreatedAt"/> / <see cref="Version"/> 由闸门在登记时填(调用方给的草稿里它们是空值)。
/// <see cref="Version"/> 是登记那一刻闸门的单调修订号(全设备唯一、只增):控制端回答时带着它(<c>expectedVersion</c>),闸门据此识别「答的是不是我看到的那一份」。
/// 不含任何界面文字:控制端按类型翻译。
/// </summary>
public abstract record InteractionRequest
{
    public InteractionId Id { get; init; }

    public RunId RunId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public long Version { get; init; }

    /// <summary>线上的种类名(和 2.x 一致:<c>tool_permission</c> / <c>ask_user</c> / …)。</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// 为真 = Run 正常结束(<c>Done</c>)时不清理它,之后仍可回答(2.x ★:目标提案不占住 Run,批准要问的是「这个目标要不要」,而模型这时候该继续干活)。
    /// 中止 / 出错的 Run 一律清理。
    /// </summary>
    public virtual bool SurvivesNormalRunEnd => false;

    /// <summary>
    /// 为真 = 回答里有秘密(SSH 密码、私钥口令):<c>interaction_resolved</c> 事件里<b>不带回答</b>(只有谁、什么时候),别的控制端和事件日志都看不到它。
    /// 回答只经 <see cref="InteractionGate.Respond"/> 交给发起方(等待者),不进任何通知。
    /// </summary>
    public virtual bool RedactsResponse => false;

    /// <summary>草稿合法性(发起方的错误,抛 <see cref="ArgumentException"/>)。返回 null = 合法。</summary>
    public virtual string? ValidateDraft() => null;

    /// <summary>回答的校验(控制端的错误,不消耗这次交互)。返回 null = 合法,否则是给控制端的英文诊断。</summary>
    public abstract string? Validate(InteractionResponse response);

    protected string? KindMismatch(InteractionResponse response) =>
        response.Kind == Kind ? null : $"The response kind '{response.Kind}' does not match the interaction kind '{Kind}'.";
}

/// <summary>
/// 工具审批。<see cref="Env"/> 必填:审批卡片必须说清「在哪台设备的哪个环境里执行」。
/// <see cref="SuggestedRule"/> 由 Agent 算(不由控制端拼);<see cref="AllowedScopes"/> 是这次可以「记住」的作用域(没有项目根就没有 Project)。
/// </summary>
public sealed record ToolPermission : InteractionRequest
{
    public required string CallId { get; init; }

    /// <summary>模型看到的名字(外部名)。</summary>
    public required string ToolName { get; init; }

    /// <summary>稳定 id,规则按它匹配。</summary>
    public required string InternalId { get; init; }

    public required JsonElement Input { get; init; }

    public required ToolTraits Traits { get; init; }

    public required ExecutionEnvironmentRef Env { get; init; }

    public string? SuggestedRule { get; init; }

    public RememberScopes AllowedScopes { get; init; } = RememberScopes.Session;

    /// <summary>仅 Bash:命令风险分级。其它工具为 null。</summary>
    public CommandRisk? Risk { get; init; }

    /// <summary>这次调用要碰的路径(规范化之后)落在项目根外面。</summary>
    public bool OutsideWorkspace { get; init; }

    public override string Kind => "tool_permission";

    public override string? ValidateDraft() => Env is null ? "A tool approval must carry the execution environment." : null;

    public override string? Validate(InteractionResponse response)
    {
        if (KindMismatch(response) is { } mismatch)
        {
            return mismatch;
        }

        switch (((ToolPermissionResponse)response).Choice)
        {
            case PermissionChoice.AllowAlwaysChoice always:
                if (SuggestedRule is null)
                {
                    return "There is no suggested rule to remember for this call.";
                }

                // 2.x 只收 workspace:规则落在有真实存放处的地方;现在三种作用域各有存放处,但仍只收这次明确开放的。
                return (AllowedScopes & always.Scope.ToFlag()) == 0 ? $"The scope '{always.Scope}' is not available for this approval." : null;
            case PermissionChoice.AllowEditedChoice edited:
                return edited.Input.ValueKind == JsonValueKind.Object ? null : "An edited call must carry a JSON object as its input.";
            case PermissionChoice.AllowOnceChoice:
                return null;
            case PermissionChoice.DenyChoice deny:
                return deny.Reason is { Length: > 8192 } ? "The denial reason is too long." : null;
            default:
                return "Unknown permission choice.";
        }
    }
}

public sealed record AskUserOption(string Label, string? Description = null);

/// <summary>
/// 一道题。三种题型压在同一个结构里:单选(<c>MultiSelect=false</c> + 选项)、多选(<c>true</c> + 选项)、纯问答(选项为空,此时 <see cref="AllowFreeform"/> 必须为真)。
/// <see cref="AllowFreeform"/> 和选项正交:「给了选项,但也允许自己写」是最常用的一档(界面上的「其它」)。
/// </summary>
public sealed record AskUserQuestion(string Header, string Question, IReadOnlyList<AskUserOption> Options, bool MultiSelect, bool AllowFreeform);

/// <summary>
/// 提问(AskUserQuestion 工具)。一次交互承载 1–4 道题,每题最多 8 个选项(03 §2)——模型在一个决策点上要问的往往不止一件事,一道一问会让内核连续挂起两次。
/// </summary>
public sealed record AskUser(IReadOnlyList<AskUserQuestion> Questions) : InteractionRequest
{
    public const int MaxQuestions = 4;

    public const int MaxOptions = 8;

    public const int MaxAnswerLength = 32768;

    public override string Kind => "ask_user";

    public override string? ValidateDraft()
    {
        if (Questions is not { Count: >= 1 and <= MaxQuestions })
        {
            return $"An interaction asks 1 to {MaxQuestions} questions.";
        }

        foreach (var q in Questions)
        {
            if (q.Options.Count > MaxOptions)
            {
                return $"A question has at most {MaxOptions} options.";
            }

            if (q.Options.Count == 0 && !q.AllowFreeform)
            {
                return "A question without options must allow a free-form answer, otherwise it cannot be answered.";
            }

            if (q.Options.Any(static o => string.IsNullOrWhiteSpace(o.Label)) || q.Options.Select(static o => o.Label).Distinct(StringComparer.Ordinal).Count() != q.Options.Count)
            {
                return "Option labels must be non-empty and unique within a question.";
            }
        }

        return null;
    }

    public override string? Validate(InteractionResponse response)
    {
        if (KindMismatch(response) is { } mismatch)
        {
            return mismatch;
        }

        var answers = ((AskUserResponse)response).Answers;
        if (answers is null)
        {
            return null; // dismiss:用户关掉了框,不是回答
        }

        // 长度必须严格等于题数:答案是按下标对到题上的,少一项说明控制端和待决表已经不是同一份题面(窗口重载时抢答),
        // 这时按下标对齐会把答案安到别的题上。
        if (answers.Count != Questions.Count)
        {
            return "The number of answers does not match the number of questions.";
        }

        for (var i = 0; i < answers.Count; i++)
        {
            var answer = answers[i];
            var question = Questions[i];
            if (answer is null || answer.Count == 0)
            {
                return $"Question {i + 1} has no answer.";
            }

            if (!question.MultiSelect && answer.Count > 1)
            {
                return $"Question {i + 1} accepts a single answer.";
            }

            if (answer.Distinct(StringComparer.Ordinal).Count() != answer.Count)
            {
                return $"Question {i + 1} has a repeated answer.";
            }

            foreach (var value in answer)
            {
                if (string.IsNullOrEmpty(value) || value.Length > MaxAnswerLength)
                {
                    return $"Question {i + 1} has an empty or oversized answer.";
                }

                if (!question.AllowFreeform && !question.Options.Any(o => o.Label == value))
                {
                    return $"Question {i + 1}: '{value}' is not one of the options.";
                }
            }
        }

        return null;
    }
}

public enum PlanApprovalAction
{
    ApproveCurrent,
    ApproveNewSession,
    RequestRevision,
    Reject,
}

public sealed record PlanApproval(string Plan, string PlanId, string Path) : InteractionRequest
{
    public override string Kind => "plan_approval";

    public override string? Validate(InteractionResponse response)
    {
        if (KindMismatch(response) is { } mismatch)
        {
            return mismatch;
        }

        var r = (PlanApprovalResponse)response;
        if (!Enum.IsDefined(r.Action))
        {
            return "Unknown plan action.";
        }

        return r.Action == PlanApprovalAction.RequestRevision && string.IsNullOrWhiteSpace(r.Feedback) ? "A revision request must say what to change." : null;
    }
}

/// <summary>目标提案。不占住 Run(<see cref="SurvivesNormalRunEnd"/>):Run 正常结束后仍可回答,按会话取消。</summary>
public sealed record GoalProposal(string SessionId, string Condition) : InteractionRequest
{
    public override string Kind => "goal_proposal";

    public override bool SurvivesNormalRunEnd => true;

    public override string? Validate(InteractionResponse response) => KindMismatch(response);
}

public enum WorkflowApprovalAction
{
    Run,
    Revise,
    Cancel,
}

public sealed record WorkflowApproval(string CallId, JsonElement Workflow) : InteractionRequest
{
    public override string Kind => "workflow_approval";

    public override string? Validate(InteractionResponse response)
    {
        if (KindMismatch(response) is { } mismatch)
        {
            return mismatch;
        }

        var r = (WorkflowApprovalResponse)response;
        if (!Enum.IsDefined(r.Action))
        {
            return "Unknown workflow action.";
        }

        return r.Action == WorkflowApprovalAction.Revise && string.IsNullOrWhiteSpace(r.Feedback) ? "A revision request must say what to change." : null;
    }
}

public sealed record WidgetSubmit(string CallId, string Title) : InteractionRequest
{
    public const int MaxValueLength = 32 * 1024;

    public override string Kind => "widget_submit";

    public override string? Validate(InteractionResponse response)
    {
        if (KindMismatch(response) is { } mismatch)
        {
            return mismatch;
        }

        return ((WidgetSubmitResponse)response).Value is { Length: > MaxValueLength } ? "The submitted value is too large." : null;
    }
}

/// <summary>
/// SSH 主机指纹确认(06 §3「主机指纹」):这台设备第一次连这台主机(或这个算法),让用户核对指纹后决定是否信任并钉扎。
/// <b>只有「首次出现」才会走这里</b>:指纹和钉扎的不一致时连接直接失败(<c>HostKeyChanged</c>),不弹确认、不覆盖旧指纹——「不一致」的详情进环境状态,不是交互。
/// 不是某个 Run 的事:<see cref="InteractionRequest.RunId"/> 是设备级作用域(<c>env:&lt;环境 id&gt;:…</c>),不属于任何会话。
/// </summary>
/// <param name="HostName">钉扎用的主机名(默认端口是裸主机名,否则 <c>[host]:port</c>,与 known_hosts 同形;多跳时是这一跳的)。</param>
/// <param name="Algorithm">主机密钥算法(<c>ssh-ed25519</c> ……)。</param>
/// <param name="Fingerprint">SHA256 指纹(<c>ssh-keygen -l</c> 同款,Base64 不带 <c>=</c> 和 <c>SHA256:</c> 前缀)。</param>
public sealed record SshHostKey(ExecutionEnvironmentRef Env, string HostName, string Algorithm, string Fingerprint) : InteractionRequest
{
    public override string Kind => "ssh_host_key";

    public override string? ValidateDraft() => Env is null ? "An SSH host key confirmation must carry the execution environment." : null;

    public override string? Validate(InteractionResponse response) => KindMismatch(response);
}

public enum SshCredentialPromptKind
{
    Password,

    /// <summary>私钥的口令。<see cref="SshCredential.KeyName"/> 是私钥文件名(不含目录)。</summary>
    KeyPassphrase,

    /// <summary>键盘交互式认证(两步验证等):<see cref="SshCredential.Prompts"/> 是服务器给的问题。</summary>
    KeyboardInteractive,
}

public sealed record SshCredentialPrompt(string Text, bool Echo);

/// <summary>
/// SSH 凭据提示(密码 / 私钥口令 / 键盘交互式问答)。任何已连接的控制端都能回答,先到先得。
/// <b>回答里有秘密</b>:<see cref="RedactsResponse"/> 为真,<c>interaction_resolved</c> 事件不带它;请求本身只有「问什么」,不含任何凭据(已保存的凭据不会出现在这里)。
/// </summary>
/// <param name="HostName">在向哪一跳要凭据(多跳时每跳都不同,是钉扎用的主机名)。</param>
/// <param name="KeyName">私钥文件名(只有文件名,不含目录);只在 <see cref="SshCredentialPromptKind.KeyPassphrase"/> 时有值。</param>
/// <param name="Instruction">键盘交互式的服务器说明文字。</param>
/// <param name="PreviousRejected">这次是因为上一次给的(含已保存的)被拒绝才再问的。</param>
/// <param name="CanRemember">这次的回答能不能「记住」(已保存的环境才能;试连接的草稿不能)。</param>
/// <param name="HasSaved">设备上已经保存了这一项(被拒绝过,所以才再问):界面可以提示「已保存的不对」。</param>
public sealed record SshCredential(
    ExecutionEnvironmentRef Env,
    SshCredentialPromptKind PromptKind,
    string HostName,
    string UserName,
    string? KeyName = null,
    string? Instruction = null,
    IReadOnlyList<SshCredentialPrompt>? Prompts = null,
    bool PreviousRejected = false,
    bool CanRemember = false,
    bool HasSaved = false) : InteractionRequest
{
    /// <summary>密码 / 口令 / 一个回答的长度上限。</summary>
    public const int MaxAnswerLength = 8192;

    public const int MaxPrompts = 16;

    public override string Kind => "ssh_credential";

    public override bool RedactsResponse => true;

    public override string? ValidateDraft() => Env is null
        ? "An SSH credential prompt must carry the execution environment."
        : PromptKind == SshCredentialPromptKind.KeyboardInteractive && Prompts is not { Count: >= 1 and <= MaxPrompts }
            ? $"A keyboard-interactive prompt has 1 to {MaxPrompts} questions."
            : null;

    public override string? Validate(InteractionResponse response)
    {
        if (KindMismatch(response) is { } mismatch)
        {
            return mismatch;
        }

        var r = (SshCredentialResponse)response;
        if (r.Answers is null)
        {
            return null; // 取消:用户不想提供凭据,连接会以「已取消」结束
        }

        var expected = PromptKind == SshCredentialPromptKind.KeyboardInteractive ? Prompts!.Count : 1;
        if (r.Answers.Count != expected)
        {
            return "The number of answers does not match the number of questions.";
        }

        foreach (var answer in r.Answers)
        {
            if (answer is null || answer.Length > MaxAnswerLength || answer.Contains('\0'))
            {
                return "An answer is missing, too long, or contains a NUL character.";
            }

            // 密码 / 口令不能为空(空的认证一定被拒,还会白白烧一次尝试);键盘交互式的个别回答可以空
            if (PromptKind != SshCredentialPromptKind.KeyboardInteractive && answer.Length == 0)
            {
                return "The password or passphrase is empty.";
            }
        }

        return r.Remember && !CanRemember ? "This credential cannot be remembered." : null;
    }
}

// ───────────────────────── 回答 ─────────────────────────

public abstract record InteractionResponse
{
    public abstract string Kind { get; }
}

/// <summary>
/// 用户对一次工具审批的回答。刻意和闸门自己的结论(<see cref="Permissions.GateOutcome"/>)分成两个类型:
/// 「谁有资格说 always」在类型上必须看得出来。规则文本不从这里进——Agent 用请求里那条 <see cref="ToolPermission.SuggestedRule"/> 自己写。
/// </summary>
public abstract record PermissionChoice
{
    private PermissionChoice()
    {
    }

    public static PermissionChoice AllowOnce { get; } = new AllowOnceChoice();

    public static PermissionChoice AllowAlways(RememberScope scope) => new AllowAlwaysChoice(scope);

    /// <summary>用户改写了参数。改写后的参数会重新走一遍规则检查(3.0 新增,2.x 没有,是个漏洞)和工具的参数校验。</summary>
    public static PermissionChoice AllowEdited(JsonElement input) => new AllowEditedChoice(input);

    public static PermissionChoice Deny(string? reason = null) => new DenyChoice(reason);

    public sealed record AllowOnceChoice : PermissionChoice;

    public sealed record AllowAlwaysChoice(RememberScope Scope) : PermissionChoice;

    public sealed record AllowEditedChoice(JsonElement Input) : PermissionChoice;

    public sealed record DenyChoice(string? Reason) : PermissionChoice;
}

public sealed record ToolPermissionResponse(PermissionChoice Choice) : InteractionResponse
{
    public override string Kind => "tool_permission";
}

/// <param name="Answers"><c>null</c> = dismiss(用户关掉了框,不是回答)。否则每道题一组回答,顺序与题一一对应;单选题也是长度为 1 的列表。</param>
public sealed record AskUserResponse(IReadOnlyList<IReadOnlyList<string>>? Answers) : InteractionResponse
{
    public override string Kind => "ask_user";
}

public sealed record PlanApprovalResponse(PlanApprovalAction Action, string? Feedback = null) : InteractionResponse
{
    public override string Kind => "plan_approval";
}

public sealed record GoalProposalResponse(bool Approved) : InteractionResponse
{
    public override string Kind => "goal_proposal";
}

public sealed record WorkflowApprovalResponse(WorkflowApprovalAction Action, string? Feedback = null) : InteractionResponse
{
    public override string Kind => "workflow_approval";
}

/// <param name="Value"><c>null</c> = 用户关掉了,没有提交。否则是 widget <c>submit()</c> 的值的 JSON 文本。</param>
public sealed record WidgetSubmitResponse(string? Value) : InteractionResponse
{
    public override string Kind => "widget_submit";
}

/// <param name="Trust"><c>true</c> = 信任并钉扎这个指纹,继续连接;<c>false</c> = 拒绝(连接以 <c>HostKeyRejected</c> 失败,不发任何凭据)。</param>
public sealed record SshHostKeyResponse(bool Trust) : InteractionResponse
{
    public override string Kind => "ssh_host_key";
}

/// <param name="Answers"><c>null</c> = 取消。否则密码 / 口令是一项,键盘交互式每个问题一项(与 <see cref="SshCredential.Prompts"/> 顺序一致)。</param>
/// <param name="Remember">用 NCK1 把这次的密码 / 口令存在设备上(只对 <see cref="SshCredential.CanRemember"/> 的请求有效)。</param>
public sealed record SshCredentialResponse(IReadOnlyList<string>? Answers, bool Remember = false) : InteractionResponse
{
    public override string Kind => "ssh_credential";

    /// <summary>★ 回答里有秘密:任何自动生成的 <c>ToString()</c>(日志、断言消息、调试器)都不能带出它。</summary>
    public override string ToString() => $"SshCredentialResponse {{ Answers = {(Answers is null ? "cancelled" : $"{Answers.Count} redacted")}, Remember = {Remember} }}";
}
