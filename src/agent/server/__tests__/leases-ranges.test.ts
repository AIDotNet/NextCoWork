import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { connectController } from '../../../controller-client/connect'
import type { ControllerClient } from '../../../controller-client/client'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { store } from '../../../main/state/store'
import { connectDirectDevice } from '../../../transport/node/direct'
import { pairWithDevice } from '../../../transport/node/pairing-client'
import { registerFileMethods } from '../methods/files'
import { registerLeaseMethods } from '../methods/leases'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let root = ''
let workspace = ''
let service: DeviceService | null = null
const transports: SecureTransport[] = []
type Receipt = { status: string; result: Record<string, unknown> }

beforeEach(async () => {
  root = mkdtempSync(join(tmpdir(), 'ncw-lease-range-'))
  workspace = join(root, 'workspace')
  mkdirSync(workspace)
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0, features: [],
    register: (router, _identity, services) => { registerFileMethods(router); registerLeaseMethods(router, services.leases) } })
  store.putWorkspace({ id: 'workspace', name: 'Files', rootPath: workspace, environment: { kind: 'local' },
    settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
})
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  await service?.stop().catch(() => {})
  service = null
  rmSync(root, { recursive: true, force: true })
})

async function controller(controllerId: string): Promise<ControllerClient> {
  const credentials = await pairWithDevice(service!.createInvitation().text, { controllerId, label: controllerId, confirmDevice: () => true })
  const transport = await connectDirectDevice(`wss://127.0.0.1:${service!.address().port}/device`, credentials)
  transports.push(transport)
  let n = 0
  const { client } = await connectController(transport, { expectedDeviceId: service!.identity.deviceId, expectedControllerId: controllerId,
    clientSessionId: `s-${controllerId}`, bindingEpoch: 0, nextId: () => `r${++n}`, clock,
    capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
  return client
}

describe('input leases over the wire', () => {
  const target = { kind: 'terminal', resourceId: 'term-1' }

  it('grants one typist at a time, shows viewers the holder without the token, and fences handed-off tokens', async () => {
    const alice = await controller('alice')
    const bob = await controller('bob')
    const got = await alice.command('leases.acquire', target, 'a1') as Receipt
    expect(got.status).toBe('succeeded')
    const token = got.result.token as number
    // 身份来自连接:bob 拿不到;错误是失败回执,不是协议错误。
    expect(await bob.command('leases.acquire', target, 'b1')).toMatchObject({ status: 'failed', result: { error: { code: 'busy' } } })
    const seen = await bob.query('leases.holder', target) as { holder: string }
    expect(seen.holder).toBe('alice')
    expect(JSON.stringify(seen)).not.toContain(String(token))

    const handed = await alice.command('leases.handoff', { ...target, token, to: 'bob' }, 'a2') as Receipt
    expect(handed.status).toBe('succeeded')
    // alice 迟到的续租/输入带旧 token:被拒绝。
    expect(await alice.command('leases.renew', { ...target, token }, 'a3')).toMatchObject({ status: 'failed', result: { error: { code: 'stale_binding' } } })
    expect(await bob.command('leases.renew', { ...target, token: handed.result.token as number }, 'b2')).toMatchObject({ status: 'succeeded' })
    expect(await bob.command('leases.release', { ...target, token: handed.result.token as number }, 'b3')).toMatchObject({ status: 'succeeded', result: { released: true } })
    expect(await bob.query('leases.holder', target)).toBeNull()
    alice.detach(); bob.detach()
  })

  it('cannot hand a lease to a controller that was never paired or has been revoked, and revocation frees held leases', async () => {
    const alice = await controller('alice')
    await controller('bob')
    const lease = await alice.command('leases.acquire', target, 'a1') as Receipt
    expect(await alice.command('leases.handoff', { ...target, token: lease.result.token as number, to: 'mallory' }, 'a2'))
      .toMatchObject({ status: 'failed', result: { error: { code: 'not_found' } } })
    service!.revoke('alice')
    const carol = await controller('carol')
    expect(await carol.query('leases.holder', target)).toBeNull()
    expect(await carol.command('leases.acquire', target, 'c1')).toMatchObject({ status: 'succeeded' })
    carol.detach()
  })

  it('rejects unknown kinds, out-of-range durations and identity fields in params', async () => {
    const alice = await controller('alice')
    await expect(alice.command('leases.acquire', { kind: 'toaster', resourceId: 'x' }, 'x1')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(alice.command('leases.acquire', { ...target, ttlMs: 5 }, 'x2')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(alice.command('leases.acquire', { ...target, holder: 'bob' }, 'x3')).rejects.toMatchObject({ code: 'invalid_request' })
    alice.detach()
  })
})

describe('range reads', () => {
  const bytes = Buffer.from(Array.from({ length: 1000 }, (_, i) => i % 251))
  type Range = { data: string; offset: number; length: number; size: number; revision: string; eof: boolean }

  it('reads a large file in chunks that reassemble exactly, with a stable revision', async () => {
    writeFileSync(join(workspace, 'blob.bin'), bytes)
    const client = await controller('alice')
    const parts: Buffer[] = []
    let offset = 0
    let revision: string | undefined
    for (;;) {
      const range = await client.query('files.readRange', { workspaceId: 'workspace', path: 'blob.bin', offset, length: 300,
        ...(revision !== undefined && { revision }) }) as Range
      revision ??= range.revision
      expect(range.revision).toBe(revision)
      parts.push(Buffer.from(range.data, 'base64'))
      offset += range.length
      if (range.eof) break
    }
    expect(Buffer.concat(parts).equals(bytes)).toBe(true)
    // 越过文件尾:空块 + eof,不是错误。
    expect(await client.query('files.readRange', { workspaceId: 'workspace', path: 'blob.bin', offset: 5000, length: 10 }))
      .toMatchObject({ length: 0, eof: true, size: 1000 })
    client.detach()
  })

  it('refuses to stitch two versions together when the file changes between chunks', async () => {
    writeFileSync(join(workspace, 'blob.bin'), bytes)
    const client = await controller('alice')
    const first = await client.query('files.readRange', { workspaceId: 'workspace', path: 'blob.bin', offset: 0, length: 100 }) as Range
    writeFileSync(join(workspace, 'blob.bin'), Buffer.concat([bytes, Buffer.from('more')]))
    await expect(client.query('files.readRange', { workspaceId: 'workspace', path: 'blob.bin', offset: 100, length: 100, revision: first.revision }))
      .rejects.toMatchObject({ code: 'conflict' })
    client.detach()
  })

  it('keeps ranges inside the workspace: absolute and parent paths, symlinks out, directories and oversize requests', async () => {
    writeFileSync(join(root, 'outside.bin'), 'secret')
    writeFileSync(join(workspace, 'a.bin'), bytes)
    mkdirSync(join(workspace, 'dir'))
    symlinkSync(join(root, 'outside.bin'), join(workspace, 'link.bin'))
    const client = await controller('alice')
    const read = (path: string, length = 10) => client.query('files.readRange', { workspaceId: 'workspace', path, offset: 0, length })
    await expect(read('/etc/hosts')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(read('../outside.bin')).rejects.toMatchObject({ code: 'invalid_request' })
    const linked = await read('link.bin').catch((e: unknown) => e)
    expect(linked).toBeInstanceOf(Error) // 符号链接不被跟随出工作区
    expect(JSON.stringify(linked)).not.toContain(root)
    await expect(read('dir')).rejects.toBeInstanceOf(Error)
    await expect(read('missing.bin')).rejects.toMatchObject({ code: 'not_found' })
    await expect(read('a.bin', 512 * 1024 + 1)).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.query('files.readRange', { workspaceId: 'nope', path: 'a.bin', offset: 0, length: 1 })).rejects.toMatchObject({ code: 'not_found' })
    client.detach()
  })
})
