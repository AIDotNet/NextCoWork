/**
 * 子代理转录**在库里、不在列表里**。
 *
 * 这条不变量有两个方向,少测哪个都会通过一个错误的实现:
 * - 只断言「侧边栏里没有」的话,把子会话整个删掉也是绿的 —— 而那样子代理
 *   面板就再也读不出它的转录了;
 * - 只断言「`getSession` 读得到」的话,当初那个 bug 本身也是绿的。
 *
 * 所以每一组用例都成对写。另外,过滤口不止侧边栏一个(搜索的 FTS 与 LIKE 兜底、
 * 数据导出、存储统计、按时长清理),漏掉任何一个,同一个泄漏就从那儿冒出来。
 */
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import type { AgentMessage } from '../../../shared/agent/message'
import type { DataExport } from '../../../shared/domain/data'
import { DEFAULT_SETTINGS } from '../../../shared/domain/settings'
import { DB_FILENAME, closeDatabase, openDatabase } from '../index'
import * as repo from '../repo'
import { MIGRATIONS } from '../schema'

let dir = ''

beforeEach(() => {
  closeDatabase()
  dir = mkdtempSync(join(tmpdir(), 'nextcowork-subagent-db-'))
  openDatabase(dir)
})

afterEach(() => {
  closeDatabase()
  rmSync(dir, { recursive: true, force: true })
})

/** 一父两子(其中一个是孙),形状和 `runtime.childRequestFor` 派生出来的一致。 */
const PARENT = 'session-parent'
const CHILD = `${PARENT}:sub:run-1:sub:1`
const GRANDCHILD = `${CHILD}:sub:run-1:sub:1:sub:2`

function seedTree(): void {
  repo.ensureSession({ id: PARENT, workspaceId: 'w', title: '父对话' })
  repo.ensureSession({ id: CHILD, workspaceId: 'w', parentSessionId: PARENT })
  repo.ensureSession({ id: GRANDCHILD, workspaceId: 'w', parentSessionId: CHILD })
}

function say(id: string, text: string): AgentMessage {
  return { id, role: 'assistant', createdAt: 1_700_000_000_000, schemaVersion: 1, parts: [{ type: 'text', text }] }
}

describe('子代理转录不进任何面向用户的枚举', () => {
  it('侧边栏只列顶层，而子会话的元数据与转录照读不误', () => {
    seedTree()
    repo.commitMessage(CHILD, say('m-child', '子代理的产出'))

    expect(repo.listSessions('w').map((s) => s.id)).toEqual([PARENT])
    // ★ 成对断言:它没有被删掉,只是不列出来 —— 子代理面板和续跑都靠这两个。
    expect(repo.getSession(CHILD)).toMatchObject({ id: CHILD, parentSessionId: PARENT })
    expect(repo.getHistory(CHILD).map((m) => m.id)).toEqual(['m-child'])
  })

  it('搜索不命中子会话的消息——FTS 与 LIKE 兜底都不命中', () => {
    seedTree()
    repo.commitMessage(PARENT, say('m-parent', 'searchneedle 父'))
    repo.commitMessage(CHILD, say('m-child', 'searchneedle 子'))

    expect(repo.searchAll('searchneedle').map((h) => h.sessionId)).toEqual([PARENT])
    expect(repo.searchAll('searchneedle', 'w').map((h) => h.sessionId)).toEqual([PARENT])

    /*
      ★ 兜底那条必须单独测。它平时跑不到(只在 FTS 对极端 Unicode 或旧库抛错时
      接手),所以漏加过滤也不会有任何用例变红 —— 泄漏会一直躺到某个用户的库上
      才现身。这里把虚表摘掉,逼 searchAll 走进 catch。
    */
    const raw = new DatabaseSync(join(dir, DB_FILENAME))
    raw.exec('DROP TABLE messages_fts')
    raw.close()
    closeDatabase()
    openDatabase(dir)

    expect(repo.searchAll('searchneedle').map((h) => h.sessionId)).toEqual([PARENT])
    expect(repo.searchAll('searchneedle', 'w').map((h) => h.sessionId)).toEqual([PARENT])
  })

  it('数据导出只带顶层，而备份 manifest 依据的全量枚举保持不变', () => {
    seedTree()

    expect(repo.exportDataSnapshot().sessions.map((s) => s.session.id)).toEqual([PARENT])
    /*
      ★ `listAllSessionDetails` 数的是「库里有几行」,它喂的是备份 manifest,
      而校验端拿裸 COUNT(*) 对账 —— 在它上面加过滤会让每一个新建的备份都恢复不了。
      这条断言就是钉住那个禁令的。
    */
    expect(new Set(repo.listAllSessionDetails().map((s) => s.session.id)))
      .toEqual(new Set([PARENT, CHILD, GRANDCHILD]))
  })

  it('存储统计里的会话数只数顶层', () => {
    seedTree()
    expect(repo.storageStats(dir, join(dir, 'attachments'), null).conversationCount).toBe(1)
  })

  it('导入旧存档时丢弃里面的子会话，不让一次导入把泄漏还原', () => {
    // 旧版导出确实带着子会话,而且那时候它们还没有 parentSessionId 字段。
    const legacy: DataExport = {
      type: 'nextcowork-data-export',
      version: 1,
      exportedAt: 1_700_000_000_000,
      schemaVersion: MIGRATIONS.at(-1)?.version ?? 1,
      encryptedCredentials: false,
      settings: DEFAULT_SETTINGS,
      workspaces: [],
      sessions: [
        {
          session: { id: PARENT, workspaceId: 'w', title: '父对话', model: '', mode: 'normal', thinking: 'auto', rootPathAtCreation: '', status: 'idle', archived: false, favorited: false, createdAt: 1, updatedAt: 2 },
          messages: [],
          contextCheckpoints: []
        },
        {
          session: { id: CHILD, workspaceId: 'w', title: '新对话', model: '', mode: 'normal', thinking: 'auto', rootPathAtCreation: '', status: 'idle', archived: false, favorited: false, createdAt: 1, updatedAt: 3 },
          messages: [],
          contextCheckpoints: []
        }
      ],
      providers: [],
      aliases: [],
      mcpServers: [],
      searchProviders: [],
      disabledSkillIds: [],
      userModelCatalog: []
    }

    const result = repo.mergeDataExport(legacy)

    expect(repo.listSessions('w').map((s) => s.id)).toEqual([PARENT])
    expect(repo.getSession(CHILD)).toBeUndefined()
    // 被丢掉的那条既不算导入也不算跳过 —— 它根本不是一个用户实体
    expect(result.sessionsImported).toBe(1)
    expect(result.skipped).toBe(0)
  })
})

describe('删父会话时整棵子树一起回收', () => {
  // 检查点那一档随 `context_checkpoints` 表一并删除(schema V26):压缩边界现在是
  // 转录里的一条消息,跟着 `getHistory` 一起被回收,不再需要单独断言。
  it('任意深度的子转录、它们的消息与 run 记录全部消失', () => {
    seedTree()
    for (const id of [PARENT, CHILD, GRANDCHILD]) {
      repo.commitMessage(id, say(`m-${id}`, `deleteneedle ${id}`))
      repo.setRunRecord(`run-${id}`, id, 'done', 1, 2)
    }

    expect(repo.deleteSession(PARENT).sort()).toEqual([CHILD, GRANDCHILD, PARENT].sort())

    for (const id of [PARENT, CHILD, GRANDCHILD]) {
      expect(repo.getSession(id)).toBeUndefined()
      expect(repo.getHistory(id)).toEqual([])
    }
    // FTS 是虚表,没有外键管得着它 —— 漏删的话搜索会一直返回幽灵结果
    expect(repo.searchAll('deleteneedle')).toEqual([])
  })

  it('按时长清理:会话数只数顶层，字节与消息数覆盖整棵子树', () => {
    seedTree()
    for (const id of [PARENT, CHILD, GRANDCHILD]) {
      for (let i = 0; i < 3; i++) repo.commitMessage(id, say(`m-${id}-${String(i)}`, `旧消息 ${String(i)}`))
    }

    const preview = repo.cleanupPreview('age', Date.now() + 1000)
    // 确认框里那句「将删除 N 条对话」说的是对话,不是转录
    expect(preview.sessionCount).toBe(1)
    // 而字节数要和真正消失的量对得上:9 条,不是 3 条
    expect(preview.messageCount).toBe(9)
    expect(preview.bytes).toBeGreaterThan(0)

    repo.deleteByAge(Date.now() + 1000)
    for (const id of [PARENT, CHILD, GRANDCHILD]) expect(repo.getHistory(id)).toEqual([])
  })

  it('自环的 parentSessionId 会被丢弃，递归查询不会原地打转', () => {
    const session = repo.createSession({ id: 'self', workspaceId: 'w' })
    repo.putSession({ ...session, parentSessionId: 'self' })

    expect(repo.getSession('self')?.parentSessionId).toBeUndefined()
    expect(repo.sessionSubtreeIds('self')).toEqual(['self'])
    expect(repo.listSessions('w').map((s) => s.id)).toEqual(['self'])
  })
})

describe('第 10 条迁移:把已经泄漏进侧边栏的存量脏行清掉', () => {
  it('升级一个第 9 版的真实库，子会话连同它的消息与 FTS 行一起消失', () => {
    const legacyDir = mkdtempSync(join(tmpdir(), 'nextcowork-v9-'))
    const legacyPath = join(legacyDir, DB_FILENAME)
    const raw = new DatabaseSync(legacyPath)
    raw.exec('CREATE TABLE migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at INTEGER NOT NULL)')
    const insert = raw.prepare('INSERT INTO migrations (version, name, applied_at) VALUES (?, ?, ?)')
    for (const migration of MIGRATIONS.slice(0, 9)) {
      raw.exec(migration.sql)
      insert.run(migration.version, migration.name, Date.now())
    }
    // 第 9 版没有 parent_session_id,子会话和顶层长得一模一样 —— 这正是那个 bug。
    const putSession = raw.prepare(
      `INSERT INTO sessions (id, workspace_id, title, model, mode, thinking, root_path_at_creation,
        status, archived, favorited, created_at, updated_at, json)
       VALUES (?, 'w', '新对话', '', 'normal', 'auto', '', 'idle', 0, 0, 1, 1, '{}')`
    )
    const putMessage = raw.prepare(
      `INSERT INTO messages (id, session_id, ordinal, role, parts, schema_version, created_at)
       VALUES (?, ?, 0, 'assistant', ?, 1, 1)`
    )
    const putFts = raw.prepare(
      `INSERT INTO messages_fts (message_id, session_id, title, content) VALUES (?, ?, '新对话', 'leakneedle')`
    )
    for (const id of [PARENT, CHILD, GRANDCHILD]) {
      putSession.run(id)
      putMessage.run(`m-${id}`, id, JSON.stringify([{ type: 'text', text: 'leakneedle' }]))
      putFts.run(`m-${id}`, id)
    }
    raw.close()

    closeDatabase()
    openDatabase(legacyDir)

    try {
      // 顶层完好,两条子会话(含深度 2 那条)连根消失
      expect(repo.listSessions('w').map((s) => s.id)).toEqual([PARENT])
      expect(repo.getSession(PARENT)).toBeDefined()
      expect(repo.getSession(CHILD)).toBeUndefined()
      expect(repo.getSession(GRANDCHILD)).toBeUndefined()
      // 消息由外键 CASCADE 带走;FTS 是虚表,靠迁移里那条显式 DELETE
      expect(repo.getHistory(CHILD)).toEqual([])
      expect(repo.searchAll('leakneedle').map((h) => h.sessionId)).toEqual([PARENT])
    } finally {
      closeDatabase()
      rmSync(legacyDir, { recursive: true, force: true })
      openDatabase(dir)
    }
  })
})

/**
 * `modelProviderId` **只活在 json 列里**,没有提列 —— 照 `titleSource` 的先例。
 * 提列的判据是「要不要被 WHERE / ORDER BY / 级联删除读到」,而它一样都不沾。
 */
describe('会话上的 modelProviderId', () => {
  it('往返读得回来', () => {
    repo.createSession({ id: 's1', workspaceId: 'w', model: 'shared', modelProviderId: 'codex' })
    expect(repo.getSession('s1')).toMatchObject({ model: 'shared', modelProviderId: 'codex' })
  })

  it('没给时读回 undefined,不是空串', () => {
    repo.createSession({ id: 's2', workspaceId: 'w', model: 'shared' })
    expect(repo.getSession('s2')?.modelProviderId).toBeUndefined()
  })

  /** 旧库里的每一行 json 都没有这个字段 —— 读到它们时不许抛 */
  it('json 里缺这个字段时照常读出会话', () => {
    repo.createSession({ id: 's3', workspaceId: 'w', model: 'shared', modelProviderId: 'codex' })
    const db = new DatabaseSync(join(dir, DB_FILENAME))
    const row = db.prepare('SELECT json FROM sessions WHERE id = ?').get('s3') as { json: string }
    const stripped = JSON.parse(row.json) as Record<string, unknown>
    delete stripped.modelProviderId
    db.prepare('UPDATE sessions SET json = ? WHERE id = ?').run(JSON.stringify(stripped), 's3')
    db.close()

    const after = repo.getSession('s3')
    expect(after?.model).toBe('shared')
    expect(after?.modelProviderId).toBeUndefined()
  })
})
