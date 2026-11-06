/**
 * `Task` —— 派一个子代理去独立完成一件事。
 *
 * ## 它为什么值得存在
 *
 * 子代理拿到的是一个**全新的上下文窗**:父代理那几十轮对话、几万字符的
 * 文件内容,一个字都不会带过去,回来的也只有一段结论。于是「在这个仓库里
 * 找出所有调用了 X 的地方」这种要翻二十个文件的活,可以在**不污染主对话**
 * 的前提下做完 —— 那二十个文件的内容留在子代理的窗口里,和它一起消失。
 *
 * ## 描述是**注册时**拼出来的,不是模块加载时
 *
 * 可用的 `subagent_type` 清单逐字写在 description 里(照搬 CC)——
 * 模型判断「这活派给谁」唯一的依据就是那几行。所以这里导出的是一个
 * **工厂函数**而不是一个常量:子代理注册表重扫之后,`runtime` 会再调一次
 * 并重新 `register()`,清单跟着变。`ToolRegistry.register` 按 internalId
 * 幂等替换、且保住 externalName,所以重注册不会让历史转录里的引用失配。
 *
 * ## 这个工具本身不碰 store、不碰 run 注册表
 *
 * 它只调 `ctx.spawnSubagent`(见 `tool/registry.ts` 上那条窄缝的说明)。
 * 建 run、继承订阅、发 `subagent_start/end`、取子代理的产出 —— 全在
 * `runtime.ts` 的启动器里,因为只有它同时握着父 handle 和子 runId。
 */
import { z } from 'zod'
import { MAX_DEPTH } from '../../../../shared/agent/run-request'
import { agentError } from '../../../../shared/agent/error'
import { toolFail, toolOk } from '../../../../shared/agent/tool'
import type { AgentDefinition } from '../../../../shared/domain/agent-def'
import { abortError } from '../../abort'
import { BUILTIN_AGENTS } from '../../agent/builtin'
import { defineTool } from '../define'
import type { ToolRegistration } from '../registry'

const TaskInput = z.object({
  description: z
    .string()
    .min(1)
    .max(200)
    .describe('A 3-5 word description of the task, shown on the card in the UI, e.g. "Find config readers"'),
  prompt: z
    .string()
    .min(1)
    .max(50_000)
    .describe(
      'The full task for the subagent. IMPORTANT: the subagent CANNOT SEE THIS CONVERSATION, so every piece ' +
        'of context it needs — and exactly what you want back — must be written here'
    ),
  subagent_type: z
    .string()
    .min(1)
    .max(64)
    .describe('The name of the subagent to use, chosen from the list in this tool\'s description'),
  run_in_background: z
    .boolean()
    .optional()
    .describe('If true, start the subagent and continue this conversation without waiting for its report')
})

/** 清单里每个子代理占的那一行。`tools` 省略时照 CC 写成 `*`。 */
function describeAgent(a: AgentDefinition): string {
  const tools = a.tools === undefined ? '*' : a.tools.join(', ')
  return `- ${a.name}: ${a.description} (Tools: ${tools})`
}

function buildDescription(agents: readonly AgentDefinition[]): string {
  const catalog =
    agents.length === 0
      ? '(No subagents are available — do not call this tool)'
      : agents.map(describeAgent).join('\n')

  return (
    'Launches a subagent to handle a multi-step task independently.\n\n' +
    'Available subagent types, and the tools each one may use:\n' +
    `${catalog}\n\n` +
    'You MUST name the one you want with `subagent_type`, copied verbatim from the list above.\n\n' +
    'When to use this tool:\n' +
    '- You are searching the codebase for a keyword or a file and are not confident you will find the ' +
    'right match in the first few tries — hand it over instead of reading twenty search hits into your ' +
    'own context\n' +
    '- Reaching a conclusion means opening many files, and you only need the conclusion\n' +
    '- One subagent description CLEARLY matches the task\n\n' +
    'When NOT to use this tool:\n' +
    '- You already know which file to read — use `Read` directly, a subagent is only slower\n' +
    '- You are searching for a specific class or function name — use `Glob` / `Grep` directly\n' +
    '- The task takes two or three steps — do it yourself\n' +
    '- The task needs back-and-forth with the user: IMPORTANT, A SUBAGENT CANNOT ASK THE USER ANYTHING. ' +
    'It gets no answer, so it will guess one and carry on\n\n' +
    'Things you must know before using it:\n' +
    '1. IMPORTANT: THE SUBAGENT CANNOT SEE THIS CONVERSATION. All it sees is what you write in `prompt`. ' +
    'Put the background, the constraints, and what you want returned into it — whatever you leave out, ' +
    'it will invent\n' +
    '2. IMPORTANT: IT SENDS YOU EXACTLY ONE MESSAGE and then it is finished. You cannot ask a follow-up ' +
    'or add anything: either accept the result, or launch it again with a clearer `prompt`\n' +
    '3. EVERY CALL IS STATELESS. Nothing is shared between subagents, or between two calls to the same one\n' +
    '4. Say explicitly WHAT YOU WANT: research and report back, or actually change the code. If you do ' +
    'not say, a subagent with write access may well start editing\n' +
    '5. CHECK ITS OUTPUT before you act on it. Subagents make mistakes like you do, and all you get is the ' +
    'conclusion, never the reasoning behind it\n' +
    `6. Nesting is capped at ${String(MAX_DEPTH)} levels — a subagent launching a subagent is refused\n` +
    '7. Set run_in_background=true only when the parent conversation can continue without the result. ' +
    'Background runs remain observable and can be stopped from the task panel.'
  )
}

/**
 * 造一个 `Task` 工具,描述里带着**传进来**的那份子代理清单。
 *
 * ★ 每次调用现拼,不缓存:缓存就意味着「用户新加的 agent 文件要重启才生效」,
 * 而这件事没有任何地方会提示他。拼一份字符串的代价可以忽略。
 *
 * ★ **默认值是内建那几条,不是某个注册表的内容。** 注册表现在按工作区分桶
 * (见 `kernel/registry-buckets.ts`),而这个函数不知道自己是为哪个工作区造的 ——
 * 让它去猜一个桶,等于把「A 的子代理出现在 B 的工具描述里」这件事藏进一个默认参数。
 * 需要某个工作区那份清单的调用方必须显式传:每次 run 的 `snapshotRunTools`
 * 和上下文预览都是这么做的。
 */
export function taskTool(agents: readonly AgentDefinition[] = BUILTIN_AGENTS): ToolRegistration {

  return defineTool({
    internalId: 'Task',
    description: buildDescription(agents),
    schema: TaskInput,
    /*
      ★ `readOnly: false` 白送一道闸门:plan 模式的 `snapshot({ readOnlyOnly: true })`
      会直接把这个工具摘掉 —— 计划模式下没法借子代理绕开只读围栏,不用写一行代码。
      代价是计划模式也派不出只读的调研子代理,v1 接受。

      标成只读是**错**的,哪怕子代理自己只用了读工具:它跑的是一整条 agent 循环,
      能用哪些工具由它的定义决定,而不是由这个字段决定。
    */
    readOnly: false,
    /*
      `destructive: false`:派一个子代理这个动作本身不破坏任何东西,
      真正的破坏性操作由子代理里的那个工具自己去过权限闸门 ——
      **子代理里的每一次工具调用都会再走一遍 `approve`**,不是派出去就放行了。
    */
    destructive: false,
    needsNetwork: false,

    async run(input, ctx) {
      /*
        ★ 深度闸门放在**第一行**,而且这条消息要说清「为什么」和「那你该做什么」。
        只说「达到上限」的话,模型会换一个 subagent_type 再试一次 —— 它会以为
        是那个子代理的问题。
      */
      if (ctx.depth >= MAX_DEPTH) {
        return toolFail(
          `The subagent nesting limit of ${String(MAX_DEPTH)} levels has been reached. ` +
            `Do this step yourself with Read / Glob / Grep and the other tools — do NOT launch another subagent.`
        )
      }

      if (ctx.spawnSubagent === undefined) {
        return toolFail('Subagents cannot be launched in this environment. Do this step yourself.')
      }

      const outcome = await ctx.spawnSubagent({
        subagentType: input.subagent_type,
        prompt: input.prompt,
        description: input.description,
        callId: ctx.callId,
        ...(input.run_in_background === true ? { background: true } : {})
      })

      /*
        ★ 「还没派出去就被拒」原样转交,不加任何前缀。

        启动器给的 reason 已经是一段完整的人话(「没有名为 X 的子代理。
        当前可用:…」),再包一层「子代理 X 失败:」会让模型以为那个子代理
        存在但坏掉了 —— 然后它会原样重试一次。
      */
      if (outcome.kind === 'refused') return toolFail(outcome.reason)

      if (outcome.kind === 'background') {
        /*
          ★ 「已排队」要说成排队,不能说成「已启动」。

          并发满的时候这次派发是真的还没开始跑。谎报成已启动的话,模型会在
          几秒后就去等它的产出、或者据此认为这条调查已经在进行中 ——
          而它可能还要等上几分钟。说清楚它自己会开始,模型就不会去重派一次。
        */
        return {
          ...toolOk(
            outcome.detached === true
              ? `The user moved this subagent to the background while it was still running (run ${outcome.childRunId}). ` +
                'It keeps working on its own and its final report will be delivered to you when it finishes — ' +
                'do NOT launch it again. Continue with the parent task.'
              : (outcome.queued === true
                ? `The subagent is queued behind the running ones (run ${outcome.childRunId}); ` +
                  'it starts automatically as soon as a concurrency slot frees up. '
                : `The subagent started in the background (run ${outcome.childRunId}). `) +
                'Continue with the parent task; its progress and final report remain available in the task panel.'
          ),
          subagent: {
            childRunId: outcome.childRunId,
            status: 'running',
            background: true,
            ...(outcome.color === undefined ? {} : { color: outcome.color })
          }
        }
      }

      switch (outcome.status) {
        case 'done':
          /*
            ★ 「跑完了但一个字都没说」要当**失败**报,不能返回一个空的 toolOk。
            空结果被当成成功的话,父代理会认为「查过了,没有」并据此往下做 ——
            而真实情况是这次调查根本没发生。
          */
          if (outcome.text.trim() === '') {
            const error = agentError('unknown',
              `The subagent ${input.subagent_type} finished without producing any text. ` +
                'Retry once with a more specific prompt, or do this step yourself.',
              { messageKey: 'chat.subagent.error.noOutput', messageParams: { agent: input.subagent_type } }
            )
            return {
              ...toolFail(error.message),
              subagent: {
                childRunId: outcome.childRunId,
                status: 'error',
                error,
                ...(outcome.color === undefined ? {} : { color: outcome.color })
              }
            }
          }
          return {
            ...toolOk(outcome.text),
            subagent: {
              childRunId: outcome.childRunId,
              status: outcome.status,
              summary: outcome.text.slice(0, 240),
              ...(outcome.color === undefined ? {} : { color: outcome.color })
            }
          }

        case 'error': {
          const error = typeof outcome.error === 'string'
            ? agentError('unknown', outcome.error)
            : outcome.error
          /*
            ★ 网络故障要**明说它是瞬时的**。

            子 run 走到这里时,session 层已经按 `RESUME_DELAYS_MS` 自己续跑过 5 次
            (`agent-session.ts` 的 `canResume`)—— 这里是最后一道防线,而父代理是
            唯一还能做点什么的人。只甩一句「failed: 连接「X」失败:net::ERR_CONNECTION_CLOSED」
            的话,模型读不出「这跟我的提示词无关、原样再派一次就可能成」,于是要么
            放弃这一步、要么换个方向瞎试。措辞形状同上面那条空输出错误:说清原因 + 给一个动作。
          */
          const transient = error?.code === 'network'
            ? ' This was a transient network failure and the subagent already retried on its own; '
              + 'dispatching the same task again may well succeed.'
            : ''
          return {
            ...toolFail(`The subagent ${input.subagent_type} failed: ${error?.message ?? 'unknown error'}${transient}`),
            subagent: {
              childRunId: outcome.childRunId,
              status: 'error',
              ...(outcome.color === undefined ? {} : { color: outcome.color }),
              ...(error === undefined ? {} : { error })
            }
          }
        }

        case 'aborted':
          /*
            ★ **原样抛中断,不包成 toolFail。**父被中断时会级联中断子,
            此时父自己也正在中断收尾 —— 返回一个「正常的失败结果」会和
            `finalizeAbort` 的孤儿 tool_result 补齐打架,同一个 callId
            出现两条 tool_result,下一轮上行就是 400。
            `defineTool` 的契约写明了中断原样抛出。
          */
          throw abortError('The subagent was aborted')

        /* c8 ignore next 4 */
        case 'running':
          // 启动器只在 run_end 之后 resolve,所以这一支到不了。留着是为了穷尽 union:
          // 将来 RunStatus 加一个成员时,这里会是编译错误而不是一个静默的 undefined
          return toolFail(`The subagent ${input.subagent_type} is in an unexpected state (still running).`)
      }
    }
  })
}

/** 给测试用:不经注册表,直接看一份清单会拼成什么样。 */
export const buildTaskDescriptionForTest = buildDescription
