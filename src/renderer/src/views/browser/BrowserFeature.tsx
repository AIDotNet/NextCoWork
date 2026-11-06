import {
  ArrowLeft,
  CircleHelp,
  Download,
  Globe2,
  Lightbulb,
  ListRestart,
  MoreHorizontal,
  Plus,
  Sparkles,
  Trash2,
  Unplug,
  Upload
} from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import type { BrowserProfile } from '../../../../shared/domain/browser'
import { isLocalEnvironment } from '../../../../shared/domain/environment'
import { Button } from '../../components/arc/button/button'
import { Dialog } from '../../components/ui/Dialog'
import { IconButton } from '../../components/ui/IconButton'
import { Menu, MenuItem, MenuSeparator } from '../../components/ui/Menu'
import {
  clearBrowserProfileState,
  createBrowserProfile,
  deleteBrowserProfile,
  exportBrowserCookies,
  importBrowserCookies,
  listBrowserProfiles
} from '../../services/browser'
import { on } from '../../services/ipc'
import { useI18n } from '../../i18n'
import { useTabsStore } from '../../stores/tabs'
import { useWindowStore } from '../../stores/window'
import { cn } from '../../lib/cn'
import { FeatureFrame } from '../../shell/FeatureFrame'

export function BrowserFeature({ onClose }: { onClose?: () => void }): ReactNode {
  const { t } = useI18n()
  const workspaceId = useWindowStore((state) => state.activeWorkspaceId)
  const workspace = useWindowStore((state) => state.activeWorkspaceId ? state.workspaceTargets[state.activeWorkspaceId] : undefined)
  if (workspaceId !== null && (!workspace || !isLocalEnvironment(workspace.environment))) return <div className="flex min-h-0 flex-1 flex-col bg-canvas">
    <div className="flex h-12 shrink-0 items-center border-b border-border px-3"><IconButton label={t('common.close')} onClick={onClose}><ArrowLeft size={16} /></IconButton></div>
    <p role="status" className="m-auto p-6 text-[13px] text-fg-muted">{t('ssh.browserUnavailable')}</p>
  </div>
  return <LocalBrowserFeature onClose={onClose} />
}

function LocalBrowserFeature({ onClose }: { onClose?: () => void }): ReactNode {
  const { t } = useI18n()
  const [profiles, setProfiles] = useState<BrowserProfile[]>([])
  const [selectedId, setSelectedId] = useState<string>('default')
  const [dialogOpen, setDialogOpen] = useState(false)
  const [name, setName] = useState('')
  const [domains, setDomains] = useState('')
  const [startUrl, setStartUrl] = useState('')
  const [busy, setBusy] = useState(false)
  const [confirmingDeleteId, setConfirmingDeleteId] = useState<string | null>(null)
  const [formError, setFormError] = useState<string | null>(null)
  const [feedback, setFeedback] = useState<string | null>(null)
  const activeWorkspaceId = useWindowStore((state) => state.activeWorkspaceId)
  const activeOuterId = useWindowStore((state) => state.activeOuterId)
  const activateOuter = useWindowStore((state) => state.activate)
  const outer = useWindowStore((state) => state.outer)
  const openTab = useTabsStore((state) => state.open)

  const refresh = (): void => {
    void listBrowserProfiles()
      .then((items) => {
        setProfiles(items)
        setSelectedId((current) => (items.some((item) => item.id === current) ? current : (items[0]?.id ?? 'default')))
      })
      .catch(() => setProfiles([]))
  }

  useEffect(() => {
    refresh()
    return on('browser:profilesChanged', (items) => {
      setProfiles(items)
      setSelectedId((current) => (items.some((item) => item.id === current) ? current : (items[0]?.id ?? 'default')))
    })
  }, [])

  const selected = profiles.find((profile) => profile.id === selectedId) ?? profiles[0]

  const returnToWorkspace = (): void => {
    onClose?.()
    // 兼容热更新前已经存在内存里的旧式 browser feature Tab；冷启动时 store
    // 会迁移掉这类持久化记录，正常入口始终走上面的独立主内容模式。
    const workspaceTab = outer.find((tab) => tab.kind === 'workspace' && tab.ref.workspaceId === activeWorkspaceId)
    if (workspaceTab !== undefined && workspaceTab.id !== activeOuterId) {
      activateOuter(workspaceTab.id)
    }
  }

  const create = async (): Promise<void> => {
    if (name.trim() === '' || busy) return
    setBusy(true)
    setFormError(null)
    try {
      const profile = await createBrowserProfile({
        name,
        domains: domains
          .split(',')
          .map((value) => value.trim())
          .filter(Boolean),
        ...(startUrl.trim() === '' ? {} : { startUrl: startUrl.trim() })
      })
      setProfiles((current) => [...current.filter((item) => item.id !== profile.id), profile])
      setSelectedId(profile.id)
      setDialogOpen(false)
      setName('')
      setDomains('')
      setStartUrl('')
    } catch {
      setFormError(t('browser.operationFailed'))
    } finally {
      setBusy(false)
    }
  }

  const openBrowser = (profile: BrowserProfile | undefined = selected): void => {
    if (activeWorkspaceId === null) return
    returnToWorkspace()
    openTab(activeWorkspaceId, 'browser', 'main', {
      title: profile?.isDefault ? t('browser.defaultProfile') : (profile?.name ?? t('browser.defaultProfile')),
      url: profile?.startUrl ?? '',
      profileId: profile?.id
    })
  }

  const exportCookies = async (profile: BrowserProfile): Promise<void> => {
    if (activeWorkspaceId === null || busy) return
    setBusy(true)
    setFeedback(null)
    try {
      const saved = await exportBrowserCookies(activeWorkspaceId, profile.id)
      if (saved) setFeedback(t('browser.cookieExported'))
    } catch {
      setFeedback(t('browser.operationFailed'))
    } finally {
      setBusy(false)
    }
  }

  const importCookies = async (profile: BrowserProfile): Promise<void> => {
    if (activeWorkspaceId === null || busy) return
    setBusy(true)
    setFeedback(null)
    try {
      const imported = await importBrowserCookies(activeWorkspaceId, profile.id)
      if (imported !== null) setFeedback(t('browser.cookieImported', { count: imported }))
    } catch {
      setFeedback(t('browser.operationFailed'))
    } finally {
      setBusy(false)
    }
  }

  const clearState = async (profile: BrowserProfile): Promise<void> => {
    if (activeWorkspaceId === null || profile.isDefault || busy) return
    setBusy(true)
    setFeedback(null)
    try {
      await clearBrowserProfileState(activeWorkspaceId, profile.id)
      setFeedback(t('browser.stateCleared'))
    } catch {
      setFeedback(t('browser.operationFailed'))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (profile: BrowserProfile): Promise<void> => {
    if (profile.isDefault || busy) return
    setBusy(true)
    try {
      await deleteBrowserProfile(profile.id)
      setProfiles((current) => current.filter((item) => item.id !== profile.id))
      setSelectedId((current) => (current === profile.id ? 'default' : current))
      setConfirmingDeleteId(null)
    } catch {
      setFeedback(t('browser.operationFailed'))
    } finally {
      setBusy(false)
    }
  }

  return (
    /*
      这条 52px 是浏览器管理页**自己的**标题栏 —— 它整体替换掉外层那条 34px
      (AppShell 里 activeStandaloneFeature === 'browser' 的分支)。所以右端同样
      要给自绘的三颗窗口按钮让位:它们是 fixed 悬浮层,在这一页照样浮在右上角
      (比这条的垂直中线高 9px,已知取舍,见 shell/WindowControls.tsx)。
      让位与拖动区归 `shell/FeatureFrame` 统一管,原委见那个文件的头。
    */
    <FeatureFrame
      header={
        <>
          <IconButton
            label={t('browser.back')}
            size={28}
            width={40}
            onClick={returnToWorkspace}
            className="rounded-pill bg-tint"
          >
            <ArrowLeft size={15} />
          </IconButton>
          <h1 className="text-[14px] font-medium text-fg">{t('browser.title')}</h1>
        </>
      }
    >

      <div className="flex min-h-0 flex-1">
        <aside className="flex w-[240px] shrink-0 flex-col border-r border-hairline bg-surface/60 px-2 py-3">
          <div className="px-2">
            <div className="flex items-center gap-1 text-[13px] text-fg">
              {t('browser.profiles')} <span className="text-[11px] text-fg-faint">{profiles.length}</span>
            </div>
            <p className="mt-1 text-[11px] leading-4 text-fg-faint">{t('browser.profileHint')}</p>
          </div>

          <Button variant="secondary" type="button" size="sm" onClick={() => setDialogOpen(true)} className="mt-3 w-full">
            <Plus size={14} />
            {t('browser.newProfile')}
          </Button>

          <div className="mt-3 space-y-1 overflow-y-auto">
            {profiles.map((profile) => {
              const isSelected = profile.id === selected?.id
              const displayName = profile.isDefault ? t('browser.defaultProfile') : profile.name
              return (
                <div
                  role="tab"
                  tabIndex={0}
                  key={profile.id}
                  onClick={() => setSelectedId(profile.id)}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' || event.key === ' ') setSelectedId(profile.id)
                  }}
                  className={cn(
                    'group relative flex w-full items-center gap-2 rounded-[10px] px-2.5 py-2 text-left transition-colors',
                    isSelected ? 'bg-tint-strong' : 'hover:bg-tint-hover'
                  )}
                >
                  <span className="flex size-7 shrink-0 items-center justify-center rounded-[8px] bg-tint text-fg-muted">
                    <Globe2 size={14} />
                  </span>
                  <span className="min-w-0 flex-1">
                    <span className="flex items-center gap-1 truncate text-[12px] text-fg">
                      {displayName}
                      {profile.isDefault && (
                        <span className="rounded bg-tint px-1 text-[10px] text-fg-faint">{t('common.default')}</span>
                      )}
                    </span>
                    <span className="mt-0.5 block truncate text-[11px] text-fg-faint">
                      {profile.isDefault ? t('browser.defaultProfileHint') : t('browser.profileJustNow')}
                    </span>
                  </span>
                  {isSelected && (
                    <span className="shrink-0 rounded-[6px] bg-accent/10 px-1.5 py-1 text-[10px] text-accent transition-opacity group-hover:opacity-0">
                      {t('browser.profileActive')}
                    </span>
                  )}
                  <Menu
                    label={`${displayName} ${t('browser.open')}`}
                    width={180}
                    align="end"
                    trigger={<MoreHorizontal size={14} />}
                    onOpenChange={(open) => {
                      if (!open) setConfirmingDeleteId(null)
                    }}
                    className={isSelected ? 'absolute right-2' : undefined}
                    triggerClassName="flex size-6 shrink-0 items-center justify-center rounded-[6px] text-icon opacity-0 group-hover:opacity-100 hover:bg-tint-hover hover:text-fg"
                  >
                    {(close) => (
                      <>
                        <MenuItem
                          icon={<Globe2 size={14} />}
                          onSelect={() => {
                            openBrowser(profile)
                            close()
                          }}
                        >
                          {t('browser.open')}
                        </MenuItem>
                        <MenuSeparator />
                        <MenuItem
                          icon={<Download size={14} />}
                          disabled={busy}
                          onSelect={() => {
                            void exportCookies(profile)
                            close()
                          }}
                        >
                          {t('browser.exportCookie')}
                        </MenuItem>
                        <MenuItem
                          icon={<Upload size={14} />}
                          disabled={busy}
                          onSelect={() => {
                            void importCookies(profile)
                            close()
                          }}
                        >
                          {t('browser.importCookie')}
                        </MenuItem>
                        {!profile.isDefault && (
                          <>
                            <MenuSeparator />
                            <MenuItem
                              icon={<ListRestart size={14} />}
                              disabled={busy}
                              onSelect={() => {
                                void clearState(profile)
                                close()
                              }}
                            >
                              {t('browser.clearState')}
                            </MenuItem>
                            <MenuSeparator />
                            <MenuItem
                              danger
                              icon={<Trash2 size={14} />}
                              onSelect={() => {
                                if (confirmingDeleteId === profile.id) {
                                  void remove(profile)
                                  close()
                                } else {
                                  setConfirmingDeleteId(profile.id)
                                }
                              }}
                            >
                              {confirmingDeleteId === profile.id
                                ? t('common.confirmDelete')
                                : t('browser.deleteProfile')}
                            </MenuItem>
                          </>
                        )}
                      </>
                    )}
                  </Menu>
                </div>
              )
            })}
          </div>
        </aside>

        <main className="min-w-0 flex-1 overflow-y-auto px-4 py-3">
          {feedback !== null && (
            <div className="mb-3 rounded-[8px] bg-tint px-3 py-2 text-[12px] text-fg-muted">{feedback}</div>
          )}
          <div className="flex items-center justify-between">
            <h2 className="text-[14px] font-medium text-fg">{t('browser.automationTitle')}</h2>
            <Button variant="secondary" type="button" size="sm" onClick={() => openBrowser()}>
              <Plus size={14} />
              {t('browser.manualCreate')}
            </Button>
          </div>

          <section className="mt-3 rounded-[18px] border border-hairline bg-surface px-4 py-4">
            <div className="flex items-center gap-3">
              <span className="flex size-8 items-center justify-center rounded-pill bg-tint">
                <Lightbulb size={16} className="text-fg-muted" />
              </span>
              <div className="min-w-0 flex-1">
                <div className="text-[13px] text-fg">{t('browser.switchChat')}</div>
                <p className="mt-0.5 text-[11px] text-fg-faint">{t('browser.automationHint')}</p>
              </div>
              <Button variant="secondary" type="button" size="sm" onClick={() => openBrowser()}>
                {t('browser.open')}
              </Button>
            </div>
          </section>

          <section className="mt-3 rounded-[18px] border border-hairline bg-surface px-4 py-4">
            <div className="flex items-center gap-3">
              <span className="flex size-8 items-center justify-center rounded-pill bg-tint">
                <Globe2 size={16} className="text-fg-muted" />
              </span>
              <div className="min-w-0 flex-1">
                <div className="text-[13px] text-fg">{t('browser.extension')}</div>
                <p className="mt-0.5 text-[11px] text-fg-faint">{t('browser.extensionHint')}</p>
              </div>
              <span className="rounded-pill bg-tint px-2 py-1 text-[11px] text-fg-faint">
                {t('browser.notConnected')}
              </span>
              <Button type="button" size="sm" variant="secondary" disabled>
                <Unplug size={13} />
                {t('browser.connect')}
              </Button>
            </div>
          </section>

          <div className="flex items-center justify-center gap-1.5 py-10 text-[12px] text-fg-faint">
            <Sparkles size={13} />
            <span>{t('browser.automationHint')}</span>
            <CircleHelp size={13} />
          </div>
        </main>
      </div>

      <Dialog
        open={dialogOpen}
        onClose={() => setDialogOpen(false)}
        title={t('browser.createProfileTitle')}
        description={t('browser.createProfileHint')}
        width={420}
        footer={
          <>
            <Button variant="secondary" type="button" size="sm" onClick={() => setDialogOpen(false)}>
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              size="sm"
              variant="primary"
              disabled={name.trim() === '' || busy}
              onClick={() => {
                void create()
              }}
            >
              {t('browser.create')}
            </Button>
          </>
        }
      >
        <div className="space-y-3">
          {formError !== null && (
            <div className="rounded-[8px] bg-danger/10 px-2.5 py-2 text-[12px] text-danger">{formError}</div>
          )}
          <label className="block text-[12px] text-fg-muted">
            {t('browser.profileName')}
            <input
              autoFocus
              value={name}
              onChange={(event) => setName(event.target.value)}
              placeholder={t('browser.profileNamePlaceholder')}
              className="apple-field mt-1 h-9 w-full rounded-[8px] border border-hairline bg-canvas px-2.5 text-[12px] text-fg outline-none focus:border-fg-faint"
            />
          </label>
          <label className="block text-[12px] text-fg-muted">
            {t('browser.profileDomains')}
            <input
              value={domains}
              onChange={(event) => setDomains(event.target.value)}
              placeholder={t('browser.profileDomainsPlaceholder')}
              className="apple-field mt-1 h-9 w-full rounded-[8px] border border-hairline bg-canvas px-2.5 text-[12px] text-fg outline-none focus:border-fg-faint"
            />
          </label>
          <label className="block text-[12px] text-fg-muted">
            {t('browser.profileStartUrl')}
            <input
              value={startUrl}
              onChange={(event) => setStartUrl(event.target.value)}
              placeholder={t('browser.profileStartUrlPlaceholder')}
              className="apple-field mt-1 h-9 w-full rounded-[8px] border border-hairline bg-canvas px-2.5 text-[12px] text-fg outline-none focus:border-fg-faint"
            />
          </label>
        </div>
      </Dialog>
    </FeatureFrame>
  )
}
