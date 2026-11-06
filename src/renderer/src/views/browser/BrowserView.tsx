import { ArrowLeft, ArrowRight, ExternalLink, Globe, Keyboard, MousePointer2, RotateCw, ShieldAlert } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { InnerTab } from '../../../../shared/domain/tab'
import type { Workspace } from '../../../../shared/domain/workspace'
import { isLocalEnvironment } from '../../../../shared/domain/environment'
import { browserPartition, type BrowserCuaEvent } from '../../../../shared/domain/browser'
import {
  bindBrowserView,
  closeBrowserTab,
  listBrowserTabs,
  navigateBrowserTab,
  onBrowserCua,
  openBrowserTab
} from '../../services/browser'
import { useI18n } from '../../i18n'
import { useTabsStore } from '../../stores/tabs'
import { IconButton } from '../../components/ui/IconButton'
import { cn } from '../../lib/cn'
import { Spinner } from '../../components/ui/Spinner'
import { isAddressableUrl, isExternalOpenableUrl, resolveAddress } from './address'

interface BrowserElement extends HTMLElement {
  loadURL?: (url: string) => Promise<void>
  goBack?: () => void
  goForward?: () => void
  reload?: () => void
  openDevTools?: () => void
  getWebContentsId?: () => number
}

interface CuaIndicator extends BrowserCuaEvent {
  visible: boolean
}

/**
 * 隔离的浏览器标签视图。
 *
 * 每个工作区使用独立的 Electron session partition；页面只能通过 webview 加载
 * http(s) 与 file:// 地址（原先是「永远只能 http(s)」，2026-09-22 按工作区所有者
 * 要求放开本地页面 —— 需求与风险见 `main/kernel/tool/builtin/ssrf.ts` 的
 * `allowFileUrls`，协议闸在 `main/index.ts` 的 `browserUrlProtocol`），
 * 不能导航到应用的 ncw:// 或脚本协议。
 */
export function BrowserView({ tab, workspace }: { tab: Extract<InnerTab, { kind: 'browser' }>; workspace: Workspace }): ReactNode {
  const { t } = useI18n()
  if (!isLocalEnvironment(workspace.environment)) return <div role="status" className="flex min-h-0 flex-1 items-center justify-center p-6 text-[13px] text-fg-muted">{t('ssh.browserUnavailable')}</div>
  return <LocalBrowserView tab={tab} workspace={workspace} />
}

function LocalBrowserView({ tab, workspace }: { tab: Extract<InnerTab, { kind: 'browser' }>; workspace: Workspace }): ReactNode {
  const { t } = useI18n()
  const setBrowser = useTabsStore((state) => state.setBrowser)
  const [url, setUrl] = useState(tab.ref.url)
  const [draft, setDraft] = useState(tab.ref.url)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [cuaIndicator, setCuaIndicator] = useState<CuaIndicator | null>(null)
  const viewRef = useRef<BrowserElement | null>(null)

  useEffect(() => {
    setUrl(tab.ref.url)
    setDraft(tab.ref.url)
    setError(null)
  }, [tab.id, tab.ref.url])

  useEffect(() => {
    if (tab.ref.url === '') return
    let alive = true
    void listBrowserTabs(workspace.id).then((tabs) => {
      const known = tabs.find(
        (item) => item.id === tab.ref.browserId || (item.source === 'user' && item.clientTabId === tab.id)
      )
      if (known !== undefined) return known
      return openBrowserTab(workspace.id, tab.ref.url, tab.title, tab.ref.profileId, tab.id)
    }).then((remote) => {
      if (!alive) return
      setBrowser(workspace.id, tab.id, { browserId: remote.id, url: remote.url })
    }).catch(() => {
      if (alive) setError(t('browser.operationFailed'))
    })
    return () => { alive = false }
  }, [setBrowser, tab.id, tab.ref.browserId, tab.ref.profileId, tab.ref.url, tab.title, t, workspace.id])

  useEffect(() => {
    const view = viewRef.current
    if (view === null) return
    let alive = true

    const onAttach = (): void => {
      const browserId = tab.ref.browserId
      let webContentsId: number | undefined
      try {
        webContentsId = view.getWebContentsId?.()
      } catch {
        // Before did-attach Electron throws instead of returning an absent id.
        return
      }
      if (browserId === undefined || typeof webContentsId !== 'number' || !Number.isInteger(webContentsId) || webContentsId <= 0) return
      void bindBrowserView(workspace.id, browserId, webContentsId).catch(() => {
        if (alive) setError(t('browser.operationFailed'))
      })
    }
    const onNavigate = (event: Event): void => {
      const next = (event as Event & { url?: string }).url
      if (typeof next !== 'string' || !isAddressableUrl(next)) return
      setUrl(next)
      setDraft(next)
      setBrowser(workspace.id, tab.id, { url: next })
      // Keep the shared manager authoritative when a page navigates itself
      // (link click, history API, or redirect) instead of only updating the
      // optimistic renderer tab. This is what lets another window and Agent
      // tools observe the same URL without waiting for the address bar form.
      if (tab.ref.browserId !== undefined && tab.ref.browserId !== '') {
        void navigateBrowserTab(workspace.id, tab.ref.browserId, next).catch(() => undefined)
      }
      setLoading(false)
    }
    const onStart = (): void => setLoading(true)
    const onStop = (): void => setLoading(false)
    const onFail = (): void => {
      setLoading(false)
      setError(t('browser.loadFailed'))
    }

    view.addEventListener('did-attach', onAttach)
    view.addEventListener('did-navigate', onNavigate)
    view.addEventListener('did-navigate-in-page', onNavigate)
    view.addEventListener('did-start-loading', onStart)
    view.addEventListener('did-stop-loading', onStop)
    view.addEventListener('did-fail-load', onFail)
    onAttach()
    return () => {
      alive = false
      view.removeEventListener('did-attach', onAttach)
      view.removeEventListener('did-navigate', onNavigate)
      view.removeEventListener('did-navigate-in-page', onNavigate)
      view.removeEventListener('did-start-loading', onStart)
      view.removeEventListener('did-stop-loading', onStop)
      view.removeEventListener('did-fail-load', onFail)
    }
  }, [setBrowser, tab.id, tab.ref.browserId, workspace.id, t])

  useEffect(() => {
    let hideTimer: ReturnType<typeof setTimeout> | null = null
    let removeTimer: ReturnType<typeof setTimeout> | null = null
    const unsubscribe = onBrowserCua((event) => {
      if (event.workspaceId !== workspace.id || event.tabId !== tab.ref.browserId) return
      if (hideTimer !== null) clearTimeout(hideTimer)
      if (removeTimer !== null) clearTimeout(removeTimer)
      setCuaIndicator({ ...event, visible: true })
      hideTimer = setTimeout(() => {
        setCuaIndicator((current) => current === null ? null : { ...current, visible: false })
      }, 200)
      removeTimer = setTimeout(() => setCuaIndicator(null), 400)
    })
    return () => {
      unsubscribe()
      if (hideTimer !== null) clearTimeout(hideTimer)
      if (removeTimer !== null) clearTimeout(removeTimer)
    }
  }, [tab.ref.browserId, workspace.id])

  const navigate = async (): Promise<void> => {
    const raw = draft.trim()
    if (raw === '') return
    /*
      需求：本地页面也能从地址栏打开（file://，2026-09-22 —— 见 address.ts 头注）。
      解析失败/协议不认直接给 invalidUrl，不再混进下面的通用 catch —— 那条路会把
      「地址填错了」显示成「操作失败」，用户会以为是浏览器坏了而不是自己拼错了。
    */
    const href = resolveAddress(raw)
    if (href === null) {
      setError(t('browser.invalidUrl'))
      return
    }
    setError(null)
    setLoading(true)
    try {
      if (tab.ref.browserId === undefined) {
        const remote = await openBrowserTab(workspace.id, href, tab.title, tab.ref.profileId, tab.id)
        setBrowser(workspace.id, tab.id, { browserId: remote.id, url: remote.url })
      } else {
        await navigateBrowserTab(workspace.id, tab.ref.browserId, href)
        setBrowser(workspace.id, tab.id, { url: href })
      }
      setUrl(href)
      setDraft(href)
      await viewRef.current?.loadURL?.(href)
    } catch {
      setLoading(false)
      setError(t('browser.operationFailed'))
    }
  }

  const openExternal = (): void => {
    if (!isExternalOpenableUrl(url)) return
    window.open(url, '_blank', 'noopener,noreferrer')
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col bg-canvas">
      <div className="flex h-11 shrink-0 items-center gap-1 border-b border-hairline px-2">
        <IconButton label={t('browser.back')} size={26} onClick={() => viewRef.current?.goBack?.()}>
          <ArrowLeft size={14} />
        </IconButton>
        <IconButton label={t('browser.forward')} size={26} onClick={() => viewRef.current?.goForward?.()}>
          <ArrowRight size={14} />
        </IconButton>
        <IconButton label={t('browser.reload')} size={26} onClick={() => viewRef.current?.reload?.()}>
          <RotateCw size={14} className={cn(loading && 'animate-spin motion-reduce:animate-none')} />
        </IconButton>
        <form
          className="flex min-w-0 flex-1 items-center rounded-[8px] border border-hairline bg-surface px-2"
          onSubmit={(event) => {
            event.preventDefault()
            void navigate()
          }}
        >
          <Globe size={13} className="mr-1.5 shrink-0 text-fg-faint" />
          <input
            value={draft}
            onChange={(event) => setDraft(event.target.value)}
            placeholder={t('browser.addressPlaceholder')}
            aria-label={t('browser.address')}
            spellCheck={false}
            className="h-7 min-w-0 flex-1 bg-transparent text-[12px] text-fg outline-none placeholder:text-fg-faint"
          />
          <ShieldAlert size={13} className="shrink-0 text-fg-faint" aria-label={t('browser.isolated')} />
        </form>
        <IconButton label={t('browser.openExternal')} size={26} onClick={openExternal}>
          <ExternalLink size={14} />
        </IconButton>
      </div>

      {error !== null && (
        <div className="flex shrink-0 items-center gap-2 border-b border-hairline bg-danger/8 px-3 py-2 text-[12px] text-danger">
          <ShieldAlert size={14} />
          <span className="min-w-0 flex-1 truncate">{error}</span>
        </div>
      )}

      <div className="relative min-h-0 flex-1 overflow-hidden bg-white">
        {url === '' ? (
          <div className="flex h-full flex-col items-center justify-center gap-2 bg-canvas text-fg-faint">
            <Globe size={34} strokeWidth={1.4} />
            <p className="text-[13px]">{t('browser.emptyTitle')}</p>
            <p className="text-[12px]">{t('browser.emptyHint')}</p>
          </div>
        ) : (
          /* React has no built-in webview type; Electron upgrades this custom element. */
          <webview
            ref={(node) => {
              viewRef.current = node as BrowserElement | null
            }}
            src={url}
            partition={browserPartition(workspace.id, tab.ref.profileId)}
            allowpopups={false}
            webpreferences="contextIsolation=yes,nodeIntegration=no,sandbox=yes"
            className="h-full w-full border-0"
          />
        )}
        {cuaIndicator !== null && (
          <div
            aria-hidden="true"
            className={cn(
              'pointer-events-none absolute z-20 text-accent transition-opacity duration-200 motion-reduce:transition-none',
              cuaIndicator.visible ? 'opacity-100' : 'opacity-0'
            )}
            style={{ left: cuaIndicator.x, top: cuaIndicator.y }}
          >
            <MousePointer2 size={22} className="-translate-x-[2px] -translate-y-[2px] fill-canvas drop-shadow-sm" />
            {cuaIndicator.kind === 'click' && (
              <span className="absolute left-0 top-0 h-5 w-5 -translate-x-1/2 -translate-y-1/2 animate-ping rounded-full border border-accent motion-reduce:animate-none" />
            )}
            {cuaIndicator.kind === 'type' && (
              <span className="absolute left-4 top-4 rounded border border-accent/40 bg-canvas/90 p-1 shadow-sm">
                <Keyboard size={12} />
              </span>
            )}
          </div>
        )}
        {loading && (
          <div className="pointer-events-none absolute right-3 top-3 rounded-pill bg-canvas/90 px-2 py-1 text-[11px] text-fg-muted shadow-sm">
            <Spinner size="xs" className="mr-1 inline" />
            {t('browser.loading')}
          </div>
        )}
      </div>
    </div>
  )
}

/** Called by tab cleanup when an Agent-owned tab is closed from the UI. */
export async function closeManagedBrowserTab(workspaceId: string, tab: Extract<InnerTab, { kind: 'browser' }>): Promise<void> {
  if (tab.ref.browserId !== undefined) await closeBrowserTab(workspaceId, tab.ref.browserId)
}
