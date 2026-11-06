/**
 * 侧边栏底部的设备切换器:这台电脑 ↔ 已配对设备。
 *
 * - 只在配对过设备(或当前指向了远程设备)时出现。
 * - 选中远程设备离线时,触发器带警示点 —— 此时需要设备的操作会被拒绝而**不会**落到本机,
 *   用户得看得出为什么。「这台电脑」永远是菜单里的第一项,是离线时的逃生口。
 * - 选择的事实来源是主进程(`stores/device.ts` 只存它返回的状态),这里不推演。
 */
import { MonitorSmartphone } from 'lucide-react'
import { useEffect, type ReactNode } from 'react'
import { Menu, MenuItem, MenuLabel, MenuSeparator } from '../components/ui/Menu'
import { useI18n, type Translate } from '../i18n'
import { cn } from '../lib/cn'
import type { SettingsPageId } from '../settings/nav'
import { useDeviceStore } from '../stores/device'
import { toast } from '../stores/toast'
import { deviceSwitcherModel } from './device-switcher-model'

const STATE_KEY = {
  idle: 'devices.state.idle', connecting: 'devices.state.connecting', online: 'devices.state.online', offline: 'devices.state.offline'
} as const

export function DeviceSwitcher({ onOpenSettings }: { onOpenSettings: (page?: SettingsPageId) => void }): ReactNode {
  const { t } = useI18n()
  const state = useDeviceStore((s) => s.state)
  const load = useDeviceStore((s) => s.load)
  useEffect(() => { void load() }, [load])

  const model = deviceSwitcherModel(state)
  if (!model.visible || state === null) return null

  const run = (action: () => Promise<void>): void => {
    action().catch((error: unknown) => {
      toast.error(`${t('deviceSwitcher.switchFailed')}: ${error instanceof Error ? error.message : String(error)}`, 'device-switch')
    })
  }
  const name = model.label ?? t('devices.thisComputer')
  return (
    <Menu
      label={t('deviceSwitcher.label')}
      width={260}
      triggerClassName="h-8 max-w-[9rem] gap-1.5 px-2 text-[12px]"
      trigger={(
        <>
          <span className="relative shrink-0">
            <MonitorSmartphone size={15} />
            {model.warn && <span aria-hidden className="absolute -right-0.5 -top-0.5 size-1.5 rounded-full bg-danger" />}
          </span>
          <span className={cn('truncate', model.warn && 'text-danger')}>{name}</span>
        </>
      )}
    >
      {() => (
        <>
          <MenuLabel>{t('deviceSwitcher.label')}</MenuLabel>
          <MenuItem icon={<MonitorSmartphone size={15} />} description={t('devices.thisComputerHint')} checked={!model.remote}
            onSelect={() => run(() => useDeviceStore.getState().useLocal())}>
            {t('devices.thisComputer')}
          </MenuItem>
          {state.devices.map((device) => (
            <MenuItem key={device.id} icon={<MonitorSmartphone size={15} />} description={stateText(t, device.selected ? state.current.state : 'idle')}
              checked={state.current.deviceId === device.id}
              onSelect={() => run(() => useDeviceStore.getState().select(device.id))}>
              {device.label}
            </MenuItem>
          ))}
          <MenuSeparator />
          <MenuItem onSelect={() => onOpenSettings('devices')}>{t('deviceSwitcher.manage')}</MenuItem>
        </>
      )}
    </Menu>
  )
}

function stateText(t: Translate, state: keyof typeof STATE_KEY): string {
  return t(STATE_KEY[state])
}
