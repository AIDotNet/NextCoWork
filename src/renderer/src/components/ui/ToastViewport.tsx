/**
 * toast 的显示层 —— Arc 的 `ToastStack`。整个应用只挂一次(`App.tsx`),内容从 `stores/toast` 来。
 *
 * 堆叠、展开、滑动关闭、悬停暂停全是 Arc 的。这里只做一件事:把 `stores/toast` 的
 * 增 / 改 / 删同步成 Arc 的 `toast()` / `dismiss()`。store 不动,是因为它被 React 之外的
 * 代码调用,而且它的三条约束(错误不自动消失、同 key 替换并计数、最多 3 条)是产品规则,
 * 不是显示层的事:
 *
 *   - 错误:`duration: null` → Arc 的 `Infinity`,等用户自己关;
 *   - 同 key 再次触发:store 复用同一个 id,这里用同一个 id 再调一次 `toast()`,
 *     Arc 就地更新并重新计时;计数 ≥2 时拼在正文后面;
 *   - store 挤掉 / 关掉的那条:这里 `dismiss(id)`。
 *
 * ★ store 原本靠显示层的计时器自己出队,这一份计时器仍然留在这里。否则 store 里会
 *   一直挂着 Arc 早已关掉的那条,下一次同 key 触发时计数会从旧值接着往上数。
 */
import { useEffect, useRef, type ReactNode } from 'react'
import { useI18n } from '../../i18n'
import { useToastStore, type Toast } from '../../stores/toast'
import { ToastStack, ToastStackProvider, useToastStack } from '../arc/toast-stack/toast-stack'

function ToastSync(): null {
  const { t } = useI18n()
  const toasts = useToastStore((s) => s.toasts)
  const dismissStored = useToastStore((s) => s.dismiss)
  const { toast, dismiss } = useToastStack()
  /** 已经同步给 Arc 的版本:id → `count` */
  const shown = useRef(new Map<string, number>())
  const timers = useRef(new Map<string, number>())

  useEffect(() => {
    const live = new Set(toasts.map((x) => x.id))
    for (const id of [...shown.current.keys()]) {
      if (live.has(id)) continue
      shown.current.delete(id)
      window.clearTimeout(timers.current.get(id))
      timers.current.delete(id)
      dismiss(id)
    }
    for (const item of toasts) {
      if (shown.current.get(item.id) === item.count) continue
      shown.current.set(item.id, item.count)
      toast({
        id: item.id,
        type: item.tone,
        title: titleOf(item, t),
        duration: item.duration ?? Infinity
      })
      window.clearTimeout(timers.current.get(item.id))
      if (item.duration !== null) {
        timers.current.set(item.id, window.setTimeout(() => dismissStored(item.id), item.duration))
      }
    }
  }, [toasts, toast, dismiss, dismissStored, t])

  useEffect(() => {
    const pending = timers.current
    return () => {
      for (const id of pending.values()) window.clearTimeout(id)
    }
  }, [])

  return null
}

function titleOf(item: Toast, t: ReturnType<typeof useI18n>['t']): string {
  return item.count > 1 ? `${item.message} (${t('toast.repeated', { count: String(item.count) })})` : item.message
}

export function ToastViewport({ children }: { children?: ReactNode }): ReactNode {
  return (
    <ToastStackProvider>
      {children}
      <ToastSync />
      <ToastStack className="ncw-toasts app-no-drag" />
    </ToastStackProvider>
  )
}
