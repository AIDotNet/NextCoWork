import { ArrowLeft, Copy, Workflow } from 'lucide-react'
import { useCallback, useEffect, useState, type ReactNode } from 'react'
import type { MarkdownResourceFile, MarkdownResourceScope } from '../../../../../shared/domain/markdown-resource'
import { isBuiltinModeId, MODE_ID_RE, type ModeDefinition } from '../../../../../shared/domain/mode'
import { Button } from '../../../components/arc/button/button'
import { IconButton } from '../../../components/ui/IconButton'
import SegmentedControl from '../../../components/arc/segmented-control/segmented-control'
import { TextInput } from '../../../components/ui/TextInput'
import { useI18n } from '../../../i18n'
import { listModes, onModesChanged } from '../../../services/modes'
import { deleteResource, getResource, saveResource } from '../../../services/resources'
import { useWindowStore } from '../../../stores/window'
import { MarkdownResourceEditor } from '../markdown/MarkdownResourceEditor'
import { readField, readListField, setField, setListField, type Frontmatter } from '../markdown/frontmatter-form'
import { ResourceListPane } from '../shared/ResourceListPane'
import type { PanelRow } from '../shared/ResourcePanel'

interface ModeRow extends PanelRow {
  mode: ModeDefinition
}

interface ModeSavePayload {
  scope: MarkdownResourceScope
  name: string
  frontmatter: Frontmatter
  body: string
}

export function ModesPanel(): ReactNode {
  const { t } = useI18n()
  const workspaceId = useWindowStore((state) => state.activeWorkspaceId)
  const [rows, setRows] = useState<ModeRow[]>([])
  const [tools, setTools] = useState<readonly string[]>([])
  const [editing, setEditing] = useState<MarkdownResourceFile | null>(null)
  const [viewing, setViewing] = useState<ModeDefinition | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const modeName = useCallback((mode: ModeDefinition): string => isBuiltinModeId(mode.id)
    ? t(`composer.mode.${mode.id}` as 'composer.mode.code' | 'composer.mode.plan' | 'composer.mode.acp')
    : mode.name, [t])
  const modeDescription = useCallback((mode: ModeDefinition): string => isBuiltinModeId(mode.id)
    ? t(`composer.mode.${mode.id}Hint` as 'composer.mode.codeHint' | 'composer.mode.planHint' | 'composer.mode.acpHint')
    : mode.description, [t])

  const refresh = useCallback(() => {
    if (workspaceId === null) return
    void listModes(workspaceId).then((catalog) => {
      setTools(catalog.tools)
      setRows(catalog.modes.map((mode) => ({
        name: modeName(mode),
        description: modeDescription(mode),
        scope: mode.source.kind,
        enabled: true,
        mode
      })))
      setError(catalog.diagnostics.length === 0 ? null : t('ext.modes.diagnostics', { count: catalog.diagnostics.length }))
    }).catch(() => setError(t('ext.error.loadFailed')))
  }, [modeDescription, modeName, t, workspaceId])

  useEffect(() => {
    refresh()
    return onModesChanged(refresh)
  }, [refresh])

  const createDraft = (source?: ModeDefinition): void => {
    const taken = new Set(rows.map((row) => row.mode.id))
    const base = source?.id ?? 'custom-mode'
    let id = `${base}${source === undefined ? '' : '-custom'}`
    for (let suffix = 2; taken.has(id); suffix += 1) id = `${base}-custom-${String(suffix)}`
    const scope: MarkdownResourceScope = workspaceId === null ? 'global' : 'project'
    setViewing(null)
    setEditing({
      kind: 'mode',
      scope,
      name: id,
      path: '',
      frontmatter: {
        name: source === undefined ? '' : t('ext.modes.copyName', { name: modeName(source) }),
        description: source === undefined ? '' : modeDescription(source),
        ...(source?.tools === undefined ? {} : { tools: [...source.tools] }),
        ...(source?.requiredTools === undefined ? {} : { requiredTools: [...source.requiredTools] })
      },
      body: source?.prompt ?? '',
      skipped: [],
      revision: ''
    })
  }

  const open = (row: ModeRow): void => {
    setError(null)
    if (row.scope === 'builtin') {
      setViewing(row.mode)
      setEditing(null)
      return
    }
    void getResource('mode', row.scope, row.mode.id, workspaceId ?? undefined)
      .then((file) => { setEditing(file); setViewing(null) })
      .catch(() => setError(t('ext.error.loadFailed')))
  }

  const save = (payload: ModeSavePayload): void => {
    const current = editing
    if (current === null) return
    const moved = current.revision !== '' && (payload.name !== current.name || payload.scope !== current.scope)
    setSaving(true)
    setError(null)
    void saveResource({
      kind: 'mode',
      scope: payload.scope,
      name: payload.name,
      ...(workspaceId === null ? {} : { workspaceId }),
      frontmatter: payload.frontmatter,
      body: payload.body,
      ...(current.revision === '' || moved ? {} : { revision: current.revision })
    }).then(async (file) => {
      if (moved) await deleteResource('mode', current.scope, current.name, workspaceId ?? undefined)
      setEditing(file)
      refresh()
    }).catch(() => setError(t('ext.error.saveFailed')))
      .finally(() => setSaving(false))
  }

  const remove = (): void => {
    const current = editing
    if (current === null) return
    if (current.revision === '') { setEditing(null); return }
    void deleteResource('mode', current.scope, current.name, workspaceId ?? undefined)
      .then(() => { setEditing(null); refresh() })
      .catch(() => setError(t('ext.error.deleteFailed')))
  }

  if (viewing !== null) {
    return <BuiltinModeView mode={viewing} onBack={() => setViewing(null)} onCopy={() => createDraft(viewing)} />
  }
  if (editing !== null) {
    return <ModeEditor key={`${editing.scope}:${editing.name}:${editing.path}`} file={editing} tools={tools}
      taken={new Set(rows.map((row) => `${row.scope}:${row.mode.id}`))} saving={saving} error={error}
      onSave={save} onDelete={remove} onClose={() => { setEditing(null); setError(null) }} />
  }

  return <ResourceListPane rows={rows} error={error} icon={<Workflow size={26} />}
    emptyTitle={t('ext.modes.empty')} emptyHint={t('ext.modes.emptyHint')} onNew={() => createDraft()}
    onOpen={open} onToggle={() => undefined} openReadOnly hideToggle />
}

function BuiltinModeView({ mode, onBack, onCopy }: { mode: ModeDefinition; onBack: () => void; onCopy: () => void }): ReactNode {
  const { t } = useI18n()
  const name = isBuiltinModeId(mode.id) ? t(`composer.mode.${mode.id}` as 'composer.mode.code') : mode.name
  return <div className="flex min-h-0 flex-1 flex-col">
    <div className="flex shrink-0 items-center gap-2 border-b border-hairline px-4 py-2">
      <IconButton label={t('ext.back')} size={26} width={34} onClick={onBack} className="rounded-pill bg-tint"><ArrowLeft size={14} /></IconButton>
      <span className="text-[13px] text-fg">{name}</span>
      <span className="text-[11px] text-fg-faint">{t('ext.modes.readOnly')}</span>
      <Button type="button" variant="primary" size="sm" className="ml-auto" onClick={onCopy}><Copy size={13} />{t('ext.modes.copy')}</Button>
    </div>
    <div className="min-h-0 flex-1 overflow-y-auto p-4">
      <p className="mb-4 text-[12px] text-fg-muted">{mode.description}</p>
      <ModeToolSummary tools={mode.tools ?? []} required={mode.requiredTools ?? []} />
      <h2 className="mt-5 mb-2 text-[12px] font-medium text-fg">{t('ext.modes.prompt')}</h2>
      <pre className="selectable whitespace-pre-wrap rounded-lg border border-border bg-app p-3 text-[12px] leading-5 text-fg-muted">{mode.prompt}</pre>
    </div>
  </div>
}

function ModeEditor({ file, tools, taken, saving, error, onSave, onDelete, onClose }: {
  file: MarkdownResourceFile
  tools: readonly string[]
  taken: ReadonlySet<string>
  saving: boolean
  error: string | null
  onSave: (payload: ModeSavePayload) => void
  onDelete: () => void
  onClose: () => void
}): ReactNode {
  const { t } = useI18n()
  const [id, setId] = useState(file.name)
  const [scope, setScope] = useState<MarkdownResourceScope>(file.scope)
  const [frontmatter, setFrontmatter] = useState<Frontmatter>(file.frontmatter)
  const [body, setBody] = useState(file.body)
  const [sourceMode, setSourceMode] = useState(false)
  const selected = readListField(frontmatter, 'tools')
  const required = readListField(frontmatter, 'requiredTools')
  const name = readField(frontmatter, 'name')
  const description = readField(frontmatter, 'description')
  const idTaken = (id !== file.name || scope !== file.scope) && taken.has(`${scope}:${id}`)
  const invalid = !MODE_ID_RE.test(id) || isBuiltinModeId(id) || idTaken || name.trim() === '' || description.trim() === '' || body.trim() === ''
    || required.some((tool) => !selected.includes(tool))
  const payload = (): ModeSavePayload => ({ scope, name: id, frontmatter, body })

  const fields = <ModeFields frontmatter={frontmatter} setFrontmatter={setFrontmatter} tools={tools} />
  if (sourceMode) {
    return <MarkdownResourceEditor kind="mode" file={{ ...file, name: id, scope }} frontmatter={frontmatter} body={body}
      onBody={setBody} onSave={() => { if (!invalid) onSave(payload()) }} onDelete={onDelete} onClose={onClose}
      saving={saving} error={error} fields={<>
        <div className="mb-3 flex items-center justify-between"><span className="text-[12px] text-fg-muted">{t('ext.modes.sourceHint')}</span>
          <Button type="button" variant="secondary" size="sm" onClick={() => setSourceMode(false)}>{t('ext.modes.form')}</Button></div>
        {fields}
      </>} />
  }

  return <div className="flex min-h-0 flex-1 flex-col">
    <div className="flex shrink-0 items-center gap-2 border-b border-hairline px-4 py-2">
      <IconButton label={t('ext.back')} size={26} width={34} onClick={onClose} className="rounded-pill bg-tint"><ArrowLeft size={14} /></IconButton>
      <span className="truncate text-[13px] text-fg">{id}</span>
      <Button type="button" variant="secondary" size="sm" className="ml-auto" onClick={() => setSourceMode(true)}>{t('ext.modes.source')}</Button>
      <Button type="button" variant="danger" size="sm" onClick={onDelete}>{t('ext.delete')}</Button>
      <Button type="button" variant="primary" size="sm" disabled={invalid || saving} onClick={() => onSave(payload())}>{t('ext.save')}</Button>
    </div>
    {(error !== null || invalid) && <p className="shrink-0 border-b border-hairline px-4 py-1.5 text-[11px] text-danger" role="alert">
      {error ?? (idTaken ? t('ext.error.modeIdTaken') : t('ext.error.modeInvalid'))}
    </p>}
    <div className="min-h-0 flex-1 overflow-y-auto p-4">
      <div className="grid max-w-3xl grid-cols-[140px_1fr] items-center gap-x-4 gap-y-3">
        <label className="text-[12px] text-fg-muted">{t('ext.modes.id')}</label>
        <TextInput value={id} onChange={setId} ariaLabel={t('ext.modes.id')} invalid={!MODE_ID_RE.test(id) || isBuiltinModeId(id) || idTaken} />
        <label className="text-[12px] text-fg-muted">{t('ext.modes.scope')}</label>
        <SegmentedControl value={scope} onValueChange={(value) => setScope(value as MarkdownResourceScope)} label={t('ext.modes.scope')} options={[
          { value: 'global', label: t('ext.scope.global') },
          { value: 'project', label: t('ext.scope.project') }
        ]} />
      </div>
      <div className="mt-4 max-w-3xl">{fields}</div>
      <label className="mt-4 mb-1 block text-[12px] text-fg-muted">{t('ext.modes.prompt')}</label>
      <textarea className="apple-field selectable min-h-56 w-full max-w-3xl rounded-lg border border-border bg-surface-field p-3 text-[13px] leading-5 text-fg outline-none focus:border-fg-faint"
        value={body} onChange={(event) => setBody(event.target.value)} aria-label={t('ext.modes.prompt')} />
    </div>
  </div>
}

function ModeFields({ frontmatter, setFrontmatter, tools }: {
  frontmatter: Frontmatter
  setFrontmatter: (value: Frontmatter) => void
  tools: readonly string[]
}): ReactNode {
  const { t } = useI18n()
  const selected = readListField(frontmatter, 'tools')
  const required = readListField(frontmatter, 'requiredTools')
  const toggle = (key: 'tools' | 'requiredTools', tool: string, checked: boolean): void => {
    const values = key === 'tools' ? selected : required
    const next = checked ? [...values, tool] : values.filter((value) => value !== tool)
    let fm = setListField(frontmatter, key, next)
    if (key === 'tools' && !checked && required.includes(tool)) {
      fm = setListField(fm, 'requiredTools', required.filter((value) => value !== tool))
    }
    setFrontmatter(fm)
  }
  return <div className="flex flex-col gap-3">
    <label className="text-[12px] text-fg-muted">{t('ext.modes.name')}</label>
    <TextInput value={readField(frontmatter, 'name')} onChange={(value) => setFrontmatter(setField(frontmatter, 'name', value))} ariaLabel={t('ext.modes.name')} />
    <label className="text-[12px] text-fg-muted">{t('ext.modes.description')}</label>
    <TextInput value={readField(frontmatter, 'description')} onChange={(value) => setFrontmatter(setField(frontmatter, 'description', value))} ariaLabel={t('ext.modes.description')} />
    <div>
      <p className="mb-1 text-[12px] text-fg-muted">{t('ext.modes.tools')}</p>
      <p className="mb-2 text-[11px] text-fg-faint">{t('ext.modes.toolsHint')}</p>
      <div className="grid grid-cols-2 gap-1 rounded-lg border border-border p-2 sm:grid-cols-3">
        {tools.map((tool) => <label key={tool} className="flex items-center gap-2 rounded px-2 py-1 text-[11px] text-fg-muted hover:bg-tint">
          <input type="checkbox" checked={selected.includes(tool)} onChange={(event) => toggle('tools', tool, event.target.checked)} />
          <span className="truncate font-mono">{tool}</span>
        </label>)}
      </div>
    </div>
    <div>
      <p className="mb-1 text-[12px] text-fg-muted">{t('ext.modes.requiredTools')}</p>
      <p className="mb-2 text-[11px] text-fg-faint">{t('ext.modes.requiredToolsHint')}</p>
      <div className="grid grid-cols-2 gap-1 rounded-lg border border-border p-2 sm:grid-cols-3">
        {selected.map((tool) => <label key={tool} className="flex items-center gap-2 rounded px-2 py-1 text-[11px] text-fg-muted hover:bg-tint">
          <input type="checkbox" checked={required.includes(tool)} onChange={(event) => toggle('requiredTools', tool, event.target.checked)} />
          <span className="truncate font-mono">{tool}</span>
        </label>)}
      </div>
    </div>
  </div>
}

function ModeToolSummary({ tools, required }: { tools: readonly string[]; required: readonly string[] }): ReactNode {
  const { t } = useI18n()
  return <div className="grid gap-3 sm:grid-cols-2">
    <div><h2 className="mb-1 text-[12px] font-medium text-fg">{t('ext.modes.tools')}</h2><p className="selectable font-mono text-[11px] text-fg-muted">{tools.join(', ') || t('ext.modes.allTools')}</p></div>
    <div><h2 className="mb-1 text-[12px] font-medium text-fg">{t('ext.modes.requiredTools')}</h2><p className="selectable font-mono text-[11px] text-fg-muted">{required.join(', ') || t('ext.modes.none')}</p></div>
  </div>
}
