import { Check, ChevronDown, Copy, Laptop, Loader, MonitorSmartphone, WifiOff } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import type { DeviceConnectionState, DeviceInvitationPreview, DeviceSummary } from '../../../../shared/domain/device-connection'
import { Button } from '../../components/arc/button/button'
import { ActionIconButton, useTransientStatus } from '../../components/ui/ActionIconButton'
import { Collapsible, CollapsibleContent, CollapsibleIndicator, CollapsibleTrigger } from '../../components/ui/Collapsible'
import { TextInput } from '../../components/ui/TextInput'
import { useI18n, type Translate, type TranslationKey } from '../../i18n'
import { cn } from '../../lib/cn'
import { useDeviceStore } from '../../stores/device'
import { copyText } from '../../services/app'
import { inspectDeviceInvitation } from '../../services/device'
import { SettingGroup } from '../Row'

const STATE_KEY = {
  idle: 'devices.state.idle', connecting: 'devices.state.connecting', online: 'devices.state.online', offline: 'devices.state.offline'
} as const satisfies Record<DeviceConnectionState, string>

/** 状态点的配色。`idle` 是不在场的灰,不是「正常」的绿。 */
const STATE_TONE = {
  idle: 'text-fg-faint', connecting: 'text-warning', online: 'text-accent', offline: 'text-danger'
} as const satisfies Record<DeviceConnectionState, string>

const errorText = (error: unknown): string => error instanceof Error ? error.message : String(error)

/**
 * 在一台新设备上把服务装起来的三步。
 *
 * ★ 三条命令**原样可复制**。这一页真正会失败的地方不是点错按钮,而是照着文档敲错一条命令 ——
 *   所以命令就写在这一页上,每条配一颗复制按钮,用户不必先去翻包里的 README。
 * ★ 文本与 `docs/device-service.md` 是同一份:界面上一处、文档里一处,不一致时用户会以界面上的为准。
 */
const INSTALL_STEPS: ReadonlyArray<{ title: TranslationKey; note: TranslationKey; command: string }> = [
  {
    title: 'devices.installStep1',
    note: 'devices.installStep1Note',
    command: 'npm i -g @aidotnet/nextcowork-device-service'
  },
  {
    title: 'devices.installStep2',
    note: 'devices.installStep2Note',
    command: 'ncw-device-service diagnose'
  },
  {
    title: 'devices.installStep3',
    note: 'devices.installStep3Note',
    command: 'ncw-device-service --data-dir ~/ncw-device --host 0.0.0.0 --port 7801 --pairing-port 7800 --advertise nas.lan --invite'
  }
]

/** 把指纹按 4 位一组展示,方便和设备上显示的逐位核对。 */
export const groupFingerprint = (fingerprint: string): string => fingerprint.replace(/(.{4})/g, '$1 ').trim()

/**
 * 设备页(控制端本地):一张卡 = 一台设备。
 *
 * ★ 配对分两步:先「检查邀请」(不联网,只读出设备指纹),用户核对指纹之后才「配对」——
 *   主进程会再校验这个指纹与邀请一致,所以界面展示的和实际信任的是同一把根。
 * ★ 「移除连接」只删这台电脑上的凭据,文案里直说不会撤销设备上的配对。
 * ★ 设备离线时只标明「离线」,不提供也不暗示回退到本机运行。
 * ★ 卡片的高亮只表达一件事:**现在用的是谁**。状态另有自己的状态点,两者不用同一个颜色,
 *   否则「在线」和「当前」会在同一张卡上抢同一种绿。
 */
export function DevicesPage(): ReactNode {
  const { t } = useI18n()
  const state = useDeviceStore((s) => s.state)
  const loadError = useDeviceStore((s) => s.error)
  const load = useDeviceStore((s) => s.load)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  /*
    安装步骤的展开状态。`null` = 还没被用户点过,这时按「有没有设备」决定:
    一台都没有的人正要的就是这一段,而已经有设备的人多半是来粘贴邀请的。
    ★ 用户点过之后就以他的选择为准 —— 让清单的变化把刚展开的那段收回去,是最讨厌的一种动。
  */
  const [stepGuide, setStepGuide] = useState<boolean | null>(null)

  useEffect(() => { void load() }, [load])

  const run = async (action: () => Promise<void>): Promise<void> => {
    setBusy(true)
    setError(null)
    try { await action() } catch (cause) { setError(errorText(cause)) } finally { setBusy(false) }
  }

  const current = state?.current
  const local = current?.deviceId === null || current === undefined
  return (
    <div className="flex flex-col gap-2">
      <p className="px-1 text-[12px] leading-[1.5] text-fg-muted">{t('devices.scopeNote')}</p>
      {(error !== null || loadError !== null) && (
        <div role="alert" className="rounded-[10px] border border-danger/35 px-3 py-2 text-[12px] text-fg-muted">
          <span className="text-fg">{loadError !== null ? t('devices.errorLoad') : t('devices.error')}</span>
          {`: ${error ?? loadError ?? ''}`}
        </div>
      )}
      <SettingGroup title={t('devices.title')}>
        <ul className="flex flex-col gap-2 pt-1 pb-2">
          <li>
            <DeviceCard
              icon={<Laptop size={17} />}
              // 本机这张卡只有一个状态:现在是不是在用它。连线状态属于设备。
              title={<span className="truncate text-[13px] text-fg">{t('devices.thisComputer')}</span>}
              selected={local}
              meta={<span className="truncate">{t('devices.thisComputerHint')}</span>}
              actions={local
                ? <CurrentBadge t={t} />
                : <Button type="button" variant="secondary" size="sm" disabled={busy} onClick={() => void run(() => useDeviceStore.getState().useLocal())}>{t('devices.useLocal')}</Button>}
            />
          </li>
          {state?.devices.map((device) => (
            <li key={device.id}>
              <DeviceRow device={device} busy={busy} t={t} run={run} />
            </li>
          ))}
          {state !== null && state.devices.length === 0 && (
            <li className="flex flex-col items-center justify-center gap-1.5 rounded-card border border-dashed border-border bg-surface-raised px-6 py-6 text-center">
              <MonitorSmartphone aria-hidden size={18} className="text-fg-faint" />
              <span className="text-[12px] text-fg-muted">{t('devices.empty')}</span>
              {/* 空的时候不能只留一句「还没有」—— 用户下一步该干什么,必须写在同一张卡上。 */}
              <span className="text-[11.5px] leading-[1.5] text-fg-faint">{t('devices.emptyHint')}</span>
            </li>
          )}
        </ul>
      </SettingGroup>
      {/* 一台都没有 = 打开这一段:它是这一页唯一的「从零开始」。 */}
      <AddDevice busy={busy} t={t} run={run} guideOpen={stepGuide ?? state?.devices.length === 0} onGuideOpenChange={setStepGuide} />
    </div>
  )
}

/**
 * 一张卡的壳子:**图标 + 标题行 + 元信息 + 右侧动作**,下面可以就地长出展开区。
 *
 * ★ 展开区(中转设置、移除确认)在动作行**外面**:它们不是动作,塞进同一行会让
 *   那一行在展开时重新排一遍,用户刚点的那颗按钮会跳到别处。
 */
function DeviceCard({ icon, title, meta, actions, selected, children }: {
  icon: ReactNode
  title: ReactNode
  meta?: ReactNode
  actions: ReactNode
  selected: boolean
  children?: ReactNode
}): ReactNode {
  return (
    <div className={cn(
      'rounded-card border px-3 py-2.5 transition-colors',
      selected ? 'border-accent/40 bg-tint/55' : 'border-border hover:border-hairline'
    )}>
      <div className="flex items-start gap-3">
        <span aria-hidden className="mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-[10px] bg-surface-sunken text-icon">{icon}</span>
        <div className="min-w-0 flex-1">
          <div className="flex min-w-0 items-center gap-2">{title}</div>
          {meta !== undefined && (
            <div className="mt-1 flex flex-wrap items-center gap-x-1.5 gap-y-0.5 text-[11.5px] leading-[1.5] text-fg-muted">{meta}</div>
          )}
        </div>
        <div className="flex shrink-0 items-center gap-1.5 pt-0.5">{actions}</div>
      </div>
      {children}
    </div>
  )
}

/** 「当前」。和状态点分开:它是选择,不是连接状态。 */
function CurrentBadge({ t }: { t: Translate }): ReactNode {
  return (
    <span className="rounded-pill bg-accent/15 px-2 py-0.5 text-[11px] text-accent">{t('devices.current')}</span>
  )
}

/** 连接状态:一个点 + 一句话。连线中会转,离线在悬停时给出原因不存在这里 —— 原因在卡片元信息里。 */
function StatePill({ state, t }: { state: DeviceConnectionState; t: Translate }): ReactNode {
  const tone = STATE_TONE[state]
  return (
    <span className={cn('inline-flex shrink-0 items-center gap-1 rounded-pill bg-tint px-1.5 py-0.5 text-[11px]', tone)}>
      {state === 'connecting'
        ? <Loader aria-hidden size={10} className="animate-spin" />
        : state === 'offline'
          ? <WifiOff aria-hidden size={10} />
          : <span aria-hidden className="size-1.5 rounded-pill bg-current" />}
      {t(STATE_KEY[state])}
    </span>
  )
}

const Dot = (): ReactNode => <span aria-hidden className="text-fg-faint">·</span>

function DeviceRow({ device, busy, t, run }: {
  device: DeviceSummary; busy: boolean; t: Translate; run: (action: () => Promise<void>) => Promise<void>
}): ReactNode {
  const [confirming, setConfirming] = useState(false)
  const [editingRelay, setEditingRelay] = useState(false)
  const [relayUrl, setRelayUrl] = useState(device.relay ?? '')
  const [relayCa, setRelayCa] = useState('')
  const store = useDeviceStore.getState
  return (
    <DeviceCard
      icon={<MonitorSmartphone size={17} />}
      selected={device.selected}
      title={(
        <>
          <span className="truncate text-[13px] text-fg">{device.label}</span>
          <StatePill state={device.state} t={t} />
          {device.selected && <CurrentBadge t={t} />}
        </>
      )}
      meta={(
        <>
          <span className="truncate">{t('devices.endpoints', { value: device.endpoints.join(', ') })}</span>
          <Dot /><span>{t('devices.pairedAt', { value: new Date(device.pairedAt).toLocaleDateString() })}</span>
          {device.relay !== null && <><Dot /><span className="truncate">{t('devices.relay', { value: device.relay })}</span></>}
        </>
      )}
      actions={(
        <>
          {!device.selected && (
            <Button type="button" variant="secondary" size="sm" disabled={busy} onClick={() => void run(() => store().select(device.id))}>{t('devices.use')}</Button>
          )}
          <Button type="button" variant="ghost" size="sm" disabled={busy} onClick={() => { setConfirming(false); setEditingRelay((open) => !open) }}>{t('devices.relayEdit')}</Button>
          <Button type="button" variant="ghost" size="sm" disabled={busy} onClick={() => { setEditingRelay(false); setConfirming(true) }}>{t('devices.remove')}</Button>
        </>
      )}
    >
      {confirming && (
        <div className="mt-2.5 flex flex-wrap items-center gap-2 rounded-[10px] bg-tint px-3 py-2.5">
          <p className="min-w-[12rem] flex-1 text-[11.5px] leading-[1.5] text-fg-muted">
            <span className="text-fg">{t('devices.removeConfirm', { name: device.label })}</span>
            {' '}{t('devices.removeNote')}
          </p>
          <Button type="button" variant="danger" size="sm" disabled={busy}
            onClick={() => void run(async () => { await store().remove(device.id); setConfirming(false) })}>{t('devices.remove')}</Button>
          <Button type="button" variant="secondary" size="sm" onClick={() => setConfirming(false)}>{t('devices.cancel')}</Button>
        </div>
      )}
      {editingRelay && (
        <div className="mt-2.5 flex flex-col gap-2 rounded-[10px] bg-tint px-3 py-2.5">
          <p className="text-[11.5px] leading-[1.5] text-fg-muted">{t('devices.relayNote')}</p>
          <TextInput value={relayUrl} onChange={setRelayUrl} placeholder="wss://relay.example.com" ariaLabel={t('devices.relayUrl')} disabled={busy} />
          <textarea
            value={relayCa}
            onChange={(event) => setRelayCa(event.target.value)}
            rows={3}
            spellCheck={false}
            aria-label={t('devices.relayCa')}
            placeholder={t('devices.relayCa')}
            disabled={busy}
            className="selectable w-full rounded-md border border-hairline bg-surface-field px-3 py-2 text-[12px] text-fg"
          />
          <div className="flex gap-2">
            <Button type="button" variant="primary" size="sm" disabled={busy}
              onClick={() => void run(async () => { await store().setRelay(device.id, relayUrl.trim() === '' ? null : relayUrl.trim(), relayCa); setEditingRelay(false) })}>{t('devices.relaySave')}</Button>
            <Button type="button" variant="secondary" size="sm" disabled={busy || device.relay === null}
              onClick={() => void run(async () => { await store().setRelay(device.id, null); setRelayUrl(''); setRelayCa(''); setEditingRelay(false) })}>{t('devices.relayClear')}</Button>
          </div>
        </div>
      )}
    </DeviceCard>
  )
}

function AddDevice({ busy, t, run, guideOpen, onGuideOpenChange }: {
  busy: boolean
  t: Translate
  run: (action: () => Promise<void>) => Promise<void>
  guideOpen: boolean
  onGuideOpenChange: (open: boolean) => void
}): ReactNode {
  const [text, setText] = useState('')
  const [preview, setPreview] = useState<DeviceInvitationPreview | null>(null)
  const [label, setLabel] = useState('')

  const reset = (): void => { setText(''); setPreview(null); setLabel('') }
  return (
    <SettingGroup title={t('devices.add')}>
      <p className="px-1 pb-2 text-[12px] leading-[1.5] text-fg-muted">{t('devices.addDescription')}</p>
      <div className="flex flex-col gap-3 px-1 pb-2">
        <TextInput
          value={text}
          // 改了邀请文本,之前核对过的指纹就不再对应它 —— 必须重新检查。
          onChange={(value) => { setText(value); setPreview(null) }}
          placeholder={t('devices.invitationPlaceholder')}
          ariaLabel={t('devices.invitation')}
          disabled={busy}
        />
        {preview === null ? (
          <div>
            <Button type="button" variant="secondary" size="sm" disabled={busy || text.trim() === ''}
              onClick={() => void run(async () => { setPreview(await inspectDeviceInvitation(text.trim())) })}>{t('devices.inspect')}</Button>
          </div>
        ) : (
          <div className="flex flex-col gap-3 rounded-card border border-border px-3 py-3">
            <div>
              <p className="text-[12px] text-fg-muted">{t('devices.fingerprint')}</p>
              <code className="selectable mt-1 block break-all rounded-md border border-hairline px-3 py-2 text-[12.5px] text-fg" aria-label={t('devices.fingerprint')}>{groupFingerprint(preview.fingerprint)}</code>
              <p className="mt-1 text-[12px] leading-[1.5] text-fg-muted">{t('devices.fingerprintHint')}</p>
              <p className="mt-1 text-[12px] text-fg-muted">{t('devices.endpoints', { value: preview.endpoints.join(', ') })}</p>
            </div>
            <TextInput value={label} onChange={setLabel} placeholder={t('devices.namePlaceholder')} ariaLabel={t('devices.name')} disabled={busy} />
            <div className="flex gap-2">
              <Button type="button" variant="primary" size="sm" disabled={busy || label.trim() === ''}
                onClick={() => void run(async () => { await useDeviceStore.getState().pair(text.trim(), label.trim(), preview.fingerprint); reset() })}>
                {busy ? t('devices.pairing') : t('devices.confirmPair')}
              </Button>
              <Button type="button" variant="secondary" size="sm" disabled={busy} onClick={reset}>{t('devices.cancel')}</Button>
            </div>
          </div>
        )}
      </div>
      {/*
        ★ 安装步骤放在粘贴框**下面**:已经有邀请的人先看到该粘的地方,而正要装一台新设备的人
          看到的是展开着的一段。顺序反过来就成了「每次都先读一遍安装教程才能粘贴」。
      */}
      <div className="border-t border-hairline px-1">
        <DeviceInstallGuide open={guideOpen} onOpenChange={onGuideOpenChange} t={t} />
      </div>
    </SettingGroup>
  )
}

/**
 * 「还没装过服务」的那一段。三步 + 三条可复制的命令 + 一句回到上面输入框的收尾。
 *
 * ★ 它**不联网、也不探测**:界面判断不出你手上那台机器装没装、装的是哪个版本,
 *   所以这里只写步骤,不写「已安装」这种界面猜出来的状态。
 */
function DeviceInstallGuide({ open, onOpenChange, t }: {
  open: boolean
  onOpenChange: (open: boolean) => void
  t: Translate
}): ReactNode {
  return (
    <Collapsible open={open} onOpenChange={onOpenChange}>
      <CollapsibleTrigger className="py-2.5">
        <div className="min-w-0 flex-1">
          <p className="text-[12.5px] text-fg">{t('devices.installTitle')}</p>
          <p className="mt-0.5 text-[11.5px] leading-[1.6] text-fg-muted">{t('devices.installHint')}</p>
        </div>
        <CollapsibleIndicator><ChevronDown size={13} /></CollapsibleIndicator>
      </CollapsibleTrigger>
      <CollapsibleContent>
        <ol className="flex flex-col gap-3 pt-1 pb-2">
          {INSTALL_STEPS.map((step, index) => (
            <li key={step.title} className="flex gap-2.5">
              {/* 编号只是「这是第几步」的视觉锚点,顺序本身由 `<ol>` 给出。 */}
              <span aria-hidden className="mt-px flex size-[17px] shrink-0 items-center justify-center rounded-pill bg-tint text-[10.5px] tabular-nums text-fg-muted">
                {index + 1}
              </span>
              <div className="min-w-0 flex-1">
                <p className="text-[12.5px] text-fg">{t(step.title)}</p>
                <p className="mt-0.5 text-[11.5px] leading-[1.6] text-fg-muted">{t(step.note)}</p>
                <CommandLine command={step.command} testId={`device-install-command-${String(index + 1)}`} t={t} />
              </div>
            </li>
          ))}
        </ol>
        <p className="pb-1 text-[11.5px] leading-[1.6] text-fg-muted">{t('devices.installFinish')}</p>
        <p className="pb-3 text-[11.5px] leading-[1.6] text-fg-faint">{t('devices.installMore')}</p>
      </CollapsibleContent>
    </Collapsible>
  )
}

/**
 * 一条可复制的命令。
 *
 * ★ 复制失败**不弹错误**:命令就在左边明晃晃地写着,手动选中即可 —— 为一次复制失败
 *   弹一条红字,只会让人以为这一步本身坏了(同 `ProviderPanel` 里那段)。
 */
function CommandLine({ command, testId, t }: { command: string; testId: string; t: Translate }): ReactNode {
  const [status, setStatus] = useTransientStatus()
  return (
    <div className="mt-1.5 flex items-start gap-1.5">
      {/* 换行按词边界断(`break-words`),不是 `break-all`:命令是照着读/对照敲的,
          `--pairing-port` 被切成 `--pairing-po` / `rt` 就白写了这一行。 */}
      <code className="selectable min-w-0 flex-1 break-words rounded-md border border-hairline bg-surface-field px-2.5 py-1.5 font-mono text-[11.5px] leading-[1.6] text-fg">
        {command}
      </code>
      <span className="mt-0.5 shrink-0">
        <ActionIconButton
          testId={testId}
          label={t(status === 'failed' ? 'devices.copyFailed' : status === 'done' ? 'devices.copied' : 'common.copy')}
          onClick={() => { void copyText(command).then(() => setStatus('done')).catch(() => setStatus('failed')) }}
        >
          {status === 'done' ? <Check size={12} /> : <Copy size={12} />}
        </ActionIconButton>
      </span>
    </div>
  )
}
