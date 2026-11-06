/**
 * 「删除并退出」遇到被占用的 Chromium profile 目录。
 *
 * 真实症状只在 Windows 上出现：主进程活着时 Chromium 一直握着 `GPUCache` /
 * `Cookies` / `Network` 里的文件句柄，Windows 不允许 rename 一棵内部有打开句柄的
 * 目录，于是暂存第一步就 `EPERM`。旧代码让它整体回滚 —— 用户点了「永久删除并退出」，
 * 结果一个字节都没删掉。
 *
 * mock 掉 `renameSync` 是唯一能在 macOS/Linux 上复现它的办法：POSIX 的 rename
 * 根本不看打开句柄，拿真文件怎么锁都锁不出这个错。
 *
 * ★ 单独一个文件，不并进 storage-safety.test.ts —— `vi.mock('node:fs')` 是模块级的，
 *   混进那套大用例里会让每一条都跑在被改写过的 fs 上。
 */
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const electron = vi.hoisted(() => ({ quit: vi.fn() }))
const runState = vi.hoisted(() => ({ ids: [] as string[] }))
/** 这一轮要假装「被别的进程占着」的源路径。 */
const locked = vi.hoisted(() => ({ paths: new Set<string>() }))

vi.mock('electron', () => ({
  app: { getVersion: (): string => '0.1.0-test', quit: electron.quit },
  dialog: { showOpenDialog: vi.fn(), showSaveDialog: vi.fn() },
  shell: { openPath: vi.fn() }
}))

vi.mock('../../window/registry', () => ({
  windows: { emitToAll: vi.fn(), broadcast: vi.fn() }
}))

vi.mock('../../kernel/run-registry', () => ({
  runs: { activeRunIds: (): string[] => [...runState.ids], hasSessionOperations: (): boolean => false }
}))

vi.mock('../../runtime', () => ({
  getHost: () => ({
    secrets: {
      available: (): boolean => true,
      get: async (): Promise<string | null> => null,
      set: async (): Promise<void> => {},
      remove: async (): Promise<void> => {}
    }
  })
}))

vi.mock('node:fs', async (importActual) => {
  const actual = await importActual<typeof import('node:fs')>()
  return {
    ...actual,
    default: actual,
    renameSync: (from: Parameters<typeof actual.renameSync>[0], to: Parameters<typeof actual.renameSync>[1]): void => {
      if (locked.paths.has(String(from))) {
        throw Object.assign(
          new Error(`EPERM: operation not permitted, rename '${String(from)}' -> '${String(to)}'`),
          { code: 'EPERM', syscall: 'rename' }
        )
      }
      actual.renameSync(from, to)
    }
  }
})

import { DATA_SUBDIRNAME, DB_FILENAME, closeDatabase, openDatabase } from '../../db'
import { PENDING_DELETE_FILENAME } from '../pending-delete'
import { clearLocalData } from '../storage'

/**
 * Electron profile 根。Chromium 的东西铺在这一层,补删清单也落在这里。
 */
let root = ''
/** 数据根 —— profile 根下的 `data/`。我们自己的库和文件树在这一层。 */
let dataRoot = ''

/** 建一棵最小的数据树：`data/` 下是我们的数据，根层是 Chromium 的 profile。 */
function seedDataRoot(): { attachment: string; profile: string } {
  const attachment = join(dataRoot, 'attachments', 'sessions', 's1')
  mkdirSync(attachment, { recursive: true })
  writeFileSync(join(attachment, 'note.txt'), 'managed')
  const profile = join(root, 'GPUCache')
  mkdirSync(profile)
  writeFileSync(join(profile, 'shader.bin'), 'cached')
  return { attachment, profile }
}

function pendingPaths(): string[] {
  const file = join(root, PENDING_DELETE_FILENAME)
  if (!existsSync(file)) return []
  return (JSON.parse(readFileSync(file, 'utf8')) as { paths: string[] }).paths
}

beforeEach(() => {
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'nextcowork-locked-root-'))
  dataRoot = join(root, DATA_SUBDIRNAME)
  openDatabase(dataRoot)
  locked.paths.clear()
  electron.quit.mockReset()
  runState.ids = []
  vi.spyOn(console, 'error').mockImplementation(() => {})
})

afterEach(() => {
  vi.restoreAllMocks()
  closeDatabase()
  rmSync(root, { recursive: true, force: true })
})

describe('被占用的 Chromium profile 目录', () => {
  it('删不掉的 GPUCache 不再拖垮整次删除，改为记进补删清单', () => {
    const { attachment, profile } = seedDataRoot()
    locked.paths.add(profile)

    expect(clearLocalData({ confirm: true })).toEqual({ deleted: true })

    // 自己的数据真的删掉了 —— 这正是旧代码做不到的那一半。
    expect(existsSync(attachment)).toBe(false)
    expect(existsSync(join(dataRoot, DB_FILENAME))).toBe(false)
    // 占用中的目录原封不动留着，等下次启动补删。
    expect(readFileSync(join(profile, 'shader.bin'), 'utf8')).toBe('cached')
    expect(pendingPaths()).toEqual([profile])
    expect(electron.quit).toHaveBeenCalledOnce()
  })

  it('没有任何路径被占用时不写补删清单', () => {
    seedDataRoot()

    expect(clearLocalData({ confirm: true })).toEqual({ deleted: true })

    expect(existsSync(join(root, PENDING_DELETE_FILENAME))).toBe(false)
    expect(existsSync(join(root, 'GPUCache'))).toBe(false)
  })

  it('Chromium 自己的那几个库文件同样可以推迟', () => {
    seedDataRoot()
    const chromiumDb = join(root, 'declarative_performance_observer.db')
    writeFileSync(chromiumDb, 'chromium owned')
    locked.paths.add(chromiumDb)

    expect(clearLocalData({ confirm: true })).toEqual({ deleted: true })

    expect(readFileSync(chromiumDb, 'utf8')).toBe('chromium owned')
    expect(pendingPaths()).toEqual([chromiumDb])
  })

  it('带碰撞后缀的 nextcowork N.db 归我们,搬不动就回滚', () => {
    const { attachment } = seedDataRoot()
    const collision = join(dataRoot, 'nextcowork 2.db')
    writeFileSync(collision, 'our data')
    locked.paths.add(collision)

    expect(() => clearLocalData({ confirm: true })).toThrow(/暂存/)

    expect(readFileSync(join(attachment, 'note.txt'), 'utf8')).toBe('managed')
    expect(existsSync(join(root, PENDING_DELETE_FILENAME))).toBe(false)
  })

  it('自己的数据搬不动时仍然整体回滚 —— 可降级的只有 Chromium 那张表', () => {
    const { attachment, profile } = seedDataRoot()
    locked.paths.add(join(dataRoot, 'attachments'))

    expect(() => clearLocalData({ confirm: true })).toThrow(/暂存/)

    expect(readFileSync(join(attachment, 'note.txt'), 'utf8')).toBe('managed')
    expect(readFileSync(join(profile, 'shader.bin'), 'utf8')).toBe('cached')
    expect(existsSync(join(root, PENDING_DELETE_FILENAME))).toBe(false)
    expect(electron.quit).not.toHaveBeenCalled()
  })
})
