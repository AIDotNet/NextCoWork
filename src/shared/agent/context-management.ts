import type { AgentMessage } from './message'

/*
  ★ 三层窗口 —— 一个 `contextWindow` 曾经同时回答三个不同的问题,这里把它们拆开。
  改动任何一层之前,先确认你要动的是哪一个:

  1. **协议窗口** = `ModelAlias.contextWindow` 原值。回答「发出去会不会被上游 400」。
     只有 `validateModelRuntime` 的 context_length 硬校验读它,**永远不受用户开关影响** ——
     模型明明吃得下,我们自己先报错是纯粹的自伤。
  2. **有效窗口** = `effectiveContextWindow()`。回答「我自愿用到多少」。
     它是 `shouldCompact` 的分母,也是圆环的分母。默认被 `LONG_CONTEXT_THRESHOLD` 夹住。
  3. **压缩阈值** = 有效窗口 − 摘要输出预留 − 缓冲,见 `autoCompactThreshold()`(Claude Code 的公式)。
     回答「什么时候开始压」。原先只长在 kernel/context-assembler.ts 里(所以这里写着
     「本文件不管这一层」),现在渲染层的状态行也要按当前窗口重判同一条不等式,
     故下沉到本文件,装配器改为调用它 —— 判据仍然只有一份。
*/

/**
 * 别名表里查不到这个模型时,协议窗口按它算。
 * 保守取小:猜大了 `shouldCompact` 会迟到,那一轮直接被上游 400。
 *
 * ★ 它和 `LONG_CONTEXT_THRESHOLD` **必须分开**。合并的话,哪天计费分界挪到 400K,
 * 会顺手把「查不到时的兜底」也改成 400K —— 于是一个真实窗口 128K 的老模型被算成
 * 「还剩 70%」然后 400。两个数各有各的改动理由。
 */
export const FALLBACK_CONTEXT_WINDOW = 200_000

/**
 * OpenAI 现代四款(astra / sol / terra / luna)的长上下文计费分界,
 * 见 `domain/pricing-seed.ts` 里的 `two(272_000, 便宜档, 贵档)`。
 *
 * ★ **这是价格常量,不是能力常量。** 越过它模型照样工作,只是输入 / 缓存读 / 缓存写 ×2、
 * 输出 ×1.5。所以默认把有效窗口夹在这里,等于「默认不越过收费线」;
 * 「最大上下文」开关的准确语义是**花钱开关**,不是解锁更大容量。
 */
export const LONG_CONTEXT_THRESHOLD = 272_000

/**
 * 有效窗口:用户自愿使用的上限。`shouldCompact` 和圆环分母都读这个。
 *
 * `maxContext` 关(默认)→ 夹在计费分界内;开 → 放开到模型的协议窗口。
 * 协议窗口缺失 / 非有限 / 非正 一律落到 `FALLBACK_CONTEXT_WINDOW`。
 */
export function effectiveContextWindow(
  protocolWindow: number | undefined,
  maxContext = false
): number {
  const protocol =
    typeof protocolWindow === 'number' && Number.isFinite(protocolWindow) && protocolWindow > 0
      ? protocolWindow
      : FALLBACK_CONTEXT_WINDOW
  return maxContext ? protocol : Math.min(protocol, LONG_CONTEXT_THRESHOLD)
}

/*
  ★★ 压缩阈值 —— 照 Claude Code 的 autocompact 公式:

      有效窗口 − min(maxOutputTokens, 摘要输出上限 20K) − 缓冲 13K

  原先是「输入 + min(maxOut, 窗口×0.25) > 窗口×0.8」。改的理由:
  1. 旧公式在 272K 窗口、32K 输出下于 185K 触发,留出来的 87K 大半浪费;
     CC 的公式给摘要请求自己的输出(20K)和一次工具回执的余量(13K)留位置,
     其余全部给对话 —— 272K 窗口下在 239K 触发。
  2. 分母读的仍是**有效窗口**(默认夹在 272K 计费线),不是协议窗口:
     「最大上下文」开关的语义没有变。

  ★ `MIN_THRESHOLD_RATIO` 是我们对 CC 的唯一偏离:CC 只对接 200K 级窗口,
  这里还有 8K / 32K 的本地模型,光减 33K 就是负数 —— 那样每轮都会压缩,
  压完仍然为真,永远收敛不了。封底到窗口的一半。
*/

/**
 * 摘要请求的输出上限,同 CC 的 `COMPACT_MAX_OUTPUT_TOKENS`。阈值里也为它留位置。
 *
 * ★ 它现在**只**服务于下面 `autoCompactThreshold` 那一层的预留 —— 回答的是
 * 「正文什么时候该压」。摘要请求**实际**发多大的 `max_tokens` 不再读它:那个跟随
 * 全局设置项、再按压缩模型自己的窗口夹(`main/kernel/compaction/binding.ts`)。
 * 两个数**不是同一个东西,别合并**:这一层的分母是会话模型的有效窗口,
 * 那一层的分母是压缩模型的协议窗口,而这两个模型现在可以不是同一个。
 */
export const COMPACT_MAX_OUTPUT_TOKENS = 20_000
/** 阈值与有效窗口之间的缓冲,同 CC 的 `AUTOCOMPACT_BUFFER_TOKENS`。 */
export const AUTOCOMPACT_BUFFER_TOKENS = 13_000
/** 状态行提前多少开始提示「接近上限」,同 CC 的 `WARNING_THRESHOLD_BUFFER_TOKENS`。 */
export const WARNING_BUFFER_TOKENS = 20_000
/** 阈值封底:窗口的这个比例。见上面那段 ★。 */
export const MIN_THRESHOLD_RATIO = 0.5

/** 自动压缩在占用达到多少时触发。主进程判据与渲染层状态行共用这一份。 */
export function autoCompactThreshold(contextWindow: number, maxOutputTokens: number): number {
  const reserve = Math.min(Math.max(0, maxOutputTokens), COMPACT_MAX_OUTPUT_TOKENS)
  const threshold = contextWindow - reserve - AUTOCOMPACT_BUFFER_TOKENS
  return Math.max(threshold, Math.floor(contextWindow * MIN_THRESHOLD_RATIO))
}

/**
 * 「这一轮该压缩了吗」。
 *
 * 需求:主进程(真正触发自动压缩)和渲染层状态行(「接近上限,可 /compact」按**当前**
 * 有效窗口重判 —— 用户中途打开「最大上下文」之后旧建议要立刻回落)必须读同一个公式。
 * 各写一份的话,状态行会和真正的判据悄悄分叉。
 *
 * `inputTokens` 在主进程是「上一次上游真值 + 其后新增消息的估算」
 * (见 `AgentSession.contextTokens`,同 CC 的 `tokenCountWithEstimation`),
 * 在渲染层是上一轮的上游真值。
 */
export function shouldCompactAt(input: {
  inputTokens: number
  contextWindow: number
  maxOutputTokens: number
}): boolean {
  return input.inputTokens >= autoCompactThreshold(input.contextWindow, input.maxOutputTokens)
}

/** 状态行开始提示的那条线。 */
export function shouldWarnAt(input: {
  inputTokens: number
  contextWindow: number
  maxOutputTokens: number
}): boolean {
  return input.inputTokens >= autoCompactThreshold(input.contextWindow, input.maxOutputTokens) - WARNING_BUFFER_TOKENS
}

/**
 * 这个模型开「最大上下文」有没有意义 —— 协议窗口得真的比计费分界大。
 *
 * ★ **只用来置灰菜单项,绝不用来 normalize 存下来的值。**
 * `effectiveContextWindow` 的 `min()` 已经兜住了不适用的情况,再去抹掉用户存的 true,
 * 会让「sol → claude → sol」这条常见路径静默丢掉用户的选择。
 * (对照 `normalizeModelThinkingLevel`:那个**必须** normalize,因为下发一个模型不认的
 * reasoning effort 会被上游拒;而这里多存一个 true 不会有任何下游后果。)
 */
export function supportsMaxContext(protocolWindow: number | undefined): boolean {
  return effectiveContextWindow(protocolWindow, true) > LONG_CONTEXT_THRESHOLD
}

/**
 * 圆环上那道「272K 在哪儿」的刻度所处的比例。
 * 有效窗口 ≤ 分界时返回 undefined —— 刻度会正好落在终点,画出来是噪音。
 */
export function longContextTickRatio(
  protocolWindow: number | undefined,
  maxContext: boolean
): number | undefined {
  const effective = effectiveContextWindow(protocolWindow, maxContext)
  if (effective <= LONG_CONTEXT_THRESHOLD) return undefined
  return LONG_CONTEXT_THRESHOLD / effective
}

/**
 * 272_000 → '272K',1_050_000 → '1.05M'。
 *
 * ★ 不要换成 settings/pages/model/pricing-table.ts 的 `compactTokens`:那个要求整除,
 * 1_050_000 会被显示成 '1050K';而且 chat 视图不该反向依赖 settings 目录。
 */
export function formatContextWindow(n: number): string {
  if (!Number.isFinite(n) || n <= 0) return '—'
  if (n >= 1_000_000) {
    const m = n / 1_000_000
    return `${Number(m.toFixed(2))}M`
  }
  if (n >= 1000) {
    const k = n / 1000
    return `${Number(k.toFixed(k < 10 ? 1 : 0))}K`
  }
  return String(n)
}

// ─────────────────────────── 占用归因 ───────────────────────────

/**
 * 上下文**被谁占掉了**。
 *
 * ★ 这是归因,不是余量 —— 分母是「已用」而不是窗口,所有档相加恒等于 `used`。
 * 上面那三层窗口回答「还装得下多少」,这一组回答「已经装进去的那些是什么」,
 * 是两个不同的问题:一个 3.4% 的读数配上「MCP 占了其中 42%」才是可行动的,
 * 单看余量只能得出「还早着呢」,而挂满 MCP 的工作区在**一句话都没聊**的时候
 * 就已经少掉半个窗口了。
 *
 * ★ 分档就是分档到**用户能关掉的那个东西**。`tools-mcp` 因此带 `detail`
 * (按 server 拆):「MCP 占 42%」不可行动,「github 这一个占 28%」可以。
 * 同理没有 `tools-skill` 这一档 —— 技能的清单段和它带的工具对用户是**一件事**
 * (设置里就是一个开关),拆成两行只会让人以为关掉技能只省下其中一半。
 */
export type ContextSegmentKind =
  /** 基础提示词 + 角色 + 环境事实 + 模式附录。用户关不掉,但它是基线。 */
  | 'system'
  /** 技能清单段 + 技能注册的工具。 */
  | 'skills'
  | 'tools-builtin'
  | 'tools-mcp'
  /** 个性化、AGENTS.md、每轮注入的状态块 —— 用户自己写进去的那些。 */
  | 'instructions'
  /** 真实对话(含工具结果)。压缩唯一能减掉的就是这一档。 */
  | 'messages'

export interface ContextSegmentDetail {
  id: string
  label: string
  tokens: number
}

export interface ContextSegment {
  kind: ContextSegmentKind
  tokens: number
  /** 目前只有 `tools-mcp` 填,按 token 降序。 */
  detail?: ContextSegmentDetail[]
}

/**
 * 归因用的百分比:分母是**已用量**,不是窗口。
 *
 * ★ 空会话(`used` 为 0)返回 0 而不是 NaN —— 界面上 `NaN%` 是个渲染 bug 的样子,
 * 而这里的 0 是真的:还没发过请求,确实什么都没占。
 */
export function contextSegmentShare(tokens: number, used: number): number {
  if (!Number.isFinite(used) || used <= 0) return 0
  return Math.max(0, Math.min(1, tokens / used))
}

/**
 * **还没发过请求时**的归因 —— 装配一次但不发出去。
 *
 * ★ 它存在的理由就是这个功能最值钱的那一半:一个挂满 MCP 的工作区在**一句话
 * 都没聊**的时候就已经少掉半个窗口,而那个数只有在你还没开始聊的时候看见才有用。
 * 等第一条消息发出去,窗口已经被占了,再看只是事后报告。
 *
 * ★ 它是**估算**,和 `transcript.lastInputTokens`(上游回报的真值)不是一个东西。
 * 所以界面上只有在真值还不存在时才拿它顶上,一旦有过一轮真实请求就让位。
 */
export interface ContextPreview {
  used: number
  window: number
  segments: ContextSegment[]
}

/**
 * 自动 / 手动压缩这一轮的状态,随 `context_status` 事件发给渲染层。
 *
 * 需求:压缩要发一次模型请求(几秒到几十秒),这段时间状态行必须说「正在压缩」,
 * 失败了必须说「压缩失败」—— 否则用户看到的是一次无缘无故变长的停顿。
 *
 * ★ 原先还有 `fallback` / `exhausted` 两档,描述机械压缩「折叠了 / 折叠不动」。
 * 机械压缩已随 Claude Code 式重写删除(压缩只剩模型摘要这一条路),两档一并删除。
 * `disabled` 是熔断:同一个 run 里连续失败 `MAX_CONSECUTIVE_COMPACT_FAILURES` 次后
 * 不再自动重试 —— 此时唯一有用的动作在用户那边(/compact、换模型、另起会话)。
 */
export type ContextStatusPhase = 'compacting' | 'compacted' | 'failed' | 'disabled'

export interface ContextSearchHit {
  messageId: string
  role: AgentMessage['role']
  createdAt: number
  snippet: string
}

export interface ContextStatus {
  phase: ContextStatusPhase
  trigger?: 'auto' | 'manual'
}
