/**
 * 需求：开关与远程监听真正驱动生命周期；慢凭证读取、快速连点、只改故障切换
 * 不能遗留一个 UI 显示已关却仍可调用的 HTTP 服务。只使用内存数据库/凭证。
 */
import { afterEach, expect, it } from 'vitest'
import { nodeHost } from '../../kernel/host'
import { getGatewayAddresses, getGatewayKey, getGatewayStatus, reconcileGateway, shutdownGateway } from '../../ipc/gateway'
import { installHost, resetRuntimeForTest } from '../../runtime'
import { store } from '../../state/store'

afterEach(async () => {
  shutdownGateway()
  await reconcileGateway()
  resetRuntimeForTest()
})

it('serializes listener changes, keeps one durable key and closes the actual port on shutdown', async () => {
  resetRuntimeForTest()
  const values = new Map<string, string>()
  let writes = 0
  let release: () => void = () => {}
  const reading = new Promise<void>((resolve) => { release = resolve })
  installHost(nodeHost({ secrets: {
    get: async (ref) => { await reading; return values.get(ref) ?? null },
    set: async (ref, value) => { writes++; values.set(ref, value) },
    available: () => true
  } }))
  store.updateSettings({ gateway: { enabled: true, allowRemote: false, preferredPort: 19836 } })
  const starting = reconcileGateway()
  await Promise.resolve()
  store.updateSettings({ gateway: { enabled: false } })
  const disabling = reconcileGateway()
  release()
  await Promise.all([starting, disabling])
  expect(getGatewayStatus().port).toBeNull()
  expect(getGatewayAddresses()).toEqual([])

  store.updateSettings({ gateway: { enabled: true, allowRemote: true } })
  await reconcileGateway()
  const remote = getGatewayStatus()
  expect(remote.error).toBeUndefined()
  expect(remote.host).toBe('0.0.0.0')
  expect(remote.port).not.toBeNull()
  const key = getGatewayKey()
  expect(key.length).toBe(64)
  expect(JSON.stringify(remote)).not.toContain(key)
  expect(writes).toBe(1)
  const address = `http://127.0.0.1:${remote.port}`
  expect(getGatewayAddresses()).toContain(address)
  const headers = { Authorization: `Bearer ${key}` }
  expect((await fetch(address + '/v1/status', { headers })).status).toBe(200)

  store.updateSettings({ gateway: { failover: true } })
  await reconcileGateway()
  expect(getGatewayStatus().port).toBe(remote.port)
  expect(getGatewayKey()).toBe(key)

  store.updateSettings({ gateway: { allowRemote: false } })
  await reconcileGateway()
  expect(getGatewayStatus().host).toBe('127.0.0.1')
  expect(getGatewayAddresses()).toHaveLength(1)
  expect(getGatewayKey()).toBe(key)

  const local = getGatewayAddresses()[0]!
  shutdownGateway()
  await reconcileGateway()
  expect(getGatewayStatus().port).toBeNull()
  await expect(fetch(local + '/v1/status', { headers, signal: AbortSignal.timeout(1000) })).rejects.toThrow()
})
