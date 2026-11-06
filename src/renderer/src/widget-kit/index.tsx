/**
 * `nextcowork/widget` —— TSX widget 能 import 的宿主组件与能力。
 *
 * 由 `scripts/build-plugin-runtime.mjs` 打成 `resources/plugin-runtime/widget.js`,
 * 经 `ncw-widget://shell/__widget.js` 下发(`net/widget-protocol.ts` 的 import map)。
 *
 * 三部分:
 * 1. **宿主控件**(Button / Slider / Select …)—— 原样转发 `nextcowork/ui`,构建时
 *    标成 external,和插件视图用的是同一份实现,不在这里复制;
 * 2. **展示与布局**(Stack / Grid / Stat / Table …)—— `components.tsx`;
 * 3. **宿主能力**(useWidgetState / useAction / host.draft …)—— `hooks.ts`。
 *
 * ★ `Dialog` / `Menu` / `ContextMenu` 刻意不转发:它们是整窗口的浮层,而 widget
 * 是对话里的一块 iframe —— 浮层只会被裁在 iframe 里,看起来像是坏了。
 */
import { Component, type ErrorInfo, type ReactNode } from 'react'
import { createRoot } from 'react-dom/client'
import { Button } from '../plugin-ui/ui'
import { envReady, getEnv, call } from './host'

export {
  Button,
  IconButton,
  ActionIconButton,
  TextInput,
  TextArea,
  NumberInput,
  Toggle,
  Slider,
  Segmented,
  Select,
  CheckboxCards,
  RadioCards,
  EmptyState,
  ProgressBar,
  Spinner,
  Tooltip,
  cn
} from '../plugin-ui/ui'
export type { SelectOption, ChoiceOption } from '../plugin-ui/ui'

export * from './components'
export {
  host,
  useHost,
  useEnvironment,
  useWidgetState,
  useWidgetData,
  useSubmit,
  useAction,
  useCompletion,
  useTheme
} from './hooks'
export type { WidgetHost, Submission, ActionHandle, Completion, WidgetTheme } from './hooks'

// ── 挂载 ──

interface BoundaryState {
  error: Error | null
}

/**
 * 渲染期的错误兜底。显示一行错误 + "让 AI 修复"(把错误填进输入框,不发送)。
 *
 * ★ 按钮走 `draft` 而不是自动发一条消息:widget 坏了是常态(模型写错一个属性名),
 * 但"坏了就自动开新一轮"等于让一张图替用户决定什么时候花 token。
 */
class WidgetBoundary extends Component<{ children: ReactNode }, BoundaryState> {
  override state: BoundaryState = { error: null }

  static getDerivedStateFromError(error: Error): BoundaryState {
    return { error }
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('[widget] render error', error, info.componentStack)
  }

  override render(): ReactNode {
    const { error } = this.state
    if (error === null) return this.props.children
    return <ErrorView error={error} />
  }
}

function ErrorView({ error }: { error: Error }): ReactNode {
  const title = getEnv().title
  const ask = (): void => {
    const name = title === '' ? 'the widget' : `the widget "${title}"`
    void call('draft', { text: `${name} failed to render: ${error.message}\nPlease fix it.` }).catch(() => {})
  }
  return (
    <div className="flex flex-col items-start gap-2 py-1">
      <div className="ncw-widget-error">{error.message}</div>
      <Button size="sm" onClick={ask}>
        Ask AI to fix
      </Button>
    </div>
  )
}

type Factory = (require: (name: string) => unknown, module: { exports: Record<string, unknown> }, exports: Record<string, unknown>) => void

/**
 * 外壳运行时调用的挂载入口(`widget-shell/runtime.ts` 的 `mountTsx`)。**不是给 widget 用的**。
 *
 * `modules` 是外壳事先 import 好的模块表;`factory` 是包起来的 CJS 编译产物。
 *
 * ★ `require` 返回的对象带 `__esModule: true`:sucrase 的默认导入走
 * `obj.__esModule ? obj.default : obj`。不标的话 `import React from 'react'` 拿到的是
 * 整个命名空间而不是 React —— 碰巧也能用,但 `import X from 'nextcowork/charts'` 这类
 * 没有默认导出的就会拿到一个对象而不是报错。
 */
export async function __mountWidget(container: HTMLElement, modules: Record<string, Record<string, unknown>>, factory: Factory): Promise<void> {
  await envReady()
  const loading = document.getElementById('ncw-loading')
  const root = createRoot(container)
  const fail = (error: unknown): void => {
    if (loading !== null) loading.hidden = true
    root.render(<ErrorView error={error instanceof Error ? error : new Error(String(error))} />)
  }

  const module: { exports: Record<string, unknown> } = { exports: {} }
  const require = (name: string): unknown => {
    const namespace = modules[name]
    if (namespace === undefined) throw new Error(`Cannot import "${name}" in a widget.`)
    return { ...namespace, __esModule: true }
  }
  try {
    factory(require, module, module.exports)
  } catch (err) {
    fail(err)
    return
  }
  const Widget = module.exports['default']
  if (typeof Widget !== 'function') {
    fail(new Error('The widget has no default export component (export default function Widget() { … }).'))
    return
  }
  if (loading !== null) loading.hidden = true
  const View = Widget as () => ReactNode
  root.render(
    <WidgetBoundary>
      <View />
    </WidgetBoundary>
  )
}
