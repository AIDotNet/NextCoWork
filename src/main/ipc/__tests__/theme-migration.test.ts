/**
 * 主题目录迁移与清理的边界。
 *
 * 这两件事共用一个目录,而它们的判据来自**不同的权威**:
 * `sweepOrphans` 认 `index.json`,`cleanupAttachments` 认 `attachments` 表。
 * 把主题图搬进附件根之后,如果清理仍然扫全根,用户传的每一张主题图
 * 都会在下一次清理时消失 —— 这个文件盯的就是那条边界。
 */
import { existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

/** Electron profile 根 —— 旧版的 `themes/` 躺在这一层。 */
let userDataDir = ''
/** 数据根 —— profile 根下的 `data/`,附件树在这一层。 */
let dataDir = ''

vi.mock('electron', () => ({
  app: { getPath: (): string => userDataDir },
  dialog: { showOpenDialog: vi.fn() },
  shell: { openPath: vi.fn() }
}))

vi.mock('../../runtime', () => ({
  getHost: () => ({ paths: { userData: (): string => dataDir, attachments: (): string => join(dataDir, 'attachments') } })
}))

vi.mock('../../window/registry', () => ({ windows: { broadcast: vi.fn() } }))
vi.mock('../../kernel/run-registry', () => ({ runs: { activeRunIds: (): string[] => [], hasSessionOperations: (): boolean => false } }))

import { DATA_SUBDIRNAME, closeDatabase, openDatabase } from '../../db'
import { attachmentRoot } from '../../net/attachment-protocol'
import { cleanupAttachments } from '../storage'
import { migrateLegacyThemesDir } from '../theme'

const legacyDir = (): string => join(userDataDir, 'themes')
const newDir = (): string => join(attachmentRoot(), 'themes')

beforeEach(() => {
  closeDatabase()
  userDataDir = mkdtempSync(join(tmpdir(), 'nextcowork-theme-'))
  // ★ 两层不能合成一个目录。旧的 `themes/` 在 profile 根,新位置在 `data/attachments/`
  //   下 —— 合成一层的话「搬过去」和「原地没动」长得一模一样,这个文件就白写了。
  dataDir = join(userDataDir, DATA_SUBDIRNAME)
  openDatabase(dataDir)
})

afterEach(() => {
  closeDatabase()
  rmSync(userDataDir, { recursive: true, force: true })
})

function seedLegacy(files: Record<string, string>): void {
  mkdirSync(legacyDir(), { recursive: true })
  for (const [name, body] of Object.entries(files)) {
    writeFileSync(join(legacyDir(), name), body)
  }
}

describe('migrateLegacyThemesDir', () => {
  it('把旧目录里的文件搬到 attachments/themes/ 并删掉旧目录', () => {
    seedLegacy({ 'img_A.png': 'A', 'index.json': '[]' })

    migrateLegacyThemesDir()

    expect(readdirSync(newDir()).sort()).toEqual(['img_A.png', 'index.json'])
    expect(existsSync(legacyDir())).toBe(false)
  })

  it('★ 幂等:再跑一次不报错、不改变结果', () => {
    seedLegacy({ 'img_A.png': 'A' })
    migrateLegacyThemesDir()
    migrateLegacyThemesDir()

    expect(readdirSync(newDir())).toEqual(['img_A.png'])
  })

  it('★ 目标已存在时跳过而不是覆盖 —— 那意味着上次已经搬过,旧位置的是副本', () => {
    mkdirSync(newDir(), { recursive: true })
    writeFileSync(join(newDir(), 'img_A.png'), 'NEW')
    seedLegacy({ 'img_A.png': 'OLD' })

    migrateLegacyThemesDir()

    // 现役的那份没被旧副本盖掉
    expect(readdirSync(newDir())).toEqual(['img_A.png'])
    expect(existsSync(legacyDir())).toBe(false)
  })

  it('旧目录不存在时是空操作', () => {
    expect(() => { migrateLegacyThemesDir() }).not.toThrow()
  })
})

describe('清理不碰主题图', () => {
  /**
   * ★ 这是把主题图搬进附件根之后**最危险**的一条。
   *
   * `cleanupAttachments` 的孤儿判据是「不在 attachments 表里就删」,
   * 而主题图由 `index.json` 管、从不进那张表。扫全根的话,
   * 用户传的每一张主题图都会在下一次清理时静默消失。
   */
  it('★ themes/ 下的文件不被当作孤儿删除', () => {
    const dir = newDir()
    mkdirSync(dir, { recursive: true })
    const theme = join(dir, 'img_01J8ABC.png')
    writeFileSync(theme, 'THEME')
    writeFileSync(join(dir, 'index.json'), '[]')

    cleanupAttachments()

    expect(existsSync(theme)).toBe(true)
    expect(existsSync(join(dir, 'index.json'))).toBe(true)
  })

  it('sessions/ 下的真孤儿仍然照删 —— 分治不是放弃清理', () => {
    const dir = join(attachmentRoot(), 'sessions', 'S1')
    mkdirSync(dir, { recursive: true })
    const orphan = join(dir, '01J8ORPHAN.png')
    writeFileSync(orphan, 'O')

    cleanupAttachments()

    expect(existsSync(orphan)).toBe(false)
  })
})
