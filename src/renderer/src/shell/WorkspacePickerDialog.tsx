import { Folder, FolderOpen, Pencil, Search, Server } from 'lucide-react'
import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type { OuterTab } from '../../../shared/domain/tab'
import type { Workspace } from '../../../shared/domain/workspace'
import { isLocalEnvironment } from '../../../shared/domain/environment'
import { useI18n } from '../i18n'
import { cn } from '../lib/cn'
import { Button } from '../components/arc/button/button'
import { Dialog } from '../components/ui/Dialog'
import { IconButton } from '../components/ui/IconButton'
import SegmentedControl from '../components/arc/segmented-control/segmented-control'
import { TextInput } from '../components/ui/TextInput'

type WorkspaceFilter = 'all' | 'recent' | 'opened' | 'ssh'

export function WorkspacePickerDialog({
  open,
  onClose,
  workspaces,
  tabs,
  activeWorkspaceId,
  onOpenWorkspace,
  onEditWorkspace,
  onPickWorkspace,
  onCreateWorkspace,
  onCreateSshWorkspace
}: {
  open: boolean
  onClose: () => void
  workspaces: readonly Workspace[]
  tabs: readonly OuterTab[]
  activeWorkspaceId?: string | null
  onOpenWorkspace: (workspaceId: string) => void
  onEditWorkspace: (workspaceId: string) => void
  onPickWorkspace: () => void
  onCreateWorkspace: () => void
  onCreateSshWorkspace: () => void
}): ReactNode {
  const { t } = useI18n()
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState<WorkspaceFilter>('all')
  const searchRef = useRef<HTMLInputElement>(null)
  const openedIds = useMemo(
    () => new Set(tabs.filter((tab) => tab.kind === 'workspace').map((tab) => tab.ref.workspaceId)),
    [tabs]
  )

  useEffect(() => {
    if (!open) return
    setQuery('')
    setFilter('all')
    const frame = window.requestAnimationFrame(() => searchRef.current?.focus())
    return () => window.cancelAnimationFrame(frame)
  }, [open])

  const visibleWorkspaces = useMemo(() => {
    const normalizedQuery = query.trim().toLocaleLowerCase()
    const recentIds = new Set(
      [...workspaces]
        .sort((a, b) => b.lastOpenedAt - a.lastOpenedAt)
        .slice(0, 8)
        .map((workspace) => workspace.id)
    )
    const filtered = workspaces.filter((workspace) => {
      const matchesQuery = normalizedQuery === '' || `${workspace.name} ${workspace.rootPath}`.toLocaleLowerCase().includes(normalizedQuery)
      if (!matchesQuery) return false
      if (filter === 'recent') return recentIds.has(workspace.id)
      if (filter === 'opened') return openedIds.has(workspace.id)
      if (filter === 'ssh') return !isLocalEnvironment(workspace.environment)
      return true
    })
    return [...filtered].sort((a, b) => {
      if (a.id === activeWorkspaceId) return -1
      if (b.id === activeWorkspaceId) return 1
      return b.lastOpenedAt - a.lastOpenedAt || a.name.localeCompare(b.name)
    })
  }, [activeWorkspaceId, filter, openedIds, query, workspaces])

  const filterOptions = [
    { value: 'all' as const, label: t('workspace.filterAll') },
    { value: 'recent' as const, label: t('workspace.filterRecent') },
    { value: 'opened' as const, label: t('workspace.filterOpened') },
    { value: 'ssh' as const, label: t('workspace.filterSsh') }
  ]

  return (
    <Dialog
      open={open}
      onClose={onClose}
      title={t('workspace.switch')}
      description={t('workspace.count', { count: workspaces.length })}
      width={860}
      footer={(
        <div className="flex w-full flex-wrap items-center justify-between gap-2">
          <span className="text-[11px] text-fg-faint">{t('workspace.switchHint')}</span>
          <div className="flex flex-wrap justify-end gap-2">
            <Button variant="secondary" type="button" size="sm" onClick={() => { onPickWorkspace(); onClose() }}>
              <FolderOpen size={13} />
              {t('nav.openFolder')}
            </Button>
            <Button variant="secondary" type="button" size="sm" onClick={() => { onCreateWorkspace(); onClose() }}>
              <Folder size={13} />
              {t('nav.createWorkspace')}
            </Button>
            <Button variant="secondary" type="button" size="sm" onClick={() => { onCreateSshWorkspace(); onClose() }}>
              <Server size={13} />
              {t('nav.createSshWorkspace')}
            </Button>
          </div>
        </div>
      )}
    >
      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-2.5 sm:flex-row sm:items-center">
          <TextInput
            value={query}
            onChange={setQuery}
            placeholder={t('workspace.searchPlaceholder')}
            ariaLabel={t('workspace.searchPlaceholder')}
            icon={<Search size={15} />}
            inputRef={searchRef}
            className="min-w-0 flex-1"
          />
          <SegmentedControl
            value={filter}
            options={filterOptions}
            onValueChange={(v) => setFilter(v as WorkspaceFilter)}
            label={t('workspace.filterLabel')}
            className="max-w-full shrink-0 self-start sm:self-auto"
          />
        </div>

        {visibleWorkspaces.length === 0 ? (
          <div className="flex min-h-[280px] flex-col items-center justify-center rounded-card border border-dashed border-border bg-surface-raised px-6 text-center">
            <Search size={22} className="text-fg-faint" />
            <p className="mt-3 text-[13px] text-fg">{t('workspace.noMatches')}</p>
            <p className="mt-1 text-[12px] text-fg-faint">{t('workspace.noMatchesHint')}</p>
          </div>
        ) : (
          <ul
            aria-label={t('workspace.switch')}
            className="grid gap-3"
            style={{ gridTemplateColumns: 'repeat(auto-fill, minmax(210px, 1fr))' }}
          >
            {visibleWorkspaces.map((workspace) => {
              const opened = openedIds.has(workspace.id)
              const current = workspace.id === activeWorkspaceId
              return (
                <WorkspaceCard
                  key={workspace.id}
                  workspace={workspace}
                  opened={opened}
                  current={current}
                  onOpen={() => {
                    onOpenWorkspace(workspace.id)
                    onClose()
                  }}
                  onEdit={() => {
                    onEditWorkspace(workspace.id)
                    onClose()
                  }}
                  t={t}
                />
              )
            })}
          </ul>
        )}
      </div>
    </Dialog>
  )
}

function WorkspaceCard({
  workspace,
  opened,
  current,
  onOpen,
  onEdit,
  t
}: {
  workspace: Workspace
  opened: boolean
  current: boolean
  onOpen: () => void
  onEdit: () => void
  t: ReturnType<typeof useI18n>['t']
}): ReactNode {
  const local = isLocalEnvironment(workspace.environment)
  const Icon = local ? Folder : Server
  const typeLabel = local ? t('workspace.local') : t('workspace.ssh')

  return (
    <li className="group relative min-w-0">
      <button
        type="button"
        title={workspace.rootPath}
        aria-current={current ? 'page' : undefined}
        onClick={onOpen}
        className={cn(
          'app-no-drag flex min-h-[116px] w-full min-w-0 flex-col rounded-card border p-3 text-left',
          'transition-[background-color,border-color,box-shadow,transform] duration-150',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60',
          current
            ? 'border-accent bg-accent/10 shadow-sm shadow-accent/10'
            : 'border-border bg-surface-raised hover:border-accent/60 hover:bg-tint-hover'
        )}
      >
        <span className="flex min-w-0 items-start gap-2">
          <span className={cn('flex size-8 shrink-0 items-center justify-center rounded-[8px]', current ? 'bg-accent/15 text-accent' : 'bg-tint text-icon')}>
            <Icon size={17} />
          </span>
          <span className="min-w-0 flex-1 pr-6">
            <span className="block truncate text-[13px] text-fg">{workspace.name}</span>
            <span className="mt-1 block truncate text-[11px] text-fg-faint" title={workspace.rootPath}>{workspace.rootPath}</span>
          </span>
        </span>
        <span className="mt-auto flex min-w-0 items-center gap-2 pt-3 text-[11px]">
          <span className="truncate text-fg-faint">{typeLabel}</span>
          {workspace.unavailable === true && <span className="truncate text-danger">{t('workspace.unavailable')}</span>}
          {current && <span className="ml-auto shrink-0 rounded-pill bg-accent px-2 py-0.5 text-[10px] text-accent-fg">{t('workspace.current')}</span>}
          {!current && opened && <span className="ml-auto shrink-0 text-accent">{t('workspace.opened')}</span>}
        </span>
      </button>
      <IconButton
        label={t('workspace.edit')}
        size={26}
        onClick={onEdit}
        className="absolute top-2 right-2 opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100"
      >
        <Pencil size={13} />
      </IconButton>
    </li>
  )
}
