/**
 * `usage` category 的双设备闭环:两台设备各自记账,经加密同步后概览合并,请求日志不跨设备。
 *
 * 假服务端按 category 各存一份当前快照(对应 CoWork `cloud_config_v2_document` 的
 * (vault, kind) 唯一键),push 按 baseRevision 判冲突,usage 只留最新事件 ——
 * 与 `EncryptedConfigSyncRepository` 的语义一致。测试不碰真实网络。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { SyncEnvelope, SyncEvent, SyncVault } from '../../shared/domain/config-sync'
import type { UsageAttemptRecord } from '../../shared/domain/usage'
import { closeDatabase, openDatabase } from '../db'
import * as repo from '../db/repo'
import { getUsageActivityStats, getUsageDailySeries } from '../db/usage-rollup'
import { nodeHost } from '../kernel/host'
import { installHost, resetRuntimeForTest } from '../runtime'

vi.mock('electron', () => ({ shell: { openExternal: vi.fn() } }))
vi.mock('../window/registry', () => ({ windows: { emitToAll: vi.fn() } }))

import {
  confirmInitialConfigSync,
  setConfigSyncUsage,
  setupConfigSync,
  shutdownConfigSync,
  startConfigSync
} from '../ipc/config-sync'

const password = 'shared sync password 2026'
const dirs: string[] = []

class KindedSyncServer {
  vault: SyncVault | null = null
  cursor = 0
  readonly docs = new Map<string, SyncEvent>()
  /** >0 时下一次 usage pull 假装没有新事件 —— 用来逼出「两台设备基于同一版本各推一次」的冲突。 */
  stalePulls = 0
  usageConflicts = 0
  readonly requestBodies: string[] = []

  fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = new URL(input instanceof URL ? input.href : String(input))
    const path = url.pathname.replace('/api/client/config-sync/v2', '')
    const method = init?.method ?? 'GET'
    const raw = typeof init?.body === 'string' ? init.body : ''
    if (raw !== '') this.requestBodies.push(raw)
    const kind = url.searchParams.get('kind') ?? ''

    if (path === '/vault' && method === 'GET') {
      return Response.json({ vault: this.vault, legacyExists: false, migrationRequired: false })
    }
    if (path === '/vault' && method === 'POST') {
      this.vault = (JSON.parse(raw) as { vault: SyncVault }).vault
      return Response.json(this.vault)
    }
    if (path === '/devices/register' && method === 'POST') return Response.json({ ok: true })
    if (path === '/preview' && method === 'GET') {
      const current = this.docs.get(kind)
      return Response.json({ events: current === undefined ? [] : [current] })
    }
    if (path === '/push' && method === 'POST') {
      const accepted: Array<{ mutationId: string; revision: number }> = []
      const conflicts: Array<{ mutationId: string; remote: SyncEvent | null }> = []
      for (const mutation of (JSON.parse(raw) as { mutations: SyncEnvelope[] }).mutations) {
        const current = this.docs.get(mutation.kind)
        const base = current?.revision ?? 0
        if (mutation.baseRevision !== base) {
          if (mutation.kind === 'usage') this.usageConflicts += 1
          conflicts.push({ mutationId: mutation.mutationId, remote: current ?? null })
          continue
        }
        this.cursor += 1
        this.docs.set(mutation.kind, {
          ...mutation,
          revision: base + 1,
          cursor: this.cursor,
          deviceId: new Headers(init?.headers).get('x-sync-device-id') ?? 'unknown'
        })
        accepted.push({ mutationId: mutation.mutationId, revision: base + 1 })
      }
      return Response.json({ accepted, conflicts })
    }
    if (path === '/pull' && method === 'GET') {
      const after = Number(url.searchParams.get('cursor') ?? 0)
      if (kind === 'usage' && this.stalePulls > 0) {
        this.stalePulls -= 1
        return Response.json({ cursor: after, hasMore: false, events: [] })
      }
      const current = this.docs.get(kind)
      const events = current !== undefined && current.cursor > after ? [current] : []
      return Response.json({ cursor: events.at(-1)?.cursor ?? after, hasMore: false, events })
    }
    return Response.json({ error: 'unsupported' }, { status: 404 })
  }
}

/** 打开(或重新打开)一台设备的库;传入 dir 即回到同一台设备。 */
async function openDevice(server: KindedSyncServer, dir?: string): Promise<string> {
  closeDatabase()
  const target = dir ?? mkdtempSync(join(tmpdir(), 'nextcowork-usage-sync-'))
  if (dir === undefined) dirs.push(target)
  openDatabase(target)
  resetRuntimeForTest()
  const host = nodeHost({ fetch: server.fetch })
  installHost(host)
  await host.secrets.set('nextcowork:client-access-token', 'account-access')
  return target
}

/** 固定在当天正午,避免用例在本地时间接近零点时跨到另一天。 */
function noon(): number {
  const d = new Date()
  d.setHours(12, 0, 0, 0)
  return d.getTime()
}

function record(id: string, inputTokens: number, patch: Partial<UsageAttemptRecord> = {}): void {
  repo.recordUsageAttempt({
    id, at: noon(), runId: `run-${id}`, workspaceId: 'w', sessionId: 's', attempt: 1,
    providerId: 'acme', providerName: 'Acme', protocol: 'anthropic',
    endpoint: 'https://request-log-only.invalid/v1/messages',
    alias: 'assistant', upstreamModel: 'model-a', responseModel: null,
    inputTokens, outputTokens: 0, cacheReadTokens: 0, cacheWriteTokens: 0, cacheWrite1hTokens: 0,
    thinkingTokens: null, thinkingTokensEstimated: false, latencyMs: 100, timeToFirstTokenMs: null,
    ok: true, httpStatus: 200, errorKind: null, errorMessage: 'secret error text in request log',
    stopReason: 'end_turn', costMicros: null, currency: null, pricingTier: null, pricingWindow: null,
    toolCalls: 0, toolErrors: 0,
    ...patch
  })
}

function todayInputTokens(): number {
  return getUsageDailySeries({ to: noon() + 12 * 60 * 60 * 1000 })
    .reduce((sum, bucket) => sum + bucket.inputTokens, 0)
}

afterEach(() => {
  shutdownConfigSync()
  resetRuntimeForTest()
  closeDatabase()
  for (const dir of dirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

describe('v2 usage statistics synchronization', () => {
  it('merges two devices that pushed from the same base, without losing either side', async () => {
    const server = new KindedSyncServer()

    const deviceA = await openDevice(server)
    record('a-1', 100)
    startConfigSync('1')
    await setupConfigSync({ password, remember: true })
    expect(server.docs.get('usage')?.revision).toBe(1)
    shutdownConfigSync()

    await openDevice(server)
    record('b-1', 40)
    startConfigSync('1')
    await setupConfigSync({ password, remember: false })
    // B 这一轮「没看见」A 的快照,于是基于 revision 0 推 —— 服务端判冲突
    server.stalePulls = 1
    await confirmInitialConfigSync()

    expect(server.usageConflicts).toBe(1)
    expect(server.docs.get('usage')?.revision).toBe(2)
    // 冲突被自动解:B 的概览 = A + B,而本机请求日志仍只有自己那一条
    expect(todayInputTokens()).toBe(140)
    expect(getUsageActivityStats().syncedDevices).toBe(1)
    expect(repo.getUsageRequestLogs({ to: noon() + 60 * 60 * 1000 }).total).toBe(1)
    shutdownConfigSync()

    // 回到 A:拉到合并后的快照,自己那片没变,所以不再推第三版
    await openDevice(server, deviceA)
    startConfigSync('1')
    await setupConfigSync({ password, remember: false })
    expect(todayInputTokens()).toBe(140)
    expect(server.docs.get('usage')?.revision).toBe(2)
  })

  it('never puts request-log fields or plaintext usage on the wire', async () => {
    const server = new KindedSyncServer()
    await openDevice(server)
    record('a-1', 100)
    startConfigSync('1')
    await setupConfigSync({ password, remember: true })
    const wire = server.requestBodies.join('\n')
    expect(server.docs.get('usage')).toBeDefined()
    expect(wire).not.toContain('request-log-only.invalid')
    expect(wire).not.toContain('secret error text')
    expect(wire).not.toContain('model-a')
  })

  it('turning usage sync off drops other devices from the overview but keeps this device on the others', async () => {
    const server = new KindedSyncServer()
    const deviceA = await openDevice(server)
    record('a-1', 100)
    startConfigSync('1')
    await setupConfigSync({ password, remember: true })
    shutdownConfigSync()

    await openDevice(server)
    record('b-1', 40)
    startConfigSync('1')
    await setupConfigSync({ password, remember: false })
    await confirmInitialConfigSync()
    expect(todayInputTokens()).toBe(140)

    const status = await setConfigSyncUsage(false)
    expect(status.control?.selection.usage).toBe(false)
    expect(todayInputTokens()).toBe(40)
    expect(getUsageActivityStats().syncedDevices).toBe(0)
    shutdownConfigSync()

    // 关掉的是 B 的开关;A 仍看得到 B 关之前推上去的那一片
    await openDevice(server, deviceA)
    startConfigSync('1')
    await setupConfigSync({ password, remember: false })
    expect(todayInputTokens()).toBe(140)
  })
})
