/**
 * 压缩提示词 —— 照 Claude Code 的 compact prompt 写。
 *
 * 需求:上下文压缩按 Claude Code 的模型重写,摘要的**形状**是这套机制能不能用的
 * 一半:压完之后模型手里只剩这份摘要 + 重附的文件,摘要漏了「用户原话」或
 * 「正在改哪一行」,下一轮它就会重新问一遍、或者把已经做完的事再做一遍。
 * 九节结构(意图 / 概念 / 文件与代码 / 错误 / 问题 / 用户原话 / 待办 / 当前工作 /
 * 下一步)就是为了堵这两个洞,逐节照搬,不要「精简」。
 *
 * 不变式:
 * - 摘要请求发的是**原始对话**(同一段 `messagesForModel`)+ 末尾一条压缩指令,
 *   不是旧实现那种把转录拍平成一段 digest 文本。模型读原文才分得清谁说了什么、
 *   哪次工具失败了;digest 还要预算、还会漏,漏掉的部分正是旧实现的事故源头。
 * - `<analysis>` 是给模型打草稿的,**不进上下文**(`formatCompactSummary` 剥掉)。
 *
 * 故意不做的:不翻译提示词、不按界面语言切换 —— 这是给模型的指令,不是用户可见文案
 * (§6 i18n 管不到它);摘要用什么语言写由对话本身决定。
 */

/**
 * ★ 放在指令**最前面**。压缩请求不下发工具 schema,但历史里全是 tool_call,
 * 有的模型会照着历史的样子「继续调用工具」,产出一段伪造的调用文本而不是摘要。
 * CC 在同一个位置放了同样的禁令。
 */
const NO_TOOLS_PREAMBLE = `CRITICAL: Respond with TEXT ONLY. Do NOT call any tools.

- Do NOT use Read, Bash, Grep, Glob, Edit, Write, or ANY other tool.
- You already have all the context you need in the conversation above.
- Tool calls will be REJECTED and will waste your only turn — you will fail the task.
- Your entire response must be plain text: an <analysis> block followed by a <summary> block.

`

const COMPACT_PROMPT = `Your task is to create a detailed summary of the conversation so far, paying close attention to the user's explicit requests and your previous actions.
This summary should be thorough in capturing technical details, code patterns, and architectural decisions that would be essential for continuing development work without losing context.

Before providing your final summary, wrap your analysis in <analysis> tags to organize your thoughts and ensure you've covered all necessary points. In your analysis process:

1. Chronologically analyze each message and section of the conversation. For each section thoroughly identify:
   - The user's explicit requests and intents
   - Your approach to addressing the user's requests
   - Key decisions, technical concepts and code patterns
   - Specific details like:
     - file names
     - full code snippets
     - function signatures
     - file edits
   - Errors that you ran into and how you fixed them
   - Pay special attention to specific user feedback that you received, especially if the user told you to do something differently.
2. Double-check for technical accuracy and completeness, addressing each required element thoroughly.

Your summary should include the following sections:

1. Primary Request and Intent: Capture all of the user's explicit requests and intents in detail
2. Key Technical Concepts: List all important technical concepts, technologies, and frameworks discussed.
3. Files and Code Sections: Enumerate specific files and code sections examined, modified, or created. Pay special attention to the most recent messages and include full code snippets where applicable and include a summary of why this file read or edit is important.
4. Errors and fixes: List all errors that you ran into, and how you fixed them. Pay special attention to specific user feedback that you received, especially if the user told you to do something differently.
5. Problem Solving: Document problems solved and any ongoing troubleshooting efforts.
6. All user messages: List ALL user messages that are not tool results. These are critical for understanding the users' feedback and changing intent.
7. Pending Tasks: Outline any pending tasks that you have explicitly been asked to work on.
8. Current Work: Describe in detail precisely what was being worked on immediately before this summary request, paying special attention to the most recent messages from both user and assistant. Include file names and code snippets where applicable.
9. Optional Next Step: List the next step that you will take that is related to the most recent work you were doing. IMPORTANT: ensure that this step is DIRECTLY in line with the user's most recent explicit requests, and the task you were working on immediately before this summary request. If your last task was concluded, then only list next steps if they are explicitly in line with the users request. Do not start on tangential requests or really old requests that were already completed without confirming with the user first.
   If there is a next step, include direct quotes from the most recent conversation showing exactly what task you were working on and where you left off. This should be verbatim to ensure there's no drift in task interpretation.

Here's an example of how your output should be structured:

<example>
<analysis>
[Your thought process, ensuring all points are covered thoroughly and accurately]
</analysis>

<summary>
1. Primary Request and Intent:
   [Detailed description]

2. Key Technical Concepts:
   - [Concept 1]
   - [Concept 2]

3. Files and Code Sections:
   - [File Name 1]
      - [Summary of why this file is important]
      - [Summary of the changes made to this file, if any]
      - [Important Code Snippet]

4. Errors and fixes:
    - [Detailed description of error 1]:
      - [How you fixed the error]
      - [User feedback on the error if any]

5. Problem Solving:
   [Description of solved problems and ongoing troubleshooting]

6. All user messages:
    - [Detailed non tool use user message]

7. Pending Tasks:
   - [Task 1]

8. Current Work:
   [Precise description of current work]

9. Optional Next Step:
   [Optional Next step to take]
</summary>
</example>

Please provide your summary based on the conversation so far, following this structure and ensuring precision and thoroughness in your response.

The summary should be written in the same language the user has been using in the conversation.`

const NO_TOOLS_TRAILER = `

REMINDER: Do NOT call any tools. Respond with plain text only — an <analysis> block followed by a <summary> block.`

/**
 * 压缩指令(作为最后一条 user 消息追加在原始对话之后)。
 *
 * `instructions` 是 `/compact <补充>` 里用户写的那段,CC 把它接在
 * 「Additional Instructions:」之后 —— 用户可以借此要求「重点保留 X」。
 */
export function compactPrompt(instructions?: string): string {
  const extra = instructions === undefined || instructions.trim() === ''
    ? ''
    : `\n\nAdditional Instructions:\n${instructions.trim()}`
  return `${NO_TOOLS_PREAMBLE}${COMPACT_PROMPT}${extra}${NO_TOOLS_TRAILER}`
}

/**
 * 模型原始输出 → 进上下文的摘要正文。
 *
 * - 剥掉 `<analysis>`:草稿只在生成时有用,留着会把压缩省下的 token 吃回去一半。
 * - 有 `<summary>` 就取里面;没有(模型没按格式写)就取剥完 analysis 的全文 ——
 *   宁可带一点格式噪音,也不要因为少一个标签就判整次压缩失败。
 * - 连续空行压成一个。
 */
export function formatCompactSummary(raw: string): string {
  let text = raw.replace(/<analysis>[\s\S]*?<\/analysis>/gi, '')
  // 输出被截断时 analysis 可能没有闭合标签 —— 那时后面没有 summary 可取。
  text = text.replace(/<analysis>[\s\S]*$/i, '')
  const summary = /<summary>([\s\S]*?)(?:<\/summary>|$)/i.exec(text)
  if (summary?.[1] !== undefined) text = summary[1]
  return text.replace(/\n{3,}/g, '\n\n').trim()
}

/**
 * 边界消息里给模型看的那段续接语 + 摘要。
 *
 * ★ `autoContinue` 只在**自动**压缩时为真:那时 run 正在跑,压完要立刻接着干,
 * 必须告诉模型「别停下来问用户」—— 否则它会礼貌地复述一遍摘要然后问「要继续吗」,
 * 一个长任务就此停住。手动 /compact 之后是用户说下一句,不需要这句。
 * 这是 CC 的 `suppressFollowUpQuestions` 语义。
 */
export function continuationText(summary: string, autoContinue: boolean): string {
  const head =
    'This session is being continued from a previous conversation that ran out of context. ' +
    'The summary below covers the earlier portion of the conversation.\n\n'
  const tail = autoContinue
    ? '\n\nPlease continue the conversation from where we left it off without asking the user any further questions. ' +
      'Continue with the last task that you were asked to work on.'
    : ''
  return `${head}${summary}${tail}`
}
