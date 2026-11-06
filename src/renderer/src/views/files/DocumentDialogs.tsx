import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Button } from '../../components/arc/button/button'
import { Dialog } from '../../components/ui/Dialog'
import { useI18n } from '../../i18n'
import { quitConfirmed } from '../../services/app'
import { confirmDocumentChanges, isDocumentDirty, useDocumentsStore } from '../../stores/documents'

/** Kept at the shell root so inactive tabs and workspaces can also be saved. */
export function DocumentDialogs(): ReactNode {
  const { t } = useI18n()
  const confirmation = useDocumentsStore((s) => s.confirmation)
  const entries = useDocumentsStore((s) => s.entries)
  const [busy, setBusy] = useState(false)
  const allowUnload = useRef(false)
  const checkingUnload = useRef(false)
  const close = (proceed: boolean): void => {
    useDocumentsStore.setState({ confirmation: null })
    confirmation?.resolve(proceed)
  }

  useEffect(() => {
    const hasChanges = (): boolean => Object.values(useDocumentsStore.getState().entries).some((entry) => entry.saving || isDocumentDirty(entry))
    const confirmUnload = (reload: boolean): void => {
      if (checkingUnload.current) return
      checkingUnload.current = true
      // Chromium blocks synchronous dialogs inside beforeunload. Let the unload
      // cancel first, then use the same application dialog as tab closing.
      setTimeout(() => {
        void confirmDocumentChanges().then((proceed) => {
          checkingUnload.current = false
          if (!proceed) return
          allowUnload.current = true
          if (reload) window.location.reload()
          else {
            window.close()
            /*
              ★ 这次 unload 如果是**退出流程**引起的,上面那个 close() 已经让整次
              退出作废了(主进程侧的理由见 `main/quit-flow.ts` 文件头:此刻停服务、
              封库都还没做,作废是安全的)。用户刚刚选的就是「保存 / 丢弃」——
              那正是「退出」这个意图的延续,所以这里替他把退出重来一次;不重来的话
              点了退出只会发现应用还在,还得再点一次。

              不是退出流程时(用户自己关窗,而窗口的 close 被拦成隐藏)这条消息
              没有副作用:主进程那侧对应的是一轮空的退出请求。
            */
            quitConfirmed()
          }
        })
      }, 0)
    }
    const beforeUnload = (event: BeforeUnloadEvent): void => {
      if (allowUnload.current || !hasChanges()) return
      event.preventDefault()
      event.returnValue = false
      confirmUnload(false)
    }
    const beforeReload = (event: KeyboardEvent): void => {
      if (event.key !== 'F5' && !((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'r')) return
      if (!hasChanges()) return
      event.preventDefault()
      event.stopPropagation()
      confirmUnload(true)
    }
    window.addEventListener('beforeunload', beforeUnload)
    window.addEventListener('keydown', beforeReload, true)
    return () => {
      window.removeEventListener('beforeunload', beforeUnload)
      window.removeEventListener('keydown', beforeReload, true)
    }
  }, [])

  const saveAll = async (): Promise<void> => {
    if (!confirmation || busy) return
    setBusy(true)
    let success = true
    try {
      for (const key of confirmation.keys) {
        const entry = useDocumentsStore.getState().entries[key]
        if (entry && !(await useDocumentsStore.getState().save(entry.workspaceId, entry.path))) success = false
      }
      if (success && confirmation.keys.every((key) => {
        const entry = useDocumentsStore.getState().entries[key]
        return !entry || !isDocumentDirty(entry)
      })) close(true)
    } finally { setBusy(false) }
  }

  return <Dialog open={confirmation !== null} title={t('document.confirmTitle')} description={t('document.confirmHint')} onClose={() => { if (!busy) close(false) }} footer={<>
    <Button type="button" variant="secondary" disabled={busy} onClick={() => close(false)}>{t('common.cancel')}</Button>
    <Button type="button" variant="danger" disabled={busy} onClick={() => { if (confirmation) useDocumentsStore.getState().discard(confirmation.keys); close(true) }}>{t('document.discard')}</Button>
    <Button type="button" variant="primary" disabled={busy} onClick={() => { void saveAll() }}>{t(busy ? 'document.saving' : 'document.saveAll')}</Button>
  </>}>
    <ul className="space-y-2">{confirmation?.keys.map((key) => {
      const entry = entries[key]
      return entry ? <li key={key} className="text-[12px]"><div className="break-all font-mono text-fg">{entry.path}</div>{entry.error && <p role="alert" className="mt-1 text-danger">{t(entry.error)}</p>}</li> : null
    })}</ul>
  </Dialog>
}
