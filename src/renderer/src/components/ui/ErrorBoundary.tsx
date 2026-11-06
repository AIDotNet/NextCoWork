/**
 * 渲染期错误兜底 —— 把「一个组件抛了」限制在它自己那一块。
 *
 * ★ React 里没有被任何边界接住的渲染期异常会卸载**整棵树**:Electron 窗口里的
 * 表现就是整窗白屏,连关掉出错那块的按钮都没有了。设置页的 MCP 编辑弹窗
 * 就这样坏过一次(库里一条缺 `envNames` 的配置,`.map` 到 undefined)。
 *
 * 用法:包在「坏了也只该坏这一块」的子树外面。`resetKey` 变了就清掉错误态
 * (比如切到另一个设置页、关掉再打开弹窗),不需要整棵重挂。
 *
 * `widget-kit` 里的 `WidgetBoundary` 是同一个思路,但它跑在 widget 沙箱里、
 * 回退界面要调宿主的 `draft`,所以那边单独一份,不复用这里。
 */
import { AlertTriangle } from 'lucide-react'
import { Component, type ErrorInfo, type ReactNode } from 'react'
import { useI18n } from '../../i18n'
import { Button } from '../arc/button/button'

interface Props {
  children: ReactNode
  /** 出现在控制台日志里,方便定位是哪一块坏了 */
  label: string
  /** 变化时清掉错误态 */
  resetKey?: unknown
  /** 回退界面上的「关闭」按钮。不传时显示「重试」(清掉错误态、重新渲染) */
  onDismiss?: () => void
}

interface State {
  error: Error | null
  resetKey: unknown
}

export class ErrorBoundary extends Component<Props, State> {
  override state: State = { error: null, resetKey: this.props.resetKey }

  static getDerivedStateFromError(error: unknown): Partial<State> {
    return { error: error instanceof Error ? error : new Error(String(error)) }
  }

  static getDerivedStateFromProps(props: Props, state: State): Partial<State> | null {
    if (Object.is(props.resetKey, state.resetKey)) return null
    return { error: null, resetKey: props.resetKey }
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error(`[ui] render error in ${this.props.label}`, error, info.componentStack)
  }

  private readonly reset = (): void => {
    this.setState({ error: null })
  }

  override render(): ReactNode {
    const { error } = this.state
    if (error === null) return this.props.children
    const { onDismiss } = this.props
    return (
      <ErrorFallback
        error={error}
        onAction={
          onDismiss === undefined
            ? this.reset
            : () => {
                this.reset()
                onDismiss()
              }
        }
        dismiss={onDismiss !== undefined}
      />
    )
  }
}

function ErrorFallback({
  error,
  onAction,
  dismiss
}: {
  error: Error
  onAction: () => void
  dismiss: boolean
}): ReactNode {
  const { t } = useI18n()
  return (
    <div role="alert" className="m-3 flex flex-col items-start gap-2 rounded-[8px] bg-danger/10 px-3 py-2.5">
      <p className="flex items-start gap-1.5 text-[12.5px] text-danger">
        <AlertTriangle size={13} className="mt-[2px] shrink-0" />
        <span>{t('common.renderError')}</span>
      </p>
      <code className="selectable break-all font-mono text-[11.5px] text-fg-muted">{error.message}</code>
      <Button size="sm" type="button" variant="secondary" onClick={onAction}>
        {dismiss ? t('common.close') : t('common.retry')}
      </Button>
    </div>
  )
}
