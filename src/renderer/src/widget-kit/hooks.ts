/**
 * TSX widget 的宿主能力,包成 React 的写法。
 *
 * 每个 hook 背后都是 `host.ts` 的一次 `call`;能不能做成由宿主判,这里只负责
 * 把"等结果 / 出错 / 进行中"这些状态替 widget 管起来 —— 模型写的组件里不该
 * 出现手写的请求 id 和 message 监听。
 */
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from 'react'
import type { WidgetEnvironment } from '../../../shared/domain/widget'
import { call, getEnv, subscribeEnv } from './host'

/** 宿主能力的命令式入口。按钮的 onClick 里直接调。 */
export const host = {
  /** 复制文字到剪贴板(宿主弹一条提示) */
  copy: (text: string): Promise<void> => call('copy', { text }).then(() => undefined),
  /**
   * 让用户把一份内容存成文件(系统保存对话框,路径由用户选)。
   * `data` 是文本;二进制传 base64 并给 `encoding: 'base64'`。
   */
  download: (name: string, data: string, options: { mime?: string; encoding?: 'utf8' | 'base64' } = {}): Promise<boolean> =>
    call('download', { name, data, mime: options.mime ?? 'text/plain', encoding: options.encoding ?? 'utf8' }).then((saved) => saved === true),
  /** 在编辑器里打开工作区里的一个文件(工作区相对路径或绝对路径) */
  openFile: (path: string): Promise<void> => call('openFile', { path }).then(() => undefined),
  /** 打开外链(https)。宿主会先让用户确认真实地址 */
  openUrl: (url: string): Promise<void> => call('openUrl', { url }).then(() => undefined),
  /** 一条轻提示 */
  toast: (message: string, tone: 'info' | 'success' | 'error' = 'info'): Promise<void> =>
    call('toast', { message, tone }).then(() => undefined),
  /** 把文字填进输入框(**不发送**,由用户决定发不发) */
  draft: (text: string): Promise<void> => call('draft', { text }).then(() => undefined),
  /**
   * 发布一段状态摘要,随用户的**下一条**消息一起交给模型(用户看得见、可以移除)。
   * 再调一次会替换上一份;传空串清除。
   */
  setContext: (summary: string): Promise<void> => call('setContext', { text: summary }).then(() => undefined),
  /**
   * 交互模式下把结果交回给模型。只能交一次;非交互模式下会被拒绝。
   * `value` 必须能 JSON 序列化。
   */
  submit: (value: unknown): Promise<void> => call('submit', { value: JSON.stringify(value ?? null) }).then(() => undefined),
  /**
   * 调一次模型(不带工具、不进对话)。第一次调用会请用户允许。
   */
  complete: (prompt: string, options: { system?: string; maxTokens?: number } = {}): Promise<string> =>
    call('complete', { prompt, ...options }).then((text) => (typeof text === 'string' ? text : '')),
  /** 触发一个预先声明的动作。结果是工具的文本输出 */
  runAction: (id: string, params: Record<string, string | number> = {}): Promise<{ ok: boolean; output: string }> =>
    call('runAction', { id, params }).then((result) => {
      const value = result as { ok?: unknown; output?: unknown } | null
      return { ok: value?.ok === true, output: typeof value?.output === 'string' ? value.output : '' }
    })
}

export type WidgetHost = typeof host

export function useHost(): WidgetHost {
  return host
}

export function useEnvironment(): WidgetEnvironment {
  return useSyncExternalStore(subscribeEnv, getEnv)
}

// ── 持久化状态 ──

/**
 * 整张卡的状态是**一个对象**,按 key 分格。整份存、整份读(≤16KB)。
 * 本地副本立即生效,写回宿主做去抖 —— 拖滑块时每帧一次 IPC 没有意义。
 */
let stateDoc: Record<string, unknown> | null = null
const stateListeners = new Set<() => void>()
let flushTimer: ReturnType<typeof setTimeout> | undefined

function doc(): Record<string, unknown> {
  if (stateDoc === null) {
    const initial = getEnv().state
    stateDoc = initial !== null && typeof initial === 'object' && !Array.isArray(initial) ? { ...(initial as Record<string, unknown>) } : {}
  }
  return stateDoc
}

function scheduleFlush(): void {
  if (flushTimer !== undefined) clearTimeout(flushTimer)
  flushTimer = setTimeout(() => {
    flushTimer = undefined
    void call('setState', { state: doc() }).catch((err: unknown) => {
      console.warn('[widget] state was not saved:', err)
    })
  }, 400)
}

/**
 * 像 `useState`,但值会随这张卡持久化 —— 重开对话时滑块、Tab、表单还在原处。
 * `key` 在同一个 widget 内唯一即可。值必须能 JSON 序列化。
 */
export function useWidgetState<T>(key: string, initial: T): [T, (next: T | ((previous: T) => T)) => void] {
  const subscribe = useCallback((listener: () => void) => {
    stateListeners.add(listener)
    return () => {
      stateListeners.delete(listener)
    }
  }, [])
  const value = useSyncExternalStore(subscribe, () => doc()[key])
  const current = (value === undefined ? initial : value) as T
  const currentRef = useRef(current)
  currentRef.current = current
  const set = useCallback(
    (next: T | ((previous: T) => T)) => {
      const resolved = typeof next === 'function' ? (next as (previous: T) => T)(currentRef.current) : next
      stateDoc = { ...doc(), [key]: resolved }
      for (const listener of stateListeners) listener()
      scheduleFlush()
    },
    [key]
  )
  return [current, set]
}

/**
 * 模型经 `visualize_update_widget` 推来的最新数据;还没推过时是 `initial`。
 */
export function useWidgetData<T>(initial: T): T {
  const env = useEnvironment()
  return (env.data === null || env.data === undefined ? initial : env.data) as T
}

// ── 交互模式 ──

export interface Submission {
  /** 这张卡是不是在等提交 */
  interactive: boolean
  /** 已经交过了(之后 `submit` 会被拒) */
  submitted: boolean
  submitting: boolean
  error: string | null
  submit(value: unknown): Promise<void>
}

export function useSubmit(): Submission {
  const env = useEnvironment()
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const submit = useCallback(async (value: unknown) => {
    setSubmitting(true)
    setError(null)
    try {
      await host.submit(value)
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    } finally {
      setSubmitting(false)
    }
  }, [])
  return { interactive: env.interactive, submitted: env.submitted, submitting, error, submit }
}

// ── 动作 ──

export interface ActionHandle {
  /** 这个 id 在声明里存在吗。不存在时 `run` 会被拒 */
  available: boolean
  label: string
  status: 'idle' | 'running' | 'done' | 'error'
  /** 最近一次的文本输出(成功或失败都有) */
  output: string
  run(params?: Record<string, string | number>): Promise<{ ok: boolean; output: string }>
}

export function useAction(id: string): ActionHandle {
  const env = useEnvironment()
  const declared = env.actions.find((action) => action.id === id)
  const [status, setStatus] = useState<ActionHandle['status']>('idle')
  const [output, setOutput] = useState('')
  const mounted = useRef(true)
  useEffect(() => () => {
    mounted.current = false
  }, [])
  const run = useCallback(
    async (params: Record<string, string | number> = {}) => {
      setStatus('running')
      try {
        const result = await host.runAction(id, params)
        if (mounted.current) {
          setOutput(result.output)
          setStatus(result.ok ? 'done' : 'error')
        }
        return result
      } catch (err) {
        const message = err instanceof Error ? err.message : String(err)
        if (mounted.current) {
          setOutput(message)
          setStatus('error')
        }
        return { ok: false, output: message }
      }
    },
    [id]
  )
  return { available: declared !== undefined, label: declared?.label ?? id, status, output, run }
}

// ── 模型调用 ──

export interface Completion {
  status: 'idle' | 'running' | 'done' | 'error'
  text: string
  error: string | null
  complete(prompt: string, options?: { system?: string; maxTokens?: number }): Promise<string>
}

export function useCompletion(): Completion {
  const [status, setStatus] = useState<Completion['status']>('idle')
  const [text, setText] = useState('')
  const [error, setError] = useState<string | null>(null)
  const complete = useCallback(async (prompt: string, options: { system?: string; maxTokens?: number } = {}) => {
    setStatus('running')
    setError(null)
    try {
      const result = await host.complete(prompt, options)
      setText(result)
      setStatus('done')
      return result
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
      setStatus('error')
      return ''
    }
  }, [])
  return { status, text, error, complete }
}

// ── 主题 ──

export interface WidgetTheme {
  appearance: 'light' | 'dark'
  /** 用户的动效档位。`off` / `reduced` 时别做大动画 */
  motion: string
}

let themeSnapshot: WidgetTheme = readTheme()

function readTheme(): WidgetTheme {
  const root = document.documentElement
  return { appearance: root.dataset['theme'] === 'light' ? 'light' : 'dark', motion: root.dataset['themeMotion'] ?? 'full' }
}

export function useTheme(): WidgetTheme {
  return useSyncExternalStore(
    (listener) => {
      const handler = (): void => {
        const next = readTheme()
        if (next.appearance === themeSnapshot.appearance && next.motion === themeSnapshot.motion) return
        themeSnapshot = next
        listener()
      }
      window.addEventListener('ncw:theme', handler)
      return () => {
        window.removeEventListener('ncw:theme', handler)
      }
    },
    () => themeSnapshot
  )
}
