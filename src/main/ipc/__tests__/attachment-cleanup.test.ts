/**
 * 附件清理的判据 —— 这个文件盯的是**「什么绝不该被删」**。
 *
 * 清理是不可逆的:删错一个文件,用户没有任何入口能拿回来,而且他往往
 * 不会立刻发现(一张历史消息里的图变成裂图,可能是几周后才被看到)。
 * 所以这里的用例大半是**否定式**的 —— 断言某某文件在清理后仍然存在。
 */
import { existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

let userDataDir = ''

vi.mock('electron', () => ({
  app: { getPath: (): string => userDataDir },
  dialog: { showOpenDialog: vi.fn() },
  shell: { openPath: vi.fn() }
}))

vi.mock('../../window/registry', () => ({ windows: { broadcast: vi.fn() } }))
vi.mock('../../kernel/run-registry', () => ({ runs: { activeRunIds: (): string[] => [], hasSessionOperations: (): boolean => false, onAbortAll: vi.fn(() => () => {}) } }))

import { DRAFT_ATTACHMENT_TTL_MS } from '../../../shared/domain/attachment'
import { closeDatabase, openDatabase } from '../../db'
import * as repo from '../../db/repo'
import { attachmentRoot } from '../../net/attachment-protocol'
import { uploadAttachment } from '../attachment'
import { cleanupAttachments, cleanupPreview } from '../storage'

const bytesOf = (s: string): Uint8Array<ArrayBuffer> =>
  new Uint8Array(Buffer.from(s, 'utf8')) as Uint8Array<ArrayBuffer>

beforeEach(() => {
  closeDatabase()
  userDataDir = mkdtempSync(join(tmpdir(), 'nextcowork-clean-'))
  openDatabase(userDataDir)
})

afterEach(() => {
  closeDatabase()
  rmSync(userDataDir, { recursive: true, force: true })
})

/**
 * 把一条附件的 created_at 往前推,模拟「传了很久没发」。
 *
 * ★ 必须先删行再插:`putDraftAttachment` 的 `ON CONFLICT` **有意不更新
 * `created_at`** —— 重复上传同一个文件不该把草稿的年龄刷新掉,否则一个
 * 被反复粘贴的图永远不会过期。这里绕开那条语义,而不是去改它。
 */
function ageAttachment(id: string, ms: number): void {
  const row = repo.getAttachmentRow(id)
  if (row === undefined) throw new Error('no row')
  repo.removeAttachmentRow(id)
  repo.putDraftAttachment({
    id: row.id,
    scope: row.scope,
    ownerId: row.ownerId ?? undefined,
    path: row.path,
    size: row.size,
    checksum: row.checksum,
    createdAt: Date.now() - ms
  })
}

describe('绝不该被删的', () => {
  it('★ 刚上传还没发送的草稿附件必须留着 —— 用户挑好图去倒杯水,回来图还得在', () => {
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('A')
    })
    const path = repo.getAttachmentRow(a.id)?.path as string

    cleanupAttachments()

    expect(existsSync(path)).toBe(true)
    expect(repo.getAttachmentRow(a.id)).toBeDefined()
  })

  it('★ 已提交的附件必须留着', () => {
    repo.ensureSession({ id: 'S1', workspaceId: 'W1', rootPathAtCreation: '/tmp' })
    repo.commitMessage('S1', {
      id: 'M1', role: 'user', parts: [{ type: 'text', text: 'x' }],
      createdAt: Date.now(), schemaVersion: 1
    })
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('A')
    })
    const path = repo.getAttachmentRow(a.id)?.path as string
    repo.commitAttachmentsByPath([path], 'M1', 'S1')

    cleanupAttachments()

    expect(existsSync(path)).toBe(true)
  })

  it('★ 未超期的草稿即使已经放了几天也必须留着', () => {
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('A')
    })
    ageAttachment(a.id, DRAFT_ATTACHMENT_TTL_MS / 2)
    const path = repo.getAttachmentRow(a.id)?.path as string

    cleanupAttachments()

    expect(existsSync(path)).toBe(true)
  })
})

describe('应该被回收的', () => {
  it('★ 超期草稿:文件与记录一起收 —— 否则没发出去的图会留到卸载', () => {
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('A')
    })
    const path = repo.getAttachmentRow(a.id)?.path as string
    ageAttachment(a.id, DRAFT_ATTACHMENT_TTL_MS + 1000)

    const r = cleanupAttachments()

    expect(existsSync(path)).toBe(false)
    expect(repo.getAttachmentRow(a.id)).toBeUndefined()
    expect(r.deleted).toBeGreaterThan(0)
  })

  it('★ .tmp 残片无条件回收 —— 上传写到一半崩掉留下的,永远不该存在', () => {
    const dir = join(attachmentRoot(), 'sessions', 'S1')
    mkdirSync(dir, { recursive: true })
    const shard = join(dir, '.01J8ABC.png.tmp')
    writeFileSync(shard, 'HALF')

    cleanupAttachments()

    expect(existsSync(shard)).toBe(false)
  })

  it('磁盘上有、表里没有的真孤儿被回收', () => {
    const dir = join(attachmentRoot(), 'sessions', 'S1')
    mkdirSync(dir, { recursive: true })
    const orphan = join(dir, '01J8ORPHAN.png')
    writeFileSync(orphan, 'O')

    cleanupAttachments()

    expect(existsSync(orphan)).toBe(false)
  })

  it('升级前位于附件根下旧目录里的孤儿也会回收', () => {
    const legacyDir = join(attachmentRoot(), 'legacy-session-S1')
    mkdirSync(legacyDir, { recursive: true })
    const orphan = join(legacyDir, 'legacy-orphan.png')
    writeFileSync(orphan, 'O')

    cleanupAttachments()

    expect(existsSync(orphan)).toBe(false)
  })

  it('升级前旧目录里仍有数据库引用的文件必须保留', () => {
    const legacyDir = join(attachmentRoot(), 'legacy-session-S1')
    mkdirSync(legacyDir, { recursive: true })
    const referenced = join(legacyDir, 'legacy-referenced.png')
    writeFileSync(referenced, 'R')
    repo.putDraftAttachment({
      id: 'legacy-row',
      scope: 'session',
      ownerId: 'S1',
      path: referenced,
      size: 1,
      checksum: 'legacy-checksum',
      createdAt: Date.now()
    })

    cleanupAttachments()

    expect(existsSync(referenced)).toBe(true)
    expect(repo.getAttachmentRow('legacy-row')).toBeDefined()
  })

  it('表里有、磁盘上没有的死记录被回收', () => {
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('A')
    })
    rmSync(repo.getAttachmentRow(a.id)?.path as string)

    cleanupAttachments()

    expect(repo.getAttachmentRow(a.id)).toBeUndefined()
  })
})

describe('预览与执行一致', () => {
  it('★ 预览报几个就删几个 —— 两边共用同一次扫描', () => {
    // 一个超期草稿 + 一个孤儿 + 一个残片 = 3
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('A')
    })
    ageAttachment(a.id, DRAFT_ATTACHMENT_TTL_MS + 1000)

    const dir = join(attachmentRoot(), 'sessions', 'S1')
    writeFileSync(join(dir, '01J8ORPHAN.png'), 'O')
    writeFileSync(join(dir, '.01J8X.png.tmp'), 'H')

    const preview = cleanupPreview({ kind: 'attachments' })
    const result = cleanupAttachments()

    expect(preview.attachmentCount).toBe(result.attachmentCount)
    expect(readdirSync(dir)).toHaveLength(0)
  })

  it('干净的库上预览为 0,执行不删任何东西', () => {
    const a = uploadAttachment({
      scope: 'session', ownerId: 'S1', displayName: 'a.png', mime: 'image/png', bytes: bytesOf('A')
    })
    expect(cleanupPreview({ kind: 'attachments' }).attachmentCount).toBe(0)
    expect(cleanupAttachments().deleted).toBe(0)
    expect(existsSync(repo.getAttachmentRow(a.id)?.path as string)).toBe(true)
  })
})
