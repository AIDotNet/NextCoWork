using Ncw.Agent.Llm;

namespace Ncw.Agent.Kernel.Loop;

/// <summary>
/// 循环层产生的文字。<b>都是发给模型看的英文,不是界面文字</b>(界面上这些结果卡片由控制端按 IsError / 事件类型自己渲染)。
/// 方括号开头的是「没有真的发生」的说明,和 2.x 的写法一致。
/// </summary>
public static class LoopMessages
{
    public const string Interrupted = "[interrupted: the user stopped the run before this tool call finished]";

    public const string RecoveredInterrupted = "[not executed: the previous run ended before this tool call produced a result]";

    public const string RunFailed = "[not executed: the run failed before this tool could finish]";

    public const string StreamFailed = "[not executed: the upstream response did not complete successfully]";

    public const string OutputLimit =
        "[not executed: the response reached the output limit, so the arguments of this tool call may have been truncated. Re-issue the tool call.]";

    public const string DefaultDenied = "The user denied this tool call.";

    public static string UnknownTool(string name, IReadOnlyList<string> available) =>
        $"There is no tool named {name}. Pick one from the list of available tools: {(available.Count == 0 ? "(none)" : string.Join(", ", available))}.";

    public static string InvalidArguments(string detail) =>
        $"The tool arguments were not valid JSON or were cut off. Re-issue the tool call with valid arguments. Details: {detail}";

    public static string ToolFailed(Exception e) => $"Tool execution failed: {e.GetType().Name}: {e.Message}";

    public static string TruncationNote(int total, int kept) =>
        $"\n\n[Output truncated: {total} characters in total; only the first {kept} were kept]";
}

/// <summary>循环层本地生成的错误的 MessageKey(控制端按它翻译)。</summary>
public static class LoopErrorKeys
{
    /// <summary>没有工具调用的回合撞上输出上限(2.x <c>agent.error.outputLimit</c>)。</summary>
    public const string OutputLimit = "loop.outputLimit";

    /// <summary>循环层内部(钩子、落库)抛出了意料之外的异常。</summary>
    public const string Internal = "loop.internal";
}
