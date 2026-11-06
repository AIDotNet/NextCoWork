/**
 * 「此刻在干什么」—— 把转录投影成状态行那一句话的**相位**与**场景**。
 *
 * ★ **为什么不是直接读 `status`。** `RunStatus` 只有 running/done/error/aborted 四档,
 * 一次 run 里绝大部分时间都卡在 `running` 上不动;而这段时间里 Agent 其实在
 * 读文件、跑命令、连网、派子代理之间来回切。状态行拿 `running` 只能写「运行中」,
 * 那句话从第一个工具到第十个工具一个字都不变 —— 界面上唯一在动的信息,
 * 恰恰是它没有表达的那一维。
 *
 * ★ **相位之上还有一层「场景」**(`WhimsyBucket`)。只按相位分组时,`Read` 和 `LS`、
 * `Grep` 和 `Glob` 说的是同一句话,而**用户看得见它们是不同的工具**;更要命的是
 * 「这一步已经跑了 40 秒」「同一个工具连着第 5 次」「上一个工具刚报错」这三种时刻 ——
 * 它们在相位上和一切正常时完全一样,却恰恰是用户最想被告知一声的。
 * 所以 `whimsyBucketOf` 让场景**压过**工具、工具压过相位。
 *
 * ★ **纯函数,不碰 store,不读时钟,单测穷尽。** 它的 bug 形态是「并行跑三个工具时选错了那个」,
 * 依赖工具的具体到达顺序才现形,靠盯屏幕复现不了 —— 和 `tool-timeline.ts`
 * 抬头说的是同一条理由。「这一步跑了多久」由调用方量好再传进来(`elapsedMs`),
 * 这里不碰 `Date.now()`,否则这组判定就再也测不了。
 *
 * ★ 产出**只喂给装饰性文案**。真正的状态仍然是 `data-status` 上那四档,
 * e2e 探针读的是它;这里换多少个词都不该让任何一条断言动。
 */
import type { ToolCallState, TranscriptState } from '../agent/transcript'
import { presenterOf, type ToolShape } from './tool-presenter'

/**
 * 工具的八种形态 + 三种「没有工具在跑」的时刻。
 *
 * 复用 `ToolShape` 而不是另起一套分类:那边已经按「展开后长什么样」把工具分完了,
 * 而**用户看到的是同一批分组**(时间线的折叠标题也用它)。再分一套的话,
 * 状态行说「在检索」而时间线把那次调用归进「读取」,两处对不上。
 */
export type ActivityPhase = ToolShape | 'waiting' | 'writing' | 'working'

/**
 * 一句装饰文案的取词分组。相位是兜底,前面那些是「说得更具体的那一档」。
 *
 * 命名分两段(`read.file` / `slow.tool`),第一段就是它退化后的去处 ——
 * 哪天想砍掉细分,直接退回相位即可。
 */
export type WhimsyBucket =
  | ActivityPhase
  // 具体工具:同一相位里长相差得远的那几对
  | 'read.file'
  | 'read.dir'
  | 'mutate.write'
  | 'mutate.edit'
  | 'search.glob'
  | 'search.grep'
  | 'command.bash'
  | 'network.fetch'
  | 'network.search'
  | 'plan.todo'
  | 'delegate.subagent'
  | 'skill'
  | 'schedule'
  // 场景:在相位上看不出来、但用户一定注意到了的几种时刻
  | 'slow.wait'
  | 'slow.tool'
  | 'grind'
  | 'recover'
  | 'context.tight'
  | 'context.compacting'

/**
 * 这一帧的「在干什么」。
 *
 * ★ 不含时间:见抬头。要判断「久不久」的那一维由调用方给。
 */
export interface ActivitySnapshot {
  phase: ActivityPhase
  /** 当前在跑的那个工具的转录名(externalName);没有工具在跑时缺省 */
  tool?: string
  /** 当前那次调用的 id —— 调用方用它判断「还是不是同一步」,从而决定要不要重新计时 */
  callId?: string
  /** 同一个工具连着跑到第几次(含这一次)。没有工具在跑时是 1 */
  streak: number
  /** 最近一次**结束**的工具是报错结束的 —— 也就是说模型正在善后 */
  recovering: boolean
  context: 'ok' | 'tight' | 'compacting'
}

/**
 * 首字节等多久算「久」。
 *
 * 20 秒:短于它的等待在体感上还算正常(带思考的模型开口本来就慢),
 * 长于它时用户已经在怀疑是不是卡死了 —— 那一刻说一句「还没来…」比继续
 * 轮换「琢磨中…」诚实得多。
 */
export const SLOW_WAIT_MS = 20000

/**
 * 一个工具跑多久算「久」。
 *
 * 30 秒 > 20 秒:装依赖、跑测试本来就慢,阈值太小会让每一次正常构建都被吐槽一遍。
 */
export const SLOW_TOOL_MS = 30000

/** 同一个工具连着第几次算「没完没了」。 */
export const GRIND_STREAK = 4

/** 工具 → 更具体的取词分组。查不到的(MCP / 插件 / 新工具)一律退回相位,不用登记。 */
const TOOL_BUCKETS: Record<string, WhimsyBucket> = {
  Read: 'read.file',
  LS: 'read.dir',
  Write: 'mutate.write',
  Edit: 'mutate.edit',
  Glob: 'search.glob',
  Grep: 'search.grep',
  Bash: 'command.bash',
  WebFetch: 'network.fetch',
  web_search: 'network.search',
  TodoWrite: 'plan.todo',
  Task: 'delegate.subagent',
  Skill: 'skill',
  ListScheduledTasks: 'schedule',
  CreateScheduledTask: 'schedule',
  UpdateScheduledTask: 'schedule',
  DeleteScheduledTask: 'schedule'
}

/**
 * @param waitingForResponse 首字节还没到(调用方已有的判定,不在这里重算)
 */
export function activitySnapshotOf(
  transcript: TranscriptState,
  waitingForResponse: boolean
): ActivitySnapshot {
  const latest = latestRunning(transcript)
  const settled = lastSettled(transcript)
  return {
    phase: latest === undefined ? livePhase(transcript, waitingForResponse) : presenterOf(latest.name).shape,
    ...(latest === undefined ? {} : { tool: latest.name, callId: latest.callId }),
    streak: latest === undefined ? 1 : streakOf(transcript, latest.name),
    recovering: settled?.status === 'error',
    context: contextOf(transcript)
  }
}

/** 只要相位那一维的老调用方(以及那批只盯相位的单测)。 */
export function activityOf(transcript: TranscriptState, waitingForResponse: boolean): ActivityPhase {
  return activitySnapshotOf(transcript, waitingForResponse).phase
}

/**
 * 场景 → 取词分组。**越靠前的越"反常"**,反常的那件事压过一切。
 *
 * ★ `context.compacting` 在最前面:压缩期间模型根本没在推进这一轮,
 * 此时还按工具说话就是在描述一件已经暂停的事。
 * ★ `context.tight` 在最后面(只在没有具体工具可说时才轮到它):
 * `shouldCompact` 一旦为真会一直真到压缩发生,放前面等于把接下来的
 * 十几次工具调用全盖成同一句话。
 */
export function whimsyBucketOf(snapshot: ActivitySnapshot, elapsedMs: number): WhimsyBucket {
  if (snapshot.context === 'compacting') return 'context.compacting'
  if (snapshot.tool === undefined) {
    if (snapshot.phase === 'waiting' && elapsedMs >= SLOW_WAIT_MS) return 'slow.wait'
  } else if (elapsedMs >= SLOW_TOOL_MS) return 'slow.tool'
  if (snapshot.recovering) return 'recover'
  if (snapshot.streak >= GRIND_STREAK) return 'grind'
  const specific = snapshot.tool === undefined ? undefined : TOOL_BUCKETS[snapshot.tool]
  if (specific !== undefined) return specific
  if (snapshot.context === 'tight') return 'context.tight'
  return snapshot.phase
}

/**
 * ★ **并行跑多个工具时,取最新开跑的那个。** 一次助手轮次里同时挂三五个工具是常态;
 * 取第一个的话,那句文案会在头一个慢工具跑完之前一直停在它上面 ——
 * 而用户刚刚在时间线上看到新增的是最后那一行。
 *
 * `startedAt` 可能缺失(旧转录重放,见 `ToolCallState.startedAt`)。缺的按 0 算,
 * 于是它只会输给任何一个有戳的;全都没戳时退化成插入序的最后一个,
 * 那正是 `Object.values` 的顺序。
 */
function latestRunning(transcript: TranscriptState): ToolCallState | undefined {
  let latest: ToolCallState | undefined
  for (const call of Object.values(transcript.tools)) {
    if (call.status !== 'running') continue
    if (latest === undefined || (call.startedAt ?? 0) >= (latest.startedAt ?? 0)) latest = call
  }
  return latest
}

/**
 * 没有工具在跑的三种时刻。看**最后一个** live 块:前面那些已经流完了,
 * 此刻在动的是最后这个。
 * `tool_use` 是参数 JSON 还在流、工具尚未开跑 —— 它既不算「在想」也不算「在读文件」,
 * 归到通用那档,免得文案抢在工具之前就开始描述一件还没发生的事。
 */
function livePhase(transcript: TranscriptState, waitingForResponse: boolean): ActivityPhase {
  const block = transcript.live.at(-1)
  if (block?.kind === 'thinking') return 'reasoning'
  if (block?.kind === 'text') return 'writing'
  if (block?.kind === 'tool_use') return 'working'
  return waitingForResponse ? 'waiting' : 'working'
}

/**
 * 已经开跑过的调用,按开跑顺序。
 *
 * `pending` 的排除在外:它们还没有 `startedAt`,按 0 排会全部挤到队头,
 * 把「连着跑了几次」的尾部连击算断。
 */
function startedCalls(transcript: TranscriptState): ToolCallState[] {
  return Object.values(transcript.tools)
    .filter((call) => call.status !== 'pending')
    .sort((a, b) => (a.startedAt ?? 0) - (b.startedAt ?? 0))
}

/**
 * 同一个工具连着跑到第几次。
 *
 * 从尾部往回数,遇到别的工具就断 —— 一轮里交替 Read/Grep 各五次**不算**连击:
 * 那是正常的排查节奏,而「连着 Edit 第六次」才是那种让人想说句话的时刻。
 */
function streakOf(transcript: TranscriptState, name: string): number {
  const calls = startedCalls(transcript)
  let streak = 0
  for (let i = calls.length - 1; i >= 0; i -= 1) {
    if (calls[i]?.name !== name) break
    streak += 1
  }
  return Math.max(1, streak)
}

/** 最近一次结束的调用(按结束时间;没戳时退化成开跑顺序里的最后一个)。 */
function lastSettled(transcript: TranscriptState): ToolCallState | undefined {
  let last: ToolCallState | undefined
  for (const call of startedCalls(transcript)) {
    if (call.status !== 'ok' && call.status !== 'error') continue
    if (last === undefined || (call.endedAt ?? 0) >= (last.endedAt ?? 0)) last = call
  }
  return last
}

function contextOf(transcript: TranscriptState): ActivitySnapshot['context'] {
  if (transcript.contextStatus?.phase === 'compacting') return 'compacting'
  return transcript.contextUsage?.shouldCompact === true ? 'tight' : 'ok'
}
