/**
 * 设置模态浮层 —— 步骤 14。
 *
 * ★ **不 portal,根节点必须 `app-no-drag`。** 它盖住顶部那条 34px 自绘标题栏,
 * 而那块是 `-webkit-app-region: drag`,OS 会吞掉该区域里所有 pointer 事件 ——
 * 不加的话表现是「浮层上半部分点不动,一按住整个窗口跟着鼠标跑」。
 * 同一条理由在 `Menu.tsx` 文件头写过一遍(那是「不用 radix」的原因),
 * 全窗浮层比菜单更严重,因为它必然压在标题栏上。
 *
 * ★ **z-100 而不是 z-50。** 见 theme.css 末尾那段 z 轴约定:50 是面板内部的
 * 下拉菜单,100 是模态。浮层自己建立层叠上下文,所以它**内部**的 z-50 菜单
 * (模型选择器)天然在自己的遮罩之上,不需要把 Menu 提上去。
 *
 * ★ **点遮罩关闭绑 `click` 不绑 `pointerdown`。** 端口框/代理框是草稿态、
 * 靠失焦提交:pointerdown 会在 blur 之前就把面板卸掉,用户刚打的端口号丢了。
 *
 * 几何量自 docs/images/06cd7b3c(窗口 1146×920):
 *   浮层 x43..1101 / y100..819 → 1058×720;左导航 192 宽;内容内边距 24;
 *   导航行高 34、步进 38;搜索框 30 高;完成按钮 34 高、贴右下各 24。
 * 宽是**上限**不是定值 —— 用户给的浅色截图窗口只有 878 宽,浮层量到 858。
 */
import { Search, X } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { Bootstrap } from '../../../shared/domain/bootstrap'
import type { ClientAuthState } from '../../../shared/domain/client-auth'
import type { AppSettings, AppSettingsPatch } from '../../../shared/domain/settings'
import { Button } from '../components/arc/button/button'
import { IconButton } from '../components/ui/IconButton'
import SegmentedControl from '../components/arc/segmented-control/segmented-control'
import { TextInput } from '../components/ui/TextInput'
import { useFocusTrap } from '../components/ui/useFocusTrap'
import { prettyAccelerator } from '../lib/accelerator'
import { cn } from '../lib/cn'
import { usePresence } from '../lib/usePresence'
import { updateSettings } from '../services/app'
import { SETTINGS_ICON } from './icons'
import {
  matchPages,
  matchRows,
  resolveSettingsPage,
  SETTINGS_PAGES,
  visibleSettingsPages,
  type SettingsPageId,
  type SettingsRow
} from './nav'
import { AboutPage } from './pages/AboutPage'
import { ConnectionPage } from './pages/ConnectionPage'
import { DataPage } from './pages/DataPage'
import { ExperimentalPage } from './pages/ExperimentalPage'
import { DevicesPage } from './pages/DevicesPage'
import { ImportPage } from './pages/import/ImportPage'
import { GeneralPage } from './pages/GeneralPage'
import { ModelPage } from './pages/model/ModelPage'
import { PreferencePage } from './pages/PreferencePage'
import { UsagePage } from './pages/usage/UsagePage'
import { StubPage } from './pages/StubPage'
import { useI18n, type Translate } from '../i18n'
import { AccountPage } from './pages/AccountPage'
import { WalletPage } from './pages/wallet/WalletPage'
import { Dialog } from '../components/ui/Dialog'
import { ErrorBoundary } from '../components/ui/ErrorBoundary'
import { themeDraftDirty, useThemeProfiles } from '../stores/themeProfiles'


export function SettingsOverlay({
  open,
  page,
  settings,
  versions,
  auth,
  onNavigate,
  onClose
}: {
  open: boolean
  page: SettingsPageId
  settings: AppSettings
  versions: Bootstrap['versions']
  /** App 级登录态：决定钱包页是否出现（`requiresSignIn`），也是钱包页余额的唯一真源 */
  auth: ClientAuthState
  onNavigate: (p: SettingsPageId) => void
  onClose: () => void
}): ReactNode {
  const presence = usePresence(open, 280)
  const panelRef = useRef<HTMLDivElement>(null)
  const searchRef = useRef<HTMLInputElement>(null)
  const signedIn = auth.mode === 'authenticated'
  const lastOpenPage = useRef<SettingsPageId>(page)
  if (open) lastOpenPage.current = page
  // AppShell keeps this component mounted so the close transition can finish.
  // Keep the last real page visible while the parent store is switching back to
  // its default value after close, avoiding a one-frame flash of General.
  // 需求：未登录时请求到的钱包页改落账户页（见 nav.ts 的 `resolveSettingsPage`）。
  const visiblePage = resolveSettingsPage(open ? page : lastOpenPage.current, signedIn)
  const [query, setQuery] = useState('')
  const [sub, setSub] = useState<string>('')
  const [seenPage, setSeenPage] = useState(page)
  const { t } = useI18n()
  const [confirmClose, setConfirmClose] = useState(false)
  const discardThemeDraft = useThemeProfiles((state) => state.discard)
  const requestClose = (): void => {
    if (visiblePage === 'preference' && themeDraftDirty()) setConfirmClose(true)
    else onClose()
  }

  const def = SETTINGS_PAGES.find((p) => p.id === visiblePage)
  const subs = def?.subs

  // 换页时把子 Tab 重置到该页的第一个 —— 渲染期改状态,不用 useEffect:
  // 后者会先渲染一帧「连接页 + 上一页残留的子 Tab」再纠正,看得见地闪一下
  if (visiblePage !== seenPage) {
    setSeenPage(visiblePage)
    setSub(subs?.[0]?.id ?? '')
  }
  const availableSubs = subs === undefined
    ? undefined
    : [...subs.map((item) => ({ ...item, label: subLabel(t, item.id) })), ...(visiblePage === 'model' ? [{ id: 'management', label: t('settings.sub.management') }] : [])]
  const activeSub = availableSubs === undefined ? '' : availableSubs.some((s) => s.id === sub) ? sub : availableSubs[0]!.id

  useFocusTrap(panelRef, open && presence.mounted, searchRef)

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent): void => {
      // ★ 先让下层消费者说话:Menu 关自己时会 preventDefault。不查这个的话
      // 「在设置里打开模型选择器再按 Esc」会同时关掉菜单和整个面板。
      if (e.key !== 'Escape' || e.defaultPrevented) return
      e.preventDefault()
      onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [open, onClose])

  const patch = (p: AppSettingsPatch): void => {
    // 失败只记日志:值已经在界面上生效了(main 广播回来才是最终态),
    // 为一次写回失败弹个错打断用户不值当 —— 同 `Composer.tsx` 的先例
    void updateSettings(p).catch((e: unknown) => console.error('设置写入失败', e))
  }

  const rows = matchRows(query)
  const pages = matchPages(query, signedIn)
  const searching = query.trim() !== ''

  const goto = (target: SettingsPageId, targetSub?: string): void => {
    onNavigate(target)
    setSeenPage(target)
    const d = SETTINGS_PAGES.find((p) => p.id === target)
    setSub(targetSub ?? d?.subs?.[0]?.id ?? '')
    setQuery('')
  }

  if (!presence.mounted) return null

  return (
    <div
      className={cn(
        'app-no-drag fixed inset-0 z-100 flex items-center justify-center p-[10px]',
        'transition-opacity duration-280 ease-panel motion-reduce:transition-none',
        presence.shown ? 'opacity-100' : 'pointer-events-none opacity-0'
      )}
      role="dialog"
      aria-modal="true"
      aria-label={t('common.settings')}
    >
      {/* 苹果风格:轻遮罩、不模糊 —— 面板自己是毛玻璃,背景糊掉反而看不出玻璃感 */}
      <div className="absolute inset-0 bg-scrim/20" onClick={requestClose} />

      <div
        ref={panelRef}
        tabIndex={-1}
        className={cn(
          'relative flex h-[720px] max-h-full w-full max-w-[1058px] overflow-hidden',
          'apple-sheet outline-none',
          // Menu uses viewport coordinates while remaining inside this panel;
          // keep the ancestor transform-free so its fixed positioning stays exact.
          'transition-opacity duration-280 ease-panel motion-reduce:transition-none',
          presence.shown ? 'opacity-100' : 'opacity-0'
        )}
      >
        {/* ── 左:导航 ── */}
        <nav className="flex w-[192px] shrink-0 flex-col bg-surface">
          <div className="flex h-[44px] shrink-0 items-center px-3">
            <span className="flex-1 text-[13px] text-fg">{t('common.settings')}</span>
            <kbd className="font-sans text-[11px] text-fg-faint">
              {prettyAccelerator(settings.shortcuts.openSettings)}
            </kbd>
          </div>

          <div className="px-2 pb-2">
            <TextInput
              value={query}
              onChange={setQuery}
              placeholder={t('settings.search')}
              ariaLabel={t('settings.searchLabel')}
              icon={<Search size={13} />}
              inputRef={searchRef}
              className="h-[30px]"
            />
          </div>

          <ul className="scroll-thin flex min-h-0 flex-1 flex-col gap-1 overflow-y-auto px-2 pb-2">
            {visibleSettingsPages(signedIn).map((p) => {
              const Icon = SETTINGS_ICON[p.id]
              const on = p.id === visiblePage && !searching
              return (
                <li key={p.id}>
                  <button
                    type="button"
                    aria-current={on ? 'page' : undefined}
                    onClick={() => goto(p.id)}
                    className={cn(
                      'flex h-[34px] w-full items-center gap-2.5 rounded-[8px] px-2.5',
                      'text-left text-[13px] transition-colors',
                      // 苹果风格:选中 = 强调色实底白字(系统设置的侧栏),悬停只淡淡一层
                      on ? 'bg-accent text-accent-fg' : 'text-fg hover:bg-fg/6'
                    )}
                  >
                    <span className={cn('shrink-0', on ? 'text-accent-fg' : 'text-icon')}>
                      <Icon size={15} />
                    </span>
                    <span className="min-w-0 flex-1 truncate">{pageLabel(t, p.id)}</span>
                  </button>
                </li>
              )
            })}
          </ul>
        </nav>

        {/* ── 右:内容 ── */}
        {/*
          ★ 菜单的夹取边界(见 `menu-position.ts` 的 `MenuBounds`)。标在**内容列**上,
          不是标在整个浮层上:导航就在浮层里,夹到浮层等于允许面板盖住导航,
          而那正是要修的现象。
        */}
        <div data-menu-bounds className="flex min-w-0 flex-1 flex-col bg-canvas">
          <header className="flex shrink-0 items-center gap-4 px-6 pt-5 pb-3">
            <h2 className="text-[15px] text-fg">{searching ? t('settings.searchResults') : pageLabel(t, visiblePage)}</h2>
            {!searching && availableSubs !== undefined && (
              <SegmentedControl
                label={`${pageLabel(t, visiblePage)}${t('settings.categorySuffix')}`}
                value={activeSub}
                options={(availableSubs ?? []).map((s) => ({ value: s.id, label: s.label }))}
                onValueChange={setSub}
              />
            )}
            <span className="flex-1" />
            <IconButton label={t('accessibility.closeSettings')} onClick={requestClose}>
              <X size={15} />
            </IconButton>
          </header>

          <div className="scroll-thin fade-bottom flex min-h-0 flex-1 flex-col overflow-y-auto px-6 pb-2">
            {searching ? (
              <SearchResults rows={rows} pages={pages.map((p) => p.id)} onPick={goto} />
            ) : (
              /* ★ 某一页渲染期抛错只坏这一页,不能让整个窗口白屏;切页即复位 */
              <ErrorBoundary label={`settings:${visiblePage}`} resetKey={`${visiblePage}/${activeSub}`}>
                <PageBody
                  page={visiblePage}
                  sub={activeSub}
                  settings={settings}
                  versions={versions}
                  auth={auth}
                  patch={patch}
                />
              </ErrorBoundary>
            )}
          </div>

          <footer className="flex h-[82px] shrink-0 items-center justify-end px-6">
            <Button type="button" variant="primary" onClick={requestClose}>
              {t('common.done')}
            </Button>
          </footer>
        </div>
      </div>
      <Dialog open={confirmClose} title={t('themeStudio.unsaved')} onClose={() => setConfirmClose(false)} footer={<><Button type="button" variant="secondary" onClick={() => setConfirmClose(false)}>{t('common.cancel')}</Button><Button type="button" variant="secondary" onClick={() => { discardThemeDraft(); setConfirmClose(false); onClose() }}>{t('themeStudio.discard')}</Button></>}>
        <p>{t('themeStudio.discardHint')}</p>
      </Dialog>
    </div>
  )
}

function PageBody({
  page,
  sub,
  settings,
  versions,
  auth,
  patch
}: {
  page: SettingsPageId
  sub: string
  settings: AppSettings
  versions: Bootstrap['versions']
  auth: ClientAuthState
  patch: (p: AppSettingsPatch) => void
}): ReactNode {
  const props = { settings, sub, patch }
  switch (page) {
    case 'general':
      return <GeneralPage {...props} />
    case 'import':
      /*
        ★ 不吃 `SettingsPageProps` —— 这一页一个 `AppSettings` 字段都不读。
        来源授权、同步范围、已导入的映射全都是**设备本地状态**,存在
        `import_sources` 表里,不进会跟着账户云同步漂到另一台机器的设置
        (那台机器上 `/Users/a/.claude` 根本不存在)。硬塞 props 会让下一个人
        以为这里有设置可读。
      */
      return <ImportPage />
    case 'preference':
      return <PreferencePage {...props} />
    case 'model':
      return <ModelPage {...props} />
    /*
      ★ 不吃 `SettingsPageProps` —— 这一页一个 `AppSettings` 字段都不读、
      也不写。它只按时间窗查历史账,所有状态都是页面本地的。
    */
    case 'usage':
      return <UsagePage />
    case 'connection':
      return <ConnectionPage {...props} />
    case 'data':
      return <DataPage {...props} />
    /* ★ 不吃 `SettingsPageProps`:设备清单是这台电脑本地的,不属于会同步到账户的设置。 */
    case 'devices':
      return <DevicesPage />
    case 'experimental':
      return <ExperimentalPage {...props} />
    case 'about':
      return <AboutPage versions={versions} />
    default:
      if (page === 'account') return <AccountPage settings={settings} sub={sub} patch={patch} />
      /*
        ★ 不吃 `SettingsPageProps` —— 钱包一个 `AppSettings` 字段都不读，它读的是登录态。
        以前这里是 `AccountPage` 的 `walletOnly` 分支；钱包重写成独立页之后那个分支已删除。
      */
      if (page === 'wallet') return <WalletPage auth={auth} />
      return <StubPage page={page} />
  }
}

/** 「页面 › 行标题」列表。点一条 = 跳过去 + 清空查询(留着查询就还在结果页里) */
function SearchResults({
  rows,
  pages,
  onPick
}: {
  rows: readonly SettingsRow[]
  pages: readonly SettingsPageId[]
  onPick: (p: SettingsPageId, sub?: string) => void
}): ReactNode {
  const { t } = useI18n()
  if (rows.length === 0 && pages.length === 0) {
    return (
      <p className="py-10 text-center text-[13px] text-fg-muted">{t('settings.noMatch')}</p>
    )
  }
  return (
    <ul className="flex flex-col gap-0.5 py-2">
      {pages.map((id) => (
        <li key={`page-${id}`}>
          <ResultButton onClick={() => onPick(id)}>
            <span className="text-fg">{pageLabel(t, id)}</span>
            <span className="text-[11.5px] text-fg-faint">{t('settings.page')}</span>
          </ResultButton>
        </li>
      ))}
      {rows.map((r) => (
        <li key={`${r.page}-${r.sub ?? ''}-${r.title}`}>
          <ResultButton onClick={() => onPick(r.page, r.sub)}>
            <span className="text-fg-muted">{pageLabel(t, r.page)}</span>
            <span className="text-fg-faint">›</span>
            <span className="min-w-0 flex-1 truncate text-fg">{r.title}</span>
          </ResultButton>
        </li>
      ))}
    </ul>
  )
}

function pageLabel(t: Translate, page: SettingsPageId): string {
  return t(`settings.page.${page}` as Parameters<Translate>[0])
}

function subLabel(t: Translate, id: string): string {
  return t(`settings.sub.${id}` as Parameters<Translate>[0])
}

function ResultButton({
  children,
  onClick
}: {
  children: ReactNode
  onClick: () => void
}): ReactNode {
  return (
    <button
      type="button"
      onClick={onClick}
      className={cn(
        'flex w-full items-center gap-2 rounded-[8px] px-3 py-2.5 text-left text-[13px]',
        'transition-colors hover:bg-tint-hover'
      )}
    >
      {children}
    </button>
  )
}
