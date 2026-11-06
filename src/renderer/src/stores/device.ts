/**
 * 设备清单与当前选择的渲染层缓存。主进程是唯一事实来源,这里只存最近一次它给的状态。
 *
 * 照 `stores/mcp.ts`:订阅在第一次 `load()` 时装(顶层 `on()` 会在 import 时碰 `window.nextcowork`)。
 * 每个动作都以主进程返回的**完整状态**覆盖本地,不在本地推演(例如切换后「谁在线」),
 * 所以掉线/重连/被别的窗口改选都不会让这里和真实状态分岔。
 */
import { create } from 'zustand'
import type { DevicesState } from '../../../shared/domain/device-connection'
import { getDevicesState, onDevicesChanged, pairDevice, removeDevice, selectDevice, setDeviceRelay, useLocalDevice } from '../services/device'

interface DeviceStoreState {
  state: DevicesState | null
  /** 读取失败的原因;`null` = 没失败。空清单和「读不出来」是两回事。 */
  error: string | null
  load: () => Promise<void>
  pair: (text: string, label: string, confirmedFingerprint: string) => Promise<void>
  select: (id: string) => Promise<void>
  useLocal: () => Promise<void>
  remove: (id: string) => Promise<void>
  setRelay: (id: string, url: string | null, caPem?: string) => Promise<void>
}

let subscribed = false
/** 每收到一次广播加一;`load()` 发起时记下它,回来时若变了说明有更新的状态已经到了,旧的读取结果不能盖上去。 */
let broadcasts = 0
function subscribeOnce(): void {
  if (subscribed) return
  subscribed = true
  onDevicesChanged((state) => { broadcasts += 1; useDeviceStore.setState({ state, error: null }) })
}

const messageOf = (error: unknown): string => error instanceof Error ? error.message : String(error)

export const useDeviceStore = create<DeviceStoreState>((set) => ({
  state: null,
  error: null,

  async load() {
    subscribeOnce()
    const seen = broadcasts
    try {
      const state = await getDevicesState()
      if (broadcasts === seen) set({ state, error: null })
    } catch (error) {
      console.error('[device] 设备清单读取失败:', error)
      set({ error: messageOf(error) })
    }
  },
  async pair(text, label, confirmedFingerprint) { set({ state: await pairDevice(text, label, confirmedFingerprint), error: null }) },
  async select(id) { set({ state: await selectDevice(id), error: null }) },
  async useLocal() { set({ state: await useLocalDevice(), error: null }) },
  async remove(id) { set({ state: await removeDevice(id), error: null }) },
  async setRelay(id, url, caPem) { set({ state: await setDeviceRelay(id, url, caPem), error: null }) }
}))
