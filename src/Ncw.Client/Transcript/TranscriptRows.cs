using Ncw.Client.Stores;
using Ncw.Client.Widgets;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Transcript;

/// <summary>
/// 对话转录里的一行(纯数据,由 <see cref="TranscriptBuilder"/> 从 Store 里的会话、Run、子代理、交互派生,不持有任何事实)。
/// <see cref="Key"/> 在同一份转录里稳定:同一行流式中和落库后是同一个 key,视图据此原地更新而不是重建(保住展开 / 折叠这类纯 UI 状态)。
/// 记录是值相等的(只含字符串、数字、其他记录),所以「没变」可以用 Equals 判定。不含任何界面文字。
/// </summary>
public abstract record TranscriptRow(string Key);

/// <summary>用户消息。<see cref="FilesText"/> 是引用的文件名(逗号分隔);图片只给个数。</summary>
public sealed record UserRow(string Key, string MessageId, string Text, string FilesText, int ImageCount, DateTimeOffset At) : TranscriptRow(Key);

/// <summary>助手的一段文字(Markdown)。<see cref="IsStreaming"/> = 还在流,文字是到目前为止的增量。</summary>
public sealed record AssistantRow(string Key, string MessageId, string Text, bool IsStreaming) : TranscriptRow(Key);

/// <summary>思考块(默认折叠)。<see cref="DurationMs"/> 只有落库后才有。</summary>
public sealed record ThinkingRow(string Key, string Text, long? DurationMs, bool IsStreaming) : TranscriptRow(Key);

public enum ToolKind
{
    Other,
    Read,
    Edit,
    Write,
    Command,
    Search,
}

/// <summary>工具卡片的状态。<see cref="WaitingApproval"/>:有一条挂起的审批指向这次调用;<see cref="Interrupted"/>:调用没有回执而 Run 已经不在了。</summary>
public enum ToolRowStatus
{
    Running,
    Done,
    Failed,
    WaitingApproval,
    Interrupted,
}

/// <summary>
/// 一次工具调用。<see cref="Env"/> 说清在哪台设备的哪个环境里执行(AGENTS.md §5):实时事件带的优先,落库的旧调用用项目所在环境兜底。
/// <see cref="Preview"/> 是摘要:Edit / Write 是 <c>+</c> / <c>-</c> 开头的 diff 行,命令是输出的前几行(每行一个前导空格);<see cref="MoreLines"/> 是没显示的行数。
/// </summary>
public sealed record ToolRow(
    string Key,
    string CallId,
    string Name,
    ToolKind Kind,
    string Target,
    ToolRowStatus Status,
    EnvRefDto? Env,
    string Preview,
    int MoreLines,
    int Added,
    int Removed,
    bool Truncated,
    int? QueuedAhead) : TranscriptRow(Key);

/// <summary>子代理卡片(<c>Task</c> 调用)。状态文案、计时、能否停止都来自 Core 的 <see cref="SubagentView"/>,这里只带结果。</summary>
public sealed record SubagentRow(string Key, SubagentRef Ref, SubagentViewState View, string? Summary, string? Color, bool Unknown) : TranscriptRow(Key);

/// <summary>对话内 Agent 动态工作流卡片(<c>RunWorkflow</c> 调用):不被折叠进工作区分组,直接用 <c>TKCode.Arc.Workflow</c> 渲染。</summary>
public sealed record WorkflowRow(
    string Key,
    string CallId,
    string? RunId,
    string Title,
    WorkflowDefinition? FallbackDefinition,
    ToolRowStatus Status,
    EnvRefDto? Env) : TranscriptRow(Key);

/// <summary>汇报行:后台子代理的结果回传到了主代理。</summary>
public sealed record SubagentReportRow(string Key, string CallId, string ChildRunId, string? Summary) : TranscriptRow(Key);

/// <summary>压缩边界。</summary>
public sealed record CompactRow(string Key, string Trigger, int PreTokens, int PostTokens, string Summary) : TranscriptRow(Key);

/// <summary>错误。<see cref="MessageKey"/> 由界面翻译(没有就显示 <see cref="Message"/> 的英文诊断);<see cref="CanRetry"/> 只有最后一条错误可以重试。</summary>
public sealed record ErrorRow(string Key, string Code, string Message, string? MessageKey, bool Retryable, bool CanRetry) : TranscriptRow(Key);

/// <summary>目标状态(达成 / 未达成 / 已设定 / 已清除)。</summary>
public sealed record GoalStatusRow(string Key, bool Met, string Condition, string? Reason, bool Failed, bool Cleared, bool Set) : TranscriptRow(Key);

public enum RunStatusKind
{
    Retrying,
    Compacting,
    Unknown,
}

/// <summary>转录末尾的运行状态提示:上游重试中、正在压缩、设备断开(状态未知)。</summary>
public sealed record StatusRow(string Key, RunStatusKind Kind, int Attempt, int DelayMs) : TranscriptRow(Key);

/// <summary>一条挂起的交互(审批、提问、计划批准、目标提案……)。<see cref="Interaction"/> 是 Store 里的那一份,不拷贝。</summary>
public sealed record InteractionRow(string Key, InteractionDto Interaction) : TranscriptRow(Key);

/// <summary>
/// 内联可视化卡片(<c>visualize_show_widget</c> / <c>visualize_edit_widget</c> 的一次调用)。<see cref="Phase"/> 由 Core 的 <see cref="WidgetView"/> 这一个函数从 <see cref="Facts"/> 派生
/// (桌面卡片、手机快照卡共用,不各判一遍)。<see cref="Card"/> 是引用相等的(同一份 Details 只解析一次);<see cref="DataJson"/> 是这张卡按标题认领的最新一份 <c>visualize_update_widget</c> 数据。
/// 卡片里有「设备 + 环境」标识(AGENTS.md §5):实时事件带的优先,卡片自带的次之,项目环境兜底。
/// </summary>
public sealed record WidgetRow(
    string Key,
    string CallId,
    string ToolName,
    string Title,
    WidgetCard? Card,
    WidgetPhase Phase,
    WidgetFacts Facts,
    string LoadingText,
    string? DataJson,
    EnvRefDto? Env) : TranscriptRow(Key);

/// <summary>
/// 「工作区」分组表头:一轮已经全部完成(没有运行中的 Run、没有待处理的交互、没有还在跑的工具 / 子代理)之后,这一轮里除<b>最后一段回复文字</b>以外的操作
/// (思考、工具调用、子代理、子代理汇报、中间的文字)收成一组(参考 Codex 的「已处理」折叠)。
/// 转录仍然是平铺的:表头后面紧跟 <see cref="Members"/> 行就是这一组的成员,是否展开是纯 UI 状态(<c>ConversationViewModel</c>),收起时成员行不创建视图模型。
/// <see cref="Actions"/> 是成员里的工具调用 + 子代理个数,<see cref="Failed"/> 是其中失败的工具个数;界面据此拼摘要(Client 不产出界面文字)。
/// </summary>
public sealed record WorkGroupRow(string Key, int Members, int Actions, int Failed) : TranscriptRow(Key);
