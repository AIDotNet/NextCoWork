/**
 * 设置 › 通用 › 提示音:**哪一批 Agent 事件该响哪一声**。纯逻辑,不碰 Web Audio、不碰 IPC。
 *
 * 需求:三个开关(`AppSettings.notifications`)各管一种时刻 ——
 * 顶层 run 正常跑完(任务完成)、有工具操作等批准(权限审批)、计划模式交出待确认的计划(计划审批)。
 * 抽成纯函数是为了能在 vitest 里直接喂事件测,不必起 Electron(见 `__tests__/sound-cue.test.ts`)。
 *
 * ★ 交互类的映射**复用** `shared/agent/interaction.ts` 的 `INTERACTION_SOUND`,不在这里再写一张
 *   「kind → 开关」的表:那张表是三类音效的唯一出处,新增一种 InteractionKind 时编译期会逼着人去填它,
 *   这里的 `INTERACTION_CUE` 跟着它的值域走,于是漏配也会在编译期挂。
 *
 * 故意不做:
 * - `ask_user`(音色 `ask`)不响 —— 设置页没有这个开关,擅自响就是一个用户关不掉的声音。
 * - `aborted` / `error` 不算「任务完成」:用户自己点的停止不该再响一声回应他;出错走的是界面上的错误条。
 */
import { INTERACTION_SOUND, type InteractionKind } from '../../../shared/agent/interaction'
import type { AppSettings } from '../../../shared/domain/settings'
import type { AgentEventEnvelope } from '../../../shared/ipc/contract'

export type SoundCue = 'taskComplete' | 'permission' | 'plan'

export type SoundPreferences = AppSettings['notifications']

type InteractionSound = (typeof INTERACTION_SOUND)[InteractionKind]

/** `INTERACTION_SOUND` 的音色 → 这里的提示音与管它的那个开关;`null` = 没有开关,不响 */
const INTERACTION_CUE: Record<InteractionSound, { cue: SoundCue; pref: keyof SoundPreferences } | null> = {
  approval: { cue: 'permission', pref: 'permissionApproval' },
  plan: { cue: 'plan', pref: 'planApproval' },
  ask: null
}

/**
 * 同一批里凑出多种提示音时只响最急的那一声(数小的赢)。
 * 需求:「等你批准」比「跑完了」更需要人立刻回来;几个音叠在一起播,耳朵分不出是哪一种。
 */
const PRIORITY: Record<SoundCue, number> = { permission: 0, plan: 1, taskComplete: 2 }

export interface SoundCueTracker {
  /** 记下一个**顶层** run。只有记过的 run 结束时才算「任务完成」 */
  noteTopLevelRun: (runId: string) => void
  /** 这一批事件该响哪一声;不该响返回 `null` */
  cueOf: (env: AgentEventEnvelope, prefs: SoundPreferences) => SoundCue | null
}

export function createSoundCueTracker(): SoundCueTracker {
  /*
    ★ 「顶层」靠调用方**事先**登记,而不是收到 run_end 时再去判断:子代理的 run 也会把自己的
    run_end 推到同一条事件流上(订阅是从父 run 继承的,见 `main/window/registry.ts` 的 `inherit`)。
    不区分的话,一轮派出三个子代理的回复会响四声,前三声还发生在主回复写完之前。
  */
  const topLevel = new Set<string>()
  return {
    noteTopLevelRun: (runId) => {
      topLevel.add(runId)
    },
    cueOf: (env, prefs) => {
      let best: SoundCue | null = null
      for (const event of env.events) {
        let cue: SoundCue | null = null
        if (event.type === 'interaction_request') {
          const mapped = INTERACTION_CUE[INTERACTION_SOUND[event.interaction.kind]]
          if (mapped !== null && prefs[mapped.pref] === true) cue = mapped.cue
        } else if (event.type === 'run_end') {
          // 不管响不响都摘掉:run 结束后不会再有事件,留着只会让这张表越攒越大。
          const wasTopLevel = topLevel.delete(env.runId)
          if (wasTopLevel === true && event.status === 'done' && prefs.taskComplete === true) cue = 'taskComplete'
        }
        if (cue !== null && (best === null || PRIORITY[cue] < PRIORITY[best])) best = cue
      }
      return best
    }
  }
}
