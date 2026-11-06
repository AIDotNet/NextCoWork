/**
 * 需求：现有网关设置驱动真实 HTTP 生命周期，向界面报告实际端口和故障。
 * 设置仍以主进程 store 为唯一权威；密钥只进入 secrets，不写 settings/广播。
 */
import { randomBytes } from 'node:crypto'
import { networkInterfaces } from 'node:os'
import type { GatewayStatus } from '../../shared/domain/provider'
import { AgentBackend } from '../gateway/agent-backend'
import { GatewayServer } from '../gateway/server'
import { getHost, getRouter } from '../runtime'
import { store } from '../state/store'
import { windows } from '../window/registry'
import { abortRun, startExternalRun } from './agent'
import { isAccountSwitchInFlight } from '../account-switch'

const KEY_REF = 'nextcowork:agent-http-api-key'
let server: GatewayServer | null = null
let key: string | null = null
let error: string | undefined
let revision = 0
let stopped = false
let queue = Promise.resolve()
let configuration: string | null = null

export function getGatewayStatus(): GatewayStatus {
  const settings = store.getSettings().gateway
  return {
    enabled: settings.enabled,
    port: server?.port ?? null,
    host: server?.host ?? (settings.allowRemote === true ? '0.0.0.0' : '127.0.0.1'),
    hasKey: key !== null,
    failover: settings.failover,
    health: getRouter().health(),
    ...(error === undefined ? {} : { error })
  }
}

/** 需求：复制真实可访问的 LAN 地址；0.0.0.0 是绑定地址，不能作为远程 base URL。 */
export function getGatewayAddresses(): string[] {
  const status = getGatewayStatus()
  if (status.port === null) return []
  const hosts = new Set(['127.0.0.1'])
  if (status.host === '0.0.0.0') {
    for (const entries of Object.values(networkInterfaces())) {
      for (const entry of entries ?? []) {
        if (entry.family === 'IPv4' && !entry.internal) hosts.add(entry.address)
      }
    }
  }
  return [...hosts].map((host) => `http://${host}:${status.port}`)
}

const backend = new AgentBackend({
  start: startExternalRun,
  abort: (runId) => abortRun({ runId, cascade: true }),
  status: getGatewayStatus,
  created: (session) => windows.emitToAll('sessions:changed', { kind: 'metadata', sessionIds: [session.id], workspaceId: session.workspaceId })
})

function publish(): void { windows.emitToAll('gateway:status', getGatewayStatus()) }

/** 需求：只有显式复制才能向渲染层返回密钥，状态推送中不出现明文。 */
export function getGatewayKey(): string {
  if (key === null) throw new Error('Gateway is not ready')
  return key
}

/** 需求：串行切换端口/监听范围，快速连点时最后一份设置获胜且不遗留监听器。 */
export function reconcileGateway(): Promise<void> {
  const expected = ++revision
  queue = queue.then(async () => {
    if (stopped || expected !== revision) return
    const settings = store.getSettings().gateway
    const host = settings.allowRemote === true ? '0.0.0.0' : '127.0.0.1'
    const wanted = `${host}:${settings.preferredPort}`
    error = undefined
    // 需求：故障切换只影响下一次路由，不应重绑端口并断开所有 SSE 客户端。
    if (settings.enabled && server !== null && server.port !== null && configuration === wanted) { publish(); return }
    const previous = server
    server = null
    configuration = null
    await previous?.close()
    if (!settings.enabled) { publish(); return }
    try {
      if (!Number.isInteger(settings.preferredPort) || settings.preferredPort < 1024 || settings.preferredPort > 65535) {
        throw new Error('Gateway port must be between 1024 and 65535')
      }
      const secrets = getHost().secrets
      if (key === null) {
        const stored = await secrets.get(KEY_REF)
        const next = stored ?? randomBytes(32).toString('hex')
        if (stored === null) await secrets.set(KEY_REF, next)
        key = next
      }
      if (stopped || expected !== revision) return
      const next = new GatewayServer(backend, key, host, () => !isAccountSwitchInFlight())
      try { await next.listen(settings.preferredPort) }
      catch (failure) { await next.close(); throw failure }
      if (stopped || expected !== revision) { await next.close(); return }
      server = next
      configuration = wanted
    } catch (failure) {
      error = failure instanceof Error ? failure.message : 'Gateway could not start'
    }
    if (!stopped && expected === revision) publish()
  }).catch((failure: unknown) => {
    error = failure instanceof Error ? failure.message : 'Gateway configuration failed'
    if (!stopped) publish()
  })
  return queue
}

export async function setGatewayEnabled(enabled: boolean): Promise<GatewayStatus> {
  store.updateSettings({ gateway: { enabled } })
  windows.emitToAll('settings:changed', store.getSettings())
  await reconcileGateway()
  return getGatewayStatus()
}

export function resetGatewayHealth(providerId?: string): GatewayStatus {
  getRouter().resetHealth(providerId)
  publish()
  return getGatewayStatus()
}

/** 需求：退出时立刻阻止新请求，不等待排队中的 secrets/监听初始化。 */
export function shutdownGateway(): void {
  stopped = true
  revision++
  const previous = server
  server = null
  void previous?.close()
}
