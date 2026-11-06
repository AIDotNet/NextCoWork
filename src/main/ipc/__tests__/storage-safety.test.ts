import {
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync
} from 'node:fs'
import { randomBytes } from 'node:crypto'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

const electron = vi.hoisted(() => ({
  openDialog: vi.fn(),
  saveDialog: vi.fn(),
  openPath: vi.fn(),
  quit: vi.fn()
}))

const secretState = vi.hoisted(() => ({
  values: new Map<string, string>(),
  setHook: undefined as ((ref: string, value: string) => Promise<void>) | undefined
}))
const runState = vi.hoisted(() => ({ ids: [] as string[], sessionOperation: false }))

vi.mock('electron', () => ({
  app: {
    getVersion: (): string => '0.1.0-test',
    quit: electron.quit
  },
  dialog: {
    showOpenDialog: electron.openDialog,
    showSaveDialog: electron.saveDialog
  },
  shell: { openPath: electron.openPath }
}))

vi.mock('../../window/registry', () => ({
  windows: { emitToAll: vi.fn(), broadcast: vi.fn() }
}))

vi.mock('../../kernel/run-registry', () => ({
  runs: { activeRunIds: (): string[] => [...runState.ids], hasSessionOperations: (): boolean => runState.sessionOperation }
}))

vi.mock('../../runtime', () => ({
  getHost: () => ({
    secrets: {
      available: (): boolean => true,
      get: async (ref: string): Promise<string | null> => secretState.values.get(ref) ?? null,
      set: async (ref: string, value: string): Promise<void> => {
        secretState.values.set(ref, value)
        await secretState.setHook?.(ref, value)
      },
      remove: async (ref: string): Promise<void> => {
        secretState.values.delete(ref)
      }
    }
  })
}))

import {
  DATA_SUBDIRNAME,
  DB_FILENAME,
  closeDatabase,
  databaseFilePath,
  openDatabase,
  txAsync
} from '../../db'
import * as repo from '../../db/repo'
import { switchConfigProfile } from '../../db/config-profile'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { store } from '../../state/store'
import {
  CREDENTIAL_KEY_FILENAME,
  decryptCredentialValue,
  encryptCredentialValue
} from '../../secrets/credential-crypto'
import {
  chooseBackupDirectory,
  cleanupAttachments,
  cleanupByAge,
  cleanupPreview,
  clearHistory,
  clearLocalData,
  createBackup,
  exportData,
  getBackupStatus,
  getStats,
  getStatsAsync,
  importApply,
  importPreview,
  restoreBackup
} from '../storage'

/** Electron profile 根 —— Chromium 的条目铺在这一层。 */
let root = ''
/** 数据根 —— profile 根下的 `data/`,我们自己的库和文件树在这一层。 */
let dataRoot = ''
let outside = ''

beforeEach(() => {
  closeDatabase()
  root = mkdtempSync(join(tmpdir(), 'nextcowork-storage-root-'))
  dataRoot = join(root, DATA_SUBDIRNAME)
  outside = mkdtempSync(join(tmpdir(), 'nextcowork-storage-outside-'))
  openDatabase(dataRoot)
  electron.openDialog.mockReset()
  electron.saveDialog.mockReset()
  electron.openPath.mockReset()
  electron.quit.mockReset()
  secretState.values.clear()
  secretState.setHook = undefined
  runState.ids = []
  runState.sessionOperation = false
})

afterEach(() => {
  closeDatabase()
  rmSync(root, { recursive: true, force: true })
  rmSync(outside, { recursive: true, force: true })
})

function select(path: string): void {
  electron.openDialog.mockResolvedValueOnce({ canceled: false, filePaths: [path] })
}

async function makeBackup(directory: string): Promise<string> {
  store.updateSettings({ data: { backupDirectory: directory } })
  const status = await createBackup({ manual: true })
  if (status.lastBackupPath === null) throw new Error('backup path missing')
  return status.lastBackupPath
}

describe('备份目录路径边界', () => {
  it.each([
    ['数据根目录', (dataRoot: string) => dataRoot],
    ['附件目录', (dataRoot: string) => join(dataRoot, 'attachments')],
    ['附件子目录', (dataRoot: string) => join(dataRoot, 'attachments', 'sessions')],
    ['数据库文件', (dataRoot: string) => join(dataRoot, DB_FILENAME)]
  ])('拒绝%s', async (_label, target) => {
    const path = target(dataRoot)
    select(path)
    await expect(chooseBackupDirectory()).rejects.toThrow(/备份目录|数据目录/)
  })

  it('拒绝符号链接目录，即使链接目标位于数据树外', async () => {
    const link = join(root, '..', `nextcowork-backup-link-${process.pid}-${Date.now()}`)
    symlinkSync(outside, link, 'dir')
    try {
      select(link)
      await expect(chooseBackupDirectory()).rejects.toThrow(/备份目录/)
    } finally {
      rmSync(link, { force: true })
    }
  })

  it('接受存在且可写的外部目录并写入本机设置', async () => {
    select(outside)
    await expect(chooseBackupDirectory()).resolves.toBe(outside)
    expect(store.getSettings().data.backupDirectory).toBe(outside)
  })
})

describe('统计与清理不越过符号链接和外部引用', () => {
  it('附件统计只读取链接本身，不读取外部目标文件大小', async () => {
    const sessions = join(dataRoot, 'attachments', 'sessions')
    mkdirSync(sessions, { recursive: true })
    const target = join(outside, 'large.bin')
    writeFileSync(target, Buffer.alloc(2 * 1024 * 1024, 7))
    symlinkSync(target, join(sessions, 'external-link'))

    const stats = getStats()
    expect(stats.attachmentBytes).toBeGreaterThan(0)
    expect(stats.attachmentBytes).toBeLessThan(lstatSync(target).size)
    await expect(getStatsAsync()).resolves.toEqual(stats)
  })

  it('历史清理预览不把外部附件或已经丢失的记录冒充为可释放空间', () => {
    const external = join(outside, 'external-image.png')
    writeFileSync(external, Buffer.alloc(2 * 1024 * 1024, 1))
    repo.ensureSession({ id: 's', workspaceId: 'w', title: 'External image' })
    repo.commitMessage('s', {
      id: 'm',
      role: 'user',
      parts: [{ type: 'image', mime: 'image/png', dataRef: external }],
      createdAt: 1,
      schemaVersion: 1
    })

    const preview = cleanupPreview({ kind: 'history' })
    expect(preview.attachmentCount).toBe(1)
    expect(preview.bytes).toBeLessThan(lstatSync(external).size)
    expect(existsSync(external)).toBe(true)
  })
})

describe('恢复保留本机专属备份状态', () => {
  it('把程序密文与它对应的主密钥作为同一个恢复单元', async () => {
    const backupDirectory = join(outside, 'credential-backups')
    mkdirSync(backupDirectory)
    const keyPath = join(dataRoot, CREDENTIAL_KEY_FILENAME)
    const backupKey = randomBytes(32)
    writeFileSync(keyPath, backupKey, { mode: 0o600 })
    repo.putCredential('provider:backup', encryptCredentialValue(backupKey, 'sk-from-backup'))
    const archive = await makeBackup(backupDirectory)

    const laterKey = randomBytes(32)
    writeFileSync(keyPath, laterKey)
    repo.putCredential('provider:later', encryptCredentialValue(laterKey, 'sk-later'))
    select(archive)
    await restoreBackup({ confirm: false }, 51)
    await expect(restoreBackup({ confirm: true }, 51)).resolves.toMatchObject({ restored: true })

    const restoredKey = readFileSync(keyPath)
    expect(restoredKey).toEqual(backupKey)
    const restored = repo.getCredential('provider:backup')
    expect(restored).toBeDefined()
    expect(decryptCredentialValue(restoredKey, restored!)).toBe('sk-from-backup')
    expect(repo.getCredential('provider:later')).toBeUndefined()
  })

  it('恢复另一份数据库后仍保留当前设备的 Shell 选择', async () => {
    const backupDirectory = join(outside, 'shell-backups')
    mkdirSync(backupDirectory)
    store.updateSettings({ shell: 'powershell' })
    const archive = await makeBackup(backupDirectory)
    store.updateSettings({ shell: 'zsh' })
    select(archive)
    await restoreBackup({ confirm: false }, 41)
    await expect(restoreBackup({ confirm: true }, 41)).resolves.toMatchObject({ restored: true })
    expect(store.getSettings().shell).toBe('zsh')
  })

  /**
   * ★ manifest 的 sessionCount 来自 `listAllSessionDetails()`,而校验端拿的是
   * **裸** `SELECT COUNT(*) FROM sessions`。子代理转录在表里有真行,所以
   * 一旦有人图省事在 `listAllSessionDetails` 上加了 `parent_session_id IS NULL`
   * 过滤(那是**导出**该做的事,不是备份),两边就对不上 ——
   * 症状是**每一个新建的备份都恢复不了**,而且和会话数量有关、难以复现。
   */
  it('库里有子代理转录时，备份 manifest 与校验仍然对得上', async () => {
    const backupDirectory = join(outside, 'subagent-backups')
    mkdirSync(backupDirectory)
    repo.ensureSession({ id: 'parent', workspaceId: 'w', title: '父对话' })
    repo.ensureSession({ id: 'parent:sub:r:sub:1', workspaceId: 'w', parentSessionId: 'parent' })

    const archive = await makeBackup(backupDirectory)
    select(archive)

    // 预览能读出来就说明校验没抛;数字数的是库里的行,子转录也算在内
    expect((await restoreBackup({ confirm: false }, 21))?.preview?.sessionCount).toBe(2)
  })

  it('备份计数和密文标记覆盖全部账户，而不是当前可见的会话', async () => {
    const key = randomBytes(32)
    writeFileSync(join(dataRoot, CREDENTIAL_KEY_FILENAME), key, { mode: 0o600 })
    for (const [scope, count] of [['account-a', 2], ['account-b', 1]] as const) {
      switchConfigProfile(scope)
      const workspaceId = `workspace-${scope}`
      store.putWorkspace({
        id: workspaceId, name: scope, rootPath: outside,
        settings: { ...DEFAULT_WORKSPACE_SETTINGS }, createdAt: 1, lastOpenedAt: 1
      })
      const sessionId = `session-${scope}`
      repo.ensureSession({ id: sessionId, workspaceId, title: scope })
      for (let index = 0; index < count; index++) {
        repo.commitMessage(sessionId, {
          id: `${sessionId}-${String(index)}`, role: 'user',
          parts: [{ type: 'text', text: scope }], createdAt: index + 1, schemaVersion: 1
        })
      }
      if (scope === 'account-a') repo.putCredential('provider:account-a', encryptCredentialValue(key, 'scoped-secret'))
    }
    expect(repo.getCredential('provider:account-a')).toBeUndefined()
    expect(repo.listAllSessionDetails()).toHaveLength(1)
    const archive = await makeBackup(outside)
    select(archive)
    expect((await restoreBackup({ confirm: false }, 24))?.preview).toMatchObject({
      sessionCount: 2, messageCount: 3, manifest: { encryptedCredentials: true }
    })
  })

  it('当前目录为 null 时，不接受归档中的另一台设备路径', async () => {
    const sourceDirectory = join(outside, 'source-backups')
    mkdirSync(sourceDirectory)
    repo.ensureSession({ id: 'from-backup', workspaceId: 'w', title: 'Backup' })
    const archive = await makeBackup(sourceDirectory)

    store.updateSettings({ data: { backupDirectory: null } })
    select(archive)
    expect((await restoreBackup({ confirm: false }, 11))?.preview?.sessionCount).toBe(1)
    await expect(restoreBackup({ confirm: true }, 11)).resolves.toMatchObject({ restored: true })

    expect(store.getSettings().data.backupDirectory).toBeNull()
    expect(getBackupStatus()).toMatchObject({
      directory: null,
      lastBackupAt: null,
      lastBackupPath: null
    })
  })

  it('当前外部目录及其成功状态在恢复后保持不变', async () => {
    const sourceDirectory = join(outside, 'source-backups')
    const localDirectory = join(outside, 'local-backups')
    mkdirSync(sourceDirectory)
    mkdirSync(localDirectory)
    repo.ensureSession({ id: 'from-backup', workspaceId: 'w', title: 'Backup' })
    const sourceArchive = await makeBackup(sourceDirectory)
    const localArchive = await makeBackup(localDirectory)
    const localStatus = getBackupStatus()

    select(sourceArchive)
    await restoreBackup({ confirm: false }, 12)
    await restoreBackup({ confirm: true }, 12)

    expect(store.getSettings().data.backupDirectory).toBe(localDirectory)
    expect(getBackupStatus()).toMatchObject({
      directory: localDirectory,
      lastBackupAt: localStatus.lastBackupAt,
      lastBackupPath: localArchive,
      lastError: null
    })
  })

  it('确认属于发起预览的窗口，另一窗口不能确认', async () => {
    const backupDirectory = join(outside, 'backups')
    mkdirSync(backupDirectory)
    const archive = await makeBackup(backupDirectory)
    select(archive)
    await restoreBackup({ confirm: false }, 21)

    await expect(restoreBackup({ confirm: true }, 22)).rejects.toThrow(/没有待确认/)
    await expect(restoreBackup({ confirm: true }, 21)).resolves.toMatchObject({ restored: true })
  })

  it('存在运行中的 Agent 时阻止确认，停止后仍可确认同一预览', async () => {
    const backupDirectory = join(outside, 'backups')
    mkdirSync(backupDirectory)
    const archive = await makeBackup(backupDirectory)
    select(archive)
    await restoreBackup({ confirm: false }, 23)

    runState.ids = ['active-run']
    await expect(restoreBackup({ confirm: true }, 23)).rejects.toThrow(/运行中的 Agent/)
    runState.ids = []
    await expect(restoreBackup({ confirm: true }, 23)).resolves.toMatchObject({ restored: true })
  })

  it('没有运行中的 Agent 时，压缩或 run 收尾也阻止恢复', async () => {
    const archive = await makeBackup(outside)
    select(archive)
    await restoreBackup({ confirm: false }, 25)
    runState.sessionOperation = true
    await expect(restoreBackup({ confirm: true }, 25)).rejects.toThrow(/历史操作/)
    runState.sessionOperation = false
    await expect(restoreBackup({ confirm: true }, 25)).resolves.toMatchObject({ restored: true })
  })

  it('预览后文件损坏时不改当前目录和备份状态', async () => {
    const backupDirectory = join(outside, 'backups')
    mkdirSync(backupDirectory)
    const archive = await makeBackup(backupDirectory)
    const beforeSettings = structuredClone(store.getSettings())
    const beforeStatus = getBackupStatus()

    select(archive)
    await restoreBackup({ confirm: false }, 31)
    writeFileSync(archive, 'corrupt after preview')
    await expect(restoreBackup({ confirm: true }, 31)).rejects.toThrow(/ZIP|备份/)

    expect(store.getSettings()).toEqual(beforeSettings)
    expect(getBackupStatus()).toEqual(beforeStatus)
  })
})

describe('备份带上全局 settings.json', () => {
  const globalPath = (): string => join(dataRoot, 'settings.json')
  const hooks = (command: string): string =>
    JSON.stringify({ version: 1, hooks: { Stop: [{ id: 'h1', command, timeout: 60 }] } })

  it('★ 钩子进包，并且能还原回来 —— 只还原一半的话，用户拿回了会话却丢了钩子', async () => {
    const directory = join(outside, 'hook-backups')
    mkdirSync(directory)
    writeFileSync(globalPath(), hooks('notify.sh'))
    const archive = await makeBackup(directory)

    // 备份之后被改掉，恢复应该把它盖回去
    writeFileSync(globalPath(), hooks('别的东西.sh'))
    select(archive)
    await restoreBackup({ confirm: false }, 41)
    await restoreBackup({ confirm: true }, 41)

    expect(JSON.parse(readFileSync(globalPath(), 'utf8')).hooks.Stop[0].command).toBe('notify.sh')
  })

  it('★ 包里没有这个条目时不动磁盘上现有的那份 —— 备份里没有不等于用户想删掉它', async () => {
    const directory = join(outside, 'no-hook-backups')
    mkdirSync(directory)
    // 备份的时候没有全局设置
    const archive = await makeBackup(directory)
    // 之后才配了钩子
    writeFileSync(globalPath(), hooks('后来配的.sh'))

    select(archive)
    await restoreBackup({ confirm: false }, 42)
    await restoreBackup({ confirm: true }, 42)

    expect(existsSync(globalPath())).toBe(true)
    expect(JSON.parse(readFileSync(globalPath(), 'utf8')).hooks.Stop[0].command).toBe('后来配的.sh')
  })

  it('没有全局设置时备份照常能做、能恢复', async () => {
    const directory = join(outside, 'plain-backups')
    mkdirSync(directory)
    repo.ensureSession({ id: 's1', workspaceId: 'w', title: '只有会话' })
    const archive = await makeBackup(directory)
    select(archive)
    await restoreBackup({ confirm: false }, 43)
    await expect(restoreBackup({ confirm: true }, 43)).resolves.toMatchObject({ restored: true })
  })
})

describe('加密凭证导入跟随冲突合并结果', () => {
  it('新供应商的归档 ref 会映射为本机派生 ref', async () => {
    store.putProvider({
      id: 'portable',
      name: 'Portable',
      protocol: 'anthropic',
      baseUrl: 'https://portable.invalid',
      credentialRef: 'legacy:foreign-ref',
      priority: 0,
      enabled: true
    })
    secretState.values.set('legacy:foreign-ref', 'source-secret')
    const exported = join(outside, 'portable.json')
    electron.saveDialog.mockResolvedValueOnce({ canceled: false, filePath: exported })
    await exportData({ includeEncryptedKeys: true, password: 'password-123' })

    store.removeProvider('portable')
    secretState.values.clear()
    select(exported)
    await importPreview(41)
    await importApply({ password: 'password-123' }, 41)

    expect(store.listProviders().find((item) => item.id === 'portable')?.credentialRef)
      .toBe('provider:portable')
    expect(secretState.values.get('provider:portable')).toBe('source-secret')
    expect(secretState.values.has('legacy:foreign-ref')).toBe(false)
  })

  it('冲突规则跳过本地配置时，不会旁路覆盖它的密钥', async () => {
    store.putProvider({
      id: 'same',
      name: 'Incoming older',
      protocol: 'anthropic',
      baseUrl: 'https://incoming.invalid',
      credentialRef: 'source:same',
      priority: 0,
      enabled: true,
      updatedAt: 10
    } as Parameters<typeof store.putProvider>[0])
    secretState.values.set('source:same', 'incoming-secret')
    const exported = join(outside, 'older.json')
    electron.saveDialog.mockResolvedValueOnce({ canceled: false, filePath: exported })
    await exportData({ includeEncryptedKeys: true, password: 'password-123' })

    store.putProvider({
      id: 'same',
      name: 'Local newer',
      protocol: 'anthropic',
      baseUrl: 'https://local.invalid',
      credentialRef: 'local:same',
      priority: 0,
      enabled: true,
      updatedAt: 20
    } as Parameters<typeof store.putProvider>[0])
    secretState.values.clear()
    secretState.values.set('local:same', 'local-secret')

    select(exported)
    const preview = await importPreview(42)
    expect(preview?.skippedCount).toBeGreaterThan(0)
    await importApply({ password: 'password-123' }, 42)

    expect(store.listProviders().find((item) => item.id === 'same')).toMatchObject({
      name: 'Local newer',
      credentialRef: 'local:same'
    })
    expect(secretState.values.get('local:same')).toBe('local-secret')
    expect(secretState.values.has('source:same')).toBe(false)
  })

  it('凭证写入失败时回滚自己的配置和宿主凭证，不覆盖排队事务', async () => {
    for (const id of ['rollback-first', 'rollback-second']) {
      store.putProvider({ id, name: id, protocol: 'anthropic', baseUrl: 'https://source.invalid', credentialRef: `provider:${id}`, priority: 0, enabled: true })
      secretState.values.set(`provider:${id}`, `source-${id}`)
    }
    const exported = join(outside, 'failed-import.json')
    electron.saveDialog.mockResolvedValueOnce({ canceled: false, filePath: exported })
    await exportData({ includeEncryptedKeys: true, password: 'password-123' })
    store.removeProvider('rollback-first')
    store.removeProvider('rollback-second')
    secretState.values.clear()
    const before = structuredClone(store.getSettings())
    select(exported)
    await importPreview(45)
    let reached: () => void = () => {}
    let release: () => void = () => {}
    const started = new Promise<void>((resolve) => { reached = resolve })
    const gate = new Promise<void>((resolve) => { release = resolve })
    secretState.setHook = async (ref) => {
      if (ref === 'provider:rollback-second') {
        reached()
        await gate
        throw new Error('credential-write-failed')
      }
    }
    const imported = importApply({ password: 'password-123' }, 45)
    const failed = imported.catch((error: unknown) => error)
    await started
    let queuedStarted = false
    const queued = txAsync(() => { queuedStarted = true; repo.setKv('unrelated-after-import', 'keep') })
    await Promise.resolve()
    expect(queuedStarted).toBe(false)
    release()
    expect(await failed).toMatchObject({ message: 'credential-write-failed' })
    await queued
    expect(repo.getKv('unrelated-after-import', null)).toBe('keep')
    expect(store.listProviders().some((provider) => provider.id.startsWith('rollback-'))).toBe(false)
    expect(secretState.values.size).toBe(0)
    expect(store.getSettings()).toEqual(before)
  })

  it('密码错误时不修改任何配置或密钥', async () => {
    store.putProvider({
      id: 'protected',
      name: 'Protected source',
      protocol: 'anthropic',
      baseUrl: 'https://source.invalid',
      credentialRef: 'source:protected',
      priority: 0,
      enabled: true
    })
    secretState.values.set('source:protected', 'source-secret')
    const exported = join(outside, 'protected.json')
    electron.saveDialog.mockResolvedValueOnce({ canceled: false, filePath: exported })
    await exportData({ includeEncryptedKeys: true, password: 'correct-password' })

    store.removeProvider('protected')
    secretState.values.clear()
    select(exported)
    await importPreview(43)
    await expect(importApply({ password: 'wrong-password' }, 43)).rejects.toThrow(/密码验证失败/)

    expect(store.listProviders().some((item) => item.id === 'protected')).toBe(false)
    expect(secretState.values.size).toBe(0)
  })

  it('较新的 MCP 配置切换传输类型时移除旧类型的孤儿凭证', async () => {
    store.putMcpServer({
      id: 'portable-mcp',
      name: 'Portable HTTP',
      transport: 'streamable-http',
      url: 'https://mcp.example.invalid',
      headerNames: ['Authorization'],
      enabled: false,
      updatedAt: 20
    } as Parameters<typeof store.putMcpServer>[0])
    secretState.values.set('mcp:portable-mcp:headers', '{"Authorization":"source"}')
    const exported = join(outside, 'mcp-transport.json')
    electron.saveDialog.mockResolvedValueOnce({ canceled: false, filePath: exported })
    await exportData({ includeEncryptedKeys: true, password: 'password-123' })

    store.putMcpServer({
      id: 'portable-mcp',
      name: 'Local stdio',
      transport: 'stdio',
      command: 'npx',
      args: [],
      envNames: ['TOKEN'],
      enabled: false,
      updatedAt: 10
    } as Parameters<typeof store.putMcpServer>[0])
    secretState.values.set('mcp:portable-mcp:env', '{"TOKEN":"old-local"}')

    select(exported)
    await importPreview(44)
    await importApply({ password: 'password-123' }, 44)

    expect(store.getMcpServer('portable-mcp')?.transport).toBe('streamable-http')
    expect(secretState.values.has('mcp:portable-mcp:env')).toBe(false)
    expect(secretState.values.get('mcp:portable-mcp:headers'))
      .toBe('{"Authorization":"source"}')
  })
})

describe('原生对话框与文件格式失败路径', () => {
  it('用户取消选择时不制造成功结果', async () => {
    electron.openDialog.mockResolvedValue({ canceled: true, filePaths: [] })
    await expect(chooseBackupDirectory()).resolves.toBeNull()
    await expect(importPreview(51)).resolves.toBeNull()
    await expect(restoreBackup({ confirm: false }, 51)).resolves.toBeNull()
  })

  it('未来版本导出在预览阶段即被拒绝', async () => {
    const exported = join(outside, 'future.json')
    electron.saveDialog.mockResolvedValueOnce({ canceled: false, filePath: exported })
    await exportData({})
    const parsed = JSON.parse(readFileSync(exported, 'utf8')) as Record<string, unknown>
    parsed.version = 999
    writeFileSync(exported, JSON.stringify(parsed))

    select(exported)
    await expect(importPreview(52)).rejects.toThrow(/版本高于当前应用/)
  })

  it('保存目标不可写成文件时导出失败且不伪装成功', async () => {
    electron.saveDialog.mockResolvedValueOnce({ canceled: false, filePath: outside })
    await expect(exportData({})).rejects.toThrow()
  })
})

describe('历史操作的全库删除保护', () => {
  it.each([
    ['附件清理', () => cleanupAttachments()],
    ['按年龄清理', () => cleanupByAge({ age: '15d' })],
    ['清空历史', () => clearHistory()],
    ['删除全部数据', () => clearLocalData({ confirm: true })]
  ])('%s 不绕过压缩或运行收尾的互斥', (_name, action) => {
    repo.ensureSession({ id: 'protected-session', workspaceId: 'w' })
    runState.sessionOperation = true
    expect(action).toThrow(/历史操作/)
    expect(repo.getSession('protected-session')).toBeDefined()
    expect(existsSync(join(dataRoot, DB_FILENAME))).toBe(true)
    expect(electron.quit).not.toHaveBeenCalled()
  })
})

describe('删除并退出的文件系统边界', () => {
  it('删除受管数据和链接本身，但保留外部目标、外部备份与 Claude 目录', () => {
    const managed = join(dataRoot, 'attachments', 'sessions', 's')
    mkdirSync(managed, { recursive: true })
    writeFileSync(join(managed, 'managed.txt'), 'managed')

    const externalTarget = join(outside, 'target.txt')
    writeFileSync(externalTarget, 'keep target')
    const link = join(managed, 'external-link')
    symlinkSync(externalTarget, link)

    const backupDirectory = join(outside, 'backups')
    mkdirSync(backupDirectory)
    writeFileSync(join(backupDirectory, 'keep.ncwbackup'), 'keep backup')
    store.updateSettings({ data: { backupDirectory } })

    const claude = join(root, '.claude')
    mkdirSync(claude)
    writeFileSync(join(claude, 'shared.json'), 'keep claude')
    /*
      ★ 插件宿主和浏览器工作区的 `persist:` 分区落在 `Partitions/` 里。它一直不在
      受管清单上,「删除全部数据」从来没清掉过那两处的登录态 —— 这条用例就是钉死
      它现在会被清掉。
    */
    const partition = join(root, 'Partitions', 'plugin-host', 'Local Storage')
    mkdirSync(partition, { recursive: true })
    writeFileSync(join(partition, 'leveldb.log'), 'plugin host session')
    const collisionWal = join(dataRoot, 'nextcowork 3.db-wal')
    const collisionShm = join(dataRoot, 'nextcowork 3.db-shm')
    const chromiumDb = join(root, 'declarative_performance_observer.db-journal')
    const dipsCollision = join(root, 'DIPS-wal 4')
    const devToolsMarker = join(root, 'DevToolsActivePort')
    const migratedInstructions = join(dataRoot, 'AGENTS.md')
    for (const path of [
      collisionWal,
      collisionShm,
      chromiumDb,
      dipsCollision,
      devToolsMarker,
      migratedInstructions
    ]) writeFileSync(path, 'managed profile data')
    const unrelated = join(root, 'user-note.txt')
    writeFileSync(unrelated, 'keep unrelated file')
    expect(databaseFilePath()).not.toBeNull()

    expect(clearLocalData({ confirm: true })).toEqual({ deleted: true })

    expect(existsSync(join(managed, 'managed.txt'))).toBe(false)
    expect(existsSync(link)).toBe(false)
    expect(readFileSync(externalTarget, 'utf8')).toBe('keep target')
    expect(readFileSync(join(backupDirectory, 'keep.ncwbackup'), 'utf8')).toBe('keep backup')
    expect(readFileSync(join(claude, 'shared.json'), 'utf8')).toBe('keep claude')
    expect(existsSync(join(root, 'Partitions'))).toBe(false)
    for (const path of [
      collisionWal,
      collisionShm,
      chromiumDb,
      dipsCollision,
      devToolsMarker,
      migratedInstructions
    ]) expect(existsSync(path)).toBe(false)
    expect(readFileSync(unrelated, 'utf8')).toBe('keep unrelated file')
    expect(existsSync(join(dataRoot, DB_FILENAME))).toBe(false)
    expect(electron.quit).toHaveBeenCalledOnce()
  })

  it('有运行中的 Agent 时不删除也不退出', () => {
    const managed = join(root, 'cache', 'keep-on-block.txt')
    mkdirSync(join(root, 'cache'))
    writeFileSync(managed, 'keep')
    runState.ids = ['active-run']

    expect(() => clearLocalData({ confirm: true })).toThrow(/运行中的 Agent/)
    expect(readFileSync(managed, 'utf8')).toBe('keep')
    expect(electron.quit).not.toHaveBeenCalled()
  })
})
