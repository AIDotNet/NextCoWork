import { createHash } from 'node:crypto'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { basename, dirname, join, parse } from 'node:path'
import { promises as fs } from 'node:fs'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { connectController } from '../../../controller-client/connect'
import type { ControllerClient } from '../../../controller-client/client'
import type { ClientClock, SecureTransport } from '../../../controller-client/ports'
import { closeDatabase } from '../../../main/db'
import { store } from '../../../main/state/store'
import { connectDirectDevice } from '../../../transport/node/direct'
import { pairWithDevice } from '../../../transport/node/pairing-client'

// 文件方法的整条链不得加载 Electron。
vi.mock('electron', () => { throw new Error('Device service imported Electron') })
import { installHost, resetRuntimeForTest } from '../../../main/runtime'
import { installWorkspaceTrash } from '../../../main/services/workspace-files'
import { headlessHost } from '../../../main/host/headless'
import { DeviceEventHub } from '../events'
import { registerCoreMethods } from '../methods/core'
import { startDeviceService, type DeviceService } from '../service'

const clock: ClientClock = { schedule: (callback, ms) => { const timer = setTimeout(callback, ms); return () => clearTimeout(timer) } }
let root = ''
let workspace = ''
let service: DeviceService | null = null
let uninstall: (() => void) | null = null
const transports: SecureTransport[] = []
type Receipt = { status: string; result: Record<string, unknown> }

beforeEach(async () => {
  resetRuntimeForTest()
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'ncw-files-methods-'))
  workspace = join(root, 'workspace')
  mkdirSync(workspace)
  writeFileSync(join(workspace, 'notes.txt'), 'hello\n')
  mkdirSync(join(workspace, 'sub'))
  service = await startDeviceService({ dataDir: join(root, 'data'), host: '127.0.0.1', port: 0, pairingPort: 0,
    features: ['tasks'], register: (router, _identity, services) => {
      uninstall = registerCoreMethods(router, new DeviceEventHub(), services.leases, services.onRevoke)
    } })
  installHost(headlessHost())
  installWorkspaceTrash(async (path) => {
    const bin = join(root, 'trash')
    await fs.mkdir(bin, { recursive: true })
    await fs.rename(path, join(bin, basename(path)))
  })
  store.putWorkspace({ id: 'workspace', name: 'Files', rootPath: workspace, environment: { kind: 'local' },
    settings: DEFAULT_WORKSPACE_SETTINGS, createdAt: 1, lastOpenedAt: 1 })
})
afterEach(async () => {
  for (const transport of transports.splice(0)) transport.close()
  await service?.stop().catch(() => {})
  service = null
  uninstall?.()
  uninstall = null
  installWorkspaceTrash(null)
  resetRuntimeForTest()
  rmSync(root, { recursive: true, force: true })
})

async function controllerAs(controllerId: string): Promise<ControllerClient> {
  const credentials = await pairWithDevice(service!.createInvitation().text,
    { controllerId, label: controllerId, confirmDevice: () => true })
  const transport = await connectDirectDevice(`wss://127.0.0.1:${service!.address().port}/device`, credentials)
  transports.push(transport)
  let n = 0
  const { client } = await connectController(transport, { expectedDeviceId: service!.identity.deviceId,
    expectedControllerId: controllerId, clientSessionId: `files-${controllerId}`, bindingEpoch: 0, nextId: () => `r${++n}`, clock,
    capabilities: { kind: 'test', maxFrameBytes: 1024 * 1024, features: [] } })
  return client
}
const controller = (): Promise<ControllerClient> => controllerAs('controller')

describe('remote file methods', () => {
  it('lists a directory and reads a text file with its revision', async () => {
    const client = await controller()
    const listing = await client.query('files.list', { workspaceId: 'workspace', path: '' }) as { entries: Array<{ name: string; kind: string }> }
    expect(listing.entries.map((entry) => `${entry.kind}:${entry.name}`).sort()).toEqual(['dir:sub', 'file:notes.txt'])
    const file = await client.query('files.read', { workspaceId: 'workspace', path: 'notes.txt' }) as { content: string; revision: string }
    expect(file.content).toBe('hello\n')
    expect(file.revision).toBeTruthy()
    client.detach()
  })

  it('writes only against the revision that was read, and never overwrites a newer disk version', async () => {
    const client = await controller()
    const read = await client.query('files.read', { workspaceId: 'workspace', path: 'notes.txt' }) as { revision: string }
    const saved = await client.command('files.write', { workspaceId: 'workspace', path: 'notes.txt', content: 'from controller\n',
      revision: read.revision }, 'w1') as Receipt
    expect(saved.status).toBe('succeeded')
    expect(readFileSync(join(workspace, 'notes.txt'), 'utf8')).toBe('from controller\n')

    // 本机编辑器在控制端读完之后又改了文件:拿旧 revision 写必须冲突,磁盘内容保持。
    writeFileSync(join(workspace, 'notes.txt'), 'edited locally\n')
    const stale = await client.command('files.write', { workspaceId: 'workspace', path: 'notes.txt', content: 'stale write\n',
      revision: read.revision }, 'w2') as Receipt
    expect(stale).toMatchObject({ status: 'failed', result: { error: { code: 'conflict' } } })
    expect(readFileSync(join(workspace, 'notes.txt'), 'utf8')).toBe('edited locally\n')
    client.detach()
  })

  it('keeps every path inside the workspace and never leaks system paths in errors', async () => {
    const client = await controller()
    writeFileSync(join(root, 'outside.txt'), 'secret')
    for (const path of ['../outside.txt', '/etc/hosts', 'C:\\Windows\\win.ini', 'sub/../../outside.txt', '\\\\host\\share']) {
      await expect(client.query('files.read', { workspaceId: 'workspace', path })).rejects.toMatchObject({ code: 'invalid_request' })
      await expect(client.query('files.list', { workspaceId: 'workspace', path })).rejects.toMatchObject({ code: 'invalid_request' })
    }
    await expect(client.command('files.write', { workspaceId: 'workspace', path: '/tmp/x', content: 'x', revision: 'r' }, 'w0'))
      .rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.command('files.mutate', { workspaceId: 'workspace', path: 'notes.txt', operation: 'copy', destination: '/tmp/stolen.txt' }, 'm00'))
      .rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.query('files.read', { workspaceId: 'workspace', path: 'missing.txt' })).rejects.toMatchObject({ code: 'not_found' })
    await expect(client.query('files.list', { workspaceId: 'nope', path: '' })).rejects.toMatchObject({ code: 'not_found' })
    // 工作区内指向外部的符号链接:读取被围栏拒绝,且错误里没有系统路径。
    symlinkSync(join(root, 'outside.txt'), join(workspace, 'link.txt'))
    const linked = await client.query('files.read', { workspaceId: 'workspace', path: 'link.txt' }).catch((e: unknown) => e)
    expect(linked).toMatchObject({ code: expect.stringMatching(/invalid_request|forbidden|not_found/) })
    expect(JSON.stringify(linked)).not.toContain(root)
    await client.command('files.mutate', { workspaceId: 'workspace', path: 'link.txt', operation: 'delete' }, 'm0')
    expect(existsSync(join(root, 'outside.txt'))).toBe(true)
    client.detach()
  })

  it('creates, renames, copies and deletes through the recoverable trash, refusing overwrites by default', async () => {
    const client = await controller()
    const run = (key: string, params: Record<string, unknown>) =>
      client.command('files.mutate', { workspaceId: 'workspace', ...params }, key) as Promise<Receipt>
    expect((await run('m1', { path: 'new.txt', operation: 'create-file' })).status).toBe('succeeded')
    expect(existsSync(join(workspace, 'new.txt'))).toBe(true)
    expect((await run('m2', { path: 'new.txt', operation: 'rename', destination: 'renamed.txt' })).status).toBe('succeeded')
    expect((await run('m3', { path: 'renamed.txt', operation: 'copy', destination: 'copy.txt' })).status).toBe('succeeded')
    // 目标已存在:默认拒绝,不覆盖。
    expect(await run('m4', { path: 'copy.txt', operation: 'copy', destination: 'notes.txt' })).toMatchObject({ status: 'failed', result: { error: { code: 'conflict' } } })
    expect(readFileSync(join(workspace, 'notes.txt'), 'utf8')).toBe('hello\n')
    expect((await run('m5', { path: 'copy.txt', operation: 'delete' })).status).toBe('succeeded')
    expect(existsSync(join(workspace, 'copy.txt'))).toBe(false)
    expect(existsSync(join(root, 'trash', 'copy.txt'))).toBe(true)
    // 同一个 key 重放不会再执行一次。
    expect((await run('m5', { path: 'copy.txt', operation: 'delete' }))).toMatchObject({ status: 'succeeded' })
    client.detach()
  })

  it('refuses delete when no recoverable trash is available instead of deleting permanently', async () => {
    installWorkspaceTrash(null)
    const client = await controller()
    const result = await client.command('files.mutate', { workspaceId: 'workspace', path: 'notes.txt', operation: 'delete' }, 'nt') as Receipt
    expect(result.status).toBe('failed')
    expect(existsSync(join(workspace, 'notes.txt'))).toBe(true)
    client.detach()
  })

  it('rejects malformed requests before any receipt exists', async () => {
    const client = await controller()
    await expect(client.command('files.mutate', { workspaceId: 'workspace', path: 'a', operation: 'format-disk' }, 'bad1')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.command('files.mutate', { workspaceId: 'workspace', path: 'a', operation: 'copy', overwrite: false }, 'bad2')).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.command('files.write', { workspaceId: 'workspace', path: 'a', content: 'x' }, 'bad3')).rejects.toMatchObject({ code: 'invalid_request' })
    client.detach()
  })
})

describe('chunked upload', () => {
  const payload = Buffer.from(Array.from({ length: 1500 }, (_, i) => (i * 7) % 256))
  const b64 = (buffer: Buffer): string => buffer.toString('base64')
  const parts = (): string[] => readdirSync(workspace).filter((name) => name.startsWith('.ncw-upload-'))

  async function begin(client: ControllerClient, key: string, path: string, over: Record<string, unknown> = {}): Promise<string> {
    const receipt = await client.command('files.uploadBegin', { workspaceId: 'workspace', path, size: payload.length, ...over }, key) as Receipt
    expect(receipt.status).toBe('succeeded')
    return receipt.result.uploadId as string
  }

  it('uploads in chunks, verifies the digest and lands atomically; the part file never shows up as the target', async () => {
    const client = await controller()
    const id = await begin(client, 'u1', 'up.bin')
    expect(parts()).toHaveLength(1)
    expect(existsSync(join(workspace, 'up.bin'))).toBe(false) // 提交之前目标不存在
    expect(await client.query('files.uploadChunk', { uploadId: id, offset: 0, data: b64(payload.subarray(0, 600)) })).toEqual({ received: 600 })
    expect(await client.query('files.uploadChunk', { uploadId: id, offset: 600, data: b64(payload.subarray(600, 1200)) })).toEqual({ received: 1200 })
    expect(existsSync(join(workspace, 'up.bin'))).toBe(false)
    await client.query('files.uploadChunk', { uploadId: id, offset: 1200, data: b64(payload.subarray(1200)) })
    const digest = createHash('sha256').update(payload).digest('hex')
    expect(await client.command('files.uploadCommit', { uploadId: id, sha256: digest }, 'u1c')).toMatchObject({ status: 'succeeded', result: { size: 1500 } })
    expect(readFileSync(join(workspace, 'up.bin')).equals(payload)).toBe(true)
    expect(parts()).toEqual([])
    client.detach()
  })

  it('resumes after a lost reply: a repeated or out-of-order chunk is refused with the byte to resume from', async () => {
    const client = await controller()
    const id = await begin(client, 'u1', 'resume.bin')
    await client.query('files.uploadChunk', { uploadId: id, offset: 0, data: b64(payload.subarray(0, 500)) })
    await expect(client.query('files.uploadChunk', { uploadId: id, offset: 0, data: b64(payload.subarray(0, 500)) })).rejects.toMatchObject({ code: 'conflict', message: expect.stringContaining('500') })
    await expect(client.query('files.uploadChunk', { uploadId: id, offset: 900, data: b64(payload.subarray(900, 1000)) })).rejects.toMatchObject({ code: 'conflict' })
    // 不完整的上传不能提交。
    expect(await client.command('files.uploadCommit', { uploadId: id }, 'c0')).toMatchObject({ status: 'failed', result: { error: { code: 'conflict' } } })
    await client.query('files.uploadChunk', { uploadId: id, offset: 500, data: b64(payload.subarray(500)) })
    expect(await client.command('files.uploadCommit', { uploadId: id }, 'c1')).toMatchObject({ status: 'succeeded' })
    expect(readFileSync(join(workspace, 'resume.bin')).equals(payload)).toBe(true)
    client.detach()
  })

  it('refuses a wrong digest, drops the part file and leaves any existing target untouched', async () => {
    writeFileSync(join(workspace, 'keep.bin'), 'original')
    const client = await controller()
    const id = await begin(client, 'u1', 'keep.bin', { overwrite: true })
    await client.query('files.uploadChunk', { uploadId: id, offset: 0, data: b64(payload) })
    expect(await client.command('files.uploadCommit', { uploadId: id, sha256: '0'.repeat(64) }, 'c1')).toMatchObject({ status: 'failed', result: { error: { code: 'conflict' } } })
    expect(readFileSync(join(workspace, 'keep.bin'), 'utf8')).toBe('original')
    expect(parts()).toEqual([])
    client.detach()
  })

  it('never overwrites by default, even if the target appears after begin, and overwrites only when asked', async () => {
    writeFileSync(join(workspace, 'exists.bin'), 'original')
    const client = await controller()
    expect(await client.command('files.uploadBegin', { workspaceId: 'workspace', path: 'exists.bin', size: 1 }, 'b0')).toMatchObject({ status: 'failed', result: { error: { code: 'conflict' } } })
    const id = await begin(client, 'u1', 'late.bin')
    await client.query('files.uploadChunk', { uploadId: id, offset: 0, data: b64(payload) })
    writeFileSync(join(workspace, 'late.bin'), 'someone else wrote this')
    expect(await client.command('files.uploadCommit', { uploadId: id }, 'c1')).toMatchObject({ status: 'failed', result: { error: { code: 'conflict' } } })
    expect(readFileSync(join(workspace, 'late.bin'), 'utf8')).toBe('someone else wrote this')
    const over = await begin(client, 'u2', 'exists.bin', { overwrite: true })
    await client.query('files.uploadChunk', { uploadId: over, offset: 0, data: b64(payload) })
    expect(await client.command('files.uploadCommit', { uploadId: over }, 'c2')).toMatchObject({ status: 'succeeded' })
    expect(readFileSync(join(workspace, 'exists.bin')).equals(payload)).toBe(true)
    client.detach()
  })

  it('keeps uploads inside the workspace and limits size, chunks and encoding', async () => {
    const client = await controller()
    for (const path of ['/etc/x', '../x', 'sub/../../x']) {
      await expect(client.command('files.uploadBegin', { workspaceId: 'workspace', path, size: 1 }, `p${path.length}`)).rejects.toMatchObject({ code: 'invalid_request' })
    }
    await expect(client.command('files.uploadBegin', { workspaceId: 'workspace', path: 'x', size: 256 * 1024 * 1024 + 1 }, 's1')).rejects.toMatchObject({ code: 'invalid_request' })
    expect(await client.command('files.uploadBegin', { workspaceId: 'workspace', path: 'no-such-dir/x', size: 1 }, 's2')).toMatchObject({ status: 'failed' })
    const id = await begin(client, 'u1', 'ok.bin')
    await expect(client.query('files.uploadChunk', { uploadId: id, offset: 0, data: '!!!not base64!!!' })).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.query('files.uploadChunk', { uploadId: id, offset: 0, data: b64(Buffer.alloc(2000)) })).rejects.toMatchObject({ code: 'resource_limit' }) // 超过声明的 1500
    await expect(client.query('files.uploadChunk', { uploadId: 'up-missing', offset: 0, data: b64(payload) })).rejects.toMatchObject({ code: 'not_found' })
    client.detach()
  })

  it('belongs to its owner: another controller cannot continue, commit or abort it; abort cleans the part file', async () => {
    const alice = await controller()
    const id = await begin(alice, 'u1', 'mine.bin')
    const other = await controllerAs('bob')
    await expect(other.query('files.uploadChunk', { uploadId: id, offset: 0, data: b64(payload) })).rejects.toMatchObject({ code: 'forbidden' })
    await expect(other.query('files.uploadAbort', { uploadId: id })).rejects.toMatchObject({ code: 'forbidden' })
    expect(await other.command('files.uploadCommit', { uploadId: id }, 'c1')).toMatchObject({ status: 'failed', result: { error: { code: 'forbidden' } } })
    expect(await alice.query('files.uploadAbort', { uploadId: id })).toEqual({ aborted: true })
    expect(parts()).toEqual([])
    expect(await alice.query('files.uploadAbort', { uploadId: id })).toEqual({ aborted: false })
    alice.detach(); other.detach()
  })

  it('drops a revoked controller\'s unfinished uploads at once, leaving other controllers\' uploads alone', async () => {
    const alice = await controller()
    const bob = await controllerAs('bob')
    await begin(alice, 'u1', 'alice.bin')
    const bobId = await begin(bob, 'u2', 'bob.bin')
    expect(parts()).toHaveLength(2)
    expect(service!.revoke('controller')).toBe(true)
    // 撤销之后立刻只剩 bob 的那一份临时文件,不是等 30 分钟空闲过期。
    expect(parts()).toHaveLength(1)
    await bob.query('files.uploadChunk', { uploadId: bobId, offset: 0, data: Buffer.from('x').toString('base64') }).catch(() => undefined)
    expect(await bob.query('files.uploadAbort', { uploadId: bobId })).toEqual({ aborted: true })
    expect(parts()).toEqual([])
    bob.detach()
  })

  it('cleans every unfinished part file when the service stops', async () => {
    const client = await controller()
    await begin(client, 'u1', 'a.bin'); await begin(client, 'u2', 'b.bin')
    expect(parts()).toHaveLength(2)
    uninstall?.(); uninstall = null
    expect(parts()).toEqual([])
    client.detach()
  })
})

describe('directory browsing', () => {
  it('lists only sub-directory names for choosing a workspace root, never files', async () => {
    mkdirSync(join(workspace, 'alpha')); mkdirSync(join(workspace, '.hidden')); mkdirSync(join(workspace, 'beta'))
    writeFileSync(join(workspace, 'secret.txt'), 'x')
    symlinkSync(join(workspace, 'alpha'), join(workspace, 'link-to-dir'))
    symlinkSync(join(workspace, 'secret.txt'), join(workspace, 'link-to-file'))
    const client = await controller()
    const listing = await client.query('directories.list', { path: workspace }) as { path: string; parent: string | null; entries: Array<{ name: string; hidden: boolean }> }
    expect(listing.entries).toEqual([
      { name: '.hidden', hidden: true }, { name: 'alpha', hidden: false }, { name: 'beta', hidden: false }, { name: 'link-to-dir', hidden: false }, { name: 'sub', hidden: false }
    ])
    expect(JSON.stringify(listing)).not.toContain('secret.txt')
    expect(listing.parent).toBe(dirname(listing.path))
    // 空路径从服务用户的家目录开始;根目录没有上一级。
    expect((await client.query('directories.list', { path: '' }) as { path: string }).path).toBe(realpathSync(homedir()))
    expect((await client.query('directories.list', { path: parse(workspace).root }) as { parent: string | null }).parent).toBeNull()
    client.detach()
  })

  it('rejects relative paths, control characters, files and missing directories', async () => {
    const client = await controller()
    await expect(client.query('directories.list', { path: 'relative' })).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.query('directories.list', { path: `${workspace}\u0000x` })).rejects.toMatchObject({ code: 'invalid_request' })
    await expect(client.query('directories.list', { path: join(workspace, 'notes.txt') })).rejects.toMatchObject({ code: 'invalid_request' })
    const missing = await client.query('directories.list', { path: join(workspace, 'missing') }).catch((e: unknown) => e)
    expect(missing).toMatchObject({ code: 'not_found' })
    expect(JSON.stringify(missing)).not.toContain(workspace)
    client.detach()
  })
})
