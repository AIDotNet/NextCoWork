/**
 * 权限闸门的**策略**部分 —— 只有那张表,没有任何机制。
 *
 * ★ 刻意和 `PermissionDecision` 分成两个类型。`allow_always` / `allow_edited` 是
 * **用户的回答**,闸门永远不可能产出它们。混成一个类型的话,「谁有资格说 always」
 * 在类型上就看不出来了。
 *
 * `evaluate()` 返回 `ask` 时,调用方(`runtime.ts`)通过 InteractionGate
 * 等待用户回答。策略判断不持有待决状态,也不把询问自动降级为拒绝。
 */
import type { PermissionMode, PermissionQuery } from '../../shared/agent/permission'

export type PermissionOutcome =
  | { kind: 'allow' }
  | { kind: 'deny'; reason: string }
  | { kind: 'ask' }

/**
 * 联网被关掉时给模型的说法。
 *
 * ★ 必须说清「这是用户关的开关」,否则模型会去试 `Bash` 里的 curl —— 那是同一件事
 * 换个入口,而且绕过了这道闸。最后一句直接堵掉那条路。
 */
const NETWORK_OFF =
  'This tool needs web access, but the "web search" switch is turned off for this workspace, so the ' +
  'call was denied. This is the user\'s setting, not a mistake in how you called it. Do NOT reach for ' +
  'curl or wget through Bash to do the same thing — that would route around a switch the user turned ' +
  'off deliberately. Tell the user this step needs web access and that they can turn on the ' +
  '"web search" switch in the composer.'

/**
 * 那张表。**顺序即语义**,`permission-gate.test.ts` 按行逐条钉死。
 *
 * 1. `needsNetwork && !webSearch` → deny。这里的 `needsNetwork` 由调用方按
 *    `NETWORK_SWITCH_TOOLS` 名单给出(= 受「联网搜索」开关管的工具),不再是「这个工具
 *    出不出网」。★ 必须排在只读之前:一个**只读**的联网工具(`WebFetch` 就是)在开关
 *    关掉时仍然要拒。`full` 档也放宽不了它 ——
 *    `permission.ts` 里 `'full'` 的注释原文就是「**联网仍受开关控制**」。
 * 2. `readOnly` → allow。三档都放行读。每读一个文件弹一次窗,用户 30 秒内就学会
 *    无脑点「允许」—— 那比不弹更危险,因为后面真正该看的那次也会被点掉。
 * 3. `mode === 'ask'` → ask。写与执行才问。
 * 4. `mode === 'auto' && destructive` → ask。对应「仅对潜在不安全操作询问」。
 * 5. 其余(`auto` 非破坏 / `full`)→ allow。
 */
export function evaluate(q: PermissionQuery): PermissionOutcome {
  if (q.needsNetwork === true && !q.webSearch) return { kind: 'deny', reason: NETWORK_OFF }
  if (q.readOnly) return { kind: 'allow' }
  if (q.mode === 'ask') return { kind: 'ask' }
  if (q.mode === 'auto' && q.destructive) return { kind: 'ask' }
  return { kind: 'allow' }
}

/**
 * 受「联网搜索」开关管的工具的 `internalId` —— **精确名单**,不是下限表。
 *
 * 需求(用户决定,2026-09-27):输入框那颗「联网搜索」开关**只管网页搜索与网页抓取**
 * 这两个工具;生图有自己的开关(`AppSettings.imageGenerationEnabled`),其余工具
 * 不再受它控制。原因是标签和行为对不上:开关写着「联网搜索」,关掉后却连生图、
 * 内置浏览器、可视化卡片一起消失,模型只会说「找不到工具」,用户查不到原因
 * (实际事故:选好了生图模型,对话里 `generate_image` 始终不下发)。
 *
 * ★ 这张表原先是「出网工具的**下限表**」,与 `ToolInfo.needsNetwork` 取或,管着
 * `WebFetch` / `web_search` / 9 个 `browser_*` / `generate_image` /
 * `visualize_show_widget`,外加所有 `needsNetwork: true` 的 MCP(远程传输)与插件工具。
 * 那套设计的理由是「字段可能被抄漏,表兜底」「MCP 服务器不能自报不联网」——
 * 在「开关 = 关掉所有出网」的语义下成立。语义收窄之后,判定改成**只按这张名单**:
 * - `needsNetwork` 字段留作「这个工具会出网」的**事实描述**,不再参与开关判定;
 * - 所以浏览器、可视化卡片、生图、远程 MCP、插件工具关掉开关后**照常出网** ——
 *   这是本次决定的直接后果,不是遗漏。要重新管住它们,往这张表里加名字即可。
 *
 * 两处消费者必须读同一张表:`ToolRegistry.snapshot({ network })`(不下发)和
 * `runtime.ts` 的 `approveWith`(调用时拒)。分家的症状是「列表里没有,调用却放行」
 * 或反过来「下发了,每次调用都被拒」。
 */
export const NETWORK_SWITCH_TOOLS: ReadonlySet<string> = new Set([
  'WebFetch',
  'web_search'
])

/** 给测试和诊断用:把一次判定压成一行人话。 */
export function describeOutcome(mode: PermissionMode, o: PermissionOutcome): string {
  if (o.kind === 'allow') return `${mode}:放行`
  if (o.kind === 'ask') return `${mode}:需要询问`
  return `${mode}:拒绝`
}

/**
 * `# Environment` 里那几行**权限事实**。
 *
 * ★ 它放在这里、跟着上面那张表走,而不是在 `context-assembler.ts` 里另写一份 ——
 * 理由和 `text.ts` / `untrusted.ts` 文件头那条一样:**两处必须给出同一个答案**。
 * 提示词说「写盘会被拒」而闸门其实放行(或者反过来),是最坏的一种漂移:
 * 模型会据此**提前放弃**一件它本来做得成的事,而这中间不会有任何报错。
 *
 * 写成事实而不是规则(见 `context-assembler.ts` 文件头第 3 关):模型**事前**
 * 就知道自己写盘要不要审批,而不是撞一次墙再学 —— 撞墙那一轮不只是浪费,
 * 它的下一步大概率是去试 `Bash` 绕。
 */
export function permissionFacts(mode: PermissionMode, webSearch: boolean): string {
  const say = (readOnly: boolean, destructive: boolean): string => {
    const o = evaluate({ mode, readOnly, destructive, webSearch })
    if (o.kind === 'allow') return 'run without asking'
    if (o.kind === 'ask') return 'wait for user approval before execution'
    return 'are denied'
  }
  /*
    ★ 只说「搜索与抓取」这两个:开关现在只管它们(`NETWORK_SWITCH_TOOLS`)。
    原先这行写的是「需要联网的工具」,而语义收窄之后那句话会让模型以为浏览器/生图
    也被拒 —— 正是本文件头说的「提示词与闸门漂移、模型提前放弃」那种坏法。
  */
  const net = webSearch
    ? 'available'
    : 'denied — the network switch is off for this workspace, and Bash cannot be used to route around it'

  return (
    `Permission mode: ${mode}\n` +
    `- Reading and searching: ${say(true, false)}\n` +
    `- Writing files and running commands: ${say(false, true)}\n` +
    `- File tools are not fenced to the workspace: an absolute path anywhere on this machine resolves, ` +
    `and the rules above are what governs it\n` +
    `- Web search and web fetch (${[...NETWORK_SWITCH_TOOLS].join(', ')}): ${net}`
  )
}
