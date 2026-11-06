import { ArrowUp, ChevronRight, Folder, RefreshCw, Server } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { ConnectionProfile, RemoteDirectory } from '../../../shared/domain/environment'
import type { Workspace } from '../../../shared/domain/workspace'
import { Button } from '../components/arc/button/button'
import { Dialog } from '../components/ui/Dialog'
import { IconButton } from '../components/ui/IconButton'
import { TextInput } from '../components/ui/TextInput'
import { useI18n } from '../i18n'
import { browseConnection, cancelConnectionRequest, closeBrowse, connectForBrowse, connectionErrorDetail, connectionErrorKey, createSshWorkspace, listConnections, onConnectionsChanged } from '../services/connections'
import { useWindowStore } from '../stores/window'
import { Spinner } from '../components/ui/Spinner'

export function CreateSshWorkspaceDialog({ hidden, onClose, onCreated }: { hidden: boolean; onClose(): void; onCreated(workspace: Workspace): Promise<boolean> }): ReactNode {
  const { t } = useI18n()
  const [profiles, setProfiles] = useState<ConnectionProfile[]>([])
  const [selected, setSelected] = useState('')
  const [directory, setDirectory] = useState<RemoteDirectory | null>(null)
  const [path, setPath] = useState('')
  const [showHidden, setShowHidden] = useState(false)
  const [allowed, setAllowed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)
  const [failureDetail, setFailureDetail] = useState<string | undefined>(undefined)
  const request = useRef<string | null>(null)
  const browseId = useRef<string | null>(null)
  const alive = useRef(true)
  const selectedRevision = profiles.find((profile) => profile.id === selected)?.revision
  const reportFailure = (error: unknown): void => { setFailure(connectionErrorKey(error)); setFailureDetail(connectionErrorDetail(error)) }
  useEffect(() => {
    if (request.current) void cancelConnectionRequest(request.current).catch(() => {})
    if (browseId.current) void closeBrowse(browseId.current).catch(() => {})
    request.current = null; browseId.current = null
    setDirectory(null); setPath(''); setAllowed(false); setBusy(false); setFailure(null); setFailureDetail(undefined)
  }, [selected, selectedRevision])
  useEffect(() => {
    alive.current = true
    const load = (): void => { void listConnections().then((items) => {
      if (!alive.current) return
      const available = items.map((item) => item.profile).filter((profile) => profile.enabled)
      setProfiles(available)
      setSelected((current) => available.some((profile) => profile.id === current) ? current : available[0]?.id ?? '')
    }).catch((error: unknown) => { if (alive.current) reportFailure(error) }) }
    load()
    const off = onConnectionsChanged(load)
    return () => {
      alive.current = false; off()
      if (request.current) void cancelConnectionRequest(request.current).catch(() => {})
      if (browseId.current) void closeBrowse(browseId.current).catch(() => {})
    }
  }, [])
  const loadDirectory = async (destination?: string): Promise<void> => {
    if (request.current) void cancelConnectionRequest(request.current).catch(() => {})
    const requestId = crypto.randomUUID()
    request.current = requestId
    setBusy(true); setFailure(null); setFailureDetail(undefined)
    try {
      const next = directory && destination !== undefined ? await browseConnection(directory.browseId, destination, requestId) : await connectForBrowse(selected, requestId, allowed)
      if (!alive.current || request.current !== requestId) { if (!directory) void closeBrowse(next.browseId).catch(() => {}); return }
      browseId.current = next.browseId
      setDirectory(next); setPath(next.path)
    } catch (error) { if (alive.current && request.current === requestId) reportFailure(error) }
    finally { if (request.current === requestId) { request.current = null; if (alive.current) setBusy(false) } }
  }
  const create = async (): Promise<void> => {
    if (!directory || busy) return
    const requestId = crypto.randomUUID()
    request.current = requestId
    setBusy(true); setFailure(null); setFailureDetail(undefined)
    try {
      const workspace = await createSshWorkspace(directory.browseId, directory.path, requestId)
      if (alive.current && request.current === requestId && await onCreated(workspace)) onClose()
    } catch (error) { if (alive.current) reportFailure(error) }
    finally { if (request.current === requestId) { request.current = null; if (alive.current) setBusy(false) } }
  }
  const entries = directory?.entries.filter((entry) => showHidden || !entry.name.startsWith('.')) ?? []
  return <Dialog open={!hidden} width={600} title={t('ssh.create')} onClose={onClose} footer={<>
    <Button variant="secondary" type="button" size="sm" onClick={onClose}>{t('common.cancel')}</Button>
    <Button type="button" size="sm" variant="primary" disabled={busy || (directory === null && (!selected || !allowed))}
      onClick={() => { if (directory) void create(); else void loadDirectory() }}>{busy ? <Spinner size="sm" /> : <Server size={13} />}{t(directory ? 'ssh.useDirectory' : 'ssh.connect')}</Button>
  </>}>
    <div className="flex flex-col gap-3">
      <div className="flex min-w-0 items-center gap-2">
        <select value={selected} aria-label={t('ssh.selectServer')} disabled={busy} className="apple-field h-8 min-w-0 flex-1 rounded-[6px] border border-border bg-canvas px-2 text-[12px] text-fg" onChange={(event) => {
          if (browseId.current) void closeBrowse(browseId.current).catch(() => {})
          browseId.current = null; setSelected(event.target.value); setDirectory(null); setAllowed(false)
        }}><option value="" disabled>{t('ssh.selectServer')}</option>{profiles.map((profile) => <option key={profile.id} value={profile.id}>{profile.name} · {profile.target.host}</option>)}</select>
        <Button variant="secondary" type="button" size="sm" onClick={() => useWindowStore.getState().openSettings('connection')}>{t('ssh.manage')}</Button>
      </div>
      {directory === null ? <label className="flex items-start gap-2 text-[12px] leading-relaxed text-fg-muted"><input type="checkbox" className="mt-1 shrink-0" checked={allowed} onChange={(event) => setAllowed(event.target.checked)} />{t('ssh.native.risk')}</label> : <>
        <div className="break-all text-[12px] text-fg-muted">{directory.facts.username}@{directory.facts.hostname} · {directory.facts.os}</div>
        <nav aria-label={t('ssh.path')} className="flex min-w-0 flex-wrap items-center gap-1 text-[12px] text-fg-muted">
          {directory.breadcrumbs.map((entry, index) => <span key={entry.path} className="flex min-w-0 max-w-full items-center gap-1">
            {index > 0 && <ChevronRight size={12} className="shrink-0" />}
            <button type="button" disabled={busy} className="truncate px-1 py-1 hover:text-fg" title={entry.path} onClick={() => { void loadDirectory(entry.path) }}>{entry.name}</button>
          </span>)}
        </nav>
        <div className="flex min-w-0 items-center gap-2">
          {directory.facts.os === 'win32' && directory.roots.length > 0 && <select aria-label={t('ssh.volume')} value={directory.roots.find((root) => directory.path.toLowerCase().startsWith(root.toLowerCase())) ?? ''} disabled={busy} className="apple-field h-8 max-w-24 text-[12px]" onChange={(event) => { void loadDirectory(event.target.value) }}><option value="" disabled>{t('ssh.volume')}</option>{directory.roots.map((root) => <option key={root} value={root}>{root}</option>)}</select>}
          <IconButton label={t('ssh.parent')} disabled={busy || directory.parent === directory.path} onClick={() => { void loadDirectory(directory.parent) }}><ArrowUp size={14} /></IconButton>
          <div className="min-w-0 flex-1" onKeyDown={(event) => { if (event.key === 'Enter' && !event.nativeEvent.isComposing && !busy) void loadDirectory(path) }}><TextInput value={path} onChange={setPath} ariaLabel={t('ssh.path')} /></div>
          <Button variant="secondary" type="button" size="sm" disabled={busy} onClick={() => { void loadDirectory(path) }}>{t('ssh.go')}</Button>
          <IconButton label={t('common.refresh')} disabled={busy} onClick={() => { void loadDirectory(directory.path) }}><RefreshCw size={14} /></IconButton>
        </div>
        <label className="flex items-center gap-2 text-[12px] text-fg-muted"><input type="checkbox" checked={showHidden} onChange={(event) => setShowHidden(event.target.checked)} />{t('ssh.hidden')}</label>
        <div className="h-[260px] overflow-auto border-y border-border" aria-label={t('ssh.selectDirectory')} aria-busy={busy}>
          {entries.length === 0 && <p className="py-8 text-center text-[12px] text-fg-faint">{t('ssh.emptyDirectory')}</p>}
          {entries.map((entry) => <button key={entry.path} type="button" disabled={busy} onClick={() => { void loadDirectory(entry.path) }} className="flex w-full items-center gap-2 px-2 py-2 text-left text-[13px] text-fg hover:bg-tint-hover focus-visible:bg-tint-hover">
            <Folder size={15} className="shrink-0 text-icon" /><span className="min-w-0 truncate" title={entry.path}>{entry.name}</span>
          </button>)}
        </div>
        {directory.truncated && <p className="text-[12px] text-fg-muted">{t('ssh.truncated')}</p>}
      </>}
      {failure && <p role="alert" className="text-[12px] text-danger">{t(failure)}</p>}
      {failureDetail && <p className="break-all font-mono text-[11px] text-fg-faint">{failureDetail}</p>}
    </div>
  </Dialog>
}