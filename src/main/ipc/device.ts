/**
 * 设备页的主进程接线(controller-local):已配对设备清单、配对、切换、移除。
 *
 * 管理器本身(`main/device/*`)不依赖 Electron;这里只装上两样平台相关的东西:
 * 系统钥匙串(`safeStorage`,存控制端私钥)和「变化 → 广播给所有窗口」。
 * 清单与私钥都在这台电脑的 `<userData>/controller/` 下,不进业务库。
 */
import { app, safeStorage } from 'electron'
import { join } from 'node:path'
import type { DevicesState } from '../../shared/domain/device-connection'
import type { InvokeChannel, InvokeReq, InvokeRes, IpcSendMap, SendChannel } from '../../shared/ipc/contract'
import { AgentBridge } from '../device/agent-bridge'
import { DeviceBroker, type DeviceBridges, type WindowTopics } from '../device/broker'
import { NodeDeviceConnector } from '../device/connector'
import { keychainSecretPort } from '../device/keychain-secrets'
import { DeviceManager } from '../device/manager'
import { DeviceRegistry } from '../device/registry'
import { TerminalBridge } from '../device/terminal-bridge'
import { terminalTopic, windows, type WindowContext } from '../window/registry'

let manager: DeviceManager | null = null
let bridges: DeviceBridges | null = null

/**
 * 用 Electron 的应用数据目录,**不是** `host.paths.userData()`:后者跟着账号配置作用域走(切账号会换目录),
 * 而「这台电脑配对了哪些设备」不该因为切账号就消失或串到另一个账号。
 */
function controllerDirectory(): string { return join(app.getPath('userData'), 'controller') }

/** 懒创建:第一次用到设备页(或有路由需求)时才读清单。清单损坏会在这里抛错并保持原文件。 */
export function deviceManager(): DeviceManager {
  if (manager !== null) return manager
  const dir = controllerDirectory()
  const registry = new DeviceRegistry(dir, keychainSecretPort(dir, safeStorage))
  /*
    ★ 远程选择暂时只在**本次运行内**有效:重启后回到本机。
    路由表目前只覆盖几条只读频道,其余设备类频道在选了设备后会被拒绝(见 `device/broker.ts`);
    如果启动时就自动恢复上次的远程选择,应用一打开就是一片被拒绝的界面。
    等路由表覆盖到整个应用,再把这里换成 `manager.restore()`(它已实现并有测试)。
  */
  if (registry.selected() !== null) registry.select(null)
  const connector = new NodeDeviceConnector(registry)
  let epoch = 0
  const created: DeviceManager = new DeviceManager(registry, connector, (state) => {
    windows.emitToAll('device:changed', state)
    // 切换/移除设备:旧连接上的 run 与终端跟随一律作废,迟到的事件不能串到新设备上。
    if (state.current.epoch !== epoch) { epoch = state.current.epoch; bridges?.agent.closeAll(); bridges?.terminals.closeAll() }
  })
  const agent = new AgentBridge({
    emit: (topic, envelope) => { windows.emitToTopic(topic, 'agent:event', envelope) },
    run: (work) => created.run(work)
  })
  const terminals = new TerminalBridge({
    emitData: (localId, payload) => { windows.emitToTopic(terminalTopic(localId), 'terminal:data', payload) },
    emitExit: (localId, payload) => { windows.emitToTopic(terminalTopic(localId), 'terminal:exit', payload) },
    run: (work) => created.run(work),
    warn: (message, error) => { console.warn(`[device] ${message} failed:`, error instanceof Error ? error.message : error) }
  })
  bridges = { agent, terminals }
  connector.onResync((streamId) => { void agent.resync(streamId); void terminals.resync(streamId) })
  manager = created
  return created
}

export function closeDeviceManager(): void {
  bridges?.agent.closeAll()
  bridges?.terminals.closeAll()
  bridges = null
  manager?.close()
  manager = null
}

/**
 * 每个 invoke 都经过这里:选了远程设备 → 按路由表转发或拒绝;否则交给本机处理函数。
 * ★ 管理器还没创建 = 从未选过设备(选择只发生在管理器里,且启动时已重置),直接本机处理,
 *   所以清单损坏之类的问题只会影响设备页,不会拖垮其它频道。
 * `window` 是发起调用的窗口:agent 频道用它订阅/退订该 run 的事件推送。
 */
export function routeInvoke<K extends InvokeChannel>(channel: K, req: InvokeReq<K>, local: () => Promise<InvokeRes<K>> | InvokeRes<K>,
  window?: WindowContext): Promise<InvokeRes<K>> {
  if (manager === null) return Promise.resolve().then(local)
  const topics: WindowTopics | undefined = window === undefined ? undefined : {
    subscribe: (topic) => { windows.subscribe(topic, window.sender) },
    unsubscribe: (topic) => { windows.unsubscribe(topic, window.sender) }
  }
  return new DeviceBroker(manager, bridges ?? undefined).dispatch(channel, req, local, topics)
}

/** send 类频道的同一道闸(终端输入/改尺寸)。没选设备 = 本机处理;选了就转给设备或丢弃,不会落到本机。 */
export function routeSend<K extends SendChannel>(channel: K, payload: IpcSendMap[K], local: () => void): void {
  if (manager === null) { local(); return }
  new DeviceBroker(manager, bridges ?? undefined).dispatchSend(channel, payload, local)
}

export const getDevicesState = (): DevicesState => deviceManager().state()
export const inspectDeviceInvitation = (req: { text: string }) => deviceManager().inspectInvitation(req.text)

export async function pairDevice(req: { text: string; label: string; confirmedFingerprint: string }): Promise<DevicesState> {
  const current = deviceManager()
  await current.pair(req.text, { label: req.label, confirmedFingerprint: req.confirmedFingerprint })
  return current.state()
}

export async function selectDevice(req: { id: string }): Promise<DevicesState> {
  const current = deviceManager()
  // 连接失败时选择仍被记住、状态是 offline:把最终状态交给界面,而不是把「连不上」当成「没选成」。
  try { await current.select(req.id) } catch (error) {
    if (current.current().deviceId !== req.id) throw error
  }
  return current.state()
}

export function useLocalDevice(): DevicesState {
  const current = deviceManager()
  current.useLocal()
  return current.state()
}

export async function removeDevice(req: { id: string }): Promise<DevicesState> {
  const current = deviceManager()
  await current.remove(req.id)
  return current.state()
}

export function setDeviceRelay(req: { id: string; url: string | null; caPem?: string }): DevicesState {
  const current = deviceManager()
  const caPem = req.caPem?.trim()
  current.setRelay(req.id, req.url === null || req.url.trim() === '' ? null : { url: req.url.trim(), ...(caPem !== undefined && caPem !== '' && { caPem }) })
  return current.state()
}
