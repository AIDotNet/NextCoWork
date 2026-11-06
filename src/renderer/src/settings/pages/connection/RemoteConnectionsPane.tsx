import { Cable, FolderOpen, Pencil, Plus, Server, Trash2, Unplug } from 'lucide-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import type { ConnectionProfile, ConnectionProfileInput, SshAuthMethod } from '../../../../../shared/domain/environment'
import { Button } from '../../../components/arc/button/button'
import { Dialog } from '../../../components/ui/Dialog'
import { IconButton } from '../../../components/ui/IconButton'
import SegmentedControl from '../../../components/arc/segmented-control/segmented-control'
import { Select } from '../../../components/ui/Select'
import { TextInput } from '../../../components/ui/TextInput'
import { useI18n } from '../../../i18n'
import { Spinner } from '../../../components/ui/Spinner'
import { cancelConnectionRequest, closeBrowse, connectForBrowse, connectionErrorDetail, connectionErrorKey, disconnectConnection, listConnections,
  onConnectionsChanged, onConnectionStatus, pickSshFile, removeConnection, saveConnection } from '../../../services/connections'

export function RemoteConnectionsPane(): ReactNode {
  const { t } = useI18n()
  const [items, setItems] = useState<Awaited<ReturnType<typeof listConnections>>>([])
  const [editing, setEditing] = useState<{ profile: ConnectionProfile | null; hasPassword: boolean } | undefined>(undefined)
  const [confirmTest, setConfirmTest] = useState<ConnectionProfile | null>(null)
  const [deleting, setDeleting] = useState<ConnectionProfile | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [failure, setFailure] = useState<string | null>(null)
  const [failureDetail, setFailureDetail] = useState<string | undefined>(undefined)
  const requestRef = useRef<string | null>(null)
  useEffect(() => {
    let alive = true
    const load = (): void => { void listConnections().then((next) => { if (alive) setItems(next) }).catch((error: unknown) => { if (alive) { setFailure(connectionErrorKey(error)); setFailureDetail(connectionErrorDetail(error)) } }) }
    load()
    const off = onConnectionsChanged(load)
    const offStatus = onConnectionStatus((status) => setItems((current) => current.map((item) => item.profile.id === status.connectionId ? { ...item, status } : item)))
    return () => { alive = false; off(); offStatus(); if (requestRef.current) void cancelConnectionRequest(requestRef.current).catch(() => {}) }
  }, [])
  const run = async (id: string, action: () => Promise<unknown>): Promise<void> => {
    setBusy(id); setFailure(null); setFailureDetail(undefined)
    try { await action() } catch (error) { setFailure(connectionErrorKey(error)); setFailureDetail(connectionErrorDetail(error)) } finally { setBusy(null) }
  }
  const test = (): void => {
    if (!confirmTest) return
    const profile = confirmTest
    setConfirmTest(null)
    const requestId = crypto.randomUUID()
    requestRef.current = requestId
    void run(profile.id, async () => {
      const directory = await connectForBrowse(profile.id, requestId, true)
      await closeBrowse(directory.browseId)
      requestRef.current = null
    })
  }
  return <section className="flex flex-col gap-4">
    <header className="flex items-center justify-between gap-3">
      <h3 className="text-[14px] font-medium text-fg">{t('ssh.title')}</h3>
      <Button type="button" variant="secondary" size="sm" onClick={() => setEditing({ profile: null, hasPassword: false })}><Plus size={13} />{t('ssh.add')}</Button>
    </header>
    {failure && <p role="alert" className="text-[12px] text-danger">{t(failure)}</p>}
    {failureDetail && <p className="break-all font-mono text-[11px] text-fg-faint">{failureDetail}</p>}
    <div className="divide-y divide-border">
      {items.length === 0 && <p className="py-8 text-center text-[13px] text-fg-faint">{t('ssh.empty')}</p>}
      {items.map(({ profile, status, hasPassword }) => <div key={profile.id} className="flex items-center gap-3 py-3">
        <Server size={17} className="shrink-0 text-icon" />
        <div className="min-w-0 flex-1">
          <div className="truncate text-[13px] text-fg">{profile.name}</div>
          <div className="truncate font-mono text-[11px] text-fg-faint">{profile.target.kind === 'manual' && profile.target.username ? `${profile.target.username}@` : ''}{profile.target.host}</div>
          <div className="text-[11px] text-fg-muted">{t(`ssh.phase.${status.phase}`)}</div>
        </div>
        <label className="flex items-center gap-1.5 text-[12px] text-fg-muted">
          <input type="checkbox" checked={profile.enabled} disabled={busy !== null} onChange={(event) => { void run(profile.id, () => saveConnection({ ...profile, enabled: event.target.checked })) }} />{t('ssh.enabled')}
        </label>
        <IconButton label={t(status.phase === 'ready' ? 'ssh.disconnect' : 'ssh.test')} disabled={busy !== null || !profile.enabled}
          onClick={() => { if (status.phase === 'ready') void run(profile.id, () => disconnectConnection(profile.id)); else setConfirmTest(profile) }}>
          {busy === profile.id ? <Spinner size="sm" /> : status.phase === 'ready' ? <Unplug size={14} /> : <Cable size={14} />}
        </IconButton>
        <IconButton label={t('ssh.edit')} disabled={busy !== null} onClick={() => setEditing({ profile, hasPassword })}><Pencil size={14} /></IconButton>
        <IconButton label={t('ssh.remove')} disabled={busy !== null} onClick={() => setDeleting(profile)}><Trash2 size={14} /></IconButton>
      </div>)}
    </div>
    {busy && requestRef.current && <Button type="button" variant="secondary" size="sm" onClick={() => { if (requestRef.current) void cancelConnectionRequest(requestRef.current) }}>{t('common.cancel')}</Button>}
    {editing !== undefined && <ServerEditor key={editing.profile?.id ?? 'new'} editing={editing.profile} hasPassword={editing.hasPassword} onClose={() => setEditing(undefined)} />}
    <Dialog open={confirmTest !== null} title={t('ssh.native.title')} onClose={() => setConfirmTest(null)} footer={<>
      <Button type="button" variant="secondary" size="sm" onClick={() => setConfirmTest(null)}>{t('common.cancel')}</Button><Button type="button" size="sm" variant="primary" onClick={test}>{t('ssh.native.allow')}</Button>
    </>}><p className="mb-3 break-all text-[13px] text-fg">{confirmTest?.name} · {confirmTest?.target.host}</p><p className="text-[12px] leading-relaxed text-fg-muted">{t('ssh.native.risk')}</p></Dialog>
    <Dialog open={deleting !== null} title={t('ssh.remove')} onClose={() => setDeleting(null)} footer={<>
      <Button type="button" variant="secondary" size="sm" onClick={() => setDeleting(null)}>{t('common.cancel')}</Button><Button type="button" size="sm" variant="danger" onClick={() => {
        if (deleting) { const id = deleting.id; setDeleting(null); void run(id, () => removeConnection(id)) }
      }}>{t('common.delete')}</Button>
    </>}><p className="text-[13px] text-fg-muted">{t('ssh.removeConfirm', { name: deleting?.name ?? '' })}</p></Dialog>
  </section>
}

export function ServerEditor({ editing, hasPassword, onClose }: { editing: ConnectionProfile | null; hasPassword: boolean; onClose(): void }): ReactNode {
  const { t } = useI18n()
  const target = editing?.target
  const [mode, setMode] = useState<'manual' | 'config'>(target?.kind ?? 'manual')
  const [authMethod, setAuthMethod] = useState<SshAuthMethod>(editing ? editing.authMethod ?? 'auto' : 'password')
  const [name, setName] = useState(editing?.name ?? '')
  const [host, setHost] = useState(target?.host ?? '')
  const [username, setUsername] = useState(target?.kind === 'manual' ? target.username ?? '' : '')
  const [port, setPort] = useState(target?.kind === 'manual' ? String(target.port ?? 22) : '22')
  const [identity, setIdentity] = useState(target?.identityFile ?? '')
  const [proxyJump, setProxyJump] = useState(target?.kind === 'manual' ? target.proxyJump ?? '' : '')
  const [configFile, setConfigFile] = useState(target?.kind === 'config' ? target.configFile ?? '' : '')
  const [platform, setPlatform] = useState<ConnectionProfile['platform']>(editing?.platform ?? 'auto')
  /**
   * ★ 已存的密码**不回填** —— 它根本没离开过主进程,这里只知道"有没有"。
   * 所以空串的含义是「不动已存的那份」,清除要靠 `clearPassword` 显式表达。
   */
  const [password, setPassword] = useState('')
  const [clearPassword, setClearPassword] = useState(false)
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<string | null>(null)
  const [targetChange, setTargetChange] = useState(false)
  const [confirmed, setConfirmed] = useState(false)
  const usesPassword = authMethod === 'password' || authMethod === 'auto'
  const usesIdentity = authMethod === 'key' || authMethod === 'auto'
  const validPort = Number.isInteger(Number(port)) && Number(port) >= 1 && Number(port) <= 65535
  const valid = name.trim() && host.trim() && (mode === 'config' || (username.trim() && validPort && (authMethod !== 'key' || identity.trim())))
  const save = async (): Promise<void> => {
    if (busy || !valid || (targetChange && !confirmed)) return
    setBusy(true); setFailure(null)
    const identityTarget = usesIdentity && identity.trim() ? { identityFile: identity.trim() } : {}
    const profile: ConnectionProfileInput = { kind: 'ssh', name, platform, authMethod, enabled: editing?.enabled ?? true,
      ...(editing ? { id: editing.id, revision: editing.revision } : {}), confirmTargetChange: confirmed,
      // undefined = 不动已存的;null = 清除;非空 = 写入
      ...(usesPassword ? (clearPassword ? { password: null } : password !== '' ? { password } : {}) : {}),
      target: mode === 'config' ? { kind: 'config', host: host.trim(), ...identityTarget, ...(configFile.trim() ? { configFile: configFile.trim() } : {}) }
        : { kind: 'manual', host, port: Number(port), username: username.trim(),
          ...identityTarget,
          ...(proxyJump ? { proxyJump } : {}) } }
    try { await saveConnection(profile); onClose() }
    catch (error) { const key = connectionErrorKey(error); setFailure(key); if (key === 'environment.error.approval-required') setTargetChange(true) }
    finally { setBusy(false) }
  }
  const filePicker = (value: string, update: (value: string) => void, label: string, required = false): ReactNode => <div className="flex min-w-0 items-center gap-2">
    <div className="min-w-0 flex-1"><TextInput value={value} onChange={update} ariaLabel={label} placeholder={t(required ? 'ssh.authMethod.keyPlaceholder' : 'ssh.optional')} /></div>
    <IconButton label={t('ssh.pickFile')} disabled={busy} onClick={() => { void pickSshFile().then((path) => { if (path) update(path) }).catch((error: unknown) => setFailure(connectionErrorKey(error))) }}><FolderOpen size={14} /></IconButton>
  </div>
  const field = (label: string, child: ReactNode, hint?: string): ReactNode => <div className="grid min-w-0 gap-2 px-4 py-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1.2fr)] sm:items-start sm:gap-6">
    <div className="pt-1.5"><div className="text-[13px] font-medium text-fg">{label}</div>{hint && <p className="mt-1 text-[11px] leading-relaxed text-fg-faint">{hint}</p>}</div>
    <div className="min-w-0">{child}</div>
  </div>
  return <Dialog open title={t(editing ? 'ssh.edit' : 'ssh.add')} onClose={() => { if (!busy) onClose() }} width={680} footer={<>
    <Button type="button" variant="secondary" size="sm" onClick={onClose} disabled={busy}>{t('common.cancel')}</Button>
    <Button type="button" size="sm" variant="primary" disabled={busy || !valid || (targetChange && !confirmed)} onClick={() => { void save() }}>{busy ? <Spinner size="sm" /> : undefined}{t('common.save')}</Button>
  </>}>
    <fieldset disabled={busy} className="flex min-w-0 flex-col gap-4" onChangeCapture={() => setConfirmed(false)}>
      <section aria-label={t('ssh.basicInfo')} className="divide-y divide-border overflow-hidden rounded-[10px] border border-border">
        {field(t('ssh.name'), <TextInput value={name} onChange={setName} ariaLabel={t('ssh.name')} />)}
        {field(t('ssh.mode'), <fieldset disabled={busy} className="contents"><SegmentedControl label={t('ssh.mode')} value={mode} options={[{ value: 'manual', label: t('ssh.manual') }, { value: 'config', label: t('ssh.nativeConfig') }]} onValueChange={(value) => { setMode(value as 'manual' | 'config'); setConfirmed(false) }} /></fieldset>)}
        {field(t(mode === 'config' ? 'ssh.alias' : 'ssh.host'), <div className="flex min-w-0 gap-2">
          <div className="min-w-0 flex-1"><TextInput value={host} onChange={setHost} ariaLabel={t(mode === 'config' ? 'ssh.alias' : 'ssh.host')} /></div>
          {mode === 'manual' && <input type="number" min={1} max={65535} value={port} aria-label={t('ssh.port')} aria-invalid={!validPort || undefined} onChange={(event) => setPort(event.target.value)} className="apple-field selectable h-8 w-[72px] shrink-0 rounded-[8px] border border-border bg-surface-field px-2.5 text-[13px] text-fg outline-none focus:border-fg-faint aria-invalid:border-danger" />}
        </div>, t(mode === 'config' ? 'ssh.configHint' : 'ssh.addressHint'))}
        {mode === 'config' && field(t('ssh.configFile'), filePicker(configFile, setConfigFile, t('ssh.configFile')))}
      </section>

      <section aria-label={t('ssh.authMethod')} className="divide-y divide-border overflow-hidden rounded-[10px] border border-border">
        {field(t('ssh.authMethod'), <Select inModal disabled={busy} value={authMethod} ariaLabel={t('ssh.authMethod')} className="h-8 text-[13px]"
          options={(['password', 'key', 'auto', 'interactive', 'ask'] as const).map((value) => ({ value, label: t(`ssh.authMethod.${value}`) }))}
          onValueChange={(value) => { setAuthMethod(value as SshAuthMethod); setConfirmed(false) }} />, t(`ssh.authMethod.${authMethod}Hint`))}
        {mode === 'manual' && field(t('ssh.username'), <TextInput value={username} onChange={setUsername} ariaLabel={t('ssh.username')} />)}
        {usesIdentity && field(t('ssh.identityFile'), <div className="flex flex-col gap-2">
          {filePicker(identity, setIdentity, t('ssh.identityFile'), authMethod === 'key' && mode === 'manual')}
          {authMethod === 'key' && <p className="text-[11px] leading-relaxed text-fg-faint">{t('ssh.authMethod.passphraseHint')}</p>}
        </div>)}
        {usesPassword && field(t('ssh.password'), <div className="flex min-w-0 flex-col gap-1.5">
            <div className="flex min-w-0 items-center gap-2">
              <input type="password" autoComplete="off" value={password} aria-label={t('ssh.password')} disabled={clearPassword}
                placeholder={t(hasPassword && !clearPassword ? 'ssh.passwordSaved' : 'ssh.optional')}
                onChange={(event) => setPassword(event.target.value)}
                className="apple-field selectable h-8 min-w-0 flex-1 rounded-[8px] border border-border bg-surface-field px-2.5 text-[13px] text-fg outline-none focus:border-fg-faint disabled:opacity-40" />
              {hasPassword && !clearPassword && <Button type="button" variant="secondary" size="sm" onClick={() => { setPassword(''); setClearPassword(true) }}>{t('ssh.passwordClear')}</Button>}
            </div>
            <p className="text-[11px] leading-relaxed text-fg-faint">{t(clearPassword ? 'ssh.passwordCleared' : 'ssh.passwordHint')}</p>
        </div>)}
      </section>

      <section aria-label={t('ssh.connectionOptions')} className="divide-y divide-border overflow-hidden rounded-[10px] border border-border">
        {mode === 'manual' && field(t('ssh.proxyJump'), <TextInput value={proxyJump} onChange={setProxyJump} ariaLabel={t('ssh.proxyJump')} placeholder={t('ssh.optional')} />)}
        {field(t('ssh.platform'), <Select inModal disabled={busy} value={platform} ariaLabel={t('ssh.platform')} className="h-8 text-[13px]"
          options={[{ value: 'auto', label: t('ssh.autoPlatform') }, { value: 'linux', label: 'Linux' }, { value: 'darwin', label: 'macOS' }, { value: 'win32', label: 'Windows' }]}
          onValueChange={(value) => { setPlatform(value as ConnectionProfile['platform']); setConfirmed(false) }} />)}
      </section>
      {targetChange && <><p className="text-[12px] text-danger">{t('ssh.targetChange')}</p><label className="flex items-center gap-2 text-[12px] text-fg"><input type="checkbox" checked={confirmed} onChange={(event) => setConfirmed(event.target.checked)} />{t('ssh.confirmTargetChange')}</label></>}
      {failure && <p role="alert" className="text-[12px] text-danger">{t(failure)}</p>}
    </fieldset>
  </Dialog>
}
