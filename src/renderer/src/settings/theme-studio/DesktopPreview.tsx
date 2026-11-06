import { useLayoutEffect, useRef, useState, type ReactNode } from 'react'
import { FileText, Folder, PanelLeft } from 'lucide-react'
import type { Appearance, ImageTheme, ThemeProfile } from '../../../../shared/domain/theme'
import type { InnerTab } from '../../../../shared/domain/tab'
import { DEFAULT_WORKSPACE_SETTINGS, type Workspace } from '../../../../shared/domain/workspace'
import { Button } from '../../components/arc/button/button'
import { IconButton } from '../../components/ui/IconButton'
import SegmentedControl from '../../components/arc/segmented-control/segmented-control'
import { TextInput } from '../../components/ui/TextInput'
import { Sidebar } from '../../shell/Sidebar'
import { OuterTabBar } from '../../shell/OuterTabBar'
import { InnerTabBar } from '../../shell/InnerTabBar'
import { useI18n } from '../../i18n'
import { applyProfile } from '../../theme/apply'
import { useAppearance } from '../../theme/useAppearance'

const noop = (): void => {}
const running = new Set<string>()
/** Production shell components with isolated fixture state; the preview never creates sessions or tools. */
export function DesktopPreview({ profile, images }: { profile: ThemeProfile; images: readonly ImageTheme[] }): ReactNode {
  const { t } = useI18n()
  const current = useAppearance()
  const [appearance, setAppearance] = useState<Appearance>(current)
  const [sidebar, setSidebar] = useState(true)
  const [right, setRight] = useState(true)
  const [bottom, setBottom] = useState(true)
  const [content, setContent] = useState<'chat' | 'editor' | 'browser'>('chat')
  const frame = useRef<HTMLDivElement>(null)
  const root = useRef<HTMLDivElement>(null)
  const [scale, setScale] = useState(.45)
  const workspace: Workspace = { id: 'theme-preview', name: t('themeStudio.previewTab'), rootPath: '', settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 0, lastOpenedAt: 0 }
  const tabs: InnerTab[] = [{ id: 'preview-chat', title: t('nav.newChat'), kind: 'chat', ref: { sessionId: null } }, { id: 'preview-doc', title: 'theme.ts', kind: 'doc', ref: { path: 'theme.ts' } }]
  const barProps = { runningSessionIds: running, menu: { items: [], overflow: [] }, onActivate: noop, onClose: noop, onMove: noop, onOpen: noop }
  useLayoutEffect(() => {
    if (!frame.current) return
    const ro = new ResizeObserver(([e]) => setScale((e?.contentRect.width ?? 500) / 1100))
    ro.observe(frame.current)
    return () => ro.disconnect()
  }, [])
  useLayoutEffect(() => { if (root.current) applyProfile(root.current, appearance, profile, images) }, [appearance, profile, images])
  return <>
    <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
      <span className="text-[12px] font-medium">{t('themeStudio.preview')}</span>
      <div className="flex gap-1"><SegmentedControl label={t('themeStudio.previewMode')} value={appearance} options={(['dark', 'light'] as const).map((v) => ({ value: v, label: t(`preference.${v}`) }))} onValueChange={(v) => setAppearance(v as Appearance)} />
        <IconButton label={t('themeStudio.toggleSidebar')} active={sidebar} onClick={() => setSidebar(!sidebar)}><PanelLeft size={14} /></IconButton>
      </div>
    </div>
    <div ref={frame} className="overflow-hidden rounded-panel border border-hairline" style={{ height: 700 * scale }}>
      <div ref={root} className="theme-desktop-preview origin-top-left" style={{ width: 1100, height: 700, transform: `scale(${scale})` }}>
        <div data-theme-region="window" className="app-ground flex h-full gap-2 bg-app p-2">
          {sidebar && <Sidebar workspace={workspace} chatTabs={tabs} sessions={[]} activeFeature={null} activeSessionId={null} runningSessionIds={running}
            onNewChat={() => setContent('chat')} onSearch={noop} onOpenFeature={noop} onOpenSettings={noop} onSelectSession={noop} onDeleteSession={async () => {}} onCollapse={() => setSidebar(false)}
            auth={{ mode: 'offline', user: null, expiresAt: null }} />}
          <main className="app-canvas flex min-w-0 flex-1 flex-col overflow-hidden rounded-panel bg-canvas">
            <header data-theme-region="chrome" className="flex h-[34px] shrink-0 items-end bg-chrome px-2">
              <OuterTabBar tabs={[{ id: 'preview-workspace', kind: 'workspace', ref: { workspaceId: workspace.id } }]} activeId="preview-workspace" workspaces={[workspace]} runningWorkspaceIds={running}
                onActivate={noop} onClose={noop} onTogglePin={noop} onMove={noop} onOpenWorkspace={noop} onEditWorkspace={noop} onPickWorkspace={noop} onCreateWorkspace={noop} onCreateSshWorkspace={noop}
                rightPanelOpen={right} bottomPanelOpen={bottom} onToggleRightPanel={() => setRight(!right)} onToggleBottomPanel={() => setBottom(!bottom)} />
            </header>
            <div className="flex min-h-0 flex-1">
              <div className="flex min-h-0 min-w-0 flex-1 flex-col">
                <section data-theme-region="canvas" className="flex min-h-0 flex-1 flex-col">
                  <InnerTabBar {...barProps} tabs={tabs} activeId={content === 'editor' ? 'preview-doc' : 'preview-chat'} onActivate={(id) => setContent(id === 'preview-doc' ? 'editor' : 'chat')} />
                  <div className="flex gap-1 px-4 pt-2">{(['chat', 'editor', 'browser'] as const).map((c) => <Button key={c} type="button" variant="secondary" onClick={() => setContent(c)}>{t(`themeStudio.previewContent.${c}`)}</Button>)}</div>
                  {content === 'chat' ? <div className="flex min-h-0 flex-1 flex-col justify-end gap-4 p-5">
                    <p className="max-w-[85%] self-end rounded-card bg-tint px-4 py-3 text-[14px] text-fg">{t('themeStudio.previewMessage')}</p>
                    <div className="theme-readable rounded-card bg-canvas p-3"><p className="text-[14px] text-fg">{t('themeStudio.previewReply')}</p><p className="mt-2 text-[12px] text-fg-muted">{t('themeStudio.previewDescription')}</p></div>
                    <div className="flex items-center gap-3 rounded-card border border-border bg-surface-raised p-3"><FileText size={20} className="text-icon" /><div><p className="text-[13px]">theme.ts</p><p className="text-[11px] text-fg-muted">TypeScript · 4 KB</p></div><span className="ml-auto rounded bg-accent px-2 py-1 text-[11px] text-accent-fg">{t('themeStudio.ready')}</span></div>
                    <div className="rounded-card border border-border bg-surface-input p-3"><TextInput value="" onChange={noop} placeholder={t('themeStudio.previewPlaceholder')} ariaLabel={t('themeStudio.previewPlaceholder')} /><div className="mt-2 flex justify-end"><Button type="button" variant="primary">{t('themeStudio.send')}</Button></div></div>
                  </div> : <div data-theme-region="content" className="m-4 min-h-0 flex-1 rounded-card bg-canvas p-4">
                    {content === 'editor' ? <pre className="font-mono text-[13px] text-fg">{'export const theme = {\n  canvas: "#1c1c1e",\n  accent: "#0071e3"\n}'}</pre> : <><div className="rounded bg-surface-field px-3 py-2 font-mono text-[12px] text-fg-muted">https://nextcowork.local</div><h3 className="mt-5 text-[20px]">NextCoWork</h3><p className="mt-2 text-[13px] text-fg-muted">{t('themeStudio.previewReply')}</p></>}
                  </div>}
                </section>
                {bottom && <section data-theme-region="bottomPanel" className="h-[145px] shrink-0 border-t border-hairline bg-surface">
                  <InnerTabBar {...barProps} tabs={[{ id: 'preview-terminal', kind: 'terminal', title: t('themeStudio.previewTerminal'), ref: { terminalId: 'preview' } }]} activeId="preview-terminal" onClose={() => setBottom(false)} />
                  <pre data-theme-region="content" className="h-[105px] bg-canvas p-3 font-mono text-[12px] text-fg">{'$ npm run typecheck\n'}<span className="text-accent">{t('themeStudio.previewTerminalReady')}</span></pre>
                </section>}
              </div>
              {right && <aside data-theme-region="rightPanel" className="w-[205px] shrink-0 border-l border-hairline bg-surface">
                <InnerTabBar {...barProps} tabs={[{ id: 'preview-files', kind: 'files', title: t('themeStudio.previewFiles'), ref: { path: '' } }]} activeId="preview-files" onClose={() => setRight(false)} />
                <div data-theme-region="content" className="p-3 text-[12px] text-fg">{['src', 'theme.ts', 'theme.css', 'package.json'].map((file, i) => <div key={file} className="flex items-center gap-2 rounded px-2 py-2 hover:bg-tint-hover">{i === 0 ? <Folder size={14} className="text-icon" /> : <FileText size={14} className="text-icon" />}{file}</div>)}</div>
              </aside>}
            </div>
          </main>
        </div>
      </div>
    </div>
    <div className="mt-2 flex flex-wrap gap-1"><Button type="button" variant="secondary" onClick={() => setRight(!right)}>{t('themeStudio.toggleRight')}</Button><Button type="button" variant="secondary" onClick={() => setBottom(!bottom)}>{t('themeStudio.toggleBottom')}</Button></div>
  </>
}
