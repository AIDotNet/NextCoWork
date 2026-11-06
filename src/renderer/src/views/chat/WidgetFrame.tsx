/**
 * 可视化 widget 的**宿主侧外壳** —— 直接长在对话里的那个 iframe(没有卡片框)。
 *
 * 需求:模型产出的一段 HTML/SVG/TSX 要长在对话里,html/svg **边生成边长出来**,
 * tsx 写完一次性挂载。它是内置 `visualize_show_widget` 的渲染末端,和插件卡片
 * (`shell/PluginCardFrame.tsx`)是同一类东西的两个变体。刻意没有合并成一个
 * 组件:插件卡片拿的是"一次性推入的只读快照 + pluginId 反查",这里拿的是
 * "一份持续增长的代码 + 自己的 scheme",数据方向与生命周期都不一样
 * (见 `shared/agent/tool-card.ts` 里 `kind: 'widget'` 上那段说明)。
 *
 * ## 四条不变式
 *
 * 1. **iframe 是不透明源**(`sandbox="allow-scripts"` 而**没有** `allow-same-origin`)。
 *    widget 是模型生成的任意代码,一旦允许同源,它就能拿到父文档 ——
 *    而父文档里有那份 preload 桥。全仓唯一被允许同源嵌入的是插件视图,
 *    那是因为插件有清单、有权限声明、由用户显式安装。
 * 2. **只信 `event.source`。** 不透明源发来的消息 `event.origin` 是字符串
 *    `"null"`,拿它做判据等于没判。身份来自"这个窗口手里的那个 contentWindow
 *    引用"—— 冒充者拿不到它。
 * 3. **高度由 iframe 报、宿主钳制。** 不钳制的话一个报 100000px 的 widget
 *    能把聊天顶飞;钳得太小又会让内容出现内滚动条。
 * 4. **iframe 的每一次请求都当成不可信输入。** `handleCall` 逐字段收窄;会影响
 *    外部的(打开链接、跑动作、调模型)由**这里**画确认框,框里显示真实的地址与
 *    入参,而不是 widget 自己写的文案。能做什么的边界在主进程(`widget-host.ts`)。
 *
 * ## 为什么这里有一份"半截代码"
 *
 * `code` 从两个来源来,取决于这次工具调用走到哪一步(`WidgetDetail.tsx` 里写着):
 * 生成中拿的是**半截参数**,生成完拿的是卡片里那份完整代码。两条路都汇到
 * 这个组件的 `code`/`final` 两个 prop 上 —— 于是"边写边渲染"和"重开对话看到成品"
 * 是同一段渲染代码,不需要两套。
 */
import { Copy, RotateCw } from 'lucide-react'
import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type { ToolCard } from '../../../../shared/agent/tool-card'
import {
  WIDGET_LIMITS,
  WIDGET_MESSAGE,
  WIDGET_SHELL_URL,
  type WidgetAction,
  type WidgetEnvironment,
  type WidgetFormat,
  type WidgetHostMethod,
  resolveWorkspacePath
} from '../../../../shared/domain/widget'
import { WIDGET_SOURCE_VARS, widgetTokens } from '../../theme/widget-tokens'
import { cn } from '../../lib/cn'
import { useI18n } from '../../i18n'
import { Dialog } from '../../components/ui/Dialog'
import { Button } from '../../components/ui/Button'
import { IconButton } from '../../components/ui/IconButton'
import { copyText, openExternal } from '../../services/app'
import { completeForWidget, getWidgetState, runWidgetAction, saveWidgetFile, setWidgetState, submitWidget } from '../../services/widget'
import { toast } from '../../stores/toast'
import { createPushScheduler } from './widget-push'
import { useTranscriptTools } from './thread-tables'
import { useWidgetHost } from './widget-host'

/**
 * 高度钳制区间。
 *
 * ★ 下限很小:widget 外面没有框了,生成期只有一行加载提示 —— 先撑出一个 60px 的空白
 *   反而像是"这里有张卡没加载出来"。
 * ★ 上限 1400 —— 仪表盘与长流程图是这套东西的常规产物。上限的作用只是挡住
 *   "一次失控的高度上报",不是限流正常内容。
 */
const MIN_HEIGHT = 24
const MAX_HEIGHT = 1400

/** 加载提示的轮换间隔。只在内容出现之前有意义。 */
const LOADING_ROTATE_MS = 2200

/**
 * 应用自己的那批 token,原名推给 iframe。TSX 模式里 `nextcowork/ui` 的控件认的是
 * 这些名字(theme.css 的 `--color-*`),而 `widgetTokens` 给的是规范里那套名字 ——
 * 少了这批,控件会停在 theme.css 的静态默认色上,不跟换色器走。
 */
const APP_VARS = [
  'app', 'canvas', 'surface', 'chrome', 'surface-raised', 'surface-input', 'surface-field', 'surface-sunken',
  'surface-menu', 'tint', 'tint-hover', 'tint-strong', 'border', 'stroke', 'hairline', 'fg', 'fg-muted', 'fg-faint',
  'icon', 'accent', 'accent-fg', 'accent-soft', 'danger', 'warning', 'success',
  'chart-1', 'chart-2', 'chart-3', 'chart-4', 'chart-5', 'chart-6', 'chart-7', 'chart-8', 'chart-other'
].map((name) => `--color-${name}`)

export interface WidgetFrameProps {
  /** 当前这份代码 —— 生成中是半截的,完成后是完整的。tsx 时是**编译产物**(完成后才有) */
  code: string
  /** 生成是否已结束。为真时 iframe 里才会执行脚本 / 挂载组件(只一次) */
  final: boolean
  /** 无障碍名字,也是 widget 的标识(模型给的 snake_case),属领域值,不翻译 */
  title: string
  format?: WidgetFormat
  /** 产出它的那次工具调用。状态持久化、动作、提交都按它认这张卡 —— 生成期没有 */
  callId?: string
  /** 模型写的源码原文,给"复制源码"用。缺省时不显示那颗按钮 */
  source?: string
  interactive?: boolean
  submitted?: boolean
  actions?: readonly WidgetAction[]
  /** 内容出现之前轮换显示的提示词,来自模型。可能为空数组 */
  loadingMessages?: readonly string[]
  className?: string
}

/** 等用户在确认框里点一下的那一件事。 */
type Confirm =
  | { kind: 'url'; url: string; resolve: (ok: boolean) => void }
  | { kind: 'action'; label: string; toolName: string; input: unknown; resolve: (ok: boolean) => void }
  | { kind: 'complete'; resolve: (ok: boolean) => void }

/** `Omit` 对联合类型不分配 —— 逐支去掉 resolve。 */
type ConfirmRequest = Confirm extends infer C ? (C extends Confirm ? Omit<C, 'resolve'> : never) : never

/** 只收字符串,超长就拒绝 —— 截断后的东西(尤其 JSON)是坏的。 */
function text(args: Record<string, unknown>, key: string, max: number): string {
  const value = args[key]
  if (typeof value !== 'string') throw new Error(`${key} must be a string`)
  if (value.length > max) throw new Error(`${key} is too long (limit ${String(max)} characters)`)
  return value
}

export function WidgetFrame({
  code,
  final,
  title,
  format = 'html',
  callId,
  source,
  interactive = false,
  submitted = false,
  actions,
  loadingMessages,
  className
}: WidgetFrameProps): ReactNode {
  const { t } = useI18n()
  const host = useWidgetHost()
  const tools = useTranscriptTools()
  const frameRef = useRef<HTMLIFrameElement>(null)
  const [height, setHeight] = useState(MIN_HEIGHT)
  const [loadingIndex, setLoadingIndex] = useState(0)
  const [generation, setGeneration] = useState(0)
  const [confirm, setConfirm] = useState<Confirm | null>(null)
  const completeAllowed = useRef(false)

  /*
    持久化状态。生成期(`callId` 缺省)与没有宿主的地方(只读面板)不读不写:
    ★ 读回来之前**不推环境** —— iframe 里的组件会等第一份环境再挂载(`envReady`),
    否则滑块会先停在默认值、状态一到再跳回去。
  */
  const sessionId = host?.sessionId
  const [state, setState] = useState<{ loaded: boolean; value: unknown }>({ loaded: false, value: null })
  useEffect(() => {
    if (!final || callId === undefined || sessionId === undefined) {
      setState({ loaded: true, value: null })
      return
    }
    let cancelled = false
    getWidgetState(sessionId, callId)
      .then((value) => { if (!cancelled) setState({ loaded: true, value }) })
      .catch(() => { if (!cancelled) setState({ loaded: true, value: null }) })
    return () => { cancelled = true }
  }, [final, callId, sessionId])

  /**
   * 模型经 `visualize_update_widget` 推来的最新一份数据 —— 按 `title` 认领,后来者覆盖。
   * ★ 遍历的是会话级的工具表,插入顺序就是调用顺序。
   */
  const data = useMemo(() => {
    let latest: unknown = null
    for (const call of Object.values(tools)) {
      const card: ToolCard | undefined = call.output?.card
      if (card?.kind === 'widget_data' && card.title === title) latest = card.data
    }
    return latest
  }, [tools, title])

  const env = useMemo<WidgetEnvironment>(() => ({
    title,
    state: state.value,
    data,
    interactive,
    submitted,
    actions: (actions ?? []).map(({ id, label, params }) => ({ id, label, ...(params === undefined ? {} : { params }) }))
  }), [title, state.value, data, interactive, submitted, actions])

  /**
   * 主题变量。**在推送那一刻现读**,不在渲染期读:换色器改的是
   * `documentElement` 上的 CSS 变量,而 React 不会因为 CSS 变量变化而重渲。
   */
  const readTheme = useCallback((): { tokens: Record<string, string>; appearance: string; motion: string } => {
    const root = document.documentElement
    const style = getComputedStyle(root)
    const sourceVars: Partial<Record<(typeof WIDGET_SOURCE_VARS)[number], string>> = {}
    for (const name of WIDGET_SOURCE_VARS) sourceVars[name] = style.getPropertyValue(name).trim()
    const appearance = root.dataset['theme'] === 'light' ? 'light' : 'dark'
    const tokens = widgetTokens(appearance, sourceVars)
    for (const name of APP_VARS) {
      const value = style.getPropertyValue(name).trim()
      if (value !== '' && tokens[name] === undefined) tokens[name] = value
    }
    return { tokens, appearance, motion: root.dataset['themeMotion'] ?? 'full' }
  }, [])

  /** 往 iframe 里发一条消息。targetOrigin 只能是 `'*'` —— 见文件头第 2 条。 */
  const post = useCallback((message: Record<string, unknown>) => {
    frameRef.current?.contentWindow?.postMessage(message, '*')
  }, [])

  const postEnv = useCallback(() => {
    if (state.loaded) post({ type: WIDGET_MESSAGE.env, env })
  }, [post, env, state.loaded])

  /** 把当前状态整份重推。`load` 与 `ready` 都走它 —— 两边的启动顺序不需要靠猜。 */
  const pushAll = useCallback(() => {
    const theme = readTheme()
    post({ type: WIDGET_MESSAGE.theme, tokens: theme.tokens, appearance: theme.appearance, motion: theme.motion })
    post({ type: WIDGET_MESSAGE.loading, message: loadingMessages?.[loadingIndex] ?? '' })
    postEnv()
    // tsx 的半截代码没有意义(也不是编译产物),只在收尾时推
    if (format !== 'tsx' || final) post({ type: WIDGET_MESSAGE.content, html: code, final, format })
  }, [post, readTheme, postEnv, loadingMessages, loadingIndex, code, final, format])

  /*
    推送节流。★ 依赖里刻意**没有** `code`/`final`:调度器是一份长期存活的对象,
    内容变化经 `offer` 交给它,而不是每次都重建 —— 重建会丢掉待发的那一份,
    也会把"到点就发"的节奏打乱。见 `widget-push.ts` 的文件头。
  */
  const scheduler = useMemo(
    () => createPushScheduler((html, isFinal) => post({ type: WIDGET_MESSAGE.content, html, final: isFinal, format })),
    [post, format]
  )

  useEffect(() => {
    if (format === 'tsx' && !final) return
    scheduler.offer(code, final)
  }, [scheduler, code, final, format])

  // 卸载前把待发的最后一份发出去,再拆掉定时器。
  useEffect(() => () => {
    scheduler.flush()
    scheduler.dispose()
  }, [scheduler])

  // 环境变了(状态读回来、模型推了新数据、交了卷)就推。
  useEffect(() => {
    postEnv()
  }, [postEnv])

  // 主题变化(换色器 / 深色切换 / 动效档位)要重推,否则 widget 会停在旧配色上。
  useEffect(() => {
    const root = document.documentElement
    const observer = new MutationObserver(() => {
      const theme = readTheme()
      post({ type: WIDGET_MESSAGE.theme, tokens: theme.tokens, appearance: theme.appearance, motion: theme.motion })
    })
    observer.observe(root, { attributes: true, attributeFilter: ['data-theme', 'data-theme-motion', 'data-theme-font', 'data-theme-weight', 'style'] })
    return () => {
      observer.disconnect()
    }
  }, [post, readTheme])

  // 加载提示轮换。内容一到就没用了(iframe 里自己会把它藏掉)。
  useEffect(() => {
    if (final || (loadingMessages?.length ?? 0) <= 1) return
    const timer = setInterval(() => {
      setLoadingIndex((index) => index + 1)
    }, LOADING_ROTATE_MS)
    return () => {
      clearInterval(timer)
    }
  }, [final, loadingMessages])

  // 提示换了一条也要推下去。跟着 loadingIndex 走。
  useEffect(() => {
    const message = loadingMessages?.[loadingIndex % Math.max(1, loadingMessages.length)]
    if (message !== undefined) post({ type: WIDGET_MESSAGE.loading, message })
  }, [post, loadingMessages, loadingIndex])

  /** 让用户点一下。同一时刻只允许一个确认框 —— 第二个请求直接拒,不排队。 */
  const ask = useCallback((request: ConfirmRequest): Promise<boolean> => {
    return new Promise((resolve) => {
      setConfirm((current) => {
        if (current !== null) {
          resolve(false)
          return current
        }
        return { ...request, resolve } as Confirm
      })
    })
  }, [])

  const settle = (ok: boolean): void => {
    confirm?.resolve(ok)
    setConfirm(null)
  }

  /**
   * iframe 请求的一项宿主能力。抛错 = 拒绝,错误信息原样回给 widget。
   * ★ 每一支都先收窄参数再做事 —— 这里是不可信输入进可信进程的那道门(文件头第 4 条)。
   */
  const handleCall = useCallback(async (method: WidgetHostMethod, args: Record<string, unknown>): Promise<unknown> => {
    const needHost = (): NonNullable<typeof host> => {
      if (host === undefined) throw new Error('This widget is shown read-only here.')
      return host
    }
    const needCall = (): string => {
      if (callId === undefined) throw new Error('The widget is still being generated.')
      return callId
    }
    switch (method) {
      case 'copy':
        await copyText(text(args, 'text', WIDGET_LIMITS.copy))
        toast.success(t('widget.copied'), 'widget-copy')
        return true
      case 'download': {
        const encoding = args['encoding'] === 'base64' ? 'base64' : 'utf8'
        const saved = await saveWidgetFile(text(args, 'name', 200), text(args, 'data', WIDGET_LIMITS.download * 1.4), encoding)
        if (saved !== null) toast.success(t('widget.saved'), 'widget-save')
        return saved !== null
      }
      case 'openFile': {
        const current = needHost()
        const resolved = resolveWorkspacePath(current.workspaceRoot, text(args, 'path', 4096))
        if (resolved === null) throw new Error('Only files inside the workspace can be opened.')
        current.openFile(resolved)
        return true
      }
      case 'openUrl': {
        const url = text(args, 'url', 4096)
        let parsed: URL
        try {
          parsed = new URL(url)
        } catch {
          throw new Error('Invalid URL.')
        }
        if (parsed.protocol !== 'https:') throw new Error('Only https links can be opened.')
        if (!(await ask({ kind: 'url', url: parsed.toString() }))) throw new Error('The user declined to open the link.')
        await openExternal(parsed.toString())
        return true
      }
      case 'toast': {
        const tone = args['tone'] === 'success' || args['tone'] === 'error' ? args['tone'] : 'info'
        toast[tone](text(args, 'message', WIDGET_LIMITS.toast), `widget-toast-${title}`)
        return true
      }
      case 'setState': {
        const current = needHost()
        const json = JSON.stringify(args['state'] ?? null)
        if (json.length > WIDGET_LIMITS.state) throw new Error(`Widget state is limited to ${String(WIDGET_LIMITS.state)} characters.`)
        await setWidgetState(current.sessionId, needCall(), JSON.parse(json) as unknown)
        return true
      }
      case 'submit': {
        if (!interactive) throw new Error('This widget is not waiting for a submission.')
        if (submitted) throw new Error('This widget has already been submitted.')
        await submitWidget(needCall(), text(args, 'value', WIDGET_LIMITS.submit))
        return true
      }
      case 'draft':
        needHost().draft(text(args, 'text', WIDGET_LIMITS.draft))
        toast.info(t('widget.drafted'), 'widget-draft')
        return true
      case 'setContext':
        needHost().setContext(callId ?? title, title, text(args, 'text', WIDGET_LIMITS.context))
        return true
      case 'runAction': {
        const current = needHost()
        const actionId = text(args, 'id', 64)
        const rawParams = args['params']
        const params = rawParams !== null && typeof rawParams === 'object' && !Array.isArray(rawParams) ? (rawParams as Record<string, unknown>) : {}
        const request = { sessionId: current.sessionId, callId: needCall(), actionId, params, confirmed: false }
        let result = await runWidgetAction(request)
        if (result.kind === 'ask') {
          const label = actions?.find((action) => action.id === actionId)?.label ?? actionId
          if (!(await ask({ kind: 'action', label, toolName: result.toolName, input: result.input }))) {
            throw new Error('The user declined this action.')
          }
          result = await runWidgetAction({ ...request, confirmed: true })
        }
        if (result.kind === 'deny') throw new Error(result.reason)
        if (result.kind !== 'result') throw new Error('The action needs confirmation again.')
        return { ok: result.ok, output: result.output }
      }
      case 'complete': {
        const current = needHost()
        if (!completeAllowed.current) {
          if (!(await ask({ kind: 'complete' }))) throw new Error('The user did not allow model calls from this widget.')
          completeAllowed.current = true
        }
        const system = args['system']
        const maxTokens = args['maxTokens']
        return await completeForWidget({
          sessionId: current.sessionId,
          prompt: text(args, 'prompt', WIDGET_LIMITS.completePrompt),
          ...(typeof system === 'string' ? { system: text(args, 'system', WIDGET_LIMITS.completeSystem) } : {}),
          ...(typeof maxTokens === 'number' && Number.isFinite(maxTokens) ? { maxTokens } : {})
        })
      }
    }
  }, [host, callId, interactive, submitted, actions, title, ask, t])

  useEffect(() => {
    const frame = frameRef.current
    if (frame === null) return
    const onMessage = (event: MessageEvent): void => {
      // ★ 只认这一个 frame 发来的。见文件头第 2 条:origin 在这里是 "null"。
      if (event.source !== frame.contentWindow) return
      // 入参是 `unknown` 的世界:iframe 里跑的是页面脚本,消息体只能逐字段收窄。
      const data = event.data as { type?: unknown; height?: unknown; id?: unknown; method?: unknown; args?: unknown } | null
      if (data === null || typeof data !== 'object') return
      if (data.type === WIDGET_MESSAGE.ready) {
        pushAll()
        return
      }
      if (data.type === WIDGET_MESSAGE.height && typeof data.height === 'number' && Number.isFinite(data.height)) {
        setHeight(Math.max(MIN_HEIGHT, Math.min(MAX_HEIGHT, Math.round(data.height))))
        return
      }
      if (data.type === WIDGET_MESSAGE.call && typeof data.id === 'number' && typeof data.method === 'string') {
        const id = data.id
        const args = data.args !== null && typeof data.args === 'object' && !Array.isArray(data.args) ? (data.args as Record<string, unknown>) : {}
        handleCall(data.method as WidgetHostMethod, args)
          .then((value) => { post({ type: WIDGET_MESSAGE.reply, id, ok: true, value: value ?? null }) })
          .catch((err: unknown) => {
            post({ type: WIDGET_MESSAGE.reply, id, ok: false, error: err instanceof Error ? err.message : String(err) })
          })
      }
    }
    const onLoad = (): void => pushAll()
    window.addEventListener('message', onMessage)
    frame.addEventListener('load', onLoad)
    return () => {
      window.removeEventListener('message', onMessage)
      frame.removeEventListener('load', onLoad)
    }
  }, [pushAll, handleCall, post])

  const reload = (): void => {
    setHeight(MIN_HEIGHT)
    setGeneration((value) => value + 1)
  }

  return (
    <div className={cn('group/widget relative -mx-[2px]', className)}>
      <iframe
        key={generation}
        ref={frameRef}
        src={WIDGET_SHELL_URL}
        title={title}
        // 见文件头第 1 条。allow-forms 是给规范里那批表单/滑块用的。
        sandbox="allow-scripts allow-forms"
        style={{ height }}
        className="block w-full border-0 bg-transparent"
      />
      {/*
        悬停才出现的两颗按钮。widget 外面没有标题行了,复制源码 / 重新渲染只能放在这里;
        平时完全不可见,不给内容加任何框。
      */}
      {final && (
        <div className="pointer-events-none absolute top-1 right-1 flex gap-0.5 opacity-0 transition-opacity group-hover/widget:pointer-events-auto group-hover/widget:opacity-100 focus-within:pointer-events-auto focus-within:opacity-100">
          {source !== undefined && (
            <IconButton
              label={t('widget.toolbar.copySource')}
              size={24}
              onClick={() => {
                void copyText(source).then(() => toast.success(t('widget.copied'), 'widget-copy'))
              }}
            >
              <Copy size={13} />
            </IconButton>
          )}
          <IconButton label={t('widget.toolbar.reload')} size={24} onClick={reload}>
            <RotateCw size={13} />
          </IconButton>
        </div>
      )}
      <Dialog
        open={confirm !== null}
        onClose={() => { settle(false) }}
        title={
          confirm?.kind === 'url'
            ? t('widget.url.title')
            : confirm?.kind === 'action'
              ? t('widget.action.title', { label: confirm.label })
              : t('widget.complete.title')
        }
        width={480}
        footer={
          <>
            <Button onClick={() => { settle(false) }}>{t('widget.cancel')}</Button>
            <Button variant="accent" onClick={() => { settle(true) }}>
              {confirm?.kind === 'url' ? t('widget.url.open') : confirm?.kind === 'action' ? t('widget.action.run') : t('widget.complete.allow')}
            </Button>
          </>
        }
      >
        {confirm?.kind === 'url' && (
          <div className="flex flex-col gap-2 text-[13px]">
            <p className="m-0 text-fg-muted">{t('widget.url.body')}</p>
            <code className="block break-all rounded-[6px] bg-surface-sunken px-2 py-1.5 font-mono text-[12px] text-fg">{confirm.url}</code>
          </div>
        )}
        {confirm?.kind === 'action' && (
          <div className="flex flex-col gap-2 text-[13px]">
            <p className="m-0 text-fg-muted">{t('widget.action.body')}</p>
            <div className="text-[12px] font-medium text-fg">{confirm.toolName}</div>
            <pre className="m-0 max-h-[240px] overflow-auto rounded-[6px] bg-surface-sunken px-2 py-1.5 font-mono text-[12px] whitespace-pre-wrap break-all text-fg">
              {JSON.stringify(confirm.input, null, 2)}
            </pre>
          </div>
        )}
        {confirm?.kind === 'complete' && <p className="m-0 text-[13px] text-fg-muted">{t('widget.complete.body')}</p>}
      </Dialog>
    </div>
  )
}
