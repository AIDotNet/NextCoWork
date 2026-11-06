/**
 * 设置 › 通用 › 提示音的落地点:订阅 Agent 事件流,按用户的三个开关响一声。
 *
 * 需求:`AppSettings.notifications` 三个开关原先能存、没人读(设置页上挂着 `LandsAt`);
 * 这里是它们唯一的消费者。「哪批事件响哪声」的判断在 `lib/sound-cue.ts`,声音本身在 `lib/chime.ts`,
 * 这个文件只负责把事件流、顶层 run 索引和最新设置接到一起。
 *
 * ★ 和 `startAgentEventPump()` 一样**只在 App.tsx 起一次**。每个 chat Tab 各起一个的话,
 *   同一个 run 跑完会响 N 声。
 *
 * ★ 「顶层 run」取自 `useRunIndex`,而且是**它一出现就记下**,不是收到 run_end 时再去查:
 *   主进程先广播 `agent:activeRuns`、后推 run_end(`run-registry.ts` 的 `create` 里那个监听器
 *   先于合批泵登记),于是 run_end 到达时索引里已经没有这个 run 了。
 *   那样写的表现是任务完成音永远不响,且零报错。也不按 runId 的字符串格式猜父子 ——
 *   那个格式是主进程的实现细节(见 `shared/agent/event.ts` 里 `childSessionId` 的注释)。
 *
 * 故意不做:
 * - 不因为「窗口在前台 / 正看着这个会话」而静音 —— 设置文案说的是「跑完时响一声」,没有这个例外。
 * - 没有窗口订阅的 run(定时任务起的)收不到事件,所以不响;那一路由主进程的系统通知负责
 *   (`main/scheduled/scheduler.ts`)。
 */
import { onAgentEvent } from '../services/agent'
import { playChime } from '../lib/chime'
import { createSoundCueTracker, type SoundPreferences } from '../lib/sound-cue'
import { useRunIndex, type RunIndexEntry } from './session'

const MUTED: SoundPreferences = { taskComplete: false, permissionApproval: false, planApproval: false }

/**
 * 最新一份开关。**不是设置的镜像**:没有任何界面读它,它只是事件回调里取最新值的地方;
 * 权威仍是主进程,App 每次收到 `settings:changed` 都整份覆写。
 * `null` = 握手还没拿到设置,这期间一律不响(而不是按出厂值响)。
 */
let preferences: SoundPreferences | null = null

export function setSoundPreferences(next: SoundPreferences): void {
  preferences = next
}

export function startNotificationSounds(): () => void {
  const tracker = createSoundCueTracker()
  const note = (entries: readonly RunIndexEntry[]): void => {
    for (const entry of entries) tracker.noteTopLevelRun(entry.runId)
  }
  // ⌘R 重载后 bootstrap 补回来的 run 可能早于这里登记,先把现有的收一遍。
  note(useRunIndex.getState())
  const offIndex = useRunIndex.subscribe(note)
  const offEvents = onAgentEvent((env) => {
    // 设置还没到也要过一遍 tracker:run_end 要靠它把顶层 run 从表里摘掉。
    const cue = tracker.cueOf(env, preferences ?? MUTED)
    if (cue !== null) playChime(cue)
  })
  return () => {
    offIndex()
    offEvents()
  }
}
