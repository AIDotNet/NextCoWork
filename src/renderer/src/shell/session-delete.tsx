/**
 * 侧边栏「删除会话」的确认流 —— 普通删除与「Agent 正在运行」两条路共用这一份。
 *
 * ★ 判「在不在跑」以**主进程的活体检查**为准,不以渲染层的运行中索引为准。
 * 曾经侧边栏先看 `runningSessionIds`,命中就弹一个只有「关闭」的提示;而主进程那边又按
 * 注册表里可能残留的条目拦一次。两处任何一处状态陈旧(run 中断了却没收尾、
 * 父 run 结束后还挂着的后台子代理……),删除就永远失败,只有重启能解。
 * 现在一律先请求删除:主进程认定真有 Agent 在跑才抛 conflict,这里据此弹
 * 「停止并删除 / 取消」;用户确认后带 `stopRunning` 再来一次。
 */
import { useState, type ReactNode } from 'react'
import { Dialog } from '../components/ui/Dialog'
import { Button } from '../components/arc/button/button'
import { isSessionDeleteRunningError } from '../services/sessions'
import { toast } from '../stores/toast'
import type { Translate } from '../i18n'

export type DeleteSessionFn = (sessionId: string, options?: { stopRunning?: boolean }) => Promise<void>

/**
 * 发起一次删除。返回 `'running'` 表示被运行中的 Agent 挡下、需要用户确认;
 * 其他失败已经 toast 过,再原样抛出,调用方的 `run()` 照旧收尾菜单。
 */
export async function requestSessionDelete(
  onDeleteSession: DeleteSessionFn,
  sessionId: string
): Promise<'deleted' | 'running'> {
  try {
    await onDeleteSession(sessionId)
    return 'deleted'
  } catch (error) {
    if (isSessionDeleteRunningError(error)) return 'running'
    toast.error(error instanceof Error ? error.message : String(error), 'session-delete')
    throw error
  }
}

export function useRunningSessionDelete(onDeleteSession: DeleteSessionFn, t: Translate): {
  /** 主进程说「在跑」时调用:弹出「停止并删除」确认框 */
  askStopAndDelete: (sessionId: string) => void
  dialog: ReactNode
} {
  const [pendingId, setPendingId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const close = (): void => {
    if (!busy) setPendingId(null)
  }
  const confirm = async (): Promise<void> => {
    if (pendingId === null) return
    setBusy(true)
    try {
      await onDeleteSession(pendingId, { stopRunning: true })
      setPendingId(null)
    } catch (error) {
      toast.error(error instanceof Error ? error.message : String(error), 'session-delete')
    } finally {
      setBusy(false)
    }
  }
  const dialog = (
    <Dialog
      open={pendingId !== null}
      onClose={close}
      title={t('session.deleteRunningTitle')}
      width={420}
      footer={
        <>
          <Button type="button" size="sm" variant="ghost" disabled={busy} onClick={close}>{t('common.cancel')}</Button>
          <Button type="button" size="sm" variant="danger" loading={busy} onClick={() => { void confirm() }}>
            {t('session.deleteRunningConfirm')}
          </Button>
        </>
      }
    >
      <p className="text-[12.5px] text-fg-muted">{t('session.deleteRunningMessage')}</p>
    </Dialog>
  )
  return { askStopAndDelete: setPendingId, dialog }
}
