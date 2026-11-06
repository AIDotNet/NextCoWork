/**
 * 命令 / 子代理面板的共用骨架。
 *
 * ★ 两者同构这件事已经在 IPC 层证明过了（`resource:get/save/delete` 一套通吃），
 *   界面这一层自然也该共用。各自只剩三样东西要给：怎么取列表、frontmatter 表单
 *   长什么样、新建时的默认内容。
 */
import { useCallback, useEffect, useState, type ReactNode } from 'react'
import type {
  MarkdownResourceFile,
  MarkdownResourceKind,
  MarkdownResourceScope
} from '../../../../../shared/domain/markdown-resource'
import { Button } from '../../../components/arc/button/button'
import { Dialog } from '../../../components/ui/Dialog'
import SegmentedControl from '../../../components/arc/segmented-control/segmented-control'
import { TextInput } from '../../../components/ui/TextInput'
import { useI18n } from '../../../i18n'
import { deleteResource, getResource, saveResource } from '../../../services/resources'
import { useWindowStore } from '../../../stores/window'
import { MarkdownResourceEditor } from '../markdown/MarkdownResourceEditor'
import type { Frontmatter } from '../markdown/frontmatter-form'
import { ResourceListPane } from './ResourceListPane'
import type { ResourceRow } from './ResourceTable'

export interface PanelRow extends ResourceRow {
  scope: 'builtin' | 'global' | 'project'
}

export function ResourcePanel({
  kind,
  load,
  setEnabled,
  subscribe,
  renderFields,
  newFileDefaults,
  icon,
  emptyTitle,
  emptyHint
}: {
  kind: MarkdownResourceKind
  load: (workspaceId?: string) => Promise<PanelRow[]>
  setEnabled: (name: string, enabled: boolean) => Promise<void>
  subscribe: (cb: () => void) => () => void
  renderFields: (fm: Frontmatter, set: (fm: Frontmatter) => void) => ReactNode
  newFileDefaults: () => { frontmatter: Frontmatter; body: string }
  icon: ReactNode
  emptyTitle: string
  emptyHint: string
}): ReactNode {
  const { t } = useI18n()
  const workspaceId = useWindowStore((s) => s.activeWorkspaceId)
  const [rows, setRows] = useState<PanelRow[]>([])
  const [error, setError] = useState<string | null>(null)

  // 打开的编辑器。null = 在列表页。
  const [editing, setEditing] = useState<MarkdownResourceFile | null>(null)
  const [draftFm, setDraftFm] = useState<Frontmatter>({})
  const [draftBody, setDraftBody] = useState('')
  const [saving, setSaving] = useState(false)

  const [creating, setCreating] = useState(false)
  const [newName, setNewName] = useState('')
  const [newScope, setNewScope] = useState<MarkdownResourceScope>('global')

  const refresh = useCallback(() => {
    void load(workspaceId ?? undefined)
      .then(setRows)
      .catch(() => setError(t('ext.error.loadFailed')))
  }, [load, workspaceId, t])

  useEffect(() => {
    refresh()
    return subscribe(refresh)
  }, [refresh, subscribe])

  const open = (scope: MarkdownResourceScope, name: string): void => {
    setError(null)
    void getResource(kind, scope, name, workspaceId ?? undefined)
      .then((file) => {
        setEditing(file)
        setDraftFm(file.frontmatter)
        setDraftBody(file.body)
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
  }

  const save = (): void => {
    if (editing === null) return
    setSaving(true)
    setError(null)
    void saveResource({
      kind,
      scope: editing.scope,
      name: editing.name,
      ...(workspaceId === null ? {} : { workspaceId }),
      frontmatter: draftFm,
      body: draftBody,
      // 新建出来的那一份 revision 是空串 —— 传 undefined 让主进程走「新建」分支。
      ...(editing.revision === '' ? {} : { revision: editing.revision })
    })
      .then((file) => {
        setEditing(file)
        setDraftFm(file.frontmatter)
        setDraftBody(file.body)
        refresh()
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setSaving(false))
  }

  const remove = (): void => {
    if (editing === null) return
    void deleteResource(kind, editing.scope, editing.name, workspaceId ?? undefined)
      .then(() => {
        setEditing(null)
        refresh()
      })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
  }

  const create = (): void => {
    const name = newName.trim()
    if (name === '') return
    setCreating(false)
    setNewName('')
    const defaults = newFileDefaults()
    /*
      ★ 先在内存里造一份「还没落盘」的 MarkdownResourceFile，revision 留空。
      不先写一个空文件再打开，是因为用户可能填一半就走了 —— 那样磁盘上会留下
      一个半成品，而它会立刻出现在斜杠命令弹层里。
    */
    setEditing({
      kind,
      scope: newScope,
      name,
      path: '',
      frontmatter: defaults.frontmatter,
      body: defaults.body,
      skipped: [],
      revision: ''
    })
    setDraftFm(defaults.frontmatter)
    setDraftBody(defaults.body)
  }

  if (editing !== null) {
    return (
      <MarkdownResourceEditor
        kind={kind}
        file={editing}
        frontmatter={draftFm}
        body={draftBody}
        onBody={setDraftBody}
        onSave={save}
        onDelete={remove}
        onClose={() => { setEditing(null); setError(null) }}
        saving={saving}
        error={error}
        fields={renderFields(draftFm, setDraftFm)}
      />
    )
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <ResourceListPane
        rows={rows}
        error={error}
        icon={icon}
        emptyTitle={emptyTitle}
        emptyHint={emptyHint}
        onNew={() => {
          setNewScope(workspaceId === null ? 'global' : 'project')
          setCreating(true)
        }}
        onOpen={(row) => { if (row.scope !== 'builtin') open(row.scope, row.name) }}
        onToggle={(row, enabled) => { void setEnabled(row.name, enabled).then(refresh) }}
      />

      <Dialog
        title={t('ext.newTitle')}
        open={creating}
        onClose={() => setCreating(false)}
        footer={
          <>
            <Button type="button" variant="secondary" size="sm" onClick={() => setCreating(false)}>{t('common.cancel')}</Button>
            <Button type="button" variant="primary" size="sm" onClick={create} disabled={newName.trim() === ''}>
              {t('ext.create')}
            </Button>
          </>
        }
      >
        <div className="flex flex-col gap-3">
          <TextInput
            value={newName}
            onChange={setNewName}
            placeholder={t('ext.namePlaceholder')}
            ariaLabel={t('ext.namePlaceholder')}
          />
          <SegmentedControl
            value={newScope}
            onValueChange={(value) => setNewScope(value as MarkdownResourceScope)}
            label={t('ext.scopeFilter')}
            options={[
              { value: 'global', label: t('ext.scope.global') },
              ...(workspaceId === null ? [] : [{ value: 'project' as const, label: t('ext.scope.project') }])
            ]}
          />
        </div>
      </Dialog>
    </div>
  )
}
