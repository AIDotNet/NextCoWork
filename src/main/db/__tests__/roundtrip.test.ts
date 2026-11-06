/**
 * **「退出应用重开,配置还在」** —— 步骤 1 的验收条件,后面整个模型管理都建在它上面。
 *
 * 所以这里测的不是「SQL 语句写对了没有」,而是**跨进程边界的那一下**:
 * 每个用例都真的关库、真的重开,再读回来。用真文件不用 `:memory:` ——
 * 内存库上「关了再开」是句空话,而这正是要测的东西。
 *
 * 走 `store` 而不是 `repo`:全应用用的是前者,而这次改动的全部承诺就是
 * 「访问器签名不变、实现换成 SQL」。密钥那条是例外,`store` 上本来就没有
 * 密钥访问器(加解密属于 `main/host/index.ts`),所以直接对 `repo` 测字节保真。
 */
import { mkdtempSync, rmSync, statSync } from 'node:fs'
import { homedir, tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { AgentMessage } from '../../../shared/agent/message'
import type { McpServerConfig } from '../../../shared/domain/mcp'
import { mcpSecretRef } from '../../../shared/domain/mcp'
import { SEARCH_PROVIDER_IDS, searchSecretRef } from '../../../shared/domain/search'
import { DEFAULT_SETTINGS } from '../../../shared/domain/settings'
import { MIGRATIONS } from '../schema'
import { MIGRATION_CLAIM_KEY, MIGRATION_UNDO_KEY } from '../legacy-merge'
import type { ModelAlias, UpstreamProvider } from '../../../shared/domain/provider'
import { anthropicCacheTtlOf } from '../../../shared/domain/provider'
import type { Workspace } from '../../../shared/domain/workspace'
import type { SshConnectionProfile } from '../../../shared/domain/environment'
import { DEFAULT_WORKSPACE_SETTINGS } from '../../../shared/domain/workspace'
import { store } from '../../state/store'
import { DATABASE_DIRNAME, DATA_SUBDIRNAME, DB_FILENAME, DatabaseBusyError, DatabaseTransactionClosedError, closeDatabase, db, defaultDatabaseDirectory, openDatabase, stmt, tx, txAsync } from '../index'
import * as repo from '../repo'
import {
  claimMigratedLocalWorkspaces,
  migrateLegacyLocalProvidersToCurrentAccount,
  physicalCredentialRef,
  switchConfigProfile
} from '../config-profile'

let dir = ''

/** 关掉再开 = 模拟一次退出重启。库文件留在原地,只有句柄换了。 */
const restart = (): void => {
  closeDatabase()
  openDatabase(dir)
}

beforeEach(() => {
  // 先关:前一个用例可能留着句柄,而 openDatabase 对重复打开是抛错的
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-db-'))
  openDatabase(dir)
})

afterEach(() => {
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

const provider = (id: string, priority: number): UpstreamProvider => ({
  id,
  name: id,
  protocol: 'anthropic',
  baseUrl: `https://${id}.invalid`,
  credentialRef: `${id}:key`,
  priority,
  enabled: true
})

const alias = (providerId: string, name: string): ModelAlias => ({
  alias: name,
  providerId,
  upstreamModel: `${providerId}-上游名`,
  capabilities: { tools: true, vision: false, thinking: false, caching: false },
  contextWindow: 100_000,
  maxOutputTokens: 4096
})

const workspace = (id: string): Workspace => ({
  id,
  name: `工作区 ${id}`,
  rootPath: `/tmp/${id}`,
  settings: { ...DEFAULT_WORKSPACE_SETTINGS, defaultModel: 'm' },
  createdAt: 1_700_000_000_000,
  lastOpenedAt: 1_700_000_001_000
})

describe('账户上线前的供应商密钥迁移', () => {
  it('首次登录只复制 local provider/model/key，原件保留且第二次不重复', () => {
    const p = provider('legacy-provider', 1)
    store.putProvider(p)
    store.putAlias(alias(p.id, 'legacy-model'))
    repo.putCredential(p.credentialRef, new Uint8Array([9, 8, 7]))

    switchConfigProfile('account-a')
    migrateLegacyLocalProvidersToCurrentAccount()
    expect(store.listProviders().map((item) => item.id)).toContain(p.id)
    expect(store.listAliases().map((item) => item.alias)).toContain('legacy-model')
    expect(repo.getCredential(p.credentialRef)).toEqual(new Uint8Array([9, 8, 7]))
    const physical = physicalCredentialRef(p.credentialRef, 'account-a')
    expect(stmt('SELECT blob FROM credentials WHERE ref = ?').get(physical)?.['blob']).toEqual(
      new Uint8Array([9, 8, 7])
    )

    migrateLegacyLocalProvidersToCurrentAccount()
    expect(store.listProviders().filter((item) => item.id === p.id)).toHaveLength(1)
    switchConfigProfile(null)
    expect(store.listProviders().map((item) => item.id)).toContain(p.id)
    expect(repo.getCredential(p.credentialRef)).toEqual(new Uint8Array([9, 8, 7]))
  })
})

describe('启动数据迁移的工作区归属', () => {
  it('复制 local 默认工作区并重连迁入会话，登录账户可见且 local 原件保留', () => {
    const migratedWorkspace = workspace('ws-default')
    const localWorkspace = workspace('unrelated-local-workspace')
    store.putWorkspace(migratedWorkspace)
    store.putWorkspace(localWorkspace)
    const migratedSession = store.createSession({ workspaceId: migratedWorkspace.id, title: '迁入的会话' })
    store.setKv(MIGRATION_UNDO_KEY, {
      at: 1,
      source: '/legacy',
      sessions: [migratedSession.id],
      workspaces: [],
      files: []
    })

    switchConfigProfile('account-a')
    expect(store.listWorkspaces()).toEqual([])
    expect(repo.getSession(migratedSession.id)).toBeUndefined()

    expect(claimMigratedLocalWorkspaces('account-a')).toBeGreaterThan(0)
    const [accountWorkspace] = store.listWorkspaces()
    expect(accountWorkspace?.id).not.toBe(migratedWorkspace.id)
    expect(repo.getSession(migratedSession.id)?.workspaceId).toBe(accountWorkspace?.id)
    expect(claimMigratedLocalWorkspaces('account-a')).toBe(0)

    switchConfigProfile('account-b')
    expect(claimMigratedLocalWorkspaces('account-b')).toBe(0)
    expect(store.listWorkspaces()).toEqual([])

    switchConfigProfile(null)
    expect(store.listWorkspaces().map((item) => item.id)).toEqual(
      expect.arrayContaining([migratedWorkspace.id, localWorkspace.id])
    )
  })

  it('已有归属清单时仍会认领后来写进旧撤销清单的会话', () => {
    const migratedWorkspace = workspace('legacy-workspace')
    store.putWorkspace(migratedWorkspace)
    const firstSession = store.createSession({ workspaceId: migratedWorkspace.id, title: '先迁入的会话' })
    store.setKv(MIGRATION_UNDO_KEY, {
      at: 1,
      source: '/legacy',
      sessions: [firstSession.id],
      workspaces: [migratedWorkspace.id],
      files: []
    })

    switchConfigProfile('account-a')
    expect(claimMigratedLocalWorkspaces('account-a')).toBeGreaterThan(0)
    const copiedWorkspaceId = repo.getSession(firstSession.id)?.workspaceId
    if (copiedWorkspaceId === undefined) throw new Error('先迁入会话没有工作区副本')

    switchConfigProfile(null)
    const laterSession = store.createSession({ workspaceId: migratedWorkspace.id, title: '降级期间迁入的会话' })
    // 模拟旧版本覆盖其唯一的撤销清单；已有归属清单不能因此遮住这条会话。
    store.setKv(MIGRATION_UNDO_KEY, {
      at: 2,
      source: '/downgraded',
      sessions: [laterSession.id],
      workspaces: [],
      files: []
    })

    switchConfigProfile('account-a')
    expect(claimMigratedLocalWorkspaces('account-a')).toBeGreaterThan(0)
    expect(repo.getSession(laterSession.id)?.workspaceId).toBe(copiedWorkspaceId)
  })

  it('用户删除迁入工作区后不在下次认领时复活空副本', () => {
    const migratedWorkspace = workspace('legacy-workspace')
    store.putWorkspace(migratedWorkspace)
    const migratedSession = store.createSession({ workspaceId: migratedWorkspace.id, title: '迁入的会话' })
    store.setKv(MIGRATION_UNDO_KEY, {
      at: 1,
      source: '/legacy',
      sessions: [migratedSession.id],
      workspaces: [],
      files: []
    })

    switchConfigProfile('account-a')
    expect(claimMigratedLocalWorkspaces('account-a')).toBeGreaterThan(0)
    const copiedWorkspaceId = repo.getSession(migratedSession.id)?.workspaceId
    if (copiedWorkspaceId === undefined) throw new Error('迁入会话没有工作区副本')
    store.removeWorkspace(copiedWorkspaceId)
    // 模拟旧根补回同一条会话：映射指向已删副本时不能把它再写去一个不存在的 id。
    stmt('UPDATE sessions SET workspace_id = ? WHERE id = ?').run(migratedWorkspace.id, migratedSession.id)

    expect(claimMigratedLocalWorkspaces('account-a')).toBe(0)
    expect(repo.getSession(migratedSession.id)).toBeUndefined()
    expect(store.listWorkspaces()).toEqual([])

    switchConfigProfile(null)
    expect(repo.getSession(migratedSession.id)?.workspaceId).toBe(migratedWorkspace.id)
  })

  it('删除旧副本后，新的待认领会话会获得新的账户工作区', () => {
    const migratedWorkspace = workspace('legacy-workspace')
    store.putWorkspace(migratedWorkspace)
    const firstSession = store.createSession({ workspaceId: migratedWorkspace.id, title: '先迁入的会话' })
    store.setKv(MIGRATION_UNDO_KEY, {
      at: 1,
      source: '/legacy',
      sessions: [firstSession.id],
      workspaces: [migratedWorkspace.id],
      files: []
    })

    switchConfigProfile('account-a')
    expect(claimMigratedLocalWorkspaces('account-a')).toBeGreaterThan(0)
    const deletedWorkspaceId = repo.getSession(firstSession.id)?.workspaceId
    if (deletedWorkspaceId === undefined) throw new Error('先迁入会话没有工作区副本')
    store.removeWorkspace(deletedWorkspaceId)

    switchConfigProfile(null)
    const laterSession = store.createSession({ workspaceId: migratedWorkspace.id, title: '重试迁入的会话' })
    const claim = store.getKv<{
      sessions: string[]
      workspaces: string[]
      claimedBy?: string
      workspaceMap?: Record<string, string>
      pending?: boolean
    }>(MIGRATION_CLAIM_KEY, { sessions: [], workspaces: [] })
    store.setKv(MIGRATION_CLAIM_KEY, {
      ...claim,
      sessions: [...claim.sessions, laterSession.id],
      pending: true
    })

    switchConfigProfile('account-a')
    expect(claimMigratedLocalWorkspaces('account-a')).toBeGreaterThan(0)
    const newWorkspaceId = repo.getSession(laterSession.id)?.workspaceId
    expect(newWorkspaceId).not.toBe(deletedWorkspaceId)
    expect(store.getWorkspace(newWorkspaceId ?? '')).toBeDefined()
  })

  it('远端来源降级为 unbound，且不重连到同路径的远端工作区', () => {
    const migratedWorkspace = {
      ...workspace('legacy-workspace'),
      environment: { kind: 'connection' as const, connectionId: 'legacy-ssh' }
    }
    store.putWorkspace(migratedWorkspace)
    const migratedSession = store.createSession({ workspaceId: migratedWorkspace.id, title: '迁入的会话' })
    store.setKv(MIGRATION_UNDO_KEY, {
      at: 1,
      source: '/legacy',
      sessions: [migratedSession.id],
      workspaces: [migratedWorkspace.id],
      files: []
    })

    switchConfigProfile('account-a')
    const remoteWorkspace = {
      ...workspace('remote-workspace'),
      rootPath: migratedWorkspace.rootPath,
      environment: { kind: 'connection' as const, connectionId: 'ssh-workspace' }
    }
    store.putWorkspace(remoteWorkspace)

    expect(claimMigratedLocalWorkspaces('account-a')).toBeGreaterThan(0)
    const copiedWorkspaceId = repo.getSession(migratedSession.id)?.workspaceId
    expect(copiedWorkspaceId).not.toBe(remoteWorkspace.id)
    expect(store.getWorkspace(copiedWorkspaceId ?? '')?.environment).toEqual({ kind: 'unbound' })
    expect(store.listWorkspaces()).toHaveLength(2)
  })

  it('复用同根目录的账户工作区，不创建重复工作区', () => {
    const migratedWorkspace = workspace('legacy-workspace')
    store.putWorkspace(migratedWorkspace)
    const migratedSession = store.createSession({ workspaceId: migratedWorkspace.id, title: '迁入的会话' })
    store.setKv(MIGRATION_UNDO_KEY, {
      at: 1,
      source: '/legacy',
      sessions: [migratedSession.id],
      workspaces: [migratedWorkspace.id],
      files: []
    })

    switchConfigProfile('account-a')
    const accountWorkspace = { ...workspace('account-workspace'), rootPath: `${migratedWorkspace.rootPath}/` }
    store.putWorkspace(accountWorkspace)

    expect(claimMigratedLocalWorkspaces('account-a')).toBeGreaterThan(0)
    expect(store.listWorkspaces().map((item) => item.id)).toEqual([accountWorkspace.id])
    expect(repo.getSession(migratedSession.id)?.workspaceId).toBe(accountWorkspace.id)
  })
})

describe('Shell 机器本地设置', () => {
  it('默认跟随系统，手动选择在关库重开后仍然保留', () => {
    expect(store.getSettings().shell).toBe('system')
    store.updateSettings({ shell: 'bash' })
    restart()
    expect(store.getSettings().shell).toBe('bash')
    store.updateSettings({ theme: 'dark' })
    expect(store.getSettings().shell).toBe('bash')
  })

  it('切换账户与恢复旧账户快照都保留当前设备的 Shell', () => {
    store.updateSettings({ shell: 'bash' })
    switchConfigProfile('account-a')
    expect(store.getSettings().shell).toBe('bash')
    store.updateSettings({ shell: 'pwsh' })
    switchConfigProfile('account-b')
    expect(store.getSettings().shell).toBe('pwsh')
    store.updateSettings({ shell: 'system' })
    switchConfigProfile('account-a')
    expect(store.getSettings().shell).toBe('system')
    switchConfigProfile(null)
    expect(store.getSettings().shell).toBe('system')
  })

  it('普通导出不携带手动 Shell，导入保留当前设备的选择', () => {
    store.updateSettings({ shell: 'zsh' })
    const exported = repo.exportDataSnapshot()
    expect(exported.settings.shell).toBe('system')
    expect(store.getSettings().shell).toBe('zsh')
    exported.settings.shell = 'powershell'
    exported.settings.theme = 'dark'
    repo.mergeDataExport(exported)
    expect(store.getSettings().shell).toBe('zsh')
    expect(store.getSettings().theme).toBe('dark')
  })
})

describe('★ 关库重开之后,配置一样都不少', () => {
  it('keeps workspace-scoped MCP configurations device-local', () => {
    repo.configureSyncAccount('test-account')
    const server: McpServerConfig = { id: 'remote-mcp', name: 'Remote process', enabled: true, workspaceId: 'remote', transport: 'stdio', command: 'remote-command', args: [], envNames: [] }
    repo.putMcpServer(server)
    expect(stmt("SELECT payload FROM sync_outbox WHERE kind = 'mcpServer'").all()).toEqual([])
    repo.enqueueInitialSyncSnapshot('test-account')
    expect(stmt("SELECT payload FROM sync_outbox WHERE kind = 'mcpServer'").all()).toEqual([])
    restart()
    expect(repo.getMcpServer(server.id)).toEqual(server)
  })
  /*
    ★ 这条用例在 v2 之后换了断言对象,**意图一个字没变**:SSH 连接是设备本地的,
    工作区到它的绑定一个字节都不该离开这台机器。

    v1 的做法是「照样入 outbox,但投影掉 environment / rootPath」,所以当年断言的是
    「那一行在,且里面没有敏感字段」。v2 把明文 outbox **整个停写**了(见 `repo.ts`
    的 `enqueueSyncMutation`:配置写入只打一个脏标记,真正上传走加密快照),
    于是现在能断言的是更强的一条 —— **一行明文都不产生**。

    ⚠️ v2 的上传器还没接上(`encryptSyncDocument` 目前只有测试在调用),所以
    「快照里到底装了什么」暂时无处可断言。等上传链路落地必须把它加回来:
    `workspacePreferences()` 那层投影现在没有任何测试守着。
  */
  it('persists device-only SSH profiles and never syncs workspace bindings', () => {
    const connection: SshConnectionProfile = { id: 'ssh-one', name: 'Private server', kind: 'ssh', enabled: true,
      platform: 'auto', revision: 1, createdAt: 1, updatedAt: 1, target: { kind: 'manual', host: 'private.internal',
        username: 'user', port: 22, identityFile: '/private/key' } }
    // ★ enabled 必须显式给 true:`configureSyncAccount` 的第二个参数默认 false,
    //   不给的话 `enqueueSyncMutation` 一进门就 return,这条用例会变成一句废话。
    repo.configureSyncAccount('test-account', true)
    store.putConnectionProfile(connection)
    store.putWorkspace({ ...workspace('remote'), environment: { kind: 'connection', connectionId: connection.id } })

    // 一行明文都不该有 —— 连接目标、私钥路径、工作区绑定都在这两次写入里
    expect(stmt('SELECT kind, payload FROM sync_outbox').all()).toEqual([])
    // 但同步层必须知道「这个作用域变脏了」,否则下一次快照不会重算
    expect(repo.getConfigDirty('test-account')).toBe(true)

    // 首次快照同样不再落明文
    repo.enqueueInitialSyncSnapshot('test-account')
    expect(stmt('SELECT payload FROM sync_outbox').all()).toEqual([])

    restart()
    expect(store.getConnectionProfile(connection.id)).toEqual(connection)
    expect(() => store.removeConnectionProfile(connection.id)).toThrow('connection-in-use')
    store.removeWorkspace('remote')
    store.removeConnectionProfile(connection.id)
    expect(store.getConnectionProfile(connection.id)).toBeUndefined()
  })

  it('does no writes for identical history and leaves unchanged search rows intact during edits', () => {
    const session = repo.ensureSession({ id: 'delta-history', workspaceId: 'workspace', title: 'History' })
    const messages: AgentMessage[] = Array.from({ length: 6 }, (_, index) => ({
      id: `delta-${index}`, role: 'assistant', parts: [{ type: 'text', text: `needle${index}` }], createdAt: index + 1, schemaVersion: 1
    }))
    for (const message of messages) repo.commitMessage(session.id, message, 'owner-run')
    const changes = (): unknown => stmt('SELECT total_changes() AS count').get()?.['count']
    const before = changes()
    repo.replaceHistory(session.id, structuredClone(messages))
    expect(changes()).toBe(before)
    db().exec('CREATE TEMP TABLE body_updates (id TEXT); CREATE TEMP TRIGGER track_body_updates AFTER UPDATE OF parts ON messages BEGIN INSERT INTO body_updates VALUES (new.id); END')
    const indexRows = (): Map<string, number> => new Map(stmt('SELECT rowid, message_id FROM messages_fts WHERE session_id = ?').all(session.id).map((row) => [String(row['message_id']), Number(row['rowid'])]))
    const indexedBefore = indexRows()
    const edited = { ...messages[3]!, parts: [{ type: 'text' as const, text: 'replacementneedle' }] }
    repo.replaceHistory(session.id, [messages[5]!, messages[0]!, edited, messages[2]!])
    expect(stmt('SELECT id FROM body_updates').all()).toEqual([{ id: edited.id }])
    expect(repo.getHistory(session.id).map((message) => message.id)).toEqual(['delta-5', 'delta-0', 'delta-3', 'delta-2'])
    for (const id of ['delta-5', 'delta-0', 'delta-2']) expect(indexRows().get(id)).toBe(indexedBefore.get(id))
    expect(repo.searchAll('needle1')).toEqual([])
    expect(repo.searchAll('replacementneedle')[0]?.messageId).toBe(edited.id)
    expect(repo.getSessionDetail(session.id)?.messageRuns).toEqual({ 'delta-5': 'owner-run', 'delta-0': 'owner-run', 'delta-3': 'owner-run', 'delta-2': 'owner-run' })
    restart()
    expect(repo.getHistory(session.id).map((message) => message.id)).toEqual(['delta-5', 'delta-0', 'delta-3', 'delta-2'])
  })

  it('rejects duplicate and foreign message ids before changing authoritative history', () => {
    repo.ensureSession({ id: 'history-owner', workspaceId: 'workspace' })
    repo.ensureSession({ id: 'foreign-owner', workspaceId: 'workspace' })
    const message: AgentMessage = { id: 'owned', role: 'user', parts: [{ type: 'text', text: 'original' }], createdAt: 1, schemaVersion: 1 }
    repo.commitMessage('history-owner', message)
    repo.commitMessage('foreign-owner', { ...message, id: 'foreign' })
    expect(() => repo.replaceHistory('history-owner', [message, message])).toThrow()
    expect(() => repo.replaceHistory('history-owner', [{ ...message, id: 'foreign' }])).toThrow()
    expect(repo.getHistory('history-owner')).toEqual([message])
    repo.replaceHistory('history-owner', [])
    expect(repo.getHistory('history-owner')).toEqual([])
    expect(repo.searchAll('original').map((hit) => hit.sessionId)).toEqual(['foreign-owner'])
  })

  it('updates every search title only when the normalized session title changes', () => {
    const session = repo.ensureSession({ id: 'title-cost', workspaceId: 'workspace', title: 'OriginalTitle' })
    const updates = vi.spyOn(stmt('UPDATE messages_fts SET title = ? WHERE rowid IN (SELECT fts_rowid FROM messages_fts_rowid WHERE session_id = ?)'), 'run')
    const messages: AgentMessage[] = Array.from({ length: 20 }, (_, index) => ({
      id: `title-cost-${index}`, role: 'assistant', parts: [{ type: 'text', text: 'searchable content' }], createdAt: index + 1, schemaVersion: 1
    }))
    for (const message of messages) repo.commitMessage(session.id, message)
    repo.putSession({ ...repo.getSession(session.id)!, title: '  OriginalTitle  ' })
    repo.replaceHistory(session.id, messages)
    expect(updates).not.toHaveBeenCalled()
    repo.renameSession(session.id, 'RenamedTitle')
    expect(updates).toHaveBeenCalledTimes(1)
    expect(repo.searchAll('OriginalTitle')).toEqual([])
    expect(repo.searchAll('RenamedTitle')).toHaveLength(20)
    updates.mockRestore()
    restart()
    expect(repo.searchAll('RenamedTitle')).toHaveLength(20)
  })

  it('keeps one FTS row per message, addressed by rowid, through rewrites and deletes', () => {
    const session = repo.ensureSession({ id: 'fts-rowid', workspaceId: 'workspace', title: 'RowidTitle' })
    const message = (id: string, text: string, createdAt: number): AgentMessage => ({
      id, role: 'assistant', parts: [{ type: 'text', text }], createdAt, schemaVersion: 1
    })
    const ftsRows = (): number => Number(stmt('SELECT COUNT(*) AS n FROM messages_fts').get()?.['n'])
    const mapRows = (): number => Number(stmt('SELECT COUNT(*) AS n FROM messages_fts_rowid').get()?.['n'])
    const before = ftsRows()
    repo.commitMessage(session.id, message('fts-a', 'alphaword', 1))
    repo.commitMessage(session.id, message('fts-b', 'betaword', 2))
    repo.replaceHistory(session.id, [message('fts-a', 'gammaword', 1), message('fts-b', 'betaword', 2)])
    expect(ftsRows() - before).toBe(2)
    expect(mapRows()).toBe(ftsRows())
    expect(repo.searchAll('alphaword')).toEqual([])
    expect(repo.searchAll('gammaword')).toHaveLength(1)
    repo.replaceHistory(session.id, [message('fts-a', 'gammaword', 1)])
    expect(repo.searchAll('betaword')).toEqual([])
    expect(mapRows()).toBe(ftsRows())
    repo.deleteSession(session.id)
    expect(ftsRows()).toBe(before)
    expect(mapRows()).toBe(before)
  })

  it('★ V37 之前的版本开过同一个库后,新消息照样写得进去、旧版写的也搜得到', () => {
    const session = repo.ensureSession({ id: 'fts-legacy', workspaceId: 'workspace', title: 'LegacyTitle' })
    const message = (id: string, text: string, createdAt: number): AgentMessage => ({
      id, role: 'assistant', parts: [{ type: 'text', text }], createdAt, schemaVersion: 1
    })
    repo.commitMessage(session.id, message('legacy-a', 'firstword', 1))
    // 旧版写法:按 message_id 删、不带 rowid 插,不碰映射表
    const legacyWrite = (id: string, text: string): void => {
      stmt('DELETE FROM messages_fts WHERE message_id = ?').run(id)
      stmt('INSERT INTO messages_fts (message_id, session_id, title, content) VALUES (?, ?, ?, ?)').run(id, session.id, 'LegacyTitle', text)
    }
    const insertMessage = stmt(`INSERT INTO messages (id, session_id, ordinal, role, parts, created_at, schema_version)
      VALUES (?, ?, ?, 'assistant', '[]', ?, 1)`)
    insertMessage.run('legacy-b', session.id, 100, 2)
    insertMessage.run('legacy-c', session.id, 101, 3)
    legacyWrite('legacy-a', 'rewrittenword')
    legacyWrite('legacy-b', 'legacyword')
    legacyWrite('legacy-c', 'thirdword')
    restart()
    expect(() => repo.commitMessage(session.id, message('legacy-d', 'newword', 4))).not.toThrow()
    expect(repo.searchAll('rewrittenword')).toHaveLength(1)
    expect(repo.searchAll('legacyword')).toHaveLength(1)
    expect(repo.searchAll('thirdword')).toHaveLength(1)
    expect(repo.searchAll('newword')).toHaveLength(1)
    const unmapped = stmt('SELECT COUNT(*) AS n FROM messages_fts WHERE rowid NOT IN (SELECT fts_rowid FROM messages_fts_rowid)').get()
    expect(Number(unmapped?.['n'])).toBe(0)
  })

  it('供应商 / 别名 / 工作区 / 设置 / kv 全部读得回来', () => {
    store.putProvider(provider('主', 0))
    store.putProvider(provider('备', 1))
    store.putAlias(alias('主', 'claude-sonnet-4'))
    store.putAlias(alias('备', 'claude-sonnet-4'))
    store.putWorkspace(workspace('ws-1'))
    store.updateSettings({
      defaultModel: 'claude-sonnet-4',
      gateway: { preferredPort: 20000 }
    })
    store.setKv('tabs.outer.main', {
      outer: [{ id: 't1' }],
      activeOuterId: 't1'
    })

    restart()

    expect(store.listProviders().map((p) => p.id)).toEqual(['主', '备'])
    expect(store.listAliases()).toHaveLength(2)
    expect(store.getWorkspace('ws-1')?.name).toBe('工作区 ws-1')
    expect(store.getSettings().defaultModel).toBe('claude-sonnet-4')
    expect(store.getSettings().gateway.preferredPort).toBe(20000)
    expect(store.getKv('tabs.outer.main', null)).toEqual({
      outer: [{ id: 't1' }],
      activeOuterId: 't1'
    })
  })

  it('Provider 的协议专属缓存档位跨重启并经导出导入保留', () => {
    /*
      `off` 是旧库 / 旧归档里真实存在的取值(现在提示缓存强制开启),类型里已经没有它,
      所以用显式转换把它当成「未知的旧 JSON」写进去:读回来的有效值必须是强制的 5m。
      同理,整个 `protocolOptions` 缺失的旧行也不能读成 undefined。
    */
    const configured = [
      ['关闭', 'off'],
      ['五分钟', '5m'],
      ['一小时', '1h']
    ] as const
    const missing = '缺省'
    const expected = { 关闭: '5m', 五分钟: '5m', 一小时: '1h', 缺省: '5m' }

    for (const [id, cacheTtl] of configured) {
      store.putProvider({
        ...provider(id, configured.findIndex(([name]) => name === id)),
        protocolOptions: { anthropic: { cacheTtl } } as unknown as UpstreamProvider['protocolOptions']
      })
    }
    store.putProvider(provider(missing, configured.length))

    restart()

    const readTtls = Object.fromEntries(
      store.listProviders().map((p) => [p.id, anthropicCacheTtlOf(p)])
    )
    expect(readTtls).toEqual(expected)

    // Provider JSON is part of the regular data snapshot; no special export
    // channel is needed for protocol-specific options.
    const exported = repo.exportDataSnapshot()
    expect(exported.providers.map((p) => [p.id, anthropicCacheTtlOf(p)])).toEqual([
      ['关闭', '5m'],
      ['五分钟', '5m'],
      ['一小时', '1h'],
      ['缺省', '5m']
    ])

    for (const id of [...configured.map(([name]) => name), missing]) store.removeProvider(id)
    expect(store.listProviders()).toEqual([])
    repo.mergeDataExport(exported)
    restart()

    expect(
      Object.fromEntries(store.listProviders().map((p) => [p.id, anthropicCacheTtlOf(p)]))
    ).toEqual(expected)
  })

  /**
   * 旧归档(用户手上真实存在的导出文件)里带着 `off`,导入走的是 `putProvider`,
   * 所以那条路径也必须把值归一化后**存下来** —— 只靠读的时候兜底的话,
   * 库里会长期留着一个已经不合法的值,下一次读的人未必走同一个访问器。
   */
  it('旧归档里的 off 档位导入时按 5m 落库', () => {
    const snapshot = repo.exportDataSnapshot()
    repo.mergeDataExport({
      ...snapshot,
      providers: [
        {
          ...provider('legacy-cache', 0),
          protocolOptions: { anthropic: { cacheTtl: 'off' } }
        } as unknown as UpstreamProvider
      ]
    })

    restart()

    const imported = store.listProviders().find((p) => p.id === 'legacy-cache')
    expect(anthropicCacheTtlOf(imported!)).toBe('5m')

    const raw = new DatabaseSync(join(dir, DB_FILENAME), { readOnly: true })
    const stored = JSON.parse(
      String(raw.prepare('SELECT json FROM providers WHERE id = ?').get('legacy-cache')?.['json'])
    ) as UpstreamProvider
    raw.close()
    expect(stored.protocolOptions).toEqual({ anthropic: { cacheTtl: '5m' } })
  })

  it('别名带着能力位和数字字段一起回来,不是只剩个名字', () => {
    store.putProvider(provider('主', 0))
    store.putAlias(alias('主', 'm'))

    restart()

    const [a] = store.listAliases()
    expect(a).toEqual(alias('主', 'm'))
  })

  /**
   * ★ `PRAGMA foreign_keys` 是**连接级**的,不是存在文件里的属性。
   * 重开时忘了开,级联就静默失效 —— 而症状(下拉框里一个选不动的模型)
   * 离这里非常远。所以级联要在**重开之后**再验一次。
   */
  it('删 provider 的级联在重开之后仍然生效', () => {
    store.putProvider(provider('主', 0))
    store.putProvider(provider('备', 1))
    store.putAlias(alias('主', 'm1'))
    store.putAlias(alias('主', 'm2'))
    store.putAlias(alias('备', 'm1'))

    restart()
    store.removeProvider('主')

    expect(store.listAliases().map((a) => a.providerId)).toEqual(['备'])
  })

  it('删工作区连带删掉它的内层 Tab 记录,且两者一起落盘', () => {
    store.putWorkspace(workspace('ws-1'))
    store.setKv('tabs.inner.ws-1', { tabs: [{ id: 'x' }], activeTabId: 'x' })

    store.removeWorkspace('ws-1')
    restart()

    expect(store.getWorkspace('ws-1')).toBeUndefined()
    expect(store.getKv('tabs.inner.ws-1', 'FALLBACK')).toBe('FALLBACK')
  })
})

describe('异步事务的所有者与排队', () => {
  function gate(): { promise: Promise<void>; release(): void } {
    let release: () => void = () => {}
    const promise = new Promise<void>((resolve) => { release = resolve })
    return { promise, release }
  }

  it('await 期间拒绝无关读写，同一调用链仍可嵌套同步和异步事务', async () => {
    const started = gate()
    const paused = gate()
    const work = txAsync(async () => {
      repo.setKv('owned-first', 1)
      started.release()
      await paused.promise
      tx(() => repo.setKv('owned-nested', 2))
      await txAsync(async () => { await Promise.resolve(); repo.setKv('owned-async', 3) })
    })
    await started.promise
    try {
      expect(() => repo.getKv('owned-first', null)).toThrow(DatabaseBusyError)
      expect(() => repo.setKv('unrelated', 4)).toThrow(DatabaseBusyError)
      expect(() => tx(() => repo.setKv('unrelated-tx', 5))).toThrow(DatabaseBusyError)
      expect(() => closeDatabase()).toThrow(DatabaseBusyError)
    } finally { paused.release(); await work }
    expect(repo.getKv('owned-first', null)).toBe(1)
    expect(repo.getKv('owned-nested', null)).toBe(2)
    expect(repo.getKv('owned-async', null)).toBe(3)
    expect(repo.getKv('unrelated', null)).toBeNull()
  })

  it('失败只回滚自己，排队的另一个异步事务随后独立提交', async () => {
    const started = gate()
    const paused = gate()
    const first = txAsync(async () => {
      repo.setKv('rollback-owned', 1)
      started.release()
      await paused.promise
      throw new Error('rollback-original')
    })
    const failed = first.catch((error: unknown) => error)
    await started.promise
    let secondStarted = false
    const second = txAsync(() => { secondStarted = true; repo.setKv('second-owner', 2) })
    await Promise.resolve()
    expect(secondStarted).toBe(false)
    paused.release()
    expect(await failed).toMatchObject({ message: 'rollback-original' })
    await second
    expect(repo.getKv('rollback-owned', null)).toBeNull()
    expect(repo.getKv('second-owner', null)).toBe(2)
  })

  it('多个顶层异步事务按请求顺序执行，而不是借用别人的事务', async () => {
    const paused = gate()
    const order: number[] = []
    const first = txAsync(async () => { order.push(1); await paused.promise; repo.setKv('order', 1) })
    const second = txAsync(() => { order.push(2); expect(repo.getKv('order', null)).toBe(1); repo.setKv('order', 2) })
    const third = txAsync(() => { order.push(3); expect(repo.getKv('order', null)).toBe(2) })
    await Promise.resolve()
    expect(order).toEqual([1])
    paused.release()
    await Promise.all([first, second, third])
    expect(order).toEqual([1, 2, 3])
  })

  it('预先缓存的 statement 与迭代器也不能穿过所有者边界', async () => {
    repo.setKv('iterator-existing', 1)
    const write = stmt('INSERT INTO kv (key, json) VALUES (?, ?)')
    const iterator = stmt('SELECT key FROM kv ORDER BY key').iterate()
    const started = gate()
    const paused = gate()
    const work = txAsync(async () => { started.release(); await paused.promise })
    await started.promise
    try {
      expect(() => write.run('cached-unrelated', '1')).toThrow(DatabaseBusyError)
      expect(() => iterator[Symbol.iterator]().next()).toThrow(DatabaseBusyError)
    } finally { paused.release(); await work; iterator.return?.() }
    expect(repo.getKv('cached-unrelated', null)).toBeNull()
  })

  it('提交之后才醒来的异步续体不能悄悄在事务之外继续写', async () => {
    const paused = gate()
    let late: Promise<void> = Promise.resolve()
    await txAsync(() => {
      late = paused.promise.then(() => repo.setKv('late-owner-write', 1))
      void late.catch(() => undefined)
    })
    paused.release()
    await expect(late).rejects.toThrow(DatabaseTransactionClosedError)
    expect(repo.getKv('late-owner-write', null)).toBeNull()
  })

  it('同步事务拒绝 Promise 回调且回滚，迟到的续体也被拒绝', async () => {
    const paused = gate()
    let late: Promise<void> = Promise.resolve()
    expect(() => tx(() => {
      repo.setKv('sync-before-await', 1)
      late = paused.promise.then(() => repo.setKv('sync-after-await', 2))
      return late
    })).toThrow(/使用 txAsync/)
    paused.release()
    await expect(late).rejects.toThrow(DatabaseTransactionClosedError)
    expect(repo.getKv('sync-before-await', null)).toBeNull()
    expect(repo.getKv('sync-after-await', null)).toBeNull()
  })

  it('排队请求遇到换库时失败，不能把旧请求写进新连接', async () => {
    const queued = txAsync(() => repo.setKv('queued-old-connection', 1))
    closeDatabase()
    openDatabase(dir)
    await expect(queued).rejects.toThrow(/数据库/)
    expect(repo.getKv('queued-old-connection', null)).toBeNull()
  })
})

describe('基础配置同步边界', () => {
  /*
    ★ v2 之后,「配置写入 → 明文 outbox」这条路没有了。一次配置写入现在产出的是
    一个**脏标记**(`enqueueSyncMutation` 只调 `setConfigDirty`),真正上传走加密快照。

    所以这条用例断言的是新契约的两半:
    - 写配置**不产生任何明文行** —— 供应商的 baseUrl 之类不再落盘;
    - 但同步层知道该作用域变脏了,否则下一次快照不会重算(这是 v1 那个
      「同事务入队」保证过的东西,v2 换成了同事务打标记)。

    ⚠️ 「同步出去的 payload 不含 credentialRef」这一条暂时无处可断言:剥离它的
    代码(`repo.ts` 的 `enqueueInitialSyncSnapshot`)还在,但它的产物现在被丢弃。
    v2 上传器落地后必须把那条断言加回来 —— 凭证引用是设备本地的,绝不该上云。
  */
  it('配置写入只打脏标记，不再产生任何明文 outbox 行', () => {
    // ★ enabled 显式给 true —— 默认是 false,不给这条用例测不到任何东西
    repo.configureSyncAccount('account-a', true)
    repo.putProvider(provider('cloud-provider', 0))

    expect(repo.listPendingSyncMutations('account-a')).toHaveLength(0)
    expect(repo.getConfigDirty('account-a')).toBe(true)
    // 凭证引用留在本机那一份里 —— 它是设备本地的,不随配置走
    expect(repo.listProviders().find((p) => p.id === 'cloud-provider')).toHaveProperty('credentialRef')
  })

  it('单独修改供应商凭证也会把 providers 密文快照标脏', () => {
    repo.configureSyncAccount('account-a', true)
    repo.putCredential('provider:cloud-provider', new Uint8Array([1, 2, 3]))
    expect(repo.getConfigCategoryDirty('providers', 'account-a')).toBe(true)

    repo.setConfigCategoryDirty('providers', 'account-a', false)
    repo.removeCredential('provider:cloud-provider')
    expect(repo.getConfigCategoryDirty('providers', 'account-a')).toBe(true)
  })

  it('异步同步应用的标记只属于自己的调用链，不遮住无关本地修改', async () => {
    repo.configureSyncAccount('account-a', true)
    let release: () => void = () => {}
    const paused = new Promise<void>((resolve) => { release = resolve })
    const applying = repo.withSyncApplyAsync(async () => {
      await paused
      repo.putProvider(provider('remote-owned', 1))
    })
    repo.putProvider(provider('local-unrelated', 0))
    expect(repo.getConfigDirty('account-a')).toBe(true)
    repo.setConfigDirty('account-a', false)
    repo.setConfigCategoryDirty('providers', 'account-a', false)
    release()
    await applying
    expect(repo.getConfigDirty('account-a')).toBe(false)
    expect(repo.listProviders().map((item) => item.id)).toEqual(['local-unrelated', 'remote-owned'])
  })

  it('远端应用不会再次写入 outbox，会话数据也不进入同步表', () => {
    repo.configureSyncAccount('account-a', true)
    repo.withSyncApply(() => repo.putProvider(provider('remote-provider', 0)))
    expect(repo.listPendingSyncMutations('account-a')).toHaveLength(0)
    // ★ 远端推回来的改动**不算本地改动**,连脏标记都不该打 —— 否则每次拉取
    //   都会把自己标脏,下一轮再上传一次,两台设备互相触发个没完。
    expect(repo.getConfigDirty('account-a')).toBe(false)

    store.createSession({ workspaceId: 'local', title: 'local session' })
    expect(repo.listPendingSyncMutations('account-a')).toHaveLength(0)
  })
})

describe('会话、完整内容块与全文索引', () => {
  const transcript = (): AgentMessage[] => [
    {
      id: 'm-assistant',
      role: 'assistant',
      createdAt: 1_700_000_000_200,
      schemaVersion: 1,
      parts: [
        { type: 'thinking', text: 'private reasoning', opaque: { signature: 'sig-1' } },
        { type: 'text', text: 'persistneedle answer' },
        { type: 'tool_call', callId: 'call-1', name: 'Read', input: { path: 'README.md' } },
        { type: 'subagent', callId: 'call-2', childRunId: 'child-1', summary: 'child summary' },
        { type: 'image', mime: 'image/png', dataRef: '/missing/imported-image.png' },
        {
          type: 'error',
          error: { code: 'tool_failed', message: 'tool failed safely', retryable: false }
        }
      ]
    },
    {
      id: 'm-tool-result',
      role: 'user',
      createdAt: 1_700_000_000_100,
      schemaVersion: 1,
      parts: [
        {
          type: 'tool_result',
          callId: 'call-1',
          output: { content: 'tool output', truncated: true, originalBytes: 100_000 },
          isError: false
        }
      ]
    }
  ]

  it('重启后仍按提交顺序恢复每一种 ContentPart，时间戳不会重排消息', () => {
    repo.ensureSession({ id: 'session-parts', workspaceId: 'workspace-1', title: 'OriginalTitle' })
    const messages = transcript()
    for (const message of messages) repo.commitMessage('session-parts', message)
    repo.setRunRecord('run-1', 'session-parts', 'completed', 10, 20)

    restart()

    expect(repo.getHistory('session-parts')).toEqual(messages)
    expect(repo.getSessionDetail('session-parts')?.messages).toEqual(messages)
    const raw = new DatabaseSync(join(dir, DB_FILENAME), { readOnly: true })
    expect(raw.prepare('SELECT status, ended_at FROM runs WHERE id = ?').get('run-1')).toMatchObject({
      status: 'completed',
      ended_at: 20
    })
    raw.close()
  })

  it('正文与新标题可搜索，删除会话后 FTS 不留幽灵结果', () => {
    repo.ensureSession({ id: 'session-search', workspaceId: 'workspace-1', title: 'OriginalTitle' })
    for (const message of transcript()) repo.commitMessage('session-search', message)

    expect(repo.searchAll('persistneedle').map((hit) => hit.sessionId)).toEqual(['session-search'])
    repo.renameSession('session-search', 'RenamedOrbit')
    expect(new Set(repo.searchAll('RenamedOrbit').map((hit) => hit.sessionId)))
      .toEqual(new Set(['session-search']))
    expect(repo.searchAll('OriginalTitle')).toEqual([])

    repo.deleteSession('session-search')
    expect(repo.searchAll('persistneedle')).toEqual([])
    expect(repo.searchAll('RenamedOrbit')).toEqual([])
  })

  it('升级时重建漏掉的全文索引，容忍坏正文并清除幽灵结果', () => {
    repo.ensureSession({ id: 'rebuild-search', workspaceId: 'workspace-1', title: 'RebuildTitle' })
    for (const message of transcript()) repo.commitMessage('rebuild-search', message)
    stmt('UPDATE messages SET parts = ? WHERE id = ?').run('{broken', 'm-tool-result')
    db().exec('DELETE FROM messages_fts')
    stmt('INSERT INTO messages_fts (message_id, session_id, title, content) VALUES (?, ?, ?, ?)')
      .run('ghost-message', 'ghost-session', 'GhostTitle', 'ghostneedle')
    // 模拟修复迁移尚未执行的旧库，不修改已发布迁移。
    stmt('DELETE FROM migrations WHERE version = ?').run(29)
    restart()

    expect(repo.searchAll('persistneedle').map((hit) => hit.messageId)).toEqual(['m-assistant'])
    expect(stmt('SELECT content FROM messages_fts WHERE message_id = ?').get('m-assistant')?.['content'])
      .toBe(repo.searchableMessageText(transcript()[0]!.parts))
    expect(stmt('SELECT content FROM messages_fts WHERE message_id = ?').get('m-tool-result')?.['content']).toBe('')
    expect(stmt('SELECT message_id FROM messages_fts WHERE message_id = ?').get('ghost-message')).toBeUndefined()
    const indexed = stmt('SELECT message_id, content FROM messages_fts ORDER BY message_id').all()
    restart()
    expect(stmt('SELECT message_id, content FROM messages_fts ORDER BY message_id').all()).toEqual(indexed)
    expect(stmt('SELECT version FROM migrations WHERE version = ?').all(29)).toHaveLength(1)
  })

  it('归档、收藏与清空历史都持久化，且不删除配置', () => {
    store.putProvider(provider('keep-provider', 0))
    repo.ensureSession({ id: 'session-flags', workspaceId: 'workspace-1', title: 'Flags' })
    repo.commitMessage('session-flags', transcript()[0]!)
    repo.setSessionArchived('session-flags', true)
    repo.setSessionFavorited('session-flags', true)

    restart()
    expect(repo.getSession('session-flags')).toMatchObject({ archived: true, favorited: true })
    expect(repo.listSessions('workspace-1', true)[0]).toMatchObject({
      id: 'session-flags',
      archived: true,
      favorited: true
    })

    repo.deleteAllHistory()
    restart()
    expect(repo.getSession('session-flags')).toBeUndefined()
    expect(repo.getHistory('session-flags')).toEqual([])
    expect(repo.searchAll('persistneedle')).toEqual([])
    expect(store.listProviders().map((item) => item.id)).toContain('keep-provider')
  })
})

describe('密钥:数据库这一层只认字节', () => {
  /**
   * 存进去的是 `safeStorage.encryptString` 的产物 —— 密文里必然有非 UTF-8 的字节。
   * 哪天有人图省事把 BLOB 改成 TEXT,这一条会当场红:那种改动的症状是
   * 「密钥存进去了,读出来解不开」,而且只在某些 key 上出现。
   */
  it('非 UTF-8 的密文原样进、原样出,跨重启不变', () => {
    const cipher = new Uint8Array([0xff, 0x00, 0xfe, 0x01, 0x80, 0x7f])
    repo.putCredential('主:key', cipher)

    restart()

    expect(repo.getCredential('主:key')).toEqual(cipher)
    expect(repo.getCredential('不存在的')).toBeUndefined()
  })

  it('同一个 ref 重复写是更新;删掉之后读回 undefined', () => {
    repo.putCredential('r', new Uint8Array([1]))
    repo.putCredential('r', new Uint8Array([2, 3]))
    restart()
    expect(repo.getCredential('r')).toEqual(new Uint8Array([2, 3]))

    repo.removeCredential('r')
    restart()
    expect(repo.getCredential('r')).toBeUndefined()
  })
})

describe('导入供应商的凭证引用边界', () => {
  it('新记录不采信归档中的 credentialRef，按供应商 ID 派生', () => {
    const snapshot = repo.exportDataSnapshot()
    repo.mergeDataExport({
      ...snapshot,
      providers: [{ ...provider('imported', 0), credentialRef: 'provider:someone-else' }]
    })

    expect(store.listProviders().find((item) => item.id === 'imported')?.credentialRef)
      .toBe('provider:imported')
  })

  it('较新的导入记录覆盖配置时仍保留本机已有的 legacy ref', () => {
    store.putProvider({ ...provider('existing', 0), credentialRef: 'legacy:key', updatedAt: 10 } as UpstreamProvider)
    const snapshot = repo.exportDataSnapshot()
    repo.mergeDataExport({
      ...snapshot,
      providers: [{
        ...provider('existing', 0),
        name: 'newer name',
        credentialRef: 'provider:hijack',
        updatedAt: 20
      } as UpstreamProvider]
    })

    expect(store.listProviders().find((item) => item.id === 'existing')).toMatchObject({
      name: 'newer name',
      credentialRef: 'legacy:key'
    })
  })
})

/**
 * V2 的两张表。这里测的仍然是「关了再开还在」,外加两件**只有跨进程才看得出来**的事:
 * 删配置要连密钥一起删,以及搜索服务读回来是「目录八家」而不是「库里那几行」。
 */
describe('MCP 服务器', () => {
  const stdio = (id: string): McpServerConfig => ({
    id,
    name: id,
    enabled: true,
    transport: 'stdio',
    command: 'npx',
    args: ['-y', '@modelcontextprotocol/server-everything'],
    envNames: ['TOKEN']
  })

  it('三种传输方式都能原样读回来', () => {
    store.putMcpServer(stdio('local'))
    store.putMcpServer({
      id: 'remote',
      name: '远端',
      enabled: false,
      transport: 'streamable-http',
      url: 'https://mcp.example.com/v1',
      headerNames: ['Authorization']
    })
    restart()

    const all = store.listMcpServers()
    expect(all.map((c) => c.id)).toEqual(['local', 'remote'])
    const local = all[0]
    expect(local?.transport).toBe('stdio')
    // 可辨识联合读回来之后仍然是那个分支 —— JSON 列保住了变体特有的字段
    expect(local !== undefined && local.transport === 'stdio' ? local.args : null).toEqual([
      '-y',
      '@modelcontextprotocol/server-everything'
    ])
  })

  /**
   * ★ 这一条是 `removeMcpServer` 存在的理由。只删配置的话,`credentials` 里
   * 那行密文会变成孤儿(键名存在刚被删掉的配置里),而且下次建一个同 id 的
   * 服务器会**默默继承**上一个的 token —— 症状是「我没填 Authorization,它却连上了」。
   */
  it('删服务器时密钥一起删,同 id 重建不会继承旧密钥', () => {
    store.putMcpServer(stdio('s1'))
    repo.putCredential(mcpSecretRef('s1', 'env'), new Uint8Array([9, 9]))
    restart()
    expect(repo.getCredential(mcpSecretRef('s1', 'env'))).toEqual(new Uint8Array([9, 9]))

    store.removeMcpServer('s1')
    restart()
    expect(store.listMcpServers()).toEqual([])
    expect(repo.getCredential(mcpSecretRef('s1', 'env'))).toBeUndefined()
  })
})

describe('搜索服务', () => {
  /**
   * ★ 一家都没配过时库是空的,但界面要列出八家。这个左连接收在 repo 里做一次,
   * 而不是让设置页、`web_search`、连通性测试各做一遍 —— 三份实现必然分叉。
   */
  it('库为空时也返回目录里的八家', () => {
    const all = store.listSearchProviders()
    expect(all).toHaveLength(SEARCH_PROVIDER_IDS.length)
    expect(all.every((c) => !c.enabled)).toBe(true)
  })

  it('存过的按 priority 排在前,没存过的接在后面', () => {
    store.putSearchProviders([
      { id: 'brave', enabled: true, priority: 0 },
      { id: 'tavily', enabled: true, priority: 1 }
    ])
    restart()

    const all = store.listSearchProviders()
    expect(all.slice(0, 2).map((c) => c.id)).toEqual(['brave', 'tavily'])
    // 八家一个不少 —— 覆盖两家不等于把另外六家弄丢了
    expect(all).toHaveLength(SEARCH_PROVIDER_IDS.length)
  })

  it('清 Key 只清这一家,不动别家', () => {
    repo.putCredential(searchSecretRef('tavily'), new Uint8Array([1]))
    repo.putCredential(searchSecretRef('exa'), new Uint8Array([2]))
    store.clearSearchCredential('tavily')
    restart()

    expect(repo.getCredential(searchSecretRef('tavily'))).toBeUndefined()
    expect(repo.getCredential(searchSecretRef('exa'))).toEqual(new Uint8Array([2]))
  })
})

describe('迁移', () => {
  it('默认数据库目录是主目录下的 .next-cowork,与 cwd 无关', () => {
    expect(defaultDatabaseDirectory()).toBe(join(homedir(), DATABASE_DIRNAME, DATA_SUBDIRNAME))
  })

  it('重开不会重跑迁移,也不会清空已有的行', () => {
    store.putProvider(provider('主', 0))
    restart()
    restart()

    const raw = new DatabaseSync(join(dir, DB_FILENAME))
    const rows = raw.prepare('SELECT version FROM migrations').all()
    const version = raw.prepare('PRAGMA user_version').get()
    raw.close()

    /*
      ★ 对着 `MIGRATIONS` 断言,不写死数字。写死的话每加一条迁移都会挂在这里,
      而失败信息(`expected 2 to be 1`)完全说不出「你只是加了条迁移」——
      下一个人会先去怀疑迁移跑重了。这一条要测的是**重开不重跑**,
      也就是「行数 == 迁移条数」,而不是「行数 == 1」。
    */
    expect(rows).toHaveLength(MIGRATIONS.length)
    expect(Number(version?.['user_version'])).toBe(
      MIGRATIONS.reduce((max, m) => Math.max(max, m.version), 0)
    )
    expect(store.listProviders()).toHaveLength(1)
  })

  it('真实的 V1–V3 数据库会追加升级到会话迁移并可立即写入全文索引', () => {
    const v3Dir = mkdtempSync(join(tmpdir(), 'nextcowork-v3-'))
    const v3Path = join(v3Dir, DB_FILENAME)
    const raw = new DatabaseSync(v3Path)
    raw.exec('CREATE TABLE migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)')
    const insert = raw.prepare('INSERT INTO migrations (version, name, applied_at) VALUES (?, ?, ?)')
    for (const migration of MIGRATIONS.slice(0, 3)) {
      raw.exec(migration.sql)
      insert.run(migration.version, migration.name, Date.now())
    }
    raw.exec('PRAGMA user_version = 3')
    raw.close()

    closeDatabase()
    openDatabase(v3Dir)
    repo.ensureSession({ id: 'upgraded-session', workspaceId: 'w', title: 'Migrated' })
    repo.commitMessage('upgraded-session', {
      id: 'upgraded-message',
      role: 'user',
      parts: [{ type: 'text', text: 'migrationneedle' }],
      createdAt: 100,
      schemaVersion: 1
    })

    expect(repo.searchAll('migrationneedle')[0]?.sessionId).toBe('upgraded-session')
    const upgraded = new DatabaseSync(v3Path, { readOnly: true })
    const tables = upgraded
      .prepare("SELECT name FROM sqlite_master WHERE type IN ('table', 'view')")
      .all()
      .map((row) => String(row['name']))
    const versions = upgraded
      .prepare('SELECT version FROM migrations ORDER BY version')
      .all()
      .map((row) => Number(row['version']))
    expect(tables).toEqual(expect.arrayContaining(['sessions', 'messages', 'runs', 'attachments', 'messages_fts']))
    expect(versions).toEqual(MIGRATIONS.map((migration) => migration.version))
    expect(Number(upgraded.prepare('PRAGMA user_version').get()?.['user_version'])).toBe(
      MIGRATIONS.at(-1)?.version
    )
    upgraded.close()
    closeDatabase()
    rmSync(v3Dir, { recursive: true, force: true })
    openDatabase(dir)
  })

  it('从旧版第 6 版升级时补 owner_id,并修复去重索引', () => {
    // 模拟已经执行过旧版 1~6 的真实用户库:第 5 版只有 scope/status,
    // 第 6 版只有 display_name,两者都没有 owner_id。
    const legacyDir = mkdtempSync(join(tmpdir(), 'nextcowork-legacy-'))
    const legacyPath = join(legacyDir, DB_FILENAME)
    const raw = new DatabaseSync(legacyPath)
    raw.exec('CREATE TABLE migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)')
    const insert = raw.prepare('INSERT INTO migrations (version, name, applied_at) VALUES (?, ?, ?)')
    for (const migration of MIGRATIONS.slice(0, 6)) {
      // 第 5 版曾有一个发布变体漏掉 owner_id,但仍写入了同一个迁移号。
      // 用那条真实历史形态构造数据库,验证第 7 版能补列。
      const sql = migration.version === 5
        ? migration.sql
            .replace('ALTER TABLE attachments ADD COLUMN owner_id TEXT;\n', '')
            .replace('UPDATE attachments SET owner_id = session_id WHERE owner_id IS NULL;\n', '')
            .replace('CREATE INDEX attachments_by_checksum ON attachments (checksum, scope, owner_id);', 'CREATE INDEX attachments_by_checksum ON attachments (checksum, scope);')
        : migration.sql
      raw.exec(sql)
      insert.run(migration.version, migration.name, Date.now())
    }
    raw.close()

    // 让应用按当前迁移表打开这个旧库,只应执行新增的第 7 条。
    closeDatabase()
    openDatabase(legacyDir)
    const upgraded = new DatabaseSync(legacyPath, { readOnly: true })
    const columns = upgraded.prepare('PRAGMA table_info(attachments)').all().map((r) => String(r['name']))
    const checksumIndex = upgraded
      .prepare("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'attachments_by_checksum'")
      .get()
    const versions = upgraded.prepare('SELECT version FROM migrations ORDER BY version').all().map((r) => Number(r['version']))
    upgraded.close()
    closeDatabase()
    rmSync(legacyDir, { recursive: true, force: true })

    expect(columns).toContain('owner_id')
    expect(String(checksumIndex?.['sql'])).toContain('(checksum, scope, owner_id)')
    expect(versions).toEqual(MIGRATIONS.map((migration) => migration.version))
  })

  /**
   * ★ 旧版本存下的行会缺新字段。读回 `undefined` 的话,它会顺着 IPC 一路流到
   * 界面上变成一个空下拉框 —— 而不是一个报错。
   *
   * 这不是假想:方案 §7 下一步就要把 `gateway.failover` 改名成 `routing.failover`,
   * 那之后**所有已经存在的行**都缺 `routing`。
   */
  it('旧行缺字段时补的是默认值,不是 undefined', () => {
    closeDatabase()
    const raw = new DatabaseSync(join(dir, DB_FILENAME))
    raw
      .prepare(
        'INSERT INTO settings (id, json) VALUES (1, ?) ON CONFLICT (id) DO UPDATE SET json = excluded.json'
      )
      // 一个「上个版本」的行:只有两个字段,嵌套块也只给了一半
      .run(JSON.stringify({ theme: 'dark', gateway: { enabled: true } }))
    raw.close()
    openDatabase(dir)

    const s = store.getSettings()
    expect(s.theme).toBe('dark')
    expect(s.gateway.enabled).toBe(true)
    // 同一个嵌套块里没给的属性拿默认值,而不是整块被替换掉
    expect(s.gateway.preferredPort).toBe(DEFAULT_SETTINGS.gateway.preferredPort)
    // 整块缺席的也一样
    expect(s.subagent).toEqual(DEFAULT_SETTINGS.subagent)
    expect(s.locale).toBe(DEFAULT_SETTINGS.locale)
  })
})

describe('首次启动:库文件所在的目录还不存在', () => {
  /**
   * ★ `DatabaseSync` **不会**替你建目录,它只抛 `unable to open database file` ——
   * 一条完全不提目录的错。而这条路径是全新安装必走的:`app.getPath('userData')`
   * 在第一次启动时可能还没落地,dev 那个 `…-dev` 后缀的更是没人建过。
   *
   * 少了 `mkdirSync` 的表现是**应用起不来**,不是某个功能不好使,
   * 所以这一条守的是整个步骤 1 的最坏情况。
   */
  it('自己把多层父目录建出来,并且照常读写', () => {
    closeDatabase()
    const fresh = join(dir, '还没建过', 'userData-dev')
    expect(() => openDatabase(fresh)).not.toThrow()

    store.putProvider(provider('主', 0))
    closeDatabase()
    openDatabase(fresh)
    expect(store.listProviders().map((p) => p.id)).toEqual(['主'])

    // 库确实落在那个新建的目录里,不是退回上一层或内存
    expect(statSync(join(fresh, DB_FILENAME)).size).toBeGreaterThan(0)
  })
})

describe('openDatabase 的调用顺序是被钉死的', () => {
  /**
   * ★ 守的是这样一次改动:有人把 `openDatabase()` 挪到 `initRuntime()` 后面。
   * 那时 seed 会写进一个内存兜底库,换成文件库时那些行凭空消失 ——
   * 症状「配置重启后没了」和**完全没做持久化**一模一样,查起来会绕很远。
   */
  it('重复打开直接抛,而不是静默换一个库', () => {
    expect(() => openDatabase(dir)).toThrow(/已经打开/)
  })

  it('先碰过 store(走内存兜底)再 openDatabase,抛的错要点明是顺序问题', () => {
    closeDatabase()
    store.listProviders() // 没人指定位置 —— 开一个内存库兜底
    expect(() => openDatabase(dir)).toThrow(/内存兜底/)
  })
})

/**
 * 第 3 条迁移(`model_pricing` / `usage_records`)。定价库的访问器还没写(步骤 3/5),
 * 所以这一组直接对表断言 —— 测的是 **DDL 承诺的那几件事**,而它们都会**静默**地坏:
 *
 * 1. 加了外键 → 整张种子表插不进去;
 * 2. 唯一索引写成普通复合主键 → 重复行共存,查价随机命中一条;
 * 3. `cost_micros` 写成 NOT NULL DEFAULT 0 → 「查不到定价」变成「这次免费」。
 *
 * 三件都不会有人报 bug,所以在这里钉住。
 */
describe('定价与用量的表结构', () => {
  /** 直连库文件。`enableForeignKeyConstraints` 默认为 true,正是外键那条要的环境 */
  const raw = (): DatabaseSync => new DatabaseSync(join(dir, DB_FILENAME))

  const insertPricing = (
    d: DatabaseSync,
    providerId: string | null,
    modelId: string,
    effectiveFrom: string | null
  ): void => {
    d.prepare(
      `INSERT INTO model_pricing
         (provider_id, model_id, effective_from, display_name, currency, modality,
          tiers, source, fetched_at)
       VALUES (?, ?, ?, 'x', 'USD', 'text', '[]', 'https://example.com', '2026-09-04')`
    ).run(providerId, modelId, effectiveFrom)
  }

  /**
   * ★★ **`model_pricing.provider_id` 上不能有外键。**
   *
   * 种子表按**厂商**记价(`deepseek` / `zai`),而那些厂商用户可能一个都没配 ——
   * 加外键的后果不是「少几行」,是**整张种子表一行都插不进去**,
   * 表现为「装完之后定价页整页空白」。
   *
   * 这条用一个**确定不存在**于 providers 表里的 id 去插,所以它红的时候
   * 只有一个可能:有人给这列补了 `REFERENCES providers (id)`。
   */
  it('provider_id 指向一个没配过的厂商时照样插得进去(不能有外键)', () => {
    const d = raw()
    expect(d.prepare('SELECT COUNT(*) c FROM providers').get()?.['c']).toBe(0)
    expect(() => insertPricing(d, 'deepseek', 'deepseek-v4-flash', null)).not.toThrow()
    d.close()
  })

  /**
   * ★ NULL 在 UNIQUE 里彼此不相等,所以 `(provider_id, model_id, effective_from)`
   * 做普通复合主键**挡不住**这一条 —— 表达式索引是唯一拦得住的写法。
   */
  it('通用价(provider_id 与 effective_from 都为 NULL)不能重复', () => {
    const d = raw()
    insertPricing(d, null, 'gpt-6-astra', null)
    expect(() => insertPricing(d, null, 'gpt-6-astra', null)).toThrow(/UNIQUE/)
    d.close()
  })

  /** 反向:索引不能宽到把该共存的行也拦掉 —— 这两种形状种子表里都真实存在 */
  it('同名模型的不同厂商价、以及同厂商的不同生效区间,都能共存', () => {
    const d = raw()
    insertPricing(d, null, 'glm-5.3-flash', null)
    insertPricing(d, 'zai', 'glm-5.3-flash', '2026-01-01')
    insertPricing(d, 'zai', 'glm-5.3-flash', '2026-09-10')
    expect(d.prepare('SELECT COUNT(*) c FROM model_pricing').get()?.['c']).toBe(3)
    d.close()
  })

  /**
   * ★★ `cost_micros` 的 NULL 和 0 是**两个不同的事实**:前者「查不到定价」,
   * 后者「真的不要钱」。PricingTable 顶部那张「用过但查不到定价」的表就是
   * 靠 `IS NULL` 筛出来的 —— 这一列要是 NOT NULL DEFAULT 0,那个入口直接失效,
   * 而「种子表 modelId 抄错了」从此没有任何外显方式。
   */
  it('cost_micros 可空,且 NULL 与 0 查得出区别', () => {
    const d = raw()
    const ins = (id: string, cost: number | null): void => {
      d.prepare(
        `INSERT INTO usage_records
           (id, at, run_id, provider_id, alias, upstream_model, latency_ms, ok, cost_micros)
         VALUES (?, 1, 'r', 'p', 'a', 'm', 10, 1, ?)`
      ).run(id, cost)
    }
    ins('无定价', null)
    ins('免费', 0)

    const missing = d.prepare('SELECT id FROM usage_records WHERE cost_micros IS NULL').all()
    expect(missing.map((r) => r['id'])).toEqual(['无定价'])
    d.close()
  })

  /**
   * ★ 和上一条**刻意相反**:token 数缺省补 0 是对的 —— `TokenUsage` 里没有缓存字段
   * 就是真的没有那类 token。这条断言把这个不对称固定下来,免得有人为了「一致」
   * 把两边改成同一种写法(往哪边改都会坏掉其中一个)。
   */
  it('token 计数列缺省是 0,不是 NULL', () => {
    const d = raw()
    d.prepare(
      `INSERT INTO usage_records (id, at, run_id, provider_id, alias, upstream_model, latency_ms, ok)
       VALUES ('x', 1, 'r', 'p', 'a', 'm', 10, 1)`
    ).run()
    const row = d.prepare('SELECT * FROM usage_records WHERE id = ?').get('x')
    expect(row?.['input_tokens']).toBe(0)
    expect(row?.['cache_write_1h_tokens']).toBe(0)
    // 同一行里,没给的费用仍然是 NULL
    expect(row?.['cost_micros']).toBeNull()
    d.close()
  })

  it('两张表跨重启都在,索引也在', () => {
    restart()
    const d = raw()
    const names = (type: string): string[] =>
      d
        .prepare(`SELECT name FROM sqlite_master WHERE type = ? AND name NOT LIKE 'sqlite_%'`)
        .all(type)
        .map((r) => String(r['name']))
    expect(names('table')).toEqual(expect.arrayContaining(['model_pricing', 'usage_records']))
    expect(names('index')).toEqual(
      expect.arrayContaining([
        'model_pricing_key',
        'model_pricing_by_model',
        'usage_records_by_at',
        'usage_records_by_model',
        'usage_records_by_provider'
      ])
    )
    d.close()
  })
})

describe('lastGoalStatusMessage · 目标恢复只读最后一条标记', () => {
  const assistant = (id: string, parts: AgentMessage['parts']): AgentMessage =>
    ({ id, role: 'assistant', parts, createdAt: 1, schemaVersion: 1 })

  it('取最后一条真带 goal_status 的助手消息;正文里恰好出现那串字符的不算', () => {
    const goal = (condition: string) => ({ type: 'goal_status' as const, met: false, condition })
    store.setHistory('goal-session', [
      assistant('a1', [{ type: 'text', text: '好' }, goal('旧目标')]),
      assistant('a2', [{ type: 'text', text: '好' }, goal('新目标')]),
      // 转义后的引号让它不会命中;下划线是 LIKE 通配,换成别的字符的那一条会命中候选但要被解析排除
      assistant('a3', [{ type: 'text', text: '"type":"goal_status"' }]),
      assistant('a4', [{ type: 'text', text: '好' }, { type: 'goal-status' } as never]),
      { id: 'u1', role: 'user', parts: [{ type: 'text', text: '继续' }], createdAt: 2, schemaVersion: 1 }
    ])
    expect(store.lastGoalStatusMessage('goal-session')?.id).toBe('a2')
    expect(store.lastGoalStatusMessage('no-such-session')).toBeUndefined()
  })
})
