/**
 * TSX widget 里**通往宿主的那条线** —— 跑在 `ncw-widget://shell/` 的沙箱 iframe 里。
 *
 * iframe 是不透明源,没有 preload,能做的只有 postMessage。这里把它包成一组 Promise:
 * `call(method, args)` 发一条带 id 的 `ncw:widget:call`,等宿主回一条同 id 的
 * `ncw:widget:reply`。每个方法能干什么、拒绝什么,全在宿主那一侧
 * (`views/chat/WidgetFrame.tsx` 的 `handleCall`)—— **这里不是安全边界**,
 * 它只是让 widget 代码好写。
 *
 * ★ 环境(状态 / 数据 / 交互 / 动作)由外壳运行时先收下(`widget-shell/runtime.ts`
 * 的 `onEnv`),因为它常常比这个模块先到。这里先读那份留存,再订阅后续变化。
 */
import type { WidgetEnvironment, WidgetHostMethod } from '../../../shared/domain/widget'

const CALL = 'ncw:widget:call'
const REPLY = 'ncw:widget:reply'

interface Pending {
  resolve(value: unknown): void
  reject(error: Error): void
}

const pending = new Map<number, Pending>()
let sequence = 0

window.addEventListener('message', (event: MessageEvent) => {
  // 只认父窗口。不透明源下 origin 是 "null",身份只能靠 source。
  if (event.source !== window.parent) return
  const data = event.data as { type?: unknown; id?: unknown; ok?: unknown; value?: unknown; error?: unknown } | null
  if (data === null || typeof data !== 'object' || data.type !== REPLY || typeof data.id !== 'number') return
  const entry = pending.get(data.id)
  if (entry === undefined) return
  pending.delete(data.id)
  if (data.ok === true) entry.resolve(data.value)
  else entry.reject(new Error(typeof data.error === 'string' ? data.error : 'The host rejected this request.'))
})

export function call(method: WidgetHostMethod, args: Record<string, unknown> = {}): Promise<unknown> {
  sequence += 1
  const id = sequence
  return new Promise((resolve, reject) => {
    pending.set(id, { resolve, reject })
    window.parent.postMessage({ type: CALL, id, method, args }, '*')
  })
}

// ── 环境 ──

const EMPTY_ENV: WidgetEnvironment = {
  title: '',
  state: null,
  data: null,
  interactive: false,
  submitted: false,
  actions: []
}

function readEnv(): WidgetEnvironment | undefined {
  const raw = (window as unknown as { __ncwWidgetEnv?: unknown }).__ncwWidgetEnv
  return raw !== null && typeof raw === 'object' ? (raw as WidgetEnvironment) : undefined
}

let env: WidgetEnvironment = readEnv() ?? EMPTY_ENV
const listeners = new Set<() => void>()

window.addEventListener('ncw:widget:env', () => {
  env = readEnv() ?? EMPTY_ENV
  for (const listener of listeners) listener()
})

export function getEnv(): WidgetEnvironment {
  return env
}

export function subscribeEnv(listener: () => void): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/**
 * 等第一份环境。
 *
 * ★ 挂载要等它:`useWidgetState` 的初值来自持久化的状态,先挂载再到的话,组件第一帧
 * 用的是默认值,随后状态一到整块跳一下 —— 而用户刚拖过的滑块会先弹回去再弹回来。
 * 等不到(宿主没给,比如设置页的预览)就在超时后按空环境挂载,不让 widget 卡死。
 */
export function envReady(timeoutMs = 1500): Promise<void> {
  if (readEnv() !== undefined) return Promise.resolve()
  return new Promise((resolve) => {
    const done = (): void => {
      window.removeEventListener('ncw:widget:env', done)
      clearTimeout(timer)
      resolve()
    }
    const timer = setTimeout(done, timeoutMs)
    window.addEventListener('ncw:widget:env', done)
  })
}
