using Ncw.Agent.Kernel.Session;

namespace Ncw.Agent.Kernel.Subagents;

/// <summary>
/// 不可信文本(用户 / 项目目录里的子代理文件)进工具描述和系统提示词之前的消毒(2.x 的 <c>text.ts</c>):
/// 委托给会话层的 <see cref="PromptText"/>,「哪些字符要削」只有一份答案。
/// </summary>
internal static class AgentText
{
    public static string StripControlChars(string text) => PromptText.StripControlChars(text);

    public static string Clamp(string text, int max) => PromptText.Clamp(text, max);
}
