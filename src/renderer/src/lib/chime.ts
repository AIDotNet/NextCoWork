/**
 * 三种提示音的合成器(设置 › 通用 › 提示音)。
 *
 * 需求:任务完成 / 权限审批 / 计划审批各响一声,而且**三声听得出区别** ——
 * 用户人不在屏幕前时,耳朵是唯一能告诉他「该回来批准了」还是「已经跑完了」的通道。
 *
 * ★ 用 Web Audio 现场合成,不带音频文件:仓库里没有任何音频资源,放 .mp3 就要一起处理
 *   打包路径、CSP 的 media-src 和素材授权;三段短音用几个振荡器就够了。
 *   音色是一张表(`PATTERNS`),换声音只改表,不改播放逻辑。
 *
 * ★ 依赖 Electron 的默认自动播放策略 `no-user-gesture-required`(主进程没有改 `autoplayPolicy`)。
 *   哪天把它改成需要手势,表现是提示音全部静默且零报错 —— 下面那句 `resume()` 救不回来。
 */
import type { SoundCue } from './sound-cue'

interface Tone {
  /** Hz */
  freq: number
  /** 相对这一声起点的偏移,秒 */
  start: number
  /** 秒 */
  duration: number
  type: OscillatorType
}

/*
  任务完成:上行两音,「叮—咚」收尾感;
  权限审批:同音高两下短促三角波,像敲门,最容易被注意到;
  计划审批:大三和弦琶音,和「任务完成」同属柔和一类但多一个音,耳朵分得开。
*/
const PATTERNS: Record<SoundCue, readonly Tone[]> = {
  taskComplete: [
    { freq: 659.25, start: 0, duration: 0.16, type: 'sine' },
    { freq: 987.77, start: 0.12, duration: 0.34, type: 'sine' }
  ],
  permission: [
    { freq: 880, start: 0, duration: 0.12, type: 'triangle' },
    { freq: 880, start: 0.18, duration: 0.16, type: 'triangle' }
  ],
  plan: [
    { freq: 523.25, start: 0, duration: 0.14, type: 'sine' },
    { freq: 659.25, start: 0.1, duration: 0.14, type: 'sine' },
    { freq: 783.99, start: 0.2, duration: 0.32, type: 'sine' }
  ]
}

/** 峰值音量。提示音是提醒不是警报,和系统通知音差不多响即可(推的,没有量测) */
const PEAK_GAIN = 0.12
/** 起音时长。直接从 0 跳到峰值会有「咔」的爆音 */
const ATTACK_S = 0.012
/**
 * 两声之间的最短间隔。看起来多余,但不能删:
 * 需求:两个会话在同一帧跑完、或一批里先后来两条审批时,只响一声。
 * 删掉的症状是两段音叠在一起,听上去是一团分不出种类的杂音。
 */
const MIN_GAP_MS = 250

let context: AudioContext | null = null
let lastPlayedAt = Number.NEGATIVE_INFINITY

/**
 * ★ 全应用复用**一个** AudioContext:每响一声建一个的话,不 close 就一直占着音频图,
 *   close 又得等声音放完再关 —— 复用一个两头都省了。
 */
function audioContext(): AudioContext | null {
  if (context !== null) return context
  if (typeof AudioContext === 'undefined') return null
  try {
    context = new AudioContext()
  } catch (err) {
    console.warn('[chime] AudioContext 创建失败,提示音不可用:', err)
    return null
  }
  return context
}

export function playChime(cue: SoundCue): void {
  const now = Date.now()
  if (now - lastPlayedAt < MIN_GAP_MS) return
  const ctx = audioContext()
  if (ctx === null) return
  lastPlayedAt = now
  // 系统休眠/音频设备切换后 context 可能被挂起;挂起时排好的音会在恢复后照常播放。
  if (ctx.state === 'suspended') void ctx.resume().catch(() => undefined)
  const origin = ctx.currentTime + 0.01
  for (const tone of PATTERNS[cue]) {
    const osc = ctx.createOscillator()
    const gain = ctx.createGain()
    osc.type = tone.type
    osc.frequency.value = tone.freq
    const start = origin + tone.start
    const end = start + tone.duration
    // 指数包络不能碰 0,所以起止都落在 0.0001
    gain.gain.setValueAtTime(0.0001, start)
    gain.gain.exponentialRampToValueAtTime(PEAK_GAIN, start + ATTACK_S)
    gain.gain.exponentialRampToValueAtTime(0.0001, end)
    osc.connect(gain).connect(ctx.destination)
    // 放完就把节点从图上摘掉,否则每响一声图上多挂两个死节点
    osc.onended = () => {
      osc.disconnect()
      gain.disconnect()
    }
    osc.start(start)
    osc.stop(end + 0.02)
  }
}
